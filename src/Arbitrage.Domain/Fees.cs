using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arbitrage.Domain;

public enum LiquidityRole { Unknown, Taker, Maker }
public enum FeeStatus { NotEvaluated, KnownExact, KnownModelAccountRoundingUnknown, ConservativeEstimate, Estimated, ScheduleUnavailable, ScheduleStale, UnsupportedFeeType, AccountProfileRequired, InvalidFeeMetadata }
public enum KalshiFeeAccountProfile { Unknown, DirectMember, NonDirectMember }
public enum FeeSourceLevel { Market, Series, EventOverride }
public enum FeeOpportunityStatus { GrossDetectedFeeUnknown, FeeAdjustedDetected, FeeAdjustedNoEdge, FeeScheduleUnavailable, FeeScheduleStale, AccountFeeProfileRequired, UnsupportedFeeModel, FeeResultStale, GrossNotDetected }
public sealed record FeeRule(string Id, string? Type, decimal? Rate, DateTimeOffset EffectiveFrom, FeeSourceLevel Level, bool Clear = false);
public sealed record FeeSchedule(string Exchange, string MarketId, string? EventId, string? SeriesId, string Currency,
    DateTimeOffset RetrievedAt, DateTimeOffset? SourceUpdatedAt, string Source, ImmutableArray<FeeRule> Rules,
    ImmutableArray<string> Instruments, string? VerificationIssue = null)
{
    public const string FormulaVersion = "2026-09-23-v1";
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { FormulaVersion, Exchange, MarketId, EventId, SeriesId, Currency, Source, Rules, Instruments, VerificationIssue }))));
}
public sealed record ResolvedFeeSchedule(FeeSchedule? Schedule, FeeRule? Rule, FeeStatus Status, string Fingerprint, string? Warning);
public static class FeeScheduleResolver
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);
    public static ResolvedFeeSchedule Resolve(FeeSchedule? schedule, DateTimeOffset at)
    {
        ResolvedFeeSchedule Fail(FeeStatus status, string reason) => new(schedule, null, status, schedule?.Fingerprint ?? "missing", reason);
        if (schedule is null) return Fail(FeeStatus.ScheduleUnavailable, "Explicit public fee refresh required.");
        if (at < schedule.RetrievedAt || at - schedule.RetrievedAt > MaximumAge) return Fail(FeeStatus.ScheduleStale, "Fee metadata is outside the one-hour freshness window.");
        if (schedule.Rules.IsDefaultOrEmpty) return Fail(FeeStatus.ScheduleUnavailable, schedule.VerificationIssue ?? "Missing schedule history.");
        if (schedule.Rules.Length > 512) return Fail(FeeStatus.InvalidFeeMetadata, "Excessive schedule history.");
        var active = schedule.Rules.Where(r => r.EffectiveFrom <= at).ToArray();
        if (active.GroupBy(r => (r.Level, r.EffectiveFrom)).Any(g => g.Select(r => (r.Type, r.Rate, r.Clear)).Distinct().Count() > 1))
            return Fail(FeeStatus.InvalidFeeMetadata, "Conflicting effective rules.");
        var basis = active.Where(r => r.Level != FeeSourceLevel.EventOverride).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        var over = active.Where(r => r.Level == FeeSourceLevel.EventOverride).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        var chosen = over is { Clear: false } ? over : basis;
        if (chosen is null) return Fail(FeeStatus.ScheduleUnavailable, "No effective fee rule.");
        if (chosen.Rate is null or < 0 or > 100 || chosen.Clear || string.IsNullOrWhiteSpace(chosen.Type)) return Fail(FeeStatus.InvalidFeeMetadata, "Invalid effective fee parameters.");
        var fingerprint = schedule.Fingerprint + ":" + chosen.Id + ":" + chosen.EffectiveFrom.UtcTicks;
        return new(schedule, chosen, schedule.VerificationIssue is null ? FeeStatus.KnownExact : FeeStatus.InvalidFeeMetadata, fingerprint, schedule.VerificationIssue);
    }
}
public sealed record FeeCalculationContext(string Exchange, string MarketId, string InstrumentId, LiquidityRole Role,
    decimal Quantity, decimal Price, DepthAction Action, KalshiFeeAccountProfile Profile);
