using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperPersistenceTests
{
    [Fact] public async Task Phase03D_migration_preserves_owned_metadata_and_starts_without_funds()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var options = DatabaseOptions.ForFile(Path.Combine(root, "migration.db")); var at = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var profile = Guid.NewGuid(); var relationship = Guid.NewGuid();
        try
        {
            await using (var db = new TradingDbContext(options))
            {
                await db.Database.MigrateAsync("20260923064859_OpportunityMonitoring");
                db.AddRange(new ApplicationUser(user, at), new Workspace(workspace, "Retained workspace", at), new WorkspaceMembership(user, workspace), new LocalProfile(profile, user, workspace));
                var market = RelationshipPersistenceTests.Market("Kalshi", "preserved"); db.Add(market);
                db.Add(new MarketRelationshipEntry { Id = relationship, WorkspaceId = workspace, SourceExchange = "Kalshi", SourceId = "preserved", TargetExchange = "Kalshi", TargetId = "preserved", SourceFingerprint = "retain-source", TargetFingerprint = "retain-target" });
                db.Add(new FeeScheduleEntry { Exchange = "Kalshi", MarketId = "preserved", ScheduleJson = JsonSerializer.Serialize(FeeApiTests.Schedule("Kalshi", "preserved")), Fingerprint = "retain-fee", RetrievedAt = at });
                db.Add(new FeeProfileEntry { WorkspaceId = workspace, Profile = KalshiFeeAccountProfile.DirectMember, Revision = Guid.NewGuid() });
                db.Add(new MonitoringProfileEntry { WorkspaceId = workspace, Revision = Guid.NewGuid(), SettingsJson = JsonSerializer.Serialize(new MonitoringProfile()) });
                db.Add(new MonitoringAlertEntry { Id = Guid.NewGuid(), WorkspaceId = workspace, TriggeredAt = at, EventJson = "retained historical fixture" });
                db.Add(new AuditRecord(user, workspace, at, "before-paper")); await db.SaveChangesAsync();
            }
            await using (var db = new TradingDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(profile, (await db.LocalProfiles.SingleAsync()).Id); Assert.Equal(user, (await db.Users.SingleAsync()).Id);
                Assert.Equal("Retained workspace", (await db.Workspaces.SingleAsync()).DisplayName); Assert.Single(await db.Memberships.ToArrayAsync());
                Assert.Equal(relationship, (await db.MarketRelationships.SingleAsync()).Id); Assert.Single(await db.CatalogMarkets.ToArrayAsync());
                Assert.Equal("retain-fee", (await db.FeeSchedules.SingleAsync()).Fingerprint); Assert.Single(await db.FeeProfiles.ToArrayAsync());
                Assert.Single(await db.MonitoringProfiles.ToArrayAsync()); Assert.Equal("retained historical fixture", (await db.MonitoringAlerts.SingleAsync()).EventJson);
                Assert.Single(await db.AuditRecords.ToArrayAsync()); Assert.Empty(await db.Set<PaperGenerationEntry>().ToArrayAsync()); Assert.Empty(await db.Set<PaperLedgerEntry>().ToArrayAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact] public async Task Backend_restart_returns_committed_request_without_preview_or_replay()
    {
        string root; Guid workspace; ConfirmPaperRequest request; Guid execution;
        await using (var f = new BackendFixture(preserveStorage: true))
        {
            root = f.Root; using var client = await f.AuthenticatedClientAsync(); workspace = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!.DefaultWorkspaceId;
            var relationship = await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic);
            await f.WithDatabaseAsync(async db => { var fees = new FeeStore(db); await fees.SaveAsync(FeeApiTests.Schedule("Kalshi", "a"), default); await fees.SaveAsync(FeeApiTests.Schedule("Polymarket", "b"), default); await fees.SetProfileAsync(workspace, KalshiFeeAccountProfile.DirectMember, default); return 0; });
            var path = $"/api/v1/workspaces/{workspace}";
            (await client.PutAsJsonAsync(path + "/paper/admission-policy", new SavePaperRiskPolicyRequest(null, true, PaperRiskApiTests.Permissive))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync(path + "/paper/account/initialize", new InitializePaperRequest(true, null, "Restart fixture", [new("Kalshi", "USD", 100), new("Polymarket", "USD", 100)]))).EnsureSuccessStatusCode();
            OpportunityApiTests.Books(f);
            var job = await OpportunityApiTests.Start(client, path + "/opportunities", new(relationship, EvaluateFees: true));
            var key = Assert.Single((await client.GetFromJsonAsync<OpportunityPageResponse>($"{path}/opportunities/jobs/{job.Id}/results"))!.Items).OpportunityKey;
            var preview = (await (await client.PostAsJsonAsync(path + "/paper/preview", new PaperPreviewRequest(key, 10))).Content.ReadFromJsonAsync<PaperPreviewResponse>())!;
            Assert.True(preview.WouldExecute, preview.Rejection); request = new(Guid.NewGuid(), preview.PreviewId, key, 10, true);
            var committed = (await (await client.PostAsJsonAsync(path + "/paper/execute", request)).Content.ReadFromJsonAsync<PaperCommitResponse>())!;
            execution = committed.Execution!.Id;
        }
        await using var restarted = new BackendFixture(root: root); using var next = await restarted.AuthenticatedClientAsync();
        var response = (await (await next.PostAsJsonAsync($"/api/v1/workspaces/{workspace}/paper/execute", request)).Content.ReadFromJsonAsync<PaperCommitResponse>())!;
        Assert.True(response.Duplicate); Assert.Equal(execution, response.Execution!.Id);
        Assert.Equal(1, await restarted.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal(2, await restarted.WithDatabaseAsync(db => db.Set<PaperPositionEntry>().CountAsync()));
        var integrity = (await (await next.PostAsync($"/api/v1/workspaces/{workspace}/paper/reconcile?generationId={response.Execution.GenerationId}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>())!;
        Assert.Equal("Healthy", integrity.Integrity);
    }
}
