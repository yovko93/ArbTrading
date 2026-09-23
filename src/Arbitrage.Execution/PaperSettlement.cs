using Arbitrage.Application;

namespace Arbitrage.Execution;

public enum PaperResolutionSource { ManualScenario }
public enum PaperResolutionStatus { Committed }
public enum PaperPositionStatus { Open, Settled }
public enum SettlementRejection { None, GenerationNotFound, IntegrityFailure, MarketAlreadyResolved, SettlementTypeUnsupported,
    NoOpenPositions, OutcomeInvalid, PreviewExpired, PreviewChanged, IdempotencyConflict, ConfirmationRequired,
    ResolutionContradictsExecutionProof, ArithmeticOverflow, NotPaperMode }
public sealed record SettlementOutcome(string InstrumentId, string Outcome, decimal PayoutPerShare);
public sealed record SettlementMarket(string Exchange, string MarketId, string? Title, string? Structure, SettlementOutcome[] Outcomes);
public sealed record ExecutionSettlement(PaperExecutionState State, decimal RealizedPayoutToDate, decimal RealizedPnlToDate,
    decimal RemainingOpenCostBasis, decimal ExpectedRemainingPayout, decimal? FinalRealizedProfit, decimal? RealizedReturnOnCost,
    decimal? ExpectedVsRealizedDifference);

public static class PaperSettlement
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromSeconds(30);
    public static bool Supported(SettlementMarket m) => m.Exchange is "Kalshi" or "Polymarket" &&
        (string.Equals(m.Structure, "binary", StringComparison.OrdinalIgnoreCase) || m.Exchange == "Polymarket" && m.Structure == "Standard") &&
        m.Outcomes.Length == 2 && m.Outcomes.All(o => !string.IsNullOrWhiteSpace(o.InstrumentId)) &&
        m.Outcomes.Select(o => o.InstrumentId).Distinct(StringComparer.Ordinal).Count() == 2 &&
        m.Outcomes.Select(o => o.Outcome.ToLowerInvariant()).Order().SequenceEqual(new[] { "no", "yes" });
    public static bool ValidVector(SettlementOutcome[] vector) => vector.Length == 2 &&
        vector.Select(o => o.InstrumentId).Distinct().Count() == 2 && vector.Count(o => o.PayoutPerShare == 1) == 1 && vector.Count(o => o.PayoutPerShare == 0) == 1;
    public static (decimal Payout, decimal Pnl) Position(decimal quantity, decimal cost, decimal payoutPerShare)
    {
        if (quantity <= 0 || cost < 0 || payoutPerShare is not (0 or 1)) throw new ArgumentException("Unsupported paper settlement.");
        var payout = checked(quantity * payoutPerShare); return (payout, checked(payout - cost));
    }
    // Entry's captured complementary BUY proof is the authority; current policies/books/fees are not consulted.
    public static ExecutionSettlement Evaluate(PaperPlan plan, Func<string, string, string, decimal?> payout)
    {
        checked
        {
            if (plan.Proof.RelationshipTrust != RelationshipTrust.Deterministic ||
                plan.Proof.Strategy is not (OpportunityStrategy.CrossMarketBuyBothComplements or OpportunityStrategy.SingleMarketBinaryComplement))
                throw new InvalidOperationException("Unsupported historical execution proof.");
            decimal paid = 0, realizedCost = 0, remaining = 0; var settled = 0;
            foreach (var leg in plan.Fills.GroupBy(f => f.LegId))
            {
                var i = leg.First().Instrument; var value = payout(i.Exchange, i.NativeMarketId, i.NativeInstrumentId);
                var cost = leg.Sum(f => f.Notional + f.Fee);
                if (value is { } p) { paid += leg.Sum(f => f.Quantity) * p; realizedCost += cost; settled++; }
                else remaining += cost;
            }
            var count = plan.Fills.Select(f => f.LegId).Distinct().Count();
            var full = settled == count;
            if (paid > plan.ExpectedPayoutAtResolution || full && paid != plan.ExpectedPayoutAtResolution)
                throw new InvalidOperationException("ResolutionContradictsExecutionProof");
            var profit = paid - realizedCost;
            return new(full ? PaperExecutionState.Settled : settled > 0 ? PaperExecutionState.PartiallySettled : PaperExecutionState.Committed,
                paid, profit, remaining, plan.ExpectedPayoutAtResolution - paid, full ? paid - plan.Cost : null,
                full && plan.Cost > 0 ? (paid - plan.Cost) / plan.Cost : null, full ? paid - plan.ExpectedPayoutAtResolution : null);
        }
    }
}
