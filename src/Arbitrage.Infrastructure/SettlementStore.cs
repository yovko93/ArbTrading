using System.Security.Cryptography;
using System.Text.Json;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed record ResolutionSelection(Guid GenerationId, string Exchange, string MarketId, string WinningInstrumentId);
public sealed record ResolutionConfirmation(Guid RequestId, Guid PreviewId, ResolutionSelection Selection, bool ConfirmSimulation);
public sealed record PositionSettlement(Guid PositionId, string InstrumentId, string Outcome, string Currency, decimal Quantity, decimal CostBasis, decimal Payout, decimal RealizedPnl);
public sealed record SettlementCredit(string Exchange, string Currency, decimal Payout, decimal AvailableBefore, decimal AvailableAfter);
public sealed record ExecutionSettlementEffect(Guid ExecutionId, ExecutionSettlement Economics);
public sealed record SettlementProjection(SettlementRejection Rejection, string Fingerprint, SettlementMarket? Market,
    SettlementOutcome[] Outcomes, PositionSettlement[] Positions, ExecutionSettlementEffect[] Executions, SettlementCredit[] Credits);
public sealed record ResolutionCommitResult(SettlementRejection Rejection, PaperResolutionEntry? Resolution = null, bool Duplicate = false);
public sealed record ResolutionCandidate(SettlementMarket Market, PaperPositionEntry[] Positions, Guid[] ExecutionIds, Guid[] RelationshipIds);

