using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Strategies;

public static class OpportunityPlanner
{
    public static IReadOnlyList<OpportunityPlan> Plan(ApprovedRelationship r)
    {
        var plans = new List<OpportunityPlan>();
        OrderBookInstrumentId Instrument(MarketIdentity market, string outcome, CanonicalMarketDescriptor? descriptor) =>
            new(market.Exchange, market.NativeId, outcome, descriptor?.Outcomes.SingleOrDefault(o => o.NativeId == outcome)?.Label ?? outcome);
        var selected = r.Mappings.SelectMany(m => new[] { (r.Source, m.SourceOutcomeId), (r.Target, m.TargetOutcomeId) }).Distinct().Count();
        var partition = r.SelectedOutcomeSet.MutuallyExclusive?.Value == TruthValue.True && r.SelectedOutcomeSet.CollectivelyExhaustive?.Value == TruthValue.True && selected == 2;
        if (partition && r.Mappings.Any(m => m.Type == RelationshipType.EquivalentSameOutcome))
            return r.Mappings.Take(100).Select(m => new OpportunityPlan(r, OpportunityStrategy.CrossMarketBuyBothComplements,
                Instrument(r.Source, m.SourceOutcomeId, r.SourceDescriptor), Instrument(r.Target, m.TargetOutcomeId, r.TargetDescriptor),
                DepthAction.Buy, DepthAction.Buy, false, "Conflicting same-outcome mapping and complementary partition facts.")).ToArray();
        foreach (var mapping in r.Mappings.OrderBy(m => m.SourceOutcomeId, StringComparer.Ordinal).ThenBy(m => m.TargetOutcomeId, StringComparer.Ordinal).Take(100))
        {
            var a = Instrument(r.Source, mapping.SourceOutcomeId, r.SourceDescriptor); var b = Instrument(r.Target, mapping.TargetOutcomeId, r.TargetDescriptor);
            // The row's opposite wording does NOT invert an explicit same-economic-outcome mapping.
            if (mapping.Type == RelationshipType.EquivalentOppositeOutcome || partition)
                plans.Add(new(r, r.Source == r.Target ? OpportunityStrategy.SingleMarketBinaryComplement : OpportunityStrategy.CrossMarketBuyBothComplements,
                    a, b, DepthAction.Buy, DepthAction.Buy, true));
            else if (mapping.Type == RelationshipType.EquivalentSameOutcome)
            {
                plans.Add(new(r, OpportunityStrategy.CrossMarketSameOutcomeSpread, a, b, DepthAction.Buy, DepthAction.Sell, false, "RequiresInventory"));
                plans.Add(new(r, OpportunityStrategy.CrossMarketSameOutcomeSpread, b, a, DepthAction.Buy, DepthAction.Sell, false, "RequiresInventory"));
                AddOpposite(r.TargetDescriptor, a, b); AddOpposite(r.SourceDescriptor, b, a);
            }
            else plans.Add(new(r, OpportunityStrategy.CrossMarketBuyBothComplements, a, b, DepthAction.Buy, DepthAction.Buy, false, "UnsupportedStrategy"));
        }
        foreach (var descriptor in new[] { r.SourceDescriptor, r.TargetDescriptor }.Where(d => d is not null).Cast<CanonicalMarketDescriptor>())
        {
            if (new RelationshipValidator().ValidateBinaryComplement(descriptor).State != VerificationState.VerifiedDeterministic) continue;
            plans.Add(new(r, OpportunityStrategy.SingleMarketBinaryComplement, Instrument(descriptor.Identity, descriptor.Outcomes[0].NativeId, descriptor),
                Instrument(descriptor.Identity, descriptor.Outcomes[1].NativeId, descriptor), DepthAction.Buy, DepthAction.Buy, true));
        }
        return plans.DistinctBy(p => GrossOpportunityEvaluator.Key(p)).OrderBy(GrossOpportunityEvaluator.Key, StringComparer.Ordinal).ToArray();

        void AddOpposite(CanonicalMarketDescriptor? descriptor, OrderBookInstrumentId sameOutcome, OrderBookInstrumentId mapped)
        {
            if (descriptor is null || new RelationshipValidator().ValidateBinaryComplement(descriptor).State != VerificationState.VerifiedDeterministic) return;
            var opposite = descriptor.Outcomes.Single(o => o.NativeId != mapped.NativeInstrumentId);
            plans.Add(new(r, OpportunityStrategy.CrossMarketBuyBothComplements, sameOutcome, Instrument(descriptor.Identity, opposite.NativeId, descriptor), DepthAction.Buy, DepthAction.Buy, true));
        }
    }
}
