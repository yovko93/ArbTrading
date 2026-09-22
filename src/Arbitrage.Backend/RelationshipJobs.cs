using System.Text.Json;
using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend;

// One bounded backend-owned job at a time. Request disconnection does not cancel an admitted job.
public sealed class RelationshipJobs(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    private readonly Channel<Guid> queue = Channel.CreateBounded<Guid>(1);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Guid? activeId;
    private CancellationTokenSource? activeCancellation;
    private bool cancelRequested;
    public async Task<RelationshipJobEntry> StartAsync(Guid actor, Guid workspace, GenerateRelationshipsRequest request, CancellationToken ct)
    {
        await ready.Task.WaitAsync(ct);
        if (!await admission.WaitAsync(0, ct)) throw new RelationshipConflictException();
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(actor, workspace, true, ct);
            var row = new RelationshipJobEntry { ActorId = actor, WorkspaceId = workspace, StartedAt = clock.GetUtcNow(), RequestJson = JsonSerializer.Serialize(request) };
            db.RelationshipJobs.Add(row);
            db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), row.Id.ToString(), "RelationshipCandidateGenerationStarted",
                JsonSerializer.Serialize(new { JobId = row.Id, PolicyVersion = RelationshipPolicy.Version, request.Exchange, request.NativeId, request.EventId })));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            lock (gate) { activeId = row.Id; cancelRequested = false; }
            if (!queue.Writer.TryWrite(row.Id)) throw new InvalidOperationException("Generation worker unavailable.");
            return row;
        }
        catch { admission.Release(); throw; }
    }
    public async Task<RelationshipJobEntry?> CancelAsync(Guid actor, Guid workspace, Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(actor, workspace, true, ct);
        var row = await db.RelationshipJobs.SingleOrDefaultAsync(j => j.Id == id && j.WorkspaceId == workspace, ct);
        if (row is null) return null;
        lock (gate) if (activeId == id) { cancelRequested = true; activeCancellation?.Cancel(); }
        await tx.CommitAsync(ct); return row;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initializer has already migrated before the host starts hosted services.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.RelationshipJobs.Where(j => j.State == "Running").ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, "Interrupted").SetProperty(j => j.EndedAt, clock.GetUtcNow()), stoppingToken);
        }
        ready.TrySetResult();
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (gate) { activeCancellation = cancel; if (cancelRequested) cancel.Cancel(); }
            try { await RunAsync(id, cancel.Token); }
            finally { lock (gate) { activeId = null; activeCancellation = null; } admission.Release(); }
        }
    }
    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<RelationshipStore>();
        var job = await db.RelationshipJobs.SingleAsync(j => j.Id == id, CancellationToken.None);
        var request = JsonSerializer.Deserialize<GenerateRelationshipsRequest>(job.RequestJson)!;
        var bounds = new CandidateBounds(request.MaximumSources, request.PerSource, request.MaximumComparisons, request.RuntimeSeconds);
        var start = clock.GetTimestamp();
        try
        {
            await store.RequireMemberAsync(job.ActorId, job.WorkspaceId, true, ct);
            var query = db.CatalogMarkets.AsNoTracking().Where(m => m.Status == "Open" || m.Status == "Upcoming" || m.Status == "Paused" || m.Status == "OpenOrPaused" || m.Status == "UpcomingOrPaused");
            if (request.EventId is not null) query = query.Where(m => m.Exchange == request.Exchange && (m.EventId == request.EventId || m.GroupId == request.EventId));
            else if (!request.CrossExchange && request.Exchange is not null) query = query.Where(m => m.Exchange == request.Exchange);
            var catalog = await query.OrderByDescending(m => m.RetrievedAt).ThenBy(m => m.Exchange).ThenBy(m => m.NativeId).Take(bounds.MaximumSources + 1).ToListAsync(ct);
            var sourceWindowTruncated = catalog.Count > bounds.MaximumSources;
            MarketIdentity? selected = request.NativeId is null ? null : new(request.Exchange!, request.NativeId);
            if (selected is not null)
            {
                var market = await db.CatalogMarkets.AsNoTracking().SingleOrDefaultAsync(m => m.Exchange == selected.Exchange && m.NativeId == selected.NativeId, ct);
                if (market is null) throw new ArgumentException("Selected market unavailable.");
                if (!catalog.Any(m => m.Exchange == selected.Exchange && m.NativeId == selected.NativeId) && catalog.Count >= bounds.MaximumSources) sourceWindowTruncated = true;
                catalog.RemoveAll(m => m.Exchange == selected.Exchange && m.NativeId == selected.NativeId);
                // Selected market must survive the source cap and deterministic identity ordering.
                catalog = catalog.Take(bounds.MaximumSources - 1).Prepend(market).ToList();
            }
            var batch = new CandidateGenerator(clock).Generate(catalog.Select(CatalogSemantics.Describe).ToArray(), bounds, request.CrossExchange, selected, ct);
            job.Sources = batch.Sources; job.Comparisons = batch.Comparisons;
            var partial = batch.Partial || sourceWindowTruncated;
            foreach (var pair in batch.Pairs)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(start).TotalSeconds >= bounds.RuntimeSeconds) { partial = true; break; }
                await store.GeneratePairAsync(job.ActorId, job.WorkspaceId, pair.A, pair.B, ct); job.Written++;
            }
            job.State = partial ? "Partial" : "Complete";
            job.Notice = partial ? "A source, posting, candidate, comparison, selected-window or runtime bound limited this run." : "Completed the bounded recent/open scope; not a proof of catalog-wide matching completeness.";
        }
        catch (OperationCanceledException) { job.State = "Cancelled"; job.Notice = "Cancelled; already persisted candidates remain available."; }
        catch (Exception e) when (e is not OutOfMemoryException) { job.State = "Failed"; job.Notice = "Generation unavailable; no approval was inferred."; }
        // Failed SaveChanges may leave modified relationship entities; only persist the job terminal state.
        db.ChangeTracker.Clear(); db.RelationshipJobs.Update(job); job.EndedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
