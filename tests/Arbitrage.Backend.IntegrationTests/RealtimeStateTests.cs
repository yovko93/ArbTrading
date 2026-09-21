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

    private static ApplicationSnapshotResponse Snapshot(Guid user, Guid workspace, Guid profile, Guid instance, string name) =>
        new(1, instance, profile, DateTimeOffset.UtcNow,
            new(user, workspace, "Local", Capabilities.Phase01A),
            new("1.0", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, name), [new("Polymarket", "NotImplemented"), new("Kalshi", "NotImplemented")]);
}
