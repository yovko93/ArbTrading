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

    protected override void ConfigureConventions(ModelConfigurationBuilder builder) =>
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();

    protected override void OnModelCreating(ModelBuilder model)
    {
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
    }
}

// INTEGER UTC ticks preserve exact instants and support SQLite ordering/comparisons.
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
