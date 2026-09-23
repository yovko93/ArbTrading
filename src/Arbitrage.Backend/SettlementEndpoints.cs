using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class SettlementEndpoints
{
    public static PaperExecutionSettlementResponse Economics(ExecutionSettlement s) => new(s.State.ToString(), s.RealizedPayoutToDate, s.RealizedPnlToDate,
        s.RemainingOpenCostBasis, s.ExpectedRemainingPayout, s.FinalRealizedProfit, s.RealizedReturnOnCost, s.ExpectedVsRealizedDifference);
    public static PaperPositionResponse Position(PaperPositionEntry p) => new(p.Id, p.GenerationId, p.Exchange, p.MarketId, p.InstrumentId, p.Outcome,
        p.Currency, p.Quantity, p.CostBasis, p.Fees, p.AverageEntry, p.OpenedAt, p.UpdatedAt, p.Status.ToString(), p.SettledAt, p.SettlementPayout, p.RealizedPnl,
        p.SettlementResolutionId, p.SettlementResolutionId is null ? null : "ManualScenario");
    private static bool Valid(PaperResolutionSelection? s) => s is not null && s.GenerationId != Guid.Empty && s.Exchange is "Kalshi" or "Polymarket" &&
        s.MarketId is { Length: > 0 and <= 300 } && s.WinningInstrumentId is { Length: > 0 and <= 300 };
    public static void MapSettlement(this RouteGroupBuilder group)
    {
        group.MapGet("/resolution-candidates", async (Guid workspaceId, Guid generationId, int? page, PaperStore store, CancellationToken ct) =>
        {
            if (page is < 1 or > 100000) return Results.BadRequest();
            if (await store.GenerationAsync(workspaceId, generationId, ct) is null) return Results.NotFound();
            return Results.Ok((await store.CandidatesAsync(workspaceId, generationId, page ?? 1, ct)).Select(c => new PaperResolutionCandidateResponse(generationId,
                c.Market.Exchange, c.Market.MarketId, c.Market.Title, PaperSettlement.Supported(c.Market), c.Market.Outcomes.Select(o => new PaperPayoutResponse(o.InstrumentId, o.Outcome, 0)).ToArray(),
                c.Positions.Select(Position).ToArray(), c.ExecutionIds, c.RelationshipIds)).ToArray());
        });
        group.MapPost("/resolutions/preview", async (Guid workspaceId, PaperResolutionSelection request, IRequestActor actor, SettlementCoordinator coordinator, CancellationToken ct) =>
            Valid(request) ? Results.Ok(await coordinator.PreviewAsync(actor.UserId!.Value, workspaceId, request, ct)) : Results.BadRequest());
        group.MapPost("/resolutions/confirm", async (Guid workspaceId, ConfirmResolutionRequest request, IRequestActor actor, SettlementCoordinator coordinator,
            PaperStore store, RealtimePublisher realtime, HttpContext context, CancellationToken ct) =>
        {
            if (!Valid(request.Selection)) return Results.BadRequest();
            var r = await coordinator.ConfirmAsync(actor.UserId!.Value, workspaceId, request, context.TraceIdentifier, ct);
            if (r.Resolution is not null && !r.Duplicate) realtime.PaperChanged(workspaceId);
            return Results.Ok(new PaperResolutionCommitResponse(r.Rejection.ToString(), r.Duplicate, r.Resolution is null ? null : await Map(store, r.Resolution, ct)));
        });
        group.MapGet("/resolutions", async (Guid workspaceId, Guid? generationId, int? page, PaperStore store, CancellationToken ct) =>
        {
            if (page is < 1 or > 100000) return Results.BadRequest();
            var result = new List<PaperResolutionResponse>();
            foreach (var r in await store.ResolutionsAsync(workspaceId, generationId, page ?? 1, ct)) result.Add(await Map(store, r, ct));
            return Results.Ok(result);
        });
        group.MapGet("/resolutions/{id:guid}", async (Guid workspaceId, Guid id, PaperStore store, CancellationToken ct) =>
            await store.ResolutionAsync(workspaceId, id, ct) is { } r ? Results.Ok(await Map(store, r, ct)) : Results.NotFound());
        group.MapGet("/performance", async (Guid workspaceId, Guid generationId, PaperStore store, CancellationToken ct) =>
        {
            if (await store.GenerationAsync(workspaceId, generationId, ct) is null) return Results.NotFound();
            var p = await store.PerformanceAsync(workspaceId, generationId, ct);
            return Results.Ok(new PaperPerformanceResponse(generationId, p.Lifecycle, p.Buckets.Select(b => new PaperPerformanceBucketResponse(b.Exchange, b.Currency,
                b.StartingCash, b.CurrentCash, b.OpenCostBasis, b.SettledCostBasis, b.SettlementPayout, b.CumulativeRealizedPnl, b.OpenPositionCount, b.SettledPositionCount,
                b.CommittedExecutionCount, b.PartiallySettledExecutionCount, b.SettledExecutionCount)).ToArray()));
        });
        group.MapGet("/performance/curve", async (Guid workspaceId, Guid generationId, string exchange, string currency, int? page, int? pageSize, PaperStore store, CancellationToken ct) =>
        {
            if (page is < 1 or > 100000 || pageSize is < 1 or > 1000 || exchange.Length > 40 || currency.Length > 20) return Results.BadRequest();
            if (await store.GenerationAsync(workspaceId, generationId, ct) is null) return Results.NotFound();
            var curve = await store.CurveAsync(workspaceId, generationId, exchange, currency, page ?? 1, pageSize ?? 1000, ct);
            return Results.Ok(new PaperCurveResponse(generationId, exchange, currency, page ?? 1, curve.HasMore, curve.Points.Select(p => new PaperCurvePointResponse(
                p.Timestamp, p.Exchange, p.Currency, p.CashAvailable, p.CumulativeRealizedPnl, p.RealizedPnlDelta, p.RealizedPerformance, p.Reason, p.ExecutionId, p.ResolutionId)).ToArray()));
        });
        group.MapGet("/resolutions/diagnostics", (Guid workspaceId, SettlementMemory memory) => memory.Read(workspaceId));
    }
    private static async Task<PaperResolutionResponse> Map(PaperStore store, PaperResolutionEntry r, CancellationToken ct) =>
        new(r.Id, r.GenerationId, r.RequestId, r.ActorId, r.Exchange, r.MarketId, r.Source.ToString(), r.Status.ToString(), r.ResolvedAt, r.RecordedAt,
            (await store.OutcomesAsync(r.Id, ct)).Select(o => new PaperPayoutResponse(o.InstrumentId, o.Outcome, o.PayoutPerShare)).ToArray(),
            (await store.SettledPositionsAsync(r.Id, ct)).Select(Position).ToArray(), await store.ResolutionExecutionsAsync(r, ct), (await store.SettlementJournalAsync(r.Id, ct))?.Id);
}
