using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class PaperGenerationEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string Reason { get; set; } = "";
    public PaperIntegrity Integrity { get; set; }
    public long Revision { get; set; }
}
public sealed class PaperBalanceEntry
{
    public Guid GenerationId { get; set; }
    public string Exchange { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal InitialCash { get; set; }
    public decimal AvailableCash { get; set; }
    public decimal ReservedCash { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class PaperExecutionEntry
{
    public PaperExecutionOrigin Origin { get; set; }
    public Guid? AutomationSessionId { get; set; }
    public Guid? AutomationRelationshipId { get; set; }
    public string? AutomationInputStamp { get; set; }
    public string? AutomationProofJson { get; set; }
    public int? RiskPolicyVersion { get; set; }
    public Guid? RiskPolicyRevision { get; set; }
    public string? RiskProofJson { get; set; }
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid GenerationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestFingerprint { get; set; } = "";
    public string OpportunityKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string PlanJson { get; set; } = "";
    public PaperExecutionState State { get; set; }
    public string SettlementMarketsJson { get; set; } = "[]";
    public string? SettlementJson { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
}
public sealed class PaperLegEntry
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public string Exchange { get; set; } = "";
    public string MarketId { get; set; } = "";
    public string InstrumentId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Notional { get; set; }
    public decimal Fees { get; set; }
    public string Currency { get; set; } = "";
}
public sealed class PaperFillEntry
{
    public Guid Id { get; set; }
    public Guid LegId { get; set; }
    public string FillJson { get; set; } = "";
}
public sealed class PaperTransactionEntry
{
    public Guid Id { get; set; }
    public Guid GenerationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? ResolutionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class PaperLedgerEntry
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public string Exchange { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Reason { get; set; } = "";
    public decimal AvailableDelta { get; set; }
    public decimal ReservedDelta { get; set; }
}
public sealed class PaperPositionEntry
{
    public PaperPositionStatus Status { get; set; }
    public long Revision { get; set; }
    public Guid? SettlementResolutionId { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public decimal? SettlementPayout { get; set; }
    public decimal? RealizedPnl { get; set; }
    public Guid Id { get; set; }
    public Guid GenerationId { get; set; }
    public string Exchange { get; set; } = "";
    public string MarketId { get; set; } = "";
    public string InstrumentId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal CostBasis { get; set; }
    public decimal Fees { get; set; }
    public decimal AverageEntry { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
internal static class PaperModel
{
    public static void Configure(ModelBuilder m)
    {
        m.Entity<PaperAutomationProfileEntry>().HasKey(x => x.WorkspaceId);
        m.Entity<PaperAutomationProfileEntry>().HasOne<Arbitrage.Domain.Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperAutomationControlEntry>().HasKey(x => x.WorkspaceId);
        m.Entity<PaperAutomationControlEntry>().HasOne<Arbitrage.Domain.Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.WorkspaceId, x.Origin, x.CreatedAt });
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.WorkspaceId, x.AutomationRelationshipId, x.CreatedAt });
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.WorkspaceId, x.AutomationSessionId, x.AutomationInputStamp }).IsUnique();
        var risk = m.Entity<PaperRiskProfileEntry>();
        risk.ToTable("PaperRiskProfiles");
        risk.HasKey(x => x.WorkspaceId);
        risk.Property(x => x.Revision).IsConcurrencyToken();
        risk.HasOne<Arbitrage.Domain.Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        risk.HasOne<Arbitrage.Domain.ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        risk.OwnsOne(x => x.Limits, owned => { owned.Ignore(x => x.IsValid); owned.Ignore(x => x.Fingerprint); });
        risk.Navigation(x => x.Limits).IsRequired();
        m.Entity<PaperPositionEntry>().HasIndex(x => new { x.GenerationId, x.Status });
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.GenerationId, x.State });
        SettlementModel.Configure(m);
        m.Entity<PaperGenerationEntry>().HasKey(x => x.Id);
        m.Entity<PaperGenerationEntry>().HasIndex(x => x.WorkspaceId).IsUnique().HasFilter("\"ClosedAt\" IS NULL");
        m.Entity<PaperGenerationEntry>().HasOne<Arbitrage.Domain.Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperGenerationEntry>().HasOne<Arbitrage.Domain.ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperBalanceEntry>().HasKey(x => new { x.GenerationId, x.Exchange, x.Currency });
        m.Entity<PaperBalanceEntry>().Property(x => x.Revision).IsConcurrencyToken();
        m.Entity<PaperBalanceEntry>().HasOne<PaperGenerationEntry>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperExecutionEntry>().HasKey(x => x.Id);
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.WorkspaceId, x.RequestId }).IsUnique();
        m.Entity<PaperExecutionEntry>().HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        m.Entity<PaperExecutionEntry>().HasOne<PaperGenerationEntry>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperExecutionEntry>().HasOne<Arbitrage.Domain.ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperLegEntry>().HasKey(x => x.Id);
        m.Entity<PaperLegEntry>().HasOne<PaperExecutionEntry>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperFillEntry>().HasKey(x => x.Id);
        m.Entity<PaperFillEntry>().HasOne<PaperLegEntry>().WithMany().HasForeignKey(x => x.LegId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperTransactionEntry>().HasKey(x => x.Id);
        m.Entity<PaperTransactionEntry>().HasOne<PaperGenerationEntry>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperTransactionEntry>().HasOne<PaperExecutionEntry>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperLedgerEntry>().HasKey(x => x.Id);
        m.Entity<PaperLedgerEntry>().HasOne<PaperTransactionEntry>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperPositionEntry>().HasKey(x => x.Id);
        m.Entity<PaperPositionEntry>().HasOne<PaperGenerationEntry>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PaperPositionEntry>().HasIndex(x => new { x.GenerationId, x.Exchange, x.MarketId, x.InstrumentId, x.Outcome }).IsUnique();
    }
}
