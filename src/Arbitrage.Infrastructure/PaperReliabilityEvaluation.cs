using System.Collections.Immutable;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed partial class PaperReliabilityStore
{
    public async Task<ReliabilityReport> EvaluateAsync(Guid actor, Guid workspace, Guid id, CancellationToken ct)
    {
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var c = await db.Set<PaperReliabilityCampaignEntry>().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, ct) ?? throw new ArgumentException("CampaignNotFound");
        if (c.State is ReliabilityCampaignState.Completed or ReliabilityCampaignState.Cancelled)
            return JsonSerializer.Deserialize<ReliabilityReport>((await LatestAsync(id, ct))?.ReportJson ?? throw new ReliabilityConflict("FinalEvidenceUnavailable"))!;
        var intervals = await db.Set<PaperReliabilityIntervalEntry>().AsNoTracking().Where(x => x.CampaignId == id).OrderBy(x => x.StartOrder).Take(10001).ToArrayAsync(ct);
        var gap = c.EvidenceGapDetected || c.EventRetentionTruncated || intervals.Length > 10000;
        var counters = JsonSerializer.Deserialize<Dictionary<string, long>>(c.CountersJson)!;
        var previous = await LatestAsync(id, ct);
        var previousReport = previous is null ? null : JsonSerializer.Deserialize<ReliabilityReport>(previous.ReportJson);
        counters["ReconciliationRuns"] = previousReport?.Counters.GetValueOrDefault("ReconciliationRuns") ?? 0;
        counters["ReconciliationFailures"] = previousReport?.Counters.GetValueOrDefault("ReconciliationFailures") ?? 0;
        var stats = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var checks = new SortedDictionary<string, ReliabilityCheck>(StringComparer.Ordinal);
        void Check(string code, bool valid, long observed = 0) => checks[code] = new(code, 0, observed, valid ? ReliabilityCheckState.Satisfied : ReliabilityCheckState.Violated, valid ? "VerifiedDurableFacts" : "DurableInvariantViolation");
        void Unknown(string code) { if (!checks.TryGetValue(code, out var prior) || prior.State != ReliabilityCheckState.Violated) checks[code] = new(code, 0, 0, ReliabilityCheckState.Unknown, "EvidenceUnavailable"); gap = true; }
        var names = new[] { "NoNegativePaperCash", "NoDuplicateExecutionRequestIds", "NoDuplicateAutomaticSessionTriggerExecution", "NoAutomaticExecutionAfterKillLatchOrdering",
            "NoAutomaticExecutionWithoutExplicitArm", "NoAutomaticExecutionWithoutApprovedRiskProof", "NoAutomaticExecutionWithoutSupportedAutomationProof",
            "NoAutomaticExecutionWithoutResolvedFeeProof", "NoAutomaticExecutionForManualRelationship", "NoAutomaticExecutionForUnsupportedStrategy",
            "NoAutomaticExecutionWithoutValidSizingProof", "NoAutomaticExecutionOutsideGrid", "NoAutomaticExecutionAboveRiskQuantityLimit",
            "NoAutomaticExecutionAboveAutomationSessionDebitLimit", "NoSettledPositionStillCountedAsOpen", "NoSettlementDuplicatePayout", "LedgerReconciliationHealthy", "AutomaticInputQualityValid", "ExpectedPayoutMatchesManualScenario" };
        foreach (var name in names) Check(name, true);
        if (c.PolicyVersion != PaperReliabilityPolicy.Version || c.PolicyFingerprint != PaperReliabilityPolicy.Fingerprint) Unknown("EvidencePolicySupported");
        var first = intervals.FirstOrDefault()?.StartOrder ?? 0; var last = intervals.LastOrDefault()?.EndOrder ?? first;
        var operations = await db.Set<PaperWriterOrderEntry>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id > first && x.Id <= last).OrderBy(x => x.Id).Take(10001).ToArrayAsync(ct);
        if (operations.Length > 10000) gap = true;
        bool Included(long order) => intervals.Any(i => order > i.StartOrder && order <= i.EndOrder);
        var refs = operations.Where(o => o.Kind == "Execution" && Included(o.Id)).ToDictionary(o => o.ReferenceId);
        var anchoredIds = operations.Where(o => o.Kind == "Execution").Select(o => o.ReferenceId).ToHashSet();
        // Detect orphan/unanchored entries as well as indexed order references; time boundaries alone never prove kill ordering.
        var candidates = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.WorkspaceId == workspace && e.CreatedAt >= c.StartedAt && e.CreatedAt <= clock.GetUtcNow())
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(10001).ToArrayAsync(ct);
        if (candidates.Length > 10000) gap = true;
        var executions = candidates.Where(e => refs.ContainsKey(e.Id) || !anchoredIds.Contains(e.Id) && intervals.Any(i => e.CreatedAt > i.StartedAt && e.CreatedAt <= i.EndedAt)).ToArray();
        if (candidates.Any(e => e.Origin == PaperExecutionOrigin.AutomaticPaper && !anchoredIds.Contains(e.Id) && e.CreatedAt == c.StartedAt))
            Unknown("NoAutomaticExecutionWithoutExplicitArm");
        var automatic = executions.Where(e => e.Origin == PaperExecutionOrigin.AutomaticPaper).ToArray();
        counters["AutomaticExecutionsCommitted"] = automatic.Length; counters["ManualExecutionsCommitted"] = executions.Length - automatic.Length;
        counters["AutomaticExecutionsFullySettled"] = 0;
        counters["AutomaticExecutionsPartiallySettled"] = 0;
        counters["KillSwitchLatches"] = operations.Count(o => o.Kind == "KillSwitchLatched" && Included(o.Id)); counters["KillSwitchResets"] = operations.Count(o => o.Kind == "KillSwitchReset" && Included(o.Id));
        counters["AutomationSessions"] = operations.Count(o => o.Kind == "AutomationArmed" && Included(o.Id));
        counters["AutomationArms"] = counters["AutomationSessions"];
        counters["AutomationDisarms"] = operations.Count(o => o.Kind == "AutomationDisarmed" && Included(o.Id));
        counters["UnexpectedShutdownRecoveryCount"] = await db.Set<PaperReliabilityEventEntry>().CountAsync(e => e.CampaignId == id && e.Kind == "UnexpectedShutdownRecovery", ct);
        foreach (var operation in operations.Where(o => o.Kind == "AutomationDisarmed" && Included(o.Id)))
        { var key = "SessionTerminalReason:" + operation.ProofJson; counters[key] = counters.GetValueOrDefault(key) + 1; }
        var durations = operations.Where(o => o.Kind == "AutomationArmed" && Included(o.Id)).Select(arm =>
        {
            var stop = operations.FirstOrDefault(o => o.Kind == "AutomationDisarmed" && o.SessionId == arm.SessionId && o.Id > arm.Id);
            var end = stop?.At ?? intervals.Max(i => i.EndedAt);
            return intervals.Sum(i => Math.Max(0L, ((end < i.EndedAt ? end : i.EndedAt) - (arm.At > i.StartedAt ? arm.At : i.StartedAt)).Ticks));
        }).ToArray();
        stats["AverageObservedSessionDurationTicks"] = durations.Length == 0 ? 0 : durations.Average(x => (decimal)x);
        var sessions = automatic.Select(e => e.AutomationSessionId).Where(x => x != null).Distinct().ToArray();
        var arms = await db.Set<PaperWriterOrderEntry>().AsNoTracking().Where(o => o.WorkspaceId == workspace && o.Kind == "AutomationArmed" && sessions.Contains(o.SessionId)).Take(10001).ToArrayAsync(ct);
        var disarms = await db.Set<PaperWriterOrderEntry>().AsNoTracking().Where(o => o.WorkspaceId == workspace && o.Kind == "AutomationDisarmed" && sessions.Contains(o.SessionId)).Take(10001).ToArrayAsync(ct);
        if (disarms.Length > 10000) Unknown("NoAutomaticExecutionWithoutExplicitArm");
        var history = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.WorkspaceId == workspace && sessions.Contains(e.AutomationSessionId)).OrderBy(e => e.CreatedAt).Take(10001).ToArrayAsync(ct);
        if (history.Length > 10000 || arms.Length > 10000) gap = true;
        var priorKill = await db.Set<PaperWriterOrderEntry>().AsNoTracking().Where(o => o.WorkspaceId == workspace && o.Id <= first && (o.Kind == "KillSwitchLatched" || o.Kind == "KillSwitchReset")).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
        var kills = operations.Where(o => o.Kind is "KillSwitchLatched" or "KillSwitchReset").Concat(priorKill is null ? [] : new[] { priorKill }).OrderBy(o => o.Id).ToArray();
        if (kills.LastOrDefault(k => k.Kind == "KillSwitchLatched") is { } latch) counters["LastLatchUtcTicks"] = latch.At.UtcTicks;
        Check("NoDuplicateExecutionRequestIds", executions.Select(e => e.RequestId).Distinct().Count() == executions.Length);
        Check("NoDuplicateAutomaticSessionTriggerExecution", automatic.Select(e => (e.AutomationSessionId, e.AutomationInputStamp)).Distinct().Count() == automatic.Length);
        var initial = await db.Set<PaperReliabilityEventEntry>().Where(e => e.CampaignId == id && e.Kind == "InitialGeneration" && e.ReferenceId != null).Select(e => e.ReferenceId!.Value).ToArrayAsync(ct);
        var active = await paper.ActiveAsync(workspace, ct); var generations = executions.Select(e => e.GenerationId).Concat(initial)
            .Concat(operations.Where(o => o.Kind == "GenerationTransition" && Included(o.Id)).Select(o => o.ReferenceId))
            .Concat(active is null ? [] : new[] { active.Id }).Distinct().Take(101).ToArray();
        if (generations.Length > 100 || generations.Length == 0) Unknown("LedgerReconciliationHealthy");
        var balances = await db.Set<PaperBalanceEntry>().AsNoTracking().Where(b => generations.Contains(b.GenerationId)).Take(10001).ToArrayAsync(ct);
        if (balances.Length > 10000) gap = true;
        Check("NoNegativePaperCash", balances.All(b => b.AvailableCash >= 0 && b.ReservedCash >= 0), balances.Count(b => b.AvailableCash < 0 || b.ReservedCash < 0));
        stats["MinimumCurrentCash"] = balances.Length == 0 ? 0 : balances.Min(b => b.AvailableCash);
        var generationRevisions = await db.Set<PaperGenerationEntry>().AsNoTracking().Where(g => generations.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Revision, ct);
        foreach (var generation in generations.Take(100))
        {
            if (await db.Set<PaperExecutionEntry>().CountAsync(e => e.GenerationId == generation, ct) > 10000 || await db.Set<PaperPositionEntry>().CountAsync(p => p.GenerationId == generation, ct) > 10000)
            { Unknown("LedgerReconciliationHealthy"); continue; }
            counters["ReconciliationRuns"] = counters.GetValueOrDefault("ReconciliationRuns") + 1;
            if (await paper.ReconcileAsync(actor, workspace, generation, ct, readOnly: true) != PaperIntegrity.Healthy)
            { Check("LedgerReconciliationHealthy", false); counters["ReconciliationFailures"] = counters.GetValueOrDefault("ReconciliationFailures") + 1; }
        }
        counters["LastReconciliationUtcTicks"] = clock.GetUtcNow().UtcTicks;
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => generations.Contains(p.GenerationId)).Take(10001).ToArrayAsync(ct);
        Check("NoSettledPositionStillCountedAsOpen", positions.All(p => p.Status != PaperPositionStatus.Open || p.SettledAt == null && p.SettlementResolutionId == null));
        var payouts = await db.Set<PaperTransactionEntry>().AsNoTracking().Where(t => generations.Contains(t.GenerationId) && t.ResolutionId != null).Take(10001).ToArrayAsync(ct);
        if (positions.Length > 10000 || payouts.Length > 10000) gap = true;
        var payoutIds = payouts.Select(p => p.Id).ToArray();
        var credits = await db.Set<PaperLedgerEntry>().AsNoTracking().Where(l => payoutIds.Contains(l.TransactionId)).Take(20001).ToArrayAsync(ct);
        Check("NoSettlementDuplicatePayout", payouts.Select(p => p.ResolutionId).Distinct().Count() == payouts.Length &&
            credits.Select(l => (l.TransactionId, l.Exchange, l.Currency)).Distinct().Count() == credits.Length);
        if (credits.Length > 20000) Unknown("NoSettlementDuplicatePayout");
        var allResolutions = await db.Set<PaperResolutionEntry>().AsNoTracking().Where(r => generations.Contains(r.GenerationId)).Take(10001).ToArrayAsync(ct);
        var settlementIds = operations.Where(o => o.Kind == "SettlementCommitted" && Included(o.Id)).Select(o => o.ReferenceId).ToHashSet();
        var resolutions = allResolutions.Where(r => settlementIds.Contains(r.Id)).ToArray();
        var resolutionIds = resolutions.Select(r => r.Id).ToArray(); var outcomes = await db.Set<PaperResolutionOutcomeEntry>().AsNoTracking().Where(o => resolutionIds.Contains(o.ResolutionId)).Take(20001).ToArrayAsync(ct);
        if (allResolutions.Length > 10000 || outcomes.Length > 20000) gap = true;
        counters["SettlementCount"] = resolutions.Length;
        var money = new List<ReliabilityEconomics>(); var references = new List<ReliabilityExecutionReference>(); var quantities = new List<decimal>(); var evaluatedCounts = new List<decimal>();
        var inputAges = new List<decimal>(); var skews = new List<decimal>(); var modeledFills = new List<PaperFill>();
        void Fail(string name) => Check(name, false, checks[name].ObservedValue >= long.MaxValue ? long.MaxValue : (long)checks[name].ObservedValue + 1);
        foreach (var e in automatic)
        {
            try
            {
                var plan = JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!; var proof = JsonSerializer.Deserialize<PaperAutomationProof>(e.AutomationProofJson ?? "null");
                void Increment(string key) => counters[key] = counters.GetValueOrDefault(key) + 1;
                Increment("InputQuality:" + plan.Proof.InputQuality); Increment("RelationshipTrust:" + plan.Proof.RelationshipTrust);
                var proofHealthy = PaperStore.AutomationProofHealthy(e, plan);
                Increment((proof?.Settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity ? "SizingProof" : "FixedProof") + (proofHealthy ? "Valid" : "Invalid"));
                Increment(plan.Proof.Fees?.State == FeeOpportunityStatus.FeeAdjustedDetected && !plan.Proof.Fees.Breakdown.IsDefaultOrEmpty ? "ResolvedFeeExecutions" : "InvalidOrUnresolvedFeeExecutions");
                modeledFills.AddRange(plan.Fills);
                inputAges.AddRange(plan.Proof.Legs.Where(l => l.RetrievedAt != null).Select(l => (decimal)(e.CreatedAt - l.RetrievedAt!.Value).Ticks / TimeSpan.TicksPerMillisecond));
                if (plan.Proof.ObservedSkew is { } skew) skews.Add((decimal)skew.Ticks / TimeSpan.TicksPerMillisecond);
                var operation = refs.GetValueOrDefault(e.Id); var arm = arms.SingleOrDefault(a => a.SessionId == e.AutomationSessionId);
                var armProof = arm?.ProofJson is { } raw ? JsonSerializer.Deserialize<ReliabilityArmProof>(raw) : null;
                var executionWindow = operation is null ? null : intervals.FirstOrDefault(i => operation.Id > i.StartOrder && operation.Id <= i.EndOrder);
                if (operation is null || arm is null || arm.Id >= operation.Id || armProof is null || armProof.Permit.SessionId != e.AutomationSessionId || armProof.Permit.ActorId != e.ActorId ||
                    executionWindow is null || armProof.BackendId != executionWindow.BackendId ||
                    disarms.Any(d => d.SessionId == e.AutomationSessionId && d.Id > arm.Id && d.Id < operation.Id)) Fail("NoAutomaticExecutionWithoutExplicitArm");
                var lastKill = operation is null ? null : kills.LastOrDefault(k => k.Id < operation.Id);
                if (operation is null) Unknown("NoAutomaticExecutionAfterKillLatchOrdering"); else if (lastKill?.Kind == "KillSwitchLatched") Fail("NoAutomaticExecutionAfterKillLatchOrdering");
                if (e.RiskProofJson is null || !PaperStore.RiskProofHealthy(e, plan) || armProof?.Risk.IsValid != true || e.RiskPolicyRevision != armProof.Risk.Revision) Fail("NoAutomaticExecutionWithoutApprovedRiskProof");
                if (proof is null || !PaperStore.AutomationProofHealthy(e, plan)) { Fail("NoAutomaticExecutionWithoutSupportedAutomationProof"); Fail("NoAutomaticExecutionWithoutValidSizingProof"); }
                if (plan.Proof.Fees?.State != FeeOpportunityStatus.FeeAdjustedDetected || plan.Proof.Fees.Breakdown.IsDefaultOrEmpty) Fail("NoAutomaticExecutionWithoutResolvedFeeProof");
                if (plan.Proof.RelationshipTrust != RelationshipTrust.Deterministic) Fail("NoAutomaticExecutionForManualRelationship");
                if (plan.Proof.Strategy is not (OpportunityStrategy.CrossMarketBuyBothComplements or OpportunityStrategy.SingleMarketBinaryComplement)) Fail("NoAutomaticExecutionForUnsupportedStrategy");
                if (proof is not null && PaperAutomationPolicy.Quality(plan.Proof, proof.Settings) != PaperAutomationReason.None) Fail("AutomaticInputQualityValid");
                if (armProof is null) Unknown("NoAutomaticExecutionAboveRiskQuantityLimit"); else if (plan.Quantity > armProof.Risk.Limits.MaximumRequestedQuantity) Fail("NoAutomaticExecutionAboveRiskQuantityLimit");
                if (proof?.Settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity)
                {
                    if (!PaperQuantityGrid.Contains(proof.Settings, plan.Quantity)) Fail("NoAutomaticExecutionOutsideGrid");
                    quantities.Add(plan.Quantity); evaluatedCounts.Add(proof.Sizing?.CandidatesEvaluated ?? 0);
                }
                if (proof is not null)
                {
                    var earlier = history.Where(h => h.AutomationSessionId == e.AutomationSessionId && h.CreatedAt <= e.CreatedAt).Select(h => JsonSerializer.Deserialize<PaperPlan>(h.PlanJson)!).ToArray();
                    // Same-time entries are all included: end-of-session budget is an upper bound on every prefix.
                    foreach (var bucket in balances.Where(b => b.GenerationId == e.GenerationId))
                        if (earlier.SelectMany(p => p.Debits).Where(d => d.Exchange == bucket.Exchange && d.Currency == bucket.Currency).Sum(d => d.Total) > bucket.InitialCash * proof.Settings.MaximumSessionDebitFractionPerBucket) Fail("NoAutomaticExecutionAboveAutomationSessionDebitLimit");
                }
                counters["RealtimeBestEffortExecutions"] = counters.GetValueOrDefault("RealtimeBestEffortExecutions") + (plan.Proof.Legs.Any(l => l.Continuity == BookContinuity.BestEffort) ? 1 : 0);
                references.Add(new(e.Id, e.GenerationId, e.AutomationSessionId, e.OpportunityKey, e.AutomationInputStamp, proof?.Sizing?.DecisionFingerprint, e.RiskPolicyRevision, proof?.ProfileRevision, e.CreatedAt));
                decimal totalPayout = 0; var full = true; var anySettled = false;
                foreach (var leg in plan.Fills.GroupBy(f => f.LegId))
                {
                    var fill = leg.First(); var instrument = fill.Instrument; var resolution = resolutions.SingleOrDefault(r => r.GenerationId == e.GenerationId && r.Exchange == instrument.Exchange && r.MarketId == instrument.NativeMarketId);
                    var outcome = resolution is null ? null : outcomes.SingleOrDefault(o => o.ResolutionId == resolution.Id && o.InstrumentId == instrument.NativeInstrumentId);
                    var cost = leg.Sum(f => f.Notional + f.Fee); var quantity = leg.Sum(f => f.Quantity); var payout = quantity * (outcome?.PayoutPerShare ?? 0); totalPayout += payout; full &= outcome is not null; anySettled |= outcome is not null;
                    // Allocate captured basket payout/profit equally to its two complementary legs for separate currency/venue reporting.
                    var expected = plan.ExpectedPayoutAtResolution / 2;
                    money.Add(new(e.GenerationId, instrument.Exchange, fill.Currency, cost, expected, expected - cost, payout, outcome is null ? 0 : payout - cost, outcome is null ? cost : 0, leg.Sum(f => f.Fee)));
                }
                if (full) { counters["AutomaticExecutionsFullySettled"]++; if (totalPayout != plan.ExpectedPayoutAtResolution) Fail("ExpectedPayoutMatchesManualScenario"); counters[totalPayout == plan.ExpectedPayoutAtResolution ? "ExactPayoutMatches" : "PayoutMismatches"] = counters.GetValueOrDefault(totalPayout == plan.ExpectedPayoutAtResolution ? "ExactPayoutMatches" : "PayoutMismatches") + 1; }
                else if (anySettled) counters["AutomaticExecutionsPartiallySettled"]++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or OverflowException or ArgumentException)
            {
                foreach (var name in names.Where(n => n.StartsWith("NoAutomatic", StringComparison.Ordinal) || n == "AutomaticInputQualityValid" || n == "ExpectedPayoutMatchesManualScenario")) Unknown(name);
                gap = true;
            }
        }
        foreach (var generation in await db.Set<PaperGenerationEntry>().AsNoTracking().Where(g => generations.Contains(g.Id)).ToArrayAsync(ct))
            if (generationRevisions.GetValueOrDefault(generation.Id) != generation.Revision) { gap = true; Unknown("LedgerReconciliationHealthy"); }
        counters["AdaptiveExecutions"] = quantities.Count; counters["FixedExecutions"] = automatic.Length - quantities.Count;
        void Distribution(string key, List<decimal> values) { var value = PaperReliabilityPolicy.Distribution(values); stats[key + "Minimum"] = value.Minimum; stats[key + "Maximum"] = value.Maximum; stats[key + "Average"] = value.Average; }
        Distribution("SelectedQuantity", quantities); Distribution("SizingCandidates", evaluatedCounts);
        Distribution("InputAgeMilliseconds", inputAges); Distribution("BookSkewMilliseconds", skews);
        foreach (var bucket in modeledFills.GroupBy(f => (f.Instrument.Exchange, f.Currency)))
        {
            var quantity = bucket.Sum(f => f.Quantity); var key = bucket.Key.Exchange + ":" + bucket.Key.Currency;
            stats["ModeledBookDepthConsumed:" + key] = quantity;
            stats["ModeledEntryVWAP:" + key] = quantity == 0 ? 0 : bucket.Sum(f => f.Notional) / quantity;
        }
        long Sum(params string[] keys) => keys.Sum(k => counters.GetValueOrDefault(k));
        counters["Funnel:DuplicateSuppressed"] = Sum("DuplicateInputsSuppressed", "DuplicateSuppressed");
        counters["Funnel:CooldownSuppressed"] = Sum("CooldownRejected");
        counters["Funnel:InputQualityRejected"] = Sum("InputQualityRejected", "OpportunityUnavailable");
        counters["Funnel:EconomicsRejected"] = Sum("MinimumEdgeRejected", "MinimumProfitRejected", "InsufficientDepth");
        counters["Funnel:SizingNoAdmissible"] = Sum("NoAdmissibleAdaptiveQuantity");
        counters["Funnel:RiskRejected"] = Sum("RiskRejected", "BlockedByRisk");
        counters["Funnel:SessionLimitRejected"] = Sum("SessionExecutionLimitReached", "HourlyExecutionLimitReached", "RelationshipSessionLimit", "AutomationSessionDebitLimit");
        counters["Funnel:CommitRaceRejected"] = Sum("CommitConflict");
        counters["Funnel:ExecutionsCommitted"] = automatic.Length;
        counters["SizingCandidateEvaluations"] = Sum("AdaptiveCandidatesEvaluated");
        counters["SizingSelections"] = Sum("AdaptiveSelections");
        counters["SizingBudgetDeferrals"] = Sum("AdaptiveBudgetDeferrals");
        counters["RiskProofInvalid"] = (long)checks["NoAutomaticExecutionWithoutApprovedRiskProof"].ObservedValue;
        counters["RiskProofValid"] = automatic.Length - counters["RiskProofInvalid"];
        counters["ExecutionsAfterAnyLatchBeforeReset"] = (long)checks["NoAutomaticExecutionAfterKillLatchOrdering"].ObservedValue;
        var latestLatch = kills.LastOrDefault(k => k.Kind == "KillSwitchLatched");
        var latestReset = latestLatch is null ? null : kills.FirstOrDefault(k => k.Id > latestLatch.Id && k.Kind == "KillSwitchReset");
        counters["ExecutionCountAfterLatestLatchBeforeReset"] = latestLatch is null ? 0 : automatic.Count(e => refs.TryGetValue(e.Id, out var operation) &&
            operation.Id > latestLatch.Id && (latestReset is null || operation.Id < latestReset.Id));
        if (operations.Length > 10000 || candidates.Length > 10000 || history.Length > 10000 || generations.Length > 100 || balances.Length > 10000 || positions.Length > 10000 || payouts.Length > 10000 || allResolutions.Length > 10000 || outcomes.Length > 20000)
            foreach (var name in names) Unknown(name);
        var criteria = PaperReliabilityPolicy.Criteria(counters); var invariants = checks.Values.ToImmutableArray();
        var state = PaperReliabilityPolicy.Evaluate(criteria, invariants, gap, c.InvariantViolationDetected);
        c.InvariantViolationDetected |= state == ReliabilityEvidenceState.InvariantViolation;
        c.EvidenceGapDetected |= gap;
        var report = new ReliabilityReport(id, workspace, c.Name, c.StartedAt, clock.GetUtcNow(), c.State, c.PolicyVersion, c.PolicyFingerprint, state, gap,
            counters.ToImmutableSortedDictionary(StringComparer.Ordinal), criteria, invariants,
            money.GroupBy(m => (m.GenerationId, m.Exchange, m.Currency)).OrderBy(g => g.Key.GenerationId).ThenBy(g => g.Key.Exchange, StringComparer.Ordinal).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
                .Select(g => new ReliabilityEconomics(g.Key.GenerationId, g.Key.Exchange, g.Key.Currency, g.Sum(m => m.EntryCost), g.Sum(m => m.ExpectedPayoutAtEntry), g.Sum(m => m.ExpectedProfitAtEntry), g.Sum(m => m.SettlementPayout), g.Sum(m => m.RealizedPaperProfit), g.Sum(m => m.UnsettledCostBasis), g.Sum(m => m.ModeledFees))).ToImmutableArray(),
            references.OrderBy(r => r.CommittedAt).ThenBy(r => r.ExecutionId).ToImmutableArray(), stats.ToImmutableSortedDictionary(StringComparer.Ordinal), PaperReliabilityPolicy.Disclaimer, PaperReliabilityPolicy.Limitations, "");
        report = report with { EvidenceFingerprint = PaperReliabilityPolicy.FingerprintOf(report) };
        var evaluation = new PaperReliabilityEvaluationEntry { Id = Guid.NewGuid(), CampaignId = id, At = clock.GetUtcNow(), ReportJson = JsonSerializer.Serialize(report) };
        db.Add(evaluation); c.LatestEvaluationId = evaluation.Id;
        var old = await db.Set<PaperReliabilityEvaluationEntry>().Where(e => e.CampaignId == id).OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(99).ToArrayAsync(ct); db.RemoveRange(old);
        if (state == ReliabilityEvidenceState.InvariantViolation) await EventAsync(c, "InvariantViolationDetected", ct);
        await db.SaveChangesAsync(ct); return report;
    }
}
