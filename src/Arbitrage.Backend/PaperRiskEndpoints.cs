using System.Text.Json;
using System.Text.Json.Serialization;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class PaperRiskEndpoints
{
    // Boundary-only structural mapping. Contracts remain independent of Execution/Infrastructure.
    private static readonly JsonSerializerOptions Mapping = new() { Converters = { new JsonStringEnumConverter() } };
    public static T Map<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Mapping), Mapping)!;
    public static PaperRiskDecisionResponse? Decision(PaperRiskDecision? value) => value is null ? null : Map<PaperRiskDecisionResponse>(value);
    public static void MapPaperRisk(this RouteGroupBuilder group)
    {
        group.MapGet("/admission-policy", async (Guid workspaceId, PaperStore store, CancellationToken ct) =>
            Results.Ok(await store.RiskProfileAsync(workspaceId, ct) is { } p ? Map<PaperRiskPolicyResponse>(p) : null));
        group.MapPut("/admission-policy", async (Guid workspaceId, SavePaperRiskPolicyRequest request, IRequestActor actor,
            PaperStore store, RealtimePublisher realtime, PaperRiskDiagnostics diagnostics, HttpContext context, CancellationToken ct) =>
        {
            if (request.Limits is null) return Results.BadRequest();
            try
            {
                var p = await store.SaveRiskProfileAsync(actor.UserId!.Value, workspaceId, request.ExpectedRevision,
                    Map<PaperRiskLimits>(request.Limits), request.ConfirmPolicy, context.TraceIdentifier, ct);
                realtime.PaperChanged(workspaceId);
                diagnostics.Increment(workspaceId, "PaperRiskPolicyChanges");
                return Results.Ok(Map<PaperRiskPolicyResponse>(p));
            }
            catch (PaperRiskRevisionConflict) { diagnostics.Increment(workspaceId, "PaperRiskPolicyRevisionConflicts"); return Results.Conflict(new { Code = "RiskPolicyChanged" }); }
        });
        group.MapGet("/admission-diagnostics", (Guid workspaceId, PaperRiskDiagnostics diagnostics) => diagnostics.Read(workspaceId));
        group.MapGet("/admission-status", async (Guid workspaceId, Guid? generationId, PaperStore store, TimeProvider clock, CancellationToken ct) =>
        {
            try
            {
                var snapshot = await store.RiskSnapshotAsync(workspaceId, generationId, ct);
                var d = PaperRiskEvaluator.Evaluate(snapshot.Profile, snapshot.State, null, clock.GetUtcNow());
                return Results.Ok(new PaperRiskStatusResponse(d.Decision switch { PaperRiskOutcome.Approved => "WithinLimits",
                    PaperRiskOutcome.Rejected => "OverLimit", PaperRiskOutcome.NotConfigured => "NotConfigured", _ => "IntegrityFailure" },
                    snapshot.Profile is null ? null : Map<PaperRiskPolicyResponse>(snapshot.Profile), Decision(d)!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
    }
}
