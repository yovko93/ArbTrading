namespace Arbitrage.Contracts;

public sealed record PaperReliabilityActionRequest(Guid? CampaignId = null, Guid? ExpectedRevision = null, string? Name = null, string? Notes = null);
public sealed record PaperReliabilityCampaignResponse(Guid Id, Guid WorkspaceId, Guid CreatedBy, Guid Revision, string Name, string Notes,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt, string State, int PolicyVersion, string PolicyFingerprint,
    bool EvidenceGapDetected, bool InvariantViolationDetected, bool EventRetentionTruncated, Guid? LatestEvaluationId);
public sealed record PaperReliabilityCheckResponse(string Code, decimal RequiredValue, decimal ObservedValue, string State, string ExplanationCode);
public sealed record PaperReliabilityEconomicsResponse(Guid GenerationId, string Exchange, string Currency, decimal EntryCost, decimal ExpectedPayoutAtEntry,
    decimal ExpectedProfitAtEntry, decimal SettlementPayout, decimal RealizedPaperProfit, decimal UnsettledCostBasis, decimal ModeledFees);
public sealed record PaperReliabilityExecutionReference(Guid ExecutionId, Guid GenerationId, Guid? SessionId, string OpportunityKey, string? TriggerInputStamp,
    string? SizingDecisionFingerprint, Guid? RiskPolicyRevision, Guid? AutomationProfileRevision, DateTimeOffset CommittedAt);
public sealed record PaperReliabilityReportResponse(Guid CampaignId, Guid WorkspaceId, string Name, DateTimeOffset StartedAt, DateTimeOffset EvaluatedAt,
    string CampaignState, int PolicyVersion, string PolicyFingerprint, string State, bool EvidenceGapDetected,
    IReadOnlyDictionary<string, long> Counters, PaperReliabilityCheckResponse[] Criteria, PaperReliabilityCheckResponse[] Invariants,
    PaperReliabilityEconomicsResponse[] Economics, PaperReliabilityExecutionReference[] Executions,
    IReadOnlyDictionary<string, decimal> Statistics, string Disclaimer, string[] Limitations, string EvidenceFingerprint);
public sealed record PaperReliabilityCurrentResponse(PaperReliabilityCampaignResponse? Campaign, PaperReliabilityReportResponse? Report, long TelemetryPersistenceFailures);
public sealed record PaperReliabilityExportResponse(string Path);
public sealed record PaperReliabilityEventResponse(Guid Id, Guid CampaignId, DateTimeOffset At, string Kind, Guid? ReferenceId, long? WriterOrder);
public sealed record PaperReliabilityEvaluationResponse(Guid Id, Guid CampaignId, DateTimeOffset At);
