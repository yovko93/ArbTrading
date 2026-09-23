using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class MonitoringProfileEntry
{
    public Guid WorkspaceId { get; set; }
    public string SettingsJson { get; set; } = "";
    public Guid Revision { get; set; }
}
public sealed class MonitoringAlertEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public string EventJson { get; set; } = "";
}
public sealed class MonitoringStore(TradingDbContext db, RelationshipStore members, TimeProvider clock)
{
    public async Task<MonitoringProfile> ProfileAsync(Guid actor, Guid workspace, CancellationToken ct)
    {
        await members.RequireMemberAsync(actor, workspace, false, ct);
        var row = await db.MonitoringProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace, ct);
        var profile = row is null ? new() : JsonSerializer.Deserialize<MonitoringProfile>(row.SettingsJson) ?? throw new InvalidOperationException("InvalidMonitoringProfile");
        return profile.Valid ? profile : throw new InvalidOperationException("InvalidMonitoringProfile");
    }
    public async Task SaveProfileAsync(Guid actor, Guid workspace, MonitoringProfile profile, CancellationToken ct)
    {
        if (!profile.Valid) throw new ArgumentException("Invalid monitoring settings.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await members.RequireMemberAsync(actor, workspace, true, ct);
        var row = await db.MonitoringProfiles.SingleOrDefaultAsync(x => x.WorkspaceId == workspace, ct);
        if (row is null) { row = new() { WorkspaceId = workspace }; db.MonitoringProfiles.Add(row); }
        row.SettingsJson = JsonSerializer.Serialize(profile); row.Revision = Guid.NewGuid();
        db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), row.Revision.ToString(), "MonitoringProfileUpdated", row.SettingsJson));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task AuditAsync(Guid actor, Guid workspace, string action, CancellationToken ct)
    {
        if (action is not ("MonitoringStarted" or "MonitoringStopped")) throw new ArgumentException("Unsupported monitoring audit action.");
        await members.RequireMemberAsync(actor, workspace, true, ct);
        db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), Guid.NewGuid().ToString(), action)); await db.SaveChangesAsync(ct);
    }
    public async Task AddAlertAsync(Guid actor, MonitoringAlert alert, MonitoringProfile profile, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await members.RequireMemberAsync(actor, alert.WorkspaceId, false, ct);
        db.MonitoringAlerts.Add(new() { Id = alert.AlertId, WorkspaceId = alert.WorkspaceId, TriggeredAt = alert.TriggeredAt, EventJson = JsonSerializer.Serialize(alert) });
        await db.SaveChangesAsync(ct);
        await PruneCoreAsync(alert.WorkspaceId, profile, ct); await tx.CommitAsync(ct);
    }
    private async Task PruneCoreAsync(Guid workspace, MonitoringProfile profile, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-profile.AlertRetentionDays);
        await db.MonitoringAlerts.Where(a => a.WorkspaceId == workspace && a.TriggeredAt < cutoff).ExecuteDeleteAsync(ct);
        var excess = await db.MonitoringAlerts.Where(a => a.WorkspaceId == workspace).OrderByDescending(a => a.TriggeredAt).ThenByDescending(a => a.Id)
            .Skip(profile.AlertRetentionCount).Select(a => a.Id).ToArrayAsync(ct);
        // At most 5,001 normal retained records; pruning never touches the audit table.
        foreach (var batch in excess.Chunk(100)) await db.MonitoringAlerts.Where(a => a.WorkspaceId == workspace && batch.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }
    public async Task<(MonitoringAlert[] Items, int Total)> AlertsAsync(Guid actor, Guid workspace, int page, int size, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || size is < 1 or > 100) throw new ArgumentException("Invalid alert page.");
        var profile = await ProfileAsync(actor, workspace, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await PruneCoreAsync(workspace, profile, ct);
        var query = db.MonitoringAlerts.AsNoTracking().Where(x => x.WorkspaceId == workspace);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.TriggeredAt).ThenByDescending(x => x.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return (rows.Select(r => JsonSerializer.Deserialize<MonitoringAlert>(r.EventJson)!).ToArray(), total);
    }
    public async Task<(int Available, int Skipped)> CoverageAsync(Guid workspace, int monitored, bool manual, CancellationToken ct)
    {
        var count = await db.MarketRelationships.CountAsync(r => r.WorkspaceId == workspace &&
            (r.State == VerificationState.VerifiedDeterministic || manual && r.State == VerificationState.VerifiedManual), ct);
        return (count, Math.Max(0, count - monitored));
    }
}
