using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arbitrage.Domain;

public enum RelationshipType { Unknown, Candidate, EquivalentSameOutcome, EquivalentOppositeOutcome, MutuallyExclusive, Exhaustive, MutuallyExclusiveAndExhaustive, Subset, Superset, Overlapping, Related, Rejected }
public enum VerificationState { Proposed, NeedsReview, VerifiedDeterministic, VerifiedManual, Rejected, Stale }
public enum FactSource { Title, Rules, Description, NativeMetadata, ManualVerification, DeterministicExtraction }
public enum TruthValue { Unknown, False, True }
public enum ThresholdOperator { GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal }
public enum ContractPolarity { Unknown, Positive, Negative }
public enum EvidenceKind { ExactNormalizedEntity, ExactYear, ExactDateWindow, ExactThreshold, ExactOutcomeSemantics, SharedOfficialSource, CompatibleRules, SameEventIdentifierWithinExchange, MatchingGeography, MatchingCompetitionStage, MatchingResolutionAuthority, ContradictoryDate, ContradictoryThreshold, ContradictoryGeography, ContradictoryRules, MissingRules, MissingResolutionSource, MissingSemanticField, ContradictorySemanticField, UnknownExhaustiveness, InvalidOutcomeIdentity }
public sealed record SemanticFact<T>(T Value, FactSource Source, string Reference);
public sealed record MarketIdentity(string Exchange, string NativeId) : IComparable<MarketIdentity>
{
    public int CompareTo(MarketIdentity? other) => other is null ? 1 :
        StringComparer.Ordinal.Compare(Exchange, other.Exchange) is var c && c != 0 ? c : StringComparer.Ordinal.Compare(NativeId, other.NativeId);
}
public sealed record SemanticThreshold(string Metric, ThresholdOperator Operator, decimal Value, string Unit);
public sealed record ObservationWindow(DateTimeOffset? Start, bool? StartInclusive, DateTimeOffset? End, bool? EndInclusive, string? TimeZone, string Kind);
public sealed record SemanticOutcome(string NativeId, string Label, SemanticFact<string>? Meaning = null, ContractPolarity Polarity = ContractPolarity.Unknown);
public sealed record OutcomeSetFacts(SemanticFact<TruthValue>? MutuallyExclusive = null, SemanticFact<TruthValue>? CollectivelyExhaustive = null);
public sealed record MarketSemantics
{
    public SemanticFact<string>? Subject { get; init; }
    public SemanticFact<string>? Predicate { get; init; }
    public SemanticFact<string>? Geography { get; init; }
    public SemanticFact<string>? Event { get; init; }
    public SemanticFact<string>? Edition { get; init; }
    public SemanticFact<string>? Stage { get; init; }
    public SemanticFact<string>? Authority { get; init; }
    public SemanticFact<string>? SettlementQualifiers { get; init; }
    // Explicit non-applicability is evidence, not a null threshold interpreted as equality.
    public SemanticFact<bool>? ThresholdApplicable { get; init; }
    public SemanticFact<SemanticThreshold>? Threshold { get; init; }
    public SemanticFact<ObservationWindow>? Window { get; init; }
    public SemanticFact<ContractPolarity>? Polarity { get; init; }
    public OutcomeSetFacts OutcomeSet { get; init; } = new();
}
public sealed record CanonicalMarketDescriptor
{
    public required MarketIdentity Identity { get; init; }
    public string? NativeEventId { get; init; }
    public string? NativeSeriesId { get; init; }
    public string? NativeGroupId { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Description { get; init; }
    public string? RulesText { get; init; }
    public string? ResolutionSource { get; init; }
    public string? PublicMetadataReference { get; init; }
    public string? PublicSemanticMetadataJson { get; init; }
    public string? Category { get; init; }
    public string[] Tags { get; init; } = [];
    public string? NativeStatus { get; init; }
    public string? NormalizedStatus { get; init; }
    public string? MarketStructure { get; init; }
    public SemanticOutcome[] Outcomes { get; init; } = [];
    public DateTimeOffset? EventStart { get; init; }
    public DateTimeOffset? EventEnd { get; init; }
    public DateTimeOffset? MarketOpen { get; init; }
    public DateTimeOffset? MarketClose { get; init; }
    public DateTimeOffset? ExpectedResolution { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset RetrievedAt { get; init; }
    public DateTimeOffset? SourceUpdatedAt { get; init; }
    public MarketSemantics Semantics { get; init; } = new();
}
public sealed record RelationshipEvidence(EvidenceKind Kind, string Dimension, string Detail, bool Blocking, bool Contradiction = false);
public sealed record OutcomeMapping(string SourceOutcomeId, string TargetOutcomeId, RelationshipType Type);
public sealed record RelationshipValidation(RelationshipType Type, VerificationState State, RelationshipEvidence[] Evidence,
    OutcomeMapping[] Mappings, string[] Warnings)
{
    public string[] BlockingDifferences => Evidence.Where(e => e.Blocking).Select(e => e.Detail).ToArray();
}
public static partial class RelationshipPolicy
{
    public const int Version = 1;
    // Candidate normalization deliberately retains all words, numbers, and operators.
    public static string Normalize(string? value) => WhiteSpace().Replace((value ?? "").Normalize(NormalizationForm.FormKC).ToUpperInvariant(), " ").Trim();
    public static string[] Tokens(string? value) => Token().Matches(Normalize(value)).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    [GeneratedRegex(@"\s+")] private static partial Regex WhiteSpace();
    [GeneratedRegex(@"[\p{L}\p{N}]+|>=|<=|[><=%]|[+\-]")] private static partial Regex Token();
    public static bool Approved(VerificationState state) => state is VerificationState.VerifiedDeterministic or VerificationState.VerifiedManual;
    public static bool IsStrategyEligible(RelationshipType type, VerificationState state) => Approved(state) && type is not (RelationshipType.Unknown or RelationshipType.Candidate or RelationshipType.Related or RelationshipType.Rejected);
    public static string Fingerprint(CanonicalMarketDescriptor source)
    {
        // Serialization order is fixed by these records; collections have ordinal canonical ordering.
        // Retrieval and update timestamps are not semantic; all other raw/parsed content is retained.
        var canonical = source with { RetrievedAt = default, SourceUpdatedAt = null,
            Tags = source.Tags.Order(StringComparer.Ordinal).ToArray(),
            Outcomes = source.Outcomes.OrderBy(o => o.NativeId, StringComparer.Ordinal).ThenBy(o => o.Label, StringComparer.Ordinal).ToArray(),
            EventStart = source.EventStart?.ToUniversalTime(), EventEnd = source.EventEnd?.ToUniversalTime(),
            MarketOpen = source.MarketOpen?.ToUniversalTime(), MarketClose = source.MarketClose?.ToUniversalTime(),
            ExpectedResolution = source.ExpectedResolution?.ToUniversalTime(), ResolvedAt = source.ResolvedAt?.ToUniversalTime() };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}

public sealed class RelationshipValidator
{
    public RelationshipValidation Validate(CanonicalMarketDescriptor a, CanonicalMarketDescriptor b)
    {
        var evidence = new List<RelationshipEvidence>();
        void Missing(string dimension, EvidenceKind kind = EvidenceKind.MissingSemanticField) => evidence.Add(new(kind, dimension, $"{dimension}: insufficient authoritative evidence.", true));
        void Compare<T>(SemanticFact<T>? x, SemanticFact<T>? y, string dimension, EvidenceKind positive, EvidenceKind negative = EvidenceKind.ContradictorySemanticField)
        {
            // Title/description and manual assertions never establish deterministic proof.
            if (!Authoritative(x) || !Authoritative(y))
            {
                evidence.Add(new(EvidenceKind.MissingSemanticField, dimension,
                    $"{dimension}: A = {x?.Value?.ToString() ?? "unknown"}; B = {y?.Value?.ToString() ?? "unknown"}. Authoritative settlement evidence is required.", true)); return;
            }
            bool same = EqualityComparer<T>.Default.Equals(x!.Value, y!.Value);
            evidence.Add(new(same ? positive : negative, dimension, same ? $"{dimension}: equal evidenced values." : $"{dimension}: {x.Value} versus {y.Value}.", !same, !same));
        }
        var x = a.Semantics; var y = b.Semantics;
        Compare(x.Subject, y.Subject, "Subject", EvidenceKind.ExactNormalizedEntity);
        Compare(x.Predicate, y.Predicate, "Predicate", EvidenceKind.ExactOutcomeSemantics);
        Compare(x.Geography, y.Geography, "Geography", EvidenceKind.MatchingGeography, EvidenceKind.ContradictoryGeography);
        Compare(x.Event, y.Event, "Event", EvidenceKind.ExactOutcomeSemantics);
        Compare(x.Edition, y.Edition, "Edition", EvidenceKind.ExactYear, EvidenceKind.ContradictoryDate);
        Compare(x.Stage, y.Stage, "Stage", EvidenceKind.MatchingCompetitionStage);
        Compare(x.Authority, y.Authority, "Authority", EvidenceKind.MatchingResolutionAuthority);
        Compare(x.SettlementQualifiers, y.SettlementQualifiers, "Cancellation / void / finality", EvidenceKind.CompatibleRules, EvidenceKind.ContradictoryRules);
        Compare(x.ThresholdApplicable, y.ThresholdApplicable, "Threshold applicability", EvidenceKind.ExactThreshold, EvidenceKind.ContradictoryThreshold);
        if (x.ThresholdApplicable?.Value == true || y.ThresholdApplicable?.Value == true)
            Compare(x.Threshold, y.Threshold, "Threshold / operator / unit", EvidenceKind.ExactThreshold, EvidenceKind.ContradictoryThreshold);
        Compare(x.Window, y.Window, "Observation window", EvidenceKind.ExactDateWindow, EvidenceKind.ContradictoryDate);
        foreach (var window in new[] { x.Window?.Value, y.Window?.Value })
            if (window is null || string.IsNullOrWhiteSpace(window.TimeZone) || window.End is null || window.EndInclusive is null ||
                window.Start is not null && window.StartInclusive is null) Missing("Explicit timezone and inclusive boundaries");
        if (string.IsNullOrWhiteSpace(a.RulesText) || string.IsNullOrWhiteSpace(b.RulesText)) Missing("Rules", EvidenceKind.MissingRules);
        else if (RelationshipPolicy.Normalize(a.RulesText) != RelationshipPolicy.Normalize(b.RulesText))
            evidence.Add(new(EvidenceKind.ContradictoryRules, "Rules", "Rules text differs; policy 1 does not prove arbitrary prose equivalence.", true, true));
        else evidence.Add(new(EvidenceKind.CompatibleRules, "Rules", "Identical normalized rules; other dimensions still require proof.", false));
        if (string.IsNullOrWhiteSpace(a.ResolutionSource) || string.IsNullOrWhiteSpace(b.ResolutionSource)) Missing("Resolution source", EvidenceKind.MissingResolutionSource);
        else if (a.ResolutionSource != b.ResolutionSource) evidence.Add(new(EvidenceKind.ContradictoryRules, "Resolution source", "Different resolution source references.", true, true));
        else evidence.Add(new(EvidenceKind.SharedOfficialSource, "Resolution source", "Same explicit source reference.", false));
        if (string.IsNullOrWhiteSpace(a.MarketStructure) || a.MarketStructure != b.MarketStructure) Missing("Compatible market structure");
        if (a.Identity.Exchange == b.Identity.Exchange && a.NativeEventId is not null && a.NativeEventId == b.NativeEventId)
            evidence.Add(new(EvidenceKind.SameEventIdentifierWithinExchange, "Event group", "Shared native event is candidate evidence only.", false));
        if (!Authoritative(x.Polarity) || !Authoritative(y.Polarity) || x.Polarity!.Value == ContractPolarity.Unknown || y.Polarity!.Value == ContractPolarity.Unknown) Missing("Contract polarity");
        foreach (var set in new[] { x.OutcomeSet, y.OutcomeSet })
            if (!Authoritative(set.MutuallyExclusive) || !Authoritative(set.CollectivelyExhaustive) ||
                set.MutuallyExclusive!.Value != TruthValue.True || set.CollectivelyExhaustive!.Value != TruthValue.True)
                Missing("Outcome exclusivity / exhaustiveness", EvidenceKind.UnknownExhaustiveness);
        var mappings = new List<OutcomeMapping>();
        foreach (var market in new[] { a, b })
            if (market.Outcomes.Length < 2 || market.Outcomes.Any(o => string.IsNullOrWhiteSpace(o.NativeId)) || market.Outcomes.Select(o => o.NativeId).Distinct().Count() != market.Outcomes.Length)
                Missing("Unique native outcome identities", EvidenceKind.InvalidOutcomeIdentity);
        foreach (var outcome in a.Outcomes)
        {
            var matches = b.Outcomes.Where(o => Authoritative(outcome.Meaning) && Authoritative(o.Meaning) && o.Meaning!.Value == outcome.Meaning!.Value &&
                outcome.Polarity != ContractPolarity.Unknown && outcome.Polarity == o.Polarity).ToArray();
            if (matches.Length != 1) Missing($"Outcome meaning: {outcome.NativeId}");
            else mappings.Add(new(outcome.NativeId, matches[0].NativeId, RelationshipType.EquivalentSameOutcome));
        }
        if (mappings.Count != b.Outcomes.Length || mappings.Select(m => m.TargetOutcomeId).Distinct().Count() != mappings.Count) Missing("Bijection of outcome meanings");
        var type = x.Polarity?.Value != y.Polarity?.Value ? RelationshipType.EquivalentOppositeOutcome : RelationshipType.EquivalentSameOutcome;
        if (evidence.Any(e => e.Contradiction)) return new(RelationshipType.Rejected, VerificationState.Rejected, [.. evidence], [], []);
        if (evidence.Any(e => e.Blocking)) return new(RelationshipType.Candidate, VerificationState.NeedsReview, [.. evidence], [], ["Labels and group metadata are not settlement proof."]);
        evidence.Add(new(EvidenceKind.ExactOutcomeSemantics, "Outcomes", "Complete bijection of evidenced outcome meanings.", false));
        return new(type, VerificationState.VerifiedDeterministic, [.. evidence], [.. mappings], []);
    }
    public RelationshipValidation ValidateOutcomeSet(CanonicalMarketDescriptor market)
    {
        var self = Validate(market, market);
        var evidence = self.Evidence.Where(e => e.Kind != EvidenceKind.UnknownExhaustiveness).ToList();
        var set = market.Semantics.OutcomeSet;
        if (!Authoritative(set.MutuallyExclusive) || !Authoritative(set.CollectivelyExhaustive) || set.MutuallyExclusive!.Value == TruthValue.Unknown || set.CollectivelyExhaustive!.Value == TruthValue.Unknown)
            evidence.Add(new(EvidenceKind.UnknownExhaustiveness, "Outcome set", "Exclusivity and exhaustiveness must each have explicit evidence, including false values.", true));
        var type = (set.MutuallyExclusive?.Value, set.CollectivelyExhaustive?.Value) switch
        {
            (TruthValue.True, TruthValue.True) => RelationshipType.MutuallyExclusiveAndExhaustive,
            (TruthValue.True, _) => RelationshipType.MutuallyExclusive,
            (_, TruthValue.True) => RelationshipType.Exhaustive,
            (TruthValue.False, TruthValue.False) => RelationshipType.Overlapping,
            _ => RelationshipType.Unknown
        };
        return new(type, evidence.Any(e => e.Blocking) ? VerificationState.NeedsReview : VerificationState.VerifiedDeterministic, [.. evidence], [], []);
    }
    public RelationshipValidation ValidateBinaryComplement(CanonicalMarketDescriptor market)
    {
        var set = ValidateOutcomeSet(market);
        if (set.State != VerificationState.VerifiedDeterministic || set.Type != RelationshipType.MutuallyExclusiveAndExhaustive || market.Outcomes.Length != 2 ||
            market.Outcomes[0].Meaning?.Value != market.Outcomes[1].Meaning?.Value || market.Outcomes[0].Polarity == market.Outcomes[1].Polarity)
            return new(RelationshipType.Candidate, VerificationState.NeedsReview, [.. set.Evidence, new(EvidenceKind.UnknownExhaustiveness, "Complement", "A proved two-outcome complementary partition is required.", true)], [], []);
        return new(RelationshipType.EquivalentOppositeOutcome, VerificationState.VerifiedDeterministic, set.Evidence,
            [new(market.Outcomes[0].NativeId, market.Outcomes[1].NativeId, RelationshipType.EquivalentOppositeOutcome)], []);
    }
    private static bool Authoritative<T>(SemanticFact<T>? fact) => fact is not null && fact.Value is not null &&
        (fact.Value is not string text || !string.IsNullOrWhiteSpace(text)) &&
        fact.Source is FactSource.Rules or FactSource.NativeMetadata or FactSource.DeterministicExtraction && !string.IsNullOrWhiteSpace(fact.Reference);
}
