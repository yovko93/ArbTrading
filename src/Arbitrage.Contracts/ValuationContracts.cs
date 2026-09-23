namespace Arbitrage.Contracts;

public sealed record PaperPositionMarkResponse(PaperPositionResponse Position, string MarkStatus, string Quality, DateTimeOffset ValuedAt,
    decimal? ExecutableQuantity, decimal? UnfilledQuantity, decimal? GrossLiquidationValue, decimal? PartialGrossLiquidationValue,
    decimal? AverageLiquidationPrice, decimal? PartialAveragePrice, decimal? WorstLiquidationPrice, decimal? EstimatedExitFees, string ExitFeeStatus,
    decimal? FeeAdjustedLiquidationValue, decimal? GrossUnrealizedPnlBeforeExitFees, decimal? FeeAdjustedUnrealizedPnl,
    decimal? GrossReturnOnCost, decimal? FeeAdjustedReturnOnCost, long? BookVersion, string? SourceMode, string? Continuity,
    DateTimeOffset? BookRetrievedAt, DateTimeOffset? BookSourceTimestamp, double? BookAgeSeconds,
    OpportunityLiquidityResponse[] NativeLiquidity, string[] Warnings)
{
    public string Method => "ExecutableLiquidation";
    public FeeQuoteResponse[] ExitFeeBreakdown { get; init; } = [];
    public string ExitFeeProfile { get; init; } = "Unknown";
}
public sealed record PaperValuationBucketResponse(string Exchange, string Currency, decimal StartingCash, decimal CurrentCash,
    decimal OpenCostBasis, decimal RealizedPnl, int OpenPositionCount, int FullyMarkedPositionCount, int PartiallyMarkedPositionCount,
    int UnavailablePositionCount, decimal? GrossMarkedOpenValue, decimal? FeeAdjustedMarkedOpenValue, decimal KnownGrossMarkedOpenValue,
    decimal AccountingBookValue, decimal? GrossMarkedEquity, decimal? FeeAdjustedMarkedEquity, decimal? GrossTotalPnl,
    decimal? FeeAdjustedTotalPnl, decimal ValuationCoverage, decimal? CapitalUtilizationByCost, decimal LargestPositionCostBasis,
    decimal? LargestPositionCostShare, int UniqueMarketCount);
public sealed record PaperValuationResponse(Guid? GenerationId, string State, bool HistoricalGeneration, DateTimeOffset ValuedAt,
    PaperPositionMarkResponse[] Positions, PaperValuationBucketResponse[] Buckets, int Page, bool HasMore, string[] Warnings);
