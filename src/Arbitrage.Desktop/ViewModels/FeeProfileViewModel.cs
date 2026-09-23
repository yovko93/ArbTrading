using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class FeeProfileViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private readonly CancellationTokenSource lifetime = new();
    private long version;
    private bool disposed;
    public event EventHandler? Changed;
    public string[] Profiles { get; } = ["Unknown", "DirectMember", "NonDirectMember"];
    [ObservableProperty] private string profile = "Unknown";
    [ObservableProperty] private string notice = "Load the saved diagnostic assumption or explicitly save a choice. Default: Unknown.";
    public FeeProfileViewModel(MainViewModel state, BackendClient backend)
    { this.state = state; this.backend = backend; state.AccessInvalidated += Invalidated; }
    private void Invalidated(object? sender, EventArgs e) { version++; Profile = "Unknown"; Notice = "Workspace access changed; load the current assumption explicitly."; }
    [RelayCommand] private Task LoadAsync() => RequestAsync(false);
    [RelayCommand] private Task SaveAsync() => RequestAsync(true);
    private async Task RequestAsync(bool save)
    {
        if (disposed || !state.IsWorkspaceAuthorized || !Guid.TryParse(state.WorkspaceIdentifier, out var workspace)) return;
        var access = state.AccessGeneration; var request = ++version; var instance = state.BackendInstance;
        try
        {
            var result = await backend.FeeProfileAsync(workspace, save ? Profile : null, lifetime.Token);
            if (request != version || access != state.AccessGeneration || instance != state.BackendInstance) return;
            Profile = result.Profile; Notice = save ? "Diagnostic assumption saved. Fee results invalidated; gross results retained. No exchange request was made." : "Saved diagnostic assumption loaded.";
            if (save) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure) { if (request == version && access == state.AccessGeneration) Notice = "Fee profile unavailable; no assumption was confirmed."; }
    }
    public void Dispose() { if (disposed) return; disposed = true; version++; lifetime.Cancel(); lifetime.Dispose(); state.AccessInvalidated -= Invalidated; }
}
