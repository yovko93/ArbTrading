using System.Text.Json;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed record ReliabilityArmProof(PaperAutomationPermit Permit, PaperRiskProfile Risk, Guid BackendId);
public sealed class ReliabilityConflict(string code) : Exception(code);
public sealed partial class PaperReliabilityStore(TradingDbContext db, RelationshipStore membership, PaperStore paper, TimeProvider clock)
{
    public Task<PaperReliabilityCampaignEntry?> CurrentAsync(Guid workspace, CancellationToken ct) => db.Set<PaperReliabilityCampaignEntry>().AsNoTracking()
        .Where(x => x.WorkspaceId == workspace && (x.State == ReliabilityCampaignState.Collecting || x.State == ReliabilityCampaignState.Paused)).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
    public Task<PaperReliabilityCampaignEntry?> ReadAsync(Guid workspace, Guid id, CancellationToken ct) => db.Set<PaperReliabilityCampaignEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, ct);
    public Task<PaperReliabilityCampaignEntry[]> ListAsync(Guid workspace, int page, CancellationToken ct) => db.Set<PaperReliabilityCampaignEntry>().AsNoTracking().Where(x => x.WorkspaceId == workspace)
        .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50).ToArrayAsync(ct);
    public Task<PaperReliabilityCampaignEntry[]> CollectingAsync(CancellationToken ct) => db.Set<PaperReliabilityCampaignEntry>().AsNoTracking().Where(x => x.State == ReliabilityCampaignState.Collecting).Take(129).ToArrayAsync(ct);
    public Task<PaperReliabilityEvaluationEntry?> LatestAsync(Guid campaign, CancellationToken ct) => db.Set<PaperReliabilityEvaluationEntry>().AsNoTracking()
        .SingleOrDefaultAsync(x => x.CampaignId == campaign && db.Set<PaperReliabilityCampaignEntry>().Any(c => c.Id == campaign && c.LatestEvaluationId == x.Id), ct);
    public async Task<PaperReliabilityEventEntry[]> EventsAsync(Guid workspace, Guid id, int page, CancellationToken ct)
    { if (await ReadAsync(workspace, id, ct) is null) throw new ArgumentException("CampaignNotFound"); return await db.Set<PaperReliabilityEventEntry>().AsNoTracking().Where(x => x.CampaignId == id).OrderBy(x => x.At).ThenBy(x => x.Id).Skip((page - 1) * 100).Take(100).ToArrayAsync(ct); }
    public async Task<PaperReliabilityEvaluationEntry[]> EvaluationsAsync(Guid workspace, Guid id, int page, CancellationToken ct)
    { if (await ReadAsync(workspace, id, ct) is null) throw new ArgumentException("CampaignNotFound"); return await db.Set<PaperReliabilityEvaluationEntry>().AsNoTracking().Where(x => x.CampaignId == id).OrderByDescending(x => x.At).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50).ToArrayAsync(ct); }
    private async Task<long> OrderAsync(Guid workspace, CancellationToken ct) => await db.Set<PaperWriterOrderEntry>().Where(x => x.WorkspaceId == workspace).Select(x => (long?)x.Id).MaxAsync(ct) ?? 0;
    private async Task EventAsync(PaperReliabilityCampaignEntry c, string kind, CancellationToken ct, Guid? reference = null, long? order = null)
    {
        var count = await db.Set<PaperReliabilityEventEntry>().CountAsync(x => x.CampaignId == c.Id, ct) + db.ChangeTracker.Entries<PaperReliabilityEventEntry>().Count(x => x.State == EntityState.Added && x.Entity.CampaignId == c.Id);
        if (count >= PaperReliabilityPolicy.MaximumEvents) { c.EventRetentionTruncated = c.EvidenceGapDetected = true; return; }
        db.Add(new PaperReliabilityEventEntry { Id = Guid.NewGuid(), CampaignId = c.Id, At = clock.GetUtcNow(), Kind = kind, ReferenceId = reference, WriterOrder = order });
    }
    public async Task<PaperReliabilityCampaignEntry> StartAsync(Guid actor, Guid workspace, string name, string notes, Guid backend, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || notes.Length > 1000) throw new ArgumentException("InvalidCampaignNameOrNotes");
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var generation = await paper.ActiveAsync(workspace, ct);
        if (generation is not null && (await db.Set<PaperExecutionEntry>().CountAsync(e => e.GenerationId == generation.Id, ct) > PaperReliabilityPolicy.MaximumExecutions ||
            await db.Set<PaperPositionEntry>().CountAsync(p => p.GenerationId == generation.Id, ct) > PaperReliabilityPolicy.MaximumExecutions))
            throw new ReliabilityConflict("IntegrityEvidenceScanLimit");
        if (generation is not null && await paper.ReconcileAsync(actor, workspace, generation.Id, ct, readOnly: true) != PaperIntegrity.Healthy)
            throw new ReliabilityConflict("IntegrityUnavailable");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await membership.RequireMemberAsync(actor, workspace, true, ct);
        if (await db.Set<PaperReliabilityCampaignEntry>().AnyAsync(x => x.WorkspaceId == workspace && (x.State == ReliabilityCampaignState.Collecting || x.State == ReliabilityCampaignState.Paused), ct)) throw new ReliabilityConflict("CampaignAlreadyActive");
        if (await db.Set<PaperGenerationEntry>().AnyAsync(x => x.WorkspaceId == workspace && x.ClosedAt == null && x.Integrity != PaperIntegrity.Healthy, ct)) throw new ReliabilityConflict("IntegrityUnavailable");
        if (generation is not null && await db.Set<PaperReliabilityEventEntry>().AnyAsync(e => (e.Kind == "InitialGeneration" || e.Kind == "GenerationTransition") && e.ReferenceId == generation.Id &&
            db.Set<PaperReliabilityCampaignEntry>().Any(c => c.Id == e.CampaignId && c.WorkspaceId == workspace && c.InvariantViolationDetected), ct))
            throw new ReliabilityConflict("PriorInvariantViolationRequiresNewGeneration");
        var c = new PaperReliabilityCampaignEntry { Id = Guid.NewGuid(), WorkspaceId = workspace, CreatedBy = actor, Revision = Guid.NewGuid(), Name = name.Trim(), Notes = notes,
            StartedAt = clock.GetUtcNow(), State = ReliabilityCampaignState.Collecting, PolicyFingerprint = PaperReliabilityPolicy.Fingerprint };
        db.Add(c); await OpenAsync(c, backend, ct); await EventAsync(c, "CampaignStarted", ct);
        if (generation is not null) await EventAsync(c, "InitialGeneration", ct, generation.Id);
        db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), "paper-reliability", "PaperReliabilityCampaignStarted", JsonSerializer.Serialize(new { c.Id })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return c;
    }
    private async Task OpenAsync(PaperReliabilityCampaignEntry c, Guid backend, CancellationToken ct)
    {
        if (await db.Set<PaperReliabilityIntervalEntry>().CountAsync(x => x.CampaignId == c.Id, ct) >= PaperReliabilityPolicy.MaximumIntervals) throw new ReliabilityConflict("IntervalRetentionLimit");
        var order = await OrderAsync(c.WorkspaceId, ct); var now = clock.GetUtcNow();
        db.Add(new PaperReliabilityIntervalEntry { Id = Guid.NewGuid(), CampaignId = c.Id, BackendId = backend, StartedAt = now, EndedAt = now, StartOrder = order, EndOrder = order });
    }
    public async Task RecoverAsync(Guid id, Guid backend, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await db.Set<PaperReliabilityCampaignEntry>().SingleAsync(x => x.Id == id, ct);
        if (c.State != ReliabilityCampaignState.Collecting) return;
        var open = await db.Set<PaperReliabilityIntervalEntry>().Where(x => x.CampaignId == id && !x.Closed).ToArrayAsync(ct);
        if (open.Any(x => x.BackendId == backend)) return;
        var lastClosed = open.Length == 0 ? await db.Set<PaperReliabilityIntervalEntry>().Where(x => x.CampaignId == id && x.Closed)
            .OrderByDescending(x => x.EndedAt).ThenByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct) : null;
        if (lastClosed?.Reason is "complete" or "cancel" or "pause") c.EvidenceGapDetected = true;
        foreach (var interval in open) { interval.Closed = true; interval.Reason = "UncertainShutdown"; c.EvidenceGapDetected = true; }
        await OpenAsync(c, backend, ct); await EventAsync(c, open.Length > 0 ? "UnexpectedShutdownRecovery" : "BackendObservedStart", ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task CheckpointAsync(Guid workspace, Guid backend, PaperReliabilityTelemetry.Snapshot snapshot, bool close, string reason, CancellationToken ct,
        Func<PaperReliabilityTelemetry.Snapshot>? captureWithinWriter = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await db.Set<PaperReliabilityCampaignEntry>().SingleAsync(x => x.Id == snapshot.Campaign && x.WorkspaceId == workspace, ct);
        if (c.State != ReliabilityCampaignState.Collecting) return;
        var interval = await db.Set<PaperReliabilityIntervalEntry>().SingleAsync(x => x.CampaignId == c.Id && x.BackendId == backend && !x.Closed, ct);
        if (captureWithinWriter is not null) snapshot = captureWithinWriter();
        var oldCounters = JsonSerializer.Deserialize<Dictionary<string, long>>(c.CountersJson)!;
        foreach (var key in new[] { "UnexpectedWorkerFaults", "AdaptiveBudgetDeferrals", "CommitConflict", "SessionStop:MonitoringStopped", "SessionStop:RiskPolicyChanged", "SessionStop:PaperGenerationChanged" })
            if (snapshot.Counters.GetValueOrDefault(key) > oldCounters.GetValueOrDefault(key)) await EventAsync(c, key, ct);
        c.CountersJson = JsonSerializer.Serialize(snapshot.Counters); c.TriggersJson = JsonSerializer.Serialize(snapshot.Triggers); c.EvidenceGapDetected |= snapshot.Gap;
        var order = await OrderAsync(workspace, ct);
        var operations = await db.Set<PaperWriterOrderEntry>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id > interval.EndOrder && x.Id <= order).OrderBy(x => x.Id).Take(10001).ToArrayAsync(ct);
        if (operations.Length > 10000) c.EvidenceGapDetected = c.EventRetentionTruncated = true;
        var capacity = PaperReliabilityPolicy.MaximumEvents - await db.Set<PaperReliabilityEventEntry>().CountAsync(e => e.CampaignId == c.Id, ct)
            - db.ChangeTracker.Entries<PaperReliabilityEventEntry>().Count(e => e.State == EntityState.Added && e.Entity.CampaignId == c.Id);
        if (operations.Length > capacity) c.EvidenceGapDetected = c.EventRetentionTruncated = true;
        foreach (var operation in operations.Take(Math.Max(0, capacity)))
            db.Add(new PaperReliabilityEventEntry { Id = Guid.NewGuid(), CampaignId = c.Id, At = operation.At,
                Kind = operation.Kind == "Execution" ? "ExecutionCommitted" : operation.Kind, ReferenceId = operation.ReferenceId, WriterOrder = operation.Id });
        interval.EndedAt = snapshot.At; interval.EndOrder = order; interval.Closed = close; if (close) interval.Reason = reason;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<PaperReliabilityCampaignEntry> TransitionAsync(Guid actor, Guid workspace, Guid id, Guid expected, string action, Guid backend, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await membership.RequireMemberAsync(actor, workspace, true, ct);
        var c = await db.Set<PaperReliabilityCampaignEntry>().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, ct) ?? throw new ArgumentException("CampaignNotFound");
        if (c.Revision != expected) throw new ReliabilityConflict("RevisionConflict");
        if (c.State is ReliabilityCampaignState.Completed or ReliabilityCampaignState.Cancelled) throw new ReliabilityConflict("CampaignFinal");
        if (action == "resume")
        { if (c.State != ReliabilityCampaignState.Paused) throw new ReliabilityConflict("NotPaused"); c.State = ReliabilityCampaignState.Collecting; await OpenAsync(c, backend, ct); }
        else
        {
            if (action == "pause" && c.State != ReliabilityCampaignState.Collecting) throw new ReliabilityConflict("NotCollecting");
            foreach (var interval in await db.Set<PaperReliabilityIntervalEntry>().Where(x => x.CampaignId == id && !x.Closed).ToArrayAsync(ct)) { interval.Closed = true; interval.Reason = action; }
            c.State = action switch { "pause" => ReliabilityCampaignState.Paused, "complete" => ReliabilityCampaignState.Completed, "cancel" => ReliabilityCampaignState.Cancelled, _ => throw new ArgumentException("InvalidAction") };
            if (action == "complete") c.CompletedAt = clock.GetUtcNow(); if (action == "cancel") c.CancelledAt = clock.GetUtcNow();
            if (c.State is ReliabilityCampaignState.Completed or ReliabilityCampaignState.Cancelled && c.LatestEvaluationId is { } evaluationId)
            {
                var evaluation = await db.Set<PaperReliabilityEvaluationEntry>().SingleAsync(x => x.Id == evaluationId, ct);
                var report = JsonSerializer.Deserialize<ReliabilityReport>(evaluation.ReportJson)! with { CampaignState = c.State };
                evaluation.ReportJson = JsonSerializer.Serialize(report with { EvidenceFingerprint = PaperReliabilityPolicy.FingerprintOf(report) });
            }
        }
        c.Revision = Guid.NewGuid(); var suffix = action switch { "pause" => "Paused", "resume" => "Resumed", "complete" => "Completed", _ => "Cancelled" };
        await EventAsync(c, "Campaign" + suffix, ct); db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), "paper-reliability", "PaperReliabilityCampaign" + suffix, JsonSerializer.Serialize(new { c.Id })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return c;
    }
}
