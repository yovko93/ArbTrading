using System.Collections.Immutable;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Execution;

public enum PaperMarkStatus { NotApplicable, Marked, PartialDepth, BookUnavailable, BookStale, BookInvalid,
    BookContinuityInsufficient, BookChangedDuringValuation, FeeModelUnresolved, CurrencyUnsupported, InstrumentUnsupported, ArithmeticOverflow }
public enum PaperMarkQuality { NonActionable, FreshRest, RealtimeContinuous, RealtimeBestEffort }
public sealed record PaperInventory(Guid PositionId, Guid GenerationId, OrderBookInstrumentId Instrument, string Currency,
    PaperPositionStatus Status, decimal Quantity, decimal CostBasis, decimal HistoricalEntryFees, decimal AverageEntryPrice,
    decimal? SettlementPayout, decimal? RealizedPnl);
public sealed record PaperMark(PaperInventory Position, PaperMarkStatus MarkStatus, PaperMarkQuality Quality, DateTimeOffset ValuedAt)
{
    public string Method => "ExecutableLiquidation";
    public decimal? ExecutableQuantity { get; init; }
    public decimal? UnfilledQuantity { get; init; }
    public decimal? GrossLiquidationValue { get; init; }
    public decimal? PartialGrossLiquidationValue { get; init; }
    public decimal? AverageLiquidationPrice { get; init; }
    public decimal? PartialAveragePrice { get; init; }
    public decimal? WorstLiquidationPrice { get; init; }
    public decimal? EstimatedExitFees { get; init; }
    public FeeStatus ExitFeeStatus { get; init; } = FeeStatus.NotEvaluated;
    public decimal? FeeAdjustedLiquidationValue { get; init; }
    public decimal? GrossUnrealizedPnlBeforeExitFees { get; init; }
    public decimal? FeeAdjustedUnrealizedPnl { get; init; }
    public decimal? GrossReturnOnCost { get; init; }
    public decimal? FeeAdjustedReturnOnCost { get; init; }
    public long? BookVersion { get; init; }
    public BookSourceMode? SourceMode { get; init; }
    public BookContinuity? Continuity { get; init; }
    public DateTimeOffset? BookRetrievedAt { get; init; }
    public DateTimeOffset? BookSourceTimestamp { get; init; }
    public TimeSpan? BookAge { get; init; }
    public ImmutableArray<LiquiditySourceId> NativeLiquidity { get; init; } = [];
    public ImmutableArray<FeeQuote> ExitFeeBreakdown { get; init; } = [];
    public ImmutableArray<string> Warnings { get; init; } = [];
}

// Pure read model. No cash, journal, order source or database dependency.
public static class PaperValuation
{
    public static PaperMark Mark(PaperInventory p, bool identitySupported, CachedOrderBook b, ResolvedFeeSchedule fee,
        KalshiFeeAccountProfile profile, DateTimeOffset now)
    {
        var result = new PaperMark(p, PaperMarkStatus.NotApplicable, PaperMarkQuality.NonActionable, now);
        if (p.Status == PaperPositionStatus.Settled) return result;
        if (!identitySupported || p.Quantity <= 0) return result with { MarkStatus = PaperMarkStatus.InstrumentUnsupported };
        if (p.Currency is not ("USD" or "USDC")) return result with { MarkStatus = PaperMarkStatus.CurrencyUnsupported };
        result = result with { BookVersion = b.Version, SourceMode = b.Source, Continuity = b.Realtime?.Continuity ?? BookContinuity.NotApplicable,
            BookRetrievedAt = b.Snapshot?.RetrievedAtUtc, BookSourceTimestamp = b.Snapshot?.SourceTimestamp, BookAge = b.Eligibility.Age,
            Warnings = ["Local cached depth estimate; not an order or a prediction of future execution."] };
        if (b.Failure?.Code == "InvalidOrderBook" || b.Snapshot is { Validity: not BookValidity.Valid }) return result with { MarkStatus = PaperMarkStatus.BookInvalid };
        if (b.Snapshot is null) return result with { MarkStatus = PaperMarkStatus.BookUnavailable };
        if (b.Snapshot.Instrument != p.Instrument) return result with { MarkStatus = PaperMarkStatus.InstrumentUnsupported };
        if (b.Eligibility.Freshness == BookFreshness.Stale) return result with { MarkStatus = PaperMarkStatus.BookStale };
        if (!b.Eligibility.IsActionable) return result with { MarkStatus = b.Source == BookSourceMode.Realtime ? PaperMarkStatus.BookContinuityInsufficient : PaperMarkStatus.BookUnavailable };
        result = result with { Quality = b.Source == BookSourceMode.RestSnapshot ? PaperMarkQuality.FreshRest :
            b.Realtime?.Continuity == BookContinuity.Continuous ? PaperMarkQuality.RealtimeContinuous : PaperMarkQuality.RealtimeBestEffort };
        try
        {
            var depth = ExecutableDepth.Calculate(b.Snapshot, b.Eligibility, DepthAction.Sell, p.Quantity);
            result = result with { ExecutableQuantity = depth.ExecutableQuantity, UnfilledQuantity = depth.UnfilledQuantity,
                NativeLiquidity = depth.ConsumedLevels.Select(l => b.Snapshot.LiquiditySource(l)!).ToImmutableArray() };
            if (!depth.IsFullyExecutable) return result with { MarkStatus = PaperMarkStatus.PartialDepth,
                PartialGrossLiquidationValue = depth.GrossNotional, PartialAveragePrice = depth.Vwap };
            var grossPnl = checked(depth.GrossNotional - p.CostBasis);
            result = result with { MarkStatus = PaperMarkStatus.Marked, GrossLiquidationValue = depth.GrossNotional,
                AverageLiquidationPrice = depth.Vwap, WorstLiquidationPrice = depth.WorstPrice,
                GrossUnrealizedPnlBeforeExitFees = grossPnl, GrossReturnOnCost = p.CostBasis > 0 ? grossPnl / p.CostBasis : null };
            decimal accumulator = 0;
            var quotes = depth.ConsumedLevels.Select(l => FeeMath.Quote(fee, new(p.Instrument.Exchange, p.Instrument.NativeMarketId,
                p.Instrument.NativeInstrumentId, LiquidityRole.Taker, l.Quantity, l.Price, DepthAction.Sell, profile), ref accumulator)).ToImmutableArray();
            result = result with { ExitFeeBreakdown = quotes, Warnings = result.Warnings.AddRange(quotes.SelectMany(q => q.Warnings).Distinct()) };
            if (quotes.FirstOrDefault(q => q.TotalFee is null) is { } unknown) return result with { ExitFeeStatus = unknown.Status };
            if (quotes.Any(q => q.Currency != p.Currency)) return result with { ExitFeeStatus = FeeStatus.InvalidFeeMetadata,
                Warnings = result.Warnings.Add("Exit fee currency differs from historical position currency; no conversion assumed.") };
            var fees = quotes.Sum(q => q.TotalFee!.Value); var net = checked(depth.GrossNotional - fees); var pnl = checked(net - p.CostBasis);
            return result with { EstimatedExitFees = fees, ExitFeeStatus = FeeStatus.Estimated, FeeAdjustedLiquidationValue = net,
                FeeAdjustedUnrealizedPnl = pnl, FeeAdjustedReturnOnCost = p.CostBasis > 0 ? pnl / p.CostBasis : null };
        }
        catch (OverflowException) { return new(p, PaperMarkStatus.ArithmeticOverflow, PaperMarkQuality.NonActionable, now); }
    }
}
