using Arbitrage.Desktop.Services;
using Arbitrage.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arbitrage.Desktop.ViewModels;

// One long-lived authenticated snapshot shared by the shell's pages.
public partial class MainViewModel(BackendClient backend, ILogger<MainViewModel> logger,
    DesktopDiagnostics? diagnostics = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource lifetime = new();
    private Guid? workspaceId;
    private string savedWorkspaceName = "";
    private int operationActive;
    private long privateStateGeneration;
    public Func<Task>? RefreshRequested { get; set; }
    // Raised synchronously before clearing private UI state so realtime work is invalidated too.
    public event EventHandler? AccessInvalidated;
    public event EventHandler<OrderBookInvalidation>? OrderBookInvalidated;
    internal void NotifyOrderBookInvalidated(OrderBookInvalidation notice) => OrderBookInvalidated?.Invoke(this, notice);
    public event EventHandler? CatalogInvalidated;
    public event EventHandler? CatalogRefreshRequested;
    internal void NotifyCatalogInvalidated() => CatalogInvalidated?.Invoke(this, EventArgs.Empty);

    internal void BeginAuthorizedRealtimeSession() => Interlocked.Increment(ref privateStateGeneration);
    internal long AccessGeneration => Volatile.Read(ref privateStateGeneration);

    [ObservableProperty] private string connectionStatus = "Not loaded";
    [ObservableProperty] private string message = "Start the local backend independently, then Refresh.";
    [ObservableProperty] private string backendVersion = "Unavailable";
    [ObservableProperty] private string userId = "Unavailable";
    [ObservableProperty] private string workspaceIdentifier = "Unavailable";
    [ObservableProperty] private string workspaceName = "";
    [ObservableProperty] private string persistence = "Unavailable";
    [ObservableProperty] private string tradingMode = "Unavailable";
    [ObservableProperty] private string execution = "Unavailable";
    [ObservableProperty] private string exchanges = "Unavailable";
    [ObservableProperty] private string endpoint = "Unavailable";
    [ObservableProperty] private string workspaceFeedback = "";
    [ObservableProperty] private string validationMessage = "";
    [ObservableProperty] private DateTimeOffset? lastSuccessfulRefresh;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canEdit;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private bool hasSnapshot;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool canSave;
    [ObservableProperty] private BackendSnapshot? snapshot;
    [ObservableProperty] private string transportStatus = "Disconnected";
    [ObservableProperty] private string heartbeatStatus = "No heartbeat";
    [ObservableProperty] private string backendInstance = "Unavailable";
    [ObservableProperty] private string serverChangeNotice = "";

    public string WorkspaceHeader => HasSnapshot && ConnectionStatus is not ("AuthenticationFailed" or "AuthorizationDenied")
        ? WorkspaceName : "Workspace unavailable";
    public string VisibleUserId => ConnectionStatus is "AuthenticationFailed" or "AuthorizationDenied" ? "Unavailable" : UserId;
    public string VisibleWorkspaceIdentifier => ConnectionStatus is "AuthenticationFailed" or "AuthorizationDenied" ? "Unavailable" : WorkspaceIdentifier;
    public bool IsWorkspaceAccessDenied => ConnectionStatus is "AuthenticationFailed" or "AuthorizationDenied";
    public bool IsWorkspaceAuthorized => !IsWorkspaceAccessDenied;
    public string DataAge => !HasSnapshot ? "No backend snapshot" : IsStale ? "Last-known snapshot · stale" : "Latest completed refresh";
    public string LastRefreshLabel => LastSuccessfulRefresh?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Never";
    public string PaperExecutionLabel => Snapshot?.System.Capabilities.PaperExecutionImplemented == true ? "Available" : "Not implemented";
    public string LiveExecutionLabel => Snapshot?.System.Capabilities.LiveOrderSubmissionAvailable == true ? "Available" : "Unavailable";
    public string ManualExecutionLabel => Snapshot?.System.Capabilities.ManualLiveExecutionAvailable == true ? "Available" : "Unavailable";
    public string AutomaticExecutionLabel => Snapshot?.System.Capabilities.AutomaticLiveExecutionAvailable == true ? "Available" : "Unavailable";
    public string PolymarketLabel => Snapshot?.Exchanges.FirstOrDefault(e => e.Exchange == "Polymarket")?.IntegrationState ?? "Unavailable";
    public string KalshiLabel => Snapshot?.Exchanges.FirstOrDefault(e => e.Exchange == "Kalshi")?.IntegrationState ?? "Unavailable";

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lifetime.Dispose();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnostics?.Record("Information", "Desktop started; requesting local backend state.");
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (RefreshRequested is not null)
        {
            await RefreshRequested();
            CatalogRefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!BeginOperation()) return;
        var requestedGeneration = Volatile.Read(ref privateStateGeneration);
        diagnostics?.Record("Information", "Backend refresh requested.");
        try
        {
            var result = await backend.LoadAsync(lifetime.Token);
            if (requestedGeneration != Volatile.Read(ref privateStateGeneration)) return;
            ApplyBackendSnapshot(result);
            ConnectionStatus = ConnectionState.Connected.ToString();
            Message = "Backend state refreshed. Public market metadata is available in Market Explorer; execution is unavailable.";
            diagnostics?.Record("Information", "Backend refresh succeeded.");
            NotifyDerived();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (BackendFailure failure) { if (requestedGeneration == Volatile.Read(ref privateStateGeneration)) ReportFailure(failure); }
        catch (Exception exception) { if (requestedGeneration == Volatile.Read(ref privateStateGeneration)) ReportUnexpected(exception); }
        finally { EndOperation(); }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || workspaceId is not { } id || !BeginOperation()) return;
        var requestedGeneration = Volatile.Read(ref privateStateGeneration);
        var submittedName = WorkspaceName;
        var committed = false;
        diagnostics?.Record("Information", "Workspace display-name update requested.");
        try
        {
            var result = await backend.RenameAsync(id, submittedName, lifetime.Token);
            if (requestedGeneration != Volatile.Read(ref privateStateGeneration) || workspaceId != id || IsWorkspaceAccessDenied) return;
            savedWorkspaceName = result.DisplayName;
            if (WorkspaceName == submittedName) WorkspaceName = result.DisplayName;
            IsDirty = WorkspaceName != savedWorkspaceName;
            WorkspaceFeedback = "Workspace name saved and audited by the backend.";
            ServerChangeNotice = "";
            committed = true;
            Message = WorkspaceFeedback;
            ConnectionStatus = RefreshRequested is null ? ConnectionState.Connected.ToString() : "Synchronizing";
            IsStale = RefreshRequested is not null;
            diagnostics?.Record("Information", "Workspace display-name update succeeded.");
            NotifyDerived();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (BackendFailure failure)
        {
            if (requestedGeneration == Volatile.Read(ref privateStateGeneration))
            { WorkspaceFeedback = failure.Message; ReportFailure(failure); }
        }
        catch (Exception exception)
        {
            if (requestedGeneration == Volatile.Read(ref privateStateGeneration))
            { WorkspaceFeedback = "Workspace update failed; Refresh to inspect backend state."; ReportUnexpected(exception); }
        }
        finally { EndOperation(); }
        if (committed && RefreshRequested is not null) await RefreshRequested();
    }

    public void ApplyRealtimeSnapshot(ApplicationSnapshotResponse response, string endpoint)
    {
        if (response.Version != 1) throw new InvalidOperationException("Unsupported backend snapshot version.");
        if (HasSnapshot && (UserId != response.Session.UserId.ToString() || workspaceId != response.Workspace.WorkspaceId))
            ClearPrivateState();
        var backendChanged = BackendInstance != response.BackendInstanceId.ToString();
        ApplyBackendSnapshot(new(response.Session, response.System, response.Workspace, response.Exchanges, endpoint));
        BackendInstance = response.BackendInstanceId.ToString();
        TransportStatus = "Connected";
        if (backendChanged) HeartbeatStatus = "Awaiting heartbeat";
        ConnectionStatus = ConnectionState.Connected.ToString();
        CanEdit = true;
        Message = "Backend state synchronized. Market Explorer shows cached public metadata; execution is unavailable.";
        UpdateCanSave();
        CatalogRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetRealtimeStatus(string status, string description, bool transportConnected = false)
    {
        TransportStatus = transportConnected ? "Connected" : status;
        ConnectionStatus = status;
        Message = description;
        if (status != "Connected") { IsStale = HasSnapshot; CanEdit = false; }
        if (status is "AuthenticationFailed" or "AuthorizationDenied") ClearPrivateState(accessInvalid: true);
        UpdateCanSave();
    }

    public void SetHeartbeatStatus(string status) => HeartbeatStatus = status;

    private void ApplyBackendSnapshot(BackendSnapshot result)
    {
        var workspaceChanged = workspaceId != result.Workspace.WorkspaceId;
        workspaceId = result.Workspace.WorkspaceId;
        var serverChanged = savedWorkspaceName.Length > 0 && savedWorkspaceName != result.Workspace.DisplayName;
        if (workspaceChanged || !IsDirty)
        {
            savedWorkspaceName = result.Workspace.DisplayName;
            WorkspaceName = savedWorkspaceName;
            ServerChangeNotice = "";
        }
        else
        {
            savedWorkspaceName = result.Workspace.DisplayName;
            if (serverChanged) ServerChangeNotice = "The backend workspace name changed while your edit is unsaved. Review before saving.";
        }
        IsDirty = WorkspaceName != savedWorkspaceName;
        Snapshot = result;
        UserId = result.Session.UserId.ToString();
        WorkspaceIdentifier = result.Workspace.WorkspaceId.ToString();
        BackendVersion = result.System.BackendVersion;
        Persistence = result.System.PersistenceState;
        TradingMode = result.System.EffectiveTradingMode;
        Execution = result.System.Capabilities.PaperExecutionImplemented ? "Paper execution available" : "Execution is not implemented (including paper fills)";
        Exchanges = string.Join("  ·  ", result.Exchanges.Select(e => $"{e.Exchange}: {e.IntegrationState}"));
        Endpoint = result.Endpoint;
        LastSuccessfulRefresh = DateTimeOffset.UtcNow;
        HasSnapshot = true; IsStale = false;
        NotifyDerived();
    }

    private void ClearPrivateState(bool accessInvalid = false)
    {
        Interlocked.Increment(ref privateStateGeneration);
        if (accessInvalid) AccessInvalidated?.Invoke(this, EventArgs.Empty);
        workspaceId = null; savedWorkspaceName = "";
        Snapshot = null; HasSnapshot = false; IsStale = false;
        WorkspaceName = ""; UserId = "Unavailable"; WorkspaceIdentifier = "Unavailable";
        BackendVersion = "Unavailable"; Persistence = "Unavailable"; TradingMode = "Unavailable";
        Execution = "Unavailable"; Exchanges = "Unavailable"; Endpoint = "Unavailable";
        BackendInstance = "Unavailable"; HeartbeatStatus = "No heartbeat"; LastSuccessfulRefresh = null;
        WorkspaceFeedback = ""; ValidationMessage = ""; ServerChangeNotice = ""; CanEdit = false;
        diagnostics?.ClearBackend();
        NotifyDerived();
    }

    private bool BeginOperation()
    {
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0) return false;
        IsBusy = true; CanEdit = false;
        ConnectionStatus = ConnectionState.Loading.ToString();
        UpdateCanSave();
        return true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        CanEdit = ConnectionStatus == ConnectionState.Connected.ToString() && workspaceId.HasValue;
        Interlocked.Exchange(ref operationActive, 0);
        UpdateCanSave();
    }

    private void ReportFailure(BackendFailure failure)
    {
        ConnectionStatus = failure.State.ToString(); Message = failure.Message;
        if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
            ClearPrivateState(accessInvalid: true);
        else IsStale = HasSnapshot;
        diagnostics?.Record(failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied ? "Warning" : "Information",
            failure.State switch
            {
                ConnectionState.AuthenticationFailed => "Backend authentication failed.",
                ConnectionState.AuthorizationDenied => "Backend workspace authorization was denied.",
                ConnectionState.Disconnected => "Backend connection unavailable.",
                _ => "Backend operation unavailable."
            });
        NotifyDerived();
        logger.LogInformation("Backend operation ended with {ConnectionState}", failure.State);
    }

    private void ReportUnexpected(Exception exception)
    {
        ConnectionStatus = ConnectionState.Unavailable.ToString();
        Message = "Unexpected local client failure. Refresh to retry."; IsStale = HasSnapshot;
        diagnostics?.Record("Error", "Unexpected desktop operation failure.");
        NotifyDerived();
        logger.LogError("Client operation failed: {ErrorType}", exception.GetType().Name);
    }

    partial void OnWorkspaceNameChanged(string value)
    {
        IsDirty = value != savedWorkspaceName;
        ValidationMessage = string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100 || value.Any(char.IsControl)
            ? "Enter 1–100 characters without control characters." : "";
        OnPropertyChanged(nameof(WorkspaceHeader));
        UpdateCanSave();
    }
    partial void OnConnectionStatusChanged(string value)
    {
        OnPropertyChanged(nameof(WorkspaceHeader)); OnPropertyChanged(nameof(VisibleUserId));
        OnPropertyChanged(nameof(VisibleWorkspaceIdentifier)); OnPropertyChanged(nameof(IsWorkspaceAccessDenied));
        OnPropertyChanged(nameof(IsWorkspaceAuthorized)); UpdateCanSave();
    }
    partial void OnHasSnapshotChanged(bool value) { OnPropertyChanged(nameof(WorkspaceHeader)); OnPropertyChanged(nameof(DataAge)); }
    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(DataAge));
    partial void OnLastSuccessfulRefreshChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastRefreshLabel));
    partial void OnSnapshotChanged(BackendSnapshot? value) => NotifyDerived();
    partial void OnCanEditChanged(bool value) => UpdateCanSave();
    private void UpdateCanSave() => CanSave = CanEdit && IsDirty && !IsBusy && ValidationMessage.Length == 0;
    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(PaperExecutionLabel)); OnPropertyChanged(nameof(LiveExecutionLabel));
        OnPropertyChanged(nameof(ManualExecutionLabel)); OnPropertyChanged(nameof(AutomaticExecutionLabel));
        OnPropertyChanged(nameof(PolymarketLabel)); OnPropertyChanged(nameof(KalshiLabel));
        OnPropertyChanged(nameof(WorkspaceHeader)); OnPropertyChanged(nameof(DataAge));
        OnPropertyChanged(nameof(VisibleUserId)); OnPropertyChanged(nameof(VisibleWorkspaceIdentifier));
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
