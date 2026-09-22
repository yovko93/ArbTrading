using System.Collections.Concurrent;
using Arbitrage.Application;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public sealed class MarketSyncConflictException : Exception { }

// Backend-owned workers outlive their submitting HTTP requests and settle on host shutdown.
public sealed class MarketDiscoveryCoordinator(IServiceScopeFactory scopes,
    IEnumerable<IMarketDiscoverySource> sources, RealtimePublisher publisher,
    LocalOptions options, ILogger<MarketDiscoveryCoordinator> logger) : IHostedService
{
    private sealed class ActiveRun(DiscoveryRunEntry run, CancellationTokenSource cancellation)
    {
        public DiscoveryRunEntry Run { get; } = run;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Worker { get; set; } = Task.CompletedTask;
    }
    private readonly Dictionary<string, IMarketDiscoverySource> sourceMap = sources.ToDictionary(s => s.Exchange, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRun> active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, long> lastNotifications = new(StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MarketCatalogStore>().InterruptOldRunsAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        lifetime.Cancel();
        var jobs = active.Values.ToArray();
        foreach (var job in jobs)
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        await Task.WhenAll(jobs.Select(j => j.Worker)).WaitAsync(ct);
    }

    public async Task<DiscoveryRunEntry[]> StartRunsAsync(string exchange, Guid owner, Guid workspace, CancellationToken ct)
    {
        var selected = exchange == "All" ? sourceMap.Values.OrderBy(s => s.Exchange).ToArray() :
            sourceMap.TryGetValue(exchange, out var one) ? [one] : [];
        if (selected.Length == 0) throw new ArgumentException("Unknown exchange.", nameof(exchange));
        await admission.WaitAsync(ct);
        try
        {
            var admitted = new List<DiscoveryRunEntry>();
            foreach (var source in selected)
            {
                if (active.TryGetValue(source.Exchange, out var old))
                {
                    if (old.Run.OwnerUserId != owner || old.Run.WorkspaceId != workspace)
                        throw new MarketSyncConflictException();
                    admitted.Add(old.Run); continue;
                }
                var run = new DiscoveryRunEntry { Id = Guid.NewGuid(), Exchange = source.Exchange,
                    Scope = source.Exchange == "Polymarket" ? "All categories; closed=false" : "All categories; unopened+open+paused",
                    OwnerUserId = owner, WorkspaceId = workspace, StartedAt = DateTimeOffset.UtcNow };
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MarketCatalogStore>().CreateRunAsync(run, ct);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var job = new ActiveRun(run, cancellation);
                if (!active.TryAdd(source.Exchange, job)) throw new InvalidOperationException("Duplicate discovery admission.");
                job.Worker = Task.Run(() => RunAsync(source, job));
                admitted.Add(run);
            }
            return [.. admitted];
        }
        finally { admission.Release(); }
    }

    public async Task<DiscoveryRunEntry?> CancelAsync(Guid runId, Guid owner, Guid workspace, CancellationToken ct)
    {
        var job = active.Values.FirstOrDefault(j => j.Run.Id == runId);
        if (job is null) return null;
        if (job.Run.OwnerUserId != owner || job.Run.WorkspaceId != workspace) throw new UnauthorizedAccessException();
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        await job.Worker.WaitAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MarketCatalogStore>().GetRunAsync(runId, ct);
    }

    private async Task RunAsync(IMarketDiscoverySource source, ActiveRun job)
    {
        var run = job.Run;
        var deadline = run.StartedAt.AddMinutes(options.DiscoveryRunMinutes);
        var state = "Complete"; string? error = null; DateTimeOffset? retryAt = null;
        try
        {
            foreach (var scopeName in source.Scopes)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                var stalled = 0;
                while (true)
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= deadline) throw new MarketDiscoveryException("BudgetExceeded", "Discovery budget reached.");
                    var page = await source.ReadPageAsync(scopeName, cursor, options.DiscoveryPageSize,
                        deadline, job.Cancellation.Token);
                    // The page is bounded; its short commit is allowed to finish during cancellation.
                    await using (var dbScope = scopes.CreateAsyncScope())
                    {
                        var added = await dbScope.ServiceProvider.GetRequiredService<MarketCatalogStore>()
                            .UpsertPageAsync(run.Id, scopeName, page.Markets, page.MalformedRecords, CancellationToken.None);
                        stalled = added == 0 && page.NextCursor is not null ? stalled + 1 : 0;
                    }
                    Notify(run.WorkspaceId, run.Exchange);
                    if (page.NextCursor is null or "") break;
                    if (!visited.Add(page.NextCursor) || page.NextCursor == cursor || stalled >= 3)
                        throw new MarketDiscoveryException("CursorStalled", "Public market pagination stopped progressing.");
                    cursor = page.NextCursor;
                }
            }
            if (job.Cancellation.IsCancellationRequested) state = "Cancelled";
        }
        catch (OperationCanceledException) { state = "Cancelled"; }
        catch (MarketDiscoveryException exception)
        {
            state = exception.Code is "BudgetExceeded" or "RateLimited" ? "Partial" : "Failed";
            error = exception.Code; retryAt = exception.RetryAt;
            logger.LogWarning("{Exchange} discovery ended: {Code}", source.Exchange, exception.Code);
        }
        catch (Exception exception)
        {
            state = "Failed"; error = "InternalFailure";
            logger.LogError("{Exchange} discovery failed: {ErrorType}", source.Exchange, exception.GetType().Name);
        }
        finally
        {
            if (job.Cancellation.IsCancellationRequested) state = "Cancelled";
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MarketCatalogStore>()
                    .FinishRunAsync(run.Id, state, error, retryAt, CancellationToken.None);
                Notify(run.WorkspaceId, run.Exchange, force: true);
            }
            catch (Exception exception)
            { logger.LogError("Discovery run outcome could not be stored: {ErrorType}", exception.GetType().Name); }
            active.TryRemove(source.Exchange, out _);
            job.Cancellation.Dispose();
        }
    }

    private void Notify(Guid workspace, string exchange, bool force = false)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (!force)
        {
            var prior = lastNotifications.GetOrAdd(exchange, 0);
            if (now - prior < TimeSpan.FromSeconds(2).Ticks ||
                !lastNotifications.TryUpdate(exchange, now, prior)) return;
        }
        else lastNotifications[exchange] = now;
        publisher.CatalogChanged(workspace, exchange);
    }
}
