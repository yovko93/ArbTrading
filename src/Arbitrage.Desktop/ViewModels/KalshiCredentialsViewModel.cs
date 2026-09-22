using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class KalshiCredentialsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private long generation;
    private bool busy, disposed;
    [ObservableProperty] private string keyId = "";
    [ObservableProperty] private string selectedFile = "";
    [ObservableProperty] private string notice = "Optional. Kalshi REST market data works without credentials.";
    [ObservableProperty] private CredentialStatusResponse? status;
    public Func<string?>? SelectFile { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public KalshiCredentialsViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += Invalidated; state.PropertyChanged += StateChanged;
    }
    [RelayCommand] private void SelectPrivateKeyFile() { var file = SelectFile?.Invoke(); if (file is not null) SelectedFile = file; }
    [RelayCommand] private Task RefreshStatusAsync() => RunAsync("status");
    [RelayCommand] private Task ImportAsync() => RunAsync("import");
    [RelayCommand] private Task RemoveAsync() => RunAsync("remove");
    private async Task RunAsync(string operation)
    {
        if (busy || disposed || state.ConnectionStatus != "Connected") return;
        var version = generation; var access = state.AccessGeneration; var instance = state.BackendInstance;
        var workspace = state.WorkspaceIdentifier;
        bool Current() => !disposed && version == generation && access == state.AccessGeneration &&
            instance == state.BackendInstance && workspace == state.WorkspaceIdentifier && state.ConnectionStatus == "Connected";
        busy = true;
        try
        {
            // Read current safe metadata before deciding whether replacement confirmation is needed.
            var current = await backend.CredentialStatusAsync(CancellationToken.None);
            if (!Current()) return;
            Status = current;
            if (operation == "import")
            {
                if (current.Configured && Confirm?.Invoke("Replace Kalshi credentials? Active Kalshi streams will stop.") != true) return;
                if (!Current()) return;
                current = await backend.ImportCredentialAsync(new(KeyId, SelectedFile, current.Configured, current.Version), CancellationToken.None);
            }
            else if (operation == "remove")
            {
                if (Confirm?.Invoke("Remove Kalshi credentials and stop active Kalshi streams?") != true || !Current()) return;
                current = await backend.RemoveCredentialAsync(new(true, current.Version), CancellationToken.None);
            }
            if (!Current()) return;
            Status = current; Notice = current.Configured ? "Configured for market-data WebSocket authentication. Start realtime explicitly." : "NotConfigured — Kalshi realtime requires an API key.";
            if (operation != "status") { KeyId = ""; SelectedFile = ""; }
        }
        catch (BackendFailure e)
        {
            if (!Current()) return;
            if (e.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(e.State.ToString(), e.Message);
            else Notice = "Credential operation failed. Verify the key format and private file permissions, then retry explicitly.";
        }
        finally { busy = false; }
    }
    private void Invalidated(object? sender, EventArgs e) => Clear();
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.BackendInstance) or nameof(MainViewModel.WorkspaceIdentifier) || e.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus != "Connected") Clear(); }
    private void Clear() { generation++; Status = null; KeyId = ""; SelectedFile = ""; Notice = "Refresh credential status after authentication."; }
    public void Dispose() { disposed = true; Clear(); state.AccessInvalidated -= Invalidated; state.PropertyChanged -= StateChanged; }
}
