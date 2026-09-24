using System.Collections.Immutable;

namespace Arbitrage.Execution;

public enum ReliabilityCampaignState { Collecting, Paused, Completed, Cancelled }
public enum ReliabilityEvidenceState { InsufficientEvidence, CriteriaMet, CriteriaNotMet, InvariantViolation }
public enum ReliabilityCheckState { Satisfied, NotSatisfied, Unknown, NotApplicable, Violated }
public sealed record ReliabilityCheck(string Code, decimal RequiredValue, decimal ObservedValue, ReliabilityCheckState State, string ExplanationCode);
public sealed record ReliabilityEconomics(Guid GenerationId, string Exchange, string Currency, decimal EntryCost, decimal ExpectedPayoutAtEntry,
    decimal ExpectedProfitAtEntry, decimal SettlementPayout, decimal RealizedPaperProfit, decimal UnsettledCostBasis, decimal ModeledFees);
public sealed record ReliabilityExecutionReference(Guid ExecutionId, Guid GenerationId, Guid? SessionId, string OpportunityKey, string? TriggerInputStamp,
    string? SizingDecisionFingerprint, Guid? RiskPolicyRevision, Guid? AutomationProfileRevision, DateTimeOffset CommittedAt);
public sealed record ReliabilityReport(Guid CampaignId, Guid WorkspaceId, string Name, DateTimeOffset StartedAt, DateTimeOffset EvaluatedAt,
    ReliabilityCampaignState CampaignState, int PolicyVersion, string PolicyFingerprint, ReliabilityEvidenceState State, bool EvidenceGapDetected,
    ImmutableSortedDictionary<string, long> Counters, ImmutableArray<ReliabilityCheck> Criteria, ImmutableArray<ReliabilityCheck> Invariants,
    ImmutableArray<ReliabilityEconomics> Economics, ImmutableArray<ReliabilityExecutionReference> Executions,
    ImmutableSortedDictionary<string, decimal> Statistics, string Disclaimer, ImmutableArray<string> Limitations, string EvidenceFingerprint);

public static class PaperReliabilityPolicy
{
    public static (decimal Minimum, decimal Maximum, decimal Average) Distribution(IEnumerable<decimal> values)
    { var items = values.ToArray(); return items.Length == 0 ? (0, 0, 0) : (items.Min(), items.Max(), items.Average()); }
    public const int Version = 1;
    public const int MaximumEvents = 10000, MaximumIntervals = 10000, MaximumExecutions = 10000, MaximumEvaluations = 100;
    public const string Disclaimer = "PAPER SIMULATION EVIDENCE ONLY. This report does not demonstrate real order fill quality, exchange latency, live execution atomicity, profitability with real capital, or suitability for live trading.";
    public static readonly ImmutableArray<string> Limitations = ["Paper snapshot execution only; no actual exchange fills, real rejection rate or network order latency.",
        "Resolution is ManualScenario paper resolution, not actual exchange performance.", "Polymarket network restrictions may limit public-data coverage; Kalshi fee uncertainty may block entries.",
        "Evidence can remain insufficient. CriteriaMet unlocks nothing and never enables live trading.", "Runtime measures locally observed states, not continuous external market availability."];
    public static readonly ImmutableSortedDictionary<string, long> Minimums = new Dictionary<string, long>
    { ["BackendObservedTicks"] = TimeSpan.TicksPerHour * 24, ["MonitoringRunningTicks"] = TimeSpan.TicksPerHour * 24,
        ["AutomationArmedTicks"] = TimeSpan.TicksPerHour * 4, ["AutomationHealthyTicks"] = TimeSpan.TicksPerHour * 4,
        ["CandidateInputsObserved"] = 100, ["DistinctTriggerInputs"] = 25, ["SizingAttempts"] = 20,
        ["AutomaticExecutionsCommitted"] = 5, ["AutomaticExecutionsFullySettled"] = 0 }.ToImmutableSortedDictionary(StringComparer.Ordinal);
    public static string Fingerprint => PaperAutomationPolicy.Hash(new { Version, Minimums, MaximumUnexpectedWorkerFaults = 0 });
    public static ImmutableArray<ReliabilityCheck> Criteria(IReadOnlyDictionary<string, long> counters) =>
        [.. Minimums.Select(p => new ReliabilityCheck(p.Key, p.Value, counters.GetValueOrDefault(p.Key),
            counters.GetValueOrDefault(p.Key) >= p.Value ? ReliabilityCheckState.Satisfied : ReliabilityCheckState.NotSatisfied, "MinimumInclusive")),
            new("UnexpectedWorkerFaults", 0, counters.GetValueOrDefault("UnexpectedWorkerFaults"), counters.GetValueOrDefault("UnexpectedWorkerFaults") == 0 ? ReliabilityCheckState.Satisfied : ReliabilityCheckState.NotSatisfied, "MaximumInclusive")];
    public static ReliabilityEvidenceState Evaluate(IEnumerable<ReliabilityCheck> criteria, IEnumerable<ReliabilityCheck> invariants, bool gap, bool stickyViolation)
    {
        if (stickyViolation || invariants.Any(i => i.State == ReliabilityCheckState.Violated)) return ReliabilityEvidenceState.InvariantViolation;
        if (gap || invariants.Any(i => i.State != ReliabilityCheckState.Satisfied) || criteria.Any(c => c.State == ReliabilityCheckState.Unknown || c.ExplanationCode == "MinimumInclusive" && c.State != ReliabilityCheckState.Satisfied)) return ReliabilityEvidenceState.InsufficientEvidence;
        return criteria.Any(c => c.State == ReliabilityCheckState.NotSatisfied) ? ReliabilityEvidenceState.CriteriaNotMet : ReliabilityEvidenceState.CriteriaMet;
    }
    public static string FingerprintOf(ReliabilityReport report) => PaperAutomationPolicy.Hash(report with { EvidenceFingerprint = "" });
}

