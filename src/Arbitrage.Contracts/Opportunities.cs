namespace Arbitrage.Contracts;

public sealed record EvaluateOpportunitiesRequest(Guid? RelationshipId = null, bool IncludeManualRelationships = false,
    string? Exchange = null, string? TargetExchange = null, int MaximumRelationshipsPerRun = 100, int RuntimeSeconds = 10,
    int MaximumOpportunitiesReturned = 30, decimal MinimumGrossEdgePerShare = .001m, decimal MaximumEvaluationQuantity = 1000m,
    int MaximumSkewMilliseconds = 1000, decimal? RequestedQuantity = null, decimal? MaximumEvaluationNotional = null);
public sealed record OpportunityJobResponse(Guid Id, string State, int RelationshipsScanned, int Results, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string? Notice);
public sealed record OpportunityPageResponse(OpportunityResponse[] Items, int Total, int Page, int PageSize, OpportunityJobResponse Job);
public sealed record OpportunityLiquidityResponse(string Exchange, string MarketId, string InstrumentId, string NativeSide, decimal NativePrice);
public sealed record OpportunityLegResponse(string Exchange, string MarketId, string InstrumentId, string Outcome, string Action,
    decimal Quantity, decimal? AveragePrice, decimal? WorstPrice, decimal GrossNotional, string SourceMode, string Continuity,
    long SnapshotVersion, DateTimeOffset? RetrievedAt, DateTimeOffset? SourceTimestamp, string[] LiquidityOrigins, OpportunityLiquidityResponse[] LiquiditySources);
public sealed record OpportunitySegmentResponse(decimal Quantity, decimal LegAPrice, decimal LegBPrice, decimal CombinedPrice, decimal GrossEdgePerShare,
    decimal CumulativeCost, decimal CumulativeGuaranteedPayout, decimal CumulativeGrossProfit);
public sealed record OpportunityResponse(string OpportunityKey, string Strategy, Guid RelationshipId, string RelationshipTrust, DateTimeOffset EvaluatedAt,
    string Status, string[] Blockers, string[] Warnings, string InputQuality, decimal? ObservedSkewMilliseconds, decimal MaximumAllowedSkewMilliseconds,
    bool SkewAcceptable, OpportunityLegResponse[] Legs, OpportunitySegmentResponse[] Segments, decimal PairedQuantity, decimal FullyExecutableQuantity,
    decimal GrossCost, decimal GrossProceeds, decimal GuaranteedGrossPayout, decimal GrossProfit, decimal? GrossEdgePerShare, decimal? GrossReturnOnCost,
    bool RelationshipEligible, bool BooksActionable, bool GrossArbitrageExists, bool FullyExecutableForRequestedQuantity, bool EvaluationQuantityCapped,
    bool EvaluationNotionalCapped, int RelationshipPolicyVersion, string SourceFingerprint, string TargetFingerprint, string RelationshipRevision,
    bool ExecutionEligible, string FeeStatus, decimal? NetProfit, decimal? NetEdge)
{
    public string? SourceTitle { get; init; }
    public string? TargetTitle { get; init; }
    public RelationshipMappingResponse[] OutcomeMappings { get; init; } = [];
}
