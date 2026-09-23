namespace Arbitrage.Contracts;

public sealed record FeeMarketRequest(string Exchange, string MarketId);
public sealed record RefreshFeesRequest(FeeMarketRequest[] Markets, int RuntimeSeconds = 30);
public sealed record FeeRefreshJobResponse(Guid Id, string State, int CompletedMarkets, int RequestedMarkets, string? Notice);
public sealed record FeeProfileResponse(string Profile);
public sealed record FeeRuleResponse(string Id, string? Type, decimal? Rate, DateTimeOffset EffectiveFrom, string Level, bool Clear);
public sealed record FeeScheduleResponse(string Exchange, string MarketId, string Status, string? Currency, DateTimeOffset? RetrievedAt,
    string? Source, string? Fingerprint, string? Warning, FeeRuleResponse[] Rules);
public sealed record FeeQuoteResponse(string Exchange, string MarketId, string InstrumentId, string LiquidityRole, decimal Quantity, decimal Price,
    decimal? ModelFee, decimal? RoundedTradeFee, decimal? RoundingFee, decimal? Rebate, decimal? TotalFee, string Currency, string Status,
    string? Source, DateTimeOffset? EffectiveAt, DateTimeOffset? RetrievedAt, string ScheduleFingerprint, string[] Warnings, string ProgramRebates);
public sealed record OpportunityFeesResponse(string State, string Status, string Profile, FeeQuoteResponse[] Breakdown, decimal? TotalExchangeFees,
    decimal? FeeAdjustedCost, decimal? FeeAdjustedGuaranteedProfit, decimal? FeeAdjustedEdgePerShare, decimal? FeeAdjustedReturnOnCost, decimal MinimumEdge)
{
    public Guid ProfileRevision { get; init; }
    public string StateLabel => State switch
    {
        "FeeAdjustedDetected" => "Fee-adjusted opportunity (estimate)",
        "GrossNotDetected" => "No current gross opportunity",
        "FeeAdjustedNoEdge" => "Gross edge removed by fees",
        "FeeScheduleUnavailable" => "Fee metadata unavailable",
        "FeeScheduleStale" => "Fee metadata stale",
        "FeeResultStale" => "Fee result stale",
        "AccountFeeProfileRequired" => "Kalshi account precision profile required",
        "UnsupportedFeeModel" => "Unsupported or disputed fee model",
        _ => "Gross opportunity — fees unknown"
    };
    public string Assumptions => "One hypothetical taker order per leg; one modeled fill per consumed price level. Actual fill fragmentation is unknown. Other costs excluded.";
}
