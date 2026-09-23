using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class FeePersistenceTests
{
    [Fact] public async Task Phase03B_migration_preserves_identity_audit_catalog_relationships_and_history_and_restarts_fee_metadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid() + ".db"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var profile = Guid.NewGuid(); var relationship = Guid.NewGuid(); var job = Guid.NewGuid();
        try
        {
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            {
                await db.Database.MigrateAsync("20260922195551_IndependentRelationshipSetFacts");
                db.AddRange(new ApplicationUser(user, DateTimeOffset.UtcNow), new Workspace(workspace, "Preserved", DateTimeOffset.UtcNow), new WorkspaceMembership(user, workspace), new LocalProfile(profile, user, workspace));
                db.Add(new AuditRecord(user, workspace, DateTimeOffset.UtcNow, "before-fees"));
                db.AddRange(RelationshipPersistenceTests.Market("Kalshi", "a"), RelationshipPersistenceTests.Market("Polymarket", "b"));
                db.MarketRelationships.Add(new() { Id = relationship, WorkspaceId = workspace, SourceExchange = "Kalshi", SourceId = "a", TargetExchange = "Polymarket", TargetId = "b", State = VerificationState.VerifiedManual });
                db.RelationshipJobs.Add(new() { Id = job, ActorId = user, WorkspaceId = workspace, State = "Partial", StartedAt = DateTimeOffset.UtcNow, Notice = "Preserve history" });
                await db.SaveChangesAsync();
            }
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(user, (await db.Users.SingleAsync()).Id); Assert.Equal(profile, (await db.LocalProfiles.SingleAsync()).Id);
                Assert.Equal(workspace, (await db.Workspaces.SingleAsync()).Id); Assert.Single(await db.Memberships.ToListAsync());
                Assert.Equal("before-fees", (await db.AuditRecords.SingleAsync()).CorrelationId); Assert.Equal(2, await db.CatalogMarkets.CountAsync());
                Assert.Equal(relationship, (await db.MarketRelationships.SingleAsync()).Id); Assert.Equal(job, (await db.RelationshipJobs.SingleAsync()).Id);
                var store = new FeeStore(db); await store.SaveAsync(FeeApiTests.Schedule("Kalshi", "a"), default);
                await store.SetProfileAsync(workspace, KalshiFeeAccountProfile.DirectMember, default);
            }
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            {
                var store = new FeeStore(db); Assert.Equal(KalshiFeeAccountProfile.DirectMember, await store.ProfileAsync(workspace, default));
                var schedule = await store.ReadAsync("Kalshi", "a", default); Assert.Equal("quadratic", Assert.Single(schedule!.Rules).Type);
                Assert.Equal(schedule.Fingerprint, (await db.FeeSchedules.SingleAsync()).Fingerprint); Assert.DoesNotContain(db.Model.GetEntityTypes(), t => t.Name.Contains("FeeQuote", StringComparison.Ordinal));
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }
}
