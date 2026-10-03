using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class BackendProcessViewModel(ILocalBackendController controller, RealtimeSession realtime,
    DesktopDiagnostics? diagnostics = null) : ObservableObject
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private LocalBackendObservation current = new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
        "Local backend has not been observed yet.");
    public Func<bool>? ConfirmStop { get; set; }

    [ObservableProperty] private string processStatus = "Unknown";
    [ObservableProperty] private string managementStatus = "Unverified";
    [ObservableProperty] private string explanation = "Local backend has not been observed yet.";
    [ObservableProperty] private bool isBusy;
    public string DistributionSummary => controller.DistributionSummary;
    public bool CanRefresh => !IsBusy;
    public bool CanStart => !IsBusy && current.ProcessState == LocalProcessState.NotRunning &&
        !controller.ArtifactExplanation.StartsWith("Start requires", StringComparison.Ordinal);
    public bool CanStop => !IsBusy && current.ProcessState == LocalProcessState.Running &&
        current.Capability == LocalManagementCapability.ManagedLocal && current.Snapshot is not null;
    public string StartExplanation => IsBusy ? "Wait for the current backend action to finish." :
        CanStart ? "Start the configured local backend process." :
        current.ProcessState == LocalProcessState.NotRunning ? controller.ArtifactExplanation :
        "Start is unavailable while a backend is running or its identity is uncertain. Refresh to observe again.";
    public string StopExplanation => IsBusy ? "Wait for the current backend action to finish." :
        CanStop ? "Stop this verified managed backend for every connected desktop." :
        "Stop requires a running backend with verified managed-local ownership.";

    public Task InitializeAsync(CancellationToken cancellationToken) => ObserveAsync(cancellationToken);
    public Task RefreshAsync() => ObserveAsync(CancellationToken.None);

    private Task ObserveAsync(CancellationToken cancellationToken) => RunAsync("Refresh", async () =>
    {
        Explanation = "Refreshing backend state...";
        Apply(await controller.ObserveAsync(cancellationToken));
        await realtime.RefreshAsync();
        Record(current.ProcessState == LocalProcessState.Running ? "RefreshSucceeded" : "RefreshUnavailable");
    }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        if (!CanStart) { Explanation = StartExplanation; return Task.CompletedTask; }
        return RunAsync("Start", async () =>
        {
            ProcessStatus = "Starting";
            Explanation = "Starting local backend; waiting for authenticated readiness...";
            var result = await controller.StartAsync(CancellationToken.None);
            Apply(result);
            if (result.ProcessState == LocalProcessState.Running && result.Snapshot is not null)
            { realtime.ResumeAfterStart(); Record("StartSucceeded"); }
            else Record("StartFailed", "Warning");
        });
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync()
    {
        if (!CanStop) { Explanation = StopExplanation; return Task.CompletedTask; }
        return RunAsync("Stop", async () =>
        {
            // Acquire the operation before opening a modal confirmation (which pumps the dispatcher).
            if (ConfirmStop?.Invoke() != true) { Explanation = "Stop cancelled. Backend remains running."; Record("StopCancelled"); return; }
            ProcessStatus = "Stopping";
            Explanation = "Stopping verified local backend; waiting for confirmed exit...";
            var result = await controller.StopAsync(current, realtime.SuspendAfterStopRequest, CancellationToken.None);
            Apply(result);
            Record(result.ProcessState == LocalProcessState.NotRunning ? "StopSucceeded" : "StopUncertain",
                result.ProcessState == LocalProcessState.NotRunning ? "Information" : "Warning");
        });
    }

    private async Task RunAsync(string action, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        if (!await operations.WaitAsync(0, cancellationToken))
        { Explanation = "A backend action is already in progress. Wait for it to finish."; return; }
        try
        {
            IsBusy = true;
            Record(action + "Requested");
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Apply(new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                action + " cancelled. Refresh to verify backend state."));
            Record(action + "Cancelled", "Warning");
        }
        catch (Exception exception)
        {
            Apply(new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                action + " failed unexpectedly. Refresh to verify backend state; see Logs & Diagnostics."));
            // Exception messages may contain credentials, configuration or private paths.
            Record(action + "Failed (" + exception.GetType().Name + ")", "Error");
        }
        finally
        {
            // Availability notifications may synchronously re-enter commands. Release first.
            operations.Release();
            IsBusy = false;
        }
    }

    private void Record(string code, string severity = "Information") => diagnostics?.Record(severity, code);
    private void Apply(LocalBackendObservation result)
    {
        current = result;
        ProcessStatus = result.ProcessState.ToString();
        ManagementStatus = result.Capability.ToString();
        Explanation = result.ProcessState == LocalProcessState.NotRunning &&
            controller.ArtifactExplanation.StartsWith("Start requires", StringComparison.Ordinal)
            ? controller.ArtifactExplanation : result.Explanation;
        NotifyAvailability();
    }

    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(DistributionSummary));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StartExplanation)); OnPropertyChanged(nameof(StopExplanation));
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }
}
