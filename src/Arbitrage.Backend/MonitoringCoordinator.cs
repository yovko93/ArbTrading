using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Arbitrage.Strategies;

namespace Arbitrage.Backend;

public sealed record MonitorStatus(MonitoringState State, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, DateTimeOffset? LastEvaluationAt,
    MonitoringCoverage Coverage, int DirtyQueueDepth, bool ReconciliationRequired, string? LastErrorCode, long Generation,
    long DirtyNotificationsReceived, long DirtyNotificationsCoalesced, long DirtyNotificationsDropped, long ReconciliationPasses,
    long EvaluationsStarted, long EvaluationsCompleted, long EvaluationFailures, long RankingUpdates, long AlertsRaised,
    long AlertsSuppressedCooldown, long AlertsSuppressedNotRearmed, string CsvExportStatus, string? LastCsvErrorCode,
    long CsvSnapshotsWritten, long CsvWriteFailures);

// One explicitly admitted workspace at a time. No connector, credential, subscription or refresh dependencies.
public sealed class MonitoringCoordinator(IServiceScopeFactory scopes, OrderBookCache cache, LocalInputChanges changes,
    TimeProvider clock, RealtimePublisher publisher, MonitoringCsv csv, Arbitrage.Execution.PaperReliabilityTelemetry? reliability = null) : BackgroundService
{
    private sealed class Run(Guid actor, Guid workspace, MonitoringProfile profile, DateTimeOffset now, long epoch)
    {
        public Guid Actor = actor, Workspace = workspace;
        public Guid? StopActor;
        public MonitoringProfile Profile = profile;
        public MonitoringState State = MonitoringState.Starting;
        public DateTimeOffset StartedAt = now;
        public DateTimeOffset? StoppedAt, LastEvaluationAt;
        public DateTimeOffset NextSweep = now, NextCsv = now;
        public long Epoch = epoch, Generation, EvaluationsStarted, EvaluationsCompleted, EvaluationFailures, Reconciliations, RankingUpdates, AlertsRaised, Cooldown, NotRearmed;
        public long ReceivedAtStart, CoalescedAtStart, DroppedAtStart;
        public bool Rebuild = true, Reconcile = true, StartAudited;
        public string? Error;
        public int Available, Skipped;
        public bool Partial;
        public MonitoringDependencies Dependencies = new();
        public Dictionary<string, OpportunityPlan> Plans = new(StringComparer.Ordinal);
        public Dictionary<string, MonitoredOpportunity> Results = new(StringComparer.Ordinal);
        public HashSet<string> Dirty = new(StringComparer.Ordinal);
        public Queue<string> ReconcileRemaining = new();
        public Dictionary<(string, RankingLane), OpportunityAlertState> Alerts = [];
        public CancellationTokenSource Cancel = new();
    }
    private readonly object gate = new();
    private readonly SemaphoreSlim process = new(1, 1);
    private Run? run;
    private long epoch;
    private bool stopping;
    public event Action<Guid>? AutomaticPaperInputsChanged;
    public MonitoredOpportunity[] AutomaticPaperCandidates(Guid workspace, int maximum)
    {
        lock (gate) return run?.Workspace == workspace && run.State == MonitoringState.Running
            ? MonitoringRanking.Sort(run.Results.Values.Where(r => r.Lane == RankingLane.FeeAdjusted), "default").Take(Math.Clamp(maximum, 1, 100)).ToArray() : [];
    }
    public bool CommitIfRunning(Guid workspace, Action commit)
    { lock (gate) { if (stopping || run?.Workspace != workspace || run.State != MonitoringState.Running) return false; commit(); return true; } }
    public const int MaximumDirty = 1024;
    public const int BatchSize = 32;
    public MonitorStatus Status(Guid workspace)
    {
        lock (gate)
        {
            var r = run?.Workspace == workspace ? run : null;
            var values = r?.Results.Values.ToArray() ?? [];
            var coverage = new MonitoringCoverage(r?.Available ?? 0, r?.Plans.Values.Select(p => p.Relationship.Id).Distinct().Count() ?? 0, r?.Skipped ?? 0,
                r?.Partial ?? false, r?.Plans.Count ?? 0, values.Count(x => x.Result.Legs.All(l => l.RetrievedAt is not null)), values.Count(x => x.Result.BooksActionable),
                values.Count(x => x.Result.Fees?.TotalExchangeFees is not null), values.Count(x => x.Lane == RankingLane.FeeAdjusted), values.Count(x => x.Lane == RankingLane.GrossOnly),
                values.Count(x => x.Lane == RankingLane.NearEdge), values.Count(x => x.Lane == RankingLane.Blocked));
            return new(r?.State ?? MonitoringState.Stopped, r?.StartedAt, r?.StoppedAt, r?.LastEvaluationAt, coverage, r?.Dirty.Count ?? 0, r is not null && (r.Reconcile || r.ReconcileRemaining.Count > 0),
                r?.Error, r?.Generation ?? 0, r is null ? 0 : changes.Received - r.ReceivedAtStart, r is null ? 0 : changes.Coalesced - r.CoalescedAtStart, r is null ? 0 : changes.Dropped - r.DroppedAtStart,
                r?.Reconciliations ?? 0, r?.EvaluationsStarted ?? 0, r?.EvaluationsCompleted ?? 0, r?.EvaluationFailures ?? 0, r?.RankingUpdates ?? 0,
                r?.AlertsRaised ?? 0, r?.Cooldown ?? 0, r?.NotRearmed ?? 0, r is null || !r.Profile.CsvEnabled ? "Disabled" : csv.Status, r is null ? null : csv.Error,
                r is null ? 0 : csv.Snapshots, r is null ? 0 : csv.Failures);
        }
    }
    public MonitorStatus StartMonitoring(Guid actor, Guid workspace, MonitoringProfile profile)
    {
        if (!profile.Valid) throw new ArgumentException("Invalid monitor profile.");
        lock (gate)
        {
            if (stopping || run?.State is MonitoringState.Starting or MonitoringState.Running or MonitoringState.Degraded or MonitoringState.Stopping)
                throw new InvalidOperationException("MonitoringAlreadyActive");
            run?.Cancel.Dispose(); changes.Drain();
            csv.ResetSession();
            run = new(actor, workspace, profile, clock.GetUtcNow(), ++epoch) { ReceivedAtStart = changes.Received, CoalescedAtStart = changes.Coalesced, DroppedAtStart = changes.Dropped };
            return Status(workspace);
        }
    }
    public MonitorStatus StopMonitoring(Guid actor, Guid workspace)
    {
        MonitorStatus result;
        lock (gate)
        {
            if (run?.Workspace == workspace && run.State is MonitoringState.Starting or MonitoringState.Running or MonitoringState.Degraded)
            { run.StopActor = actor; run.State = MonitoringState.Stopping; run.Cancel.Cancel(); run.Results.Clear(); run.Dirty.Clear(); }
            result = Status(workspace);
        }
        AutomaticPaperInputsChanged?.Invoke(workspace);
        reliability?.SetState(workspace, monitoring: false, healthy: false);
        return result;
    }
    public void ProfileChanged(Guid workspace, MonitoringProfile profile)
    {
        lock (gate) if (run?.Workspace == workspace && run.State is MonitoringState.Starting or MonitoringState.Running or MonitoringState.Degraded)
        { run.Profile = profile; run.Epoch = ++epoch; run.Rebuild = true; run.Reconcile = true; run.Results.Clear(); run.Alerts.Clear(); }
    }
    public async Task SaveProfileAsync(Guid actor, Guid workspace, MonitoringProfile profile, CancellationToken ct)
    {
        // Serialize profile commits with evaluations/alert persistence so an old policy cannot publish after Save returns.
        await process.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MonitoringStore>().SaveProfileAsync(actor, workspace, profile, ct);
            ProfileChanged(workspace, profile);
        }
        finally { process.Release(); }
    }
    private void Dirty(Run r, string key)
    {
        if (!r.Plans.ContainsKey(key) || r.Dirty.Contains(key)) return;
        if (r.Dirty.Count >= MaximumDirty) { r.Reconcile = true; return; }
        r.Dirty.Add(key);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200), clock);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await ProcessOnceAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    // Exposed for deterministic scheduler/barrier fixtures; not an API route.
    public async Task ProcessOnceAsync(CancellationToken ct = default)
    {
        await process.WaitAsync(ct);
        try
        {
            Run? r; lock (gate) r = run;
            if (r is null || r.State is MonitoringState.Stopped or MonitoringState.Faulted) { changes.Drain(); return; }
            if (r.State == MonitoringState.Stopping) { await SettleAsync(r, ct); return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, r.Cancel.Token); var token = linked.Token;
            using var scope = scopes.CreateScope(); var services = scope.ServiceProvider;
            var store = services.GetRequiredService<MonitoringStore>();
            try
            {
                if (!r.StartAudited) { await store.AuditAsync(r.Actor, r.Workspace, "MonitoringStarted", token); r.StartAudited = true; }
                var notices = changes.Drain();
                lock (gate)
                {
                    if (notices.Overflow) { r.Reconcile = true; r.Rebuild = true; reliability?.Count(r.Workspace, "QueueOverflowReconciliationCount"); }
                    foreach (var change in notices.Changes.Where(c => c.WorkspaceId is null || c.WorkspaceId == r.Workspace))
                    {
                        if (change.Kind == LocalChangeKind.Relationship) r.Rebuild = true;
                        if (change.Kind == LocalChangeKind.Profile) foreach (var key in r.Plans.Keys) Dirty(r, key);
                        else foreach (var key in r.Dependencies.Affected(change)) Dirty(r, key);
                    }
                }
                var now = clock.GetUtcNow(); var sweep = now >= r.NextSweep;
                if (r.Rebuild || sweep)
                {
                    await RebuildAsync(r, services, token); // bounded, deterministic local approval/policy reconciliation, not strategy reevaluation
                    if (sweep) { await SweepAsync(r, services, token); r.NextSweep = now.AddSeconds(1); }
                }
                string[] keys; long capturedEpoch; MonitoringProfile settings;
                lock (gate)
                {
                    capturedEpoch = r.Epoch; settings = r.Profile;
                    // Overflow fallback visits each plan exactly once; subsequent notifications remain deduplicated.
                    if (r.Reconcile) { r.Reconciliations++; r.Dirty.Clear(); r.Reconcile = false; r.ReconcileRemaining = new(r.Plans.Keys.Order(StringComparer.Ordinal)); }
                    while (r.Dirty.Count < MaximumDirty && r.ReconcileRemaining.TryDequeue(out var key)) r.Dirty.Add(key);
                    reliability?.Maximum(r.Workspace, "MonitoringQueueHighWaterMark", r.Dirty.Count);
                    keys = r.Dirty.Order(StringComparer.Ordinal).Take(BatchSize).ToArray(); foreach (var key in keys) r.Dirty.Remove(key);
                }
                var coordinator = services.GetRequiredService<OpportunityCoordinator>();
                var updated = false;
                foreach (var key in keys)
                {
                    token.ThrowIfCancellationRequested(); OpportunityPlan? plan;
                    lock (gate) { plan = r.Plans.GetValueOrDefault(key); r.EvaluationsStarted++; }
                    if (plan is null) continue;
                    try
                    {
                        var result = await coordinator.EvaluateAsync(r.Actor, r.Workspace, plan,
                            new(settings.MinimumGrossEdge, settings.MaximumQuantity, MaximumSkewMilliseconds: settings.MaximumSkewMilliseconds), settings.IncludeManualRelationships, token);
                        result = await coordinator.EvaluateFeesAsync(r.Workspace, result, settings.MinimumFeeAdjustedEdge, token);
                        result = await coordinator.ValidateAsync(r.Actor, r.Workspace, result, settings.IncludeManualRelationships, true, token);
                        MonitoredOpportunity item;
                        lock (gate)
                        {
                            if (r.Epoch != capturedEpoch || r.Cancel.IsCancellationRequested || !r.Plans.ContainsKey(key)) continue;
                            item = MonitoringRanking.Classify(MonitoringRanking.Compact(result), ++r.Generation, settings) with { ProfileVersion = capturedEpoch };
                            r.Results[key] = item; r.LastEvaluationAt = clock.GetUtcNow(); r.EvaluationsCompleted++; updated = true;
                        }
                        await AlertAsync(r, item, settings, capturedEpoch, store, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (UnauthorizedAccessException) { throw; }
                    catch (Exception)
                    { lock (gate) { r.EvaluationFailures++; r.Error = "EvaluationFailed"; r.Results.Remove(key); Dirty(r, key); } }
                }
                lock (gate)
                {
                    if (r.State != MonitoringState.Stopping) r.State = r.Partial || r.Results.Count < r.Plans.Count || r.Results.Values.Any(x => x.Lane is RankingLane.Blocked or RankingLane.GrossOnly)
                        ? MonitoringState.Degraded : MonitoringState.Running;
                    if (updated) r.RankingUpdates++;
                }
                if (updated || sweep) publisher.MonitoringChanged(r.Workspace, r.Generation);
                reliability?.SetState(r.Workspace, monitoring: r.State == MonitoringState.Running);
                if (updated || sweep) { reliability?.Count(r.Workspace, "MonitoringInvalidations"); AutomaticPaperInputsChanged?.Invoke(r.Workspace); }
                if (settings.CsvEnabled && now >= r.NextCsv)
                {
                    // Validation uses local state only and prevents exporting old positive ranks after aging.
                    var rows = await RankingsAsync(r.Actor, r.Workspace, token);
                    await csv.SnapshotAsync(r.Workspace, MonitoringRanking.Sort(rows, settings.Sort).Take(settings.CsvTopRows).ToArray(), token);
                    r.NextCsv = now.AddSeconds(settings.CsvIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (r.Cancel.IsCancellationRequested || ct.IsCancellationRequested) { }
            catch (UnauthorizedAccessException) { reliability?.SetState(r.Workspace, monitoring: false, healthy: false); lock (gate) { r.State = MonitoringState.Faulted; r.Error = "WorkspaceAccessRevoked"; r.Results.Clear(); r.Dirty.Clear(); } }
            catch (Exception) { reliability?.SetState(r.Workspace, monitoring: false, healthy: false); lock (gate) { r.State = MonitoringState.Faulted; r.Error = "MonitoringUnavailable"; r.Results.Clear(); r.Dirty.Clear(); } }
        }
        finally { process.Release(); }
    }
    private async Task RebuildAsync(Run r, IServiceProvider services, CancellationToken ct)
    {
        MonitoringProfile profile; long revision; lock (gate) { profile = r.Profile; revision = r.Epoch; }
        var provider = services.GetRequiredService<IRelationshipProvider>();
        var page = await provider.ReadEvaluationPageAsync(r.Actor, r.Workspace, profile.IncludeManualRelationships, null, null, 0, profile.RelationshipLimit, ct);
        var plans = page.Items.SelectMany(p => { try { return OpportunityPlanner.Plan(p); } catch (ArgumentException) { return []; } })
            .OrderBy(GrossOpportunityEvaluator.Key, StringComparer.Ordinal).Take(2001).ToArray();
        var coverage = await services.GetRequiredService<MonitoringStore>().CoverageAsync(r.Workspace, page.Items.Count, profile.IncludeManualRelationships, ct);
        lock (gate)
        {
            if (revision != r.Epoch) return;
            var next = plans.Take(2000).GroupBy(GrossOpportunityEvaluator.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Relationship.Id).First(), StringComparer.Ordinal);
            foreach (var old in r.Plans.Keys.Except(next.Keys).ToArray()) { r.Results.Remove(old); r.Dirty.Remove(old); foreach (var a in r.Alerts.Keys.Where(k => k.Item1 == old).ToArray()) r.Alerts.Remove(a); }
            var dependencies = new MonitoringDependencies();
            foreach (var (key, plan) in next)
            {
                dependencies.Add(key, plan);
                if (!r.Plans.TryGetValue(key, out var previous) || previous.Relationship.Revision != plan.Relationship.Revision)
                { r.Results.Remove(key); if (r.Dirty.Count < MaximumDirty) r.Dirty.Add(key); else r.Reconcile = true; }
            }
            r.Plans = next; r.Dependencies = dependencies; r.Available = coverage.Available; r.Skipped = coverage.Skipped;
            r.Partial = page.HasMore || plans.Length > 2000 || coverage.Skipped > 0; r.Rebuild = false;
        }
    }
    private async Task SweepAsync(Run r, IServiceProvider services, CancellationToken ct)
    {
        MonitoredOpportunity[] items; lock (gate) items = r.Results.Values.ToArray();
        var fees = services.GetRequiredService<IFeeStore>(); var profile = await fees.ReadProfileAsync(r.Workspace, ct);
        var schedules = new Dictionary<(string, string), ResolvedFeeSchedule>();
        foreach (var item in items)
        {
            var result = item.Result; bool invalid = false;
            var actionable = true;
            foreach (var leg in result.Legs)
            {
                var book = cache.Read(leg.Instrument);
                if (book.Version != leg.SnapshotVersion) invalid = true;
                actionable &= book.Eligibility.IsActionable;
            }
            invalid |= actionable != result.BooksActionable;
            if (result.Fees is { } f)
            {
                if (f.ProfileRevision != profile.Revision) invalid = true;
                foreach (var q in f.Breakdown)
                {
                    var key = (q.Context.Exchange, q.Context.MarketId);
                    if (!schedules.TryGetValue(key, out var resolved)) schedules[key] = resolved = FeeScheduleResolver.Resolve(await fees.ReadAsync(key.Exchange, key.MarketId, ct), clock.GetUtcNow());
                    if (resolved.Fingerprint != q.ScheduleFingerprint || resolved.Status == FeeStatus.ScheduleStale && f.State != FeeOpportunityStatus.FeeScheduleStale) invalid = true;
                }
            }
            if (invalid) lock (gate)
            {
                if (r.Results.GetValueOrDefault(result.OpportunityKey)?.EvaluationGeneration != item.EvaluationGeneration) continue;
                Dirty(r, result.OpportunityKey);
                r.Results[result.OpportunityKey] = item with { Result = result.Invalidate(OpportunityStatus.StaleInput, "Local monitoring inputs changed."), Lane = RankingLane.Blocked, BestEdge = null, Distance = null, AvailableQuantity = 0 };
            }
        }
    }
    public async Task<MonitoredOpportunity[]> RankingsAsync(Guid actor, Guid workspace, CancellationToken ct)
    {
        Run? r; MonitoredOpportunity[] items; MonitoringProfile profile;
        lock (gate) { r = run?.Workspace == workspace ? run : null; items = r?.Results.Values.ToArray() ?? []; profile = r?.Profile ?? new(); }
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(actor, workspace, false, ct);
        var coordinator = scope.ServiceProvider.GetRequiredService<OpportunityCoordinator>(); var validated = new List<MonitoredOpportunity>();
        foreach (var item in items)
        {
            var current = await coordinator.ValidateAsync(actor, workspace, item.Result, profile.IncludeManualRelationships, false, ct);
            var row = MonitoringRanking.Classify(current, item.EvaluationGeneration, profile) with { AlertState = item.AlertState, ProfileVersion = item.ProfileVersion };
            lock (gate)
            {
                if (r != run || r?.Results.GetValueOrDefault(current.OpportunityKey)?.EvaluationGeneration != item.EvaluationGeneration) continue;
                if (current != item.Result) { Dirty(r!, current.OpportunityKey); r!.Results[current.OpportunityKey] = row; }
                validated.Add(row);
            }
        }
        return [.. validated];
    }
    public ArbitrageOpportunitySnapshot? CurrentSnapshot(Guid workspace, string key)
    {
        lock (gate) return run?.Workspace == workspace ? run.Results.GetValueOrDefault(key)?.Result : null;
    }
    private async Task AlertAsync(Run r, MonitoredOpportunity item, MonitoringProfile settings, long capturedEpoch, MonitoringStore store, CancellationToken ct)
    {
        foreach (var lane in new[] { RankingLane.FeeAdjusted, RankingLane.GrossOnly })
        {
            OpportunityAlertState state; bool trigger; var result = item.Result;
            lock (gate)
            {
                if (r.Epoch != capturedEpoch || r.Cancel.IsCancellationRequested || r.Results.GetValueOrDefault(result.OpportunityKey)?.EvaluationGeneration != item.EvaluationGeneration) return;
                var key = (result.OpportunityKey, lane); if (!r.Alerts.TryGetValue(key, out state!)) r.Alerts[key] = state = new();
                var fee = lane == RankingLane.FeeAdjusted;
                var valid = result.BooksActionable && result.RelationshipEligible && result.Status is OpportunityStatus.Detected or OpportunityStatus.NoGrossEdge &&
                    (fee ? result.Fees?.TotalExchangeFees is not null : true);
                var edge = fee ? result.Fees?.FeeAdjustedEdgePerShare : result.GrossEdgePerShare ?? result.BestObservedGrossEdge;
                var profit = fee ? result.Fees?.FeeAdjustedGuaranteedProfit : result.GrossProfit;
                trigger = state.Observe(edge, profit, valid, fee ? settings.FeeAlertEdge : settings.GrossAlertEdge,
                    fee ? settings.FeeAlertProfit : settings.GrossAlertProfit, settings.RearmHysteresis, TimeSpan.FromSeconds(settings.CooldownSeconds), clock.GetUtcNow(),
                    item.Lane == lane && (fee ? settings.EnableFeeAdjustedAlerts : settings.EnableGrossOnlyAlerts));
                if (state.State == "Cooldown") r.Cooldown++; if (state.State == "NotRearmed") r.NotRearmed++;
                if (r.Results.TryGetValue(result.OpportunityKey, out var current) && current.EvaluationGeneration == item.EvaluationGeneration && item.Lane == lane)
                    r.Results[result.OpportunityKey] = current with { AlertState = state.State };
            }
            if (!trigger) continue;
            var alert = new MonitoringAlert(Guid.NewGuid(), r.Workspace, result.OpportunityKey, clock.GetUtcNow(), lane,
                lane == RankingLane.GrossOnly ? "GROSS / FEES UNRESOLVED threshold crossing" : "Read-only fee-adjusted threshold crossing (modeled estimate)", item);
            await store.AddAlertAsync(r.Actor, alert, settings, ct);
            lock (gate) r.AlertsRaised++;
            publisher.MonitoringChanged(r.Workspace, r.Generation, alert.AlertId);
            if (settings.CsvEnabled) await csv.AppendAlertAsync(alert, ct);
        }
    }
    private async Task SettleAsync(Run r, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<MonitoringStore>();
            if (!r.StartAudited) { await store.AuditAsync(r.Actor, r.Workspace, "MonitoringStarted", ct); r.StartAudited = true; }
            await store.AuditAsync(r.StopActor ?? r.Actor, r.Workspace, "MonitoringStopped", ct);
        }
        catch (Exception) { r.Error = "StopAuditUnavailable"; }
        lock (gate) { r.Results.Clear(); r.Plans.Clear(); r.Dirty.Clear(); r.ReconcileRemaining.Clear(); }
        if (r.Profile.CsvEnabled) await csv.SnapshotAsync(r.Workspace, [], ct);
        lock (gate) { r.State = MonitoringState.Stopped; r.StoppedAt = clock.GetUtcNow(); }
        publisher.MonitoringChanged(r.Workspace, r.Generation);
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        lock (gate) { stopping = true; if (run is { } r && r.State is MonitoringState.Starting or MonitoringState.Running or MonitoringState.Degraded) { r.State = MonitoringState.Stopping; r.Cancel.Cancel(); } }
        await base.StopAsync(ct);
        await process.WaitAsync(ct);
        try { if (run is { State: MonitoringState.Stopping } r) await SettleAsync(r, ct); }
        finally { process.Release(); }
    }
    public override void Dispose() { base.Dispose(); lock (gate) run?.Cancel.Dispose(); }
}
