namespace Arbitrage.Contracts;

public sealed record PaperAutomationSettingsRequest(string SizingMode, decimal FixedQuantity, decimal MinimumFeeAdjustedEdgePerShare,
    decimal MinimumFeeAdjustedProfit, int MaximumExecutionsPerSession, int MaximumExecutionsPerHour,
    int MaximumExecutionsPerRelationshipPerSession, int MinimumSecondsBetweenExecutions, int RelationshipCooldownSeconds,
    decimal MaximumSessionDebitFractionPerBucket, int MaximumCandidatesPerCycle, bool RequireRealtime, bool AllowPolymarketBestEffort,
    decimal? MinimumQuantity = null, decimal? MaximumQuantity = null, decimal? QuantityStep = null);
public sealed record PaperAutomationProfileResponse(int PolicyVersion, Guid Revision, PaperAutomationSettingsRequest Settings,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid UpdatedBy, string PolicyFingerprint);
public sealed record SavePaperAutomationRequest(Guid? ExpectedRevision, bool ConfirmPolicy, PaperAutomationSettingsRequest Settings);
public sealed record ArmPaperAutomationRequest(Guid ExpectedProfileRevision, Guid ExpectedRiskRevision, Guid ExpectedGenerationId,
    Guid? ExpectedKillRevision, bool ConfirmSimulation);
public sealed record PaperAutomationControlRequest(Guid? ExpectedRevision, bool ConfirmReset, string Reason);
public sealed record PaperAutomationKillResponse(Guid? Revision, bool IsLatched, DateTimeOffset? LatchedAt, Guid? LatchedBy,
    string Reason, DateTimeOffset? ResetAt, Guid? ResetBy);
public sealed record PaperAutomationDebitResponse(string Exchange, string Currency, decimal Notional, decimal Fees, decimal Total);
public sealed record PaperAutomationStatusResponse(string State, string StopReason, PaperAutomationProfileResponse? Profile,
    Guid? RiskRevision, Guid? GenerationId, PaperAutomationKillResponse KillSwitch, Guid? SessionId, DateTimeOffset? ArmedAt,
    Guid? ArmedBy, string MonitoringState, int ExecutionsCommitted, int CandidatesConsidered, int CandidatesSkipped,
    int ExecutionsRejected, DateTimeOffset? LastActivityAt, DateTimeOffset? LastCommittedAt, PaperAutomationDebitResponse[] SessionDebits,
    int QueueDepth, IReadOnlyDictionary<string, long> Counters, string? LastSizingState = null, decimal? LastSelectedQuantity = null, int LastSizingCandidatesEvaluated = 0);
public sealed record PaperAutomationProofResponse(Guid SessionId, int ProfileVersion, Guid ProfileRevision, string ProfileFingerprint,
    string TriggerInputStamp, Guid RelationshipId, DateTimeOffset TriggeredAt, PaperSizingProofResponse? Sizing = null);
public sealed record PaperSizingPreviewRequest(string OpportunityKey);
public sealed record PaperSizingRejectionResponse(string Reason, int Count);
public sealed record PaperSizingProofResponse(int Version, string Mode, decimal SelectedQuantity, decimal MinimumQuantity, decimal MaximumQuantity,
    decimal QuantityStep, int CandidatesEvaluated, PaperSizingRejectionResponse[] Rejections, string TriggerInputStamp,
    Guid AutomationProfileRevision, Guid RiskPolicyRevision, Guid GenerationId, long FinancialRevision, string PlanFingerprint,
    PaperAutomationDebitResponse[] SessionDebits, string DecisionFingerprint);
public sealed record PaperSizingPreviewResponse(string State, string Mode, decimal? SelectedQuantity, int CandidatesEvaluated,
    decimal HighestConfiguredQuantity, decimal LowestConfiguredQuantity, decimal QuantityStep, PaperSizingRejectionResponse[] Rejections,
    decimal? GrossCost, decimal? TotalFees, decimal? Cost, decimal? ExpectedPayout, decimal? ExpectedProfit, decimal? EdgePerShare,
    PaperRiskDecisionResponse? RiskDecision, PaperSizingProofResponse? Proof, DateTimeOffset EvaluatedAt);
