using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(root, "test.db");
    public PersistenceTests() => Directory.CreateDirectory(root);
    private TradingDbContext Open() => new(DatabaseOptions.ForFile(DatabasePath));

    [Fact]
    public async Task Initialization_is_idempotent_and_ids_and_settings_survive_reopen()
    {
        Guid userId, workspaceId, profileId;
        await using (var db = Open())
        {
            var initializer = new DatabaseInitializer(db, TimeProvider.System);
            await initializer.InitializeAsync(false, false, default);
            await initializer.InitializeAsync(true, false, default);
            var profile = await db.LocalProfiles.SingleAsync();
            (userId, workspaceId, profileId) = (profile.UserId, profile.DefaultWorkspaceId, profile.Id);
            Assert.True(await new LocalStore(db).RenameAsync(userId, workspaceId, "Persisted", DateTimeOffset.UtcNow, "restart-test", default));
        }
        await using (var db = Open())
        {
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default);
            var profile = await db.LocalProfiles.SingleAsync();
            Assert.Equal((userId, workspaceId, profileId), (profile.UserId, profile.DefaultWorkspaceId, profile.Id));
            Assert.Equal(1, await db.Users.CountAsync()); Assert.Equal(1, await db.Workspaces.CountAsync());
            Assert.Equal(1, await db.Memberships.CountAsync()); Assert.Equal("Persisted", (await db.Workspaces.SingleAsync()).DisplayName);
            Assert.Single(await db.AuditRecords.ToListAsync());
        }
    }

    [Fact]
    public async Task Utc_ticks_roundtrip_preserves_precision_and_SQL_ordering()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 16, 5, 4, TimeSpan.FromHours(3)).AddTicks(1234567);
        Guid workspace;
        await using (var db = Open())
        {
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
            var profile = await db.LocalProfiles.SingleAsync(); workspace = profile.DefaultWorkspaceId;
            db.AddRange(new AuditRecord(profile.UserId, workspace, instant.AddTicks(1), "later"), new AuditRecord(profile.UserId, workspace, instant, "earlier"));
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var records = await db.AuditRecords.Where(a => a.WorkspaceId == workspace && a.OccurredAt >= instant).OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal(new[] { "earlier", "later" }, records.Select(a => a.CorrelationId));
            Assert.Equal(instant.UtcTicks, records[0].OccurredAt.UtcTicks);
            Assert.All(records, record => Assert.Equal(TimeSpan.Zero, record.OccurredAt.Offset));
        }
    }

    [Fact]
    public async Task Database_enforces_foreign_keys_and_unique_local_profile()
    {
        await using (var db = Open())
        {
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
            db.Memberships.Add(new WorkspaceMembership(Guid.NewGuid(), Guid.NewGuid()));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = Open())
        {
            var profile = await db.LocalProfiles.SingleAsync();
            db.LocalProfiles.Add(new LocalProfile(Guid.NewGuid(), profile.UserId, profile.DefaultWorkspaceId));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Existing_database_requires_explicit_migration_and_is_not_reset()
    {
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE ExistingUserData (Value TEXT); INSERT INTO ExistingUserData VALUES ('keep');";
            await command.ExecuteNonQueryAsync();
        }
        await using (var db = Open())
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default));
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM ExistingUserData";
            Assert.Equal("keep", await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Existing_ownership_without_profile_is_not_claimed()
    {
        await using var db = Open(); await db.Database.MigrateAsync();
        db.Users.Add(new ApplicationUser(Guid.NewGuid(), DateTimeOffset.UtcNow)); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default));
        Assert.Empty(await db.LocalProfiles.ToListAsync()); Assert.Empty(await db.Workspaces.ToListAsync());
    }

    [Fact]
    public void Only_one_backend_can_own_data_or_runtime_directory()
    {
        using (var lease = new LocalRuntimeLease(root, Path.Combine(root, "runtime")))
        {
            Assert.Throws<IOException>(() => new LocalRuntimeLease(root));
            Assert.Throws<IOException>(() => new LocalRuntimeLease(Path.Combine(root, "runtime")));
        }
        using var reacquired = new LocalRuntimeLease(root);
    }

    [Fact]
    public async Task Credential_file_is_private_and_rotation_is_readable()
    {
        var file = new ProtectedLocalConnectionFile(Path.Combine(root, "runtime"));
        var first = new Arbitrage.Backend.LocalCredential();
        await file.WriteAsync(new("http://127.0.0.1:5274", first.Value), default);
        Assert.True(first.Matches((await file.ReadAsync(default)).Credential));
        var next = new Arbitrage.Backend.LocalCredential();
        await file.WriteAsync(new("http://127.0.0.1:5274", next.Value), default);
        var read = await file.ReadAsync(default);
        Assert.True(next.Matches(read.Credential)); Assert.False(first.Matches(read.Credential));
        ProtectedStorage.VerifyPrivateFile(Path.Combine(root, "runtime", "connection.json"));
    }

    [Fact]
    public async Task Failed_initialization_rolls_back_all_ownership_records()
    {
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectProfile BEFORE INSERT ON LocalProfiles BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");
            await Assert.ThrowsAsync<DbUpdateException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default));
        }
        await using (var db = Open())
        {
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.Workspaces.ToListAsync());
            Assert.Empty(await db.Memberships.ToListAsync()); Assert.Empty(await db.LocalProfiles.ToListAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectProfile;");
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default);
            Assert.Single(await db.LocalProfiles.ToListAsync());
        }
    }

    [Fact]
    public async Task Credential_refresh_and_rotation_can_overlap()
    {
        var file = new ProtectedLocalConnectionFile(Path.Combine(root, "runtime"));
        await file.WriteAsync(new("http://127.0.0.1:5274", new Arbitrage.Backend.LocalCredential().Value), default);
        var reads = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                Assert.Equal(64, (await file.ReadAsync(default)).Credential.Length);
        });
        for (var i = 0; i < 25; i++)
            await file.WriteAsync(new("http://127.0.0.1:5274", new Arbitrage.Backend.LocalCredential().Value), default);
        await reads;
    }

    [Fact]
    public async Task Credential_rotation_tolerates_a_short_lived_external_reader()
    {
        var directory = Path.Combine(root, "runtime");
        var file = new ProtectedLocalConnectionFile(directory);
        await file.WriteAsync(new("http://127.0.0.1:5274", new Arbitrage.Backend.LocalCredential().Value), default);
        var next = new Arbitrage.Backend.LocalCredential();
        Task rotation;
        using (var reader = new FileStream(Path.Combine(directory, "connection.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rotation = file.WriteAsync(new("http://127.0.0.1:5274", next.Value), default);
            await Task.Delay(150);
        }
        await rotation;
        Assert.True(next.Matches((await file.ReadAsync(default)).Credential));
    }

    [Fact]
    public async Task Failed_audit_insert_rolls_back_workspace_change()
    {
        await using (var db = Open())
        {
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
            var profile = await db.LocalProfiles.SingleAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectAudit BEFORE INSERT ON AuditRecords BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");
            await Assert.ThrowsAsync<DbUpdateException>(() => new LocalStore(db).RenameAsync(profile.UserId, profile.DefaultWorkspaceId, "Must roll back", DateTimeOffset.UtcNow, "fixture", default));
        }
        await using (var db = Open())
        {
            Assert.Equal("Personal workspace", (await db.Workspaces.SingleAsync()).DisplayName);
            Assert.Empty(await db.AuditRecords.ToListAsync());
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
