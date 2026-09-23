using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class FeeEndpoints
{
    public static void MapFees(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/fees");
        group.AddEndpointFilter(async (context, next) =>
        { try { return await next(context); } catch (UnauthorizedAccessException) { return Results.StatusCode(403); } catch (ArgumentException) { return Results.BadRequest(); } });
        group.MapGet("/profile", async (Guid workspaceId, IRequestActor actor, RelationshipStore members, IFeeStore store, CancellationToken ct) =>
        { await Member(actor, workspaceId, members, false, ct); return Results.Ok(new FeeProfileResponse((await store.ProfileAsync(workspaceId, ct)).ToString())); });
        group.MapPut("/profile", async (Guid workspaceId, FeeProfileResponse request, IRequestActor actor, RelationshipStore members, IFeeStore store, TradingDbContext db, TimeProvider clock, RealtimePublisher realtime, CancellationToken ct) =>
        {
            await Member(actor, workspaceId, members, true, ct);
            if (!Enum.TryParse<KalshiFeeAccountProfile>(request.Profile, out var profile) || !Enum.IsDefined(profile) || profile.ToString() != request.Profile) return Results.BadRequest();
            db.AuditRecords.Add(new(actor.UserId!.Value, workspaceId, clock.GetUtcNow(), Guid.NewGuid().ToString(), "FeeDiagnosticProfileChanged", System.Text.Json.JsonSerializer.Serialize(request)));
            await store.SetProfileAsync(workspaceId, profile, ct); realtime.PaperValuationChanged(workspaceId); return Results.Ok(request);
        });
        group.MapGet("/schedules/{exchange}/{marketId}", async (Guid workspaceId, string exchange, string marketId, IRequestActor actor,
            RelationshipStore members, IFeeStore store, TimeProvider clock, CancellationToken ct) =>
        {
            await Member(actor, workspaceId, members, false, ct);
            var resolved = FeeScheduleResolver.Resolve(await store.ReadAsync(exchange, marketId, ct), clock.GetUtcNow()); var s = resolved.Schedule;
            return Results.Ok(new FeeScheduleResponse(exchange, marketId, resolved.Status.ToString(), s?.Currency, s?.RetrievedAt, s?.Source,
                resolved.Fingerprint, resolved.Warning, s?.Rules.Select(r => new FeeRuleResponse(r.Id, r.Type, r.Rate, r.EffectiveFrom, r.Level.ToString(), r.Clear)).ToArray() ?? []));
        });
        group.MapPost("/refresh", async (Guid workspaceId, RefreshFeesRequest request, IRequestActor actor, RelationshipStore members, FeeJobs jobs, CancellationToken ct) =>
        {
            await Member(actor, workspaceId, members, true, ct);
            if (!FeeJobs.Valid(request)) return Results.BadRequest();
            ct.ThrowIfCancellationRequested();
            try { return Results.Accepted(value: jobs.Start(actor.UserId!.Value, workspaceId, request)); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        });
        group.MapGet("/jobs/{id:guid}", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore members, FeeJobs jobs, CancellationToken ct) =>
        { await Member(actor, workspaceId, members, false, ct); return jobs.Status(workspaceId, id) is { } job ? Results.Ok(job) : Results.NotFound(); });
        group.MapPost("/jobs/{id:guid}/cancel", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore members, FeeJobs jobs, CancellationToken ct) =>
        { await Member(actor, workspaceId, members, true, ct); return jobs.Cancel(workspaceId, id) is { } job ? Results.Ok(job) : Results.NotFound(); });
    }
    private static Task Member(IRequestActor actor, Guid workspace, RelationshipStore store, bool write, CancellationToken ct) =>
        store.RequireMemberAsync(actor.UserId ?? throw new UnauthorizedAccessException(), workspace, write, ct);
}
