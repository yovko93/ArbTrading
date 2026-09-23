using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Strategies;

// Pure, immutable-input evaluation. No fee, account, inventory or external data assumptions.
public static class GrossOpportunityEvaluator
{
    public static string Key(OpportunityPlan plan)
    {
        var legs = new[] { new { plan.A.Exchange, plan.A.NativeMarketId, plan.A.NativeInstrumentId, Action = plan.ActionA },
            new { plan.B.Exchange, plan.B.NativeMarketId, plan.B.NativeInstrumentId, Action = plan.ActionB } }
            .OrderBy(l => l.Exchange, StringComparer.Ordinal).ThenBy(l => l.NativeMarketId, StringComparer.Ordinal).ThenBy(l => l.NativeInstrumentId, StringComparer.Ordinal).ThenBy(l => l.Action);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan.Strategy, plan.Relationship.Id, Legs = legs })));
    }
    public static ArbitrageOpportunitySnapshot Evaluate(OpportunityPlan plan, CachedOrderBook a, CachedOrderBook b, OpportunitySettings settings,
        DateTimeOffset now, bool includeManual = false, CancellationToken ct = default)
    {
        if (!settings.Valid) throw new ArgumentException("Invalid diagnostic evaluation settings.");
        var relationship = plan.Relationship;
        var eligible = relationship.IsStrategyEligible && relationship.PolicyVersion == RelationshipPolicy.Version &&
            (relationship.State == VerificationState.VerifiedDeterministic || includeManual && relationship.State == VerificationState.VerifiedManual);
        var skew = a.Snapshot is { } sa && b.Snapshot is { } sb ? (sa.RetrievedAtUtc - sb.RetrievedAtUtc).Duration() : (TimeSpan?)null;
        var maximumSkew = TimeSpan.FromMilliseconds(settings.MaximumSkewMilliseconds);
        var warnings = ImmutableArray.Create("PRE-FEE: fees, balances and execution are not evaluated.", "Local coherent cache read; exchange observations are not atomic.");
        if (a.Realtime?.Continuity == BookContinuity.BestEffort || b.Realtime?.Continuity == BookContinuity.BestEffort) warnings = warnings.Add("Polymarket BestEffort continuity is diagnostic only.");
        var result = new ArbitrageOpportunitySnapshot(Key(plan), plan.Strategy, relationship.Id,
            relationship.State == VerificationState.VerifiedManual ? RelationshipTrust.Manual : RelationshipTrust.Deterministic, now.ToUniversalTime(), OpportunityStatus.NoGrossEdge,
            [], warnings, Quality(a, b), skew, maximumSkew, skew is { } s && s <= maximumSkew,
            [Leg(plan.A, plan.ActionA, a), Leg(plan.B, plan.ActionB, b)], [], 0, 0, 0, 0, 0, 0, null, null,
            eligible, a.Eligibility.IsActionable && b.Eligibility.IsActionable, false, false, false, relationship.PolicyVersion,
            relationship.SourceFingerprint, relationship.TargetFingerprint, relationship.Revision)
        { SourceTitle = relationship.SourceDescriptor?.Title, TargetTitle = relationship.TargetDescriptor?.Title, OutcomeMappings = relationship.Mappings.ToImmutableArray() };
        if (!eligible) return result.Invalidate(OpportunityStatus.RelationshipIneligible, "Approved current relationship required; manual trust is opt-in.") with { RelationshipEligible = false };
        foreach (var input in new[] { a, b })
        {
            if (input.Snapshot is null) return result.Invalidate(input.Failure?.Code == "InvalidOrderBook" ? OpportunityStatus.BookInvalid : OpportunityStatus.BookUnavailable, input.Failure?.Code ?? "BookUnavailable");
            if (input.Snapshot.Validity != BookValidity.Valid || input.Failure?.Code == "InvalidOrderBook") return result.Invalidate(OpportunityStatus.BookInvalid, "Invalid or unsupported canonical book.");
            if (input.Eligibility.Freshness == BookFreshness.Stale) return result.Invalidate(OpportunityStatus.BookStale, "BookStale");
            if (!input.Eligibility.IsActionable) return result.Invalidate(input.Source == BookSourceMode.Realtime ? OpportunityStatus.BookContinuityInsufficient : OpportunityStatus.BookUnavailable, input.Eligibility.Reason ?? "NonActionable");
        }
        if (a.Snapshot!.Instrument != plan.A || b.Snapshot!.Instrument != plan.B) return result.Invalidate(OpportunityStatus.BookInvalid, "Instrument mismatch.");
        if (!result.SkewAcceptable) return result.Invalidate(OpportunityStatus.BookSkewTooLarge, "Observation skew exceeds the diagnostic limit.");
        var levelsA = plan.ActionA == DepthAction.Buy ? a.Snapshot.Asks : a.Snapshot.Bids;
        var levelsB = plan.ActionB == DepthAction.Buy ? b.Snapshot.Asks : b.Snapshot.Bids;
        // Conservative shared-origin rejection includes native and synthetic logical views, even partial use.
        var originsA = levelsA.Select(a.Snapshot.LiquiditySource).ToHashSet();
        if (originsA.Contains(null) || levelsB.Any(l => b.Snapshot.LiquiditySource(l) is not { } origin || originsA.Contains(origin)))
            return result.Invalidate(OpportunityStatus.LiquidityConflict, "Legs share native resting liquidity, or its identity is unavailable.");
        if (plan.ActionA == DepthAction.Sell || plan.ActionB == DepthAction.Sell) return result.Invalidate(OpportunityStatus.RequiresInventory, "Selling without inventory is unsupported; a price spread is not a locked position.");
        if (!plan.ComplementProven || plan.UnsupportedReason is not null) return result.Invalidate(OpportunityStatus.UnsupportedStrategy, plan.UnsupportedReason ?? "No proved complementary payout.");
        if (levelsA.Length == 0 || levelsB.Length == 0) return result.Invalidate(OpportunityStatus.InsufficientLiquidity, "Both purchase sides require depth.");
        result = result with { BestObservedGrossEdge = 1m - levelsA[0].Price - levelsB[0].Price,
            BestObservedQuantity = Math.Min(Math.Min(levelsA[0].Quantity, levelsB[0].Quantity), settings.MaximumEvaluationQuantity) };
        try
        {
            checked
            {
                var i = 0; var j = 0; decimal usedA = 0, usedB = 0, quantity = 0, cost = 0;
                var cap = Math.Min(settings.MaximumEvaluationQuantity, settings.RequestedQuantity ?? settings.MaximumEvaluationQuantity);
                var segments = ImmutableArray.CreateBuilder<PairedDepthSegment>(); bool notionalCapped = false, quantityCapped = false;
                while (i < levelsA.Length && j < levelsB.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var price = levelsA[i].Price + levelsB[j].Price; var edge = 1m - price;
                    // Zero edge is never an opportunity; positive equality at the configured threshold qualifies.
                    if (edge <= 0 || edge < settings.MinimumGrossEdgePerShare) break;
                    var available = Math.Min(levelsA[i].Quantity - usedA, levelsB[j].Quantity - usedB);
                    var take = Math.Min(available, cap - quantity);
                    if (settings.MaximumEvaluationNotional is { } notional && price > 0 && take * price > notional - cost)
                    {
                        take = (notional - cost) / price;
                        // Decimal division can round upward. Step down one representable unit in that case.
                        if (cost + take * price > notional)
                            take -= new decimal(1, 0, 0, false, (byte)((decimal.GetBits(take)[3] >> 16) & 0xff));
                        notionalCapped = true;
                    }
                    if (take <= 0) break;
                    quantity += take; cost += take * price;
                    segments.Add(new(take, levelsA[i].Price, levelsB[j].Price, price, edge, cost, quantity, quantity - cost));
                    usedA += take; usedB += take;
                    if (usedA == levelsA[i].Quantity) { i++; usedA = 0; } if (usedB == levelsB[j].Quantity) { j++; usedB = 0; }
                    if (quantity == cap)
                    { quantityCapped = cap == settings.MaximumEvaluationQuantity && i < levelsA.Length && j < levelsB.Length; break; }
                    if (notionalCapped) break;
                }
                if (quantity == 0) return result with { Status = OpportunityStatus.NoGrossEdge, Blockers = ["No positive marginal edge meets the minimum."] };
                var da = ExecutableDepth.Calculate(a.Snapshot, a.Eligibility, DepthAction.Buy, quantity);
                var db = ExecutableDepth.Calculate(b.Snapshot, b.Eligibility, DepthAction.Buy, quantity);
                // Reuse canonical leg depth calculations, and require exact agreement with the paired walk.
                var exactCost = da.GrossNotional + db.GrossNotional;
                if (exactCost != cost || settings.MaximumEvaluationNotional is { } limit && exactCost > limit || !da.IsFullyExecutable || !db.IsFullyExecutable)
                    return result.Invalidate(OpportunityStatus.BookInvalid, "Paired and individual depth disagree, or decimal rounding exceeds the notional limit.");
                if (quantity - cost <= 0) return result with { Status = OpportunityStatus.NoGrossEdge, Blockers = ["No representable positive gross profit."] };
                return result with { Status = OpportunityStatus.Detected, Segments = segments.ToImmutable(), PairedQuantity = quantity, FullyExecutableQuantity = quantity,
                    GrossCost = cost, GuaranteedGrossPayout = quantity, GrossProfit = quantity - cost, GrossEdgePerShare = (quantity - cost) / quantity,
                    GrossReturnOnCost = cost == 0 ? null : (quantity - cost) / cost, FullyExecutableForRequestedQuantity = settings.RequestedQuantity is { } requested && quantity >= requested,
                    EvaluationQuantityCapped = quantityCapped, EvaluationNotionalCapped = notionalCapped,
                    Legs = [Consumed(result.Legs[0], a.Snapshot, da), Consumed(result.Legs[1], b.Snapshot, db)] };
            }
        }
        catch (OverflowException) { return result.Invalidate(OpportunityStatus.ArithmeticOverflow, "Decimal capacity exceeded; no candidate published."); }
    }
    private static OpportunityLeg Leg(OrderBookInstrumentId id, DepthAction action, CachedOrderBook book) => new(id, action, 0, null, null, 0, book.Source,
        book.Realtime?.Continuity ?? BookContinuity.NotApplicable, book.Version, book.Snapshot?.RetrievedAtUtc, book.Snapshot?.SourceTimestamp, [], []);
    private static OpportunityLeg Consumed(OpportunityLeg leg, OrderBookSnapshot book, GrossDepthEstimate depth) => leg with
    { Quantity = depth.ExecutableQuantity, AveragePrice = depth.Vwap, WorstPrice = depth.WorstPrice, GrossNotional = depth.GrossNotional,
        LiquidityOrigins = book.Asks.Take(depth.LevelsConsumed).Select(l => l.Origin).Distinct().ToImmutableArray(),
        LiquiditySources = book.Asks.Take(depth.LevelsConsumed).Select(l => book.LiquiditySource(l)!).Distinct().ToImmutableArray() };
    private static OpportunityInputQuality Quality(CachedOrderBook a, CachedOrderBook b) =>
        !a.Eligibility.IsActionable || !b.Eligibility.IsActionable ? OpportunityInputQuality.NonActionable :
        a.Source != b.Source ? OpportunityInputQuality.Mixed : a.Source == BookSourceMode.RestSnapshot ? OpportunityInputQuality.FreshRest :
        a.Realtime?.Continuity == BookContinuity.Continuous && b.Realtime?.Continuity == BookContinuity.Continuous ? OpportunityInputQuality.RealtimeContinuous : OpportunityInputQuality.RealtimeBestEffort;
}
