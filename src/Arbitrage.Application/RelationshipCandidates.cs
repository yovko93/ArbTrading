using Arbitrage.Domain;

namespace Arbitrage.Application;

public sealed record CandidateBounds(int MaximumSources = 500, int PerSource = 20, int MaximumComparisons = 2000, int RuntimeSeconds = 15)
{
    public bool Valid => MaximumSources is >= 1 and <= 5000 && PerSource is >= 1 and <= 100 && MaximumComparisons is >= 1 and <= 10000 && RuntimeSeconds is >= 1 and <= 60;
}
public sealed record CandidatePair(CanonicalMarketDescriptor A, CanonicalMarketDescriptor B);
public sealed record CandidateBatch(CandidatePair[] Pairs, bool Partial, int Sources, int Comparisons);
public sealed class CandidateGenerator(TimeProvider clock)
{
    public CandidateBatch Generate(IReadOnlyList<CanonicalMarketDescriptor> catalog, CandidateBounds bounds, bool crossExchange,
        MarketIdentity? selected, CancellationToken ct)
    {
        if (!bounds.Valid) throw new ArgumentException("Invalid generation bounds.");
        var start = clock.GetTimestamp();
        var ordered = catalog.GroupBy(m => m.Identity.Exchange, StringComparer.Ordinal)
            .SelectMany(g => g.OrderBy(m => m.Identity.NativeId, StringComparer.Ordinal).Select((market, rank) => (market, rank)))
            .OrderBy(x => x.rank).ThenBy(x => x.market.Identity).Take(bounds.MaximumSources).Select(x => x.market).ToArray();
        var partial = catalog.Count > bounds.MaximumSources;
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        // Bounded postings prevent a common word from turning retrieval into an all-pairs scan.
        for (var i = 0; i < ordered.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(start).TotalSeconds >= bounds.RuntimeSeconds) return new([], true, i, 0);
            foreach (var token in RelationshipPolicy.Tokens(ordered[i].Title).Take(64))
            {
                if (!index.TryGetValue(token, out var posting)) index[token] = posting = [];
                if (posting.Count < bounds.PerSource + 1) posting.Add(i); else partial = true;
            }
        }
        var pairs = new List<CandidatePair>(); var seen = new HashSet<(MarketIdentity, MarketIdentity)>();
        var sources = selected is null ? ordered : ordered.Where(m => m.Identity == selected).ToArray();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(start).TotalSeconds >= bounds.RuntimeSeconds) { partial = true; break; }
            var candidates = RelationshipPolicy.Tokens(source.Title).Take(64).Where(index.ContainsKey).SelectMany(t => index[t]).Distinct()
                .Select(i => ordered[i]).Where(m => m.Identity != source.Identity && (!crossExchange || m.Identity.Exchange != source.Identity.Exchange))
                .OrderBy(m => m.Identity).Take(bounds.PerSource + 1).ToArray();
            if (candidates.Length > bounds.PerSource) partial = true;
            foreach (var target in candidates.Take(bounds.PerSource))
            {
                ct.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(start).TotalSeconds >= bounds.RuntimeSeconds || pairs.Count >= bounds.MaximumComparisons)
                    return new([.. pairs], true, sources.Length, pairs.Count);
                var a = source.Identity.CompareTo(target.Identity) < 0 ? source : target;
                var b = a == source ? target : source;
                if (seen.Add((a.Identity, b.Identity))) pairs.Add(new(a, b));
            }
        }
        return new([.. pairs], partial, sources.Length, pairs.Count);
    }
}
public sealed record ApprovedRelationship(Guid Id, RelationshipType Type, VerificationState State, OutcomeMapping[] Mappings,
    MarketIdentity Source, MarketIdentity Target, OutcomeSetFacts SourceSet, OutcomeSetFacts TargetSet, OutcomeSetFacts SelectedOutcomeSet)
{
    public bool IsStrategyEligible => RelationshipPolicy.IsStrategyEligible(Type, State);
    public int PolicyVersion { get; init; } = RelationshipPolicy.Version;
    public string SourceFingerprint { get; init; } = "";
    public string TargetFingerprint { get; init; } = "";
    public string Revision { get; init; } = "";
    public CanonicalMarketDescriptor? SourceDescriptor { get; init; }
    public CanonicalMarketDescriptor? TargetDescriptor { get; init; }
}
public sealed record ApprovedRelationshipPage(IReadOnlyList<ApprovedRelationship> Items, int Scanned, bool HasMore);
// Implementations must recheck current fingerprints, policy and membership. Manual trust is opt-in and remains distinct.
public interface IRelationshipProvider
{
    Task<IReadOnlyList<ApprovedRelationship>> ReadApprovedAsync(Guid actorId, Guid workspaceId, bool includeManual, CancellationToken ct);
    Task<ApprovedRelationshipPage> ReadEvaluationPageAsync(Guid actorId, Guid workspaceId, bool includeManual,
        Guid? relationshipId, string? exchange, int skip, int take, CancellationToken ct);
}
