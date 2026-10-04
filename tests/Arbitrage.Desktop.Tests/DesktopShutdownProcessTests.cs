using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Arbitrage.Desktop.Services;
using Arbitrage.LocalTransport;

namespace Arbitrage.Desktop.Tests;

[CollectionDefinition("Desktop shutdown", DisableParallelization = true)]
public sealed class DesktopShutdownCollection { }

[Collection("Desktop shutdown")]
public sealed class DesktopShutdownProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_desktop_close_without_backend_exits_cleanly(bool duplicateClose)
    {
        var root = CreateRoot();
        var options = Options(root);
        using var desktop = DesktopProcess.Start(root, options);
        var window = await desktop.WaitForShell();
        await desktop.Close(window, duplicateClose);
        Assert.False(File.Exists(Path.Combine(options.RuntimeDirectory, "connection.json")));
        Assert.False(File.Exists(Path.Combine(options.DataDirectory, "arbitrage.db")));
        var logs = Path.Combine(root, "desktop", "logs");
        Assert.DoesNotContain((Directory.Exists(logs) ? Directory.GetFiles(logs, "*.log") : [])
            .SelectMany(File.ReadAllLines), line => line.Contains("StopRequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_desktop_close_preserves_backend_and_reopen_reattaches_managed_ownership()
    {
        var root = CreateRoot();
        var options = Options(root);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var client = new BackendClient(http, new ProtectedLocalConnectionFile(options.RuntimeDirectory));
        var controller = new LocalBackendController(client, options);
        Process? backend = null;
        try
        {
            await RealtimeProcessTests.PrepareDevelopmentDatabase(options);
            var started = await controller.StartAsync(default);
            Assert.True(started.ProcessState == LocalProcessState.Running, started.Explanation);
            Assert.Equal(LocalManagementCapability.ManagedLocal, started.Capability);
            backend = Process.GetProcessById(started.ProcessId!.Value);
            var instance = started.Snapshot!.BackendInstanceId;
            var workspace = started.Snapshot.Workspace.WorkspaceId;
            var metadata = await File.ReadAllBytesAsync(Path.Combine(options.RuntimeDirectory, "managed-local.json"));
            var connection = await File.ReadAllBytesAsync(Path.Combine(options.RuntimeDirectory, "connection.json"));
            var account = await client.PaperAccountAsync(workspace, default);
            var automation = await client.PaperAutomationStatusAsync(workspace, default);
            var monitoring = await client.MonitoringStatusAsync(workspace, null, default);
            var reliability = await client.PaperReliabilityCurrentAsync(workspace, default);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var desktop = DesktopProcess.Start(root, options);
                var window = await desktop.WaitForShell();
                await desktop.WaitForManagedLocal(window);
                await desktop.Close(window, duplicate: true);
                // Prove survival before this test's explicit backend cleanup.
                Assert.False(backend.HasExited);
                var observed = await new LocalBackendController(client, options).ObserveAsync(default);
                Assert.Equal(LocalProcessState.Running, observed.ProcessState);
                Assert.Equal(LocalManagementCapability.ManagedLocal, observed.Capability);
                Assert.Equal(instance, observed.Snapshot!.BackendInstanceId);
                Assert.Equal(started.ProcessId, observed.ProcessId);
                var metadataAfterClose = await File.ReadAllBytesAsync(Path.Combine(options.RuntimeDirectory, "managed-local.json"));
                var connectionAfterClose = await File.ReadAllBytesAsync(Path.Combine(options.RuntimeDirectory, "connection.json"));
                Assert.True(metadata.SequenceEqual(metadataAfterClose),
                    "Managed-local metadata changed during Desktop close.");
                Assert.True(connection.SequenceEqual(connectionAfterClose),
                    "Protected connection changed during Desktop close.");
                var history = await client.RecentDiagnosticsAsync(workspace, 0, default);
                Assert.DoesNotContain(history.Events, entry => entry.Code == "StopRequested");
                Assert.Equal(account.Generation, (await client.PaperAccountAsync(workspace, default)).Generation);
                Assert.Equal(automation.State, (await client.PaperAutomationStatusAsync(workspace, default)).State);
                Assert.Equal(monitoring.State, (await client.MonitoringStatusAsync(workspace, null, default)).State);
                Assert.Equal(reliability.Campaign, (await client.PaperReliabilityCurrentAsync(workspace, default)).Campaign);
            }
        }
        finally
        {
            // Only the exact process retained from this isolated successful Start belongs to this test.
            if (backend is not null)
            {
                try
                {
                    if (!backend.HasExited)
                    {
                        var observed = await controller.ObserveAsync(default);
                        if (observed.ProcessId == backend.Id && observed.Capability == LocalManagementCapability.ManagedLocal)
                            await controller.StopAsync(observed, _ => { }, default);
                        if (!backend.HasExited) { backend.Kill(); await backend.WaitForExitAsync(); }
                    }
                }
                finally { backend.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_startup_failure_before_provider_exits_without_a_second_exception(bool loggerConstructed)
    {
        var root = CreateRoot();
        var options = Options(root);
        using var desktop = DesktopProcess.Start(root, options, configure: start =>
        {
            if (loggerConstructed) start.Environment["ARBITRAGE_BACKEND_ARTIFACT"] = "relative.exe";
            else start.Environment["ARBITRAGE_DESKTOP_DIRECTORY"] = options.RuntimeDirectory;
        });
        var dialog = await desktop.WaitForDialog();
        Assert.True(NativeWindows.PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero));
        await desktop.AssertExit(1);
        var logs = Path.Combine(root, "desktop", "logs");
        if (loggerConstructed)
        {
            foreach (var file in Directory.GetFiles(logs))
                using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        else Assert.False(Directory.Exists(logs));
    }

    [Fact]
    public async Task Validate_package_early_exit_has_no_normal_startup_or_disposal_failure()
    {
        var root = CreateRoot();
        using var desktop = DesktopProcess.Start(root, Options(root), validatePackage: true);
        // A development output has no package manifest; a normal rejection remains code 2.
        await desktop.AssertExit(2);
        Assert.False(Directory.Exists(Path.Combine(root, "desktop")));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-UI011", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root; // Retain isolated process/log evidence; never inspect or modify user profiles.
    }
    private static LocalBackendLaunchOptions Options(string root) => new(Path.Combine(root, "data"), Path.Combine(root, "runtime"),
        $"http://127.0.0.1:{RealtimeProcessTests.FreePort()}", RealtimeProcessTests.BackendArtifact(), null);

    private sealed class DesktopProcess : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stdout;
        private readonly Task<string> stderr;
        private readonly string evidence;
        private DesktopProcess(Process process, string evidence)
        {
            this.process = process; this.evidence = evidence;
            stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
        }
        public static DesktopProcess Start(string root, LocalBackendLaunchOptions options,
            Action<ProcessStartInfo>? configure = null, bool validatePackage = false)
        {
            var executable = Path.Combine(RealtimeProcessTests.RepositoryRoot(), "src", "Arbitrage.Desktop", "bin",
                RealtimeProcessTests.BuildConfiguration, "net10.0-windows", "Arbitrage.Desktop.exe");
            Assert.True(File.Exists(executable), "Build the matching Desktop executable before shutdown tests.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.ToArray())
                if (key.StartsWith("Local__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ARBITRAGE_", StringComparison.OrdinalIgnoreCase))
                    start.Environment.Remove(key);
            start.Environment["Local__DataDirectory"] = options.DataDirectory;
            start.Environment["Local__RuntimeDirectory"] = options.RuntimeDirectory;
            start.Environment["ARBITRAGE_RUNTIME_DIRECTORY"] = options.RuntimeDirectory;
            start.Environment["ARBITRAGE_DESKTOP_DIRECTORY"] = Path.Combine(root, "desktop");
            start.Environment["ARBITRAGE_BACKEND_ARTIFACT"] = options.ArtifactPath;
            start.Environment["Local__BaseUrl"] = options.BaseUrl;
            start.Environment["Local__DeploymentMode"] = "Local";
            start.Environment["Local__TradingMode"] = "Paper";
            if (validatePackage) start.ArgumentList.Add("--validate-package");
            configure?.Invoke(start);
            return new(Process.Start(start)!, Path.Combine(root, "desktop-process-" + Guid.NewGuid().ToString("N")));
        }
        public Task<IntPtr> WaitForShell() => WaitForWindow(shell: true);
        public Task<IntPtr> WaitForDialog() => WaitForWindow(shell: false);
        private async Task<IntPtr> WaitForWindow(bool shell)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                var window = NativeWindows.Find(process.Id, shell);
                if (window != IntPtr.Zero) return window;
                if (shell) Assert.Equal(IntPtr.Zero, NativeWindows.Find(process.Id, false));
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The isolated Desktop did not create the expected window: " + evidence);
        }
        public async Task WaitForManagedLocal(IntPtr window)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                var element = AutomationElement.FromHandle(window);
                var management = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "ManagedLocal"));
                var stop = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Stop local backend"));
                if (management is not null && stop?.Current.IsEnabled == true) return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The real reopened Desktop did not show ManagedLocal with Stop enabled: " + evidence);
        }
        public async Task Close(IntPtr window, bool duplicate)
        {
            Assert.True(NativeWindows.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero));
            if (duplicate) NativeWindows.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            await AssertExit(0);
        }
        public async Task AssertExit(int expected)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var error = await stderr;
            Assert.Equal(expected, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ObjectDisposedException", error, StringComparison.Ordinal);
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                var window = NativeWindows.Find(process.Id, true);
                if (window == IntPtr.Zero) window = NativeWindows.Find(process.Id, false);
                if (window != IntPtr.Zero) NativeWindows.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); }
            }
            File.WriteAllText(evidence + ".stdout.log", stdout.GetAwaiter().GetResult());
            File.WriteAllText(evidence + ".stderr.log", stderr.GetAwaiter().GetResult());
            process.Dispose();
        }
    }

    private static class NativeWindows
    {
        private delegate bool Callback(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        internal static IntPtr Find(int processId, bool shell)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var id);
                if (id != processId) return true;
                var title = new StringBuilder(256); var type = new StringBuilder(256);
                GetWindowText(window, title, 256); GetClassName(window, type, 256);
                if (title.ToString() == "Arbitrage Trading" && (shell ? type.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal) : type.ToString() == "#32770"))
                    found = window;
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found;
        }
    }
}
