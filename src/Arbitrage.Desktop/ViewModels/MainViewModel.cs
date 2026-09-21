using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arbitrage.Desktop.ViewModels;

public partial class MainViewModel(BackendClient backend, ILogger<MainViewModel> logger) : ObservableObject, IDisposable
{
    private CancellationTokenSource lifetime = new();
    private Guid? workspaceId;

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
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canEdit;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lifetime.Dispose();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        await RunAsync(async () =>
        {
            var snapshot = await backend.LoadAsync(lifetime.Token);
            workspaceId = snapshot.Workspace.WorkspaceId;
            UserId = snapshot.Session.UserId.ToString();
            WorkspaceIdentifier = snapshot.Workspace.WorkspaceId.ToString();
            WorkspaceName = snapshot.Workspace.DisplayName;
            BackendVersion = snapshot.System.BackendVersion;
            Persistence = snapshot.System.PersistenceState;
            TradingMode = snapshot.System.EffectiveTradingMode;
            Execution = snapshot.System.Capabilities.PaperExecutionImplemented ? "Paper execution available" : "Execution is not implemented (including paper fills)";
            Exchanges = string.Join("  •  ", snapshot.Exchanges.Select(e => $"{e.Exchange}: {e.IntegrationState}"));
            Message = "Backend values loaded. No balances or trading data are available in this phase.";
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || !CanEdit || workspaceId is not { } id) return;
        await RunAsync(async () =>
        {
            var result = await backend.RenameAsync(id, WorkspaceName, lifetime.Token);
            WorkspaceName = result.DisplayName;
            Message = "Workspace name saved and audited by the backend.";
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true; CanEdit = false; ConnectionStatus = ConnectionState.Loading.ToString();
        try { await operation(); ConnectionStatus = ConnectionState.Connected.ToString(); CanEdit = true; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (BackendFailure failure)
        {
            ConnectionStatus = failure.State.ToString(); Message = failure.Message;
            ClearValues();
            logger.LogInformation("Backend operation ended with {ConnectionState}", failure.State);
        }
        catch (Exception exception)
        {
            ConnectionStatus = ConnectionState.Unavailable.ToString(); Message = "Unexpected local client failure. Refresh to retry.";
            ClearValues(); logger.LogError("Client operation failed: {ErrorType}", exception.GetType().Name);
        }
        finally { IsBusy = false; }
    }

    private void ClearValues()
    {
        workspaceId = null;
        UserId = WorkspaceIdentifier = BackendVersion = Persistence = TradingMode = Execution = Exchanges = "Unavailable";
        WorkspaceName = "";
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
