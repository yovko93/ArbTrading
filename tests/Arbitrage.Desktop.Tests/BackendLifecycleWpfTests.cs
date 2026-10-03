using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class BackendLifecycleWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Real_buttons_launch_synchronize_refresh_and_stop_real_backend(bool packageMode, bool delayedReadiness)
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-04H5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = packageMode ? RealtimeProcessTests.CreatePackageFixture(root) : null;
        var artifact = RealtimeProcessTests.BackendArtifact();
        if (!packageMode)
        {
            var repository = new DirectoryInfo(Path.GetDirectoryName(artifact)!).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            artifact = BackendArtifactLocator.Resolve(Path.Combine(repository, "src", "Arbitrage.Desktop", "bin", "Release", "net10.0-windows"), false, null);
        }
        var options = new LocalBackendLaunchOptions(Path.Combine(root, "data"), Path.Combine(root, "runtime"),
            $"http://127.0.0.1:{RealtimeProcessTests.FreePort()}", package is null ? artifact : Path.Combine(package, "backend", "Arbitrage.Backend.exe"), null) { PackageRoot = package };
        var transport = new DelayedReadinessHandler { InnerHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false } };
        using var http = new HttpClient(transport);
        var client = new BackendClient(http, new ProtectedLocalConnectionFile(options.RuntimeDirectory));
        var controller = new LocalBackendController(client, options);
        Harness? ui = null;
        Process? owned = null;
        var confirmations = 0;
        try
        {
            // Exercise development controls against a real, empty migrated workspace. The package
            // variant below retains a fresh database and proves migration through the Start button.
            if (!packageMode) await RealtimeProcessTests.PrepareDevelopmentDatabase(options);
            await fixture.RunAsync(() => ui = new Harness(client, controller));
            Task? initialize = null;
            await fixture.RunAsync(() => { ui!.Realtime.Start(); initialize = ui.Process.InitializeAsync(default); });
            await initialize!;
            await fixture.RunAsync(() =>
            {
                Assert.Equal("NotRunning", ui!.Process.ProcessStatus);
                ui.AssertButtons(true, false, true);
                ui.Click("Refresh backend state");
            });
            await Wait(() => !ui!.Process.IsBusy && !ui.State.RefreshCommand.IsRunning);
            Assert.Equal(LocalProcessState.NotRunning, (await controller.ObserveAsync(default)).ProcessState);
            Task? startAction = null;
            await fixture.RunAsync(() =>
            {
                if (delayedReadiness) transport.NotBefore = DateTimeOffset.UtcNow.AddSeconds(25);
                ui!.Click("Start local backend"); ui.Click("Start local backend");
                startAction = ui.Process.StartCommand.ExecutionTask;
            });
            Assert.NotNull(startAction);
            // Await the command invoked by the real button, including the existing two-minute
            // migration and bounded two-minute readiness budgets. Synchronization has its own wait.
            await startAction.WaitAsync(TimeSpan.FromMinutes(5));
            await fixture.RunAsync(() => Assert.True(ui!.Process.ProcessStatus == "Running", ui.Process.Explanation));
            if (delayedReadiness) Assert.True(DateTimeOffset.UtcNow >= transport.NotBefore);
            await Wait(() => ui!.State.ConnectionStatus == "Connected");
            var running = await controller.ObserveAsync(default);
            Assert.Equal(LocalManagementCapability.ManagedLocal, running.Capability);
            owned = Process.GetProcessById(running.ProcessId!.Value);
            _ = owned.SafeHandle; // Retain the kernel process identity before subsequent awaits.
            var metadataPath = Path.Combine(options.RuntimeDirectory, "managed-local.json");
            Assert.True(File.Exists(metadataPath));
            ProtectedStorage.VerifyPrivateFile(metadataPath);
            var metadataBeforeRefresh = await File.ReadAllTextAsync(metadataPath);
            await fixture.RunAsync(() =>
            {
                ui!.AssertButtons(false, true, true);
                Assert.NotEqual("Workspace unavailable", ui.State.WorkspaceHeader);
                Assert.Equal("Paper: Available", ui.State.PaperStatusLabel);
                Assert.Equal("Live: Unavailable", ui.State.LiveStatusLabel);
                Assert.False(ui.State.Snapshot!.System.Capabilities.ManualLiveExecutionAvailable);
                Assert.False(ui.State.Snapshot.System.Capabilities.AutomaticLiveExecutionAvailable);
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
                    ui.Capture(theme, "Running");
                }
                ui.Click("Refresh backend state"); ui.Click("Refresh backend state");
            });
            await Wait(() => !ui!.Process.IsBusy && !ui.State.RefreshCommand.IsRunning);
            var refreshed = await controller.ObserveAsync(default);
            Assert.Equal(running.ProcessId, refreshed.ProcessId);
            Assert.Equal(running.Snapshot!.BackendInstanceId, refreshed.Snapshot!.BackendInstanceId);
            Assert.Equal(LocalManagementCapability.ManagedLocal, refreshed.Capability);
            Assert.Equal(metadataBeforeRefresh, await File.ReadAllTextAsync(metadataPath));
            if (packageMode) Assert.Contains("DatabaseCreated", controller.DistributionSummary);
            // Recreate all lifecycle/realtime view models and the controller, as on Desktop restart.
            // Reattachment must come from protected metadata, never a retained ViewModel observation.
            await ui!.Realtime.StopAsync();
            await fixture.RunAsync(ui.Dispose);
            Assert.False(owned.HasExited);
            Assert.True(File.Exists(metadataPath));
            var reopened = new LocalBackendController(client, options);
            await fixture.RunAsync(() =>
            {
                ui = new Harness(client, reopened);
                ui.Realtime.Start(); initialize = ui.Process.InitializeAsync(default);
            });
            await initialize!;
            var reattached = await reopened.ObserveAsync(default);
            Assert.Equal(LocalManagementCapability.ManagedLocal, reattached.Capability);
            Assert.Equal(running.ProcessId, reattached.ProcessId);
            Assert.Equal(running.Snapshot.BackendInstanceId, reattached.Snapshot!.BackendInstanceId);
            Assert.Equal(reattached.ProcessId, (await controller.ObserveAsync(default)).ProcessId);
            await fixture.RunAsync(() =>
            {
                ui!.AssertButtons(false, true, true);
                ui!.Process.ConfirmStop = () => { confirmations++; return true; };
                ui.Click("Stop local backend"); ui.Click("Stop local backend");
            });
            await Wait(() => ui!.Process.ProcessStatus == "NotRunning" && !ui.Process.IsBusy);
            Assert.True(owned.HasExited);
            Assert.False(File.Exists(metadataPath));
            Assert.Equal(1, confirmations);
            await fixture.RunAsync(() =>
            {
                ui!.AssertButtons(true, false, true);
                Assert.Contains(ui.Diagnostics.Events, e => e.Description == "StopSucceeded");
                ui.Click("Refresh backend state");
            });
            await Wait(() => !ui!.Process.IsBusy && !ui.State.RefreshCommand.IsRunning);
            Assert.Equal(LocalProcessState.NotRunning, (await controller.ObserveAsync(default)).ProcessState);
        }
        finally
        {
            if (ui is not null) { await ui.Realtime.StopAsync(); await fixture.RunAsync(ui.Dispose); }
            var remaining = await controller.ObserveAsync(default);
            if (remaining.Capability == LocalManagementCapability.ManagedLocal)
                await controller.StopAsync(remaining, _ => { }, default);
            // Retained process handle belongs exclusively to this test, never a PID/name/port search.
            if (owned is not null) { if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync(); } owned.Dispose(); }
            // Preserve evidence if readiness never established ownership (do not mask the test failure).
            if (remaining.ProcessState == LocalProcessState.NotRunning || owned is not null)
            {
                try
                {
                    var leasePath = Path.Combine(options.RuntimeDirectory, "backend.lock");
                    if (File.Exists(leasePath)) new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
                    Directory.Delete(root, true);
                }
                catch (IOException) { Debug.WriteLine("Preserved locked isolated WPF fixture after lifecycle test."); }
            }
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Observation_gate_rapid_clicks_and_failures_are_visible_on_real_buttons(string themeName)
    {
        var controller = new HeldController();
        Harness? ui = null;
        Task? observe = null;
        using var http = new HttpClient();
        try
        {
            await fixture.RunAsync(() =>
            {
                new WpfThemePaletteApplier(Application.Current.Resources).Apply(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
                ui = new Harness(new BackendClient(http, new MissingConnection()), controller);
                new WpfThemePaletteApplier(Application.Current.Resources).Apply(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
                observe = ui.Process.InitializeAsync(default);
                ui.AssertButtons(false, false, false);
                ui.Click("Start local backend");
                Assert.Equal(0, controller.Starts);
            });
            controller.Observation.SetResult(new(LocalProcessState.NotRunning, LocalManagementCapability.ExternalUnmanaged, "Ready to start local backend."));
            await observe!;
            await fixture.RunAsync(() => { ui!.AssertButtons(true, false, true); ui.Capture(themeName, "NotRunning"); ui.Click("Start local backend"); ui.Click("Start local backend"); });
            await Wait(() => controller.Starts == 1);
            await fixture.RunAsync(() => { Assert.Equal("Starting", ui!.Process.ProcessStatus); ui.AssertButtons(false, false, false); ui.Capture(themeName, "Starting"); });
            controller.Start.SetResult(new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged, "Backend exited before authenticated readiness. Check Logs & Diagnostics."));
            await Wait(() => !ui!.Process.IsBusy);
            await fixture.RunAsync(() =>
            {
                Assert.Equal("Faulted", ui!.Process.ProcessStatus);
                ui.AssertButtons(false, false, true);
                Assert.Contains(Descendants<TextBlock>(ui.Window), t => t.Text == ui.Process.Explanation && t.IsVisible);
                ui.Capture(themeName, "Faulted");
                controller.Throw = true;
                ui.Click("Refresh backend state");
            });
            await Wait(() => !ui!.State.RefreshCommand.IsRunning && !ui.Process.IsBusy);
            await fixture.RunAsync(() =>
            {
                Assert.Equal("Unknown", ui!.Process.ProcessStatus);
                Assert.DoesNotContain("secret", ui.Process.Explanation);
                Assert.Contains(ui.Diagnostics.Events, e => e.Description == "RefreshFailed (InvalidOperationException)");
                Assert.DoesNotContain(ui.Diagnostics.Events, e => e.Description.Contains("secret"));
            });
        }
        finally { if (ui is not null) await fixture.RunAsync(ui.Dispose); }
    }

    private async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < until)
        {
            var ready = false; await fixture.RunAsync(() => ready = condition());
            if (ready) return;
            await Task.Delay(50);
        }
        Assert.Fail("WPF lifecycle condition did not complete within 35 seconds.");
    }
    private sealed class HeldController : ILocalBackendController
    {
        public string ArtifactExplanation => "Configured backend available.";
        public TaskCompletionSource<LocalBackendObservation> Observation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LocalBackendObservation> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts; public bool Throw;
        public Task<LocalBackendObservation> ObserveAsync(CancellationToken ct) => Throw ? throw new InvalidOperationException("secret credential") : Observation.Task;
        public Task<LocalBackendObservation> StartAsync(CancellationToken ct) { Starts++; return Start.Task; }
        public Task<LocalBackendObservation> StopAsync(LocalBackendObservation current, Action<Guid> accepted, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class DelayedReadinessHandler : DelegatingHandler
    {
        public DateTimeOffset NotBefore { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            DateTimeOffset.UtcNow < NotBefore &&
            (request.RequestUri!.AbsolutePath.EndsWith("/session", StringComparison.Ordinal) ||
             request.RequestUri.AbsolutePath.EndsWith("/snapshot", StringComparison.Ordinal))
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, cancellationToken);
    }
    private sealed class Harness : IDisposable
    {
        public DesktopDiagnostics Diagnostics { get; } = new();
        public MainViewModel State { get; }
        public BackendProcessViewModel Process { get; }
        public RealtimeSession Realtime { get; }
        public MainWindow Window { get; private set; } = null!;
        private readonly ThemeService theme;
        private readonly ThemeSelectionViewModel selection;
        private readonly ShellViewModel shell;
        public Harness(BackendClient client, ILocalBackendController controller)
        {
            var dispatcher = new WpfUiDispatcher(Application.Current.Dispatcher);
            State = new(client, NullLogger<MainViewModel>.Instance, Diagnostics);
            Realtime = new(client, State, Diagnostics, dispatcher, new RealtimeDelay());
            Process = new(controller, Realtime, Diagnostics);
            State.RefreshRequested = Process.RefreshAsync;
            theme = new(new Preferences(), new SystemTheme(), new WpfThemePaletteApplier(Application.Current.Resources), dispatcher);
            selection = new(theme, Diagnostics);
            shell = new(State, selection, Diagnostics, Process);
            OpenWindow();
        }
        public void OpenWindow()
        {
            Window = new(shell) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            Window.Show(); Window.UpdateLayout();
        }
        private Button Button(string name) => Descendants<Button>(Window).Single(b => AutomationProperties.GetName(b) == name);
        public void Click(string name)
        {
            var button = Button(name);
            Assert.NotNull(button.Command); // A broken binding must fail, including disabled scenarios.
            if (button.IsEnabled) ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        public void AssertButtons(bool start, bool stop, bool refresh)
        {
            Window.UpdateLayout();
            Assert.Equal(start, Button("Start local backend").IsEnabled);
            Assert.Equal(stop, Button("Stop local backend").IsEnabled);
            Assert.Equal(refresh, Button("Refresh backend state").IsEnabled);
            Assert.Equal(Process.CanStart, Process.StartCommand.CanExecute(null));
            Assert.Equal(Process.CanStop, Process.StopCommand.CanExecute(null));
        }
        public void Capture(string themeName, string state)
        {
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
            Directory.CreateDirectory(directory);
            Window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(Window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"lifecycle-{themeName}-{state}.png")); encoder.Save(stream);
        }
        public void Dispose() { Window.Close(); shell.Dispose(); selection.Dispose(); theme.Dispose(); Realtime.Dispose(); State.Dispose(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class MissingConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Preferences : IDesktopPreferencesStore
    {
        public Task<PreferencesLoadResult> LoadAsync(CancellationToken ct) => Task.FromResult(new PreferencesLoadResult(ThemePreference.System));
        public Task SaveAsync(ThemePreference value, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class SystemTheme : ISystemThemeProvider
    {
        public bool? ApplicationsUseLightTheme => true;
        public bool HighContrast => false;
        public event EventHandler? Changed { add { } remove { } }
    }
}
