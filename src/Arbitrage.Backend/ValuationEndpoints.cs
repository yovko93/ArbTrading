using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public sealed class PaperValuationCoordinator(PaperStore store, OrderBookCache cache, TimeProvider clock)
{
    public async Task<PaperValuationResponse?> ReadAsync(Guid workspace, Guid? generation, int page, int size, CancellationToken ct)
    {
        var inputs = await store.ValuationInputsAsync(workspace, generation, ct);
        if (inputs is null) return generation is null ? new(null, "Uninitialized", false, clock.GetUtcNow(), [], [], page, false, []) : null;
        var positions = inputs.Positions;
        var ids = positions.Where(p => p.Status == PaperPositionStatus.Open).Select(Identity).Distinct().ToArray();
        var books = cache.ReadTogether(ids);
        var now = clock.GetUtcNow();
        var byId = ids.Zip(books).ToDictionary(x => x.First, x => x.Second);
        var marks = positions.Select(p =>
        {
            ct.ThrowIfCancellationRequested();
            var identity = Identity(p);
            var book = byId.GetValueOrDefault(identity) ?? new(null, null, new(BookFreshness.Unavailable, false, "Unavailable", null));
            var inventory = new PaperInventory(p.Id, p.GenerationId, identity, p.Currency, p.Status, p.Quantity, p.CostBasis, p.Fees, p.AverageEntry, p.SettlementPayout, p.RealizedPnl);
            return PaperValuation.Mark(inventory, Supported(p, inputs.Markets), book,
                FeeScheduleResolver.Resolve(inputs.Fees.SingleOrDefault(f => f.Exchange == p.Exchange && f.MarketId == p.MarketId), now), inputs.Profile.Profile, now);
        }).ToArray();
        // No retries, no I/O under the cache gate. A changed/missing version invalidates its mark.
        var current = cache.ReadTogether(ids);
        for (var i = 0; i < marks.Length; i++)
        {
            if (positions[i].Status != PaperPositionStatus.Open) continue;
            var n = Array.IndexOf(ids, Identity(positions[i]));
            if (books[n].Version != current[n].Version)
                marks[i] = new(marks[i].Position, PaperMarkStatus.BookChangedDuringValuation, PaperMarkQuality.NonActionable, now);
            else if (books[n].Eligibility.IsActionable && !current[n].Eligibility.IsActionable)
                marks[i] = PaperValuation.Mark(marks[i].Position, true, current[n], FeeScheduleResolver.Resolve(null, now), inputs.Profile.Profile, clock.GetUtcNow());
        }
        var buckets = inputs.Balances.Select(b => Summarize(b, marks)).ToArray();
        return new(inputs.Generation.Id, "Available", inputs.Generation.ClosedAt is not null, now,
            marks.Skip((page - 1) * size).Take(size).Select((m) => Map(m, positions.Single(p => p.Id == m.Position.PositionId))).ToArray(),
            buckets, page, positions.Length > page * size,
            ["Current local estimate. Separate venue/currency buckets; no FX assumption.",
             inputs.Generation.ClosedAt is null ? "Current paper generation." : "Current mark applied to historical paper position."]);
    }
    public static OrderBookInstrumentId Identity(PaperPositionEntry p) => new(p.Exchange, p.MarketId, p.InstrumentId, p.Outcome);
    public static bool Supported(PaperPositionEntry p, MarketCatalogEntry[] markets)
    {
        var m = markets.SingleOrDefault(m => m.Exchange == p.Exchange && m.NativeId == p.MarketId);
        if (m is null || m.IsIncomplete) return false;
        var outcomes = JsonSerializer.Deserialize<MarketOutcome[]>(m.OutcomesJson) ?? [];
        if (p.Exchange == "Kalshi") return m.Classification == "binary" && outcomes.Length == 2 &&
            outcomes.Count(o => o.Label == "Yes") == 1 && outcomes.Count(o => o.Label == "No") == 1 &&
            (p.InstrumentId == "yes" && p.Outcome == "Yes" || p.InstrumentId == "no" && p.Outcome == "No");
        return p.Exchange == "Polymarket" && p.InstrumentId.Length > 0 && p.InstrumentId.All(char.IsAsciiDigit) &&
            outcomes.Count(o => o.NativeTokenId == p.InstrumentId) == 1 && outcomes.Any(o => o.NativeTokenId == p.InstrumentId && o.Label == p.Outcome);
    }
    public static PaperValuationBucketResponse Summarize(PaperBalanceEntry b, PaperMark[] marks)
    {
        var all = marks.Where(m => m.Position.Instrument.Exchange == b.Exchange && m.Position.Currency == b.Currency).ToArray();
        var open = all.Where(m => m.Position.Status == PaperPositionStatus.Open).ToArray();
        var cost = open.Sum(m => m.Position.CostBasis); var realized = all.Sum(m => m.Position.RealizedPnl ?? 0);
        var full = open.Count(m => m.GrossLiquidationValue.HasValue); var partial = open.Count(m => m.MarkStatus == PaperMarkStatus.PartialDepth);
        decimal? gross = full == open.Length ? open.Sum(m => m.GrossLiquidationValue!.Value) : null;
        decimal? net = open.All(m => m.FeeAdjustedLiquidationValue.HasValue) ? open.Sum(m => m.FeeAdjustedLiquidationValue!.Value) : null;
        var largest = open.Select(m => m.Position.CostBasis).DefaultIfEmpty(0).Max();
        return new(b.Exchange, b.Currency, b.InitialCash, b.AvailableCash, cost, realized, open.Length, full, partial, open.Length - full - partial,
            gross, net, open.Sum(m => m.GrossLiquidationValue ?? 0), checked(b.AvailableCash + cost), b.AvailableCash + gross, b.AvailableCash + net,
            gross.HasValue ? realized + gross - cost : null, net.HasValue ? realized + net - cost : null,
            open.Length == 0 ? 1 : (decimal)full / open.Length, b.InitialCash > 0 ? cost / b.InitialCash : null,
            largest, cost > 0 ? largest / cost : null, open.Select(m => m.Position.Instrument.NativeMarketId).Distinct().Count());
    }
    private static PaperPositionMarkResponse Map(PaperMark m, PaperPositionEntry p) => new(SettlementEndpoints.Position(p), m.MarkStatus.ToString(), m.Quality.ToString(), m.ValuedAt,
        m.ExecutableQuantity, m.UnfilledQuantity, m.GrossLiquidationValue, m.PartialGrossLiquidationValue, m.AverageLiquidationPrice, m.PartialAveragePrice,
        m.WorstLiquidationPrice, m.EstimatedExitFees, m.ExitFeeStatus.ToString(), m.FeeAdjustedLiquidationValue, m.GrossUnrealizedPnlBeforeExitFees,
        m.FeeAdjustedUnrealizedPnl, m.GrossReturnOnCost, m.FeeAdjustedReturnOnCost, m.BookVersion, m.SourceMode?.ToString(), m.Continuity?.ToString(),
        m.BookRetrievedAt, m.BookSourceTimestamp, m.BookAge?.TotalSeconds, m.NativeLiquidity.Select(l => new OpportunityLiquidityResponse(l.Exchange,
            l.MarketId, l.InstrumentId, l.NativeSide.ToString(), l.NativePrice)).ToArray(), m.Warnings.ToArray())
        {
            ExitFeeProfile = m.ExitFeeBreakdown.FirstOrDefault()?.Context.Profile.ToString() ?? "Unknown",
            ExitFeeBreakdown = m.ExitFeeBreakdown.Select(q => new FeeQuoteResponse(q.Context.Exchange, q.Context.MarketId, q.Context.InstrumentId,
                q.Context.Role.ToString(), q.Context.Quantity, q.Context.Price, q.ModelFee, q.RoundedTradeFee, q.RoundingFee, q.Rebate, q.TotalFee,
                q.Currency, q.Status.ToString(), q.Source, q.EffectiveAt, q.RetrievedAt, q.ScheduleFingerprint, q.Warnings.ToArray(), q.ProgramRebates)).ToArray()
        };
}
public static class ValuationEndpoints
{
    public static void MapValuation(this RouteGroupBuilder group)
    {
        group.MapGet("/valuation", Read);
        group.MapGet("/risk", Read);
        group.MapGet("/positions/{positionId:guid}/valuation", async (Guid workspaceId, Guid positionId, Guid? generationId,
            PaperValuationCoordinator coordinator, PaperStore store, CancellationToken ct) =>
        {
            generationId ??= await store.PositionGenerationAsync(workspaceId, positionId, ct);
            if (generationId is null) return Results.NotFound();
            var result = await coordinator.ReadAsync(workspaceId, generationId, 1, 1000, ct);
            var mark = result?.Positions.SingleOrDefault(p => p.Position.Id == positionId);
            return mark is null ? Results.NotFound() : Results.Ok(mark);
        });
    }
    private static async Task<IResult> Read(Guid workspaceId, Guid? generationId, int? page, int? pageSize, PaperValuationCoordinator coordinator, CancellationToken ct)
    {
        if (page is < 1 or > 100000 || pageSize is < 1 or > 1000) return Results.BadRequest();
        try
        {
            var result = await coordinator.ReadAsync(workspaceId, generationId, page ?? 1, pageSize ?? 100, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (OverflowException) { return Results.Conflict(new { Code = "ArithmeticOverflow" }); }
    }
}
