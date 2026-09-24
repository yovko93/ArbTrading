using System.Text.Json;
using System.Collections.Immutable;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Arbitrage.Infrastructure;

public sealed class PaperAutomationProfileEntry
{
    public Guid WorkspaceId { get; set; }
    public string ProfileJson { get; set; } = "";
}
public sealed class PaperAutomationControlEntry
{
    public Guid WorkspaceId { get; set; }
    public Guid Revision { get; set; }
    public bool IsLatched { get; set; }
    public DateTimeOffset? LatchedAt { get; set; }
    public Guid? LatchedBy { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? ResetAt { get; set; }
    public Guid? ResetBy { get; set; }
}
public sealed class PaperAutomationException(PaperAutomationReason reason) : Exception(reason.ToString())
{ public PaperAutomationReason Reason { get; } = reason; }
public sealed record PaperAutomaticCommit(PaperAutomationPermit Permit, string TriggerStamp, PaperSizingProof? Sizing = null);
public sealed record PaperSizingSnapshot(PaperRiskSnapshot Risk, PaperAutomationHistory History);

public sealed partial class PaperStore
{
    public async Task<PaperSizingSnapshot> SizingSnapshotAsync(PaperAutomationPermit permit, Guid relationship, string key, string stamp, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var scope = await db.Database.UseTransactionAsync(tx, ct);
        await membership.RequireMemberAsync(permit.ActorId, permit.WorkspaceId, false, ct);
        var kill = await AutomationControlAsync(permit.WorkspaceId, ct);
        if (kill?.IsLatched == true || kill?.Revision != permit.KillRevision) throw new PaperAutomationException(PaperAutomationReason.KillSwitchLatched);
        var generation = await ActiveAsync(permit.WorkspaceId, ct);
        if (generation?.Id != permit.GenerationId) throw new PaperAutomationException(PaperAutomationReason.PaperGenerationChanged);
        var profile = await AutomationProfileAsync(permit.WorkspaceId, ct); var risk = await RiskProfileAsync(permit.WorkspaceId, ct);
        if (risk?.Revision != permit.RiskRevision) throw new PaperAutomationException(PaperAutomationReason.RiskPolicyChanged);
        if (profile?.Revision != permit.Profile.Revision || profile?.Valid(risk) != true) throw new PaperAutomationException(PaperAutomationReason.AutomationPolicyChanged);
        return new(new(risk, await RiskStateAsync(generation, ct)), await AutomationHistoryAsync(permit, relationship, key, stamp, ct));
    }
    public async Task<PaperAutomationProfile?> AutomationProfileAsync(Guid workspace, CancellationToken ct)
    {
        var row = await db.Set<PaperAutomationProfileEntry>().AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        try { return row is null ? null : JsonSerializer.Deserialize<PaperAutomationProfile>(row.ProfileJson) ?? throw new PaperAutomationException(PaperAutomationReason.IntegrityFailure); }
        catch (JsonException) { throw new PaperAutomationException(PaperAutomationReason.IntegrityFailure); }
    }
    public Task<PaperAutomationControlEntry?> AutomationControlAsync(Guid workspace, CancellationToken ct) =>
        db.Set<PaperAutomationControlEntry>().AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
    public async Task<PaperAutomationProfile> SaveAutomationProfileAsync(Guid actor, Guid workspace, Guid? expected, bool confirmed,
        PaperAutomationSettings settings, CancellationToken ct)
    {
        if (!confirmed) throw new PaperAutomationException(PaperAutomationReason.ConfirmationRequired);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await membership.RequireMemberAsync(actor, workspace, true, ct);
        var risk = await RiskProfileAsync(workspace, ct);
        if (risk is null) throw new PaperAutomationException(PaperAutomationReason.RiskNotConfigured);
        if (!settings.Valid(risk)) throw new PaperAutomationException(PaperAutomationReason.InvalidProfile);
        var previous = await AutomationProfileAsync(workspace, ct);
        if (previous?.Revision != expected) throw new PaperAutomationException(PaperAutomationReason.RevisionConflict);
        var now = clock.GetUtcNow(); var profile = new PaperAutomationProfile(settings.SizingMode == PaperSizingMode.FixedQuantity ? 1 : PaperAutomationProfile.Version,
            Guid.NewGuid(), settings, previous?.CreatedAt ?? now, now, actor, PaperAutomationPolicy.Hash(settings));
        var row = await db.Set<PaperAutomationProfileEntry>().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        if (row is null) { row = new() { WorkspaceId = workspace }; db.Add(row); }
        row.ProfileJson = JsonSerializer.Serialize(profile);
        AutomationAudit(actor, workspace, previous is null ? "PaperAutomationProfileConfigured" : "PaperAutomationProfileUpdated", profile);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); reliability?.SetState(workspace, healthy: false); return profile;
    }
    public async Task<PaperAutomationControlEntry> SetAutomationKillAsync(Guid actor, Guid workspace, bool latch, Guid? expected,
        bool confirmed, string reason, CancellationToken ct)
    {
        if (!latch && !confirmed) throw new PaperAutomationException(PaperAutomationReason.ConfirmationRequired);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("A bounded reason is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await membership.RequireMemberAsync(actor, workspace, true, ct);
        var row = await db.Set<PaperAutomationControlEntry>().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        if (!latch && row?.Revision != expected) throw new PaperAutomationException(PaperAutomationReason.RevisionConflict);
        if (row is null) { row = new() { WorkspaceId = workspace }; db.Add(row); }
        row.Revision = Guid.NewGuid(); row.IsLatched = latch; row.Reason = reason;
        if (latch) { row.LatchedAt = clock.GetUtcNow(); row.LatchedBy = actor; }
        else { row.ResetAt = clock.GetUtcNow(); row.ResetBy = actor; }
        AutomationAudit(actor, workspace, latch ? "PaperAutomationEmergencyStopped" : "PaperAutomationKillSwitchReset", row);
        db.Add(new PaperWriterOrderEntry { WorkspaceId = workspace, At = clock.GetUtcNow(), Kind = latch ? "KillSwitchLatched" : "KillSwitchReset", ReferenceId = row.Revision });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row;
    }
    public async Task<PaperAutomationPermit> ArmAutomationAsync(Guid actor, Guid workspace, Guid expectedProfile, Guid expectedRisk,
        Guid expectedGeneration, Guid? expectedKill, bool confirmed, Func<PaperAutomationPermit, Action, bool> activate, CancellationToken ct)
    {
        if (!confirmed) throw new PaperAutomationException(PaperAutomationReason.ConfirmationRequired);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await membership.RequireMemberAsync(actor, workspace, true, ct);
        var profile = await AutomationProfileAsync(workspace, ct) ?? throw new PaperAutomationException(PaperAutomationReason.NotConfigured);
        var risk = await RiskProfileAsync(workspace, ct) ?? throw new PaperAutomationException(PaperAutomationReason.RiskNotConfigured);
        var g = await ActiveAsync(workspace, ct) ?? throw new PaperAutomationException(PaperAutomationReason.GenerationUnavailable);
        var kill = await AutomationControlAsync(workspace, ct);
        if (kill?.IsLatched == true) throw new PaperAutomationException(PaperAutomationReason.KillSwitchLatched);
        if (profile.Revision != expectedProfile || risk.Revision != expectedRisk || g.Id != expectedGeneration || kill?.Revision != expectedKill)
            throw new PaperAutomationException(PaperAutomationReason.RevisionConflict);
        if (!profile.Valid(risk)) throw new PaperAutomationException(PaperAutomationReason.InvalidProfile);
        var assessment = PaperRiskEvaluator.Evaluate(risk, await RiskStateAsync(g, ct), null, clock.GetUtcNow());
        if (assessment.Decision != PaperRiskOutcome.Approved) throw new PaperAutomationException(assessment.Decision == PaperRiskOutcome.InvalidFinancialState ? PaperAutomationReason.IntegrityFailure : PaperAutomationReason.BlockedByRisk);
        if (await AutomationHourlyCountAsync(workspace, ct) >= profile.Settings.MaximumExecutionsPerHour) throw new PaperAutomationException(PaperAutomationReason.HourlyExecutionLimitReached);
        var permit = new PaperAutomationPermit(Guid.NewGuid(), workspace, actor, g.Id, profile, risk.Revision, risk.Fingerprint, kill?.Revision, clock.GetUtcNow());
        AutomationAudit(actor, workspace, "PaperAutomationArmed", permit);
        db.Add(new PaperWriterOrderEntry { WorkspaceId = workspace, At = clock.GetUtcNow(), Kind = "AutomationArmed", ReferenceId = permit.SessionId,
            SessionId = permit.SessionId, ProofJson = JsonSerializer.Serialize(new ReliabilityArmProof(permit, risk, reliability?.BackendId ?? Guid.Empty)) });
        await db.SaveChangesAsync(ct);
        if (!activate(permit, () => { ct.ThrowIfCancellationRequested(); tx.Commit(); })) throw new PaperAutomationException(PaperAutomationReason.MonitoringStopped);
        return permit;
    }
    public async Task AutomationDisarmedAuditAsync(PaperAutomationPermit permit, Guid actor, PaperAutomationReason reason, CancellationToken ct)
    {
        AutomationAudit(actor, permit.WorkspaceId, "PaperAutomationDisarmed", new { permit.SessionId, Reason = reason.ToString() });
        db.Add(new PaperWriterOrderEntry { WorkspaceId = permit.WorkspaceId, At = clock.GetUtcNow(), Kind = "AutomationDisarmed", ReferenceId = permit.SessionId, SessionId = permit.SessionId, ProofJson = reason.ToString() });
        await db.SaveChangesAsync(ct);
    }
    private void AutomationAudit(Guid actor, Guid workspace, string action, object value) =>
        db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), "automatic-paper", action, JsonSerializer.Serialize(value)));
    public Task<int> AutomationHourlyCountAsync(Guid workspace, CancellationToken ct)
    {
        var start = clock.GetUtcNow().AddHours(-1);
        return db.Set<PaperExecutionEntry>().CountAsync(e => e.WorkspaceId == workspace && e.Origin == PaperExecutionOrigin.AutomaticPaper && e.CreatedAt > start, ct);
    }
    public async Task<PaperAutomationHistory> AutomationHistoryAsync(PaperAutomationPermit permit, Guid relationship, string key, string stamp, CancellationToken ct)
    {
        var query = db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.WorkspaceId == permit.WorkspaceId && e.Origin == PaperExecutionOrigin.AutomaticPaper);
        var session = await query.Where(e => e.AutomationSessionId == permit.SessionId).Take(1001).ToArrayAsync(ct);
        if (session.Length > 1000) throw new PaperAutomationException(PaperAutomationReason.IntegrityFailure);
        var opportunity = await query.Where(e => e.OpportunityKey == key).OrderByDescending(e => e.CreatedAt).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(ct);
        var related = await query.Where(e => e.AutomationRelationshipId == relationship).OrderByDescending(e => e.CreatedAt).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(ct);
        var debits = session.SelectMany(e => JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Debits).GroupBy(d => (d.Exchange, d.Currency))
            .Select(g => new PaperDebit(g.Key.Exchange, g.Key.Currency, g.Sum(d => d.Notional), g.Sum(d => d.Fees))).ToImmutableArray();
        return new(session.Length, await AutomationHourlyCountAsync(permit.WorkspaceId, ct), session.Count(e => e.AutomationRelationshipId == relationship),
            opportunity, related, session.Any(e => e.AutomationInputStamp == stamp), debits);
    }
    private async Task<PaperAutomationReason> AutomationAdmissionAsync(PaperAutomaticCommit automatic, PaperPlan plan, PaperGenerationEntry generation, CancellationToken ct)
    {
        var p = automatic.Permit;
        var kill = await AutomationControlAsync(p.WorkspaceId, ct);
        if (kill?.IsLatched == true || kill?.Revision != p.KillRevision) return PaperAutomationReason.KillSwitchLatched;
        if (generation.Id != p.GenerationId || generation.WorkspaceId != p.WorkspaceId) return PaperAutomationReason.PaperGenerationChanged;
        var profile = await AutomationProfileAsync(p.WorkspaceId, ct); var risk = await RiskProfileAsync(p.WorkspaceId, ct);
        if (risk?.Revision != p.RiskRevision || risk.Fingerprint != p.RiskFingerprint) return PaperAutomationReason.RiskPolicyChanged;
        if (profile?.Revision != p.Profile.Revision || profile.PolicyFingerprint != p.Profile.PolicyFingerprint) return PaperAutomationReason.AutomationPolicyChanged;
        if (!profile.Valid(risk)) return PaperAutomationReason.IntegrityFailure;
        if (PaperAutomationPolicy.Stamp(plan.Proof, profile) != automatic.TriggerStamp) return PaperAutomationReason.CommitConflict;
        if (profile.Settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity && (automatic.Sizing is not { } sizing ||
            !PaperSizer.Healthy(sizing, profile.Settings, plan, automatic.TriggerStamp, profile.Revision, p.RiskRevision, p.GenerationId, sizing.FinancialRevision)))
            return PaperAutomationReason.IntegrityFailure;
        return PaperAutomationPolicy.Evaluate(profile.Settings, plan, await AutomationHistoryAsync(p, plan.Proof.RelationshipId, plan.Proof.OpportunityKey, automatic.TriggerStamp, ct),
            (await RiskStateAsync(generation, ct)).Buckets, clock.GetUtcNow());
    }
    internal static bool AutomationProofHealthy(PaperExecutionEntry e, PaperPlan plan)
    {
        if (e.Origin == PaperExecutionOrigin.Manual) return e.AutomationProofJson is null && e.AutomationSessionId is null && e.AutomationInputStamp is null && e.AutomationRelationshipId is null;
        if (e.Origin != PaperExecutionOrigin.AutomaticPaper || e.RiskProofJson is null || e.AutomationProofJson is null) return false;
        var p = JsonSerializer.Deserialize<PaperAutomationProof>(e.AutomationProofJson);
        return p is not null && (p.ProfileVersion == 1 && p.Settings?.SizingMode == PaperSizingMode.FixedQuantity || p.ProfileVersion == PaperAutomationProfile.Version) && p.ProfileRevision != Guid.Empty && p.SessionId != Guid.Empty &&
            p.SessionId == e.AutomationSessionId && p.RelationshipId == e.AutomationRelationshipId && p.RelationshipId == plan.Proof.RelationshipId &&
            p.TriggerInputStamp == e.AutomationInputStamp && p.TriggerInputStamp is { Length: 64 } && p.TriggerInputStamp.All(char.IsAsciiHexDigit) &&
            p.ProfileFingerprint is { Length: 64 } && p.ProfileFingerprint.All(char.IsAsciiHexDigit) && p.Settings is not null &&
            (p.Settings.SizingMode == PaperSizingMode.FixedQuantity ? p.Settings.FixedQuantity == plan.Quantity && p.Sizing is null :
                p.Sizing is { } sizing && PaperSizer.Healthy(sizing, p.Settings, plan, p.TriggerInputStamp, p.ProfileRevision, e.RiskPolicyRevision!.Value,
                    e.GenerationId, JsonSerializer.Deserialize<PaperRiskProof>(e.RiskProofJson)!.Decision.FinancialRevision)) &&
            p.ProfileFingerprint == PaperAutomationPolicy.Hash(p.Settings) && p.TriggeredAt == e.CreatedAt &&
            p.TriggerInputStamp == PaperAutomationPolicy.Stamp(plan.Proof, new(p.ProfileVersion, p.ProfileRevision, p.Settings, e.CreatedAt, e.CreatedAt, e.ActorId, p.ProfileFingerprint)) &&
            e.RequestId == PaperAutomationPolicy.RequestId(p.SessionId, e.OpportunityKey, p.TriggerInputStamp, plan.Quantity, p.Sizing);
    }
}
