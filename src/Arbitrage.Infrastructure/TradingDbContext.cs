using Arbitrage.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Arbitrage.Infrastructure;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> Memberships => Set<WorkspaceMembership>();
    public DbSet<LocalProfile> LocalProfiles => Set<LocalProfile>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<MarketCatalogEntry> CatalogMarkets => Set<MarketCatalogEntry>();
    public DbSet<DiscoveryRunEntry> DiscoveryRuns => Set<DiscoveryRunEntry>();
    public DbSet<MarketCatalogTag> CatalogTags => Set<MarketCatalogTag>();
    public DbSet<MarketRelationshipEntry> MarketRelationships => Set<MarketRelationshipEntry>();
    public DbSet<RelationshipEvidenceEntry> RelationshipEvidence => Set<RelationshipEvidenceEntry>();
    public DbSet<RelationshipOutcomeMappingEntry> RelationshipOutcomeMappings => Set<RelationshipOutcomeMappingEntry>();
    public DbSet<RelationshipJobEntry> RelationshipJobs => Set<RelationshipJobEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder) =>
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var relationship = model.Entity<MarketRelationshipEntry>();
        relationship.ToTable("MarketRelationships");
        relationship.HasKey(r => r.Id);
        relationship.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        relationship.HasIndex(r => new { r.WorkspaceId, r.SourceExchange, r.SourceId, r.TargetExchange, r.TargetId }).IsUnique();
        relationship.HasIndex(r => new { r.WorkspaceId, r.TargetExchange, r.TargetId });
        relationship.HasIndex(r => new { r.WorkspaceId, r.Type });
        relationship.HasIndex(r => new { r.WorkspaceId, r.State });
        relationship.HasMany(r => r.Evidence).WithOne().HasForeignKey(r => r.RelationshipId).OnDelete(DeleteBehavior.Cascade);
        relationship.HasMany(r => r.Mappings).WithOne().HasForeignKey(r => r.RelationshipId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RelationshipEvidenceEntry>().ToTable("RelationshipEvidence").HasKey(e => e.Id);
        model.Entity<RelationshipOutcomeMappingEntry>().ToTable("RelationshipOutcomeMappings").HasKey(e => e.Id);
        model.Entity<RelationshipJobEntry>().HasKey(j => j.Id);
        model.Entity<RelationshipJobEntry>().HasIndex(j => new { j.WorkspaceId, j.StartedAt });
        model.Entity<RelationshipJobEntry>().HasOne<Workspace>().WithMany().HasForeignKey(j => j.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ApplicationUser>().HasKey(x => x.Id);
        model.Entity<Workspace>().HasKey(x => x.Id);
        model.Entity<Workspace>().Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        model.Entity<Workspace>().ToTable(t => t.HasCheckConstraint("CK_Workspace_Name", "length(trim(DisplayName)) BETWEEN 1 AND 100"));
        var membership = model.Entity<WorkspaceMembership>();
        membership.HasKey(x => new { x.UserId, x.WorkspaceId });
        membership.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        membership.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        membership.ToTable(t => t.HasCheckConstraint("CK_Membership_Role", "Role = 0"));
        var profile = model.Entity<LocalProfile>();
        profile.HasKey(x => x.Id);
        profile.HasIndex(x => x.Slot).IsUnique();
        profile.ToTable(t => t.HasCheckConstraint("CK_Profile_Slot", "Slot = 1"));
        profile.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(x => new { x.UserId, x.DefaultWorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<AuditRecord>();
        audit.HasKey(x => x.Id);
        audit.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        audit.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
        audit.Property(x => x.Action).HasMaxLength(80);
        audit.Property(x => x.CorrelationId).HasMaxLength(128);
        var market = model.Entity<MarketCatalogEntry>();
        market.HasKey(x => new { x.Exchange, x.NativeId });
        market.Property(x => x.Exchange).HasMaxLength(24);
        market.Property(x => x.NativeId).HasMaxLength(256);
        market.HasIndex(x => new { x.Exchange, x.Status, x.Title, x.NativeId });
        market.HasIndex(x => new { x.Exchange, x.PrimaryTag, x.NativeId });
        market.HasIndex(x => x.RetrievedAt);
        var tag = model.Entity<MarketCatalogTag>();
        tag.HasKey(x => new { x.Exchange, x.NativeId, x.Tag });
        tag.Property(x => x.Exchange).HasMaxLength(24);
        tag.Property(x => x.NativeId).HasMaxLength(256);
        tag.Property(x => x.Tag).HasMaxLength(100);
        tag.HasIndex(x => x.Tag);
        tag.HasOne<MarketCatalogEntry>().WithMany().HasForeignKey(x => new { x.Exchange, x.NativeId })
            .OnDelete(DeleteBehavior.Cascade);
        var run = model.Entity<DiscoveryRunEntry>();
        run.HasKey(x => x.Id);
        run.Property(x => x.Exchange).HasMaxLength(24);
        run.Property(x => x.State).HasMaxLength(24);
        run.HasIndex(x => new { x.Exchange, x.StartedAt });
        run.HasIndex(x => new { x.Exchange, x.State });
    }
}

// INTEGER UTC ticks preserve exact instants and support SQLite ordering/comparisons.
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
