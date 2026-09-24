using Arbitrage.Execution;
namespace Arbitrage.Application.Tests;
public sealed class PaperReliabilityTests
{
    [Fact] public void Quantity_statistics_use_exact_decimals()
    { Assert.Equal((5m, 15m, 10m), PaperReliabilityPolicy.Distribution([5m, 10m, 15m])); Assert.Equal((.1m, .3m, .2m), PaperReliabilityPolicy.Distribution([.1m, .2m, .3m])); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public void Runtime_state_transitions_pause_and_restart_exclude_downtime_exactly()
    {
        var clock = new Clock(); var probe = new PaperReliabilityTelemetry(clock); var workspace = Guid.NewGuid(); var campaign = Guid.NewGuid();
        probe.Begin(workspace, campaign, [], []); probe.SetState(workspace, monitoring: true, armed: true, healthy: true);
        clock.Now += TimeSpan.FromHours(2); var first = probe.Capture(workspace, true)!;
        Assert.Equal(TimeSpan.FromHours(2).Ticks, first.Counters["AutomationHealthyTicks"]);
        clock.Now += TimeSpan.FromHours(1); probe = new(clock); probe.Begin(workspace, campaign, first.Counters, first.Triggers);
        probe.SetState(workspace, monitoring: true); clock.Now += TimeSpan.FromHours(2); var second = probe.Capture(workspace, true)!;
        Assert.Equal(TimeSpan.FromHours(4).Ticks, second.Counters["BackendObservedTicks"]); Assert.Equal(TimeSpan.FromHours(2).Ticks, second.Counters["AutomationArmedTicks"]);
        clock.Now += TimeSpan.FromHours(10); probe.Begin(workspace, campaign, second.Counters, second.Triggers); clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromHours(5).Ticks, probe.Capture(workspace)!.Counters["BackendObservedTicks"]);
    }
    [Fact] public void Thresholds_are_inclusive_unknown_is_not_satisfied_and_hard_violation_is_sticky()
    {
        var counters = PaperReliabilityPolicy.Minimums.ToDictionary(p => p.Key, p => p.Value);
        var invariants = new[] { new ReliabilityCheck("Ledger", 0, 0, ReliabilityCheckState.Satisfied, "Verified") };
        Assert.Equal(ReliabilityEvidenceState.CriteriaMet, PaperReliabilityPolicy.Evaluate(PaperReliabilityPolicy.Criteria(counters), invariants, false, false));
        counters["AutomationHealthyTicks"]--; Assert.Equal(ReliabilityEvidenceState.InsufficientEvidence, PaperReliabilityPolicy.Evaluate(PaperReliabilityPolicy.Criteria(counters), invariants, false, false));
        counters["AutomationHealthyTicks"]++; counters["AutomaticExecutionsCommitted"] = 4;
        Assert.Contains(PaperReliabilityPolicy.Criteria(counters), c => c.Code == "AutomaticExecutionsCommitted" && c.State == ReliabilityCheckState.NotSatisfied);
        counters["AutomaticExecutionsCommitted"] = 5; counters["UnexpectedWorkerFaults"] = 1;
        Assert.Equal(ReliabilityEvidenceState.CriteriaNotMet, PaperReliabilityPolicy.Evaluate(PaperReliabilityPolicy.Criteria(counters), invariants, false, false));
        Assert.Equal(ReliabilityEvidenceState.InsufficientEvidence, PaperReliabilityPolicy.Evaluate(PaperReliabilityPolicy.Criteria(counters), invariants, true, false));
        Assert.Equal(ReliabilityEvidenceState.InvariantViolation, PaperReliabilityPolicy.Evaluate(PaperReliabilityPolicy.Criteria(counters), invariants, true, true));
    }
    [Fact] public void Funnel_counts_are_aggregated_and_campaign_scoped_with_exact_distinct_inputs()
    {
        var clock = new Clock(); var probe = new PaperReliabilityTelemetry(clock); var workspace = Guid.NewGuid(); probe.Begin(workspace, Guid.NewGuid(), [], []);
        for (var n = 0; n < 100; n++) { probe.Input(workspace, "key", n < 70 ? "same" : n.ToString()); probe.Count(workspace, n < 70 ? "DuplicateInputsSuppressed" : n < 80 ? "InputQualityRejected" : n < 85 ? "CooldownRejected" : n < 90 ? "NoAdmissibleAdaptiveQuantity" : n < 95 ? "RiskRejected" : "Committed"); }
        var sample = probe.Capture(workspace, true)!; Assert.Equal(100, sample.Counters["CandidateInputsObserved"]); Assert.Equal(31, sample.Counters["DistinctTriggerInputs"]);
        Assert.Equal(70, sample.Counters["DuplicateInputsSuppressed"]); Assert.Equal(5, sample.Counters["Committed"]);
        probe.Count(workspace, "Committed"); probe.Begin(workspace, Guid.NewGuid(), [], []); Assert.False(probe.Capture(workspace)!.Counters.ContainsKey("Committed"));
    }
    [Fact] public void Bound_and_clock_regression_create_evidence_gap_without_throwing_in_producer()
    {
        var clock = new Clock(); var p = new PaperReliabilityTelemetry(clock); var w = Guid.NewGuid(); p.Begin(w, Guid.NewGuid(), [], []);
        for (var n = 0; n < 10001; n++) p.Input(w, "key", n.ToString()); Assert.True(p.Capture(w)!.Gap);
        clock.Now -= TimeSpan.FromHours(1); Assert.True(p.Capture(w)!.Gap);
    }
}