public sealed partial class PaperStore
{
    public Task<Guid[]> ResolutionExecutionsAsync(PaperResolutionEntry r, CancellationToken ct) =>
        (from e in db.Set<PaperExecutionEntry>() join l in db.Set<PaperLegEntry>() on e.Id equals l.ExecutionId
         where e.GenerationId == r.GenerationId && l.Exchange == r.Exchange && l.MarketId == r.MarketId select e.Id).Distinct().ToArrayAsync(ct);
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private async Task<SettlementMarket[]> CaptureMarketsAsync(PaperPlan plan, CancellationToken ct)
    {
        var list = new List<SettlementMarket>();
        foreach (var key in plan.Fills.Select(f => (f.Instrument.Exchange, f.Instrument.NativeMarketId)).Distinct())
        {
            var row = await db.CatalogMarkets.AsNoTracking().SingleOrDefaultAsync(m => m.Exchange == key.Exchange && m.NativeId == key.NativeMarketId, ct);
            if (row is null) continue;
            var descriptor = CatalogSemantics.Describe(row);
            list.Add(new(key.Exchange, key.NativeMarketId, descriptor.Title, descriptor.MarketStructure,
                descriptor.Outcomes.Select(o => new SettlementOutcome(o.NativeId, o.Label, 0)).ToArray()));
        }
        return list.ToArray();
    }
    private async Task<SettlementMarket[]> MarketsAsync(PaperExecutionEntry e, CancellationToken ct)
    {
        var captured = JsonSerializer.Deserialize<SettlementMarket[]>(e.SettlementMarketsJson)!;
        // Phase 04A records lack this snapshot. Resolve identities from retained local catalog only;
        // no acquisition and no dependency on current relationship approval, fees or market status.
        return captured.Length > 0 ? captured : await CaptureMarketsAsync(JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!, ct);
    }
    public async Task<ResolutionCandidate[]> CandidatesAsync(Guid workspace, Guid generation, int page, CancellationToken ct)
    {
        if (await GenerationAsync(workspace, generation, ct) is null) return [];
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == generation && p.Status == PaperPositionStatus.Open).ToArrayAsync(ct);
        var executions = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.GenerationId == generation).ToArrayAsync(ct);
        var result = new List<ResolutionCandidate>();
        foreach (var group in positions.GroupBy(p => (p.Exchange, p.MarketId)).OrderBy(g => g.Key.Exchange).ThenBy(g => g.Key.MarketId).Skip((page - 1) * 50).Take(50))
        {
            var affected = executions.Where(e => JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Fills.Any(f => f.Instrument.Exchange == group.Key.Exchange && f.Instrument.NativeMarketId == group.Key.MarketId)).ToArray();
            var definitions = new List<SettlementMarket>();
            foreach (var e in affected) definitions.AddRange((await MarketsAsync(e, ct)).Where(m => m.Exchange == group.Key.Exchange && m.MarketId == group.Key.MarketId));
            var market = definitions.FirstOrDefault() ?? new(group.Key.Exchange, group.Key.MarketId, null, null, []);
            if (definitions.Any(m => Hash(m.Outcomes) != Hash(market.Outcomes) || m.Structure != market.Structure)) market = market with { Structure = null };
            result.Add(new(market, group.ToArray(), affected.Select(e => e.Id).ToArray(), affected.Select(e => JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Proof.RelationshipId).Distinct().ToArray()));
        }
        return result.ToArray();
    }
    public Task<PaperResolutionEntry?> ResolutionAsync(Guid workspace, Guid id, CancellationToken ct) =>
        db.Set<PaperResolutionEntry>().AsNoTracking().SingleOrDefaultAsync(r => r.WorkspaceId == workspace && r.Id == id, ct);
    public Task<PaperResolutionEntry?> ResolutionRequestAsync(Guid workspace, Guid request, CancellationToken ct) =>
        db.Set<PaperResolutionEntry>().AsNoTracking().SingleOrDefaultAsync(r => r.WorkspaceId == workspace && r.RequestId == request, ct);
    public Task<PaperResolutionEntry[]> ResolutionsAsync(Guid workspace, Guid? generation, int page, CancellationToken ct) =>
        db.Set<PaperResolutionEntry>().AsNoTracking().Where(r => r.WorkspaceId == workspace && (generation == null || r.GenerationId == generation))
            .OrderByDescending(r => r.RecordedAt).ThenBy(r => r.Id).Skip((page - 1) * 50).Take(50).ToArrayAsync(ct);
    public Task<PaperResolutionOutcomeEntry[]> OutcomesAsync(Guid resolution, CancellationToken ct) =>
        db.Set<PaperResolutionOutcomeEntry>().AsNoTracking().Where(o => o.ResolutionId == resolution).OrderBy(o => o.InstrumentId).ToArrayAsync(ct);
    public Task<PaperPositionEntry[]> SettledPositionsAsync(Guid resolution, CancellationToken ct) =>
        db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.SettlementResolutionId == resolution).OrderBy(p => p.Id).ToArrayAsync(ct);
    public Task<PaperTransactionEntry?> SettlementJournalAsync(Guid resolution, CancellationToken ct) =>
        db.Set<PaperTransactionEntry>().AsNoTracking().SingleOrDefaultAsync(t => t.ResolutionId == resolution, ct);

    public async Task<SettlementProjection> ResolutionPreviewAsync(Guid actor, Guid workspace, ResolutionSelection selection, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        return await ProjectAsync(workspace, selection, ct);
    }
    private async Task<SettlementProjection> ProjectAsync(Guid workspace, ResolutionSelection s, CancellationToken ct)
    {
        SettlementProjection Reject(SettlementRejection reason) => new(reason, "", null, [], [], [], []);
        try
        {
            var generation = await GenerationAsync(workspace, s.GenerationId, ct);
            if (generation is null) return Reject(SettlementRejection.GenerationNotFound);
            if (generation.Integrity != PaperIntegrity.Healthy) return Reject(SettlementRejection.IntegrityFailure);
            var resolutions = await db.Set<PaperResolutionEntry>().AsNoTracking().Where(r => r.GenerationId == s.GenerationId).ToArrayAsync(ct);
            if (resolutions.Any(r => r.Exchange == s.Exchange && r.MarketId == s.MarketId)) return Reject(SettlementRejection.MarketAlreadyResolved);
            var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == s.GenerationId && p.Exchange == s.Exchange && p.MarketId == s.MarketId).OrderBy(p => p.Id).ToArrayAsync(ct);
            if (positions.Length == 0) return Reject(SettlementRejection.NoOpenPositions);
            if (positions.Any(p => p.Status != PaperPositionStatus.Open)) return Reject(SettlementRejection.IntegrityFailure);
            var executions = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.GenerationId == s.GenerationId).OrderBy(e => e.Id).ToArrayAsync(ct);
            var affected = executions.Where(e => JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Fills.Any(f => f.Instrument.Exchange == s.Exchange && f.Instrument.NativeMarketId == s.MarketId)).ToArray();
            var definitions = new List<SettlementMarket>();
            foreach (var e in affected) definitions.AddRange((await MarketsAsync(e, ct)).Where(m => m.Exchange == s.Exchange && m.MarketId == s.MarketId));
            var market = definitions.FirstOrDefault();
            if (market is null || definitions.Count != affected.Length || !PaperSettlement.Supported(market) || definitions.Any(m => !PaperSettlement.Supported(m) || Hash(m.Outcomes) != Hash(market.Outcomes)))
                return Reject(SettlementRejection.SettlementTypeUnsupported);
            if (!market.Outcomes.Any(o => o.InstrumentId == s.WinningInstrumentId)) return Reject(SettlementRejection.OutcomeInvalid);
            var vector = market.Outcomes.Select(o => o with { PayoutPerShare = o.InstrumentId == s.WinningInstrumentId ? 1 : 0 }).OrderBy(o => o.InstrumentId).ToArray();
            if (positions.Any(p => !vector.Any(o => o.InstrumentId == p.InstrumentId && o.Outcome == p.Outcome))) return Reject(SettlementRejection.IntegrityFailure);
            // Reject sub-cent Kalshi quantities; do not invent rounding/settlement deductions.
            if (s.Exchange == "Kalshi" && positions.Any(p => decimal.Round(p.Quantity, 2) != p.Quantity)) return Reject(SettlementRejection.SettlementTypeUnsupported);
            var ids = resolutions.Select(r => r.Id).ToArray();
            var outcomes = await db.Set<PaperResolutionOutcomeEntry>().AsNoTracking().Where(o => ids.Contains(o.ResolutionId)).ToArrayAsync(ct);
            decimal? Payout(string exchange, string marketId, string instrument)
            {
                if (exchange == s.Exchange && marketId == s.MarketId) return vector.Single(o => o.InstrumentId == instrument).PayoutPerShare;
                var r = resolutions.SingleOrDefault(r => r.Exchange == exchange && r.MarketId == marketId);
                return r is null ? null : outcomes.Single(o => o.ResolutionId == r.Id && o.InstrumentId == instrument).PayoutPerShare;
            }
            ExecutionSettlementEffect[] effects;
            try { effects = affected.Select(e => new ExecutionSettlementEffect(e.Id, PaperSettlement.Evaluate(JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!, Payout))).ToArray(); }
            catch (InvalidOperationException) { return Reject(SettlementRejection.ResolutionContradictsExecutionProof); }
            var settlements = positions.Select(p => { var result = PaperSettlement.Position(p.Quantity, p.CostBasis, vector.Single(o => o.InstrumentId == p.InstrumentId).PayoutPerShare);
                return new PositionSettlement(p.Id, p.InstrumentId, p.Outcome, p.Currency, p.Quantity, p.CostBasis, result.Payout, result.Pnl); }).ToArray();
            var balances = await BalancesAsync(s.GenerationId, ct);
            var credits = settlements.GroupBy(p => p.Currency).Select(g => { var b = balances.Single(b => b.Exchange == s.Exchange && b.Currency == g.Key); var amount = g.Sum(p => p.Payout);
                return new SettlementCredit(s.Exchange, g.Key, amount, b.AvailableCash, checked(b.AvailableCash + amount)); }).ToArray();
            var proof = Hash(new { generation.Revision, Positions = positions, Executions = affected, Resolutions = resolutions.OrderBy(r => r.Id),
                Outcomes = outcomes.OrderBy(o => o.ResolutionId).ThenBy(o => o.InstrumentId), Balances = balances.OrderBy(b => b.Exchange).ThenBy(b => b.Currency), Vector = vector });
            return new(SettlementRejection.None, proof, market, vector, settlements, effects, credits);
        }
        catch (OverflowException) { return Reject(SettlementRejection.ArithmeticOverflow); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return Reject(SettlementRejection.IntegrityFailure); }
    }
    public async Task<ResolutionCommitResult> CommitResolutionAsync(Guid actor, Guid workspace, ResolutionConfirmation request,
        string proof, DateTimeOffset createdAt, string correlation, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var fingerprint = Hash(request);
        var previous = await ResolutionRequestAsync(workspace, request.RequestId, ct);
        if (previous is not null) return previous.RequestFingerprint == fingerprint ? new(SettlementRejection.None, previous, true) : new(SettlementRejection.IdempotencyConflict);
        if (!request.ConfirmSimulation || request.RequestId == Guid.Empty) return new(SettlementRejection.ConfirmationRequired);
        var now = clock.GetUtcNow();
        if (now < createdAt || now - createdAt > PaperSettlement.PreviewLifetime) return new(SettlementRejection.PreviewExpired);
        var p = await ProjectAsync(workspace, request.Selection, ct);
        if (p.Rejection != SettlementRejection.None) return new(p.Rejection);
        if (p.Fingerprint != proof) return new(SettlementRejection.PreviewChanged);
        var generation = await db.Set<PaperGenerationEntry>().SingleAsync(g => g.Id == request.Selection.GenerationId && g.WorkspaceId == workspace, ct);
        var resolution = new PaperResolutionEntry { Id = Guid.NewGuid(), WorkspaceId = workspace, GenerationId = generation.Id, ActorId = actor,
            RequestId = request.RequestId, RequestFingerprint = fingerprint, RequestJson = JsonSerializer.Serialize(request), ProofFingerprint = proof,
            Exchange = request.Selection.Exchange, MarketId = request.Selection.MarketId, Source = PaperResolutionSource.ManualScenario,
            Status = PaperResolutionStatus.Committed, ResolvedAt = now, RecordedAt = now };
        db.Add(resolution);
        foreach (var o in p.Outcomes) db.Add(new PaperResolutionOutcomeEntry { ResolutionId = resolution.Id, InstrumentId = o.InstrumentId, Outcome = o.Outcome, PayoutPerShare = o.PayoutPerShare });
        var journal = new PaperTransactionEntry { Id = Guid.NewGuid(), GenerationId = generation.Id, ActorId = actor, ResolutionId = resolution.Id, CreatedAt = now, Reason = "PaperSettlement" };
        db.Add(journal);
        foreach (var credit in p.Credits)
        {
            var b = await db.Set<PaperBalanceEntry>().SingleAsync(b => b.GenerationId == generation.Id && b.Exchange == credit.Exchange && b.Currency == credit.Currency, ct);
            b.AvailableCash = credit.AvailableAfter; b.Revision = checked(b.Revision + 1); b.UpdatedAt = now;
            if (credit.Payout != 0) Entry(journal.Id, b.Exchange, b.Currency, "PaperSettlement", credit.Payout, 0);
        }
        foreach (var value in p.Positions)
        {
            var position = await db.Set<PaperPositionEntry>().SingleAsync(x => x.Id == value.PositionId, ct);
            position.Status = PaperPositionStatus.Settled; position.SettledAt = now; position.SettlementResolutionId = resolution.Id;
            position.SettlementPayout = value.Payout; position.RealizedPnl = value.RealizedPnl; position.Revision = checked(position.Revision + 1); position.UpdatedAt = now;
        }
        foreach (var effect in p.Executions)
        {
            var e = await db.Set<PaperExecutionEntry>().SingleAsync(e => e.Id == effect.ExecutionId, ct);
            e.State = effect.Economics.State; e.SettlementJson = JsonSerializer.Serialize(effect.Economics);
            if (e.State == PaperExecutionState.Settled) e.SettledAt = now;
        }
        generation.Revision = checked(generation.Revision + 1);
        db.AuditRecords.Add(new(actor, workspace, now, correlation, "PaperResolutionCommitted", JsonSerializer.Serialize(new { GenerationId = generation.Id,
            ResolutionId = resolution.Id, resolution.Exchange, resolution.MarketId, Source = "ManualScenario", request.Selection.WinningInstrumentId,
            PositionCount = p.Positions.Length, ExecutionCount = p.Executions.Length })));
        await db.SaveChangesAsync(ct); ct.ThrowIfCancellationRequested(); await tx.CommitAsync(ct);
        return new(SettlementRejection.None, resolution);
    }
}
