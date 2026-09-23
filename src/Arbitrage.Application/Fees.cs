using System.Collections.Immutable;
using Arbitrage.Domain;

namespace Arbitrage.Application;

public interface IPublicFeeSource
{
    Task<FeeSchedule> ReadAsync(string exchange, string marketId, CancellationToken ct);
}
public interface IFeeStore
{
    Task<FeeProfileState> ReadProfileAsync(Guid workspace, CancellationToken ct);
    Task<FeeSchedule?> ReadAsync(string exchange, string marketId, CancellationToken ct);
    Task SaveAsync(FeeSchedule schedule, CancellationToken ct);
    Task<KalshiFeeAccountProfile> ProfileAsync(Guid workspace, CancellationToken ct);
    Task SetProfileAsync(Guid workspace, KalshiFeeAccountProfile profile, CancellationToken ct);
}
public sealed record FeeProfileState(KalshiFeeAccountProfile Profile, Guid Revision);
public sealed record OpportunityFees(FeeOpportunityStatus State, FeeStatus Status, KalshiFeeAccountProfile Profile,
    ImmutableArray<FeeQuote> Breakdown, decimal? TotalExchangeFees, decimal? FeeAdjustedCost, decimal? FeeAdjustedGuaranteedProfit,
    decimal? FeeAdjustedEdgePerShare, decimal? FeeAdjustedReturnOnCost, decimal MinimumEdge)
{
    public Guid ProfileRevision { get; init; }
    public OpportunityFees Invalidate() => this with { State = FeeOpportunityStatus.FeeResultStale, TotalExchangeFees = null,
        FeeAdjustedCost = null, FeeAdjustedGuaranteedProfit = null, FeeAdjustedEdgePerShare = null, FeeAdjustedReturnOnCost = null };
}
public static class FeeOpportunityEvaluator
{
    public static OpportunityFees Evaluate(ArbitrageOpportunitySnapshot gross, IReadOnlyList<ResolvedFeeSchedule> schedules,
        KalshiFeeAccountProfile profile, decimal minimumEdge = 0)
    {
        var quotes = ImmutableArray.CreateBuilder<FeeQuote>();
        if (gross.Legs.Length != 2 || schedules.Count != 2 || gross.Segments.IsDefaultOrEmpty || gross.PairedQuantity <= 0 || !gross.GrossArbitrageExists)
            return new(FeeOpportunityStatus.GrossNotDetected, FeeStatus.NotEvaluated, profile, [], null, null, null, null, null, minimumEdge);
        for (var leg = 0; leg < 2; leg++)
        {
            var l = gross.Legs[leg]; decimal accumulator = 0;
            // Paired segmentation can split the same native price when the OTHER leg moves. Merge those adjacent pieces.
            var levels = new List<(decimal Price, decimal Quantity)>();
            foreach (var segment in gross.Segments)
            {
                var price = leg == 0 ? segment.LegAPrice : segment.LegBPrice;
                if (levels.Count > 0 && levels[^1].Price == price) levels[^1] = (price, levels[^1].Quantity + segment.Quantity);
                else levels.Add((price, segment.Quantity));
            }
            foreach (var level in levels)
            {
                var context = new FeeCalculationContext(l.Instrument.Exchange, l.Instrument.NativeMarketId, l.Instrument.NativeInstrumentId,
                    l.Action == DepthAction.Buy && l.LiquidityOrigins.All(o => o is LiquidityOrigin.NativeAsk or LiquidityOrigin.DerivedComplement) ? LiquidityRole.Taker : LiquidityRole.Unknown,
                    level.Quantity, level.Price, l.Action, profile);
                quotes.Add(FeeMath.Quote(schedules[leg], context, ref accumulator));
            }
        }
        var breakdown = quotes.ToImmutable();
        var unresolved = breakdown.FirstOrDefault(q => q.TotalFee is null);
        if (unresolved is not null)
        {
            var state = unresolved.Status switch
            {
                FeeStatus.ScheduleUnavailable => FeeOpportunityStatus.FeeScheduleUnavailable,
                FeeStatus.ScheduleStale => FeeOpportunityStatus.FeeScheduleStale,
                FeeStatus.KnownModelAccountRoundingUnknown or FeeStatus.AccountProfileRequired => FeeOpportunityStatus.AccountFeeProfileRequired,
                _ => FeeOpportunityStatus.UnsupportedFeeModel
            };
            return new(state, unresolved.Status, profile, breakdown, null, null, null, null, null, minimumEdge);
        }
        // Gross engine uses dollar face values. Do not silently exchange USDC/pUSD for USD.
        if (breakdown.Select(q => q.Currency).Distinct().Count() != 1)
            return new(FeeOpportunityStatus.UnsupportedFeeModel, FeeStatus.InvalidFeeMetadata, profile,
                breakdown.SetItem(0, breakdown[0] with { Warnings = breakdown[0].Warnings.Add("Fee currencies differ; USD/USDC conversion is not assumed. Combined fees and adjusted values are unresolved.") }),
                null, null, null, null, null, minimumEdge);
        try
        {
            var total = checked(breakdown.Sum(q => q.TotalFee!.Value)); var cost = checked(gross.GrossCost + total);
            var profit = checked(gross.GrossProfit - total); var edge = profit / gross.PairedQuantity;
            // L2 quantities do not identify exchange fill fragmentation. These are hypothetical per-level fees, never exact execution fees.
            return new(profit > 0 && edge >= minimumEdge ? FeeOpportunityStatus.FeeAdjustedDetected : FeeOpportunityStatus.FeeAdjustedNoEdge,
                FeeStatus.Estimated, profile, breakdown, total, cost, profit, edge, cost > 0 ? profit / cost : null, minimumEdge);
        }
        catch (OverflowException) { return new(FeeOpportunityStatus.UnsupportedFeeModel, FeeStatus.InvalidFeeMetadata, profile, breakdown, null, null, null, null, null, minimumEdge); }
    }
}
