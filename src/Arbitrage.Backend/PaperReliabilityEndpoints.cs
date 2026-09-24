using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class PaperReliabilityEndpoints
{
    private static PaperReliabilityReportResponse? Map(PaperReliabilityEvaluationEntry? e) => e is null ? null : PaperRiskEndpoints.Map<PaperReliabilityReportResponse>(JsonSerializer.Deserialize<ReliabilityReport>(e.ReportJson)!);
    public static void MapPaperReliability(this RouteGroupBuilder paper)
    {
        var group = paper.MapGroup("/reliability");
        group.AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ReliabilityConflict e) { return Results.Conflict(new { Code = e.Message }); } });
        group.MapGet("/current", async (Guid workspaceId, PaperReliabilityStore store, PaperReliabilityCoordinator coordinator, PaperReliabilityTelemetry telemetry, CancellationToken ct) =>
        {
            var c = await store.CurrentAsync(workspaceId, ct) ?? (await store.ListAsync(workspaceId, 1, ct)).FirstOrDefault();
            var campaign = c is null ? null : PaperRiskEndpoints.Map<PaperReliabilityCampaignResponse>(c);
            if (campaign is not null && telemetry.Capture(workspaceId)?.Gap == true) campaign = campaign with { EvidenceGapDetected = true };
            return new PaperReliabilityCurrentResponse(campaign, c is null ? null : Map(await store.LatestAsync(c.Id, ct)), coordinator.TelemetryPersistenceFailures);
        });
        group.MapGet("/campaigns", async (Guid workspaceId, int? page, PaperReliabilityStore store, CancellationToken ct) =>
            page is < 1 or > 10000 ? Results.BadRequest() : Results.Ok((await store.ListAsync(workspaceId, page ?? 1, ct)).Select(PaperRiskEndpoints.Map<PaperReliabilityCampaignResponse>)));
        group.MapGet("/campaigns/{id:guid}", async (Guid workspaceId, Guid id, PaperReliabilityStore store, CancellationToken ct) =>
            await store.ReadAsync(workspaceId, id, ct) is { } c ? Results.Ok(PaperRiskEndpoints.Map<PaperReliabilityCampaignResponse>(c)) : Results.NotFound());
        group.MapGet("/campaigns/{id:guid}/report", async (Guid workspaceId, Guid id, PaperReliabilityStore store, CancellationToken ct) =>
            await store.ReadAsync(workspaceId, id, ct) is null ? Results.NotFound() : Results.Ok(Map(await store.LatestAsync(id, ct))));
        group.MapGet("/campaigns/{id:guid}/events", async (Guid workspaceId, Guid id, int? page, PaperReliabilityStore store, CancellationToken ct) =>
            page is < 1 or > 10000 ? Results.BadRequest() : Results.Ok((await store.EventsAsync(workspaceId, id, page ?? 1, ct)).Select(PaperRiskEndpoints.Map<PaperReliabilityEventResponse>)));
        group.MapGet("/campaigns/{id:guid}/evaluations", async (Guid workspaceId, Guid id, int? page, PaperReliabilityStore store, CancellationToken ct) =>
            page is < 1 or > 10000 ? Results.BadRequest() : Results.Ok((await store.EvaluationsAsync(workspaceId, id, page ?? 1, ct)).Select(PaperRiskEndpoints.Map<PaperReliabilityEvaluationResponse>)));
        foreach (var action in new[] { "start", "pause", "resume", "evaluate", "complete", "cancel" })
            group.MapPost("/" + action, async (Guid workspaceId, PaperReliabilityActionRequest request, IRequestActor actor, PaperReliabilityCoordinator coordinator, CancellationToken ct) =>
                Results.Ok(PaperRiskEndpoints.Map<PaperReliabilityCampaignResponse>(await coordinator.ActAsync(actor.UserId!.Value, workspaceId, action, request.CampaignId, request.ExpectedRevision, request.Name, request.Notes, ct))));
        group.MapPost("/campaigns/{id:guid}/export", async (Guid workspaceId, Guid id, PaperReliabilityCoordinator coordinator, CancellationToken ct) =>
            new PaperReliabilityExportResponse(await coordinator.ExportAsync(workspaceId, id, ct)));
    }
}
