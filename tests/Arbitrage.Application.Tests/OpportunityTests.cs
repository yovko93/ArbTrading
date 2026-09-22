using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Strategies;

namespace Arbitrage.Application.Tests;

public sealed class OpportunityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private static readonly OrderBookInstrumentId A = new("Kalshi", "a", "yes", "Yes"), B = new("Polymarket", "b", "123", "No");
    private static ApprovedRelationship Relationship(VerificationState state = VerificationState.VerifiedDeterministic) => new(Guid.Parse("00000000-0000-0000-0000-000000000001"),
        RelationshipType.EquivalentOppositeOutcome, state, [new("yes", "123", RelationshipType.EquivalentOppositeOutcome)], new("Kalshi", "a"), new("Polymarket", "b"), new(), new(), new());
    private static OpportunityPlan Plan() => new(Relationship(), OpportunityStrategy.CrossMarketBuyBothComplements, A, B, DepthAction.Buy, DepthAction.Buy, true);
    private static CachedOrderBook Book(OrderBookInstrumentId id, params (decimal Price, decimal Quantity)[] asks) => Cached(OrderBookNormalizer.Normalize(id, [], asks.Select(l => new OrderBookLevel(l.Price, l.Quantity, LiquidityOrigin.NativeAsk)), At));
    private static CachedOrderBook Cached(OrderBookSnapshot book) => new(book, null, BookEligibility.Evaluate(book, At, TimeSpan.FromSeconds(5)), Version: 1);
    private static ArbitrageOpportunitySnapshot Evaluate(CachedOrderBook a, CachedOrderBook b, OpportunitySettings? settings = null, OpportunityPlan? plan = null, bool manual = false) =>
        GrossOpportunityEvaluator.Evaluate(plan ?? Plan(), a, b, settings ?? new(), At, manual);
    [Fact] public void Case_A_walks_all_paired_segments_exactly()
    {
        var r = Evaluate(Book(A, (.40m, 10), (.43m, 20)), Book(B, (.50m, 5), (.52m, 20)), new(0));
        Assert.Equal(OpportunityStatus.Detected, r.Status); Assert.Equal(25m, r.PairedQuantity);
        Assert.Equal(23.35m, r.GrossCost); Assert.Equal(25m, r.GuaranteedGrossPayout); Assert.Equal(1.65m, r.GrossProfit);
        Assert.Equal(new decimal[] { 5, 5, 15 }, r.Segments.Select(s => s.Quantity));
        Assert.Equal(new decimal[] { .90m, .92m, .95m }, r.Segments.Select(s => s.CombinedPrice));
        Assert.Equal(new decimal[] { 4.5m, 9.1m, 23.35m }, r.Segments.Select(s => s.CumulativeCost));
        Assert.Equal(10.45m, r.Legs[0].GrossNotional); Assert.Equal(12.9m, r.Legs[1].GrossNotional);
        Assert.False(r.ExecutionEligible); Assert.Null(r.NetProfit); Assert.Null(r.NetEdge); Assert.Equal("NotEvaluated", r.FeeStatus);
    }
    [Fact] public void Case_B_stops_before_negative_marginal_edge()
    {
        var r = Evaluate(Book(A, (.49m, 100)), Book(B, (.48m, 10), (.52m, 100)));
        Assert.Equal(10m, r.PairedQuantity); Assert.Equal(9.7m, r.GrossCost); Assert.Equal(.3m, r.GrossProfit); Assert.Single(r.Segments);
    }
    [Fact] public void Case_C_depth_is_limited_by_both_legs_and_requested_quantity_is_separate()
    {
        var r = Evaluate(Book(A, (.4m, 7)), Book(B, (.5m, 100)), new(RequestedQuantity: 10));
        Assert.Equal(7, r.PairedQuantity); Assert.False(r.FullyExecutableForRequestedQuantity);
        var smaller = Evaluate(Book(A, (.4m, 7)), Book(B, (.5m, 100)), new(RequestedQuantity: 3));
        Assert.Equal(3, smaller.PairedQuantity); Assert.True(smaller.FullyExecutableForRequestedQuantity);
    }
    [Theory] [InlineData("0.4995", "0.001", false)] [InlineData("0.499", "0.001", true)] [InlineData("0.5", "0", false)]
    public void Cases_D_E_positive_threshold_equality_qualifies_but_zero_never_does(string price, string minimum, bool detected)
    {
        var r = Evaluate(Book(A, (.5m, 1)), Book(B, (decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), 1)),
            new(decimal.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(detected, r.GrossArbitrageExists); Assert.Equal(detected ? OpportunityStatus.Detected : OpportunityStatus.NoGrossEdge, r.Status);
    }
    [Theory] [InlineData(VerificationState.Proposed)] [InlineData(VerificationState.NeedsReview)] [InlineData(VerificationState.Rejected)] [InlineData(VerificationState.Stale)] [InlineData(VerificationState.VerifiedManual)]
    public void Unapproved_or_default_manual_never_detected(VerificationState state)
    {
        var r = Evaluate(Book(A, (.4m, 1)), Book(B, (.5m, 1)), plan: Plan() with { Relationship = Relationship(state) });
        Assert.Equal(OpportunityStatus.RelationshipIneligible, r.Status); Assert.False(r.RelationshipEligible);
    }
    [Fact] public void Manual_opt_in_is_tagged_and_policy_mismatch_is_blocked()
    {
        var plan = Plan() with { Relationship = Relationship(VerificationState.VerifiedManual) };
        var r = Evaluate(Book(A, (.4m, 1)), Book(B, (.5m, 1)), plan: plan, manual: true);
        Assert.Equal(OpportunityStatus.Detected, r.Status); Assert.Equal(RelationshipTrust.Manual, r.RelationshipTrust);
        Assert.Equal(OpportunityStatus.RelationshipIneligible, Evaluate(Book(A, (.4m, 1)), Book(B, (.5m, 1)), plan: plan with { Relationship = plan.Relationship with { PolicyVersion = -1 } }, manual: true).Status);
    }
    [Fact] public void Same_outcome_mapping_is_not_complement_even_when_row_uses_inverse_wording()
    {
        var r = Relationship() with { Mappings = [new("yes", "123", RelationshipType.EquivalentSameOutcome)] };
        var plans = OpportunityPlanner.Plan(r);
        Assert.Equal(2, plans.Count); Assert.All(plans, p => Assert.False(p.ComplementProven));
        var bid = Cached(OrderBookNormalizer.Normalize(B, [new(.45m, 10, LiquidityOrigin.NativeBid)], [], At));
        var plan = plans.Single(p => p.A.NativeInstrumentId == A.NativeInstrumentId) with { A = A, B = B };
        Assert.Equal(OpportunityStatus.RequiresInventory, Evaluate(Book(A, (.4m, 10)), bid, plan: plan).Status);
    }
    [Fact] public void Stable_key_ignores_labels_order_and_snapshot_guids_but_keeps_direction_and_scoping()
    {
        var p = Plan(); var key = GrossOpportunityEvaluator.Key(p);
        Assert.Equal(key, GrossOpportunityEvaluator.Key(p with { A = p.B with { Outcome = "renamed" }, B = p.A }));
        Assert.NotEqual(key, GrossOpportunityEvaluator.Key(p with { ActionB = DepthAction.Sell }));
        Assert.NotEqual(key, GrossOpportunityEvaluator.Key(p with { B = p.B with { Exchange = "Kalshi" } }));
        Assert.Equal(key, Evaluate(Book(A, (.4m, 1)), Book(B, (.5m, 1))).OpportunityKey);
    }
    [Theory] [InlineData(0)] [InlineData(1)]
    public void Missing_purchase_side_is_insufficient_liquidity(int side)
    {
        var r = Evaluate(Book(A, (.4m, side == 0 ? 0 : 1)), Book(B, (.5m, side == 1 ? 0 : 1)));
        Assert.Equal(OpportunityStatus.InsufficientLiquidity, r.Status);
    }
    [Fact] public void Fractional_tiny_and_large_depth_obey_caps_without_integer_rounding()
    {
        var r = Evaluate(Book(A, (.4m, .000000001m)), Book(B, (.5m, 100)));
        Assert.Equal(.000000001m, r.PairedQuantity); Assert.Equal(.0000000009m, r.GrossCost);
        r = Evaluate(Book(A, (.4m, decimal.MaxValue)), Book(B, (.5m, decimal.MaxValue)), new(MaximumEvaluationQuantity: 1_000_000_000m));
        Assert.Equal(1_000_000_000m, r.PairedQuantity); Assert.Equal(900_000_000m, r.GrossCost); Assert.True(r.EvaluationQuantityCapped);
        Assert.Throws<ArgumentException>(() => Evaluate(Book(A), Book(B), new(MaximumEvaluationQuantity: decimal.MaxValue)));
        Assert.Throws<ArgumentException>(() => Evaluate(Book(A), Book(B), new(MaximumEvaluationQuantity: 0)));
    }
    [Fact] public void Notional_limit_never_exceeds_cap_and_zero_cost_has_unknown_return()
    {
        var r = Evaluate(Book(A, (.4m, 100)), Book(B, (.5m, 100)), new(MaximumEvaluationNotional: 4.5m));
        Assert.Equal(5m, r.PairedQuantity); Assert.Equal(4.5m, r.GrossCost); Assert.True(r.EvaluationNotionalCapped);
        r = Evaluate(Book(A, (.4m, 100)), Book(B, (.3m, 100)), new(MaximumEvaluationNotional: 1m));
        Assert.True(r.GrossCost <= 1m); // Decimal division must never round through the cap.
        r = Evaluate(Book(A, (0, 1)), Book(B, (0, 1))); Assert.Equal(1m, r.GrossProfit); Assert.Null(r.GrossReturnOnCost);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    [Theory]
    [InlineData("rest", OpportunityStatus.Detected)] [InlineData("stale", OpportunityStatus.BookStale)]
    [InlineData("missing", OpportunityStatus.BookUnavailable)] [InlineData("invalid", OpportunityStatus.BookInvalid)]
    [InlineData("skew", OpportunityStatus.BookSkewTooLarge)] [InlineData("AwaitingAnchor", OpportunityStatus.BookContinuityInsufficient)]
    [InlineData("GapDetected", OpportunityStatus.BookContinuityInsufficient)] [InlineData("Resynchronizing", OpportunityStatus.BookContinuityInsufficient)]
    public void Canonical_cache_eligibility_is_reused(string scenario, OpportunityStatus status)
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock);
        cache.Store(Book(B, (.5m, 5)).Snapshot!);
        if (scenario != "missing") cache.Store(Book(A, (.4m, 5)).Snapshot!);
        if (scenario == "stale") clock.Now = At.AddSeconds(6);
        if (scenario == "invalid") cache.Fail(A, "InvalidOrderBook");
        if (scenario == "skew") clock.Now = At.AddMilliseconds(1500);
        if (scenario == "skew") cache.Store(OrderBookNormalizer.Normalize(B, [], [new(.5m, 5, LiquidityOrigin.NativeAsk)], At.AddMilliseconds(1500)));
        if (Enum.TryParse<BookContinuity>(scenario, out var continuity)) cache.PublishRealtime(A, new(1, RealtimeSubscriptionState.Streaming, continuity, true, AnchorAt: At), Book(A, (.4m, 5)).Snapshot);
        var books = cache.ReadTogether(A, B); var r = Evaluate(books[0], books[1]); Assert.Equal(status, r.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Continuous_and_best_effort_quality_remain_explicit(bool mixedVenues)
    {
        var second = mixedVenues ? B : new("Kalshi", "other", "no", "No"); var cache = new OrderBookCache(new Clock());
        cache.PublishRealtime(A, new(1, RealtimeSubscriptionState.Streaming, BookContinuity.Continuous, true, AnchorAt: At), Book(A, (.4m, 5)).Snapshot);
        cache.PublishRealtime(second, new(1, RealtimeSubscriptionState.Streaming, mixedVenues ? BookContinuity.BestEffort : BookContinuity.Continuous, true, AnchorAt: At), Book(second, (.5m, 5)).Snapshot);
        var pair = cache.ReadTogether(A, second); var r = Evaluate(pair[0], pair[1], plan: Plan() with { B = second });
        Assert.Equal(OpportunityStatus.Detected, r.Status); Assert.Equal(mixedVenues ? OpportunityInputQuality.RealtimeBestEffort : OpportunityInputQuality.RealtimeContinuous, r.InputQuality);
        Assert.False(r.ExecutionEligible);
    }
    [Theory] [InlineData(10)] [InlineData(3)]
    public void Kalshi_native_no_bid_and_derived_yes_ask_are_one_source_even_for_partial_use(int quantity)
    {
        var no = A with { NativeInstrumentId = "no", Outcome = "No" };
        var yesBook = OrderBookNormalizer.NormalizeBinary(A, [], [new(.4m, 10, LiquidityOrigin.NativeBid)], At);
        var noBook = OrderBookNormalizer.NormalizeBinary(no, [], [new(.4m, 10, LiquidityOrigin.NativeBid)], At);
        Assert.Equal(yesBook.LiquiditySource(yesBook.Asks[0]), noBook.LiquiditySource(noBook.Bids[0]));
        var r = Evaluate(Cached(yesBook), Cached(noBook), new(RequestedQuantity: quantity), Plan() with { B = no, ActionB = DepthAction.Sell });
        Assert.Equal(OpportunityStatus.LiquidityConflict, r.Status); Assert.Equal(0m, r.PairedQuantity);
    }
    [Fact] public void Independent_derived_liquidity_is_allowed_and_native_level_identity_is_scoped()
    {
        var yes = OrderBookNormalizer.NormalizeBinary(A, [], [new(.6m, 10, LiquidityOrigin.NativeBid), new(.55m, 10, LiquidityOrigin.NativeBid)], At);
        Assert.NotEqual(yes.LiquiditySource(yes.Asks[0]), yes.LiquiditySource(yes.Asks[1]));
        var r = Evaluate(Cached(yes), Book(B, (.5m, 5)));
        Assert.Equal(OpportunityStatus.Detected, r.Status); Assert.Contains(LiquidityOrigin.DerivedComplement, r.Legs[0].LiquidityOrigins);
        Assert.NotEqual(r.Legs[0].LiquiditySources[0], r.Legs[1].LiquiditySources[0]);
    }
    [Fact] public void Cache_versions_detect_replacement_and_immutable_capture_does_not_change()
    {
        var cache = new OrderBookCache(new Clock()); cache.Store(Book(A, (.4m, 1)).Snapshot!); cache.Store(Book(B, (.5m, 1)).Snapshot!);
        var captured = cache.ReadTogether(A, B); cache.Store(Book(A, (.45m, 1)).Snapshot!);
        Assert.False(cache.VersionsMatch([A, B], captured.Select(c => c.Version).ToArray())); Assert.Equal(.4m, captured[0].Snapshot!.Asks[0].Price);
    }
    private static SemanticFact<T> Fact<T>(T value) => new(value, FactSource.Rules, "fixture:complete-settlement-rule");
    private static CanonicalMarketDescriptor Proven(string exchange, string id, string yes, string no) => new()
    {
        Identity = new(exchange, id), Title = "Will A win?", RulesText = "Fixture complete settlement rules", ResolutionSource = "fixture:authority", MarketStructure = "Binary",
        Outcomes = [new(yes, "Yes", Fact("a-win"), ContractPolarity.Positive), new(no, "No", Fact("a-win"), ContractPolarity.Negative)],
        Semantics = new() { Subject = Fact("a"), Predicate = Fact("win"), Geography = Fact("US"), Event = Fact("election"), Edition = Fact("2028"), Stage = Fact("final"),
            Authority = Fact("certified-result"), SettlementQualifiers = Fact("certified-final; void-if-cancelled"), ThresholdApplicable = Fact(false),
            Window = Fact(new ObservationWindow(null, null, new DateTimeOffset(2028, 12, 31, 0, 0, 0, TimeSpan.Zero), false, "UTC", "Deadline")),
            Polarity = Fact(ContractPolarity.Positive), OutcomeSet = new(Fact(TruthValue.True), Fact(TruthValue.True)) }
    };
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Same_and_inverse_propositions_use_proven_opposite_instruments_and_native_single_market_partition(bool inverse)
    {
        var a = Proven("Kalshi", "a", "yes", "no"); var b = Proven("Polymarket", "b", "123", "456");
        if (inverse) b = b with { Semantics = b.Semantics with { Polarity = Fact(ContractPolarity.Negative) },
            Outcomes = [b.Outcomes[0] with { Polarity = ContractPolarity.Negative }, b.Outcomes[1] with { Polarity = ContractPolarity.Positive }] };
        var proof = new RelationshipValidator().Validate(a, b); Assert.Equal(VerificationState.VerifiedDeterministic, proof.State);
        var r = Relationship() with { Type = proof.Type, Mappings = proof.Mappings, SourceDescriptor = a, TargetDescriptor = b };
        var plans = OpportunityPlanner.Plan(r);
        var cross = plans.Where(p => p.Strategy == OpportunityStrategy.CrossMarketBuyBothComplements).ToArray(); Assert.Equal(2, cross.Length);
        Assert.All(cross, p =>
        {
            var first = p.A.Exchange == "Kalshi" ? a.Outcomes.Single(o => o.NativeId == p.A.NativeInstrumentId) : b.Outcomes.Single(o => o.NativeId == p.A.NativeInstrumentId);
            var second = p.B.Exchange == "Kalshi" ? a.Outcomes.Single(o => o.NativeId == p.B.NativeInstrumentId) : b.Outcomes.Single(o => o.NativeId == p.B.NativeInstrumentId);
            Assert.NotEqual(first.Polarity, second.Polarity);
        });
        var single = plans.Single(p => p.Strategy == OpportunityStrategy.SingleMarketBinaryComplement && p.A.Exchange == "Polymarket");
        var result = Evaluate(Book(single.A, (.4m, 7)), Book(single.B, (.5m, 9)), plan: single);
        Assert.Equal(OpportunityStatus.Detected, result.Status); Assert.Equal(7, result.GuaranteedGrossPayout);
        var unproven = r with { SourceDescriptor = a with { Semantics = new() }, TargetDescriptor = b with { Semantics = new() } };
        Assert.DoesNotContain(OpportunityPlanner.Plan(unproven), p => p.Strategy == OpportunityStrategy.SingleMarketBinaryComplement || p.ComplementProven);
    }
    [Fact] public void Only_explicit_two_outcome_exhaustive_partition_supports_set_basket()
    {
        var r = Relationship() with { Type = RelationshipType.MutuallyExclusive, Mappings = [new("yes", "123", RelationshipType.MutuallyExclusive)] };
        Assert.False(Assert.Single(OpportunityPlanner.Plan(r)).ComplementProven);
        r = r with { SelectedOutcomeSet = new(Fact(TruthValue.True), Fact(TruthValue.True)) };
        Assert.True(Assert.Single(OpportunityPlanner.Plan(r)).ComplementProven);
        r = r with { Mappings = [.. r.Mappings, new("yes", "456", RelationshipType.MutuallyExclusive)] };
        Assert.All(OpportunityPlanner.Plan(r), p => Assert.False(p.ComplementProven));
    }
    [Fact] public void Conflicting_partition_facts_cannot_turn_same_economic_outcomes_into_complements()
    {
        var r = Relationship() with { Mappings = [new("yes", "123", RelationshipType.EquivalentSameOutcome)], SelectedOutcomeSet = new(Fact(TruthValue.True), Fact(TruthValue.True)) };
        var plan = Assert.Single(OpportunityPlanner.Plan(r)); Assert.False(plan.ComplementProven);
        Assert.Equal(OpportunityStatus.UnsupportedStrategy, Evaluate(Book(A, (.4m, 10)), Book(B, (.5m, 10)), plan: plan with { A = A, B = B }).Status);
    }
}
