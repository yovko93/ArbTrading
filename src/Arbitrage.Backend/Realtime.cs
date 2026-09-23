using System.Security.Claims;
using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Arbitrage.Backend;

public sealed class BackendInstance
{
    public Guid Id { get; } = Guid.NewGuid();
}

public static class WorkspaceGroup
{
    public static string Name(Guid workspaceId) => $"workspace:{workspaceId:N}";
}

[Authorize]
public sealed class ApplicationHub(ILocalProfileStore profiles, WorkspaceService workspaces, BackendInstance instance) : Hub
{
    // The server resolves the only local profile and checks membership on every connection.
    public async Task<WorkspaceSubscriptionResponse> SubscribeDefaultWorkspace()
    {
        var profile = await profiles.GetAsync(Context.ConnectionAborted);
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor != profile.UserId)
            throw new HubException("Workspace access denied.");
        var result = await workspaces.ReadAsync(profile.DefaultWorkspaceId, Context.ConnectionAborted);
        if (!result.IsSuccess) throw new HubException("Workspace access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceGroup.Name(profile.DefaultWorkspaceId), Context.ConnectionAborted);
        return new(instance.Id, profile.DefaultWorkspaceId, DateTimeOffset.UtcNow);
    }
}

public sealed class BackendDiagnosticStore(BackendInstance instance)
{
    public const int Retention = 256;
    private sealed class Feed
    {
        public long NextSequence = 1;
        public long Dropped;
        public Queue<BackendDiagnosticEvent> Events { get; } = new();
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Feed> feeds = new();

    public BackendDiagnosticEvent Add(Guid workspaceId, string severity, string source, string code, string message, string? correlationId)
    {
        if (severity is not ("Information" or "Warning" or "Error") ||
            source is not ("Backend" or "Workspace" or "Realtime" or "Runtime") ||
            code.Length > 48 || code.Any(char.IsControl) || message.Length > 160 || message.Any(char.IsControl) ||
            correlationId is { Length: > 64 } || correlationId?.Any(char.IsControl) == true)
            throw new ArgumentException("Unsafe diagnostic event.");
        lock (gate)
        {
            var feed = GetFeed(workspaceId);
            var entry = new BackendDiagnosticEvent(instance.Id, feed.NextSequence++, DateTimeOffset.UtcNow,
                severity, source, code, message, workspaceId, correlationId);
            feed.Events.Enqueue(entry);
            while (feed.Events.Count > Retention) feed.Events.Dequeue();
            return entry;
        }
    }

    public void CountDropped(Guid workspaceId)
    {
        lock (gate) GetFeed(workspaceId).Dropped++;
    }

    public RecentDiagnosticsResponse Recent(Guid workspaceId, long after, int take)
    {
        if (after < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException();
        lock (gate)
        {
            var feed = GetFeed(workspaceId);
            var oldest = feed.Events.TryPeek(out var first) ? first.Sequence : feed.NextSequence;
            var latest = feed.NextSequence - 1;
            var available = feed.Events.Where(e => e.Sequence > after).ToArray();
            var gap = (after > 0 && (after < oldest - 1 || after > latest)) || available.Length > take;
            var page = available.Length > take ? available[^take..] : available;
            return new(instance.Id, workspaceId, oldest, latest, feed.Dropped, gap,
                page);
        }
    }

    private Feed GetFeed(Guid workspaceId)
    {
        if (!feeds.TryGetValue(workspaceId, out var feed)) feeds[workspaceId] = feed = new Feed();
        return feed;
    }
}

public sealed class RealtimePublisher(BackendDiagnosticStore diagnostics, BackendInstance instance)
{
    public sealed record Dispatch(Guid WorkspaceId, StateInvalidation? Invalidation = null,
        BackendDiagnosticEvent? Diagnostic = null, ApplicationHeartbeat? Heartbeat = null,
        CatalogInvalidation? Catalog = null, OrderBookInvalidation? OrderBook = null, MonitoringInvalidation? Monitoring = null);
    private readonly Channel<Dispatch> queue = Channel.CreateBounded<Dispatch>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    public ChannelReader<Dispatch> Reader => queue.Reader;
    public void PaperChanged(Guid workspace)
    {
        foreach (var kind in new[] { "PaperAccountChanged", "PaperPositionsChanged", "PaperExecutionChanged" })
            Enqueue(new(workspace, new(instance.Id, workspace, kind)));
    }

