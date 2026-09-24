using Arbitrage.Domain;
using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class PaperReliabilityCampaignEntry
{
    public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public Guid CreatedBy { get; set; } public Guid Revision { get; set; }
    public string Name { get; set; } = ""; public string Notes { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public DateTimeOffset? CancelledAt { get; set; }
    public ReliabilityCampaignState State { get; set; } public int PolicyVersion { get; set; } = 1; public string PolicyFingerprint { get; set; } = "";
    public bool EvidenceGapDetected { get; set; } public bool InvariantViolationDetected { get; set; } public bool EventRetentionTruncated { get; set; }
    public string CountersJson { get; set; } = "{}"; public string TriggersJson { get; set; } = "[]"; public Guid? LatestEvaluationId { get; set; }
}
public sealed class PaperReliabilityIntervalEntry
{
    public Guid Id { get; set; } public Guid CampaignId { get; set; } public Guid BackendId { get; set; }
    public DateTimeOffset StartedAt { get; set; } public DateTimeOffset EndedAt { get; set; } public bool Closed { get; set; }
    public long StartOrder { get; set; } public long EndOrder { get; set; } public string Reason { get; set; } = "Collecting";
}
public sealed class PaperReliabilityEventEntry
{
    public Guid Id { get; set; } public Guid CampaignId { get; set; } public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = ""; public Guid? ReferenceId { get; set; } public long? WriterOrder { get; set; }
}
public sealed class PaperReliabilityEvaluationEntry
{
    public Guid Id { get; set; } public Guid CampaignId { get; set; } public DateTimeOffset At { get; set; } public string ReportJson { get; set; } = "";
}
// Minimal immutable ordering anchors share the authoritative writer reservation; bulk campaign telemetry never does.
public sealed class PaperWriterOrderEntry
{
    public long Id { get; set; } public Guid WorkspaceId { get; set; } public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = ""; public Guid ReferenceId { get; set; } public Guid? SessionId { get; set; }
    public string? ProofJson { get; set; }
}
internal static class PaperReliabilityModel
{
    public static void Configure(ModelBuilder m)
    {
        var c = m.Entity<PaperReliabilityCampaignEntry>(); c.HasKey(x => x.Id); c.HasIndex(x => new { x.WorkspaceId, x.StartedAt });
        c.HasIndex(x => new { x.WorkspaceId, x.State }).IsUnique().HasFilter("State = 0");
        c.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        c.Property(x => x.Name).HasMaxLength(100); c.Property(x => x.Notes).HasMaxLength(1000);
        m.Entity<PaperReliabilityIntervalEntry>().HasKey(x => x.Id);
        m.Entity<PaperReliabilityIntervalEntry>().HasIndex(x => new { x.CampaignId, x.StartedAt });
        m.Entity<PaperReliabilityIntervalEntry>().HasOne<PaperReliabilityCampaignEntry>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperReliabilityEventEntry>().HasKey(x => x.Id);
        m.Entity<PaperReliabilityEventEntry>().HasIndex(x => new { x.CampaignId, x.At, x.Id });
        m.Entity<PaperReliabilityEventEntry>().HasIndex(x => new { x.CampaignId, x.WriterOrder }).IsUnique();
        m.Entity<PaperReliabilityEventEntry>().HasOne<PaperReliabilityCampaignEntry>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperReliabilityEvaluationEntry>().HasKey(x => x.Id);
        m.Entity<PaperReliabilityEvaluationEntry>().HasIndex(x => new { x.CampaignId, x.At });
        m.Entity<PaperReliabilityEvaluationEntry>().HasOne<PaperReliabilityCampaignEntry>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperWriterOrderEntry>().HasKey(x => x.Id);
        m.Entity<PaperWriterOrderEntry>().Property(x => x.Id).ValueGeneratedOnAdd();
        m.Entity<PaperWriterOrderEntry>().HasIndex(x => new { x.WorkspaceId, x.Id });
        m.Entity<PaperWriterOrderEntry>().HasIndex(x => new { x.WorkspaceId, x.Kind, x.ReferenceId });
        m.Entity<PaperWriterOrderEntry>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
