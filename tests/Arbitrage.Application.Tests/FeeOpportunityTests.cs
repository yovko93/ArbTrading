using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Strategies;

namespace Arbitrage.Application.Tests;

public sealed class FeeOpportunityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private static ArbitrageOpportunitySnapshot CaseA()
    {
        var a = new OrderBookInstrumentId("Kalshi", "a", "yes", "Yes"); var b = new OrderBookInstrumentId("Polymarket", "b", "123", "No");
        var relationship = new ApprovedRelationship(Guid.NewGuid(), RelationshipType.EquivalentOppositeOutcome, VerificationState.VerifiedDeterministic,
            [new("yes", "123", RelationshipType.EquivalentOppositeOutcome)], new("Kalshi", "a"), new("Polymarket", "b"), new(), new(), new());
        var plan = new OpportunityPlan(relationship, OpportunityStrategy.CrossMarketBuyBothComplements, a, b, DepthAction.Buy, DepthAction.Buy, true);
        CachedOrderBook Book(OrderBookInstrumentId id, params (decimal P, decimal Q)[] levels)
        {
            var book = OrderBookNormalizer.Normalize(id, [], levels.Select(l => new OrderBookLevel(l.P, l.Q, id.Exchange == "Kalshi" ? LiquidityOrigin.DerivedComplement : LiquidityOrigin.NativeAsk)), At);
            return new(book, null, BookEligibility.Evaluate(book, At, TimeSpan.FromSeconds(5)), Version: 1);
        }
        return GrossOpportunityEvaluator.Evaluate(plan, Book(a, (.4m, 10), (.43m, 20)), Book(b, (.5m, 5), (.52m, 20)), new(0), At);
    }
    private static ResolvedFeeSchedule[] Schedules(decimal multiplier = 1) => [
        FeeScheduleResolver.Resolve(new("Kalshi", "a", "e", "s", "USD", At, null, "deterministic fixture; rounding guide assumption",
            [new("k", "quadratic", multiplier, At, FeeSourceLevel.Series)], ["yes"]), At),
        FeeScheduleResolver.Resolve(new("Polymarket", "b", null, null, "USD", At, null, "deterministic fixture; common unit assumption",
            [new("p", "prediction-quadratic-v1", .04m, At, FeeSourceLevel.Market)], ["123"]), At)];
    [Fact] public void Case_A_exact_components_preserve_gross_and_mark_L2_estimated()
    {
        var gross = CaseA(); var fees = FeeOpportunityEvaluator.Evaluate(gross, Schedules(), KalshiFeeAccountProfile.NonDirectMember);
        Assert.Equal(25m, gross.PairedQuantity); Assert.Equal(23.35m, gross.GrossCost); Assert.Equal(1.65m, gross.GrossProfit);
        Assert.Equal(4, fees.Breakdown.Length); // paired engine has 3 segments; each leg has only 2 native levels
        Assert.Equal(.43m, fees.Breakdown.Where(q => q.Context.Exchange == "Kalshi").Sum(q => q.TotalFee));
        Assert.Equal(.24968m, fees.Breakdown.Where(q => q.Context.Exchange == "Polymarket").Sum(q => q.TotalFee));
        Assert.Equal(.67968m, fees.TotalExchangeFees); Assert.Equal(24.02968m, fees.FeeAdjustedCost);
        Assert.Equal(.97032m, fees.FeeAdjustedGuaranteedProfit); Assert.Equal(.0388128m, fees.FeeAdjustedEdgePerShare);
        Assert.Equal(.97032m / 24.02968m, fees.FeeAdjustedReturnOnCost); Assert.Equal(FeeStatus.Estimated, fees.Status);
        Assert.Equal(FeeOpportunityStatus.FeeAdjustedDetected, fees.State);
        Assert.All(fees.Breakdown, q => Assert.Equal(LiquidityRole.Taker, q.Context.Role));
        Assert.Null(gross.NetProfit); Assert.False(gross.ExecutionEligible);
        Assert.NotEqual(.04m * 25m * .516m * (1 - .516m), fees.Breakdown.Where(q => q.Context.Exchange == "Polymarket").Sum(q => q.ModelFee));
    }
    [Fact] public void Gross_positive_can_be_fee_negative_and_threshold_equality_is_inclusive_above_zero()
    {
        var gross = CaseA(); var negative = FeeOpportunityEvaluator.Evaluate(gross, Schedules(4), KalshiFeeAccountProfile.NonDirectMember);
        Assert.Equal(1.65m, gross.GrossProfit); Assert.Equal(1.95968m, negative.TotalExchangeFees);
        Assert.Equal(-.30968m, negative.FeeAdjustedGuaranteedProfit); Assert.Equal(FeeOpportunityStatus.FeeAdjustedNoEdge, negative.State);
        Assert.Equal(FeeOpportunityStatus.FeeAdjustedDetected, FeeOpportunityEvaluator.Evaluate(gross, Schedules(), KalshiFeeAccountProfile.NonDirectMember, .0388128m).State);
        Assert.Equal(FeeOpportunityStatus.FeeAdjustedNoEdge, FeeOpportunityEvaluator.Evaluate(gross, Schedules(), KalshiFeeAccountProfile.NonDirectMember, .0388129m).State);
    }
    [Fact] public void Unknown_profile_missing_stale_and_currency_conflict_never_become_zero()
    {
        var gross = CaseA(); var unknown = FeeOpportunityEvaluator.Evaluate(gross, Schedules(), KalshiFeeAccountProfile.Unknown);
        Assert.Null(unknown.TotalExchangeFees); Assert.Equal(FeeOpportunityStatus.AccountFeeProfileRequired, unknown.State);
        var schedules = Schedules(); schedules[0] = FeeScheduleResolver.Resolve(null, At);
        Assert.Equal(FeeOpportunityStatus.FeeScheduleUnavailable, FeeOpportunityEvaluator.Evaluate(gross, schedules, KalshiFeeAccountProfile.DirectMember).State);
        schedules = Schedules(); schedules[0] = FeeScheduleResolver.Resolve(schedules[0].Schedule, At.AddHours(2));
        Assert.Equal(FeeOpportunityStatus.FeeScheduleStale, FeeOpportunityEvaluator.Evaluate(gross, schedules, KalshiFeeAccountProfile.DirectMember).State);
        schedules = Schedules(); schedules[1] = FeeScheduleResolver.Resolve(schedules[1].Schedule! with { Currency = "USDC" }, At);
        Assert.Null(FeeOpportunityEvaluator.Evaluate(gross, schedules, KalshiFeeAccountProfile.DirectMember).TotalExchangeFees);
        var invalid = gross with { Fees = unknown }; invalid = invalid.Invalidate(OpportunityStatus.StaleInput, "changed");
        Assert.Equal(FeeOpportunityStatus.FeeResultStale, invalid.Fees!.State); Assert.Equal(gross.GrossProfit, invalid.GrossProfit);
        Assert.Equal(FeeOpportunityStatus.GrossNotDetected, FeeOpportunityEvaluator.Evaluate(invalid, Schedules(), KalshiFeeAccountProfile.DirectMember).State);
    }
}