    public void WorkspaceChanged(Guid instanceId, Guid workspaceId, string? correlationId)
    {
        Enqueue(new(workspaceId, new(instanceId, workspaceId, "WorkspaceSettings")));
        Diagnostic(workspaceId, "Information", "Workspace", "WorkspaceSettingsChanged",
            "Workspace display name changed.", correlationId);
    }

    public void Diagnostic(Guid workspaceId, string severity, string source, string code, string message, string? correlationId = null)
    {
        var entry = diagnostics.Add(workspaceId, severity, source, code, message, correlationId);
        Enqueue(new(workspaceId, Diagnostic: entry));
    }

    public void Heartbeat(Guid workspaceId, ApplicationHeartbeat heartbeat) => Enqueue(new(workspaceId, Heartbeat: heartbeat));
    public void MonitoringChanged(Guid workspace, long generation, Guid? alertId = null) => Enqueue(new(workspace, Monitoring: new(instance.Id, workspace, generation, alertId)));
    public void CatalogChanged(Guid workspaceId, string exchange) =>
        Enqueue(new(workspaceId, Catalog: new(instance.Id, workspaceId, exchange)));
    public void OrderBookChanged(Guid workspace, Arbitrage.Domain.OrderBookInstrumentId id, CachedOrderBook book) =>
        Enqueue(new(workspace, OrderBook: new(instance.Id, workspace,
            new(id.Exchange, id.NativeMarketId, id.NativeInstrumentId, id.Outcome), book.Version,
            book.Realtime?.Generation ?? 0, book.Source.ToString(), book.Realtime?.State.ToString() ?? "NotSubscribed")));
    private void Enqueue(Dispatch dispatch)
    {
        if (!queue.Writer.TryWrite(dispatch) && dispatch.Diagnostic is not null)
            diagnostics.CountDropped(dispatch.WorkspaceId);
        // State invalidations coalesce through the client's periodic authoritative refresh.
    }
}

public sealed class RealtimeDispatchService(RealtimePublisher publisher, IHubContext<ApplicationHub> hub,
    ILogger<RealtimeDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in publisher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var clients = hub.Clients.Group(WorkspaceGroup.Name(item.WorkspaceId));
                if (item.Invalidation is not null) await clients.SendAsync("StateInvalidated", item.Invalidation, stoppingToken);
                if (item.Diagnostic is not null) await clients.SendAsync("BackendDiagnostic", item.Diagnostic, stoppingToken);
                if (item.Heartbeat is not null) await clients.SendAsync("ApplicationHeartbeat", item.Heartbeat, stoppingToken);
                if (item.OrderBook is not null) await clients.SendAsync("OrderBookInvalidated", item.OrderBook, stoppingToken);
                if (item.Catalog is not null) await clients.SendAsync("CatalogInvalidated", item.Catalog, stoppingToken);
                if (item.Monitoring is not null) await clients.SendAsync("MonitoringChanged", item.Monitoring, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning("Realtime delivery failed: {ErrorType}", exception.GetType().Name); }
        }
    }
}

public sealed class ApplicationHeartbeatService(IServiceScopeFactory scopeFactory, BackendInstance instance,
    RealtimePublisher publisher, LocalOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var profiles = scope.ServiceProvider.GetRequiredService<ILocalProfileStore>();
                var profile = await profiles.GetAsync(stoppingToken);
                var persistence = await profiles.IsHealthyAsync(stoppingToken) ? "Healthy" : "Unavailable";
                publisher.Heartbeat(profile.DefaultWorkspaceId, new(instance.Id, DateTimeOffset.UtcNow, persistence));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed health probe is not a healthy heartbeat.
                _ = exception.GetType();
            }
        }
    }
}

public sealed class LocalShutdownCoordinator(IHostApplicationLifetime lifetime, BackendInstance instance)
{
    private int requested;
    public bool IsRequested => Volatile.Read(ref requested) != 0;
    public StopLocalRuntimeResponse Accept(HttpContext context)
    {
        if (Interlocked.Exchange(ref requested, 1) == 0)
            context.Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
        return new(instance.Id, "StopRequested");
    }
}