// Only aggregate memory operations on producer paths. No I/O, callbacks, financial mutation or execution dependency.
public sealed class PaperReliabilityTelemetry(TimeProvider clock)
{
    public Guid BackendId { get; } = Guid.NewGuid();
    private sealed class Window(Guid campaign, Dictionary<string, long> counters, IEnumerable<string> triggers, DateTimeOffset now)
    {
        public Guid Campaign = campaign; public Dictionary<string, long> Counters = counters;
        public HashSet<string> Triggers = new(triggers, StringComparer.Ordinal);
        public DateTimeOffset At = now; public bool Monitoring, Armed, Healthy, Gap;
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Window> windows = [];
    private readonly Dictionary<Guid, (bool Monitoring, bool Armed, bool Healthy)> states = [];
    public sealed record Snapshot(Guid Campaign, Dictionary<string, long> Counters, string[] Triggers, bool Gap, DateTimeOffset At);
    private static void Add(Window w, string key, long n)
    { if (n < 0 || w.Counters.GetValueOrDefault(key) > long.MaxValue - n) { w.Gap = true; return; } w.Counters[key] = w.Counters.GetValueOrDefault(key) + n; }
    private void Advance(Window w)
    {
        var now = clock.GetUtcNow(); var ticks = (now - w.At).Ticks;
        if (ticks < 0) { w.Gap = true; return; }
        Add(w, "BackendObservedTicks", ticks);
        if (w.Monitoring) Add(w, "MonitoringRunningTicks", ticks);
        if (w.Armed) Add(w, "AutomationArmedTicks", ticks);
        if (w.Armed && w.Monitoring && w.Healthy) Add(w, "AutomationHealthyTicks", ticks);
        w.At = now;
    }
    public void Begin(Guid workspace, Guid campaign, Dictionary<string, long> counters, IEnumerable<string> triggers)
    {
        lock (gate)
        {
            if (windows.ContainsKey(workspace)) return;
            var s = states.GetValueOrDefault(workspace); var w = new Window(campaign, counters, triggers, clock.GetUtcNow()) { Monitoring = s.Monitoring, Armed = s.Armed, Healthy = s.Healthy };
            windows[workspace] = w; Add(w, "BackendObservedStarts", 1);
        }
    }
    public Snapshot? Capture(Guid workspace, bool end = false)
    {
        lock (gate)
        {
            if (!windows.TryGetValue(workspace, out var w)) return null;
            Advance(w); if (end) { Add(w, "BackendObservedStops", 1); windows.Remove(workspace); }
            return new(w.Campaign, new(w.Counters), w.Triggers.Order(StringComparer.Ordinal).ToArray(), w.Gap, w.At);
        }
    }
    public void SetState(Guid workspace, bool? monitoring = null, bool? armed = null, bool? healthy = null)
    {
        lock (gate)
        {
            var old = states.GetValueOrDefault(workspace); var next = (monitoring ?? old.Monitoring, armed ?? old.Armed, healthy ?? old.Healthy);
            if (states.Count >= 128 && !states.ContainsKey(workspace)) return;
            states[workspace] = next;
            if (windows.TryGetValue(workspace, out var w)) { Advance(w); (w.Monitoring, w.Armed, w.Healthy) = next; }
        }
    }
    public void Count(Guid workspace, string key, long n = 1)
    { lock (gate) if (windows.TryGetValue(workspace, out var w)) { if (w.Counters.Count >= 128 && !w.Counters.ContainsKey(key)) w.Gap = true; else Add(w, key, n); } }
    public void Maximum(Guid workspace, string key, long value)
    { lock (gate) if (windows.TryGetValue(workspace, out var w)) w.Counters[key] = Math.Max(w.Counters.GetValueOrDefault(key), value); }
    public void Input(Guid workspace, string key, string stamp)
    {
        lock (gate) if (windows.TryGetValue(workspace, out var w))
        { Add(w, "CandidateInputsObserved", 1); if (w.Triggers.Count < 10000) w.Triggers.Add(PaperAutomationPolicy.Hash(new { key, stamp })); else w.Gap = true; w.Counters["DistinctTriggerInputs"] = w.Triggers.Count; }
    }
    public void Gap(Guid workspace) { lock (gate) if (windows.TryGetValue(workspace, out var w)) w.Gap = true; }
    public void GapAll() { lock (gate) foreach (var w in windows.Values) w.Gap = true; }
}
