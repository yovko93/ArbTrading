using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class PaperAutomationEndpoints
{
    public static void MapPaperAutomation(this RouteGroupBuilder paper)
    {
        var group = paper.MapGroup("/automation");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (PaperAutomationException e) { return Results.Conflict(new { Code = e.Reason.ToString() }); }
            catch (JsonException) { return Results.BadRequest(new { Code = "InvalidProfile" }); }
        });
        group.MapGet("/profile", async (Guid workspaceId, PaperStore store, CancellationToken ct) =>
            Results.Ok(await store.AutomationProfileAsync(workspaceId, ct) is { } p ? PaperRiskEndpoints.Map<PaperAutomationProfileResponse>(p) : null));
        group.MapPut("/profile", async (Guid workspaceId, SavePaperAutomationRequest request, IRequestActor actor, PaperAutomationCoordinator automation, CancellationToken ct) =>
        {
            if (request.Settings is null) return Results.BadRequest();
            var p = await automation.SaveAsync(actor.UserId!.Value, workspaceId, request.ExpectedRevision, request.ConfirmPolicy,
                PaperRiskEndpoints.Map<PaperAutomationSettings>(request.Settings), ct);
            return Results.Ok(PaperRiskEndpoints.Map<PaperAutomationProfileResponse>(p));
        });
        group.MapGet("/status", Status);
        group.MapGet("/diagnostics", (Guid workspaceId, PaperAutomationCoordinator automation) => automation.Runtime(workspaceId).Counters);
        group.MapPost("/arm", async (Guid workspaceId, ArmPaperAutomationRequest request, IRequestActor actor,
            PaperAutomationCoordinator automation, PaperStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            await automation.ArmAsync(actor.UserId!.Value, workspaceId, request.ExpectedProfileRevision, request.ExpectedRiskRevision,
                request.ExpectedGenerationId, request.ExpectedKillRevision, request.ConfirmSimulation, ct);
            return await Status(workspaceId, store, automation, monitor, ct);
        });
        group.MapPost("/disarm", async (Guid workspaceId, IRequestActor actor, PaperAutomationCoordinator automation,
            PaperStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            await automation.DisarmAsync(actor.UserId!.Value, workspaceId, ct);
            return await Status(workspaceId, store, automation, monitor, ct);
        });
        group.MapPost("/emergency-stop", async (Guid workspaceId, PaperAutomationControlRequest request, IRequestActor actor,
            PaperAutomationCoordinator automation, PaperStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            await automation.KillAsync(actor.UserId!.Value, workspaceId, true, request.ExpectedRevision, false, request.Reason, ct);
            return await Status(workspaceId, store, automation, monitor, ct);
        });
        group.MapPost("/reset-kill-switch", async (Guid workspaceId, PaperAutomationControlRequest request, IRequestActor actor,
            PaperAutomationCoordinator automation, PaperStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            await automation.KillAsync(actor.UserId!.Value, workspaceId, false, request.ExpectedRevision, request.ConfirmReset, request.Reason, ct);
            return await Status(workspaceId, store, automation, monitor, ct);
        });
    }
    private static async Task<IResult> Status(Guid workspaceId, PaperStore store, PaperAutomationCoordinator automation, MonitoringCoordinator monitor, CancellationToken ct)
    {
        var profile = await store.AutomationProfileAsync(workspaceId, ct);
        var kill = await store.AutomationControlAsync(workspaceId, ct);
        var risk = await store.RiskProfileAsync(workspaceId, ct);
        var generation = await store.ActiveAsync(workspaceId, ct);
        var runtime = automation.Runtime(workspaceId);
        var state = kill?.IsLatched == true ? "KillSwitchLatched" : profile is null ? "NotConfigured" : runtime.State.ToString();
        return Results.Ok(new PaperAutomationStatusResponse(state, runtime.StopReason.ToString(),
            profile is null ? null : PaperRiskEndpoints.Map<PaperAutomationProfileResponse>(profile), risk?.Revision, generation?.Id,
            new(kill?.Revision, kill?.IsLatched ?? false, kill?.LatchedAt, kill?.LatchedBy, kill?.Reason ?? "", kill?.ResetAt, kill?.ResetBy),
            runtime.Session?.SessionId, runtime.Session?.StartedAt, runtime.Session?.ActorId, monitor.Status(workspaceId).State.ToString(),
            runtime.ExecutionsCommitted, runtime.CandidatesConsidered, runtime.CandidatesSkipped, runtime.ExecutionsRejected,
            runtime.LastActivityAt, runtime.LastCommittedAt, runtime.SessionDebits.Select(PaperRiskEndpoints.Map<PaperAutomationDebitResponse>).ToArray(), runtime.QueueDepth, runtime.Counters));
    }
}
