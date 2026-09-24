using System.Security.Cryptography;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Arbitrage.Strategies;

namespace Arbitrage.Backend;

public sealed record PaperPreviewTicket(Guid Id, Guid Actor, Guid Workspace, Guid Generation, DateTimeOffset At, PaperPlan Plan, PaperRiskDecision Risk);
public sealed class PaperPreviewCache(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, PaperPreviewTicket> tickets = [];
    public void Add(PaperPreviewTicket ticket)
    {
        lock (gate)
        {
            foreach (var old in tickets.Values.Where(t => clock.GetUtcNow() - t.At > PaperPlanner.PreviewMaximumAge).ToArray()) tickets.Remove(old.Id);
            if (tickets.Count >= 256) tickets.Remove(tickets.Values.MinBy(t => t.At)!.Id);
            tickets[ticket.Id] = ticket;
        }
    }
    public PaperPreviewTicket? Find(Guid id, Guid actor, Guid workspace)
    {
        lock (gate) return tickets.GetValueOrDefault(id) is { } t && t.Actor == actor && t.Workspace == workspace ? t : null;
    }
}
public sealed class PaperDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, PaperCounters> workspaces = [];
    public void Record(Guid workspace, PaperCommitResult result)
    {
        lock (gate)
        {
            if (!workspaces.TryGetValue(workspace, out var counters))
            { if (workspaces.Count >= 128) return; workspaces[workspace] = counters = new(); }
            counters.Record(result);
        }
    }
    public PaperDiagnosticsResponse Read(Guid workspace)
    { lock (gate) return workspaces.GetValueOrDefault(workspace)?.Read() ?? new(0, 0, 0, 0, 0, 0, 0); }
}
internal sealed class PaperCounters
{
    private long attempts, committed, rejected, funds, stale, duplicates, integrity;
    public void Record(PaperCommitResult result)
    {
        Interlocked.Increment(ref attempts);
        if (result.Duplicate) Interlocked.Increment(ref duplicates);
        else if (result.Execution is not null) Interlocked.Increment(ref committed);
        else Interlocked.Increment(ref rejected);
        if (result.Rejection == PaperRejection.InsufficientPaperFunds) Interlocked.Increment(ref funds);
        if (result.Rejection is PaperRejection.BookStale or PaperRejection.MarketDataChanged or PaperRejection.PreviewExpired or PaperRejection.FeeChanged or PaperRejection.RelationshipIneligible) Interlocked.Increment(ref stale);
        if (result.Rejection == PaperRejection.IntegrityFailure) Interlocked.Increment(ref integrity);
    }
    public PaperDiagnosticsResponse Read() => new(Interlocked.Read(ref attempts), Interlocked.Read(ref committed), Interlocked.Read(ref rejected),
        Interlocked.Read(ref funds), Interlocked.Read(ref stale), Interlocked.Read(ref duplicates), Interlocked.Read(ref integrity));
}

