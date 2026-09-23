using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class OpportunityEndpoints
{
    public static void MapOpportunities(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/opportunities");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapPost("/evaluate", Start);
        group.MapPost("/relationships/{relationshipId:guid}/evaluate", async (Guid workspaceId, Guid relationshipId, EvaluateOpportunitiesRequest request,
            IRequestActor actor, RelationshipStore store, OpportunityJobs jobs, CancellationToken ct) =>
            await Start(workspaceId, request with { RelationshipId = relationshipId }, actor, store, jobs, ct));
        group.MapGet("/jobs/{id:guid}", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store, OpportunityJobs jobs, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, false, ct);
            return jobs.Status(workspaceId, id) is { } job ? Results.Ok(job) : Results.NotFound();
        });
        group.MapPost("/jobs/{id:guid}/cancel", async (Guid workspaceId, Guid id, IRequestActor actor, RelationshipStore store, OpportunityJobs jobs, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
            return jobs.Cancel(workspaceId, id) is { } job ? Results.Ok(job) : Results.NotFound();
        });
        group.MapGet("/jobs/{id:guid}/results", async (Guid workspaceId, Guid id, int? page, int? pageSize, bool? diagnostics, string? sort,
            IRequestActor actor, RelationshipStore store, OpportunityJobs jobs, OpportunityCoordinator coordinator, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, false, ct);
            if (page is < 1 or > 10000 || pageSize is < 1 or > 50 || sort is not (null or "key" or "grossProfit")) return Results.BadRequest();
            var result = await jobs.PageAsync(Actor(actor), workspaceId, id, page ?? 1, pageSize ?? 20, diagnostics ?? false, sort == "grossProfit", coordinator, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapGet("/current/{key}", async (Guid workspaceId, string key, IRequestActor actor, RelationshipStore store,
            OpportunityJobs jobs, OpportunityCoordinator coordinator, CancellationToken ct) =>
        {
            await store.RequireMemberAsync(Actor(actor), workspaceId, false, ct);
            if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) return Results.BadRequest();
            var result = await jobs.CurrentAsync(Actor(actor), workspaceId, key, coordinator, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
    private static Guid Actor(IRequestActor actor) => actor.UserId ?? throw new UnauthorizedAccessException();
    private static async Task<IResult> Start(Guid workspaceId, EvaluateOpportunitiesRequest request, IRequestActor actor,
        RelationshipStore store, OpportunityJobs jobs, CancellationToken ct)
    {
        await store.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
        if (!OpportunityJobs.Valid(request)) return Results.BadRequest();
        ct.ThrowIfCancellationRequested();
        try { return Results.Accepted(value: jobs.Start(Actor(actor), workspaceId, request)); }
        catch (InvalidOperationException) { return Results.Conflict(new { Message = "An evaluation is active or the backend is stopping." }); }
    }
    public static OpportunityResponse Map(ArbitrageOpportunitySnapshot r) => new(r.OpportunityKey, r.Strategy.ToString(), r.RelationshipId,
        r.RelationshipTrust.ToString(), r.EvaluatedAt, r.Status.ToString(), [.. r.Blockers], [.. r.Warnings], r.InputQuality.ToString(),
        r.ObservedSkew is { } skew ? (decimal)skew.Ticks / TimeSpan.TicksPerMillisecond : null,
        (decimal)r.MaximumAllowedSkew.Ticks / TimeSpan.TicksPerMillisecond, r.SkewAcceptable,
        r.Legs.Select(l => new OpportunityLegResponse(l.Instrument.Exchange, l.Instrument.NativeMarketId, l.Instrument.NativeInstrumentId, l.Instrument.Outcome,
            l.Action.ToString(), l.Quantity, l.AveragePrice, l.WorstPrice, l.GrossNotional, l.SourceMode.ToString(), l.Continuity.ToString(), l.SnapshotVersion,
            l.RetrievedAt, l.SourceTimestamp, l.LiquidityOrigins.Select(o => o.ToString()).ToArray(),
            l.LiquiditySources.Select(s => new OpportunityLiquidityResponse(s.Exchange, s.MarketId, s.InstrumentId, s.NativeSide.ToString(), s.NativePrice)).ToArray())).ToArray(),
        r.Segments.Select(s => new OpportunitySegmentResponse(s.Quantity, s.LegAPrice, s.LegBPrice, s.CombinedPrice, s.GrossEdgePerShare, s.CumulativeCost,
            s.CumulativeGuaranteedPayout, s.CumulativeGrossProfit)).ToArray(),
        r.PairedQuantity, r.FullyExecutableQuantity, r.GrossCost, r.GrossProceeds, r.GuaranteedGrossPayout, r.GrossProfit, r.GrossEdgePerShare, r.GrossReturnOnCost,
        r.RelationshipEligible, r.BooksActionable, r.GrossArbitrageExists, r.FullyExecutableForRequestedQuantity, r.EvaluationQuantityCapped, r.EvaluationNotionalCapped,
        r.RelationshipPolicyVersion, r.SourceFingerprint, r.TargetFingerprint, r.RelationshipRevision, r.ExecutionEligible, r.FeeStatus, r.NetProfit, r.NetEdge)
        { SourceTitle = r.SourceTitle, TargetTitle = r.TargetTitle, Fees = r.Fees is { } f ? new(f.State.ToString(), f.Status.ToString(), f.Profile.ToString(),
            f.Breakdown.Select(q => new FeeQuoteResponse(q.Context.Exchange, q.Context.MarketId, q.Context.InstrumentId, q.Context.Role.ToString(), q.Context.Quantity, q.Context.Price,
                q.ModelFee, q.RoundedTradeFee, q.RoundingFee, q.Rebate, q.TotalFee, q.Currency, q.Status.ToString(), q.Source, q.EffectiveAt, q.RetrievedAt,
                q.ScheduleFingerprint, [.. q.Warnings], q.ProgramRebates)).ToArray(), f.TotalExchangeFees, f.FeeAdjustedCost, f.FeeAdjustedGuaranteedProfit,
            f.FeeAdjustedEdgePerShare, f.FeeAdjustedReturnOnCost, f.MinimumEdge) : null,
            OutcomeMappings = r.OutcomeMappings.Select(m => new RelationshipMappingResponse(m.SourceOutcomeId, m.TargetOutcomeId, m.Type.ToString())).ToArray() };
}
