using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class PaperEndpoints
{
    public static void MapPaper(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/paper");
        group.AddEndpointFilter(async (context, next) =>
        {
            var actor = context.HttpContext.RequestServices.GetRequiredService<IRequestActor>().UserId;
            if (actor is null) return Results.Unauthorized();
            var workspace = Guid.Parse((string)context.HttpContext.Request.RouteValues["workspaceId"]!);
            try
            {
                await context.HttpContext.RequestServices.GetRequiredService<RelationshipStore>().RequireMemberAsync(actor.Value, workspace,
                    context.HttpContext.Request.Method != "GET", context.HttpContext.RequestAborted);
                return await next(context);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (PaperGenerationConflict) { return Results.Conflict(new { Code = "GenerationChanged" }); }
        });
        group.MapGet("/account", Account);
        group.MapPost("/account/initialize", Initialize);
        group.MapPost("/account/reset", Initialize);
        group.MapGet("/positions", async (Guid workspaceId, Guid? generationId, PaperStore store, CancellationToken ct) =>
        {
            var generation = await ResolveGeneration(store, workspaceId, generationId, ct);
            return Results.Ok(generation is null ? [] : (await store.PositionsAsync(generation.Id, ct)).Select(p => new PaperPositionResponse(p.Id, p.GenerationId,
                p.Exchange, p.MarketId, p.InstrumentId, p.Outcome, p.Currency, p.Quantity, p.CostBasis, p.Fees, p.AverageEntry, p.OpenedAt, p.UpdatedAt)).ToArray());
        });
        group.MapGet("/executions", async (Guid workspaceId, Guid? generationId, int? page, PaperStore store, CancellationToken ct) =>
            page is < 1 or > 100000 ? Results.BadRequest() : Results.Ok((await store.HistoryAsync(workspaceId, generationId, page ?? 1, ct)).Select(Execution).ToArray()));
        group.MapGet("/executions/{id:guid}", async (Guid workspaceId, Guid id, PaperStore store, CancellationToken ct) =>
            await store.DetailAsync(workspaceId, id, ct) is { } row ? Results.Ok(Execution(row)) : Results.NotFound());
        group.MapPost("/preview", async (Guid workspaceId, PaperPreviewRequest request, IRequestActor actor, PaperCoordinator paper, CancellationToken ct) =>
            ValidKey(request.OpportunityKey) ? Results.Ok(await paper.PreviewAsync(actor.UserId!.Value, workspaceId, request, ct)) : Results.BadRequest());
        group.MapPost("/execute", async (Guid workspaceId, ConfirmPaperRequest request, IRequestActor actor, PaperCoordinator paper,
            RealtimePublisher realtime, HttpContext context, CancellationToken ct) =>
        {
            if (!ValidKey(request.OpportunityKey)) return Results.BadRequest();
            var result = await paper.ConfirmAsync(actor.UserId!.Value, workspaceId, request, context.TraceIdentifier, ct);
            if (result.Execution is not null && !result.Duplicate) realtime.PaperChanged(workspaceId);
            return Results.Ok(new PaperCommitResponse(result.Execution is null ? "Rejected" : "Committed", result.Rejection.ToString(), result.Duplicate,
                result.Execution is null ? null : Execution(result.Execution)));
        });
        group.MapPost("/reconcile", async (Guid workspaceId, Guid generationId, IRequestActor actor, PaperStore store, CancellationToken ct) =>
        {
            if (await ResolveGeneration(store, workspaceId, generationId, ct) is null) return Results.NotFound();
            return Results.Ok(new PaperIntegrityResponse(generationId, (await store.ReconcileAsync(actor.UserId!.Value, workspaceId, generationId, ct)).ToString()));
        });
        group.MapGet("/diagnostics", (Guid workspaceId, PaperDiagnostics diagnostics) => diagnostics.Read(workspaceId));
    }
    private static bool ValidKey(string? key) => key is { Length: 64 } && key.All(char.IsAsciiHexDigit);
    private static async Task<PaperGenerationEntry?> ResolveGeneration(PaperStore store, Guid workspace, Guid? generation, CancellationToken ct) =>
        generation is null ? await store.ActiveAsync(workspace, ct) : await store.GenerationAsync(workspace, generation.Value, ct);
    private static async Task<IResult> Account(Guid workspaceId, Guid? generationId, PaperStore store, CancellationToken ct)
    {
        var history = await store.GenerationsAsync(workspaceId, ct);
        var g = await ResolveGeneration(store, workspaceId, generationId, ct);
        var balances = g is null ? [] : await store.BalancesAsync(g.Id, ct);
        return Results.Ok(new PaperAccountResponse(g is null ? "Uninitialized" : g.ClosedAt is null ? "Active" : "Closed", g is null ? null : Generation(g),
            balances.Select(b => new PaperBalanceResponse(b.Exchange, b.Currency, b.InitialCash, b.AvailableCash, b.ReservedCash,
                checked(b.AvailableCash + b.ReservedCash), b.Revision, b.UpdatedAt)).ToArray(), history.Select(Generation).ToArray()));
    }
    private static async Task<IResult> Initialize(Guid workspaceId, InitializePaperRequest request, IRequestActor actor, PaperStore store,
        LocalOptions options, RealtimePublisher realtime, HttpContext context, CancellationToken ct)
    {
        if (options.TradingMode != "Paper") return Results.Conflict(new { Code = "NotPaperMode" });
        if (!request.ConfirmSimulation || request.Balances is null) return Results.BadRequest();
        var reset = context.Request.Path.Value!.EndsWith("/reset", StringComparison.Ordinal);
        if (reset != request.ExpectedGenerationId.HasValue) return Results.BadRequest();
        await store.InitializeAsync(actor.UserId!.Value, workspaceId, request.ExpectedGenerationId,
            request.Balances.Select(b => new PaperFunding(b.Exchange, b.Currency, b.Amount)).ToArray(), request.Reason, context.TraceIdentifier, ct);
        realtime.PaperChanged(workspaceId);
        return await Account(workspaceId, null, store, ct);
    }
    public static PaperGenerationResponse Generation(PaperGenerationEntry g) => new(g.Id, g.CreatedAt, g.ClosedAt, g.Reason, g.Integrity.ToString());
    public static PaperFillResponse Fill(PaperFill f) => new(f.Id, f.LegId, f.Instrument.Exchange, f.Instrument.NativeMarketId, f.Instrument.NativeInstrumentId,
        f.Instrument.Outcome, "Buy", f.Quantity, f.Price, f.Notional, f.Fee, f.Currency, f.Origin.ToString(),
        new(f.NativeLiquidityIdentity.Exchange, f.NativeLiquidityIdentity.MarketId, f.NativeLiquidityIdentity.InstrumentId,
            f.NativeLiquidityIdentity.NativeSide.ToString(), f.NativeLiquidityIdentity.NativePrice), f.BookVersion, f.FilledAt);
    public static PaperExecutionResponse Execution(PaperExecutionEntry row)
    {
        var p = JsonSerializer.Deserialize<PaperPlan>(row.PlanJson)!;
        return new(row.Id, row.RequestId, row.GenerationId, row.ActorId, row.CreatedAt, row.State.ToString(), row.OpportunityKey, p.Quantity,
            p.Cost, p.ExpectedPayoutAtResolution, p.ExpectedProfitAtResolution, p.Fills.Select(Fill).ToArray(), OpportunityEndpoints.Map(p.Proof));
    }
}
