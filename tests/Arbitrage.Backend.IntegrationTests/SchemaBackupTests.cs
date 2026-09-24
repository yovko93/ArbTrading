using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class SchemaBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Arbitrage schema tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "arbitrage.db");
    private string Backups => Path.Combine(root, "backups", "schema");
    public SchemaBackupTests() => ProtectedStorage.CreatePrivateDirectory(root);
    private TradingDbContext Open(string? path = null) => new(DatabaseOptions.ForFile(path ?? Database));
    [Fact] public async Task New_and_current_database_are_idempotent_without_backups_or_business_rows()
    {
        await using var db = Open(); var diagnostics = new List<string>();
        await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, true, default, diagnostics.Add);
        var profile = await db.LocalProfiles.AsNoTracking().SingleAsync();
        await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default, diagnostics.Add);
        Assert.Equal(profile.Id, (await db.LocalProfiles.SingleAsync()).Id);
        Assert.Single(await db.Users.ToArrayAsync()); Assert.Single(await db.Workspaces.ToArrayAsync()); Assert.Single(await db.Memberships.ToArrayAsync());
        Assert.Empty(await db.Set<PaperGenerationEntry>().ToArrayAsync());
        Assert.Empty(await db.Set<PaperReliabilityCampaignEntry>().ToArrayAsync());
        Assert.False(Directory.Exists(Backups)); Assert.Equal(new[] { "DatabaseCreated", "DatabaseAlreadyCurrent" }, diagnostics);
    }
    [Fact] public async Task Consistent_backup_includes_wal_and_retains_latest_five_private_copies()
    {
        await using var db = Open(); await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, true, default);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_autocheckpoint=0");
        await db.Database.ExecuteSqlRawAsync("UPDATE Workspaces SET DisplayName='Committed WAL data'");
        var backup = new SqliteSchemaBackup();
        for (var n = 0; n < 7; n++) await backup.CreateAsync(db, DateTimeOffset.UtcNow.AddSeconds(n), default);
        var files = Directory.GetFiles(Backups, "*.db"); Assert.Equal(5, files.Length);
        Assert.Empty(Directory.GetFiles(Backups, "*.pending"));
        foreach (var file in files)
        {
            ProtectedStorage.VerifyPrivateFile(file);
            await using var copy = Open(file); Assert.Equal("Committed WAL data", (await copy.Workspaces.SingleAsync()).DisplayName);
            Assert.Empty(await copy.Database.GetPendingMigrationsAsync());
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Pending_upgrade_backup_or_migration_failure_preserves_original_schema(bool migrationFailure)
    {
        await using var db = Open(); await db.Database.MigrateAsync("20260923191645_PaperAutomation");
        if (migrationFailure) await db.Database.ExecuteSqlRawAsync("CREATE TABLE PaperReliabilityCampaignEntry (Id TEXT PRIMARY KEY)");
        else File.WriteAllText(Path.Combine(root, "backups"), "backup directory blocked");
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        if (migrationFailure)
        {
            await Assert.ThrowsAsync<SqliteException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default));
            Assert.Single(Directory.GetFiles(Backups, "*.db"));
        }
        else
        {
            var error = await Assert.ThrowsAsync<DatabaseStartupException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default));
            Assert.Equal("DatabaseBackupFailed", error.Code);
        }
        Assert.Equal(before, await db.Database.GetAppliedMigrationsAsync());
    }
    [Fact] public async Task Unknown_future_schema_fails_without_backup_reset_or_downgrade()
    {
        await using var db = Open(); await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, true, default);
        var id = (await db.LocalProfiles.SingleAsync()).Id;
        await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Future', '99.0.0')");
        var error = await Assert.ThrowsAsync<DatabaseStartupException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default));
        Assert.Equal("UnsupportedNewerSchema", error.Code); Assert.Equal(id, (await db.LocalProfiles.SingleAsync()).Id);
        Assert.Contains("20990101000000_Future", await db.Database.GetAppliedMigrationsAsync()); Assert.False(Directory.Exists(Backups));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
