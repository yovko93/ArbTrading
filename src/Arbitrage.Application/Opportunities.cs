using System.Collections.Immutable;
using Arbitrage.Domain;

namespace Arbitrage.Application;

public enum OpportunityStrategy { CrossMarketBuyBothComplements, CrossMarketSameOutcomeSpread, SingleMarketBinaryComplement }
public enum OpportunityStatus { Detected, NoGrossEdge, InsufficientLiquidity, RelationshipIneligible, BookUnavailable, BookStale, BookInvalid, BookContinuityInsufficient, BookSkewTooLarge, BooksChangedDuringEvaluation, UnsupportedStrategy, LiquidityConflict, RequiresInventory, StaleInput, ArithmeticOverflow }
public enum OpportunityInputQuality { RealtimeContinuous, RealtimeBestEffort, FreshRest, Mixed, NonActionable }
public enum RelationshipTrust { Deterministic, Manual }
public sealed record OpportunitySettings(decimal MinimumGrossEdgePerShare = .001m, decimal MaximumEvaluationQuantity = 1000m,
    decimal? MaximumEvaluationNotional = null, int MaximumSkewMilliseconds = 1000, decimal? RequestedQuantity = null)
{
    public bool Valid => MinimumGrossEdgePerShare is >= 0 and < 1 && MaximumEvaluationQuantity is > 0 and <= 1_000_000_000m &&
        (MaximumEvaluationNotional is null or > 0 and <= 1_000_000_000m) && MaximumSkewMilliseconds is >= 0 and <= 60000 &&
        (RequestedQuantity is null or > 0 and <= 1_000_000_000m);
}
public sealed record OpportunityPlan(ApprovedRelationship Relationship, OpportunityStrategy Strategy, OrderBookInstrumentId A, OrderBookInstrumentId B,
    DepthAction ActionA, DepthAction ActionB, bool ComplementProven, string? UnsupportedReason = null);
public sealed record PairedDepthSegment(decimal Quantity, decimal LegAPrice, decimal LegBPrice, decimal CombinedPrice, decimal GrossEdgePerShare,
    decimal CumulativeCost, decimal CumulativeGuaranteedPayout, decimal CumulativeGrossProfit);
public sealed record OpportunityLeg(OrderBookInstrumentId Instrument, DepthAction Action, decimal Quantity, decimal? AveragePrice, decimal? WorstPrice,
    decimal GrossNotional, BookSourceMode SourceMode, BookContinuity Continuity, long SnapshotVersion, DateTimeOffset? RetrievedAt,
    DateTimeOffset? SourceTimestamp, ImmutableArray<LiquidityOrigin> LiquidityOrigins, ImmutableArray<LiquiditySourceId> LiquiditySources);
public sealed record ArbitrageOpportunitySnapshot(string OpportunityKey, OpportunityStrategy Strategy, Guid RelationshipId, RelationshipTrust RelationshipTrust,
    DateTimeOffset EvaluatedAt, OpportunityStatus Status, ImmutableArray<string> Blockers, ImmutableArray<string> Warnings,
    OpportunityInputQuality InputQuality, TimeSpan? ObservedSkew, TimeSpan MaximumAllowedSkew, bool SkewAcceptable,
    ImmutableArray<OpportunityLeg> Legs, ImmutableArray<PairedDepthSegment> Segments, decimal PairedQuantity, decimal FullyExecutableQuantity,
    decimal GrossCost, decimal GrossProceeds, decimal GuaranteedGrossPayout, decimal GrossProfit, decimal? GrossEdgePerShare, decimal? GrossReturnOnCost,
    bool RelationshipEligible, bool BooksActionable, bool FullyExecutableForRequestedQuantity, bool EvaluationQuantityCapped, bool EvaluationNotionalCapped,
    int RelationshipPolicyVersion, string SourceFingerprint, string TargetFingerprint, string RelationshipRevision)
{
    public string? SourceTitle { get; init; }
    public decimal? BestObservedGrossEdge { get; init; }
    public decimal BestObservedQuantity { get; init; }
    public string? TargetTitle { get; init; }
    public ImmutableArray<OutcomeMapping> OutcomeMappings { get; init; } = [];
    public bool GrossArbitrageExists => Status == OpportunityStatus.Detected && GrossProfit > 0;
    public bool ExecutionEligible => false;
    public OpportunityFees? Fees { get; init; }
    public string FeeStatus => Fees?.Status.ToString() ?? "NotEvaluated";
    public decimal? NetProfit => null;
    public decimal? NetEdge => null;
    public ArbitrageOpportunitySnapshot Invalidate(OpportunityStatus status, string reason) => this with
    { Status = status, Blockers = [reason], InputQuality = OpportunityInputQuality.NonActionable, BooksActionable = false, FullyExecutableForRequestedQuantity = false, Fees = Fees?.Invalidate(), BestObservedGrossEdge = null, BestObservedQuantity = 0 };
}
