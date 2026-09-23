namespace Arbitrage.Contracts;

public sealed record PaperRiskLimitsRequest(decimal MinimumCashReserveFraction, decimal MaximumSingleExecutionDebitFraction,
    decimal MaximumOpenCostBasisFraction, decimal MaximumMarketCostBasisFraction, decimal MaximumInstrumentCostBasisFraction,
    int MaximumOpenPositions, int MaximumOpenExecutions, int MaximumOpenExecutionsPerRelationship,
    decimal MaximumRequestedQuantity, decimal MinimumFeeAdjustedEdgePerShare, decimal MinimumFeeAdjustedProfit);
public sealed record SavePaperRiskPolicyRequest(Guid? ExpectedRevision, bool ConfirmPolicy, PaperRiskLimitsRequest Limits);
public sealed record PaperRiskPolicyResponse(int PolicyVersion, Guid Revision, PaperRiskLimitsRequest Limits,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid UpdatedBy, string Fingerprint);
public sealed record PaperRiskViolationResponse(string Code, string? Exchange, string? Currency, string? MarketId, string? InstrumentId,
    decimal? CurrentValue, decimal? ProposedDelta, decimal? ProjectedValue, decimal? LimitValue, decimal? RemainingHeadroom);
public sealed record PaperRiskBucketResponse(string Exchange, string Currency, decimal InitialCash, decimal AvailableCash,
    decimal ProposedDebit, decimal ProjectedAvailableCash, decimal RequiredCashReserve, decimal CashReserveHeadroom,
    decimal CurrentOpenCostBasis, decimal ProjectedOpenCostBasis, decimal MaximumOpenCostBasis, decimal OpenCostHeadroom,
    decimal LargestMarketCostBasis, decimal MaximumMarketCostBasis, decimal LargestInstrumentCostBasis, decimal MaximumInstrumentCostBasis);
public sealed record PaperRiskExposureResponse(string Exchange, string Currency, string MarketId, string? InstrumentId,
    decimal CurrentValue, decimal ProposedDelta, decimal ProjectedValue, decimal LimitValue, decimal RemainingHeadroom);
public sealed record PaperRiskRelationshipResponse(Guid RelationshipId, int OpenExecutionCount, int MaximumOpenExecutionsPerRelationship);
public sealed record PaperRiskDecisionResponse(string Decision, int? PolicyVersion, Guid? PolicyRevision, string? PolicyFingerprint,
    Guid? GenerationId, long FinancialRevision, PaperRiskViolationResponse[] Violations, PaperRiskBucketResponse[] BucketAssessments,
    PaperRiskExposureResponse[] Exposures, int CurrentOpenPositions, int ProjectedOpenPositions, int CurrentOpenExecutions,
    int ProjectedOpenExecutions, int CurrentRelationshipOpenExecutions, int ProjectedRelationshipOpenExecutions,
    PaperRiskRelationshipResponse[] Relationships, DateTimeOffset EvaluatedAt);
public sealed record PaperRiskStatusResponse(string State, PaperRiskPolicyResponse? Policy, PaperRiskDecisionResponse Assessment);
