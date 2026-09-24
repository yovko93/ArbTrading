using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Strategies;
using System.Collections.Immutable;

namespace Arbitrage.Backend;

public sealed record CapturedOpportunityFees(ImmutableArray<ResolvedFeeSchedule> Schedules, FeeProfileState Profile);

// Only ResolveAsync is used: it reads the local catalog, never an exchange.
public sealed class OpportunityCoordinator(IRelationshipProvider relationships, OrderBookService instruments, OrderBookCache cache, TimeProvider clock, IFeeStore? fees = null)
{
    public async Task<CapturedOpportunityFees?> CaptureFeesAsync(Guid workspace, ArbitrageOpportunitySnapshot result, CancellationToken ct)
    {
        if (fees is null) return null;
        var schedules = ImmutableArray.CreateBuilder<ResolvedFeeSchedule>();
        foreach (var leg in result.Legs) schedules.Add(FeeScheduleResolver.Resolve(await fees.ReadAsync(leg.Instrument.Exchange, leg.Instrument.NativeMarketId, ct), clock.GetUtcNow()));
        return new(schedules.ToImmutable(), await fees.ReadProfileAsync(workspace, ct));
    }
    public async Task<ArbitrageOpportunitySnapshot> EvaluateFeesAsync(Guid workspace, ArbitrageOpportunitySnapshot result, decimal minimumEdge, CancellationToken ct)
    {
        if (fees is null) return result;
        var schedules = new List<ResolvedFeeSchedule>();
        foreach (var leg in result.Legs) schedules.Add(FeeScheduleResolver.Resolve(await fees.ReadAsync(leg.Instrument.Exchange, leg.Instrument.NativeMarketId, ct), clock.GetUtcNow()));
        var profile = await fees.ReadProfileAsync(workspace, ct);
        return result with { Fees = FeeOpportunityEvaluator.Evaluate(result, schedules, profile.Profile, minimumEdge) with { ProfileRevision = profile.Revision },
            Warnings = [.. result.Warnings.Select(w => w.StartsWith("PRE-FEE:", StringComparison.Ordinal) ? "Gross values exclude fees; see the separate fee result. Paper execution requires separate explicit confirmation; live execution is unavailable." : w)] };
    }
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
        if (result.Status is OpportunityStatus.Detected or OpportunityStatus.NoGrossEdge && books.Any(b => !b.Eligibility.IsActionable))
        {
            var status = books.Any(b => b.Eligibility.Freshness == BookFreshness.Stale) ? OpportunityStatus.BookStale :
                books.Any(b => b.Source == BookSourceMode.Realtime) ? OpportunityStatus.BookContinuityInsufficient : OpportunityStatus.BookUnavailable;
            return result.Invalidate(status, "Current canonical book eligibility no longer permits this evaluation.");
        }
        if (result.Fees is { } evaluated && fees is not null)
        {
            var currentProfile = await fees.ReadProfileAsync(workspace, ct);
            var invalid = evaluated.Profile != currentProfile.Profile || evaluated.ProfileRevision != currentProfile.Revision;
            foreach (var group in evaluated.Breakdown.GroupBy(q => (q.Context.Exchange, q.Context.MarketId)))
            {
                var resolved = FeeScheduleResolver.Resolve(await fees.ReadAsync(group.Key.Exchange, group.Key.MarketId, ct), clock.GetUtcNow());
                if (group.Any(q => q.ScheduleFingerprint != resolved.Fingerprint) || resolved.Status == FeeStatus.ScheduleStale) invalid = true;
            }
            if (invalid) result = result with { Fees = evaluated.Invalidate() };
        }
        // Fee/profile reads above can yield while a local book changes. Check once more at publication.
        var finalBooks = cache.ReadTogether(ids);
        if (finalBooks.Where((b, i) => b.Version != result.Legs[i].SnapshotVersion).Any())
            return result.Invalidate(publishing ? OpportunityStatus.BooksChangedDuringEvaluation : OpportunityStatus.StaleInput, "Cached inputs changed during local validation.");
        if (result.Status is OpportunityStatus.Detected or OpportunityStatus.NoGrossEdge && finalBooks.Any(b => !b.Eligibility.IsActionable))
            return result.Invalidate(OpportunityStatus.BookStale, "Cached input eligibility expired during local validation.");
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
