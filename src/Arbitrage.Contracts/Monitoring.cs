namespace Arbitrage.Contracts;

public sealed record MonitoringProfileResponse(int RelationshipLimit = 250, bool IncludeManualRelationships = false,
    decimal MinimumGrossEdge = .001m, decimal MinimumFeeAdjustedEdge = 0, decimal MaximumQuantity = 1000,
    int MaximumSkewMilliseconds = 1000, decimal NearEdgeWindow = .005m, string Sort = "default",
    bool EnableFeeAdjustedAlerts = true, bool EnableGrossOnlyAlerts = false,
    decimal FeeAlertEdge = .005m, decimal FeeAlertProfit = .01m, decimal GrossAlertEdge = .005m, decimal GrossAlertProfit = .01m,
    decimal RearmHysteresis = .001m, int CooldownSeconds = 60, bool CsvEnabled = false,
    int CsvIntervalSeconds = 30, int CsvTopRows = 100, int AlertRetentionCount = 5000, int AlertRetentionDays = 30);
public sealed record MonitoringCoverageResponse(int ApprovedRelationshipsAvailable, int RelationshipsMonitored, int RelationshipsSkippedByBound,
    bool CoveragePartial, int PlansBuilt, int PlansWithBooksAvailable, int PlansWithActionableBooks, int PlansWithResolvedFees,
    int FeeAdjustedOpportunities, int GrossOnlyOpportunities, int NearEdgeCandidates, int BlockedCandidates);
public sealed record MonitoringStatusResponse(string State, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, DateTimeOffset? LastEvaluationAt,
    MonitoringCoverageResponse Coverage, int DirtyQueueDepth, bool ReconciliationRequired, string? LastErrorCode, long Generation,
    long DirtyNotificationsReceived, long DirtyNotificationsCoalesced, long DirtyNotificationsDropped, long ReconciliationPasses,
    long EvaluationsStarted, long EvaluationsCompleted, long EvaluationFailures, long RankingUpdates, long AlertsRaised,
    long AlertsSuppressedCooldown, long AlertsSuppressedNotRearmed, string CsvExportStatus, string? LastCsvErrorCode,
    long CsvSnapshotsWritten, long CsvWriteFailures);
public sealed record MonitoringRankingResponse(int Rank, string Lane, long EvaluationGeneration, OpportunityResponse Opportunity,
    decimal? BestEdge, decimal? RequiredEdge, decimal? Distance, decimal AvailableQuantity, string AlertState)
{ public long ProfileVersion { get; init; } }
public sealed record MonitoringRankingPage(MonitoringRankingResponse[] Items, int Total, int Page, int PageSize);
public sealed record MonitoringAlertResponse(Guid AlertId, DateTimeOffset TriggeredAt, string Lane, string Reason, MonitoringRankingResponse Opportunity)
{ public string Severity { get; init; } = "Opportunity"; }
public sealed record MonitoringAlertPage(MonitoringAlertResponse[] Items, int Total, int Page, int PageSize);
public sealed record MonitoringInvalidation(Guid InstanceId, Guid WorkspaceId, long Generation, Guid? AlertId = null);