public sealed partial class PaperCoordinator(PaperStore store, RelationshipStore relationships, OpportunityJobs jobs, MonitoringCoordinator monitoring,
    OpportunityCoordinator opportunities, OrderBookCache books, PaperPreviewCache previews, LocalOptions options, TimeProvider clock,
    PaperDiagnostics diagnostics, IHostApplicationLifetime lifetime, PaperRiskDiagnostics riskDiagnostics)
{
    public async Task<PaperPreviewResponse> PreviewAsync(Guid actor, Guid workspace, PaperPreviewRequest request, CancellationToken ct)
    {
        relationships.PersistEvaluationStaleness = false;
        var now = clock.GetUtcNow(); var id = Guid.NewGuid();
        var generation = await store.ActiveAsync(workspace, ct);
        var balances = generation is null ? [] : await store.BalancesAsync(generation.Id, ct);
        PaperPlan? plan = null;
        PaperRiskDecision? risk = null;
        var error = options.TradingMode != "Paper" ? PaperRejection.NotPaperMode : generation is null ? PaperRejection.AccountUninitialized :
            generation.Integrity != PaperIntegrity.Healthy ? PaperRejection.IntegrityFailure : PaperRejection.None;
        if (error == PaperRejection.None)
        {
            var result = await PlanAsync(actor, workspace, request, ct); error = result.Rejection; plan = result.Plan;
            if (plan is not null) error = PaperStore.Funds(plan, balances);
        }
        if (plan is not null && generation is not null)
        {
            var snapshot = await store.RiskSnapshotAsync(workspace, generation.Id, ct);
            // Debit display, funds eligibility and risk headroom must describe the SAME read snapshot.
            balances = snapshot.State.Buckets.Select(b => new PaperBalanceEntry { GenerationId = generation.Id, Exchange = b.Exchange,
                Currency = b.Currency, InitialCash = b.InitialCash, AvailableCash = b.AvailableCash, Revision = b.Revision }).ToArray();
            error = PaperStore.Funds(plan, balances);
            risk = PaperRiskEvaluator.Evaluate(snapshot.Profile, snapshot.State, plan, now);
            riskDiagnostics.Record(workspace, risk);
            if (error == PaperRejection.None && risk.Decision != PaperRiskOutcome.Approved)
                error = risk.Decision == PaperRiskOutcome.NotConfigured ? PaperRejection.RiskPolicyNotConfigured : PaperRejection.RiskLimitExceeded;
            previews.Add(new(id, actor, workspace, generation.Id, now, plan, risk));
        }
        return new(id, generation?.Id, now, now + PaperPlanner.PreviewMaximumAge, error == PaperRejection.None, error.ToString(), request.OpportunityKey,
            request.Quantity, plan?.Quantity ?? 0, plan?.Fills.Select(PaperEndpoints.Fill).ToArray() ?? [], plan?.Debits.Select(d =>
            { var cash = balances.SingleOrDefault(b => b.Exchange == d.Exchange && b.Currency == d.Currency)?.AvailableCash ?? 0;
                return new PaperDebitResponse(d.Exchange, d.Currency, d.Notional, d.Fees, d.Total, cash, cash - d.Total); }).ToArray() ?? [],
            plan?.Proof.GrossCost, plan?.Proof.Fees?.TotalExchangeFees, plan?.Cost, plan?.ExpectedPayoutAtResolution, plan?.ExpectedProfitAtResolution,
            plan is null ? null : OpportunityEndpoints.Map(plan.Proof),
            ["PAPER SIMULATION — no real orders submitted.", "Snapshot Paper Fill / Immediate Taker Simulation. No latency, market impact or fill certainty is modeled.",
                "Expected payout and profit are projections. Realization requires a separate explicit Manual Scenario Resolution."], PaperRiskEndpoints.Decision(risk));
    }
    private async Task<PaperPlanResult> PlanAsync(Guid actor, Guid workspace, PaperPreviewRequest request, CancellationToken ct, bool automatic = false)
    {
        if (request.Quantity is <= 0 or > PaperPlanner.MaximumQuantity) return new(PaperRejection.RequestedQuantityInvalid);
        var source = new[] { automatic ? null : jobs.CurrentSnapshot(workspace, request.OpportunityKey), monitoring.CurrentSnapshot(workspace, request.OpportunityKey) }
            .Where(s => s is not null).OrderByDescending(s => s!.EvaluatedAt).FirstOrDefault();
        if (source is null) return new(PaperRejection.OpportunityNotFound);
        if (source.RelationshipTrust != RelationshipTrust.Deterministic) return new(PaperRejection.ManualRelationshipNotAllowed);
        var validated = await opportunities.ValidateAsync(actor, workspace, source, false, false, ct);
        if (validated.Status != OpportunityStatus.Detected || !validated.BooksActionable)
            return new(PaperPlanner.Eligibility(validated, request.Quantity));
        if (validated.Fees?.State == FeeOpportunityStatus.FeeResultStale) return new(PaperRejection.FeeChanged);
        if (request.Quantity > validated.PairedQuantity) return new(PaperRejection.InsufficientDepth);
        var page = await relationships.ReadEvaluationPageAsync(actor, workspace, false, source.RelationshipId, null, 0, 1, ct);
        var relationship = page.Items.SingleOrDefault();
        if (relationship is null) return new(PaperRejection.RelationshipIneligible);
        var candidate = OpportunityPlanner.Plan(relationship).SingleOrDefault(p => GrossOpportunityEvaluator.Key(p) == request.OpportunityKey);
        if (candidate is null) return new(PaperRejection.RelationshipIneligible);
        var captured = books.ReadTogether(candidate.A, candidate.B);
        if (captured.Where((b, i) => b.Version != source.Legs[i].SnapshotVersion).Any()) return new(PaperRejection.MarketDataChanged);
        var evaluated = GrossOpportunityEvaluator.Evaluate(candidate, captured[0], captured[1], new(MaximumEvaluationQuantity: PaperPlanner.MaximumQuantity,
            MaximumSkewMilliseconds: (int)source.MaximumAllowedSkew.TotalMilliseconds, RequestedQuantity: request.Quantity), clock.GetUtcNow(), false, ct);
        evaluated = await opportunities.EvaluateFeesAsync(workspace, evaluated, PaperPlanner.MinimumFeeAdjustedEdge, ct);
        evaluated = await opportunities.ValidateAsync(actor, workspace, evaluated, false, true, ct);
        return PaperPlanner.Create(evaluated, captured, request.Quantity, clock.GetUtcNow());
    }
    public async Task<PaperCommitResult> ConfirmAsync(Guid actor, Guid workspace, ConfirmPaperRequest request, string correlation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        var result = await ConfirmCoreAsync(actor, workspace, request, correlation, linked.Token);
        riskDiagnostics.Record(workspace, result.RiskDecision);
        diagnostics.Record(workspace, result); return result;
    }
    // Backend-internal only. Authorization is a current armed permit, never a client-supplied origin flag.
    public async Task<PaperCommitResult> ExecuteAutomaticAsync(PaperAutomationPermit permit, string key,
        Func<Action, bool> runtimeCommit, CancellationToken ct, PaperSizingBudget? budget = null)
    {
        relationships.PersistEvaluationStaleness = false;
        if (options.TradingMode != "Paper") return new(PaperRejection.NotPaperMode);
        if (monitoring.Status(permit.WorkspaceId).State != MonitoringState.Running) return new(PaperRejection.AutomationRejected, AutomationReason: PaperAutomationReason.MonitoringStopped);
        PaperSizingDecision? sizing = null;
        if (permit.Profile.Settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity)
        {
            sizing = await SizeAutomaticAsync(permit, key, budget ?? new(), ct);
            if (sizing.State != PaperSizingState.Selected) return new(PaperRejection.AutomationRejected, AutomationReason:
                sizing.State == PaperSizingState.EvaluationBudgetExceeded ? PaperAutomationReason.SizingBudgetDeferred :
                sizing.State == PaperSizingState.NoAdmissibleQuantity ? PaperAutomationReason.NoAdmissibleAdaptiveQuantity :
                sizing.State == PaperSizingState.ArithmeticOverflow ? PaperAutomationReason.ArithmeticOverflow : PaperAutomationReason.OpportunityUnavailable, SizingDecision: sizing);
        }
        var planned = sizing is null ? await PlanAsync(permit.ActorId, permit.WorkspaceId, new(key, permit.Profile.Settings.FixedQuantity), ct, true) : new PaperPlanResult(PaperRejection.None, sizing.SelectedPlan);
        if (planned.Plan is not { } plan) return new(planned.Rejection);
        var stamp = PaperAutomationPolicy.Stamp(plan.Proof, permit.Profile);
        var request = PaperAutomationPolicy.RequestId(permit.SessionId, key, stamp, plan.Quantity, sizing?.Proof);
        var fingerprint = sizing is null ? PaperAutomationPolicy.Hash(new { permit.SessionId, key, stamp, plan.Quantity }) :
            PaperAutomationPolicy.Hash(new { permit.SessionId, key, stamp, plan.Quantity, sizing.Proof!.DecisionFingerprint });
        var previous = await store.RequestAsync(permit.WorkspaceId, request, ct);
        if (previous is not null) return previous.RequestFingerprint == fingerprint ? new(PaperRejection.None, previous, true) : new(PaperRejection.DuplicateRequest);
        var risk = sizing?.SelectedRiskDecision;
        if (risk is null)
        {
            var snapshot = await store.RiskSnapshotAsync(permit.WorkspaceId, permit.GenerationId, ct);
            risk = PaperRiskEvaluator.Evaluate(snapshot.Profile, snapshot.State, plan, clock.GetUtcNow());
        }
        async Task<PaperRejection> Validate(CancellationToken token)
        {
            if (options.TradingMode != "Paper") return PaperRejection.NotPaperMode;
            if (monitoring.Status(permit.WorkspaceId).State != MonitoringState.Running) return PaperRejection.AutomationRejected;
            var current = await opportunities.ValidateAsync(permit.ActorId, permit.WorkspaceId, plan.Proof, false, true, token);
            var hard = PaperPlanner.Eligibility(current, plan.Quantity);
            return hard != PaperRejection.None ? hard : PaperAutomationPolicy.Quality(current, permit.Profile.Settings) == PaperAutomationReason.None ? PaperRejection.None : PaperRejection.AutomationRejected;
        }
        var result = await store.CommitAsync(permit.ActorId, permit.WorkspaceId, permit.GenerationId, request, fingerprint, plan, "automatic-paper", Validate,
            commit => runtimeCommit(() =>
            {
                if (!monitoring.CommitIfRunning(permit.WorkspaceId, () =>
                {
                    var candidate = monitoring.AutomaticPaperCandidates(permit.WorkspaceId, permit.Profile.Settings.MaximumCandidatesPerCycle).SingleOrDefault(r => r.Result.OpportunityKey == key);
                    if (candidate is null || PaperAutomationPolicy.Stamp(candidate.Result, permit.Profile) != stamp ||
                        !books.CommitIfCurrent(plan.Proof.Legs.Select(l => l.Instrument).ToArray(), plan.Proof.Legs.Select(l => l.SnapshotVersion).ToArray(), commit))
                        throw new PaperAutomationException(PaperAutomationReason.CommitConflict);
                })) throw new PaperAutomationException(PaperAutomationReason.MonitoringStopped);
            }),
            ct, risk, new(permit, stamp, sizing?.Proof));
        diagnostics.Record(permit.WorkspaceId, result); riskDiagnostics.Record(permit.WorkspaceId, result.RiskDecision); return result with { SizingDecision = sizing };
    }
    private async Task<PaperCommitResult> ConfirmCoreAsync(Guid actor, Guid workspace, ConfirmPaperRequest request, string correlation, CancellationToken ct)
    {
        relationships.PersistEvaluationStaleness = false;
        if (options.TradingMode != "Paper") return new(PaperRejection.NotPaperMode);
        if (!request.ConfirmSimulation) return new(PaperRejection.ConfirmationRequired);
        if (request.RequestId == Guid.Empty || request.Quantity is <= 0 or > PaperPlanner.MaximumQuantity) return new(PaperRejection.RequestedQuantityInvalid);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        var prior = await store.RequestAsync(workspace, request.RequestId, ct);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(PaperRejection.None, prior, true) : new(PaperRejection.DuplicateRequest);
        if (jobs.CurrentSnapshot(workspace, request.OpportunityKey) is null && monitoring.CurrentSnapshot(workspace, request.OpportunityKey) is null)
            return new(PaperRejection.OpportunityNotFound);
        var ticket = previews.Find(request.PreviewId, actor, workspace);
        if (ticket is null) return new(PaperRejection.PreviewExpired);
        if (ticket.Plan.Quantity != request.Quantity || ticket.Plan.Proof.OpportunityKey != request.OpportunityKey) return new(PaperRejection.DuplicateRequest);
        async Task<PaperRejection> Validate(CancellationToken token)
        {
            if (options.TradingMode != "Paper") return PaperRejection.NotPaperMode;
            var age = clock.GetUtcNow() - ticket.At;
            if (age < TimeSpan.Zero || age > PaperPlanner.PreviewMaximumAge) return PaperRejection.PreviewExpired;
            var current = await opportunities.ValidateAsync(actor, workspace, ticket.Plan.Proof, false, true, token);
            return PaperPlanner.Eligibility(current, ticket.Plan.Quantity);
        }
        var legIds = ticket.Plan.Fills.Select(f => f.LegId).Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        var executionPlan = ticket.Plan with { Id = Guid.NewGuid(), Fills = [.. ticket.Plan.Fills.Select(f => f with { Id = Guid.NewGuid(), LegId = legIds[f.LegId], FilledAt = clock.GetUtcNow() })] };
        return await store.CommitAsync(actor, workspace, ticket.Generation, request.RequestId, fingerprint, executionPlan, correlation, Validate,
            commit => books.CommitIfCurrent(ticket.Plan.Proof.Legs.Select(l => l.Instrument).ToArray(), ticket.Plan.Proof.Legs.Select(l => l.SnapshotVersion).ToArray(), commit), ct, ticket.Risk);
    }
}
