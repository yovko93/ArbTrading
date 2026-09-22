using Arbitrage.Domain;

namespace Arbitrage.Domain.Tests;

public sealed class RelationshipTests
{
    private static SemanticFact<T> Fact<T>(T value) => new(value, FactSource.Rules, "fixture:complete-rule-clause");
    private static CanonicalMarketDescriptor Market(string exchange = "Polymarket") => new()
    {
        Identity = new(exchange, "fixture"), Title = "Will Person A win the 2028 election?", RulesText = "Fixture complete settlement rules", ResolutionSource = "fixture:official-authority", MarketStructure = "Binary",
        Outcomes = [new("yes", "Yes", Fact("person-a-election-win"), ContractPolarity.Positive), new("no", "No", Fact("person-a-election-win"), ContractPolarity.Negative)],
        Semantics = new() { Subject = Fact("person-a"), Predicate = Fact("win-election"), Geography = Fact("US"), Event = Fact("presidential-election"), Edition = Fact("2028"), Stage = Fact("final"),
            Authority = Fact("official-certified-result"), SettlementQualifiers = Fact("certified-final; void-if-cancelled"), ThresholdApplicable = Fact(false),
            Window = Fact(new ObservationWindow(null, null, new DateTimeOffset(2028, 12, 31, 0, 0, 0, TimeSpan.Zero), false, "UTC", "Deadline")),
            Polarity = Fact(ContractPolarity.Positive), OutcomeSet = new(Fact(TruthValue.True), Fact(TruthValue.True)) }
    };
    [Fact]
    public void Cosmetic_titles_and_explicit_yes_yes_mapping_do_not_change_proven_semantics()
    {
        var a = Market(); var b = Market("Kalshi") with { Title = " PERSON A — election winner (2028) " };
        var result = new RelationshipValidator().Validate(a, b);
        Assert.Equal(VerificationState.VerifiedDeterministic, result.State);
        Assert.Equal(RelationshipType.EquivalentSameOutcome, result.Type);
        Assert.Contains(result.Mappings, m => m.SourceOutcomeId == "yes" && m.TargetOutcomeId == "yes");
        Assert.Empty(result.BlockingDifferences);
    }
    [Fact]
    public void Inverse_proposition_maps_yes_to_no_by_meaning_not_label()
    {
        var a = Market(); var b = Market("Kalshi");
        b = b with { Title = "Will Person A NOT win?", Semantics = b.Semantics with { Polarity = Fact(ContractPolarity.Negative) },
            Outcomes = [b.Outcomes[0] with { Polarity = ContractPolarity.Negative }, b.Outcomes[1] with { Polarity = ContractPolarity.Positive }] };
        var result = new RelationshipValidator().Validate(a, b);
        Assert.Equal(VerificationState.VerifiedDeterministic, result.State); Assert.Equal(RelationshipType.EquivalentOppositeOutcome, result.Type);
        Assert.Contains(result.Mappings, m => m.SourceOutcomeId == "yes" && m.TargetOutcomeId == "no");
    }
    [Theory]
    [InlineData("predicate", EvidenceKind.ContradictorySemanticField)]
    [InlineData("year", EvidenceKind.ContradictoryDate)]
    [InlineData("geography", EvidenceKind.ContradictoryGeography)]
    [InlineData("operator", EvidenceKind.ContradictoryThreshold)]
    [InlineData("unit", EvidenceKind.ContradictoryThreshold)]
    [InlineData("inclusive", EvidenceKind.ContradictoryDate)]
    [InlineData("stage", EvidenceKind.ContradictorySemanticField)]
    [InlineData("person", EvidenceKind.ContradictorySemanticField)]
    [InlineData("rules", EvidenceKind.ContradictoryRules)]
    [InlineData("finality", EvidenceKind.ContradictoryRules)]
    [InlineData("authority", EvidenceKind.ContradictorySemanticField)]
    public void Material_contradictions_block_equivalence(string difference, EvidenceKind evidence)
    {
        var a = Market(); var b = Market("Kalshi");
        if (difference is "operator" or "unit")
        {
            a = a with { Semantics = a.Semantics with { ThresholdApplicable = Fact(true), Threshold = Fact(new SemanticThreshold("price", ThresholdOperator.GreaterThan, 50m, "USD")) } };
            b = b with { Semantics = a.Semantics with { Threshold = Fact(new SemanticThreshold("price", difference == "operator" ? ThresholdOperator.GreaterThanOrEqual : ThresholdOperator.GreaterThan, 50m, difference == "unit" ? "EUR" : "USD")) } };
        }
        b = difference switch
        {
            "predicate" => b with { Semantics = b.Semantics with { Predicate = Fact("become-party-nominee") } },
            "year" => b with { Semantics = b.Semantics with { Edition = Fact("2032") } },
            "geography" => b with { Semantics = b.Semantics with { Geography = Fact("California") } },
            "inclusive" => b with { Semantics = b.Semantics with { Window = Fact(b.Semantics.Window!.Value with { EndInclusive = true }) } },
            "stage" => b with { Semantics = b.Semantics with { Stage = Fact("semifinal") } },
            "person" => b with { Semantics = b.Semantics with { Subject = Fact("person-a-other") } },
            "rules" => b with { RulesText = "Opposing rules" },
            "finality" => b with { Semantics = b.Semantics with { SettlementQualifiers = Fact("preliminary-result") } },
            "authority" => b with { Semantics = b.Semantics with { Authority = Fact("other-authority") } }, _ => b
        };
        var result = new RelationshipValidator().Validate(a, b);
        Assert.Equal(VerificationState.Rejected, result.State);
        Assert.Contains(result.Evidence, e => e.Kind == evidence && e.Blocking && e.Contradiction);
        Assert.Empty(result.Mappings);
    }
    [Theory]
    [InlineData("rules", EvidenceKind.MissingRules)]
    [InlineData("timezone", EvidenceKind.MissingSemanticField)]
    [InlineData("authority", EvidenceKind.MissingSemanticField)]
    [InlineData("alias", EvidenceKind.MissingSemanticField)]
    [InlineData("exhaustive", EvidenceKind.UnknownExhaustiveness)]
    [InlineData("source", EvidenceKind.MissingResolutionSource)]
    public void Missing_material_facts_require_review(string missing, EvidenceKind kind)
    {
        var a = Market(); var b = Market("Kalshi");
        b = missing switch {
            "rules" => b with { RulesText = null }, "source" => b with { ResolutionSource = null },
            "timezone" => b with { Semantics = b.Semantics with { Window = null } },
            "authority" => b with { Semantics = b.Semantics with { Authority = null } },
            "alias" => b with { Semantics = b.Semantics with { Subject = new("A", FactSource.Title, "ambiguous surname") } },
            _ => b with { Semantics = b.Semantics with { OutcomeSet = new(Fact(TruthValue.True), null) } } };
        var result = new RelationshipValidator().Validate(a, b);
        Assert.Equal(VerificationState.NeedsReview, result.State); Assert.Contains(result.Evidence, e => e.Kind == kind && e.Blocking);
    }
    [Fact]
    public void Exact_decimal_threshold_is_verified_and_distinct_operators_are_retained()
    {
        var a = Market() with { Semantics = Market().Semantics with { ThresholdApplicable = Fact(true), Threshold = Fact(new SemanticThreshold("rate", ThresholdOperator.GreaterThanOrEqual, 50.00000001m, "percent")) } };
        var result = new RelationshipValidator().Validate(a, a with { Identity = new("Kalshi", "other") });
        Assert.Equal(VerificationState.VerifiedDeterministic, result.State);
        Assert.Contains(result.Evidence, e => e.Kind == EvidenceKind.ExactThreshold && !e.Blocking);
        Assert.NotEqual(RelationshipPolicy.Normalize("X > 50"), RelationshipPolicy.Normalize("X >= 50"));
        Assert.NotEqual(RelationshipPolicy.Normalize("X win"), RelationshipPolicy.Normalize("X not win"));
    }
    [Theory]
    [InlineData(TruthValue.True, TruthValue.True)]
    [InlineData(TruthValue.True, TruthValue.False)]
    [InlineData(TruthValue.False, TruthValue.True)]
    [InlineData(TruthValue.False, TruthValue.False)]
    [InlineData(TruthValue.True, TruthValue.Unknown)]
    public void Exclusivity_and_exhaustiveness_remain_independent(TruthValue exclusive, TruthValue exhaustive)
    {
        var a = Market(); a = a with { Semantics = a.Semantics with { OutcomeSet = new(Fact(exclusive), Fact(exhaustive)) } };
        Assert.Equal(exclusive, a.Semantics.OutcomeSet.MutuallyExclusive!.Value); Assert.Equal(exhaustive, a.Semantics.OutcomeSet.CollectivelyExhaustive!.Value);
        Assert.Equal(exclusive == TruthValue.True && exhaustive == TruthValue.True ? VerificationState.VerifiedDeterministic : VerificationState.NeedsReview,
            new RelationshipValidator().Validate(a, a with { Identity = new("Kalshi", "other") }).State);
        var set = new RelationshipValidator().ValidateOutcomeSet(a);
        Assert.Equal(exhaustive == TruthValue.Unknown ? VerificationState.NeedsReview : VerificationState.VerifiedDeterministic, set.State);
        if (exclusive == TruthValue.True && exhaustive == TruthValue.False) Assert.Equal(RelationshipType.MutuallyExclusive, set.Type);
        if (exclusive == TruthValue.False && exhaustive == TruthValue.True) Assert.Equal(RelationshipType.Exhaustive, set.Type);
    }
    [Fact]
    public void Group_metadata_never_supplies_missing_set_proof()
    {
        var a = Market() with { NativeGroupId = "negative-risk", MarketStructure = "NegativeRisk", Semantics = new() };
        Assert.Equal(VerificationState.NeedsReview, new RelationshipValidator().Validate(a, a with { Identity = new("Polymarket", "another") }).State);
    }
    [Fact]
    public void Binary_complement_requires_proven_partition_and_opposite_meaning_polarities()
    {
        var validator = new RelationshipValidator(); var market = Market();
        Assert.Equal(VerificationState.VerifiedDeterministic, validator.ValidateBinaryComplement(market).State);
        Assert.Equal(VerificationState.NeedsReview, validator.ValidateBinaryComplement(market with { Semantics = market.Semantics with { OutcomeSet = new() } }).State);
    }
    [Fact]
    public void Outcome_reordering_is_stable_and_duplicate_labels_are_not_identities()
    {
        var a = Market(); var reordered = a with { Outcomes = a.Outcomes.Reverse().ToArray(), RetrievedAt = DateTimeOffset.UtcNow, SourceUpdatedAt = DateTimeOffset.UtcNow };
        Assert.Equal(RelationshipPolicy.Fingerprint(a), RelationshipPolicy.Fingerprint(reordered));
        Assert.Equal(VerificationState.VerifiedDeterministic, new RelationshipValidator().Validate(a, reordered).State);
        Assert.Equal(VerificationState.NeedsReview, new RelationshipValidator().Validate(a, a with { Outcomes = [a.Outcomes[0], a.Outcomes[0]] }).State);
        var duplicates = a with { Outcomes = a.Outcomes.Select(o => o with { Label = "Similar" }).ToArray() };
        Assert.Equal(VerificationState.VerifiedDeterministic, new RelationshipValidator().Validate(a, duplicates).State);
        Assert.NotEqual(RelationshipPolicy.Fingerprint(a), RelationshipPolicy.Fingerprint(a with { Outcomes = [a.Outcomes[0]] }));
        Assert.NotEqual(RelationshipPolicy.Fingerprint(a), RelationshipPolicy.Fingerprint(a with { RulesText = "changed" }));
    }
    [Theory]
    [InlineData(VerificationState.Proposed)] [InlineData(VerificationState.NeedsReview)] [InlineData(VerificationState.Stale)] [InlineData(VerificationState.Rejected)]
    public void Unapproved_states_never_qualify_for_strategy(VerificationState state) => Assert.False(RelationshipPolicy.IsStrategyEligible(RelationshipType.EquivalentSameOutcome, state));
}
