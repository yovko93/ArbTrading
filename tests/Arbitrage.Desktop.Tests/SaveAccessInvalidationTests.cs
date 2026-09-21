using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

public sealed partial class RealtimeProcessTests
{
    private sealed class ResponseBarrier
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid SnapshotInstance;
    }

    // Only REST timing/outcomes are controlled. SignalR uses the real authenticated backend/WebSocket.
    private sealed class AccessRaceHandler : DelegatingHandler
    {
        private readonly ConcurrentBag<ResponseBarrier> heldResponses = [];
        public ResponseBarrier? SnapshotBarrier;
        public ResponseBarrier? WriteBarrier;
        public HttpStatusCode? WriteFailure;
        public bool FailNextSnapshot;
        public int Writes;
        public AccessRaceHandler() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
        public void ReleaseAll()
        {
            SnapshotBarrier?.Release.TrySetResult(true);
            WriteBarrier?.Release.TrySetResult(true);
            foreach (var held in heldResponses) held.Release.TrySetResult(true);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref Writes);
                if (WriteBarrier is { } write)
                {
                    write.Entered.TrySetResult(true);
                    await write.Release.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    write.Returned.TrySetResult(true);
                }
                if (WriteFailure is { } failure) return new(failure);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/snapshot", StringComparison.Ordinal))
            {
                if (FailNextSnapshot) { FailNextSnapshot = false; throw new HttpRequestException("Controlled transport interruption."); }
                var held = Interlocked.Exchange(ref SnapshotBarrier, null);
                var response = await base.SendAsync(request, cancellationToken);
                if (held is not null)
                {
                    heldResponses.Add(held);
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    held.SnapshotInstance = JsonSerializer.Deserialize<ApplicationSnapshotResponse>(json, JsonSerializerOptions.Web)!.BackendInstanceId;
                    response.Content.Dispose();
                    response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    held.Entered.TrySetResult(true);
                    // Deliberately deliver the old successful response, even if its context was invalidated.
                    await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    held.Returned.TrySetResult(true);
                }
                return response;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class AccessRaceDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource<bool> Done)> queue = new();
        public int Pending => queue.Count;
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue((action, done));
            return done.Task;
        }
        public void Drain()
        {
            while (queue.TryDequeue(out var item))
            {
                try { item.Action(); item.Done.TrySetResult(true); }
                catch (Exception exception) { item.Done.TrySetException(exception); }
            }
        }
        public async Task PumpUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!condition())
            {
                Drain();
                if (!condition()) await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class AccessRaceHarness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01C2", Guid.NewGuid().ToString("N"));
        private readonly HttpClient controlHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        private readonly HttpClient stateHttp;
        private readonly LocalBackendController controller;
        private readonly ProtectedLocalConnectionFile file;
        private readonly Dictionary<int, DateTime> owned = new();
        private LocalBackendObservation? running;
        private readonly string artifact = BackendArtifact();
        public AccessRaceHandler Handler { get; } = new();
        public AccessRaceDispatcher Dispatcher { get; } = new();
        public DesktopDiagnostics Diagnostics { get; } = new();
        public MainViewModel State { get; }
        public RealtimeSession Realtime { get; }
        public Guid Instance => running!.Snapshot!.BackendInstanceId;
        public Guid Workspace => running!.Snapshot!.Workspace.WorkspaceId;
        public AccessRaceHarness()
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01C2")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test root.");
            ProtectedStorage.CreatePrivateDirectory(Path.Combine(root, "desktop"));
            file = new(Path.Combine(root, "runtime"));
            var options = new LocalBackendLaunchOptions(Path.Combine(root, "backend"), Path.Combine(root, "runtime"),
                $"http://127.0.0.1:{FreePort()}", artifact, null);
            controlHttp.BaseAddress = new(options.BaseUrl);
            controller = new(new BackendClient(controlHttp, file), options);
            stateHttp = new(Handler) { Timeout = TimeSpan.FromSeconds(20) };
            var client = new BackendClient(stateHttp, file);
            State = new(client, NullLogger<MainViewModel>.Instance, Diagnostics);
            Realtime = new(client, State, Diagnostics, Dispatcher, new RealtimeDelay());
            State.RefreshRequested = Realtime.RefreshAsync;
        }
        public async Task StartAsync()
        {
            running = await controller.StartAsync(default);
            Assert.Equal(LocalProcessState.Running, running.ProcessState);
            TrackOwned(running, owned);
            var connection = await file.ReadAsync(default);
            controlHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
            Realtime.Start();
            await Dispatcher.PumpUntilAsync(() => State.ConnectionStatus == "Connected" && Diagnostics.HistoryCursor > 0);
        }
        public async Task PublishDiagnosticAsync()
        {
            var response = await controlHttp.PutAsJsonAsync($"/api/v1/workspaces/{Workspace}/settings",
                new UpdateWorkspaceSettingsRequest("Changed by another authorized client"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        public async ValueTask DisposeAsync()
        {
            Handler.ReleaseAll();
            try
            {
                var stopped = Realtime.StopAsync();
                await Dispatcher.PumpUntilAsync(() => stopped.IsCompleted);
                await stopped;
                if (running is not null) await controller.StopAsync(running, _ => { }, default);
            }
            finally
            {
                State.Dispose(); stateHttp.Dispose(); controlHttp.Dispose();
                foreach (var entry in owned)
                {
                    try
                    {
                        using var process = Process.GetProcessById(entry.Key);
                        if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - entry.Value).TotalSeconds) < 1 &&
                            string.Equals(process.MainModule?.FileName, artifact, StringComparison.OrdinalIgnoreCase))
                        { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, "AuthenticationFailed")]
    [InlineData(HttpStatusCode.Forbidden, true, "AuthorizationDenied")]
    public async Task Save_denial_invalidates_actual_realtime_snapshot_and_queued_diagnostics(
        HttpStatusCode denial, bool queueBeforeSave, string expectedStatus)
    {
        await using var harness = new AccessRaceHarness();
        await harness.StartAsync();
        harness.Diagnostics.Record("Information", "Safe local event retained across access recovery.");
        var held = new ResponseBarrier();
        harness.Handler.SnapshotBarrier = held;
        await harness.Realtime.RefreshAsync();
        await harness.Dispatcher.PumpUntilAsync(() => held.Entered.Task.IsCompleted);
        Assert.Equal(harness.Instance, held.SnapshotInstance);
        if (queueBeforeSave)
        {
            held.Release.TrySetResult(true);
            await held.Returned.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await WaitUntilAsync(() => harness.Dispatcher.Pending >= 1);
            await harness.PublishDiagnosticAsync();
            await WaitUntilAsync(() => harness.Dispatcher.Pending >= 2);
        }
        harness.Handler.WriteFailure = denial;
        harness.State.WorkspaceName = "Denied draft";
        await harness.State.SaveCommand.ExecuteAsync(null);
        Assert.Equal(expectedStatus, harness.State.ConnectionStatus);
        Assert.False(harness.State.HasSnapshot);
        held.Release.TrySetResult(true);
        await held.Returned.Task.WaitAsync(TimeSpan.FromSeconds(12));
        await harness.Dispatcher.PumpUntilAsync(() => harness.State.HasSnapshot || harness.Realtime.CurrentInstance == Guid.Empty);
        harness.Dispatcher.Drain();
        Assert.False(harness.State.HasSnapshot);
        Assert.Equal(expectedStatus, harness.State.ConnectionStatus);
        Assert.False(harness.State.CanEdit);
        Assert.False(harness.Realtime.IsSynchronized);
        Assert.Empty(harness.Diagnostics.BackendEvents);
        Assert.Equal(0, harness.Diagnostics.HistoryCursor);
        Assert.Equal(denial == HttpStatusCode.Unauthorized ? 2 : 1, harness.Handler.Writes);

        harness.Handler.WriteFailure = null;
        await harness.Realtime.RefreshAsync();
        await harness.Dispatcher.PumpUntilAsync(() => harness.Realtime.IsSynchronized && harness.Diagnostics.HistoryCursor > 0);
        Assert.Equal(harness.Instance, harness.Realtime.CurrentInstance);
        Assert.True(harness.State.CanEdit);
        Assert.NotEmpty(harness.Diagnostics.BackendEvents);
        Assert.Contains(harness.Diagnostics.Events, e => e.Description.StartsWith("Safe local event", StringComparison.Ordinal));
        Assert.Equal(denial == HttpStatusCode.Unauthorized ? 2 : 1, harness.Handler.Writes);
    }

    [Fact]
    public async Task Delayed_save_denial_from_old_connection_does_not_revoke_fresh_subscription()
    {
        await using var harness = new AccessRaceHarness();
        await harness.StartAsync();
        var heldWrite = new ResponseBarrier();
        harness.Handler.WriteBarrier = heldWrite;
        harness.Handler.WriteFailure = HttpStatusCode.Forbidden;
        harness.State.WorkspaceName = "Draft survives ordinary interruption";
        var save = harness.State.SaveCommand.ExecuteAsync(null);
        await heldWrite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        harness.Handler.FailNextSnapshot = true;
        await harness.Realtime.RefreshAsync();
        await harness.Dispatcher.PumpUntilAsync(() => harness.State.ConnectionStatus == "Disconnected" && harness.Realtime.CurrentInstance == Guid.Empty);
        Assert.True(harness.State.IsStale);
        Assert.Equal("Draft survives ordinary interruption", harness.State.WorkspaceName);
        await harness.Realtime.RefreshAsync();
        await harness.Dispatcher.PumpUntilAsync(() => harness.Realtime.IsSynchronized);
        Assert.Equal(harness.Instance, harness.Realtime.CurrentInstance);
        heldWrite.Release.TrySetResult(true);
        await save.WaitAsync(TimeSpan.FromSeconds(12));
        harness.Dispatcher.Drain();
        Assert.True(harness.State.HasSnapshot);
        Assert.True(harness.Realtime.IsSynchronized);
        Assert.Equal("Draft survives ordinary interruption", harness.State.WorkspaceName);
        Assert.NotEmpty(harness.Diagnostics.BackendEvents);
        Assert.Equal(1, harness.Handler.Writes);
    }
}
