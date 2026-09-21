using Arbitrage.Desktop.Services;
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
        if (!BeginOperation()) return;
        diagnostics?.Record("Information", "Backend refresh requested.");
        try
        {
            var result = await backend.LoadAsync(lifetime.Token);
            var workspaceChanged = workspaceId != result.Workspace.WorkspaceId;
            workspaceId = result.Workspace.WorkspaceId;
            if (workspaceChanged || !IsDirty)
            {
                savedWorkspaceName = result.Workspace.DisplayName;
                WorkspaceName = savedWorkspaceName;
            }
            else savedWorkspaceName = result.Workspace.DisplayName;
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
            ConnectionStatus = ConnectionState.Connected.ToString();
            Message = "Backend state refreshed. Trading data remains unavailable in this phase.";
            diagnostics?.Record("Information", "Backend refresh succeeded.");
            NotifyDerived();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (BackendFailure failure) { ReportFailure(failure); }
        catch (Exception exception) { ReportUnexpected(exception); }
        finally { EndOperation(); }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || workspaceId is not { } id || !BeginOperation()) return;
        var submittedName = WorkspaceName;
        diagnostics?.Record("Information", "Workspace display-name update requested.");
        try
        {
            var result = await backend.RenameAsync(id, submittedName, lifetime.Token);
            savedWorkspaceName = result.DisplayName;
            if (WorkspaceName == submittedName) WorkspaceName = result.DisplayName;
            IsDirty = WorkspaceName != savedWorkspaceName;
            WorkspaceFeedback = "Workspace name saved and audited by the backend.";
            Message = WorkspaceFeedback;
            ConnectionStatus = ConnectionState.Connected.ToString();
            IsStale = true;
            diagnostics?.Record("Information", "Workspace display-name update succeeded.");
            NotifyDerived();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (BackendFailure failure) { WorkspaceFeedback = failure.Message; ReportFailure(failure); }
        catch (Exception exception) { WorkspaceFeedback = "Workspace update failed; Refresh to inspect backend state."; ReportUnexpected(exception); }
        finally { EndOperation(); }
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
        ConnectionStatus = failure.State.ToString(); Message = failure.Message; IsStale = HasSnapshot;
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
