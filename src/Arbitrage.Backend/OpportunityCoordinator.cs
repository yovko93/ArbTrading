using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Strategies;

namespace Arbitrage.Backend;

// Only ResolveAsync is used: it reads the local catalog, never an exchange.
public sealed class OpportunityCoordinator(IRelationshipProvider relationships, OrderBookService instruments, OrderBookCache cache, TimeProvider clock)
{
    public async Task<ArbitrageOpportunitySnapshot> EvaluateAsync(Guid actor, Guid workspace, OpportunityPlan plan,
        OpportunitySettings settings, bool manual, CancellationToken ct)
    {
        var supported = await ResolveAsync(plan.A, ct) && await ResolveAsync(plan.B, ct);
        var books = cache.ReadTogether(plan.A, plan.B);
        var result = GrossOpportunityEvaluator.Evaluate(plan, books[0], books[1], settings, clock.GetUtcNow(), manual, ct);
        if (!supported) result = result.Invalidate(OpportunityStatus.UnsupportedStrategy, "Current catalog does not support the mapped native instruments.");
        return await ValidateAsync(actor, workspace, result, manual, true, ct);
    }
    public async Task<ArbitrageOpportunitySnapshot> ValidateAsync(Guid actor, Guid workspace, ArbitrageOpportunitySnapshot result,
        bool manual, bool publishing, CancellationToken ct)
    {
        var page = await relationships.ReadEvaluationPageAsync(actor, workspace, manual, result.RelationshipId, null, 0, 1, ct);
        var current = page.Items.SingleOrDefault();
        if (current is null || current.Revision != result.RelationshipRevision || current.PolicyVersion != result.RelationshipPolicyVersion ||
            current.SourceFingerprint != result.SourceFingerprint || current.TargetFingerprint != result.TargetFingerprint)
            return result.Invalidate(OpportunityStatus.RelationshipIneligible, "Relationship approval, policy or semantic sources changed.") with { RelationshipEligible = false };
        var ids = result.Legs.Select(l => l.Instrument).ToArray();
        var books = cache.ReadTogether(ids);
        if (books.Where((b, i) => b.Version != result.Legs[i].SnapshotVersion).Any())
            return result.Invalidate(publishing ? OpportunityStatus.BooksChangedDuringEvaluation : OpportunityStatus.StaleInput, "Cached inputs changed; explicitly evaluate again.");
        if (result.Status == OpportunityStatus.Detected && books.Any(b => !b.Eligibility.IsActionable))
        {
            var status = books.Any(b => b.Eligibility.Freshness == BookFreshness.Stale) ? OpportunityStatus.BookStale :
                books.Any(b => b.Source == BookSourceMode.Realtime) ? OpportunityStatus.BookContinuityInsufficient : OpportunityStatus.BookUnavailable;
            return result.Invalidate(status, "Current canonical book eligibility no longer permits this evaluation.");
        }
        return result;
    }
    private async Task<bool> ResolveAsync(OrderBookInstrumentId id, CancellationToken ct)
    {
        try
        {
            var resolved = await instruments.ResolveAsync(id.Exchange, id.NativeMarketId, id.NativeInstrumentId, ct);
            return resolved?.Request is { } request && (id.Exchange != "Kalshi" || request.BinarySupported);
        }
        catch (ArgumentException) { return false; }
    }
}
