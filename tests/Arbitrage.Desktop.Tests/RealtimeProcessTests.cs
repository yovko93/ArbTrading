using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

public sealed partial class RealtimeProcessTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class BadConnection(string url) : ILocalConnectionFile
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref reads); return Task.FromResult(new LocalConnection(url, new string('0', 64))); }
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class CountingDelay : IRealtimeDelay
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls); return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    }
    [Fact]
    public async Task Real_managed_process_negotiates_WebSocket_synchronizes_two_clients_and_stops_safely()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01C-process", Guid.NewGuid().ToString("N"));
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01C-process")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test root.");
        var data = Path.Combine(root, "backend"); var runtime = Path.Combine(root, "runtime");
        var desktop = Path.Combine(root, "desktop");
        ProtectedStorage.CreatePrivateDirectory(desktop);
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var options = new LocalBackendLaunchOptions(data, runtime, url, BackendArtifact(), null);
        var file = new ProtectedLocalConnectionFile(runtime);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var client = new BackendClient(http, file);
        var controller = new LocalBackendController(client, options);
        var desktopEvents = new DesktopDiagnostics();
        using var desktopState = new MainViewModel(client, NullLogger<MainViewModel>.Instance, desktopEvents);
        using var realtime = new RealtimeSession(client, desktopState, desktopEvents, new InlineDispatcher(), new RealtimeDelay());
        LocalBackendObservation? first = null;
        LocalBackendObservation? second = null;
        LocalBackendObservation? third = null;
        var ownedStarts = new Dictionary<int, DateTime>();
        try
        {
            realtime.Start();
            await WaitUntilAsync(() => desktopState.ConnectionStatus == "Disconnected");
            Assert.Equal(LocalProcessState.NotRunning, (await controller.ObserveAsync(default)).ProcessState);
            var reattached = new LocalBackendController(client, options);
            var simultaneous = await Task.WhenAll(controller.StartAsync(default), reattached.StartAsync(default));
            first = simultaneous[0];
            Assert.Equal(LocalProcessState.Running, first.ProcessState);
            Assert.Equal(LocalManagementCapability.ManagedLocal, first.Capability);
            Assert.NotNull(first.Snapshot);
            Assert.Equal(first.Snapshot!.BackendInstanceId, simultaneous[1].Snapshot!.BackendInstanceId);
            Assert.Equal(first.ProcessId, simultaneous[1].ProcessId);
            TrackOwned(first, ownedStarts);
            realtime.ResumeAfterStart();
            await WaitUntilAsync(() => desktopState.ConnectionStatus == "Connected");
            Assert.True(realtime.IsSynchronized);
            Assert.Equal(first.Snapshot!.BackendInstanceId, (await controller.StartAsync(default)).Snapshot!.BackendInstanceId);
            Assert.Equal(LocalManagementCapability.ManagedLocal, (await reattached.ObserveAsync(default)).Capability);
            var connection = await file.ReadAsync(default);
            Assert.Equal(url, connection.BaseUrl);
            var badConnection = new BadConnection(url);
            var badDelay = new CountingDelay();
            var badDiagnostics = new DesktopDiagnostics();
            using (var badHttp = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
            using (var badState = new MainViewModel(new BackendClient(badHttp, badConnection),
                NullLogger<MainViewModel>.Instance, badDiagnostics))
            using (var badSession = new RealtimeSession(new BackendClient(badHttp, badConnection), badState,
                badDiagnostics, new InlineDispatcher(), badDelay))
            {
                badState.ApplyRealtimeSnapshot(first.Snapshot!, url);
                badDiagnostics.SetBackendIdentity(first.Snapshot!.Workspace.WorkspaceId, first.Snapshot.BackendInstanceId);
                badDiagnostics.AddBackend(new(first.Snapshot.BackendInstanceId, 1, DateTimeOffset.UtcNow,
                    "Information", "Backend", "Private", "Private event.", first.Snapshot.Workspace.WorkspaceId, null));
                badSession.Start();
                await WaitUntilAsync(() => badState.ConnectionStatus == "AuthenticationFailed");
                Assert.False(badState.HasSnapshot);
                Assert.Empty(badDiagnostics.BackendEvents);
                Assert.Equal(0, badDiagnostics.HistoryCursor);
                var readsAfterFailure = badConnection.Reads;
                await Task.Yield();
                Assert.Equal(readsAfterFailure, badConnection.Reads);
                Assert.Equal(0, badDelay.Calls);
                await badSession.RefreshAsync();
                await WaitUntilAsync(() => badConnection.Reads > readsAfterFailure);
                await badSession.StopAsync();
                Assert.Equal(0, badDelay.Calls);
            }
            using var unauthorized = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await unauthorized.PostAsync(url + "/hubs/v1/application/negotiate?negotiateVersion=1", null)).StatusCode);
            unauthorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await unauthorized.PostAsync(url + "/hubs/v1/application/negotiate?negotiateVersion=1", null)).StatusCode);
            unauthorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
            var other = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.NotFound,
                (await unauthorized.GetAsync(url + $"/api/v1/workspaces/{other}/snapshot")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await unauthorized.GetAsync(url + $"/api/v1/workspaces/{other}/diagnostics")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,
                (await unauthorized.PostAsJsonAsync(url + "/api/v1/local-runtime/stop", new StopLocalRuntimeRequest(Guid.NewGuid()))).StatusCode);

            await using var hubA = CreateHub(url, connection.Credential);
            await using var hubB = CreateHub(url, connection.Credential);
            var eventA = new TaskCompletionSource<StateInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
            var eventB = new TaskCompletionSource<StateInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
            hubA.On<StateInvalidation>("StateInvalidated", value => eventA.TrySetResult(value));
            hubB.On<StateInvalidation>("StateInvalidated", value => eventB.TrySetResult(value));
            await hubA.StartAsync(); await hubB.StartAsync();
            var a = await hubA.InvokeAsync<WorkspaceSubscriptionResponse>("SubscribeDefaultWorkspace");
            var b = await hubB.InvokeAsync<WorkspaceSubscriptionResponse>("SubscribeDefaultWorkspace");
            Assert.Equal(a.WorkspaceId, b.WorkspaceId);
            await Assert.ThrowsAsync<HubException>(() => hubA.InvokeAsync("SubscribeWorkspace", other));
            Assert.Equal(first.Snapshot.BackendInstanceId, a.BackendInstanceId);
            var path = url + $"/api/v1/workspaces/{a.WorkspaceId}/settings";
            var write = await unauthorized.PutAsJsonAsync(path, new UpdateWorkspaceSettingsRequest("Realtime verified"));
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            Assert.Equal("WorkspaceSettings", (await eventA.Task.WaitAsync(TimeSpan.FromSeconds(8))).Kind);
            Assert.Equal("WorkspaceSettings", (await eventB.Task.WaitAsync(TimeSpan.FromSeconds(8))).Kind);
            await WaitUntilAsync(() => desktopState.WorkspaceName == "Realtime verified");
            var snapshot = await unauthorized.GetFromJsonAsync<ApplicationSnapshotResponse>(url + $"/api/v1/workspaces/{a.WorkspaceId}/snapshot");
            Assert.Equal("Realtime verified", snapshot!.Workspace.DisplayName);
            Assert.Equal("Paper", snapshot.System.EffectiveTradingMode);
            Assert.False(snapshot.System.Capabilities.LiveOrderSubmissionAvailable);
            var history = await unauthorized.GetFromJsonAsync<RecentDiagnosticsResponse>(url + $"/api/v1/workspaces/{a.WorkspaceId}/diagnostics?take=100");
            Assert.Contains(history!.Events, e => e.Code == "WorkspaceSettingsChanged" && e.WorkspaceId == a.WorkspaceId);
            Assert.DoesNotContain(connection.Credential, JsonSerializer.Serialize(history));
            Assert.DoesNotContain(connection.Credential, JsonSerializer.Serialize(snapshot));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await unauthorized.PutAsJsonAsync(path, new UpdateWorkspaceSettingsRequest(""))).StatusCode);
            var afterRejected = await unauthorized.GetFromJsonAsync<RecentDiagnosticsResponse>(
                url + $"/api/v1/workspaces/{a.WorkspaceId}/diagnostics?take=100");
            Assert.Single(afterRejected!.Events, e => e.Code == "WorkspaceSettingsChanged");
            using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync(url + "/api/v1/local-runtime/stop",
                    new StopLocalRuntimeRequest(a.BackendInstanceId))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await unauthorized.GetAsync(url + "/health/live")).StatusCode);
            await hubA.DisposeAsync(); await hubB.DisposeAsync();
            var stopped = await reattached.StopAsync(first, realtime.SuspendAfterStopRequest, default);
            Assert.Equal(LocalProcessState.NotRunning, stopped.ProcessState);
            await WaitUntilAsync(() => desktopState.IsStale);

            second = await controller.StartAsync(default);
            Assert.Equal(LocalProcessState.Running, second.ProcessState);
            TrackOwned(second, ownedStarts);
            // Refresh must validate a replacement process without launching or stopping it.
            await realtime.RefreshAsync();
            await WaitUntilAsync(() => desktopState.ConnectionStatus == "Connected" &&
                desktopState.BackendInstance == second.Snapshot!.BackendInstanceId.ToString());
            Assert.True(realtime.IsSynchronized);
            Assert.NotEqual(first.Snapshot.BackendInstanceId, second.Snapshot!.BackendInstanceId);
            var rotated = await file.ReadAsync(default);
            Assert.NotEqual(connection.Credential, rotated.Credential);
            using var old = new HttpClient(new HttpClientHandler { UseProxy = false });
            old.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
            Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync(url + "/api/v1/session")).StatusCode);
            // A later unexpected disconnect from B must use normal automatic reconnect, with no process launch.
            Assert.Equal(LocalProcessState.NotRunning, (await controller.StopAsync(second, _ => { }, default)).ProcessState);
            await WaitUntilAsync(() => desktopState.ConnectionStatus == "Disconnected");
            third = await controller.StartAsync(default);
            Assert.Equal(LocalProcessState.Running, third.ProcessState);
            TrackOwned(third, ownedStarts);
            await WaitUntilAsync(() => desktopState.ConnectionStatus == "Connected" &&
                desktopState.BackendInstance == third.Snapshot!.BackendInstanceId.ToString());
            Assert.NotEqual(second.Snapshot.BackendInstanceId, third.Snapshot!.BackendInstanceId);
            Assert.Equal(LocalProcessState.NotRunning,
                (await controller.StopAsync(third, realtime.SuspendAfterStopRequest, default)).ProcessState);
            var backendLog = Directory.GetFiles(Path.Combine(data, "logs"), "*.log");
            Assert.DoesNotContain(connection.Credential, string.Join("", backendLog.Select(File.ReadAllText)));
            Assert.DoesNotContain(rotated.Credential, string.Join("", backendLog.Select(File.ReadAllText)));
        }
        finally
        {
            await realtime.StopAsync();
            // Test-only cleanup can terminate only the exact process launched by this test.
            foreach (var owned in ownedStarts)
            {
                try
                {
                    using var process = Process.GetProcessById(owned.Key);
                    if (!process.HasExited &&
                        Math.Abs((process.StartTime.ToUniversalTime() - owned.Value).TotalSeconds) < 1 &&
                        string.Equals(process.MainModule?.FileName, options.ArtifactPath, StringComparison.OrdinalIgnoreCase))
                    { process.Kill(); await process.WaitForExitAsync(); }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static HubConnection CreateHub(string url, string credential) => new HubConnectionBuilder()
        .WithUrl(url + "/hubs/v1/application", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.Headers["Authorization"] = "Bearer " + credential;
            options.HttpMessageHandlerFactory = _ => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        }).Build();

    private static void TrackOwned(LocalBackendObservation observation, Dictionary<int, DateTime> starts)
    {
        if (observation.ProcessId is not { } pid) throw new InvalidOperationException("Managed process ID missing.");
        using var process = Process.GetProcessById(pid);
        starts[pid] = process.StartTime.ToUniversalTime();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(80, timeout.Token);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static string BackendArtifact()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ArbitrageTrading.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root unavailable for isolated test artifact.");
        var artifact = Path.Combine(directory.FullName, "src", "Arbitrage.Backend", "bin", "Release", "net10.0", "Arbitrage.Backend.exe");
        if (!File.Exists(artifact)) throw new FileNotFoundException("Build the Release backend before real-process verification.");
        return artifact;
    }
}
