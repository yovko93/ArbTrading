using Arbitrage.Application;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public sealed record PaperAutomationRuntime(PaperAutomationState State, PaperAutomationReason StopReason, PaperAutomationPermit? Session,
    int ExecutionsCommitted, int CandidatesConsidered, int CandidatesSkipped, int ExecutionsRejected, DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastCommittedAt, PaperDebit[] SessionDebits, int QueueDepth, IReadOnlyDictionary<string, long> Counters);

// One sequential local worker. Timers reconcile safety only; no dependency can acquire exchange data.
public sealed class PaperAutomationCoordinator(IServiceScopeFactory scopes, MonitoringCoordinator monitor, LocalOptions options,
    TimeProvider clock, RealtimePublisher publisher) : BackgroundService
{
    private sealed class Session(PaperAutomationPermit permit)
    {
        public readonly PaperAutomationPermit Permit = permit;
        public readonly CancellationTokenSource Cancel = new();
        public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Attempted = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
        public readonly Dictionary<(string, string), PaperDebit> Debits = [];
        public PaperAutomationState State = PaperAutomationState.Armed;
        public PaperAutomationReason Reason;
        public int Committed, Considered, Skipped, Rejected;
        public DateTimeOffset? LastActivity, LastCommitted;
        public bool AuditPending;
        public Guid? StopActor;
    }
    private readonly object gate = new();
    private readonly SemaphoreSlim process = new(1, 1), commands = new(1, 1);
    private readonly Dictionary<Guid, Session> sessions = [];
    private bool stopping;
    private static void Count(Session s, string key) => s.Counters[key] = s.Counters.GetValueOrDefault(key) is var n && n < long.MaxValue ? n + 1 : long.MaxValue;
    public PaperAutomationRuntime Runtime(Guid workspace)
    {
        lock (gate) return sessions.TryGetValue(workspace, out var s) ? new(s.State, s.Reason, s.Permit, s.Committed, s.Considered, s.Skipped, s.Rejected,
            s.LastActivity, s.LastCommitted, s.Debits.Values.ToArray(), s.Pending.Count, new Dictionary<string, long>(s.Counters)) :
            new(PaperAutomationState.Disarmed, PaperAutomationReason.None, null, 0, 0, 0, 0, null, null, [], 0, new Dictionary<string, long>());
    }
    private void Stop(Session s, PaperAutomationReason reason, Guid? actor = null)
    {
        lock (gate)
        {
            if (s.State != PaperAutomationState.Armed) return;
            s.State = reason == PaperAutomationReason.KillSwitchLatched ? PaperAutomationState.KillSwitchLatched : reason == PaperAutomationReason.WorkerFault ? PaperAutomationState.Faulted : PaperAutomationState.Disarmed;
            s.Reason = reason; s.StopActor = actor; s.Pending.Clear(); s.Cancel.Cancel(); s.AuditPending = true; Count(s, "SessionsDisarmed");
        }
        publisher.PaperAutomationChanged(s.Permit.WorkspaceId);
    }
    public void Notify(Guid workspace)
    {
        var rows = monitor.AutomaticPaperCandidates(workspace, 100);
        lock (gate)
        {
            if (!sessions.TryGetValue(workspace, out var s) || s.State != PaperAutomationState.Armed) return;
            if (monitor.Status(workspace).State != MonitoringState.Running) { Stop(s, PaperAutomationReason.MonitoringStopped); return; }
            foreach (var row in rows.Take(s.Permit.Profile.Settings.MaximumCandidatesPerCycle))
            {
                Count(s, "CandidatesObserved"); var key = row.Result.OpportunityKey;
                if (s.Pending.Contains(key)) Count(s, "CandidatesCoalesced");
                else if (s.Pending.Count < 100) { s.Pending.Add(key); Count(s, "CandidatesQueued"); }
            }
        }
    }
    public async Task<PaperAutomationProfile> SaveAsync(Guid actor, Guid workspace, Guid? expected, bool confirmed, PaperAutomationSettings settings, CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            if (Runtime(workspace).State == PaperAutomationState.Armed) throw new PaperAutomationException(PaperAutomationReason.AutomationMustBeDisarmed);
            using var scope = scopes.CreateScope(); var value = await scope.ServiceProvider.GetRequiredService<PaperStore>().SaveAutomationProfileAsync(actor, workspace, expected, confirmed, settings, ct);
            publisher.PaperAutomationChanged(workspace); return value;
        }
        finally { commands.Release(); }
    }
    public async Task ArmAsync(Guid actor, Guid workspace, Guid profile, Guid risk, Guid generation, Guid? kill, bool confirmed, CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            if (options.TradingMode != "Paper") throw new PaperAutomationException(PaperAutomationReason.NotPaperMode);
            lock (gate)
            {
                if (stopping) throw new PaperAutomationException(PaperAutomationReason.BackendStopped);
                if (sessions.GetValueOrDefault(workspace)?.State == PaperAutomationState.Armed) throw new PaperAutomationException(PaperAutomationReason.AlreadyArmed);
                if (!sessions.ContainsKey(workspace) && sessions.Count >= 128) throw new PaperAutomationException(PaperAutomationReason.InvalidProfile);
            }
            if (monitor.Status(workspace).State != MonitoringState.Running) throw new PaperAutomationException(PaperAutomationReason.MonitoringStopped);
            Session? previous; lock (gate) previous = sessions.GetValueOrDefault(workspace);
            if (previous is not null) await AuditStopAsync(previous, ct);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PaperStore>().ArmAutomationAsync(actor, workspace, profile, risk, generation, kill, confirmed, (permit, commit) =>
            {
                lock (gate) return !stopping && monitor.CommitIfRunning(workspace, () => { commit(); sessions[workspace] = new(permit); Count(sessions[workspace], "SessionsArmed"); });
            }, ct);
            Notify(workspace); publisher.PaperAutomationChanged(workspace);
        }
        finally { commands.Release(); }
    }
    public async Task DisarmAsync(Guid actor, Guid workspace, CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            Session? s; lock (gate) s = sessions.GetValueOrDefault(workspace);
            if (s is not null) { Stop(s, PaperAutomationReason.Disarmed, actor); await AuditStopAsync(s, ct); }
        }
        finally { commands.Release(); }
    }
    public async Task KillAsync(Guid actor, Guid workspace, bool latch, Guid? expected, bool confirmed, string reason, CancellationToken ct)
    {
        // Emergency stop must NOT wait for the worker semaphore. SQLite defines its ordering against a commit.
        await commands.WaitAsync(ct);
        try
        {
            if (!latch && Runtime(workspace).State == PaperAutomationState.Armed) throw new PaperAutomationException(PaperAutomationReason.AutomationMustBeDisarmed);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PaperStore>().SetAutomationKillAsync(actor, workspace, latch, expected, confirmed, reason, ct);
            Session? s; lock (gate) s = sessions.GetValueOrDefault(workspace);
            if (s is not null)
            {
                if (latch) { Stop(s, PaperAutomationReason.KillSwitchLatched, actor); lock (gate) { s.State = PaperAutomationState.KillSwitchLatched; Count(s, "EmergencyStops"); } }
                else lock (gate) { s.State = PaperAutomationState.Disarmed; s.Reason = PaperAutomationReason.Disarmed; }
                await AuditStopAsync(s, ct);
            }
            publisher.PaperAutomationChanged(workspace);
        }
        finally { commands.Release(); }
    }
    private async Task AuditStopAsync(Session s, CancellationToken ct)
    {
        lock (gate) { if (!s.AuditPending) return; s.AuditPending = false; }
        try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<PaperStore>().AutomationDisarmedAuditAsync(s.Permit, s.StopActor ?? s.Permit.ActorId, s.Reason, ct); }
        catch { lock (gate) s.AuditPending = true; throw; }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        monitor.AutomaticPaperInputsChanged += Notify;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await ProcessOnceAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { monitor.AutomaticPaperInputsChanged -= Notify; }
    }
    // Explicit deterministic scheduler hook, not an HTTP endpoint.
    public async Task ProcessOnceAsync(CancellationToken ct = default)
    {
        await process.WaitAsync(ct);
        try
        {
            Session[] current; lock (gate) current = sessions.Values.ToArray();
            foreach (var s in current)
            {
                try
                {
                    if (s.State != PaperAutomationState.Armed) { await AuditStopAsync(s, ct); continue; }
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cancel.Token); var token = linked.Token;
                    using (var scope = scopes.CreateScope())
                    {
                        var store = scope.ServiceProvider.GetRequiredService<PaperStore>(); var p = s.Permit;
                        var snapshot = await store.RiskSnapshotAsync(p.WorkspaceId, null, token);
                        var profile = await store.AutomationProfileAsync(p.WorkspaceId, token); var kill = await store.AutomationControlAsync(p.WorkspaceId, token);
                        var reason = options.TradingMode != "Paper" ? PaperAutomationReason.NotPaperMode :
                            kill?.IsLatched == true || kill?.Revision != p.KillRevision ? PaperAutomationReason.KillSwitchLatched :
                            monitor.Status(p.WorkspaceId).State != MonitoringState.Running ? PaperAutomationReason.MonitoringStopped :
                            snapshot.State.GenerationId != p.GenerationId ? PaperAutomationReason.PaperGenerationChanged :
                            snapshot.Profile?.Revision != p.RiskRevision ? PaperAutomationReason.RiskPolicyChanged :
                            profile?.Revision != p.Profile.Revision ? PaperAutomationReason.AutomationPolicyChanged :
                            profile?.Valid(snapshot.Profile) != true || !snapshot.State.Healthy ? PaperAutomationReason.IntegrityFailure :
                            s.Committed >= p.Profile.Settings.MaximumExecutionsPerSession ? PaperAutomationReason.SessionExecutionLimitReached :
                            await store.AutomationHourlyCountAsync(p.WorkspaceId, token) >= p.Profile.Settings.MaximumExecutionsPerHour ? PaperAutomationReason.HourlyExecutionLimitReached :
                            PaperRiskEvaluator.Evaluate(snapshot.Profile, snapshot.State, null, clock.GetUtcNow()).Decision != PaperRiskOutcome.Approved ? PaperAutomationReason.BlockedByRisk : PaperAutomationReason.None;
                        if (reason != PaperAutomationReason.None) { Stop(s, reason); await AuditStopAsync(s, ct); continue; }
                    }
                    Notify(s.Permit.WorkspaceId); // Missed-event recovery; same stamps do not get retried on timer ticks.
                    var ranked = monitor.AutomaticPaperCandidates(s.Permit.WorkspaceId, s.Permit.Profile.Settings.MaximumCandidatesPerCycle);
                    lock (gate) s.Pending.IntersectWith(ranked.Select(r => r.Result.OpportunityKey));
                    foreach (var row in ranked)
                    {
                        token.ThrowIfCancellationRequested(); var key = row.Result.OpportunityKey; var stamp = PaperAutomationPolicy.Stamp(row.Result, s.Permit.Profile);
                        lock (gate)
                        {
                            if (!s.Pending.Remove(key)) continue;
                            if (s.Attempted.GetValueOrDefault(key) == stamp) { Count(s, "DuplicateInputsSuppressed"); continue; }
                            if (s.Attempted.Count >= 1024 && !s.Attempted.ContainsKey(key)) s.Attempted.Remove(s.Attempted.Keys.First());
                            s.Attempted[key] = stamp; s.Considered++; s.LastActivity = clock.GetUtcNow(); Count(s, "CandidatesProcessed");
                        }
                        var reason = PaperAutomationPolicy.Quality(row.Result, s.Permit.Profile.Settings); PaperCommitResult? result = null;
                        if (reason == PaperAutomationReason.None)
                        {
                            using var scope = scopes.CreateScope();
                            result = await scope.ServiceProvider.GetRequiredService<PaperCoordinator>().ExecuteAutomaticAsync(s.Permit, key, commit =>
                            { lock (gate) return !stopping && s.State == PaperAutomationState.Armed && sessions.GetValueOrDefault(s.Permit.WorkspaceId) == s &&
                                monitor.CommitIfRunning(s.Permit.WorkspaceId, () => { if (options.TradingMode != "Paper") throw new PaperAutomationException(PaperAutomationReason.NotPaperMode); commit(); }); }, token);
                            reason = result.AutomationReason ?? (result.Execution is not null ? result.Duplicate ? PaperAutomationReason.DuplicateSuppressed : PaperAutomationReason.Committed : result.Rejection switch
                            { PaperRejection.RiskLimitExceeded or PaperRejection.InsufficientPaperFunds => PaperAutomationReason.RiskRejected,
                                PaperRejection.RiskPolicyChanged => PaperAutomationReason.RiskPolicyChanged, PaperRejection.GenerationChanged => PaperAutomationReason.PaperGenerationChanged,
                                PaperRejection.InsufficientDepth => PaperAutomationReason.InsufficientDepth, PaperRejection.IntegrityFailure => PaperAutomationReason.IntegrityFailure,
                                PaperRejection.ArithmeticOverflow => PaperAutomationReason.ArithmeticOverflow, _ => PaperAutomationReason.CommitConflict });
                        }
                        lock (gate)
                        {
                            Count(s, reason.ToString());
                            if (reason == PaperAutomationReason.Committed)
                            {
                                s.Committed++; s.LastCommitted = clock.GetUtcNow(); Count(s, "ExecutionsCommitted");
                                var plan = System.Text.Json.JsonSerializer.Deserialize<PaperPlan>(result!.Execution!.PlanJson)!;
                                foreach (var d in plan.Debits) { var old = s.Debits.GetValueOrDefault((d.Exchange, d.Currency)); s.Debits[(d.Exchange, d.Currency)] = new(d.Exchange, d.Currency, checked((old?.Notional ?? 0) + d.Notional), checked((old?.Fees ?? 0) + d.Fees)); }
                            }
                            else { s.Skipped++; if (result is not null && result.Execution is null) s.Rejected++; }
                        }
                        if (reason == PaperAutomationReason.Committed) publisher.PaperChanged(s.Permit.WorkspaceId);
                        if (s.Committed >= s.Permit.Profile.Settings.MaximumExecutionsPerSession) reason = PaperAutomationReason.SessionExecutionLimitReached;
                        if (reason is PaperAutomationReason.SessionExecutionLimitReached or PaperAutomationReason.HourlyExecutionLimitReached or PaperAutomationReason.KillSwitchLatched or
                            PaperAutomationReason.RiskPolicyChanged or PaperAutomationReason.PaperGenerationChanged or PaperAutomationReason.AutomationPolicyChanged or PaperAutomationReason.IntegrityFailure or PaperAutomationReason.MonitoringStopped)
                        { Stop(s, reason); break; }
                    }
                    await AuditStopAsync(s, ct);
                }
                catch (OperationCanceledException) when (s.Cancel.IsCancellationRequested || ct.IsCancellationRequested) { }
                catch (PaperAutomationException e) { Stop(s, e.Reason); try { await AuditStopAsync(s, ct); } catch (Exception) { /* Retry lifecycle audit on the next safety sweep. */ } }
                catch (Exception) { Stop(s, PaperAutomationReason.WorkerFault); try { await AuditStopAsync(s, ct); } catch (Exception) { /* Remain stopped if audit storage is unavailable. */ } }
            }
        }
        finally { process.Release(); }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        lock (gate) { stopping = true; foreach (var s in sessions.Values) Stop(s, PaperAutomationReason.BackendStopped); }
        await base.StopAsync(ct); await ProcessOnceAsync(ct);
    }
}
