using System.Text.Json;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed partial class PaperStore
{
    private async Task<bool> SettlementHealthyAsync(PaperGenerationEntry generation, PaperExecutionEntry[] executions, PaperPositionEntry[] positions,
        PaperTransactionEntry[] journals, PaperLedgerEntry[] entries, CancellationToken ct)
    {
        var resolutions = await db.Set<PaperResolutionEntry>().AsNoTracking().Where(r => r.GenerationId == generation.Id).ToArrayAsync(ct);
        var ids = resolutions.Select(r => r.Id).ToArray();
        var outcomes = await db.Set<PaperResolutionOutcomeEntry>().AsNoTracking().Where(o => ids.Contains(o.ResolutionId)).ToArrayAsync(ct);
        var healthy = resolutions.Select(r => (r.Exchange, r.MarketId)).Distinct().Count() == resolutions.Length;
        foreach (var r in resolutions)
        {
            var vector = outcomes.Where(o => o.ResolutionId == r.Id).Select(o => new SettlementOutcome(o.InstrumentId, o.Outcome, o.PayoutPerShare)).ToArray();
            var request = JsonSerializer.Deserialize<ResolutionConfirmation>(r.RequestJson)!;
            var related = executions.Where(e => JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Fills.Any(f => f.Instrument.Exchange == r.Exchange && f.Instrument.NativeMarketId == r.MarketId)).ToArray();
            foreach (var e in related)
            {
                var market = (await MarketsAsync(e, ct)).SingleOrDefault(m => m.Exchange == r.Exchange && m.MarketId == r.MarketId);
                healthy &= market is not null && PaperSettlement.Supported(market) && vector.Length == market.Outcomes.Length &&
                    vector.All(o => market.Outcomes.Any(m => m.InstrumentId == o.InstrumentId && m.Outcome == o.Outcome));
            }
            healthy &= PaperSettlement.ValidVector(vector) && r.WorkspaceId == generation.WorkspaceId && r.Source == PaperResolutionSource.ManualScenario &&
                r.Status == PaperResolutionStatus.Committed && r.RequestId == request.RequestId && request.ConfirmSimulation &&
                request.Selection.GenerationId == generation.Id && request.Selection.Exchange == r.Exchange && request.Selection.MarketId == r.MarketId &&
                vector.Single(o => o.PayoutPerShare == 1).InstrumentId == request.Selection.WinningInstrumentId && Hash(request) == r.RequestFingerprint;
            var settled = positions.Where(p => p.SettlementResolutionId == r.Id).ToArray();
            healthy &= settled.Length > 0 && positions.Where(p => p.Exchange == r.Exchange && p.MarketId == r.MarketId).All(p => p.SettlementResolutionId == r.Id);
            var journal = journals.SingleOrDefault(j => j.ResolutionId == r.Id);
            healthy &= journal is not null && journal.Reason == "PaperSettlement" && journal.ExecutionId is null && journal.ActorId == r.ActorId && journal.CreatedAt == r.RecordedAt;
            var credits = entries.Where(e => e.TransactionId == journal?.Id).ToArray();
            healthy &= credits.All(e => e.Exchange == r.Exchange && e.Reason == "PaperSettlement" && e.ReservedDelta == 0 && e.AvailableDelta > 0 && settled.Any(p => p.Currency == e.Currency));
            foreach (var bucket in settled.GroupBy(p => p.Currency))
                healthy &= credits.Where(e => e.Currency == bucket.Key).Sum(e => e.AvailableDelta) == bucket.Sum(p => p.SettlementPayout ?? 0);
        }
        foreach (var p in positions)
        {
            if (p.Status == PaperPositionStatus.Open)
                healthy &= p.SettlementResolutionId is null && p.SettlementPayout is null && p.RealizedPnl is null && p.SettledAt is null && !resolutions.Any(r => r.Exchange == p.Exchange && r.MarketId == p.MarketId);
            else
            {
                var r = resolutions.SingleOrDefault(r => r.Id == p.SettlementResolutionId);
                var o = outcomes.SingleOrDefault(o => o.ResolutionId == r?.Id && o.InstrumentId == p.InstrumentId && o.Outcome == p.Outcome);
                healthy &= p.Status == PaperPositionStatus.Settled && r is not null && o is not null && r.Exchange == p.Exchange && r.MarketId == p.MarketId && p.SettledAt == r.RecordedAt &&
                    p.SettlementPayout == checked(p.Quantity * o.PayoutPerShare) && p.RealizedPnl == p.SettlementPayout - p.CostBasis;
            }
        }
        decimal? Payout(string exchange, string market, string instrument)
        {
            var r = resolutions.SingleOrDefault(r => r.Exchange == exchange && r.MarketId == market);
            return r is null ? null : outcomes.Single(o => o.ResolutionId == r.Id && o.InstrumentId == instrument).PayoutPerShare;
        }
        foreach (var e in executions)
        {
            var plan = JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!;
            var expected = PaperSettlement.Evaluate(plan, Payout);
            healthy &= e.State == expected.State;
            healthy &= e.State == PaperExecutionState.Committed ? e.SettlementJson is null && e.SettledAt is null :
                e.SettlementJson is not null && JsonSerializer.Deserialize<ExecutionSettlement>(e.SettlementJson) == expected;
            var settledTimes = plan.Fills.Select(f => resolutions.SingleOrDefault(r => r.Exchange == f.Instrument.Exchange && r.MarketId == f.Instrument.NativeMarketId)?.RecordedAt).ToArray();
            healthy &= e.State == PaperExecutionState.Settled ? e.SettledAt == settledTimes.Max() : e.SettledAt is null;
        }
        healthy &= journals.All(j => j.ResolutionId is null ? j.Reason != "PaperSettlement" : resolutions.Any(r => r.Id == j.ResolutionId));
        return healthy;
    }
}
