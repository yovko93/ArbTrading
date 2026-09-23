using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Execution;

namespace Arbitrage.Application.Tests;

public sealed class ValuationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");
    private static readonly OrderBookInstrumentId Id = new("Polymarket", "market", "123", "Yes");
    private static PaperInventory Position(decimal quantity = 25, decimal cost = 12.50m) => new(Guid.NewGuid(), Guid.NewGuid(), Id, "USDC", PaperPositionStatus.Open, quantity, cost, .25m, .49m, null, null);
    private static CachedOrderBook Book(params (decimal Price, decimal Quantity)[] bids)
    {
        var b = OrderBookNormalizer.Normalize(Id, bids.Select(l => new OrderBookLevel(l.Price, l.Quantity, LiquidityOrigin.NativeBid)), [], Now);
        return new(b, null, BookEligibility.Evaluate(b, Now, TimeSpan.FromSeconds(5)));
    }
    private static ResolvedFeeSchedule Fee(decimal rate = .1m) => FeeScheduleResolver.Resolve(new("Polymarket", "market", null, null, "USDC", Now, null, "fixture",
        [new("rule", "prediction-quadratic-v1", rate, Now.AddDays(-1), FeeSourceLevel.Market)], ["123"]), Now);
    private static PaperMark Mark(PaperInventory p, CachedOrderBook b, ResolvedFeeSchedule? fee = null) => PaperValuation.Mark(p, true, b, fee ?? Fee(), KalshiFeeAccountProfile.Unknown, Now);
    [Fact] public void Exact_multilevel_sell_includes_entry_fees_once_and_preserves_native_provenance()
    {
        var r = Mark(Position(), Book((.60m, 10), (.59m, 10), (.57m, 5)));
        Assert.Equal(PaperMarkStatus.Marked, r.MarkStatus); Assert.Equal(14.75m, r.GrossLiquidationValue);
        Assert.Equal(.59m, r.AverageLiquidationPrice); Assert.Equal(.57m, r.WorstLiquidationPrice);
        Assert.Equal(2.25m, r.GrossUnrealizedPnlBeforeExitFees); Assert.Equal(.18m, r.GrossReturnOnCost);
        Assert.Equal(.60445m, r.EstimatedExitFees); Assert.Equal(14.14555m, r.FeeAdjustedLiquidationValue);
        Assert.Equal(1.64555m, r.FeeAdjustedUnrealizedPnl); Assert.Equal(3, r.ExitFeeBreakdown.Length);
        Assert.All(r.NativeLiquidity, l => { Assert.Equal("123", l.InstrumentId); Assert.Equal(DepthAction.Sell, l.NativeSide); });
    }
    [Fact] public void Partial_depth_never_invents_value_for_unfilled_quantity()
    {
        var r = Mark(Position(), Book((.60m, 10), (.59m, 10)));
        Assert.Equal(PaperMarkStatus.PartialDepth, r.MarkStatus); Assert.Equal(20m, r.ExecutableQuantity); Assert.Equal(5m, r.UnfilledQuantity);
        Assert.Equal(11.90m, r.PartialGrossLiquidationValue); Assert.Equal(.595m, r.PartialAveragePrice);
        Assert.Null(r.GrossLiquidationValue); Assert.Null(r.GrossUnrealizedPnlBeforeExitFees); Assert.Null(r.FeeAdjustedLiquidationValue);
    }
    [Fact] public void Empty_bids_are_partial_not_zero_full_value()
    { var r = Mark(Position(), Book()); Assert.Equal(PaperMarkStatus.PartialDepth, r.MarkStatus); Assert.Equal(0m, r.ExecutableQuantity); Assert.Null(r.GrossLiquidationValue); }
    [Fact] public void Exact_losing_and_zero_cost_marks()
    {
        var r = Mark(Position(10, 8), Book((.60m, 10))); Assert.Equal(-2m, r.GrossUnrealizedPnlBeforeExitFees);
        Assert.Null(Mark(Position(10, 0), Book((.60m, 10))).GrossReturnOnCost);
    }
    [Fact] public void Exact_exit_fee_fixture()
    {
        var r = Mark(Position(20, 8), Book((.50m, 20)), Fee(.05m));
        Assert.Equal(10m, r.GrossLiquidationValue); Assert.Equal(.25m, r.EstimatedExitFees);
        Assert.Equal(9.75m, r.FeeAdjustedLiquidationValue); Assert.Equal(1.75m, r.FeeAdjustedUnrealizedPnl);
    }
    [Theory] [InlineData("missing")] [InlineData("stale")] [InlineData("conflict")] [InlineData("currency")]
    public void Unresolved_exit_fee_retains_gross(string reason)
    {
        var schedule = Fee().Schedule!;
        var resolved = reason switch {
            "missing" => FeeScheduleResolver.Resolve(null, Now), "stale" => FeeScheduleResolver.Resolve(schedule with { RetrievedAt = Now.AddHours(-2) }, Now),
            "conflict" => FeeScheduleResolver.Resolve(schedule with { VerificationIssue = "Conflicting official definitions" }, Now),
            _ => FeeScheduleResolver.Resolve(schedule with { Currency = "USD" }, Now) };
        var r = Mark(Position(), Book((.6m, 25)), resolved);
        Assert.Equal(PaperMarkStatus.Marked, r.MarkStatus); Assert.NotNull(r.GrossLiquidationValue); Assert.Null(r.EstimatedExitFees); Assert.Null(r.FeeAdjustedLiquidationValue);
    }
    [Theory] [InlineData("stale", PaperMarkStatus.BookStale)] [InlineData("invalid", PaperMarkStatus.BookInvalid)]
    [InlineData("missing", PaperMarkStatus.BookUnavailable)] [InlineData("gap", PaperMarkStatus.BookContinuityInsufficient)]
    public void Nonactionable_books_produce_no_current_mark(string reason, PaperMarkStatus expected)
    {
        var b = Book((.6m, 25)); b = reason switch {
            "stale" => b with { Eligibility = BookEligibility.Evaluate(b.Snapshot, Now.AddSeconds(6), TimeSpan.FromSeconds(5)) },
            "invalid" => b with { Failure = new("InvalidOrderBook", Now) },
            "missing" => b with { Snapshot = null },
            _ => b with { Source = BookSourceMode.Realtime, Realtime = new(1, RealtimeSubscriptionState.Resynchronizing, BookContinuity.GapDetected), Eligibility = new(BookFreshness.Fresh, false, "Gap", TimeSpan.Zero) } };
        var r = Mark(Position(), b); Assert.Equal(expected, r.MarkStatus); Assert.Null(r.GrossLiquidationValue);
    }
    [Fact] public void Settled_is_not_applicable_and_keeps_realized_pnl()
    { var r = Mark(Position() with { Status = PaperPositionStatus.Settled, RealizedPnl = 5 }, Book((.9m, 25))); Assert.Equal(PaperMarkStatus.NotApplicable, r.MarkStatus); Assert.Equal(5m, r.Position.RealizedPnl); Assert.Null(r.GrossLiquidationValue); }
    [Fact] public void Best_effort_is_not_upgraded()
    { var b = Book((.6m, 25)) with { Source = BookSourceMode.Realtime, Realtime = new(1, RealtimeSubscriptionState.Streaming, BookContinuity.BestEffort, true) }; Assert.Equal(PaperMarkQuality.RealtimeBestEffort, Mark(Position(), b).Quality); }
    [Theory] [InlineData("yes", "Yes")] [InlineData("no", "No")]
    public void Kalshi_sell_uses_own_bids_not_complement_asks(string instrument, string outcome)
    {
        var id = new OrderBookInstrumentId("Kalshi", "K", instrument, outcome);
        var b = OrderBookNormalizer.NormalizeBinary(id, [new(.3m, 25, LiquidityOrigin.NativeBid)], [new(.4m, 25, LiquidityOrigin.NativeBid)], Now);
        var c = new CachedOrderBook(b, null, BookEligibility.Evaluate(b, Now, TimeSpan.FromSeconds(10)), BookSourceMode.Realtime, new(1, RealtimeSubscriptionState.Streaming, BookContinuity.Continuous, true));
        var r = Mark(Position() with { Instrument = id, Currency = "USD" }, c);
        Assert.Equal(instrument == "yes" ? 7.5m : 10m, r.GrossLiquidationValue); Assert.Equal(PaperMarkQuality.RealtimeContinuous, r.Quality);
        Assert.All(r.NativeLiquidity, l => Assert.Equal(instrument, l.InstrumentId));
    }
    [Fact] public void Overflow_is_typed_unavailable()
    { var r = Mark(Position(10, decimal.MinValue), Book((.6m, 10))); Assert.Equal(PaperMarkStatus.ArithmeticOverflow, r.MarkStatus); Assert.Null(r.GrossLiquidationValue); }
    [Fact] public void Effective_fee_transition_is_resolved_locally()
    {
        var schedule = Fee().Schedule!; schedule = schedule with { Rules = schedule.Rules.Add(new("next", "prediction-quadratic-v1", .2m, Now.AddSeconds(1), FeeSourceLevel.Market)) };
        Assert.Equal(.25m, Mark(Position(10, 1), Book((.5m, 10)), FeeScheduleResolver.Resolve(schedule, Now)).EstimatedExitFees);
        Assert.Equal(.50m, Mark(Position(10, 1), Book((.5m, 10)), FeeScheduleResolver.Resolve(schedule, Now.AddSeconds(1))).EstimatedExitFees);
    }
}
