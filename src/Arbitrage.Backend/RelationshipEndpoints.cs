using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend;

public static class RelationshipEndpoints
{
    private static readonly SemaphoreSlim EnrichmentAdmission = new(1, 1);
    public static void MapRelationships(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/relationships");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (RelationshipConflictException) { return Results.Conflict(new { Message = "Sources changed or a generation job is active. Refresh before reviewing." }); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/", async (Guid workspaceId, string? exchange, string? targetExchange, string? state, string? type, int? page, int? pageSize,
            IRequestActor actor, RelationshipStore store, TradingDbContext db, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, false, ct);
            if (!ValidExchange(exchange) || !ValidExchange(targetExchange) || page is < 1 or > 100000 || pageSize is < 1 or > 100 ||
                state is not null && !Parse<VerificationState>(state, out _) || type is not null && !Parse<RelationshipType>(type, out _)) return Results.BadRequest();
            // Refresh before applying trust filters, so changed approved rows cannot hide from the Stale filter.
            await store.RefreshWorkspaceStalenessAsync(workspaceId, ct);
            var query = store.Query(workspaceId);
            if (exchange is not null) query = query.Where(r => r.SourceExchange == exchange || r.TargetExchange == exchange);
            if (targetExchange is not null) query = query.Where(r => r.SourceExchange == targetExchange || r.TargetExchange == targetExchange);
            if (state is not null) { var value = Enum.Parse<VerificationState>(state); query = query.Where(r => r.State == value); }
            if (type is not null) { var value = Enum.Parse<RelationshipType>(type); query = query.Where(r => r.Type == value); }
            var total = await query.CountAsync(ct); var p = page ?? 1; var size = pageSize ?? 30;
            var rows = await query.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id).Skip((p - 1) * size).Take(size).ToArrayAsync(ct);
            return Results.Ok(new RelationshipPageResponse(rows.Select(Summary).ToArray(), total, p, size));
        });
        group.MapGet("/{id:guid}", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store, CancellationToken ct) =>
        {
            var row = await store.ReadAsync(Actor(actor), workspaceId, id, ct);
            return row is null ? Results.NotFound() : Results.Ok(Detail(row));
        });
        group.MapPost("/{id:guid}/enrich", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store,
            TradingDbContext db, IRelationshipMetadataSource source, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
            var row = await store.ReadAsync(Actor(actor), workspaceId, id, ct);
            if (row is null) return Results.NotFound();
            if (!await EnrichmentAdmission.WaitAsync(0, ct)) return Results.Conflict();
            try
            {
            // At most two selected markets; no startup/navigation/catalog-triggered enrichment.
            foreach (var identity in new[] { new MarketIdentity(row.SourceExchange, row.SourceId), new MarketIdentity(row.TargetExchange, row.TargetId) })
            {
                var market = await db.CatalogMarkets.SingleAsync(m => m.Exchange == identity.Exchange && m.NativeId == identity.NativeId, ct);
                var baseline = RelationshipPolicy.Fingerprint(CatalogSemantics.BaseDescriptor(market));
                var metadata = await source.ReadAsync(identity.Exchange, identity.NativeId, market.EventId, ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
                await db.Entry(market).ReloadAsync(ct);
                if (baseline != RelationshipPolicy.Fingerprint(CatalogSemantics.BaseDescriptor(market))) throw new RelationshipConflictException();
                market.RelationshipMetadataJson = JsonSerializer.Serialize(metadata); market.RelationshipMetadataBaseFingerprint = baseline;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            return Results.Ok(Detail((await store.RevalidateAsync(Actor(actor), workspaceId, id, ct))!));
            }
            finally { EnrichmentAdmission.Release(); }
        });
        group.MapPost("/generate", async (Guid workspaceId, GenerateRelationshipsRequest request, IRequestActor actor, RelationshipStore store, RelationshipJobs jobs, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
            if (!ValidExchange(request.Exchange) || request.NativeId is { Length: < 1 or > 256 } || request.EventId is { Length: < 1 or > 256 } ||
                (request.NativeId is not null || request.EventId is not null) && request.Exchange is null || request.NativeId is not null && request.EventId is not null ||
                !new CandidateBounds(request.MaximumSources, request.PerSource, request.MaximumComparisons, request.RuntimeSeconds).Valid) return Results.BadRequest();
            return Results.Accepted(value: Job(await jobs.StartAsync(Actor(actor), workspaceId, request, ct)));
        });
        group.MapGet("/jobs/{id:guid}", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store, TradingDbContext db, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, false, ct);
            var job = await db.RelationshipJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.WorkspaceId == workspaceId, ct);
            return job is null ? Results.NotFound() : Results.Ok(Job(job));
        });
        group.MapPost("/jobs/{id:guid}/cancel", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipJobs jobs, CancellationToken ct) =>
        {
            var job = await jobs.CancelAsync(Actor(actor), workspaceId, id, ct);
            return job is null ? Results.NotFound() : Results.Ok(Job(job));
        });
        group.MapPost("/{id:guid}/revalidate", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store, CancellationToken ct) =>
        {
            var row = await store.RevalidateAsync(Actor(actor), workspaceId, id, ct);
            return row is null ? Results.NotFound() : Results.Ok(Detail(row));
        });
        foreach (var action in new[] { "verify", "reject" })
        {
            var reject = action == "reject";
            group.MapPost($"/{{id:guid}}/{action}", async (Guid workspaceId, Guid id, ReviewRelationshipRequest request, IRequestActor actor, RelationshipStore store, CancellationToken ct) =>
            {
                await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
                if (!request.Confirmed || !Parse<RelationshipType>(request.Type, out var type) || request.Mappings is null || request.Mappings.Length > 100 ||
                    request.Mappings.Any(m => !Parse<RelationshipType>(m.Type, out _)) || !Parse<TruthValue>(request.MutuallyExclusive, out var exclusive) ||
                    !Parse<TruthValue>(request.CollectivelyExhaustive, out var exhaustive)) return Results.BadRequest();
                var row = await store.ReviewAsync(Actor(actor), workspaceId, id, request.SourceFingerprint, request.TargetFingerprint, request.Reason, type,
                    request.Mappings.Select(m => new OutcomeMapping(m.SourceOutcomeId, m.TargetOutcomeId, Enum.Parse<RelationshipType>(m.Type))).ToArray(), reject, ct, exclusive, exhaustive);
                return row is null ? Results.NotFound() : Results.Ok(Detail(row));
            });
        }
    }
    private static Guid Actor(IRequestActor actor) => actor.UserId ?? throw new UnauthorizedAccessException();
    private static bool ValidExchange(string? value) => value is null or "Polymarket" or "Kalshi";
    private static bool Parse<T>(string value, out T parsed) where T : struct, Enum => Enum.TryParse(value, out parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value;
    public static RelationshipJobResponse Job(RelationshipJobEntry j) => new(j.Id, j.State, j.Sources, j.Comparisons, j.Written, j.StartedAt, j.EndedAt, j.Notice);
    public static RelationshipSummaryResponse Summary(MarketRelationshipEntry r) => new(r.Id, r.SourceExchange, r.SourceId,
        JsonSerializer.Deserialize<CanonicalMarketDescriptor>(r.SourceSnapshot)?.Title, r.TargetExchange, r.TargetId,
        JsonSerializer.Deserialize<CanonicalMarketDescriptor>(r.TargetSnapshot)?.Title, r.Type.ToString(), r.State.ToString(), r.LastValidatedAt, r.State == VerificationState.Stale);
    public static RelationshipDetailResponse Detail(MarketRelationshipEntry r) => new(Summary(r),
        Market(JsonSerializer.Deserialize<CanonicalMarketDescriptor>(r.SourceSnapshot)!), Market(JsonSerializer.Deserialize<CanonicalMarketDescriptor>(r.TargetSnapshot)!),
        r.SourceFingerprint, r.TargetFingerprint, r.PolicyVersion,
        r.Evidence.OrderByDescending(e => e.Blocking).ThenBy(e => e.Dimension).Select(e => new RelationshipEvidenceResponse(e.Kind.ToString(), e.Dimension, e.Detail, e.Blocking, e.Contradiction)).ToArray(),
        r.Mappings.Select(m => new RelationshipMappingResponse(m.SourceOutcomeId, m.TargetOutcomeId, m.Type.ToString())).ToArray(),
        JsonSerializer.Deserialize<string[]>(r.WarningsJson) ?? [], r.ReviewReason, r.ReviewerId, r.ReviewedAt, RelationshipPolicy.IsStrategyEligible(r.Type, r.State), r.MutuallyExclusive.ToString(), r.CollectivelyExhaustive.ToString());
    private static RelationshipMarketResponse Market(CanonicalMarketDescriptor m) => new(m.Identity.Exchange, m.Identity.NativeId, m.Title, m.RulesText,
        m.Description, m.ResolutionSource, m.NativeEventId, m.NativeSeriesId, m.NativeGroupId, m.MarketStructure, m.MarketOpen, m.MarketClose,
        m.ExpectedResolution, m.RetrievedAt, m.SourceUpdatedAt, m.Outcomes.Select(o => new RelationshipOutcomeResponse(o.NativeId, o.Label)).ToArray(),
        JsonSerializer.Serialize(new { m.Subtitle, m.Description, m.Category, m.Tags, m.NativeStatus, m.NormalizedStatus, m.NativeEventId, m.NativeSeriesId, m.NativeGroupId,
            m.EventStart, m.EventEnd, m.MarketOpen, m.MarketClose, m.ExpectedResolution, m.ResolvedAt, m.RetrievedAt, m.SourceUpdatedAt,
            m.Semantics, m.PublicMetadataReference, m.PublicSemanticMetadataJson }, new JsonSerializerOptions { WriteIndented = true }));
}
