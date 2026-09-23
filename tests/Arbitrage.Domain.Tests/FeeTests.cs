using Arbitrage.Domain;

namespace Arbitrage.Domain.Tests;

public sealed class FeeTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private static FeeSchedule Schedule(string exchange = "Polymarket", decimal rate = .07m) => new(exchange, "m", "e", "s", "USD", At, null, "official fixture",
        [new("base", exchange == "Kalshi" ? "quadratic" : "prediction-quadratic-v1", rate, At, FeeSourceLevel.Series)], ["yes"]);
    private static FeeQuote Quote(FeeSchedule? schedule, decimal price = .5m, decimal quantity = 100, LiquidityRole role = LiquidityRole.Taker,
        KalshiFeeAccountProfile profile = KalshiFeeAccountProfile.Unknown)
    { decimal accumulator = 0; return FeeMath.Quote(FeeScheduleResolver.Resolve(schedule, At), new(schedule?.Exchange ?? "Polymarket", "m", "yes", role, quantity, price, DepthAction.Buy, profile), ref accumulator); }
    [Theory] [InlineData("0.5", "100", "1.75")] [InlineData("0.3", "100", "1.47")] [InlineData("0.7", "100", "1.47")]
    [InlineData("0.5", "0.5", "0.00875")] [InlineData("0.5", "0.0001", "0")]
    public void Polymarket_documented_formula_symmetry_fractional_and_subminimum(string price, string quantity, string expected)
    {
        var q = Quote(Schedule(), Parse(price), Parse(quantity)); Assert.Equal(Parse(expected), q.TotalFee); Assert.Equal(0m, q.Rebate); Assert.Equal("NotIncluded", q.ProgramRebates);
    }
    [Fact] public void Zero_market_and_maker_are_known_zero_but_unknown_is_null()
    {
        Assert.Equal(0m, Quote(Schedule(rate: 0)).TotalFee);
        Assert.Equal(0m, Quote(Schedule(), role: LiquidityRole.Maker).TotalFee);
        Assert.Null(Quote(null).TotalFee); Assert.Equal(FeeStatus.ScheduleUnavailable, Quote(null).Status);
        Assert.Null(Quote(Schedule(rate: -1)).TotalFee); Assert.Null(Quote(Schedule(), role: LiquidityRole.Unknown).TotalFee);
        Assert.Null(Quote(Schedule(rate: 1.1m)).TotalFee);
    }
    [Fact] public void Five_decimal_bound_does_not_claim_an_unverified_rounding_mode()
    {
        var q = Quote(Schedule(), .15m, 1); Assert.Equal(.008925m, q.ModelFee); Assert.Equal(.00893m, q.TotalFee); Assert.Equal(FeeStatus.ConservativeEstimate, q.Status);
        Assert.Equal(.00001m, Quote(Schedule(rate: .04m), .5m, .001m).TotalFee);
    }
    [Theory] [InlineData("quadratic", "1", "1.75")] [InlineData("quadratic", "0.5", "0.875")]
    [InlineData("quadratic_with_maker_fees", "1", "1.75")]
    public void Kalshi_taker_types_use_verified_absolute_multiplier(string type, string multiplier, string expected) =>
        Assert.Equal(Parse(expected), FeeMath.KalshiModel(type, Parse(multiplier), 100, .5m, LiquidityRole.Taker));
    [Fact] public void Kalshi_unknown_flat_and_combo_types_do_not_guess()
    {
        Assert.Null(FeeMath.KalshiModel("flat", 1, 1, .5m, LiquidityRole.Taker));
        Assert.Null(FeeMath.KalshiModel("future", 1, 1, .5m, LiquidityRole.Taker));
        Assert.Null(FeeMath.KalshiModel("quadratic_with_combo_maker_fees", 1, 1, .5m, LiquidityRole.Maker));
        Assert.Equal(.4375m, FeeMath.KalshiModel("quadratic_with_maker_fees", 1, 100, .5m, LiquidityRole.Maker));
        Assert.Equal(0m, FeeMath.KalshiModel("quadratic", 1, 100, .5m, LiquidityRole.Maker));
    }
    [Fact] public void Official_non_direct_rounding_example_and_direct_precision()
    {
        var q = FeeMath.KalshiRound(.00363825m, -.055m, KalshiFeeAccountProfile.NonDirectMember);
        Assert.Equal(.003639m, q.RoundedTradeFee); Assert.Equal(.001361m, q.RoundingFee); Assert.Equal(.005m, q.TotalFee);
        var direct = FeeMath.KalshiRound(.00363825m, -.055m, KalshiFeeAccountProfile.DirectMember);
        Assert.Equal(.000061m, direct.RoundingFee); Assert.Equal(.0037m, direct.TotalFee);
        var unknown = FeeMath.KalshiRound(.00363825m, -.055m, KalshiFeeAccountProfile.Unknown);
        Assert.Equal(.003639m, unknown.RoundedTradeFee); Assert.Null(unknown.TotalFee); Assert.Null(unknown.RoundingFee);
    }
    [Fact] public void Per_order_accumulator_rebates_only_complete_units_and_caps_each_fill()
    {
        decimal accumulator = 0; FeeComponent? result = null;
        for (var i = 0; i < 3; i++) { result = FeeMath.KalshiRound(.016m, -.1m, KalshiFeeAccountProfile.NonDirectMember, accumulator); accumulator = result.Accumulator; }
        Assert.Equal(.01m, result!.Rebate); Assert.Equal(.002m, accumulator); Assert.Equal(.01m, result.TotalFee);
        var capped = FeeMath.KalshiRound(0, -.099m, KalshiFeeAccountProfile.NonDirectMember, .02m);
        Assert.Equal(0m, capped.Rebate); Assert.Equal(.001m, capped.TotalFee); Assert.Equal(.021m, capped.Accumulator);
        Assert.Equal(-.06m, FeeMath.FloorToAccountPrecision(-.058639m, .01m));
    }
    [Fact] public void Hierarchy_scheduled_change_override_clear_boundary_and_conflict()
    {
        var s = Schedule("Kalshi", 1) with { Rules = [new("base", "quadratic", 1, At, FeeSourceLevel.Series),
            new("future", "quadratic", .5m, At.AddMinutes(10), FeeSourceLevel.Series),
            new("override", "quadratic", 2, At, FeeSourceLevel.EventOverride),
            new("clear", null, null, At.AddMinutes(20), FeeSourceLevel.EventOverride, true)] };
        Assert.Equal(2m, FeeScheduleResolver.Resolve(s, At.AddMinutes(10)).Rule!.Rate);
        var before = FeeScheduleResolver.Resolve(s, At.AddMinutes(19)); var after = FeeScheduleResolver.Resolve(s, At.AddMinutes(20));
        Assert.Equal(.5m, after.Rule!.Rate); Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(FeeStatus.ScheduleStale, FeeScheduleResolver.Resolve(s, At.AddHours(2)).Status);
        s = s with { Rules = s.Rules.Add(new("conflict", "quadratic", 3, At, FeeSourceLevel.Series)) };
        Assert.Equal(FeeStatus.InvalidFeeMetadata, FeeScheduleResolver.Resolve(s, At).Status);
    }
    [Fact] public void Official_document_conflict_preserves_model_but_prevents_total()
    {
        var q = Quote(Schedule("Kalshi", 1) with { VerificationIssue = "Regulatory/API conflict" }, profile: KalshiFeeAccountProfile.DirectMember);
        Assert.Equal(1.75m, q.ModelFee); Assert.Null(q.TotalFee); Assert.Equal(FeeStatus.InvalidFeeMetadata, q.Status);
        Assert.Equal(FeeStatus.KnownModelAccountRoundingUnknown, Quote(Schedule("Kalshi", 1)).Status);
    }
    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
