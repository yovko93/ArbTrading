using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Arbitrage.Strategies;

namespace Arbitrage.Backend;

public sealed partial class PaperCoordinator
{
    public async Task<PaperSizingDecision> SizeAutomaticAsync(PaperAutomationPermit permit, string key, PaperSizingBudget budget, CancellationToken ct)
    {
        relationships.PersistEvaluationStaleness = false; var settings = permit.Profile.Settings; var at = clock.GetUtcNow();
        PaperSizingDecision Unavailable(string reason) => new(PaperSizingState.InputUnavailable, settings.SizingMode, null, 0,
            settings.MaximumQuantity ?? settings.FixedQuantity, settings.MinimumQuantity ?? settings.FixedQuantity, settings.QuantityStep ?? 0,
            [new(reason, 1)], null, null, at);
        if (options.TradingMode != "Paper") return Unavailable("NotPaperMode");
        if (monitoring.Status(permit.WorkspaceId).State != MonitoringState.Running) return Unavailable("MonitoringStopped");
        var source = monitoring.AutomaticPaperCandidates(permit.WorkspaceId, 100).SingleOrDefault(r => r.Result.OpportunityKey == key)?.Result;
        if (source is null) return Unavailable("OpportunityUnavailable");
        var quality = PaperAutomationPolicy.Quality(source, settings);
        if (quality != PaperAutomationReason.None && quality != PaperAutomationReason.InsufficientDepth) return Unavailable(quality.ToString());
        var stamp = PaperAutomationPolicy.Stamp(source, permit.Profile);
        var snapshot = await store.SizingSnapshotAsync(permit, source.RelationshipId, key, stamp, ct);
        var current = await opportunities.ValidateAsync(permit.ActorId, permit.WorkspaceId, source, false, true, ct);
        if (current.Status != OpportunityStatus.Detected || current.Fees?.State != FeeOpportunityStatus.FeeAdjustedDetected) return Unavailable("MarketDataChanged");
        var page = await relationships.ReadEvaluationPageAsync(permit.ActorId, permit.WorkspaceId, false, source.RelationshipId, null, 0, 1, ct);
        var relationship = page.Items.SingleOrDefault();
        var candidate = relationship is null ? null : OpportunityPlanner.Plan(relationship).SingleOrDefault(p => GrossOpportunityEvaluator.Key(p) == key);
        if (candidate is null) return Unavailable("RelationshipIneligible");
        var captured = books.ReadTogether(candidate.A, candidate.B);
        var fees = await opportunities.CaptureFeesAsync(permit.WorkspaceId, source, ct);
        if (fees is null) return Unavailable("FeeModelUnresolved");
        if (captured.Where((b, i) => b.Version != source.Legs[i].SnapshotVersion).Any()) return Unavailable("MarketDataChanged");
        var decision = await PaperSizer.SearchAsync(settings, (quantity, token) =>
        {
            var evaluated = GrossOpportunityEvaluator.Evaluate(candidate, captured[0], captured[1], new(MaximumEvaluationQuantity: PaperPlanner.MaximumQuantity,
                MaximumSkewMilliseconds: (int)source.MaximumAllowedSkew.TotalMilliseconds, RequestedQuantity: quantity), at, false, token);
            evaluated = evaluated with { Fees = FeeOpportunityEvaluator.Evaluate(evaluated, fees.Schedules, fees.Profile.Profile, PaperPlanner.MinimumFeeAdjustedEdge) with { ProfileRevision = fees.Profile.Revision } };
            if (PaperAutomationPolicy.Stamp(evaluated, permit.Profile) != stamp)
                return Task.FromResult(new PaperSizingCandidate(null, null, "MarketDataChanged", true));
            var planned = PaperPlanner.Create(evaluated, captured, quantity, at);
            if (planned.Plan is not { } plan)
            {
                var dependent = planned.Rejection is PaperRejection.InsufficientDepth or PaperRejection.FeeAdjustedNoEdge;
                return Task.FromResult(new PaperSizingCandidate(null, null, planned.Rejection.ToString(), !dependent));
            }
            var risk = PaperRiskEvaluator.Evaluate(snapshot.Risk.Profile, snapshot.Risk.State, plan, at);
            var admission = PaperAutomationPolicy.Evaluate(settings, plan, snapshot.History, snapshot.Risk.State.Buckets, at);
            var reason = risk.Decision != PaperRiskOutcome.Approved ? "Risk:" + (risk.Violations.FirstOrDefault()?.Code.ToString() ?? risk.Decision.ToString()) :
                admission != PaperAutomationReason.None ? admission.ToString() : null;
            return Task.FromResult(new PaperSizingCandidate(plan, risk, reason,
                risk.Decision is PaperRiskOutcome.NotConfigured or PaperRiskOutcome.InvalidFinancialState ||
                admission is PaperAutomationReason.DuplicateSuppressed or PaperAutomationReason.SessionExecutionLimitReached or PaperAutomationReason.HourlyExecutionLimitReached or PaperAutomationReason.RelationshipSessionLimit or PaperAutomationReason.CooldownRejected));
        }, budget, at, ct);
        // Revalidate the original market proof after the entire immutable search, including effective fee boundaries.
        current = await opportunities.ValidateAsync(permit.ActorId, permit.WorkspaceId, source, false, true, ct);
        if (current.Status != OpportunityStatus.Detected || current.Fees?.State != FeeOpportunityStatus.FeeAdjustedDetected ||
            !books.VersionsMatch(source.Legs.Select(l => l.Instrument).ToArray(), source.Legs.Select(l => l.SnapshotVersion).ToArray()))
            return decision with { State = PaperSizingState.InputUnavailable, SelectedQuantity = null, SelectedPlan = null, SelectedRiskDecision = null, Rejections = [new("MarketDataChanged", 1)] };
        return decision.State == PaperSizingState.Selected && settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity
            ? decision with { Proof = PaperSizer.CreateProof(decision, permit, stamp, snapshot.History) } : decision;
    }
}
