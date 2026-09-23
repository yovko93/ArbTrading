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
    private readonly object stopGate = new();
    private Guid stoppedInstance;
    private volatile bool authPaused;
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
        state.AccessInvalidated += OnAccessInvalidated;
        runner = RunSafelyAsync(lifetime.Token);
    }

    private void OnAccessInvalidated(object? sender, EventArgs args)
    {
        // No dispatcher wait, cancellation callback, or HubConnection disposal on the Save stack.
        authPaused = true;
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref notifications, 0);
        Signal();
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try { await RunAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        { await dispatcher.InvokeAsync(() =>
            { if (!authPaused) state.SetRealtimeStatus("Disconnected", "Realtime session stopped unexpectedly."); }); }
    }

    public Task RefreshAsync()
    {
        authPaused = false;
        Signal();
        return Task.CompletedTask;
    }

    public void ResumeAfterStart()
    {
        authPaused = false; Signal();
    }

    public void SuspendAfterStopRequest(Guid backendInstanceId)
    {
        lock (stopGate) stoppedInstance = backendInstanceId;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (StoppedInstance != backendInstanceId) return;
            state.SetRealtimeStatus("StopRequested",
                "Backend stop was accepted. Waiting for the managed process to exit.",
                activeHub?.State == HubConnectionState.Connected);
        });
    }

    private Guid StoppedInstance { get { lock (stopGate) return stoppedInstance; } }
    private void ClearStoppedInstance(Guid replacement)
    {
        lock (stopGate)
            if (stoppedInstance != Guid.Empty && stoppedInstance != replacement) stoppedInstance = Guid.Empty;
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
            if (StoppedInstance != Guid.Empty || authPaused)
            {
                await wake.WaitAsync(cancellationToken);
                Interlocked.Exchange(ref wakeQueued, 0);
                // Refresh is a bounded observation even after an intentional stop.
            }
            var session = Interlocked.Increment(ref generation);
            var connected = false;
            var connectedInstance = Guid.Empty;
            string? failureStatus = null;
            LocalConnection? attemptedConnection = null;
            try
            {
                await SessionUiAsync(session, () => state.SetRealtimeStatus(attempt == 0 ? "Connecting" : "Reconnecting",
                    "Connecting to the authorized local backend."), cancellationToken);
                if (session != Volatile.Read(ref generation)) throw new OperationCanceledException("Desktop access context was superseded.");
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
                hub.On<StateInvalidation>("StateInvalidated", new Func<StateInvalidation, Task>(async notification =>
                {
                    if (session != Volatile.Read(ref generation) ||
                        (currentInstance != Guid.Empty && notification.BackendInstanceId != currentInstance) ||
                        (currentWorkspace != Guid.Empty && notification.WorkspaceId != currentWorkspace)) return;
                    Interlocked.Increment(ref notifications); Signal();
                    if (notification.Kind.StartsWith("Paper", StringComparison.Ordinal))
                        await AuthorizedUiAsync(session, notification.BackendInstanceId, notification.WorkspaceId,
                            state.NotifyPaperValuationInvalidated, cancellationToken);
                }));
                hub.On<BackendDiagnosticEvent>("BackendDiagnostic", new Func<BackendDiagnosticEvent, Task>(async entry =>
                {
                    if (session != Volatile.Read(ref generation) ||
                        (currentInstance != Guid.Empty && entry.BackendInstanceId != currentInstance) ||
                        (currentWorkspace != Guid.Empty && entry.WorkspaceId != currentWorkspace)) return;
                    await AuthorizedUiAsync(session, entry.BackendInstanceId, entry.WorkspaceId,
                        () => diagnostics.AddBackend(entry), cancellationToken);
                }));
                hub.On<ApplicationHeartbeat>("ApplicationHeartbeat", new Func<ApplicationHeartbeat, Task>(async heartbeat =>
                {
                    if (session != Volatile.Read(ref generation) || heartbeat.BackendInstanceId != currentInstance) return;
                    Interlocked.Exchange(ref lastHeartbeatTick, Stopwatch.GetTimestamp());
                    await AuthorizedUiAsync(session, heartbeat.BackendInstanceId, currentWorkspace,
                        () => state.SetHeartbeatStatus("Recent application heartbeat"), cancellationToken);
                }));
                hub.On<OrderBookInvalidation>("OrderBookInvalidated", new Func<OrderBookInvalidation, Task>(async notice =>
                {
                    await AuthorizedUiAsync(session, notice.BackendInstanceId, notice.WorkspaceId,
                        () => state.NotifyOrderBookInvalidated(notice), cancellationToken);
                }));
                hub.On<MonitoringInvalidation>("MonitoringChanged", new Func<MonitoringInvalidation, Task>(async notice =>
                {
                    await AuthorizedUiAsync(session, notice.InstanceId, notice.WorkspaceId,
                        () => state.NotifyMonitoringInvalidated(notice), cancellationToken);
                }));
                hub.On<CatalogInvalidation>("CatalogInvalidated", new Func<CatalogInvalidation, Task>(async notice =>
                {
                    if (notice.BackendInstanceId != currentInstance || notice.WorkspaceId != currentWorkspace) return;
                    await AuthorizedUiAsync(session, notice.BackendInstanceId, notice.WorkspaceId,
                        state.NotifyCatalogInvalidated, cancellationToken);
                }));
                hub.Closed += _ =>
                {
                    closed = true;
                    if (session == Volatile.Read(ref generation)) Signal();
                    return Task.CompletedTask;
                };
                using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await hub.StartAsync(startTimeout.Token);
                var acknowledgment = await hub.InvokeAsync<WorkspaceSubscriptionResponse>(
                    "SubscribeDefaultWorkspace", startTimeout.Token);
                if (session != Volatile.Read(ref generation)) throw new OperationCanceledException("Desktop access context was superseded.");
                currentInstance = acknowledgment.BackendInstanceId;
                currentWorkspace = acknowledgment.WorkspaceId;
                connectedInstance = acknowledgment.BackendInstanceId;
                Interlocked.Exchange(ref lastHeartbeatTick, 0);
                var subscriptionTick = Stopwatch.GetTimestamp();
                connected = true; attempt = 0;
                await AuthorizedUiAsync(session, currentInstance, currentWorkspace, () =>
                {
                    // Even a reconnect to the same actor/workspace supersedes old command completions.
                    state.BeginAuthorizedRealtimeSession();
                    diagnostics.SetBackendIdentity(currentWorkspace, currentInstance);
                    state.SetRealtimeStatus("Synchronizing", "Loading the authoritative workspace snapshot.", true);
                }, cancellationToken);
                await RefreshSnapshotAsync(session, local.BaseUrl, cancellationToken);
                if (StoppedInstance == connectedInstance)
                    await AuthorizedUiAsync(session, currentInstance, currentWorkspace,
                        () => state.SetRealtimeStatus("StopRequested",
                            "Backend stop was requested; awaiting process exit.", true), cancellationToken);
                var lastConsistencyTick = Stopwatch.GetTimestamp();
                while (!closed && hub.State == HubConnectionState.Connected && !cancellationToken.IsCancellationRequested &&
                    session == Volatile.Read(ref generation) && !authPaused)
                {
                    var signaled = await wake.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    Interlocked.Exchange(ref wakeQueued, 0);
                    if (closed || hub.State != HubConnectionState.Connected || session != Volatile.Read(ref generation)) break;
                    var heartbeatTick = Interlocked.Read(ref lastHeartbeatTick);
                    if (Stopwatch.GetElapsedTime(heartbeatTick == 0 ? subscriptionTick : heartbeatTick) > settings.HeartbeatStaleAfter)
                        await AuthorizedUiAsync(session, currentInstance, currentWorkspace,
                            () => state.SetHeartbeatStatus("Application heartbeat stale"), cancellationToken);
                    if (!signaled && Stopwatch.GetElapsedTime(lastConsistencyTick) < settings.ConsistencyInterval) continue;
                    // Only the low-frequency consistency interval or an invalidation makes an HTTP request.
                    await RefreshSnapshotAsync(session, local.BaseUrl, cancellationToken);
                    if (!signaled) await MergeHistoryAsync(session, cancellationToken);
                    lastConsistencyTick = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) when (session != Volatile.Read(ref generation)) { }
            catch (Exception exception)
            {
                var status = Classify(exception);
                failureStatus = status;
                var accessInvalid = status is "AuthenticationFailed" or "AuthorizationDenied";
                if (accessInvalid) authPaused = true;
                var failureGeneration = accessInvalid ? Interlocked.Increment(ref generation) : session;
                var credentialRotated = false;
                if (status == "AuthenticationFailed" && attemptedConnection is not null)
                {
                    try
                    {
                        var updated = await backend.ReadConnectionAsync(cancellationToken);
                        if (updated.Credential != attemptedConnection.Credential || updated.BaseUrl != attemptedConnection.BaseUrl)
                        { credentialRotated = true; }
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                }
                var appliedFailureGeneration = 0L;
                await UiAsync(() =>
                {
                    if (failureGeneration != Volatile.Read(ref generation)) return;
                    var visibleStatus = status == "Disconnected" && StoppedInstance != Guid.Empty ? "StopRequested" : status;
                    state.SetRealtimeStatus(visibleStatus, status switch
                    {
                        "AuthenticationFailed" => "Local authentication failed. Check the protected profile, then Refresh to retry.",
                        "AuthorizationDenied" => "Workspace subscription was denied by the backend.",
                        _ when visibleStatus == "StopRequested" => "Backend stop was requested; replacement is not synchronized.",
                        _ => "Backend connection interrupted. Retained state is stale; retrying with bounded backoff."
                    });
                    if (accessInvalid)
                    {
                        diagnostics.ClearBackend();
                        diagnostics.Record("Warning", status == "AuthenticationFailed" ?
                            "Backend authentication failed." : "Workspace subscription denied.");
                    }
                    appliedFailureGeneration = Volatile.Read(ref generation);
                }, cancellationToken);
                if (credentialRotated && appliedFailureGeneration != 0 && appliedFailureGeneration == Volatile.Read(ref generation))
                { authPaused = false; Signal(); }
            }
            finally
            {
                var accessInvalidated = session != Volatile.Read(ref generation);
                var cleanupGeneration = Interlocked.Increment(ref generation);
                currentInstance = Guid.Empty; currentWorkspace = Guid.Empty;
                var hub = activeHub; activeHub = null;
                if (hub is not null) await hub.DisposeAsync();
                if (connected && !accessInvalidated && failureStatus is not ("AuthenticationFailed" or "AuthorizationDenied") &&
                    !cancellationToken.IsCancellationRequested)
                    await SessionUiAsync(cleanupGeneration, () => state.SetRealtimeStatus(StoppedInstance == connectedInstance ? "StopRequested" : "Disconnected",
                        StoppedInstance == connectedInstance ? "Backend stop requested; process exit is not yet confirmed." :
                        "Realtime transport disconnected. Retained snapshot is stale."), cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) break;
            // Invalidation wakes an active wait; it is not permission to retry the revoked session.
            if (authPaused) { wake.Wait(0); Interlocked.Exchange(ref wakeQueued, 0); }
            if (authPaused || StoppedInstance != Guid.Empty) continue;
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
        var instance = currentInstance;
        var workspace = currentWorkspace;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var before = Volatile.Read(ref notifications);
            var snapshot = await backend.LoadSnapshotAsync(workspace, cancellationToken);
            if (!IsCurrent(session, instance, workspace) || snapshot.BackendInstanceId != instance ||
                snapshot.Workspace.WorkspaceId != workspace || snapshot.Session.DefaultWorkspaceId != workspace)
                return;
            await AuthorizedUiAsync(session, instance, workspace, () =>
            {
                ClearStoppedInstance(snapshot.BackendInstanceId);
                state.ApplyRealtimeSnapshot(snapshot, endpoint);
                if (StoppedInstance == snapshot.BackendInstanceId)
                    state.SetRealtimeStatus("StopRequested", "Backend stop was requested; awaiting process exit.", true);
            }, cancellationToken);
            if (repeat == 0) await MergeHistoryAsync(session, cancellationToken);
            if (before == Volatile.Read(ref notifications)) break;
        }
    }

    private async Task MergeHistoryAsync(long session, CancellationToken cancellationToken)
    {
        var instance = currentInstance;
        var workspace = currentWorkspace;
        var cursor = 0L;
        await AuthorizedUiAsync(session, instance, workspace,
            () => cursor = diagnostics.BackendInstanceId == instance ? diagnostics.HistoryCursor : 0, cancellationToken);
        if (!IsCurrent(session, instance, workspace)) return;
        var history = await backend.RecentDiagnosticsAsync(workspace, cursor, cancellationToken);
        if (!IsCurrent(session, instance, workspace) || history.BackendInstanceId != instance ||
            history.WorkspaceId != workspace) return;
        await AuthorizedUiAsync(session, instance, workspace, () => diagnostics.MergeBackend(history), cancellationToken);
    }

    private Task UiAsync(Action action, CancellationToken cancellationToken) => dispatcher.InvokeAsync(action, cancellationToken);
    private Task SessionUiAsync(long session, Action action, CancellationToken cancellationToken) =>
        DispatchIfCurrentAsync(dispatcher, () => !authPaused && session == Volatile.Read(ref generation), action, cancellationToken);
    private bool IsCurrent(long session, Guid instance, Guid workspace) =>
        !authPaused && session == Volatile.Read(ref generation) && currentInstance == instance && currentWorkspace == workspace;
    private Task AuthorizedUiAsync(long session, Guid instance, Guid workspace, Action action,
        CancellationToken cancellationToken) => DispatchIfCurrentAsync(dispatcher,
        () => IsCurrent(session, instance, workspace), action, cancellationToken);
    internal static Task DispatchIfCurrentAsync(IUiDispatcher dispatcher, Func<bool> isCurrent, Action action,
        CancellationToken cancellationToken) => dispatcher.InvokeAsync(() =>
        {
            if (isCurrent()) action();
        }, cancellationToken);
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
        state.AccessInvalidated -= OnAccessInvalidated;
        lifetime.Cancel(); Signal();
        // The run loop owns HubConnection disposal; shutdown never stops the backend host.
    }

    public async Task StopAsync()
    {
        Dispose();
        if (runner is not null) await runner;
    }
}
