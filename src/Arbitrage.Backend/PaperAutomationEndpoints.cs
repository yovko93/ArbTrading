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
        group.MapPost("/sizing-preview", async (Guid workspaceId, PaperSizingPreviewRequest request, IRequestActor actor, PaperStore store,
            PaperAutomationCoordinator automation, PaperCoordinator paperCoordinator, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.OpportunityKey is not { Length: 64 } || !request.OpportunityKey.All(char.IsAsciiHexDigit)) return Results.BadRequest();
            var profile = await store.AutomationProfileAsync(workspaceId, ct) ?? throw new PaperAutomationException(PaperAutomationReason.NotConfigured);
            var risk = await store.RiskProfileAsync(workspaceId, ct) ?? throw new PaperAutomationException(PaperAutomationReason.RiskNotConfigured);
            var generation = await store.ActiveAsync(workspaceId, ct) ?? throw new PaperAutomationException(PaperAutomationReason.GenerationUnavailable);
            var kill = await store.AutomationControlAsync(workspaceId, ct);
            // A disarmed diagnostic uses an empty prospective session, never an execution permit/ticket returned to clients.
            var runtime = automation.Runtime(workspaceId);
            var permit = runtime.State == PaperAutomationState.Armed && runtime.Session is { } armed ? armed with { ActorId = actor.UserId!.Value } :
                new PaperAutomationPermit(Guid.Empty, workspaceId, actor.UserId!.Value, generation.Id, profile, risk.Revision, risk.Fingerprint, kill?.Revision, clock.GetUtcNow());
            var d = await paperCoordinator.SizeAutomaticAsync(permit, request.OpportunityKey, new(), ct);
            var p = d.SelectedPlan;
            return Results.Ok(new PaperSizingPreviewResponse(d.State.ToString(), d.Mode.ToString(), d.SelectedQuantity, d.CandidatesEvaluated,
                d.HighestConfiguredQuantity, d.LowestConfiguredQuantity, d.QuantityStep, d.Rejections.Select(PaperRiskEndpoints.Map<PaperSizingRejectionResponse>).ToArray(),
                p?.Proof.GrossCost, p?.Proof.Fees?.TotalExchangeFees, p?.Cost, p?.ExpectedPayoutAtResolution, p?.ExpectedProfitAtResolution,
                p?.Proof.Fees?.FeeAdjustedEdgePerShare, PaperRiskEndpoints.Decision(d.SelectedRiskDecision), d.Proof is null ? null : PaperRiskEndpoints.Map<PaperSizingProofResponse>(d.Proof), d.EvaluatedAt));
        });
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
            runtime.LastActivityAt, runtime.LastCommittedAt, runtime.SessionDebits.Select(PaperRiskEndpoints.Map<PaperAutomationDebitResponse>).ToArray(), runtime.QueueDepth, runtime.Counters,
            runtime.LastSizingState?.ToString(), runtime.LastSelectedQuantity, runtime.LastSizingCandidatesEvaluated));
    }
}
