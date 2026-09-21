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
