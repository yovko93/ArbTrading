using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class PaperResolutionEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid GenerationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestFingerprint { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ProofFingerprint { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string MarketId { get; set; } = "";
    public PaperResolutionSource Source { get; set; }
    public PaperResolutionStatus Status { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
public sealed class PaperResolutionOutcomeEntry
{
    public Guid ResolutionId { get; set; }
    public string InstrumentId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public decimal PayoutPerShare { get; set; }
}
internal static class SettlementModel
{
    public static void Configure(ModelBuilder m)
    {
        var r = m.Entity<PaperResolutionEntry>(); r.HasKey(x => x.Id);
        r.HasIndex(x => new { x.WorkspaceId, x.RequestId }).IsUnique();
        r.HasIndex(x => new { x.GenerationId, x.Exchange, x.MarketId }).IsUnique();
        r.HasOne<PaperGenerationEntry>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Arbitrage.Domain.Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Arbitrage.Domain.ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        var o = m.Entity<PaperResolutionOutcomeEntry>(); o.HasKey(x => new { x.ResolutionId, x.InstrumentId });
        o.HasOne<PaperResolutionEntry>().WithMany().HasForeignKey(x => x.ResolutionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperPositionEntry>().HasOne<PaperResolutionEntry>().WithMany().HasForeignKey(x => x.SettlementResolutionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperTransactionEntry>().HasOne<PaperResolutionEntry>().WithMany().HasForeignKey(x => x.ResolutionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperTransactionEntry>().HasIndex(x => x.ResolutionId).IsUnique();
        m.Entity<PaperGenerationEntry>().Property(x => x.Revision).IsConcurrencyToken();
        m.Entity<PaperPositionEntry>().Property(x => x.Revision).IsConcurrencyToken();
    }
}
