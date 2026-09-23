using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Strategies;

namespace Arbitrage.Application.Tests;

public sealed class PaperTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    internal static (ArbitrageOpportunitySnapshot Snapshot, CachedOrderBook[] Books) Fixture(decimal quantity = 10, string currency = "USD")
    {
        var a = new OrderBookInstrumentId("Kalshi", "a", "yes", "Yes"); var b = new OrderBookInstrumentId("Polymarket", "b", "456", "No");
        CachedOrderBook Book(OrderBookInstrumentId id, params (decimal Price, decimal Qty)[] levels)
        { var s = OrderBookNormalizer.Normalize(id, [], levels.Select(l => new OrderBookLevel(l.Price, l.Qty, LiquidityOrigin.NativeAsk)), At);
            return new(s, null, BookEligibility.Evaluate(s, At, TimeSpan.FromSeconds(5)), Version: 1); }
        CachedOrderBook[] books = [Book(a, (.4m, 10), (.43m, 20)), Book(b, (.5m, 5), (.52m, 20))];
        var relationship = new ApprovedRelationship(Guid.NewGuid(), RelationshipType.EquivalentOppositeOutcome, VerificationState.VerifiedDeterministic,
            [new("yes", "456", RelationshipType.EquivalentOppositeOutcome)], new("Kalshi", "a"), new("Polymarket", "b"), new(), new(), new());
        var s = GrossOpportunityEvaluator.Evaluate(new(relationship, OpportunityStrategy.CrossMarketBuyBothComplements, a, b, DepthAction.Buy, DepthAction.Buy, true),
            books[0], books[1], new(RequestedQuantity: quantity), At);
        FeeSchedule Schedule(string exchange, string id, string instrument, string money) => new(exchange, id, null, null, money, At, null, "isolated verified fixture",
            [new("fixture", exchange == "Kalshi" ? "quadratic" : "prediction-quadratic-v1", exchange == "Kalshi" ? 1 : .04m, At.AddDays(-1), FeeSourceLevel.Market)], [instrument]);
        var fees = FeeOpportunityEvaluator.Evaluate(s, [FeeScheduleResolver.Resolve(Schedule("Kalshi", "a", "yes", "USD"), At),
            FeeScheduleResolver.Resolve(Schedule("Polymarket", "b", "456", currency), At)], KalshiFeeAccountProfile.DirectMember, .001m);
        return (s with { Fees = fees }, books);
    }
    [Fact] public void Exact_segment_fill_fees_cash_and_weighted_cost_basis()
    {
        var (s, books) = Fixture(); var result = PaperPlanner.Create(s, books, 10, At);
        Assert.Equal(PaperRejection.None, result.Rejection); var p = result.Plan!;
        Assert.Equal(9.10m, s.GrossCost); Assert.Equal(10m, p.ExpectedPayoutAtResolution); Assert.Equal(.9m, s.GrossProfit);
        Assert.Equal(3, p.Fills.Length);
        var a = p.Fills.Single(f => f.Instrument.Exchange == "Kalshi"); Assert.Equal(10, a.Quantity); Assert.Equal(.4m, a.Price); Assert.Equal(.168m, a.Fee);
        var b = p.Fills.Where(f => f.Instrument.Exchange == "Polymarket").ToArray();
        Assert.Equal(new decimal[] { 5, 5 }, b.Select(f => f.Quantity)); Assert.Equal(new decimal[] { .5m, .52m }, b.Select(f => f.Price));
        Assert.Equal(.26792m, p.Fills.Sum(f => f.Fee)); Assert.Equal(9.36792m, p.Cost); Assert.Equal(.63208m, p.ExpectedProfitAtResolution);
        Assert.Equal(95.832m, PaperAccounting.Debit(100, p.Debits.Single(d => d.Exchange == "Kalshi").Total));
        Assert.Equal(94.80008m, PaperAccounting.Debit(100, p.Debits.Single(d => d.Exchange == "Polymarket").Total));
        var position = PaperAccounting.Accumulate(0, 0, 0, b[0]); position = PaperAccounting.Accumulate(position.Quantity, position.CostBasis, position.Fees, b[1]);
        Assert.Equal((10m, 5.19992m, .09992m, .51m), position);
        Assert.Equal(3, p.Fills.Select(f => f.NativeLiquidityIdentity).Distinct().Count());
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(1001)]
    public void Invalid_quantities_rejected(decimal quantity)
    { var (s, b) = Fixture(); Assert.Equal(PaperRejection.RequestedQuantityInvalid, PaperPlanner.Create(s, b, quantity, At).Rejection); }
    [Fact] public void Cannot_fill_partial_depth_or_mixed_currency()
    {
        var (s, b) = Fixture(30); Assert.Equal(PaperRejection.InsufficientDepth, PaperPlanner.Create(s, b, 30, At).Rejection);
        (s, b) = Fixture(currency: "USDC"); Assert.Equal(PaperRejection.CurrencyModelUnsupported, PaperPlanner.Create(s, b, 10, At).Rejection);
    }
    [Theory] [InlineData("manual", PaperRejection.ManualRelationshipNotAllowed)] [InlineData("stale", PaperRejection.BookStale)]
    [InlineData("continuity", PaperRejection.BookContinuityInsufficient)] [InlineData("fees", PaperRejection.FeeModelUnresolved)]
    [InlineData("edge", PaperRejection.FeeAdjustedNoEdge)] [InlineData("sell", PaperRejection.RelationshipIneligible)]
    public void Fail_closed_states(string mutation, PaperRejection expected)
    {
        var (s, b) = Fixture();
        s = mutation switch { "manual" => s with { RelationshipTrust = RelationshipTrust.Manual }, "stale" => s with { Status = OpportunityStatus.BookStale },
            "continuity" => s with { Status = OpportunityStatus.BookContinuityInsufficient }, "fees" => s with { Fees = null },
            "edge" => s with { Fees = s.Fees! with { FeeAdjustedGuaranteedProfit = 0 } }, "sell" => s with { Legs = [s.Legs[0] with { Action = DepthAction.Sell }, s.Legs[1]] }, _ => s };
        Assert.Equal(expected, PaperPlanner.Create(s, b, 10, At).Rejection);
    }
    [Fact] public void Book_version_and_shared_source_are_checked_again()
    {
        var (s, b) = Fixture(); Assert.Equal(PaperRejection.MarketDataChanged, PaperPlanner.Create(s, [b[0], b[1] with { Version = 2 }], 10, At).Rejection);
        var duplicate = s with { Legs = [s.Legs[0], s.Legs[0]], Segments = [new(10, .4m, .4m, .8m, .2m, 8, 10, 2)] };
        Assert.Equal(PaperRejection.LiquidityConflict, PaperPlanner.Create(duplicate, [b[0], b[0]], 10, At).Rejection);
    }
    [Fact] public void Funds_never_go_negative_and_financial_overflow_is_explicit()
    {
        Assert.Throws<InvalidOperationException>(() => PaperAccounting.Debit(5, 6)); Assert.Throws<InvalidOperationException>(() => PaperAccounting.Debit(5, -1));
        var (s, b) = Fixture(); var fill = PaperPlanner.Create(s, b, 10, At).Plan!.Fills[0];
        Assert.Throws<OverflowException>(() => PaperAccounting.Accumulate(decimal.MaxValue, 0, 0, fill));
    }
}
