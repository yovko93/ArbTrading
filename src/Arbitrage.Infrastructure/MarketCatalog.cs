using System.Text.Json;
using Arbitrage.Application;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class MarketCatalogEntry
{
    public string Exchange { get; set; } = "";
    public string Environment { get; set; } = "Production";
    public string NativeId { get; set; } = "";
    public string? EventId { get; set; }
    public string? SeriesId { get; set; }
    public string? GroupId { get; set; }
    public string? Classification { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Category { get; set; }
    public string? PrimaryTag { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string? NativeStatus { get; set; }
    public string Status { get; set; } = "Unknown";
    public string OutcomesJson { get; set; } = "[]";
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? OpenAt { get; set; }
    public DateTimeOffset? CloseAt { get; set; }
    public DateTimeOffset? ExpectedResolutionAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? SourceUpdatedAt { get; set; }
    public string? Description { get; set; }
    public string? Rules { get; set; }
    public string? SourceReference { get; set; }
    public DateTimeOffset FirstRetrievedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public Guid LastSeenRunId { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public bool IsIncomplete { get; set; }
}

public sealed class DiscoveryRunEntry
{
    public Guid Id { get; set; }
    public string Exchange { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? CurrentScope { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string State { get; set; } = "Running";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int Pages { get; set; }
    public int Observed { get; set; }
    public int Malformed { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
}

public sealed class MarketCatalogTag
{
    public string Exchange { get; set; } = "";
    public string NativeId { get; set; } = "";
    public string Tag { get; set; } = "";
}

public sealed record MarketCatalogQuery(string? Exchange, string? Search, string? Status,
    string? Tag, string Sort, int Page, int PageSize);
public sealed record MarketCatalogResult(MarketCatalogEntry[] Items, int Total);

public sealed class MarketCatalogStore(TradingDbContext db)
{
    public Task CreateRunAsync(DiscoveryRunEntry run, CancellationToken ct)
    {
        db.DiscoveryRuns.Add(run);
        return db.SaveChangesAsync(ct);
    }

    // A short transaction per page; never hold a database transaction across network requests.
    public async Task<int> UpsertPageAsync(Guid runId, string scope, IReadOnlyList<DiscoveredMarket> markets,
        int malformed, CancellationToken ct)
    {
        var run = await db.DiscoveryRuns.SingleAsync(r => r.Id == runId, ct);
        if (run.State != "Running") return 0;
        var unique = markets.Where(m => !string.IsNullOrWhiteSpace(m.NativeId))
            .GroupBy(m => (m.Exchange, m.NativeId)).Select(g => g.Last()).ToArray();
        var ids = unique.Select(m => m.NativeId).ToArray();
        var existing = await db.CatalogMarkets.Where(m => m.Exchange == run.Exchange && ids.Contains(m.NativeId))
            .ToDictionaryAsync(m => m.NativeId, ct);
        var oldTags = await db.CatalogTags.Where(t => t.Exchange == run.Exchange && ids.Contains(t.NativeId)).ToArrayAsync(ct);
        var observed = 0;
        foreach (var source in unique)
        {
            if (!existing.TryGetValue(source.NativeId, out var entry))
            {
                entry = new MarketCatalogEntry { Exchange = source.Exchange, NativeId = source.NativeId,
                    FirstRetrievedAt = source.RetrievedAt };
                db.CatalogMarkets.Add(entry);
            }
            if (entry.LastSeenRunId != runId) observed++;
            entry.Environment = source.Environment; entry.EventId = source.EventId;
            entry.SeriesId = source.SeriesId; entry.GroupId = source.GroupId;
            entry.Classification = source.Classification; entry.Title = source.Title;
            entry.Subtitle = source.Subtitle; entry.Category = source.Category;
            entry.PrimaryTag = source.Tags.FirstOrDefault();
            entry.TagsJson = JsonSerializer.Serialize(source.Tags);
            var currentTags = source.Tags.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 100)
                .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var storedTags = oldTags.Where(t => t.NativeId == source.NativeId).ToArray();
            foreach (var old in storedTags.Where(t => !currentTags.Contains(t.Tag))) db.CatalogTags.Remove(old);
            foreach (var tag in currentTags.Where(t => !storedTags.Any(old => old.Tag == t)))
                db.CatalogTags.Add(new MarketCatalogTag { Exchange = source.Exchange,
                    NativeId = source.NativeId, Tag = tag });
            entry.NativeStatus = source.NativeStatus; entry.Status = source.Status;
            entry.OutcomesJson = JsonSerializer.Serialize(source.Outcomes);
            entry.CreatedAt = source.CreatedAt; entry.OpenAt = source.OpenAt;
            entry.CloseAt = source.CloseAt; entry.ExpectedResolutionAt = source.ExpectedResolutionAt;
            entry.ResolvedAt = source.ResolvedAt; entry.SourceUpdatedAt = source.SourceUpdatedAt;
            entry.Description = source.Description; entry.Rules = source.Rules;
            entry.SourceReference = source.SourceReference; entry.RetrievedAt = source.RetrievedAt;
            entry.LastSeenRunId = runId; entry.WarningsJson = JsonSerializer.Serialize(source.Warnings);
            entry.IsIncomplete = source.IsIncomplete;
        }
        run.CurrentScope = scope; run.Pages++; run.Observed += observed; run.Malformed += malformed;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return observed;
    }

    public async Task FinishRunAsync(Guid runId, string state, string? errorCode,
        DateTimeOffset? retryAt, CancellationToken ct)
    {
        var run = await db.DiscoveryRuns.SingleAsync(r => r.Id == runId, ct);
        if (run.State != "Running") return;
        run.State = state; run.EndedAt = DateTimeOffset.UtcNow;
        run.ErrorCode = errorCode; run.RetryAt = retryAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task InterruptOldRunsAsync(CancellationToken ct)
    {
        var running = await db.DiscoveryRuns.Where(r => r.State == "Running").ToArrayAsync(ct);
        foreach (var run in running) { run.State = "Interrupted"; run.EndedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public Task<DiscoveryRunEntry?> GetRunAsync(Guid id, CancellationToken ct) =>
        db.DiscoveryRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
    public Task<DiscoveryRunEntry?> LatestOwnedRunAsync(string exchange, Guid owner, Guid workspace, CancellationToken ct) =>
        db.DiscoveryRuns.AsNoTracking().Where(r => r.Exchange == exchange &&
            r.OwnerUserId == owner && r.WorkspaceId == workspace)
            .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
    public Task<DiscoveryRunEntry?> LastCompleteAsync(string exchange, CancellationToken ct) =>
        db.DiscoveryRuns.AsNoTracking().Where(r => r.Exchange == exchange && r.State == "Complete")
            .OrderByDescending(r => r.EndedAt).FirstOrDefaultAsync(ct);
    public Task<int> CountAsync(string? exchange, CancellationToken ct) =>
        db.CatalogMarkets.CountAsync(m => exchange == null || m.Exchange == exchange, ct);
    public Task<DateTimeOffset?> LatestRetrievedAsync(string exchange, CancellationToken ct) =>
        db.CatalogMarkets.AsNoTracking().Where(m => m.Exchange == exchange)
            .OrderByDescending(m => m.RetrievedAt).Select(m => (DateTimeOffset?)m.RetrievedAt)
            .FirstOrDefaultAsync(ct);
    public Task<MarketCatalogEntry?> FindAsync(string exchange, string nativeId, CancellationToken ct) =>
        db.CatalogMarkets.AsNoTracking().SingleOrDefaultAsync(m => m.Exchange == exchange && m.NativeId == nativeId, ct);
    public async Task<MarketCatalogResult> QueryAsync(MarketCatalogQuery query, CancellationToken ct)
    {
        var rows = db.CatalogMarkets.AsNoTracking().AsQueryable();
        if (query.Exchange is not null) rows = rows.Where(m => m.Exchange == query.Exchange);
        if (query.Search is { Length: > 0 } search) rows = rows.Where(m =>
            m.NativeId.Contains(search) || (m.Title != null && m.Title.Contains(search)));
        if (query.Status is not null) rows = rows.Where(m => m.Status == query.Status);
        if (query.Tag is not null) rows = rows.Where(m => m.Category == query.Tag ||
            db.CatalogTags.Any(t => t.Exchange == m.Exchange && t.NativeId == m.NativeId && t.Tag == query.Tag));
        var total = await rows.CountAsync(ct);
        rows = query.Sort switch
        {
            "closing" => rows.OrderBy(m => m.CloseAt == null).ThenBy(m => m.CloseAt).ThenBy(m => m.Exchange).ThenBy(m => m.NativeId),
            "retrieved" => rows.OrderByDescending(m => m.RetrievedAt).ThenBy(m => m.Exchange).ThenBy(m => m.NativeId),
            _ => rows.OrderBy(m => m.Title).ThenBy(m => m.Exchange).ThenBy(m => m.NativeId)
        };
        return new(await rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(ct), total);
    }
    public Task<string[]> TagsAsync(CancellationToken ct) =>
        db.CatalogTags.AsNoTracking().Select(t => t.Tag)
            .Distinct().OrderBy(s => s).ToArrayAsync(ct);
}