public sealed record FeeComponent(decimal ModelFee, decimal RoundedTradeFee, decimal? RoundingFee, decimal? Rebate, decimal? TotalFee, decimal Accumulator);
public sealed record FeeQuote(FeeCalculationContext Context, decimal? ModelFee, decimal? RoundedTradeFee, decimal? RoundingFee,
    decimal? Rebate, decimal? TotalFee, string Currency, FeeStatus Status, string? Source, DateTimeOffset? EffectiveAt,
    DateTimeOffset? RetrievedAt, string ScheduleFingerprint, ImmutableArray<string> Warnings)
{
    public string ProgramRebates => "NotIncluded";
}
public static class FeeMath
{
    public static decimal CeilingToScale(decimal value, decimal unit) => checked(decimal.Ceiling(value / unit) * unit);
    public static decimal FloorToAccountPrecision(decimal value, decimal unit) => checked(decimal.Floor(value / unit) * unit);
    public static FeeComponent KalshiRound(decimal modelFee, decimal signedRevenue, KalshiFeeAccountProfile profile, decimal accumulator = 0)
    {
        if (modelFee < 0 || accumulator < 0 || !Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(modelFee));
        var trade = CeilingToScale(modelFee, .000001m);
        if (profile == KalshiFeeAccountProfile.Unknown) return new(modelFee, trade, null, null, null, accumulator);
        var unit = profile == KalshiFeeAccountProfile.DirectMember ? .0001m : .01m;
        var rounding = checked(signedRevenue - trade - FloorToAccountPrecision(signedRevenue - trade, unit));
        accumulator = checked(accumulator + rounding);
        var rebate = decimal.Min(FloorToAccountPrecision(accumulator, unit), FloorToAccountPrecision(trade + rounding, unit));
        return new(modelFee, trade, rounding, rebate, trade + rounding - rebate, accumulator - rebate);
    }
    public static decimal? KalshiModel(string type, decimal multiplier, decimal quantity, decimal price, LiquidityRole role) => type switch
    {
        "quadratic" => checked((role == LiquidityRole.Maker ? 0 : .07m) * multiplier * quantity * price * (1 - price)),
        "quadratic_with_maker_fees" => checked((role == LiquidityRole.Maker ? .0175m : .07m) * multiplier * quantity * price * (1 - price)),
        // The API's combo maker multiplier conflicts with the current regulatory schedule: unsupported.
        _ => null
    };
    public static FeeQuote Quote(ResolvedFeeSchedule resolved, FeeCalculationContext context, ref decimal accumulator)
    {
        var s = resolved.Schedule; var r = resolved.Rule;
        FeeQuote Unknown(FeeStatus status, string warning) => new(context, null, null, null, null, null, s?.Currency ?? "Unknown", status,
            s?.Source, r?.EffectiveFrom, s?.RetrievedAt, resolved.Fingerprint, [warning]);
        if (r is null || s is null) return Unknown(resolved.Status, resolved.Warning ?? "Fee rule unavailable.");
        if (context.Exchange != s.Exchange || context.MarketId != s.MarketId || !s.Instruments.Contains(context.InstrumentId) ||
            context.Quantity <= 0 || context.Price is <= 0 or >= 1 || context.Role == LiquidityRole.Unknown)
            return Unknown(FeeStatus.InvalidFeeMetadata, "Invalid or mismatched fee context.");
        try
        {
            decimal model; FeeComponent component; FeeStatus status; ImmutableArray<string> warnings = [];
            if (s.Exchange == "Polymarket")
            {
                if (r.Type != "prediction-quadratic-v1") return Unknown(FeeStatus.UnsupportedFeeType, "Unsupported Polymarket exponent or fee model.");
                if (r.Rate is null or < 0 or > 1) return Unknown(FeeStatus.InvalidFeeMetadata, "Invalid market fee rate.");
                model = context.Role == LiquidityRole.Maker ? 0 : checked(context.Quantity * r.Rate.Value * context.Price * (1 - context.Price));
                // Official page specifies subminimum zero, but not tie/direction mode. Ceiling is a conservative modeled-fill bound.
                var rounded = model < .00001m ? 0 : CeilingToScale(model, .00001m);
                status = rounded == model || model < .00001m ? FeeStatus.KnownExact : FeeStatus.ConservativeEstimate;
                component = new(model, rounded, 0, 0, rounded, 0);
                warnings = ["Program rebates/rewards/referrals: NotIncluded.", "Five-decimal ceiling is a conservative modeled-fill assumption; official rounding mode is unspecified."];
            }
            else if (s.Exchange == "Kalshi")
            {
                var value = KalshiModel(r.Type!, r.Rate!.Value, context.Quantity, context.Price, context.Role);
                if (value is null) return Unknown(FeeStatus.UnsupportedFeeType, "Fee type is not verified against the current regulatory formula.");
                model = value.Value;
                component = KalshiRound(model, checked(context.Quantity * context.Price * (context.Action == DepthAction.Buy ? -1 : 1)), context.Profile, accumulator);
                accumulator = component.Accumulator;
                status = context.Profile == KalshiFeeAccountProfile.Unknown ? FeeStatus.KnownModelAccountRoundingUnknown : FeeStatus.KnownExact;
            }
            else return Unknown(FeeStatus.UnsupportedFeeType, "Unsupported exchange.");
            if (resolved.Status == FeeStatus.InvalidFeeMetadata)
            { status = resolved.Status; component = component with { TotalFee = null }; warnings = warnings.Add(resolved.Warning!); }
            return new(context, model, component.RoundedTradeFee, component.RoundingFee, component.Rebate, component.TotalFee, s.Currency,
                status, s.Source, r.EffectiveFrom, s.RetrievedAt, resolved.Fingerprint, warnings);
        }
        catch (OverflowException) { return Unknown(FeeStatus.InvalidFeeMetadata, "Fee arithmetic exceeded decimal bounds."); }
    }
}
