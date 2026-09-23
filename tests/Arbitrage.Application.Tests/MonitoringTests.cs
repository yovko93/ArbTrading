using Arbitrage.Domain;
using Arbitrage.Strategies;

namespace Arbitrage.Application.Tests;

public sealed class MonitoringTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    internal static OpportunityPlan Plan(int i = 0)
    {
        var a = new OrderBookInstrumentId("Polymarket", "a" + i, "100" + i, "Yes");
        var b = new OrderBookInstrumentId("Polymarket", "b" + i, "200" + i, "No");
        var relationship = new ApprovedRelationship(new Guid(i, 0, 0, new byte[8]), RelationshipType.EquivalentOppositeOutcome,
            VerificationState.VerifiedDeterministic, [new(a.NativeInstrumentId, b.NativeInstrumentId, RelationshipType.EquivalentOppositeOutcome)],
            new(a.Exchange, a.NativeMarketId), new(b.Exchange, b.NativeMarketId), new(), new(), new());
        return new(relationship, OpportunityStrategy.CrossMarketBuyBothComplements, a, b, DepthAction.Buy, DepthAction.Buy, true);
    }
    internal static ArbitrageOpportunitySnapshot Result(decimal edge, int i = 0)
    {
        var p = Plan(i);
        CachedOrderBook Book(OrderBookInstrumentId id, decimal price)
        {
            var book = OrderBookNormalizer.Normalize(id, [], [new(price, 10, LiquidityOrigin.NativeAsk)], At);
            return new(book, null, BookEligibility.Evaluate(book, At, TimeSpan.FromSeconds(5)), Version: 1);
        }
        return GrossOpportunityEvaluator.Evaluate(p, Book(p.A, .5m), Book(p.B, .5m - edge), new(.005m), At);
    }
    [Fact] public void Alert_crossing_hysteresis_and_cooldown_match_required_sequence()
    {
        var state = new OpportunityAlertState(); var now = At;
        bool Observe(decimal edge) => state.Observe(edge, 1, true, .005m, .01m, .001m, TimeSpan.FromSeconds(60), now);
        Assert.False(Observe(.0049m)); Assert.True(Observe(.0050m)); Assert.False(Observe(.0060m));
        Assert.False(Observe(.0045m)); Assert.False(state.Armed); Assert.False(Observe(.0039m)); Assert.True(state.Armed);
        now = now.AddSeconds(30); Assert.False(Observe(.0051m)); Assert.Equal("Cooldown", state.State);
        now = now.AddSeconds(31); Assert.False(Observe(.0052m)); Assert.False(Observe(.0039m)); Assert.True(Observe(.0051m));
    }
    [Fact] public void Invalid_inputs_do_not_rearm_and_disabled_lanes_do_not_consume_crossings()
    {
        var state = new OpportunityAlertState();
        Assert.False(state.Observe(.01m, 1, true, .005m, 0, .001m, TimeSpan.Zero, At, false));
        Assert.True(state.Observe(.01m, 1, true, .005m, 0, .001m, TimeSpan.Zero, At));
        Assert.False(state.Observe(0, 1, false, .005m, 0, .001m, TimeSpan.Zero, At)); Assert.False(state.Armed);
        Assert.False(state.Observe(.01m, 1, true, .005m, 0, .001m, TimeSpan.Zero, At));
    }
    [Fact] public void Zero_threshold_requires_positive_metric_and_zero_hysteresis()
    {
        Assert.False((new MonitoringProfile(FeeAlertEdge: 0)).Valid);
        Assert.True((new MonitoringProfile(FeeAlertEdge: 0, RearmHysteresis: 0)).Valid);
        var state = new OpportunityAlertState();
        Assert.False(state.Observe(0, 0, true, 0, 0, 0, TimeSpan.Zero, At));
        Assert.True(state.Observe(.001m, 1, true, 0, 0, 0, TimeSpan.Zero, At));
        Assert.False(state.Observe(0, 0, true, 0, 0, 0, TimeSpan.Zero, At)); Assert.True(state.Armed);
    }
    [Fact] public void Near_edge_reuses_gross_guards_and_never_turns_stale_into_a_candidate()
    {
        var result = Result(.0049m); Assert.Equal(OpportunityStatus.NoGrossEdge, result.Status);
        Assert.Equal(.0049m, result.BestObservedGrossEdge); Assert.Equal(10, result.BestObservedQuantity);
        var profile = new MonitoringProfile(MinimumGrossEdge: .005m);
        var row = MonitoringRanking.Classify(result, 1, profile);
        Assert.Equal(RankingLane.NearEdge, row.Lane); Assert.Equal(.0001m, row.Distance);
        Assert.Equal(RankingLane.Blocked, MonitoringRanking.Classify(result.Invalidate(OpportunityStatus.BookStale, "stale"), 2, profile).Lane);
        Assert.Equal(RankingLane.GrossOnly, MonitoringRanking.Classify(Result(.01m), 3, profile).Lane);
        Assert.Equal(RankingLane.Blocked, MonitoringRanking.Classify(Result(-.01m), 4, profile).Lane);
    }
    [Fact] public void Default_rank_is_stable_and_alternate_sorts_keep_lanes_separate()
    {
        var profile = new MonitoringProfile(MinimumGrossEdge: .005m);
        var rows = new[] { MonitoringRanking.Classify(Result(.02m, 1), 1, profile), MonitoringRanking.Classify(Result(.01m, 2), 2, profile),
            MonitoringRanking.Classify(Result(.004m, 3), 3, profile), MonitoringRanking.Classify(Result(.0049m, 4), 4, profile) };
        foreach (var sort in MonitoringProfile.Sorts)
        {
            Assert.Equal(MonitoringRanking.Sort(rows, sort).Select(x => x.Result.OpportunityKey), MonitoringRanking.Sort(rows.Reverse(), sort).Select(x => x.Result.OpportunityKey));
            Assert.Equal(RankingLane.GrossOnly, MonitoringRanking.Sort(rows, sort).First().Lane);
        }
        Assert.Equal(new[] { rows[0], rows[1], rows[3], rows[2] }, MonitoringRanking.Sort(rows, "default"));
    }
    [Fact] public void Offline_500_relationship_1000_instrument_bursts_are_selective_deduplicated_and_bounded()
    {
        var index = new MonitoringDependencies(); var feed = new LocalInputChanges(128);
        var plans = Enumerable.Range(0, 500).Select(Plan).ToArray();
        foreach (var p in plans) index.Add(GrossOpportunityEvaluator.Key(p), p);
        Assert.Equal(500, index.Count);
        var current = Enumerable.Range(0, 500).Select(i => MonitoringRanking.Classify(Result(.01m, i), 1, new())).ToDictionary(x => x.Result.OpportunityKey);
        Assert.Equal(500, current.Count); Assert.All(current.Values, x => Assert.Equal(RankingLane.GrossOnly, x.Lane));
        var first = new LocalInputChange(LocalChangeKind.Instrument, plans[0].A.Exchange, plans[0].A.NativeMarketId, plans[0].A.NativeInstrumentId);
        for (var i = 0; i < 1000; i++) feed.Publish(first);
        Assert.Equal(1, feed.Count); Assert.Equal(999, feed.Coalesced); Assert.Single(index.Affected(first));
        foreach (var key in feed.Drain().Changes.SelectMany(index.Affected).Distinct()) current[key] = current[key] with { EvaluationGeneration = 2 };
        Assert.Single(current.Values, x => x.EvaluationGeneration == 2);
        foreach (var p in plans.Take(100)) feed.Publish(new(LocalChangeKind.Instrument, p.A.Exchange, p.A.NativeMarketId, p.A.NativeInstrumentId));
        var burst = feed.Drain(); Assert.False(burst.Overflow); Assert.Equal(100, burst.Changes.SelectMany(index.Affected).Distinct().Count());
        foreach (var key in burst.Changes.SelectMany(index.Affected).Distinct()) current[key] = current[key] with { EvaluationGeneration = 3 };
        Assert.Equal(100, current.Values.Count(x => x.EvaluationGeneration == 3)); Assert.Equal(500, current.Count);
        foreach (var p in plans) foreach (var id in new[] { p.A, p.B }) feed.Publish(new(LocalChangeKind.Instrument, id.Exchange, id.NativeMarketId, id.NativeInstrumentId));
        Assert.Equal(128, feed.Count); Assert.Equal(872, feed.Dropped); Assert.True(feed.Drain().Overflow); Assert.Equal(0, feed.Count);
    }
}
