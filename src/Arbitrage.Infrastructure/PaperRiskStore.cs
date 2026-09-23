using System.Text.Json;
using Arbitrage.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class PaperRiskProfileEntry
{
    public Guid WorkspaceId { get; set; }
    public int PolicyVersion { get; set; }
    public Guid Revision { get; set; }
    public PaperRiskLimits Limits { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public string Fingerprint { get; set; } = "";
    public PaperRiskProfile Profile() => new(PolicyVersion, Revision, Limits, CreatedAt, UpdatedAt, UpdatedBy, Fingerprint);
}
public sealed class PaperRiskRevisionConflict : Exception;
public sealed record PaperRiskSnapshot(PaperRiskProfile? Profile, CurrentPaperRiskState State);

public sealed partial class PaperStore
{
    public async Task<PaperRiskProfile?> RiskProfileAsync(Guid workspace, CancellationToken ct) =>
        (await db.Set<PaperRiskProfileEntry>().AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct))?.Profile();

    public async Task<PaperRiskProfile> SaveRiskProfileAsync(Guid actor, Guid workspace, Guid? expectedRevision,
        PaperRiskLimits limits, bool confirmed, string correlation, CancellationToken ct)
    {
        if (!confirmed || limits is not { IsValid: true }) throw new ArgumentException("Invalid or unconfirmed paper risk policy.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await membership.RequireMemberAsync(actor, workspace, true, ct);
        var row = await db.Set<PaperRiskProfileEntry>().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        if (row?.Revision != expectedRevision) throw new PaperRiskRevisionConflict();
        var now = clock.GetUtcNow(); var create = row is null;
        if (row is null) { row = new() { WorkspaceId = workspace, CreatedAt = now }; db.Add(row); }
        row.PolicyVersion = PaperRiskProfile.Version; row.Revision = Guid.NewGuid(); row.Limits = limits;
        row.UpdatedAt = now; row.UpdatedBy = actor; row.Fingerprint = limits.Fingerprint;
        db.AuditRecords.Add(new(actor, workspace, now, correlation, create ? "PaperRiskPolicyConfigured" : "PaperRiskPolicyUpdated", JsonSerializer.Serialize(row.Profile())));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.Profile();
    }
    // One coherent read snapshot for policy, revision and accounting. Final admission calls RiskStateAsync
    // inside the existing immediate SQLite writer transaction instead of opening a second transaction.
    public async Task<PaperRiskSnapshot> RiskSnapshotAsync(Guid workspace, Guid? generation, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var scope = await db.Database.UseTransactionAsync(tx, ct);
        var g = generation is { } id ? await GenerationAsync(workspace, id, ct) : await ActiveAsync(workspace, ct);
        if (generation.HasValue && g is null) throw new KeyNotFoundException();
        return new(await RiskProfileAsync(workspace, ct), await RiskStateAsync(g, ct));
    }
    private async Task<CurrentPaperRiskState> RiskStateAsync(PaperGenerationEntry? g, CancellationToken ct)
    {
        if (g is null) return new(null, 0, true, [], [], []);
        var balances = await BalancesAsync(g.Id, ct);
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == g.Id && p.Status == PaperPositionStatus.Open).Take(1001).ToArrayAsync(ct);
        var executions = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.GenerationId == g.Id &&
            (e.State == PaperExecutionState.Committed || e.State == PaperExecutionState.PartiallySettled)).Take(1001).ToArrayAsync(ct);
        var healthy = g.Integrity == PaperIntegrity.Healthy && balances.All(b => b.ReservedCash == 0) && positions.Length <= 1000 && executions.Length <= 1000;
        try
        {
            return new(g.Id, g.Revision, healthy, [.. balances.Select(b => new PaperRiskBucket(b.Exchange, b.Currency, b.InitialCash, b.AvailableCash, b.Revision))],
                [.. positions.Select(p => new PaperRiskPosition(p.Exchange, p.Currency, p.MarketId, p.InstrumentId, p.Outcome, p.CostBasis))],
                [.. executions.Select(e => new PaperRiskOpenExecution(JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!.Proof.RelationshipId))]);
        }
        catch (Exception e) when (e is JsonException or NullReferenceException)
        { return new(g.Id, g.Revision, false, [], [], []); }
    }
    internal static bool RiskProofHealthy(PaperExecutionEntry e, PaperPlan plan)
    {
        if (e.RiskProofJson is null) return e.RiskPolicyVersion is null && e.RiskPolicyRevision is null;
        var p = JsonSerializer.Deserialize<PaperRiskProof>(e.RiskProofJson);
        return p is not null && p.Decision.PolicyVersion == PaperRiskProfile.Version && p.Decision.PolicyVersion == e.RiskPolicyVersion &&
            p.Decision.PolicyRevision == e.RiskPolicyRevision && e.RiskPolicyRevision is { } revision && revision != Guid.Empty &&
            p.Decision.PolicyFingerprint is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit) &&
            p.Decision.Decision == PaperRiskOutcome.Approved && p.Decision.Violations.IsEmpty && p.Decision.GenerationId == e.GenerationId &&
            p.PlanId == plan.Id && p.Quantity == plan.Quantity && p.Cost == plan.Cost &&
            p.Decision.BucketAssessments.All(b => b.ProposedDebit == plan.Debits.Where(d => d.Exchange == b.Exchange && d.Currency == b.Currency).Sum(d => d.Total)) &&
            plan.Debits.All(d => p.Decision.BucketAssessments.Any(b => b.Exchange == d.Exchange && b.Currency == d.Currency && b.ProposedDebit == d.Total));
    }
}
