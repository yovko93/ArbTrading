using System.Net;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RealtimeStateTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public int Stops;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref Stops);
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
    private sealed class PendingWriteHandler : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Put ? Response.Task : throw new InvalidOperationException("Unexpected request.");
    }
    private sealed class FixedConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('A', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource<bool> Done)> queue = new();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue((action, done));
            return done.Task;
        }
        public void Drain()
        {
            while (queue.TryDequeue(out var pending))
            {
                try { pending.Action(); pending.Done.TrySetResult(true); }
                catch (Exception exception) { pending.Done.TrySetException(exception); }
            }
        }
    }

    [Fact]
    public void Diagnostic_history_is_bounded_scoped_and_marks_retention_gap()
    {
        var backend = new BackendInstance(); var store = new BackendDiagnosticStore(backend);
        var workspaceA = Guid.NewGuid(); var workspaceB = Guid.NewGuid();
        for (var i = 0; i < BackendDiagnosticStore.Retention + 15; i++)
            store.Add(workspaceA, "Information", "Workspace", "Changed", "Authorized event.", null);
        store.Add(workspaceB, "Information", "Workspace", "Changed", "Private event.", null);
        var recent = store.Recent(workspaceA, 1, 100);
        Assert.True(recent.Gap); Assert.Equal(100, recent.Events.Length);
        Assert.Equal(16, recent.OldestSequence);
        Assert.All(recent.Events, entry => Assert.Equal(workspaceA, entry.WorkspaceId));
        var other = store.Recent(workspaceB, 0, 100);
        Assert.Single(other.Events); Assert.Equal(1, other.NewestSequence);
        Assert.Equal(workspaceB, other.Events[0].WorkspaceId);
        Assert.Throws<ArgumentException>(() => store.Add(workspaceA, "Information", "Backend", "Unsafe", "credential\nvalue", null));
    }

    [Fact]
    public void Duplicate_stop_requests_for_same_instance_are_idempotently_accepted_after_validation()
    {
        var instance = new BackendInstance(); var lifetime = new Lifetime();
        var coordinator = new LocalShutdownCoordinator(lifetime, instance);
        var first = coordinator.Accept(new DefaultHttpContext());
        var second = coordinator.Accept(new DefaultHttpContext());
        Assert.True(coordinator.IsRequested);
        Assert.Equal(instance.Id, first.BackendInstanceId);
        Assert.Equal(first, second);
        Assert.Equal(0, lifetime.Stops); // response completion, not the handler, initiates shutdown
    }

    [Fact]
    public void Slow_consumer_drops_live_queue_without_blocking_history_or_leaking_another_scope()
    {
        var backend = new BackendInstance(); var store = new BackendDiagnosticStore(backend);
        var publisher = new RealtimePublisher(store); var workspace = Guid.NewGuid();
        for (var i = 0; i < 600; i++)
            publisher.Diagnostic(workspace, "Information", "Realtime", "QueueTest", "A real test event.");
        var history = store.Recent(workspace, 0, 100);
        Assert.True(history.DroppedCount > 0); Assert.Equal(600, history.NewestSequence);
        Assert.True(history.Gap); Assert.Equal(100, history.Events.Length);
        Assert.All(history.Events, entry => Assert.Equal(workspace, entry.WorkspaceId));
    }

    [Fact]
    public void Desktop_merges_history_and_live_overlap_and_notices_restart_and_scope_change()
    {
        var diagnostics = new DesktopDiagnostics(); var workspace = Guid.NewGuid(); var instance = Guid.NewGuid();
        diagnostics.SetBackendScope(workspace);
        var first = new BackendDiagnosticEvent(instance, 1, DateTimeOffset.UtcNow, "Information", "Backend", "Ready",
            "Backend ready.", workspace, null);
        diagnostics.AddBackend(first);
        diagnostics.MergeBackend(new(instance, workspace, 1, 1, 0, false, [first]));
        Assert.Single(diagnostics.BackendEvents);
        var next = Guid.NewGuid();
        diagnostics.MergeBackend(new(next, workspace, 2, 10, 1, true, []));
        Assert.Contains("gap", diagnostics.BackendNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(next, diagnostics.BackendInstanceId);
        diagnostics.SetBackendScope(Guid.NewGuid());
        Assert.Empty(diagnostics.BackendEvents);
        diagnostics.AddBackend(first);
        Assert.Empty(diagnostics.BackendEvents);
    }

    [Fact]
    public void Live_event_before_history_does_not_skip_earlier_retained_events()
    {
        var instance = new BackendInstance(); var store = new BackendDiagnosticStore(instance);
        var workspace = Guid.NewGuid(); var diagnostics = new DesktopDiagnostics();
        diagnostics.SetBackendScope(workspace);
        for (var i = 1; i <= 20; i++)
        {
            var entry = store.Add(workspace, "Information", "Backend", "Sequence", $"Event {i}.", null);
            if (i == 20) diagnostics.AddBackend(entry);
        }
        Assert.Equal(0, diagnostics.HistoryCursor);
        diagnostics.MergeBackend(store.Recent(workspace, diagnostics.HistoryCursor, 100));
        Assert.Equal(20, diagnostics.HistoryCursor);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i).Reverse(),
            diagnostics.BackendEvents.Select(e => e.Sequence));
    }

    [Fact]
    public void Restart_live_event_does_not_inherit_old_history_cursor()
    {
        var workspace = Guid.NewGuid(); var old = new BackendInstance(); var next = new BackendInstance();
        var diagnostics = new DesktopDiagnostics(); diagnostics.SetBackendScope(workspace);
        diagnostics.MergeBackend(new(old.Id, workspace, 1, 500, 0, false,
            [new(old.Id, 500, DateTimeOffset.UtcNow, "Information", "Backend", "Old", "Old event.", workspace, null)]));
        var store = new BackendDiagnosticStore(next);
        for (var i = 1; i <= 20; i++)
        {
            var entry = store.Add(workspace, "Information", "Backend", "New", $"New event {i}.", null);
            if (i == 1) diagnostics.AddBackend(entry);
        }
        Assert.Equal(0, diagnostics.HistoryCursor);
        diagnostics.MergeBackend(store.Recent(workspace, diagnostics.HistoryCursor, 100));
        Assert.Equal(next.Id, diagnostics.BackendInstanceId);
        Assert.Equal(20, diagnostics.BackendEvents.Count);
        Assert.All(diagnostics.BackendEvents, e => Assert.Equal(next.Id, e.BackendInstanceId));
        diagnostics.MergeBackend(store.Recent(workspace, diagnostics.HistoryCursor, 100));
        Assert.Equal(20, diagnostics.BackendEvents.Count);
        Assert.Equal(20, diagnostics.HistoryCursor);
    }

    [Fact]
    public void Overlap_out_of_order_retention_and_workspace_switch_remain_bounded()
    {
        var instance = new BackendInstance(); var workspace = Guid.NewGuid();
        var store = new BackendDiagnosticStore(instance); var diagnostics = new DesktopDiagnostics();
        diagnostics.SetBackendIdentity(workspace, instance.Id);
        BackendDiagnosticEvent? firstLive = null;
        for (var i = 1; i <= 260; i++)
        {
            var entry = store.Add(workspace, "Information", "Backend", "Sequence", $"Event {i}.", null);
            if (i == 259) firstLive = entry;
        }
        diagnostics.AddBackend(firstLive!);
        diagnostics.AddBackend(store.Recent(workspace, 0, 100).Events[^1]);
        diagnostics.MergeBackend(store.Recent(workspace, diagnostics.HistoryCursor, 100));
        diagnostics.MergeBackend(store.Recent(workspace, diagnostics.HistoryCursor, 100));
        Assert.Equal(100, diagnostics.BackendEvents.Count);
        Assert.Equal(260, diagnostics.HistoryCursor);
        Assert.Contains("gap", diagnostics.BackendNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(260, diagnostics.BackendEvents[0].Sequence);
        var otherWorkspace = Guid.NewGuid();
        diagnostics.SetBackendIdentity(otherWorkspace, Guid.NewGuid());
        diagnostics.AddBackend(firstLive!);
        Assert.Empty(diagnostics.BackendEvents);
        Assert.Equal(0, diagnostics.HistoryCursor);
        Assert.Equal(0, diagnostics.LastBackendSequence);
    }

    [Fact]
    public void Authoritative_updates_preserve_draft_and_clear_private_state_after_identity_change()
    {
        using var state = new MainViewModel(new BackendClient(new HttpClient(new RejectHandler()), new Connection()),
            NullLogger<MainViewModel>.Instance);
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var profile = Guid.NewGuid(); var instance = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(Snapshot(user, workspace, profile, instance, "First"), "http://127.0.0.1:5274");
        Assert.True(state.CanEdit);
        state.WorkspaceName = "Unsaved draft";
        state.ApplyRealtimeSnapshot(Snapshot(user, workspace, profile, instance, "Changed elsewhere"), "http://127.0.0.1:5274");
        Assert.Equal("Unsaved draft", state.WorkspaceName);
        Assert.Contains("changed", state.ServerChangeNotice, StringComparison.OrdinalIgnoreCase);
        state.SetRealtimeStatus("Disconnected", "Transport lost.");
        Assert.True(state.IsStale); Assert.Equal("Unsaved draft", state.WorkspaceName);
        state.ApplyRealtimeSnapshot(Snapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Other"),
            "http://127.0.0.1:5274");
        Assert.Equal("Other", state.WorkspaceName);
        state.SetRealtimeStatus("AuthorizationDenied", "Access denied.");
        Assert.False(state.HasSnapshot); Assert.Equal("Unavailable", state.VisibleUserId);
        Assert.Equal("", state.WorkspaceName);
    }

    [Fact]
    public async Task Access_denial_clears_private_state_and_rejects_queued_old_ui_updates()
    {
        var diagnostics = new DesktopDiagnostics(); var dispatcher = new QueuedDispatcher();
        using var state = new MainViewModel(new BackendClient(new HttpClient(new RejectHandler()), new Connection()),
            NullLogger<MainViewModel>.Instance, diagnostics);
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var profile = Guid.NewGuid(); var instance = Guid.NewGuid();
        var snapshot = Snapshot(user, workspace, profile, instance, "Private workspace");
        state.ApplyRealtimeSnapshot(snapshot, "http://127.0.0.1:5274");
        diagnostics.SetBackendIdentity(workspace, instance);
        var entry = new BackendDiagnosticEvent(instance, 1, DateTimeOffset.UtcNow, "Information", "Backend", "Private",
            "Private event.", workspace, null);
        diagnostics.AddBackend(entry);
        diagnostics.MergeBackend(new(instance, workspace, 1, 1, 0, false, [entry]));
        var generationCurrent = true;
        var queuedSnapshot = RealtimeSession.DispatchIfCurrentAsync(dispatcher, () => generationCurrent,
            () => state.ApplyRealtimeSnapshot(snapshot, "http://127.0.0.1:5274"), default);
        var queuedEvent = RealtimeSession.DispatchIfCurrentAsync(dispatcher, () => generationCurrent,
            () => diagnostics.AddBackend(new(instance, 2, DateTimeOffset.UtcNow, "Warning", "Backend", "Private",
                "Delayed private event.", workspace, null)), default);
        generationCurrent = false; // the connection generation is invalidated before queued UI work runs
        state.SetRealtimeStatus("AuthorizationDenied", "Access denied.");
        dispatcher.Drain();
        await Task.WhenAll(queuedSnapshot, queuedEvent);
        Assert.False(state.HasSnapshot); Assert.Equal("", state.WorkspaceName);
        Assert.Equal("Unavailable", state.BackendInstance); Assert.Null(state.LastSuccessfulRefresh);
        Assert.Empty(diagnostics.BackendEvents); Assert.Equal(0, diagnostics.HistoryCursor);
        diagnostics.AddBackend(entry);
        Assert.Empty(diagnostics.BackendEvents);
        diagnostics.Record("Information", "Safe desktop-local event.");
        Assert.Single(diagnostics.Events);
    }

    [Fact]
    public async Task Delayed_save_result_cannot_mutate_new_identity_or_revoked_state()
    {
        var handler = new PendingWriteHandler();
        using var http = new HttpClient(handler);
        using var state = new MainViewModel(new BackendClient(http, new FixedConnection()),
            NullLogger<MainViewModel>.Instance, new DesktopDiagnostics());
        var workspaceA = Guid.NewGuid(); var instanceA = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(Snapshot(Guid.NewGuid(), workspaceA, Guid.NewGuid(), instanceA, "First"),
            "http://127.0.0.1:5274");
        state.WorkspaceName = "Submitted";
        var save = state.SaveCommand.ExecuteAsync(null);
        var workspaceB = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(Snapshot(Guid.NewGuid(), workspaceB, Guid.NewGuid(), Guid.NewGuid(), "Second"),
            "http://127.0.0.1:5274");
        handler.Response.SetResult(new(HttpStatusCode.OK)
        { Content = System.Net.Http.Json.JsonContent.Create(new WorkspaceSettingsResponse(workspaceA, "Submitted")) });
        await save;
        Assert.Equal("Second", state.WorkspaceName);
        Assert.Equal(workspaceB.ToString(), state.WorkspaceIdentifier);
        Assert.Equal("", state.WorkspaceFeedback);

        var denied = new PendingWriteHandler();
        using var deniedHttp = new HttpClient(denied);
        using var deniedState = new MainViewModel(new BackendClient(deniedHttp, new FixedConnection()),
            NullLogger<MainViewModel>.Instance, new DesktopDiagnostics());
        deniedState.ApplyRealtimeSnapshot(Snapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Private"),
            "http://127.0.0.1:5274");
        deniedState.WorkspaceName = "Submitted";
        var deniedSave = deniedState.SaveCommand.ExecuteAsync(null);
        deniedState.SetRealtimeStatus("AuthenticationFailed", "Credential invalid.");
        denied.Response.SetResult(new(HttpStatusCode.OK)
        { Content = System.Net.Http.Json.JsonContent.Create(new WorkspaceSettingsResponse(workspaceA, "Submitted")) });
        await deniedSave;
        Assert.False(deniedState.HasSnapshot); Assert.Equal("", deniedState.WorkspaceName);
        Assert.Equal("", deniedState.WorkspaceFeedback);
    }

    private static ApplicationSnapshotResponse Snapshot(Guid user, Guid workspace, Guid profile, Guid instance, string name) =>
        new(1, instance, profile, DateTimeOffset.UtcNow,
            new(user, workspace, "Local", Capabilities.Phase01A),
            new("1.0", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, name), [new("Polymarket", "NotImplemented"), new("Kalshi", "NotImplemented")]);
}
