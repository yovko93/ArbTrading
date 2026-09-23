using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperAutomationPersistenceTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Restart_preserves_settings_history_and_kill_but_never_armed_session(bool latch)
    {
        var c = new PaperApiTests.Case(preserveStorage: true); c.Clock.ManualTimers = true;
        string root; string path; Guid profile; Guid? generation;
        try
        {
            await c.Start(); await PaperAutomationTests.Configure(c); (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
            await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
            var s = (await PaperAutomationTests.Status(c))!; Assert.Equal(1, s.ExecutionsCommitted); profile = s.Profile!.Revision; generation = s.GenerationId;
            if (latch) await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().KillAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, true, null, false, "Persistent emergency", default);
            root = c.Fixture.Root; path = c.Root;
        }
        finally { await c.DisposeAsync(); }
        await using var restarted = new BackendFixture(root: root); using var client = await restarted.AuthenticatedClientAsync();
        var status = (await client.GetFromJsonAsync<PaperAutomationStatusResponse>(path + "/paper/automation/status"))!;
        Assert.Equal(latch ? "KillSwitchLatched" : "Disarmed", status.State); Assert.Null(status.SessionId); Assert.Equal(profile, status.Profile!.Revision);
        Assert.Equal(generation, status.GenerationId); Assert.Equal(latch, status.KillSwitch.IsLatched);
        await restarted.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        var execution = Assert.Single(await restarted.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().ToArrayAsync())); Assert.Equal(PaperExecutionOrigin.AutomaticPaper, execution.Origin);
        Assert.Equal("Healthy", (await (await client.PostAsync(path + $"/paper/reconcile?generationId={generation}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>())!.Integrity);
    }
    [Fact] public async Task Phase04D_upgrade_preserves_every_existing_column_and_all_financial_facts()
    {
        await using var c = new PaperApiTests.Case(); c.Clock.ManualTimers = true; await c.Start(); var execution = await SettlementTests.Execute(c);
        await SettlementTests.Settle(c, execution.GenerationId);
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            await db.Database.MigrateAsync("20260923154728_PaperRiskPolicy"); db.ChangeTracker.Clear(); await db.Database.OpenConnectionAsync(); var connection = db.Database.GetDbConnection();
            var columns = new Dictionary<string, string[]>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'"; await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { var name = reader.GetString(0); if (!name.StartsWith("sqlite_", StringComparison.Ordinal) && !name.StartsWith("__", StringComparison.Ordinal)) columns[name] = []; }
            }
            static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            foreach (var table in columns.Keys.ToArray())
            {
                await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
                await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(1)); columns[table] = names.ToArray();
            }
            async Task<string[]> Read(string table)
            {
                await using var command = connection.CreateCommand(); command.CommandText = "SELECT " + string.Join(",", columns[table].Select(Quote)) + " FROM " + Quote(table);
                await using var reader = await command.ExecuteReaderAsync(); var rows = new List<string>();
                while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(JsonSerializer.Serialize(values)); }
                return rows.Order(StringComparer.Ordinal).ToArray();
            }
            var before = new Dictionary<string, string[]>(); foreach (var table in columns.Keys) before[table] = await Read(table);
            await db.Database.MigrateAsync(); db.ChangeTracker.Clear(); foreach (var table in columns.Keys) Assert.Equal(before[table], await Read(table));
            var e = await db.Set<PaperExecutionEntry>().SingleAsync(); Assert.Equal(PaperExecutionOrigin.Manual, e.Origin); Assert.Null(e.AutomationProofJson); Assert.NotNull(e.RiskProofJson);
            Assert.Empty(await db.Set<PaperAutomationProfileEntry>().ToArrayAsync()); Assert.Empty(await db.Set<PaperAutomationControlEntry>().ToArrayAsync());
            Assert.False(db.Database.HasPendingModelChanges()); return 0;
        });
        await SettlementTests.Settle(c, execution.GenerationId, "Polymarket", "b", "123"); Assert.Equal("Healthy", await SettlementTests.Reconcile(c, execution.GenerationId));
    }
    [Theory] [InlineData("session")] [InlineData("stamp")] [InlineData("version")] [InlineData("origin")]
    public async Task Historical_provenance_corruption_is_detected(string field)
    {
        await using var c = new PaperApiTests.Case(); c.Clock.ManualTimers = true; await c.Start(); await PaperAutomationTests.Configure(c);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        var generation = (await PaperAutomationTests.Status(c))!.GenerationId!.Value;
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var e = await db.Set<PaperExecutionEntry>().SingleAsync(); var proof = JsonSerializer.Deserialize<PaperAutomationProof>(e.AutomationProofJson!)!;
            if (field == "session") e.AutomationSessionId = Guid.NewGuid();
            if (field == "stamp") e.AutomationInputStamp = new string('Z', 64);
            if (field == "version") e.AutomationProofJson = JsonSerializer.Serialize(proof with { ProfileVersion = 99 });
            if (field == "origin") e.Origin = PaperExecutionOrigin.Manual;
            return await db.SaveChangesAsync();
        });
        Assert.Equal("Corrupt", await SettlementTests.Reconcile(c, generation));
    }
}
