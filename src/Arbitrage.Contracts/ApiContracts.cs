namespace Arbitrage.Contracts;

public sealed record Capabilities(bool WorkspaceSettings, bool PaperExecutionImplemented,
    bool LiveOrderSubmissionAvailable, bool ManualLiveExecutionAvailable, bool AutomaticLiveExecutionAvailable)
{
    public static Capabilities Phase01A { get; } = new(true, false, false, false, false);
}
public sealed record SessionResponse(Guid UserId, Guid DefaultWorkspaceId, string DeploymentMode, Capabilities Capabilities);
public sealed record SystemStatusResponse(string BackendVersion, double UptimeSeconds, string PersistenceState,
    string DeploymentMode, string ConfiguredTradingMode, string EffectiveTradingMode, Capabilities Capabilities);
public sealed record TradingModeResponse(string ConfiguredMode, string EffectiveMode, Capabilities Capabilities);
public sealed record ExchangeStatusResponse(string Exchange, string IntegrationState);
public sealed record WorkspaceSettingsResponse(Guid WorkspaceId, string DisplayName);
public sealed record UpdateWorkspaceSettingsRequest(string? DisplayName);
public sealed record ApiError(string Code, string Message, string CorrelationId);

// REST is authoritative; hub messages only invalidate this snapshot or carry diagnostics.
public sealed record ApplicationSnapshotResponse(int Version, Guid BackendInstanceId, Guid LocalProfileId, DateTimeOffset CapturedAtUtc,
    SessionResponse Session, SystemStatusResponse System, WorkspaceSettingsResponse Workspace,
    ExchangeStatusResponse[] Exchanges);
public sealed record WorkspaceSubscriptionResponse(Guid BackendInstanceId, Guid WorkspaceId, DateTimeOffset SubscribedAtUtc);
public sealed record StateInvalidation(Guid BackendInstanceId, Guid WorkspaceId, string Kind);
public sealed record ApplicationHeartbeat(Guid BackendInstanceId, DateTimeOffset SentAtUtc, string PersistenceState);
public sealed record BackendDiagnosticEvent(Guid BackendInstanceId, long Sequence, DateTimeOffset OccurredAtUtc,
    string Severity, string Source, string Code, string Message, Guid WorkspaceId, string? CorrelationId);
public sealed record RecentDiagnosticsResponse(Guid BackendInstanceId, Guid WorkspaceId, long OldestSequence,
    long NewestSequence, long DroppedCount, bool Gap, BackendDiagnosticEvent[] Events);
public sealed record StopLocalRuntimeRequest(Guid ExpectedBackendInstanceId);
public sealed record StopLocalRuntimeResponse(Guid BackendInstanceId, string Status);

// Phase 02A: cached public metadata, not orderbook quotes or trading readiness.
public sealed record MarketOutcomeResponse(string Label, string? NativeTokenId);
public sealed record MarketResponse(string Exchange, string Environment, string NativeId, string? EventId,
    string? SeriesId, string? GroupId, string? Classification, string? Title, string? Subtitle,
    string? Category, string[] Tags, string? NativeStatus, string Status, MarketOutcomeResponse[] Outcomes,
    DateTimeOffset? CreatedAt, DateTimeOffset? OpenAt, DateTimeOffset? CloseAt,
    DateTimeOffset? ExpectedResolutionAt, DateTimeOffset? ResolvedAt, DateTimeOffset? SourceUpdatedAt,
    string? Description, string? Rules, string? SourceReference, DateTimeOffset RetrievedAt,
    string[] Warnings, bool IsIncomplete);
public sealed record MarketPageResponse(MarketResponse[] Items, int Total, int Page, int PageSize,
    string ScopeNotice, string[] AvailableTags);
public sealed record DiscoveryRunResponse(Guid Id, string Exchange, string Scope, string? CurrentScope,
    string State, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Pages,
    int MarketsObserved, int MalformedRecords, string? ErrorCode, DateTimeOffset? RetryAt);
public sealed record ExchangeCatalogStatusResponse(string Exchange, string Scope, int StoredMarkets,
    DateTimeOffset? LastCompletedAt, DateTimeOffset? LatestRetrievedAt, DiscoveryRunResponse? LatestRun,
    string CatalogCapability, string ExternalAccess, string OrderbookCapability, string ExecutionCapability);
public sealed record CatalogStatusResponse(ExchangeCatalogStatusResponse[] Exchanges, string ScopeNotice,
    DateTimeOffset CheckedAt);
public sealed record StartMarketSyncRequest(string Exchange);
public sealed record StartMarketSyncResponse(DiscoveryRunResponse[] Runs);
public sealed record CatalogInvalidation(Guid BackendInstanceId, Guid WorkspaceId, string Exchange);
