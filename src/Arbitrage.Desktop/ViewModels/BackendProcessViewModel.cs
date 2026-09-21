using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class BackendProcessViewModel(ILocalBackendController controller, RealtimeSession realtime) : ObservableObject
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private LocalBackendObservation current = new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
        "Local backend has not been observed yet.");
    public Func<bool>? ConfirmStop { get; set; }

    [ObservableProperty] private string processStatus = "Unknown";
    [ObservableProperty] private string managementStatus = "Unverified";
    [ObservableProperty] private string explanation = "Local backend has not been observed yet.";
    [ObservableProperty] private bool isBusy;

    public bool CanStart => !IsBusy && current.ProcessState == LocalProcessState.NotRunning &&
        !controller.ArtifactExplanation.StartsWith("Start requires", StringComparison.Ordinal);
    public bool CanStop => !IsBusy && current.ProcessState == LocalProcessState.Running &&
        current.Capability == LocalManagementCapability.ManagedLocal && current.Snapshot is not null;
    public string StartExplanation => CanStart ? "Start the configured local backend process." :
        current.ProcessState == LocalProcessState.NotRunning ? controller.ArtifactExplanation :
        "Start is unavailable while a backend is running or its identity is uncertain.";
    public string StopExplanation => CanStop ? "Stop this verified managed backend for every connected desktop." :
        current.ProcessState == LocalProcessState.Running ? "Stop requires verified managed-local ownership." :
        "Stop is available only while a verified managed backend is running.";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ObserveAsync(cancellationToken);
    }

    public async Task RefreshAsync()
    {
        await ObserveAsync(CancellationToken.None);
        await realtime.RefreshAsync();
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        if (!await operations.WaitAsync(0, cancellationToken)) return;
        try { IsBusy = true; Apply(await controller.ObserveAsync(cancellationToken)); }
        finally { IsBusy = false; operations.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart || !await operations.WaitAsync(0)) return;
        try
        {
            IsBusy = true; ProcessStatus = "Starting";
            var result = await controller.StartAsync(CancellationToken.None);
            Apply(result);
            if (result.ProcessState == LocalProcessState.Running) realtime.ResumeAfterStart();
        }
        finally { IsBusy = false; operations.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (!CanStop || ConfirmStop?.Invoke() != true || !await operations.WaitAsync(0)) return;
        try
        {
            IsBusy = true; ProcessStatus = "Stopping";
            var stop = controller.StopAsync(current, realtime.SuspendAfterStopRequest, CancellationToken.None);
            // The controller only returns after a confirmed exit or a truthful uncertain outcome.
            var result = await stop;
            Apply(result);
        }
        finally { IsBusy = false; operations.Release(); }
    }

    private void Apply(LocalBackendObservation result)
    {
        current = result;
        ProcessStatus = result.ProcessState.ToString();
        ManagementStatus = result.Capability.ToString();
        Explanation = result.Explanation;
        NotifyAvailability();
    }

    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StartExplanation)); OnPropertyChanged(nameof(StopExplanation));
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }
}
