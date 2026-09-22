using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Arbitrage.Backend;

public static class MarketCatalogEndpoints
{
    private const string Scope = "All categories; Polymarket closed=false and Kalshi unopened/open/paused/closed non-finalized buckets. Cached metadata; no historical finalized backfill or atomic upstream snapshot.";

    public static void MapMarketCatalog(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/catalog");
        group.MapGet("/status", async (Guid workspaceId, WorkspaceService workspaces,
            MarketCatalogStore store, IRequestActor actor, CancellationToken ct) =>
        {
            if (!await Allowed(workspaces, workspaceId, ct)) return Results.StatusCode(403);
            var statuses = new List<ExchangeCatalogStatusResponse>();
            foreach (var exchange in new[] { "Polymarket", "Kalshi" })
            {
                var latest = actor.UserId is { } owner
                    ? await store.LatestOwnedRunAsync(exchange, owner, workspaceId, ct) : null;
                var complete = await store.LastCompleteAsync(exchange,
                    exchange == "Polymarket" ? MarketDiscoverySemantics.PolymarketScope : MarketDiscoverySemantics.KalshiScope, ct);
                statuses.Add(new(exchange, exchange == "Polymarket" ? "closed=false; all categories" :
                    "unopened + open + paused + closed; all categories", await store.CountAsync(exchange, ct),
                    complete?.EndedAt, await store.LatestRetrievedAsync(exchange, ct),
                    latest is null ? null : Run(latest), "Implemented",
                    latest?.State switch { "Complete" => "Succeeded", "Partial" => "Partial",
                        "Failed" => "Failed", "Cancelled" => "Cancelled", "Interrupted" => "Interrupted",
                        "Running" => "Running", _ => complete is null ? "Unverified" : "Succeeded" },
                    "NotImplemented", "Unavailable"));
            }
            return Results.Ok(new CatalogStatusResponse([.. statuses], Scope, DateTimeOffset.UtcNow));
        });
        group.MapGet("/markets", async (Guid workspaceId, string? exchange, string? search,
            string? status, string? tag, string? sort, int? page, int? pageSize,
            WorkspaceService workspaces, MarketCatalogStore store, CancellationToken ct) =>
        {
            if (!await Allowed(workspaces, workspaceId, ct)) return Results.StatusCode(403);
            if (exchange is not null and not ("Polymarket" or "Kalshi") ||
                search is { Length: > 100 } || tag is { Length: > 100 } ||
                status is not null and not ("Open" or "Upcoming" or "Paused" or "Closed" or "Determined" or "Disputed" or "Amended" or "OpenOrPaused" or "UpcomingOrPaused" or "Unknown" or "Finalized") ||
                sort is not null and not ("title" or "closing" or "retrieved") ||
                page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
                return Results.BadRequest();
            var query = new MarketCatalogQuery(exchange, search?.Trim(), status, tag,
                sort ?? "title", page ?? 1, pageSize ?? 30);
            var result = await store.QueryAsync(query, ct);
            return Results.Ok(new MarketPageResponse(result.Items.Select(Map).ToArray(), result.Total,
                query.Page, query.PageSize, Scope, await store.TagsAsync(ct)));
        });
        group.MapGet("/markets/{exchange}/{nativeId}", async (Guid workspaceId, string exchange,
            string nativeId, WorkspaceService workspaces, MarketCatalogStore store, CancellationToken ct) =>
        {
            if (!await Allowed(workspaces, workspaceId, ct)) return Results.StatusCode(403);
            if (exchange is not ("Polymarket" or "Kalshi") || nativeId.Length is < 1 or > 256 || nativeId.Any(char.IsControl))
                return Results.BadRequest();
            var market = await store.FindAsync(exchange, nativeId, ct);
            return market is null ? Results.NotFound() : Results.Ok(Map(market));
        });
        group.MapPost("/sync", async (Guid workspaceId, StartMarketSyncRequest request,
            WorkspaceService workspaces, ILocalProfileStore profiles, IRequestActor actor,
            MarketDiscoveryCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await Allowed(workspaces, workspaceId, ct)) return Results.StatusCode(403);
            var profile = await profiles.GetAsync(ct);
            if (actor.UserId != profile.UserId || profile.DefaultWorkspaceId != workspaceId)
                return Results.StatusCode(403);
            if (request.Exchange is not ("All" or "Polymarket" or "Kalshi")) return Results.BadRequest();
            try
            {
                var runs = await coordinator.StartRunsAsync(request.Exchange, profile.UserId, workspaceId, ct);
                return Results.Accepted(value: new StartMarketSyncResponse(runs.Select(Run).ToArray()));
            }
            catch (MarketSyncConflictException) { return Results.Conflict(); }
        });
        group.MapPost("/sync/{runId:guid}/cancel", async (Guid workspaceId, Guid runId,
            WorkspaceService workspaces, ILocalProfileStore profiles, IRequestActor actor,
            MarketDiscoveryCoordinator coordinator, CancellationToken ct) =>
        {
            if (!await Allowed(workspaces, workspaceId, ct)) return Results.StatusCode(403);
            var profile = await profiles.GetAsync(ct);
            if (actor.UserId != profile.UserId || profile.DefaultWorkspaceId != workspaceId)
                return Results.StatusCode(403);
            try
            {
                var run = await coordinator.CancelAsync(runId, profile.UserId, workspaceId, ct);
                return run is null ? Results.NotFound() : Results.Ok(Run(run));
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
    }

    private static async Task<bool> Allowed(WorkspaceService workspaces, Guid workspaceId, CancellationToken ct) =>
        (await workspaces.ReadAsync(workspaceId, ct)).IsSuccess;
    private static DiscoveryRunResponse Run(DiscoveryRunEntry run) => new(run.Id, run.Exchange, run.Scope,
        run.CurrentScope, run.State, run.StartedAt, run.EndedAt, run.Pages, run.Observed,
        run.Malformed, run.ErrorCode, run.RetryAt);
    private static MarketResponse Map(MarketCatalogEntry m) => new(m.Exchange, m.Environment, m.NativeId,
        m.EventId, m.SeriesId, m.GroupId, m.Classification, m.Title, m.Subtitle, m.Category,
        JsonSerializer.Deserialize<string[]>(m.TagsJson) ?? [], m.NativeStatus, m.Status,
        JsonSerializer.Deserialize<MarketOutcomeResponse[]>(m.OutcomesJson) ?? [],
        m.CreatedAt, m.OpenAt, m.CloseAt, m.ExpectedResolutionAt, m.ResolvedAt,
        m.SourceUpdatedAt, m.Description, m.Rules, m.SourceReference, m.RetrievedAt,
        JsonSerializer.Deserialize<string[]>(m.WarningsJson) ?? [], m.IsIncomplete);
}
