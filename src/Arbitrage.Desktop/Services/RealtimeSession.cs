using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Arbitrage.Contracts;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Arbitrage.Desktop.Services;

public interface IRealtimeDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
public sealed class RealtimeDelay : IRealtimeDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record RealtimeOptions(TimeSpan ConsistencyInterval, TimeSpan HeartbeatStaleAfter)
{
    public static RealtimeOptions FromEnvironment()
    {
        var consistency = ReadSeconds("ARBITRAGE_CONSISTENCY_SECONDS", 60, 30, 300);
        var stale = ReadSeconds("ARBITRAGE_HEARTBEAT_STALE_SECONDS", 45, 30, 900);
        return new(TimeSpan.FromSeconds(consistency), TimeSpan.FromSeconds(stale));
    }
    private static int ReadSeconds(string name, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} must be {minimum}–{maximum} seconds.");
        return value;
    }
}

// One owner for the entire desktop lifetime. REST snapshots remain authoritative.
public sealed class RealtimeSession(BackendClient backend, MainViewModel state, DesktopDiagnostics diagnostics,
    IUiDispatcher dispatcher, IRealtimeDelay delay, RealtimeOptions? options = null) : IDisposable
{
    private readonly RealtimeOptions settings = options ?? new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45));
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? runner;
    private long generation;
    private long notifications;
    private int wakeQueued;
    private bool intentionalStop;
    private bool authPaused;
    private long lastHeartbeatTick;
    private Guid currentInstance;
    private Guid currentWorkspace;
    private HubConnection? activeHub;
    public Guid CurrentInstance => currentInstance;
    public Guid CurrentWorkspace => currentWorkspace;
    public bool IsSynchronized => state.ConnectionStatus == "Connected" && currentInstance != Guid.Empty;

    public void Start()
    {
        if (runner is not null) return;
        runner = RunSafelyAsync(lifetime.Token);
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try { await RunAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        { await dispatcher.InvokeAsync(() => state.SetRealtimeStatus("Disconnected", "Realtime session stopped unexpectedly.")); }
    }

    public Task RefreshAsync()
    {
        authPaused = false;
        Signal();
        return Task.CompletedTask;
    }

    public void ResumeAfterStart()
    {
        intentionalStop = false; authPaused = false; Signal();
    }

    public void SuspendAfterStopRequest()
    {
        intentionalStop = true;
        _ = dispatcher.InvokeAsync(() => state.SetRealtimeStatus("StopRequested",
            "Backend stop was accepted. Waiting for the managed process to exit.", activeHub?.State == HubConnectionState.Connected));
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref wakeQueued, 1) == 0) wake.Release();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (intentionalStop || authPaused)
            {
                await wake.WaitAsync(cancellationToken);
                Interlocked.Exchange(ref wakeQueued, 0);
                // Refresh is a bounded observation even after an intentional stop.
            }
            var session = Interlocked.Increment(ref generation);
            var connected = false;
            string? failureStatus = null;
            LocalConnection? attemptedConnection = null;
            try
            {
                await UiAsync(() => state.SetRealtimeStatus(attempt == 0 ? "Connecting" : "Reconnecting",
                    "Connecting to the authorized local backend."), cancellationToken);
                var local = await backend.ReadConnectionAsync(cancellationToken);
                attemptedConnection = local;
                LocalPaths.ValidateBaseUrl(local.BaseUrl);
                var hub = new HubConnectionBuilder().WithUrl(local.BaseUrl.TrimEnd('/') + "/hubs/v1/application", options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.Headers["Authorization"] = "Bearer " + local.Credential;
                    options.HttpMessageHandlerFactory = _ => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                }).Build();
                activeHub = hub;
                var closed = false;
                hub.On<StateInvalidation>("StateInvalidated", notification =>
                {
                    if (session != Volatile.Read(ref generation) ||
                        (currentInstance != Guid.Empty && notification.BackendInstanceId != currentInstance) ||
                        (currentWorkspace != Guid.Empty && notification.WorkspaceId != currentWorkspace)) return;
                    Interlocked.Increment(ref notifications); Signal();
                });
                hub.On<BackendDiagnosticEvent>("BackendDiagnostic", new Func<BackendDiagnosticEvent, Task>(async entry =>
                {
                    if (session != Volatile.Read(ref generation) ||
                        (currentInstance != Guid.Empty && entry.BackendInstanceId != currentInstance) ||
                        (currentWorkspace != Guid.Empty && entry.WorkspaceId != currentWorkspace)) return;
                    await UiAsync(() => diagnostics.AddBackend(entry), cancellationToken);
                }));
                hub.On<ApplicationHeartbeat>("ApplicationHeartbeat", new Func<ApplicationHeartbeat, Task>(async heartbeat =>
                {
                    if (session != Volatile.Read(ref generation) || heartbeat.BackendInstanceId != currentInstance) return;
                    Interlocked.Exchange(ref lastHeartbeatTick, Stopwatch.GetTimestamp());
                    await UiAsync(() => state.SetHeartbeatStatus("Recent application heartbeat"), cancellationToken);
                }));
                hub.Closed += _ => { closed = true; Signal(); return Task.CompletedTask; };
                using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await hub.StartAsync(startTimeout.Token);
                var acknowledgment = await hub.InvokeAsync<WorkspaceSubscriptionResponse>(
                    "SubscribeDefaultWorkspace", startTimeout.Token);
                currentInstance = acknowledgment.BackendInstanceId;
                currentWorkspace = acknowledgment.WorkspaceId;
                Interlocked.Exchange(ref lastHeartbeatTick, 0);
                var subscriptionTick = Stopwatch.GetTimestamp();
                connected = true; attempt = 0;
                await UiAsync(() =>
                {
                    diagnostics.SetBackendScope(currentWorkspace);
                    state.SetRealtimeStatus("Synchronizing", "Loading the authoritative workspace snapshot.", true);
                }, cancellationToken);
                await RefreshSnapshotAsync(session, local.BaseUrl, cancellationToken);
                if (intentionalStop) await UiAsync(() => state.SetRealtimeStatus("StopRequested",
                    "Backend stop was requested; awaiting process exit.", true), cancellationToken);
                var lastConsistencyTick = Stopwatch.GetTimestamp();
                while (!closed && hub.State == HubConnectionState.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var signaled = await wake.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    Interlocked.Exchange(ref wakeQueued, 0);
                    if (closed || hub.State != HubConnectionState.Connected) break;
                    var heartbeatTick = Interlocked.Read(ref lastHeartbeatTick);
                    if (Stopwatch.GetElapsedTime(heartbeatTick == 0 ? subscriptionTick : heartbeatTick) > settings.HeartbeatStaleAfter)
                        await UiAsync(() => state.SetHeartbeatStatus("Application heartbeat stale"), cancellationToken);
                    if (!signaled && Stopwatch.GetElapsedTime(lastConsistencyTick) < settings.ConsistencyInterval) continue;
                    // Only the low-frequency consistency interval or an invalidation makes an HTTP request.
                    await RefreshSnapshotAsync(session, local.BaseUrl, cancellationToken);
                    if (!signaled) await MergeHistoryAsync(session, cancellationToken);
                    lastConsistencyTick = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var status = Classify(exception);
                failureStatus = status;
                authPaused = status is "AuthenticationFailed" or "AuthorizationDenied";
                if (status == "AuthenticationFailed" && attemptedConnection is not null)
                {
                    try
                    {
                        var updated = await backend.ReadConnectionAsync(cancellationToken);
                        if (updated.Credential != attemptedConnection.Credential || updated.BaseUrl != attemptedConnection.BaseUrl)
                        { authPaused = false; Signal(); }
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                }
                await UiAsync(() => state.SetRealtimeStatus(status,
                    status switch
                    {
                        "AuthenticationFailed" => "Local authentication failed. Check the protected profile, then Refresh to retry.",
                        "AuthorizationDenied" => "Workspace subscription was denied by the backend.",
                        _ => "Backend connection interrupted. Retained state is stale; retrying with bounded backoff."
                    }), cancellationToken);
                if (status is "AuthenticationFailed" or "AuthorizationDenied")
                    await UiAsync(() => diagnostics.Record("Warning", status == "AuthenticationFailed" ?
                        "Backend authentication failed." : "Workspace subscription denied."), cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref generation);
                currentInstance = Guid.Empty; currentWorkspace = Guid.Empty;
                var hub = activeHub; activeHub = null;
                if (hub is not null) await hub.DisposeAsync();
                if (connected && failureStatus is not ("AuthenticationFailed" or "AuthorizationDenied") &&
                    !cancellationToken.IsCancellationRequested)
                    await UiAsync(() => state.SetRealtimeStatus(intentionalStop ? "StopRequested" : "Disconnected",
                        intentionalStop ? "Backend stop requested; process exit is not yet confirmed." :
                        "Realtime transport disconnected. Retained snapshot is stale."), cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) break;
            if (authPaused || intentionalStop) continue;
            attempt = Math.Min(attempt + 1, 5);
            var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = delay.DelayAsync(backoff, wait.Token);
            var wakeTask = wake.WaitAsync(wait.Token);
            await Task.WhenAny(delayTask, wakeTask);
            wait.Cancel();
            Interlocked.Exchange(ref wakeQueued, 0);
        }
    }

    private async Task RefreshSnapshotAsync(long session, string endpoint, CancellationToken cancellationToken)
    {
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var before = Volatile.Read(ref notifications);
            var snapshot = await backend.LoadSnapshotAsync(currentWorkspace, cancellationToken);
            if (session != Volatile.Read(ref generation) || snapshot.BackendInstanceId != currentInstance ||
                snapshot.Workspace.WorkspaceId != currentWorkspace || snapshot.Session.DefaultWorkspaceId != currentWorkspace)
                return;
            await UiAsync(() => state.ApplyRealtimeSnapshot(snapshot, endpoint), cancellationToken);
            if (repeat == 0) await MergeHistoryAsync(session, cancellationToken);
            if (before == Volatile.Read(ref notifications)) break;
        }
    }

    private async Task MergeHistoryAsync(long session, CancellationToken cancellationToken)
    {
        var cursor = diagnostics.BackendInstanceId == currentInstance ? diagnostics.LastBackendSequence : 0;
        var history = await backend.RecentDiagnosticsAsync(currentWorkspace, cursor, cancellationToken);
        if (session != Volatile.Read(ref generation) || history.BackendInstanceId != currentInstance ||
            history.WorkspaceId != currentWorkspace) return;
        await UiAsync(() => diagnostics.MergeBackend(history), cancellationToken);
    }

    private Task UiAsync(Action action, CancellationToken cancellationToken) => dispatcher.InvokeAsync(action, cancellationToken);
    private static string Classify(Exception exception) => exception switch
    {
        BackendFailure { State: ConnectionState.AuthenticationFailed } => "AuthenticationFailed",
        BackendFailure { State: ConnectionState.AuthorizationDenied } => "AuthorizationDenied",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "AuthenticationFailed",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "AuthorizationDenied",
        Microsoft.AspNetCore.SignalR.HubException => "AuthorizationDenied",
        UnauthorizedAccessException => "AuthenticationFailed",
        _ => "Disconnected"
    };

    public void Dispose()
    {
        lifetime.Cancel(); Signal();
        // The run loop owns HubConnection disposal; shutdown never stops the backend host.
    }

    public async Task StopAsync()
    {
        Dispose();
        if (runner is not null) await runner;
    }
}
