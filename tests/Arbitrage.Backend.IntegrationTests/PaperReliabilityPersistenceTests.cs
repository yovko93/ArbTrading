using System.Net;
using System.Net.Http.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Arbitrage.Backend.IntegrationTests;
public sealed class PaperReliabilityPersistenceTests
{
    [Fact] public async Task Restart_excludes_one_hour_downtime_and_never_rearms()
    {
        var c = new PaperApiTests.Case(preserveStorage: true); c.Clock.ManualTimers = true;
        string root, path; Guid campaignId;
        try
        {
            await c.Start(); await PaperAutomationTests.Configure(c); var campaign = await PaperReliabilityTests.Act(c, "start"); campaignId = campaign.Id;
            (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); c.Clock.Now += TimeSpan.FromHours(2);
            await c.Fixture.Services.GetRequiredService<PaperReliabilityCoordinator>().ProcessOnceAsync(); root = c.Fixture.Root; path = c.Root;
        }
        finally { await c.DisposeAsync(); }
        c.Clock.Now += TimeSpan.FromHours(1);
        await using var restarted = new BackendFixture(s => s.AddSingleton<TimeProvider>(c.Clock), root: root);
        using var client = await restarted.AuthenticatedClientAsync(); var collector = restarted.Services.GetRequiredService<PaperReliabilityCoordinator>();
        await collector.ProcessOnceAsync(); c.Clock.Now += TimeSpan.FromHours(2); await collector.ProcessOnceAsync();
        var report = (await client.GetFromJsonAsync<PaperReliabilityReportResponse>(path + $"/paper/reliability/campaigns/{campaignId}/report"))!;
        Assert.Equal(TimeSpan.FromHours(4).Ticks, report.Counters["BackendObservedTicks"]);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, report.Counters["AutomationArmedTicks"]);
        Assert.Equal("Disarmed", (await client.GetFromJsonAsync<PaperAutomationStatusResponse>(path + "/paper/automation/status"))!.State);
    }
    [Fact] public async Task Storage_failure_is_isolated_and_recovery_records_gap()
    {
        await using var c = await PaperSizingApiTests.Start(); var campaign = await PaperReliabilityTests.Act(c, "start");
        await c.Fixture.WithDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE PaperReliabilityCampaignEntry RENAME TO UnavailableCampaigns"));
        var collector = c.Fixture.Services.GetRequiredService<PaperReliabilityCoordinator>(); await collector.ProcessOnceAsync();
        Assert.True(collector.TelemetryPersistenceFailures > 0);
        await c.Fixture.WithDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE UnavailableCampaigns RENAME TO PaperReliabilityCampaignEntry"));
        await PaperReliabilityTests.Act(c, "evaluate", campaign); var report = await PaperReliabilityTests.Report(c, campaign.Id);
        Assert.True(report.EvidenceGapDetected); Assert.Equal("InsufficientEvidence", report.State);
    }
    [Fact] public async Task Event_retention_is_bounded_and_marks_gap()
    {
        await using var c = await PaperSizingApiTests.Start(); var campaign = await PaperReliabilityTests.Act(c, "start");
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var existing = await db.Set<PaperReliabilityEventEntry>().CountAsync();
            for (var n = existing; n < 10000; n++) db.Add(new PaperReliabilityEventEntry { Id = Guid.NewGuid(), CampaignId = campaign.Id, At = c.Clock.Now, Kind = "Fixture" });
            return await db.SaveChangesAsync();
        });
        campaign = await PaperReliabilityTests.Act(c, "pause", campaign); Assert.True(campaign.EventRetentionTruncated);
        Assert.Equal(10000, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperReliabilityEventEntry>().CountAsync()));
        await PaperReliabilityTests.Act(c, "evaluate", campaign); Assert.True((await PaperReliabilityTests.Report(c, campaign.Id)).EvidenceGapDetected);
    }
    [Fact] public async Task Exact_04F_schema_upgrade_preserves_adaptive_proofs_and_every_financial_row()
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c, PaperSizingApiTests.Settings);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        await c.Fixture.WithDatabaseAsync(async db => { await db.Database.MigrateAsync("20260923191645_PaperAutomation"); return 0; });
        var before = await PaperReliabilityTests.Financial(c);
        await c.Fixture.WithDatabaseAsync(async db => { await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default);
            var path = ((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection()).DataSource;
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "backups", "schema"), "*.db"));
            await using var copy = new TradingDbContext(DatabaseOptions.ForFile(backup));
            Assert.Equal(await db.Set<PaperExecutionEntry>().CountAsync(), await copy.Set<PaperExecutionEntry>().CountAsync());
            Assert.Equal(await db.Set<PaperLedgerEntry>().CountAsync(), await copy.Set<PaperLedgerEntry>().CountAsync());
            Assert.Contains("20260924083029_PaperReliabilityCampaigns", await copy.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges()); return 0; });
        Assert.Equal(before, await PaperReliabilityTests.Financial(c));
        Assert.Empty(await c.Fixture.WithDatabaseAsync(db => db.Set<PaperReliabilityCampaignEntry>().ToArrayAsync()));
    }    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Settlement_evidence_and_duplicate_payout_are_derived_read_only(bool corrupt)
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c);
        var campaign = await PaperReliabilityTests.Act(c, "start"); (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
        await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        var execution = await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().SingleAsync());
        await SettlementTests.Settle(c, execution.GenerationId); await SettlementTests.Settle(c, execution.GenerationId, "Polymarket", "b", "123");
        if (corrupt) await c.Fixture.WithDatabaseAsync(async db =>
        {
            var original = await db.Set<PaperLedgerEntry>().FirstAsync(l => l.Reason == "PaperSettlement");
            db.Add(new PaperLedgerEntry { Id = Guid.NewGuid(), TransactionId = original.TransactionId, Exchange = original.Exchange, Currency = original.Currency,
                Reason = original.Reason, AvailableDelta = original.AvailableDelta, ReservedDelta = original.ReservedDelta }); return await db.SaveChangesAsync();
        });
        var before = await PaperReliabilityTests.Financial(c); await PaperReliabilityTests.Act(c, "evaluate", campaign); var report = await PaperReliabilityTests.Report(c, campaign.Id);
        Assert.Equal(before, await PaperReliabilityTests.Financial(c)); Assert.Equal(1, report.Counters["AutomaticExecutionsFullySettled"]);
        Assert.Equal(corrupt ? "Violated" : "Satisfied", report.Invariants.Single(i => i.Code == "NoSettlementDuplicatePayout").State);
        Assert.Equal(1, report.Counters["ExactPayoutMatches"]);
    }    [Theory] [InlineData("request")] [InlineData("session")]
    public async Task Tampered_duplicate_financial_identity_is_detected(string identity)
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c);
        var campaign = await PaperReliabilityTests.Act(c, "start"); (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
        await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_PaperExecutionEntry_WorkspaceId_RequestId");
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_PaperExecutionEntry_WorkspaceId_AutomationSessionId_AutomationInputStamp");
            var original = await db.Set<PaperExecutionEntry>().AsNoTracking().SingleAsync();
            var copy = System.Text.Json.JsonSerializer.Deserialize<PaperExecutionEntry>(System.Text.Json.JsonSerializer.Serialize(original))!;
            copy.Id = Guid.NewGuid(); if (identity == "session") copy.RequestId = Guid.NewGuid(); db.Add(copy);
            db.Add(new PaperWriterOrderEntry { WorkspaceId = copy.WorkspaceId, At = copy.CreatedAt, Kind = "Execution", ReferenceId = copy.Id, SessionId = copy.AutomationSessionId });
            return await db.SaveChangesAsync();
        });
        await PaperReliabilityTests.Act(c, "evaluate", campaign); var report = await PaperReliabilityTests.Report(c, campaign.Id);
        Assert.Equal("InvariantViolation", report.State);
        Assert.Equal("Violated", report.Invariants.Single(i => i.Code == (identity == "request" ? "NoDuplicateExecutionRequestIds" : "NoDuplicateAutomaticSessionTriggerExecution")).State);
    }    [Fact] public async Task Uncheckpointed_gap_is_visible_and_unknown_policy_cannot_pass()
    {
        await using var c = await PaperSizingApiTests.Start(); var campaign = await PaperReliabilityTests.Act(c, "start");
        c.Fixture.Services.GetRequiredService<PaperReliabilityTelemetry>().Gap(c.Session.DefaultWorkspaceId);
        var current = (await c.Client.GetFromJsonAsync<PaperReliabilityCurrentResponse>(c.Root + "/paper/reliability/current"))!;
        Assert.True(current.Campaign!.EvidenceGapDetected);
        await c.Fixture.WithDatabaseAsync(async db => { (await db.Set<PaperReliabilityCampaignEntry>().SingleAsync()).PolicyVersion = 99; return await db.SaveChangesAsync(); });
        await PaperReliabilityTests.Act(c, "evaluate", campaign); var report = await PaperReliabilityTests.Report(c, campaign.Id);
        Assert.Equal("InsufficientEvidence", report.State); Assert.Equal("Unknown", report.Invariants.Single(i => i.Code == "EvidencePolicySupported").State);
    }    [Fact] public async Task Paused_settlement_with_equal_timestamp_is_not_campaign_economics()
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c); var campaign = await PaperReliabilityTests.Act(c, "start");
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        var execution = await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().SingleAsync());
        campaign = await PaperReliabilityTests.Act(c, "pause", campaign);
        await SettlementTests.Settle(c, execution.GenerationId); await SettlementTests.Settle(c, execution.GenerationId, "Polymarket", "b", "123");
        campaign = await PaperReliabilityTests.Act(c, "resume", campaign); await PaperReliabilityTests.Act(c, "evaluate", campaign);
        var report = await PaperReliabilityTests.Report(c, campaign.Id); Assert.Equal(0, report.Counters["SettlementCount"]);
        Assert.Equal(0, report.Counters["AutomaticExecutionsFullySettled"]); Assert.All(report.Economics, m => Assert.Equal(0, m.SettlementPayout));
    }
    [Fact] public async Task Execution_before_kill_latch_is_valid_even_with_equal_utc_timestamps()
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c); var campaign = await PaperReliabilityTests.Act(c, "start");
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); var automation = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>();
        await automation.ProcessOnceAsync(); await automation.KillAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, true, null, false, "Fixture latch", default);
        await PaperReliabilityTests.Act(c, "evaluate", campaign); var report = await PaperReliabilityTests.Report(c, campaign.Id);
        Assert.Equal(1, report.Counters["KillSwitchLatches"]); Assert.Equal(0, report.Counters["ExecutionCountAfterLatestLatchBeforeReset"]);
        Assert.Equal("Satisfied", report.Invariants.Single(i => i.Code == "NoAutomaticExecutionAfterKillLatchOrdering").State);
    }    [Fact] public async Task Failed_final_evaluation_recovers_collection_with_a_gap()
    {
        await using var c = await PaperSizingApiTests.Start(); var campaign = await PaperReliabilityTests.Act(c, "start");
        await c.Fixture.WithDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE PaperReliabilityEvaluationEntry RENAME TO UnavailableEvaluations"));
        var failed = await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/complete", new PaperReliabilityActionRequest(campaign.Id, campaign.Revision));
        Assert.False(failed.IsSuccessStatusCode);
        await c.Fixture.WithDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE UnavailableEvaluations RENAME TO PaperReliabilityEvaluationEntry"));
        await c.Fixture.Services.GetRequiredService<PaperReliabilityCoordinator>().ProcessOnceAsync();
        var report = await PaperReliabilityTests.Report(c, campaign.Id); Assert.True(report.EvidenceGapDetected);
        campaign = await PaperReliabilityTests.Act(c, "complete", campaign); Assert.Equal("Completed", campaign.State);
    }}
