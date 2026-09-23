using System.Text.Json;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed record PaperFunding(string Exchange, string Currency, decimal Amount);
public sealed record PaperCommitResult(PaperRejection Rejection, PaperExecutionEntry? Execution = null, bool Duplicate = false);

// SQLite serializable transactions acquire the writer reservation before reading balances. The unique
// request index is the durable idempotency authority, including across scopes, restart and lost replies.
public sealed partial class PaperStore(TradingDbContext db, RelationshipStore membership, TimeProvider clock)
{
    public Task<PaperGenerationEntry?> GenerationAsync(Guid workspace, Guid generation, CancellationToken ct) =>
        db.Set<PaperGenerationEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == generation, ct);
    public Task<PaperGenerationEntry?> ActiveAsync(Guid workspace, CancellationToken ct) =>
        db.Set<PaperGenerationEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.ClosedAt == null, ct);
    public Task<PaperGenerationEntry[]> GenerationsAsync(Guid workspace, CancellationToken ct) =>
        db.Set<PaperGenerationEntry>().AsNoTracking().Where(x => x.WorkspaceId == workspace).OrderByDescending(x => x.CreatedAt).Take(100).ToArrayAsync(ct);
    public Task<PaperBalanceEntry[]> BalancesAsync(Guid generation, CancellationToken ct) =>
        db.Set<PaperBalanceEntry>().AsNoTracking().Where(x => x.GenerationId == generation).ToArrayAsync(ct);
    public Task<PaperPositionEntry[]> PositionsAsync(Guid generation, CancellationToken ct, PaperPositionStatus? status = PaperPositionStatus.Open, int page = 1) =>
        db.Set<PaperPositionEntry>().AsNoTracking().Where(x => x.GenerationId == generation && (status == null || x.Status == status))
            .OrderBy(x => x.Id).Skip((page - 1) * 100).Take(100).ToArrayAsync(ct);
    public Task<PaperExecutionEntry[]> HistoryAsync(Guid workspace, Guid? generation, int page, CancellationToken ct) =>
        db.Set<PaperExecutionEntry>().AsNoTracking().Where(x => x.WorkspaceId == workspace && (generation == null || x.GenerationId == generation))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50).ToArrayAsync(ct);
    public Task<PaperExecutionEntry?> DetailAsync(Guid workspace, Guid id, CancellationToken ct) =>
        db.Set<PaperExecutionEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, ct);
    public Task<PaperExecutionEntry?> RequestAsync(Guid workspace, Guid request, CancellationToken ct) =>
        db.Set<PaperExecutionEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.RequestId == request, ct);
    public async Task<PaperGenerationEntry> InitializeAsync(Guid actor, Guid workspace, Guid? expectedGeneration,
        IReadOnlyList<PaperFunding> funding, string reason, string correlation, CancellationToken ct)
    {
        if (funding.Count is < 1 or > 4 || funding.Select(x => (x.Exchange, x.Currency)).Distinct().Count() != funding.Count ||
            funding.Any(x => x.Exchange is not ("Kalshi" or "Polymarket") || x.Currency is not ("USD" or "USDC") || x.Amount is < 0 or > 1_000_000_000m) ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("Invalid paper funding.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var old = await db.Set<PaperGenerationEntry>().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.ClosedAt == null, ct);
        if (old?.Id != expectedGeneration) throw new PaperGenerationConflict();
        var now = clock.GetUtcNow();
        if (old is not null) { old.ClosedAt = now; await db.SaveChangesAsync(ct); }
        var generation = new PaperGenerationEntry { Id = Guid.NewGuid(), WorkspaceId = workspace, ActorId = actor, CreatedAt = now,
            Reason = reason, Integrity = PaperIntegrity.Healthy };
        db.Add(generation);
        var journal = new PaperTransactionEntry { Id = Guid.NewGuid(), GenerationId = generation.Id, ActorId = actor, CreatedAt = now, Reason = "ExplicitInitialFunding" };
        db.Add(journal);
        foreach (var f in funding)
        {
            db.Add(new PaperBalanceEntry { GenerationId = generation.Id, Exchange = f.Exchange, Currency = f.Currency,
                InitialCash = f.Amount, AvailableCash = f.Amount, Revision = 1, UpdatedAt = now });
            Entry(journal.Id, f.Exchange, f.Currency, "InitialFunding", f.Amount, 0);
        }
        db.AuditRecords.Add(new(actor, workspace, now, correlation, old is null ? "Paper.AccountInitialized" : "Paper.GenerationReset",
            JsonSerializer.Serialize(new { generation.Id, PreviousGenerationId = old?.Id, Reason = reason, Funding = funding })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return generation;
    }
    public static PaperRejection Funds(PaperPlan plan, IReadOnlyList<PaperBalanceEntry> balances) =>
        plan.Debits.Any(d => balances.SingleOrDefault(b => b.Exchange == d.Exchange && b.Currency == d.Currency) is not { } b || b.AvailableCash < d.Total || b.ReservedCash < 0)
            ? PaperRejection.InsufficientPaperFunds : PaperRejection.None;

    public async Task<PaperCommitResult> CommitAsync(Guid actor, Guid workspace, Guid generationId, Guid requestId, string fingerprint,
        PaperPlan plan, string correlation, Func<CancellationToken, Task<PaperRejection>> revalidate,
        Func<Action, bool> commitCurrentBooks, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var previous = await RequestAsync(workspace, requestId, ct);
        if (previous is not null) return previous.RequestFingerprint == fingerprint
            ? new(PaperRejection.None, previous, true) : new(PaperRejection.DuplicateRequest);
        var generation = await db.Set<PaperGenerationEntry>().SingleOrDefaultAsync(x => x.Id == generationId && x.WorkspaceId == workspace && x.ClosedAt == null, ct);
        if (generation is null) return new(PaperRejection.GenerationChanged);
        if (generation.Integrity != PaperIntegrity.Healthy) return new(PaperRejection.IntegrityFailure);
        foreach (var market in plan.Fills.Select(f => (f.Instrument.Exchange, f.Instrument.NativeMarketId)).Distinct())
            if (await db.Set<PaperResolutionEntry>().AnyAsync(r => r.GenerationId == generationId && r.Exchange == market.Exchange && r.MarketId == market.NativeMarketId, ct))
                return new(PaperRejection.MarketAlreadyResolved);
        var error = await revalidate(ct);
        if (error != PaperRejection.None) return new(error);
        var balances = await db.Set<PaperBalanceEntry>().Where(b => b.GenerationId == generationId).ToArrayAsync(ct);
        error = Funds(plan, balances);
        if (error != PaperRejection.None) return new(error);
        var now = clock.GetUtcNow();
        var execution = new PaperExecutionEntry { Id = plan.Id, WorkspaceId = workspace, GenerationId = generationId, ActorId = actor,
            RequestId = requestId, RequestFingerprint = fingerprint, OpportunityKey = plan.Proof.OpportunityKey, CreatedAt = now,
            State = PaperExecutionState.Committed, PlanJson = JsonSerializer.Serialize(plan) };
        db.Add(execution);
        execution.SettlementMarketsJson = JsonSerializer.Serialize(await CaptureMarketsAsync(plan, ct));
        generation.Revision = checked(generation.Revision + 1);
        var journal = new PaperTransactionEntry { Id = Guid.NewGuid(), GenerationId = generationId, ExecutionId = execution.Id,
            ActorId = actor, CreatedAt = now, Reason = "SnapshotPaperFill" };
        db.Add(journal);
        try
        {
            foreach (var debit in plan.Debits)
            {
                var b = balances.Single(b => b.Exchange == debit.Exchange && b.Currency == debit.Currency);
                b.AvailableCash = PaperAccounting.Debit(b.AvailableCash, debit.Total);
                // Reservation and consumption are visible in the journal, never as a half-committed basket.
                Entry(journal.Id, b.Exchange, b.Currency, "Reserve", -debit.Total, debit.Total);
                Entry(journal.Id, b.Exchange, b.Currency, "FillNotional", 0, -debit.Notional);
                Entry(journal.Id, b.Exchange, b.Currency, "ModeledFees", 0, -debit.Fees);
                b.Revision = checked(b.Revision + 1); b.UpdatedAt = now;
            }
            foreach (var leg in plan.Fills.GroupBy(f => f.LegId))
            {
                var first = leg.First(); var instrument = first.Instrument;
                db.Add(new PaperLegEntry { Id = leg.Key, ExecutionId = execution.Id, Exchange = instrument.Exchange, MarketId = instrument.NativeMarketId,
                    InstrumentId = instrument.NativeInstrumentId, Outcome = instrument.Outcome, Quantity = leg.Sum(f => f.Quantity),
                    Notional = leg.Sum(f => f.Notional), Fees = leg.Sum(f => f.Fee), Currency = first.Currency });
                var position = await db.Set<PaperPositionEntry>().SingleOrDefaultAsync(p => p.GenerationId == generationId && p.Exchange == instrument.Exchange &&
                    p.MarketId == instrument.NativeMarketId && p.InstrumentId == instrument.NativeInstrumentId && p.Outcome == instrument.Outcome, ct);
                if (position is null)
                {
                    position = new() { Id = Guid.NewGuid(), GenerationId = generationId, Exchange = instrument.Exchange, MarketId = instrument.NativeMarketId,
                        InstrumentId = instrument.NativeInstrumentId, Outcome = instrument.Outcome, Currency = first.Currency, OpenedAt = now };
                    db.Add(position);
                }
                if (position.Currency != first.Currency) return new(PaperRejection.CurrencyModelUnsupported);
                foreach (var fill in leg)
                {
                    var value = PaperAccounting.Accumulate(position.Quantity, position.CostBasis, position.Fees, fill);
                    (position.Quantity, position.CostBasis, position.Fees, position.AverageEntry) = value;
                    db.Add(new PaperFillEntry { Id = fill.Id, LegId = fill.LegId, FillJson = JsonSerializer.Serialize(fill) });
                }
                position.UpdatedAt = now; position.Revision = checked(position.Revision + 1);
            }
        }
        catch (OverflowException) { return new(PaperRejection.ArithmeticOverflow); }
        db.AuditRecords.Add(new(actor, workspace, now, correlation, "Paper.ExecutionCommitted", JsonSerializer.Serialize(new { execution.Id, generationId, requestId })));
        await db.SaveChangesAsync(ct);
        // Metadata cannot change behind the held SQLite writer reservation. Books are protected through COMMIT.
        error = await revalidate(ct);
        if (error != PaperRejection.None) return new(error);
        ct.ThrowIfCancellationRequested();
        if (!commitCurrentBooks(() => { ct.ThrowIfCancellationRequested(); tx.Commit(); })) return new(PaperRejection.MarketDataChanged);
        return new(PaperRejection.None, execution);
    }
    private void Entry(Guid transaction, string exchange, string currency, string reason, decimal available, decimal reserved) =>
        db.Add(new PaperLedgerEntry { Id = Guid.NewGuid(), TransactionId = transaction, Exchange = exchange, Currency = currency,
            Reason = reason, AvailableDelta = available, ReservedDelta = reserved });

    public async Task<PaperIntegrity> ReconcileAsync(Guid actor, Guid workspace, Guid generationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var generation = await db.Set<PaperGenerationEntry>().SingleAsync(g => g.Id == generationId && g.WorkspaceId == workspace, ct);
        var healthy = true;
        try
        {
            var balances = await BalancesAsync(generationId, ct);
            var journals = await db.Set<PaperTransactionEntry>().Where(t => t.GenerationId == generationId).ToArrayAsync(ct);
            var ids = journals.Select(t => t.Id).ToArray();
            var entries = await db.Set<PaperLedgerEntry>().Where(e => ids.Contains(e.TransactionId)).ToArrayAsync(ct);
            healthy &= entries.All(e => balances.Any(b => b.Exchange == e.Exchange && b.Currency == e.Currency));
            foreach (var b in balances)
            {
                var e = entries.Where(e => e.Exchange == b.Exchange && e.Currency == b.Currency).ToArray();
                healthy &= b.AvailableCash >= 0 && b.ReservedCash == 0 && e.Sum(x => x.AvailableDelta) == b.AvailableCash &&
                    e.Sum(x => x.ReservedDelta) == b.ReservedCash && e.Where(x => x.Reason == "InitialFunding").Sum(x => x.AvailableDelta) == b.InitialCash;
            }
            var executions = await db.Set<PaperExecutionEntry>().Where(e => e.GenerationId == generationId).ToArrayAsync(ct);
            var executionIds = executions.Select(e => e.Id).ToArray();
            var legs = await db.Set<PaperLegEntry>().Where(l => executionIds.Contains(l.ExecutionId)).ToArrayAsync(ct);
            var legIds = legs.Select(l => l.Id).ToArray();
            var storedFills = await db.Set<PaperFillEntry>().Where(f => legIds.Contains(f.LegId)).ToArrayAsync(ct);
            var fills = storedFills.Select(f => JsonSerializer.Deserialize<PaperFill>(f.FillJson)!).ToArray();
            healthy &= balances.Length > 0 && storedFills.Zip(fills).All(pair => pair.First.Id == pair.Second.Id && pair.First.LegId == pair.Second.LegId);
            healthy &= journals.Count(j => j.ExecutionId == null && j.Reason == "ExplicitInitialFunding") == 1;
            healthy &= journals.All(j => j.ExecutionId is null || executions.Any(e => e.Id == j.ExecutionId));
            foreach (var leg in legs)
            {
                var values = fills.Where(f => f.LegId == leg.Id).ToArray();
                healthy &= values.Length > 0 && values.All(f => f.Instrument.Exchange == leg.Exchange && f.Instrument.NativeMarketId == leg.MarketId &&
                    f.Instrument.NativeInstrumentId == leg.InstrumentId && f.Instrument.Outcome == leg.Outcome && f.Currency == leg.Currency) &&
                    values.Sum(f => f.Quantity) == leg.Quantity && values.Sum(f => f.Notional) == leg.Notional && values.Sum(f => f.Fee) == leg.Fees;
            }
            foreach (var execution in executions)
            {
                var plan = JsonSerializer.Deserialize<PaperPlan>(execution.PlanJson)!;
                var actual = fills.Where(f => legs.Any(l => l.ExecutionId == execution.Id && l.Id == f.LegId)).ToArray();
                healthy &= plan.Id == execution.Id && actual.Length == plan.Fills.Length &&
                    actual.All(f => plan.Fills.Any(p => JsonSerializer.Serialize(p) == JsonSerializer.Serialize(f))) && legs.Count(l => l.ExecutionId == execution.Id) == 2;
                var journal = journals.SingleOrDefault(j => j.ExecutionId == execution.Id);
                healthy &= journal is not null;
                foreach (var debit in plan.Debits)
                    healthy &= journal is not null && entries.Where(e => e.TransactionId == journal.Id && e.Exchange == debit.Exchange && e.Currency == debit.Currency)
                        .Sum(e => e.AvailableDelta + e.ReservedDelta) == -debit.Total;
            }
            var positions = await db.Set<PaperPositionEntry>().Where(p => p.GenerationId == generationId).ToArrayAsync(ct);
            foreach (var p in positions)
            {
                var f = fills.Where(f => f.Instrument.Exchange == p.Exchange && f.Instrument.NativeMarketId == p.MarketId && f.Instrument.NativeInstrumentId == p.InstrumentId && f.Instrument.Outcome == p.Outcome).ToArray();
                healthy &= p.Quantity > 0 && f.All(f => f.Currency == p.Currency) && f.Sum(f => f.Quantity) == p.Quantity &&
                    f.Sum(f => f.Notional + f.Fee) == p.CostBasis && f.Sum(f => f.Fee) == p.Fees && (p.CostBasis - p.Fees) / p.Quantity == p.AverageEntry;
            }
            healthy &= fills.All(f => positions.Any(p => p.Exchange == f.Instrument.Exchange && p.MarketId == f.Instrument.NativeMarketId && p.InstrumentId == f.Instrument.NativeInstrumentId && p.Outcome == f.Instrument.Outcome));
            healthy &= await SettlementHealthyAsync(generation, executions, positions, journals, entries, ct);
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or InvalidOperationException or NullReferenceException) { healthy = false; }
        // A flagged generation requires investigation/reset; a later good diagnostic never silently repairs it.
        if (!healthy) generation.Integrity = PaperIntegrity.Corrupt;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return generation.Integrity;
    }
}
public sealed class PaperGenerationConflict : Exception;
