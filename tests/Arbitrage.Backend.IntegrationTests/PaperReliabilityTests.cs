using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperReliabilityTests
{
    internal static async Task<PaperReliabilityCampaignResponse> Act(PaperApiTests.Case c, string action, PaperReliabilityCampaignResponse? campaign = null)
    {
        var response = await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/" + action, new PaperReliabilityActionRequest(campaign?.Id, campaign?.Revision, "Isolated evidence", "Test only"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<PaperReliabilityCampaignResponse>())!;
    }
    internal static async Task<PaperReliabilityReportResponse> Report(PaperApiTests.Case c, Guid id) => (await c.Client.GetFromJsonAsync<PaperReliabilityReportResponse>(c.Root + $"/paper/reliability/campaigns/{id}/report"))!;
    internal static async Task<string> Financial(PaperApiTests.Case c) => await c.Fixture.WithDatabaseAsync(async db =>
    {
        var rows = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes().Where(t => t.ClrType.Name.StartsWith("Paper", StringComparison.Ordinal) && !t.ClrType.Name.StartsWith("PaperReliability", StringComparison.Ordinal) && t.ClrType != typeof(PaperWriterOrderEntry)))
        {
            var table = entity.GetTableName()!; await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(table + JsonSerializer.Serialize(values)); }
        }
        return string.Join("\n", rows.Order(StringComparer.Ordinal));
    });
    [Fact] public async Task Explicit_lifecycle_runtime_pause_frozen_report_and_financial_isolation()
    {
        await using var c = await PaperSizingApiTests.Start();
        Assert.Null((await c.Client.GetFromJsonAsync<PaperReliabilityCurrentResponse>(c.Root + "/paper/reliability/current"))!.Campaign);
        var before = await Financial(c); var campaign = await Act(c, "start"); c.Clock.Now += TimeSpan.FromHours(2);
        campaign = await Act(c, "pause", campaign); c.Clock.Now += TimeSpan.FromHours(10); campaign = await Act(c, "resume", campaign);
        c.Clock.Now += TimeSpan.FromHours(2); await Act(c, "evaluate", campaign); var report = await Report(c, campaign.Id);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, report.Counters["BackendObservedTicks"]); Assert.Equal("InsufficientEvidence", report.State);
        Assert.Equal(0, report.Counters.GetValueOrDefault("AutomationArmedTicks")); Assert.Equal(before, await Financial(c));
        campaign = await Act(c, "complete", campaign); var frozen = JsonSerializer.Serialize(await Report(c, campaign.Id));
        c.Clock.Now += TimeSpan.FromHours(5); Assert.Equal(frozen, JsonSerializer.Serialize(await Report(c, campaign.Id))); Assert.Equal("Completed", campaign.State);
        var export = await c.Client.PostAsync(c.Root + $"/paper/reliability/campaigns/{campaign.Id}/export", null); export.EnsureSuccessStatusCode();
        var path = (await export.Content.ReadFromJsonAsync<PaperReliabilityExportResponse>())!.Path;
        var bytes = await File.ReadAllTextAsync(path); Assert.Contains("PAPER SIMULATION EVIDENCE ONLY", bytes);
        (await c.Client.PostAsync(c.Root + $"/paper/reliability/campaigns/{campaign.Id}/export", null)).EnsureSuccessStatusCode(); Assert.Equal(bytes, await File.ReadAllTextAsync(path));
        Assert.Equal(before, await Financial(c));
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/complete", new PaperReliabilityActionRequest(campaign.Id, campaign.Revision))).StatusCode);
    }
    [Fact] public async Task Operational_smoke_campaign_observes_explicit_arm_execution_disarm_and_frozen_export()
    {
        await using var c = await PaperSizingApiTests.Start();
        await PaperAutomationTests.Configure(c);
        Assert.Equal("Running", c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().Status(c.Session.DefaultWorkspaceId).State.ToString());
        var campaign = await Act(c, "start");
        Assert.Equal("Collecting", campaign.State);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
        Assert.Equal("Armed", (await PaperAutomationTests.Status(c))!.State);
        await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        await Act(c, "evaluate", campaign);
        var observed = await Report(c, campaign.Id);
        Assert.Equal(1, observed.Counters["AutomaticExecutionsCommitted"]);
        Assert.True(observed.Counters.GetValueOrDefault("CandidateInputsObserved") > 0);
        Assert.Single(observed.Executions);
        Assert.All(observed.Invariants, i => Assert.Equal("Satisfied", i.State));
        (await c.Client.PostAsync(c.Root + "/paper/automation/disarm", null)).EnsureSuccessStatusCode();
        Assert.Equal("Disarmed", (await PaperAutomationTests.Status(c))!.State);
        campaign = await Act(c, "complete", campaign);
        Assert.Equal("Completed", campaign.State);
        var frozen = JsonSerializer.Serialize(await Report(c, campaign.Id));
        var export = await c.Client.PostAsync(c.Root + $"/paper/reliability/campaigns/{campaign.Id}/export", null);
        export.EnsureSuccessStatusCode();
        var path = (await export.Content.ReadFromJsonAsync<PaperReliabilityExportResponse>())!.Path;
        var bytes = await File.ReadAllTextAsync(path);
        Assert.Contains("PAPER SIMULATION EVIDENCE ONLY", bytes);
        c.Clock.Now += TimeSpan.FromHours(1);
        await c.Fixture.Services.GetRequiredService<PaperReliabilityCoordinator>().ProcessOnceAsync();
        Assert.Equal(frozen, JsonSerializer.Serialize(await Report(c, campaign.Id)));
        (await c.Client.PostAsync(c.Root + $"/paper/reliability/campaigns/{campaign.Id}/export", null)).EnsureSuccessStatusCode();
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Existing_fixed_and_adaptive_auto_evidence_is_valid_and_boundary_scoped(bool adaptive)
    {
        await using var c = await PaperSizingApiTests.Start(); var before = await c.Execute(c.Request(await c.Preview(1))); Assert.NotNull(before.Execution);
        await PaperAutomationTests.Configure(c, adaptive ? PaperSizingApiTests.Settings : null); var campaign = await Act(c, "start");
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        var financial = await Financial(c); await Act(c, "evaluate", campaign); var report = await Report(c, campaign.Id);
        Assert.Equal(1, report.Counters["AutomaticExecutionsCommitted"]); Assert.Equal(0, report.Counters["ManualExecutionsCommitted"]);
        Assert.All(report.Invariants, i => Assert.Equal("Satisfied", i.State)); Assert.Equal(financial, await Financial(c));
        Assert.Equal(adaptive ? 1 : 0, report.Counters["AdaptiveExecutions"]); Assert.Single(report.Executions);
        Assert.Equal(2, report.Economics.Length); Assert.All(report.Economics, m => Assert.Equal("USD", m.Currency));
        campaign = await Act(c, "complete", campaign); Assert.Equal("Armed", (await PaperAutomationTests.Status(c))!.State);
        var frozen = JsonSerializer.Serialize(await Report(c, campaign.Id)); c.Clock.Now += TimeSpan.FromSeconds(61); PaperAutomationTests.RealtimeBooks(c);
        await c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().ProcessOnceAsync(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        Assert.Equal(frozen, JsonSerializer.Serialize(await Report(c, campaign.Id)));
    }
    [Theory] [InlineData("negative")] [InlineData("risk")] [InlineData("sizing")] [InlineData("fees")] [InlineData("relationship")] [InlineData("orphan")] [InlineData("kill")] [InlineData("backend")]
    public async Task Tampered_durable_facts_produce_sticky_violation_without_repair(string field)
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c, PaperSizingApiTests.Settings); var campaign = await Act(c, "start");
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var e = await db.Set<PaperExecutionEntry>().SingleAsync();
            if (field == "negative") (await db.Set<PaperBalanceEntry>().FirstAsync()).AvailableCash = -1;
            if (field == "risk") e.RiskProofJson = null;
            if (field == "sizing") { var p = JsonSerializer.Deserialize<PaperAutomationProof>(e.AutomationProofJson!)!; e.AutomationProofJson = JsonSerializer.Serialize(p with { Sizing = p.Sizing! with { SelectedQuantity = 3 } }); }
            if (field is "fees" or "relationship") { var p = JsonSerializer.Deserialize<PaperPlan>(e.PlanJson)!; e.PlanJson = JsonSerializer.Serialize(p with { Proof = field == "fees" ? p.Proof with { Fees = null } : p.Proof with { RelationshipTrust = Arbitrage.Application.RelationshipTrust.Manual } }); }
            if (field == "backend") { var arm = await db.Set<PaperWriterOrderEntry>().SingleAsync(x => x.Kind == "AutomationArmed"); var proof = JsonSerializer.Deserialize<ReliabilityArmProof>(arm.ProofJson!)!; arm.ProofJson = JsonSerializer.Serialize(proof with { BackendId = Guid.NewGuid() }); }
            if (field == "orphan") db.Remove(await db.Set<PaperWriterOrderEntry>().SingleAsync(x => x.Kind == "AutomationArmed"));
            if (field == "kill") { var arm = await db.Set<PaperWriterOrderEntry>().SingleAsync(x => x.Kind == "AutomationArmed"); arm.Kind = "KillSwitchLatched"; }
            return await db.SaveChangesAsync();
        });
        var before = await Financial(c); await Act(c, "evaluate", campaign); Assert.Equal("InvariantViolation", (await Report(c, campaign.Id)).State);
        Assert.Equal(before, await Financial(c)); await Act(c, "evaluate", campaign); Assert.Equal("InvariantViolation", (await Report(c, campaign.Id)).State);
    }
    [Fact] public async Task Authorization_revision_paging_and_no_implicit_campaign_or_arming()
    {
        await using var c = await PaperSizingApiTests.Start(); using var anonymous = c.Fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(c.Root + "/paper/reliability/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/reliability/current")).StatusCode);
        var campaign = await Act(c, "start"); Assert.Equal("NotConfigured", (await PaperAutomationTests.Status(c))!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/start", new PaperReliabilityActionRequest(Name: "duplicate"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/pause", new PaperReliabilityActionRequest(campaign.Id, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.GetAsync(c.Root + "/paper/reliability/campaigns?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.PostAsJsonAsync(c.Root + "/paper/reliability/start", new PaperReliabilityActionRequest(Name: new string('x', 101)))).StatusCode);
    }
}
