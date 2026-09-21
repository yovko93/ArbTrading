using Arbitrage.Application;
using Arbitrage.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Arbitrage.Infrastructure;

public static class DatabaseOptions
{
    public static DbContextOptions<TradingDbContext> ForFile(string path) => new DbContextOptionsBuilder<TradingDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString()).Options;
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args) => new(DatabaseOptions.ForFile(
        Path.Combine(Path.GetTempPath(), "ArbitrageTrading-schema-design.db")));
}

public sealed class DatabaseInitializer(TradingDbContext db, TimeProvider clock)
{
    public async Task InitializeAsync(bool existingDatabase, bool applyMigrations, CancellationToken cancellationToken)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (applied.Any(migration => !known.Contains(migration)))
            throw new InvalidOperationException("Database contains migrations unknown to this application version.");
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (existingDatabase && pending.Length > 0 && !applyMigrations)
            throw new InvalidOperationException("Database upgrade required. Stop the backend, back up storage, then run --migrate.");
        if (pending.Length > 0) await db.Database.MigrateAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.LocalProfiles.AnyAsync(cancellationToken))
        {
            if (await db.Users.AnyAsync(cancellationToken) || await db.Workspaces.AnyAsync(cancellationToken))
                throw new InvalidOperationException("Existing ownership data has no local profile; authorized recovery is required.");
            var user = new ApplicationUser(Guid.NewGuid(), clock.GetUtcNow());
            var workspace = new Workspace(Guid.NewGuid(), "Personal workspace", clock.GetUtcNow());
            db.AddRange(user, workspace, new WorkspaceMembership(user.Id, workspace.Id),
                new LocalProfile(Guid.NewGuid(), user.Id, workspace.Id));
            await db.SaveChangesAsync(cancellationToken);
        }
        var profile = await db.LocalProfiles.SingleAsync(cancellationToken);
        if (!await db.Memberships.AnyAsync(m => m.UserId == profile.UserId && m.WorkspaceId == profile.DefaultWorkspaceId && m.Role == WorkspaceRole.Owner, cancellationToken))
            throw new InvalidOperationException("Local ownership is invalid.");
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class LocalStore(TradingDbContext db) : IWorkspaceStore, ILocalProfileStore
{
    public Task<LocalProfile> GetAsync(CancellationToken cancellationToken) => db.LocalProfiles.AsNoTracking().SingleAsync(cancellationToken);
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try { return await db.LocalProfiles.AnyAsync(cancellationToken); }
        catch (SqliteException) { return false; }
    }

    public Task<WorkspaceSettings?> ReadAsync(Guid actorId, Guid workspaceId, CancellationToken cancellationToken) =>
        (from workspace in db.Workspaces.AsNoTracking()
         join membership in db.Memberships on workspace.Id equals membership.WorkspaceId
         where workspace.Id == workspaceId && membership.UserId == actorId
         select new WorkspaceSettings(workspace.Id, workspace.DisplayName)).SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> RenameAsync(Guid actorId, Guid workspaceId, string name, DateTimeOffset now,
        string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var authorized = await db.Memberships.AnyAsync(m => m.UserId == actorId && m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner, cancellationToken);
        if (!authorized) return false;
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, cancellationToken);
        workspace.Rename(name);
        db.AuditRecords.Add(new AuditRecord(actorId, workspaceId, now, correlationId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
