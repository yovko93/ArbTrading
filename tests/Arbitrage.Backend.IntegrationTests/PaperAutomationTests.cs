using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperAutomationTests
{
    internal static PaperAutomationSettingsRequest Settings => new("FixedQuantity", 1, .005m, .05m, 10, 10, 2, 5, 60, .25m, 20, true, true);
    private static string Path(PaperApiTests.Case c) => c.Root + "/paper/automation";
    internal static Task<PaperAutomationStatusResponse?> Status(PaperApiTests.Case c) => c.Client.GetFromJsonAsync<PaperAutomationStatusResponse>(Path(c) + "/status");
    internal static void RealtimeBooks(PaperApiTests.Case c, BookContinuity kalshi = BookContinuity.Continuous, BookContinuity poly = BookContinuity.BestEffort)
    {
        c.Clock.Now += TimeSpan.FromMilliseconds(1); c.Books(); var cache = c.Fixture.Services.GetRequiredService<OrderBookCache>();
        foreach (var id in new[] { new OrderBookInstrumentId("Kalshi", "a", "yes", "Yes"), new("Polymarket", "b", "456", "No") })
        {
            var old = cache.Read(id).Snapshot!;
            var book = OrderBookNormalizer.Normalize(id, old.Bids, old.Asks, c.Clock.Now, oppositeBids: old.OppositeBids);
            Assert.True(cache.PublishRealtime(id, new(1, RealtimeSubscriptionState.Streaming, id.Exchange == "Kalshi" ? kalshi : poly,
                Connected: true, AnchorAt: c.Clock.Now, LastReceivedAt: c.Clock.Now), book));
        }
    }
    internal static async Task Configure(PaperApiTests.Case c, PaperAutomationSettingsRequest? settings = null, bool realtime = true)
    {
        if (realtime) RealtimeBooks(c);
        var monitor = c.Fixture.Services.GetRequiredService<MonitoringCoordinator>();
        monitor.StartMonitoring(c.Session.UserId, c.Session.DefaultWorkspaceId, new()); await monitor.ProcessOnceAsync();
        (await c.Client.PutAsJsonAsync(Path(c) + "/profile", new SavePaperAutomationRequest(null, true, settings ?? Settings))).EnsureSuccessStatusCode();
    }
    internal static async Task<HttpResponseMessage> Arm(PaperApiTests.Case c, bool confirm = true)
    {
        var s = (await Status(c))!;
        return await c.Client.PostAsJsonAsync(Path(c) + "/arm", new ArmPaperAutomationRequest(s.Profile!.Revision, s.RiskRevision!.Value, s.GenerationId!.Value, s.KillSwitch.Revision, confirm));
    }
    private static Task Cycle(PaperApiTests.Case c) => c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
    private static Task<int> Count(PaperApiTests.Case c) => c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync());
    private static async Task<PaperApiTests.Case> Start()
    {
        var c = new PaperApiTests.Case(); c.Clock.ManualTimers = true; await c.Start(); return c;
    }
    [Fact] public async Task Explicit_arm_exact_fixed_quantity_provenance_dedup_and_reconciliation()
    {
        await using var c = await Start(); Assert.Equal("NotConfigured", (await Status(c))!.State);
        await Configure(c); await Cycle(c); Assert.Equal(0, await Count(c));
        Assert.Equal(HttpStatusCode.Conflict, (await Arm(c, false)).StatusCode);
        (await Arm(c)).EnsureSuccessStatusCode(); Assert.Equal("Armed", (await Status(c))!.State);
        await Cycle(c);
        var s = (await Status(c))!;
        Assert.True(s.ExecutionsCommitted == 1, JsonSerializer.Serialize(s));
        var e = Assert.Single((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        Assert.Equal("AutomaticPaper", e.Origin); Assert.Equal(1, e.Quantity); Assert.Equal(s.SessionId, e.AutomationProof!.SessionId); Assert.NotNull(e.RiskDecision);
        Assert.Equal(s.Profile!.Revision, e.AutomationProof.ProfileRevision); Assert.All(e.Fills, f => Assert.Equal(1, f.Quantity));
        await Cycle(c); await Cycle(c); Assert.Equal(1, await Count(c));
        Assert.True((await Status(c))!.Counters.GetValueOrDefault("DuplicateInputsSuppressed") > 0);
        var reconciliation = await (await c.Client.PostAsync(c.Root + $"/paper/reconcile?generationId={s.GenerationId}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>();
        Assert.Equal("Healthy", reconciliation!.Integrity);
        var audits = await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.Select(a => a.Action).ToArrayAsync());
        Assert.Contains("PaperAutomationProfileConfigured", audits); Assert.Contains("PaperAutomationArmed", audits);
    }
    [Theory] [InlineData(false, true)] [InlineData(true, false)]
    public async Task Rest_or_unacknowledged_best_effort_never_executes(bool realtime, bool allowPoly)
    {
        await using var c = await Start(); await Configure(c, Settings with { AllowPolymarketBestEffort = allowPoly }, realtime);
        (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c); Assert.Equal(0, await Count(c));
        Assert.Equal(1, (await Status(c))!.Counters.GetValueOrDefault("InputQualityRejected"));
    }
    [Theory] [InlineData("session")] [InlineData("budget")] [InlineData("relationship")] [InlineData("hour")]
    public async Task Caps_are_checked_without_quantity_clipping(string cap)
    {
        await using var c = await Start(); var settings = Settings with
        { MaximumExecutionsPerSession = cap == "session" ? 1 : 10, MaximumExecutionsPerRelationshipPerSession = 1,
            MaximumExecutionsPerHour = cap == "hour" ? 1 : 10, MaximumSessionDebitFractionPerBucket = cap == "budget" ? .001m : .25m };
        await Configure(c, settings); (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        Assert.Equal(cap == "budget" ? 0 : 1, await Count(c));
        if (cap == "session") Assert.Equal("Disarmed", (await Status(c))!.State);
        c.Clock.Now += TimeSpan.FromSeconds(61); RealtimeBooks(c); await c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().ProcessOnceAsync(); await Cycle(c);
        Assert.Equal(cap == "budget" ? 0 : 1, await Count(c));
        var s = (await Status(c))!;
        Assert.Contains(cap switch { "session" => "SessionExecutionLimitReached", "budget" => "AutomationSessionDebitLimit", "relationship" => "RelationshipSessionLimit", _ => "HourlyExecutionLimitReached" }, s.StopReason + string.Join(",", s.Counters.Keys));
    }
    [Fact] public async Task Kill_latches_before_return_reset_stays_disarmed_and_manual_execution_remains_explicit()
    {
        await using var c = await Start(); await Configure(c); (await Arm(c)).EnsureSuccessStatusCode();
        (await c.Client.PostAsJsonAsync(Path(c) + "/emergency-stop", new PaperAutomationControlRequest(null, false, "Fixture emergency"))).EnsureSuccessStatusCode();
        await Cycle(c); var s = (await Status(c))!; Assert.True(s.KillSwitch.IsLatched); Assert.Equal("KillSwitchLatched", s.State); Assert.Equal(0, await Count(c));
        Assert.Equal(HttpStatusCode.Conflict, (await Arm(c)).StatusCode);
        var manual = await c.Preview(1); Assert.True(manual.WouldExecute, manual.Rejection); Assert.Equal("Manual", (await c.Execute(c.Request(manual))).Execution!.Origin);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PostAsJsonAsync(Path(c) + "/reset-kill-switch", new PaperAutomationControlRequest(Guid.NewGuid(), true, "Stale reset"))).StatusCode);
        (await c.Client.PostAsJsonAsync(Path(c) + "/reset-kill-switch", new PaperAutomationControlRequest(s.KillSwitch.Revision, true, "Confirmed reset"))).EnsureSuccessStatusCode();
        Assert.Equal("Disarmed", (await Status(c))!.State); await Cycle(c); Assert.Equal(1, await Count(c));
        (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c); Assert.Equal(2, await Count(c));
    }
    [Theory] [InlineData("risk", "RiskPolicyChanged")] [InlineData("reset", "PaperGenerationChanged")]
    [InlineData("monitor", "MonitoringStopped")] [InlineData("mode", "NotPaperMode")]
    public async Task Safety_changes_disarm_without_a_new_candidate(string change, string expected)
    {
        await using var c = await Start(); await Configure(c); (await Arm(c)).EnsureSuccessStatusCode();
        var s = (await Status(c))!;
        if (change == "risk") (await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(s.RiskRevision, true, PaperRiskApiTests.Permissive))).EnsureSuccessStatusCode();
        if (change == "reset") (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, s.GenerationId, "Fixture reset", [new("Kalshi", "USD", 100), new("Polymarket", "USD", 100)]))).EnsureSuccessStatusCode();
        if (change == "monitor") c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().StopMonitoring(c.Session.UserId, c.Session.DefaultWorkspaceId);
        if (change == "mode") c.Fixture.Services.GetRequiredService<LocalOptions>().TradingMode = "Manual";
        await Cycle(c); s = (await Status(c))!; Assert.Equal("Disarmed", s.State); Assert.Equal(expected, s.StopReason); Assert.Equal(0, await Count(c));
    }
    [Fact] public async Task Owner_authorization_profile_revision_and_armed_edit_guards()
    {
        await using var c = await Start(); using var anonymous = c.Fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path(c) + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/automation/status")).StatusCode);
        await Configure(c); var s = (await Status(c))!;
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PutAsJsonAsync(Path(c) + "/profile", new SavePaperAutomationRequest(Guid.NewGuid(), true, Settings))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PutAsJsonAsync(Path(c) + "/profile", new SavePaperAutomationRequest(s.Profile!.Revision, true, Settings with { RequireRealtime = false }))).StatusCode);
        (await Arm(c)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await c.Client.PutAsJsonAsync(Path(c) + "/profile", new SavePaperAutomationRequest(s.Profile.Revision, true, Settings))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Arm(c)).StatusCode);
        (await c.Client.PostAsync(Path(c) + "/disarm", null)).EnsureSuccessStatusCode(); await Cycle(c); Assert.Equal(0, await Count(c));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.PostAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/automation/emergency-stop", new PaperAutomationControlRequest(null, false, "Nonmember blocked"))).StatusCode);
    }
    [Theory] [InlineData("profile", "NotConfigured")] [InlineData("risk", "RiskNotConfigured")]
    [InlineData("generation", "GenerationUnavailable")] [InlineData("integrity", "IntegrityFailure")]
    public async Task Arm_prerequisites_fail_closed(string missing, string expected)
    {
        await using var c = await Start(); await Configure(c); var s = (await Status(c))!;
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            if (missing == "profile") db.Remove(await db.Set<PaperAutomationProfileEntry>().SingleAsync());
            if (missing == "risk") db.Remove(await db.Set<PaperRiskProfileEntry>().SingleAsync());
            if (missing == "generation") (await db.Set<PaperGenerationEntry>().SingleAsync()).ClosedAt = c.Clock.Now;
            if (missing == "integrity") (await db.Set<PaperGenerationEntry>().SingleAsync()).Integrity = PaperIntegrity.Corrupt;
            return await db.SaveChangesAsync();
        });
        var response = await c.Client.PostAsJsonAsync(Path(c) + "/arm", new ArmPaperAutomationRequest(s.Profile!.Revision, s.RiskRevision!.Value, s.GenerationId!.Value, null, true));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        await Cycle(c); Assert.Equal(0, await Count(c));
    }
    [Fact] public async Task Coalescing_and_overlapping_worker_cycles_are_bounded_and_shutdown_stops_entries()
    {
        await using var c = await Start(); await Configure(c, Settings with { MaximumCandidatesPerCycle = 1 }); (await Arm(c)).EnsureSuccessStatusCode();
        var worker = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>();
        for (var i = 0; i < 1000; i++) worker.Notify(c.Session.DefaultWorkspaceId);
        Assert.Equal(1, worker.Runtime(c.Session.DefaultWorkspaceId).QueueDepth);
        await Task.WhenAll(Cycle(c), Cycle(c), Cycle(c)); Assert.Equal(1, await Count(c));
        Assert.True(worker.Runtime(c.Session.DefaultWorkspaceId).Counters["CandidatesCoalesced"] >= 999);
        await worker.StopAsync(default); c.Clock.Now += TimeSpan.FromSeconds(61); RealtimeBooks(c);
        await c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().ProcessOnceAsync(); await Cycle(c);
        Assert.Equal(1, await Count(c)); Assert.Equal(PaperAutomationReason.BackendStopped, worker.Runtime(c.Session.DefaultWorkspaceId).StopReason);
        Assert.Equal(HttpStatusCode.Conflict, (await Arm(c)).StatusCode);
    }
    [Fact] public async Task Cooldowns_survive_rearming_and_new_input_only_executes_after_elapsed_interval()
    {
        await using var c = await Start(); await Configure(c); (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c); Assert.Equal(1, await Count(c));
        var first = (await Status(c))!.SessionId;
        (await c.Client.PostAsync(Path(c) + "/disarm", null)).EnsureSuccessStatusCode(); (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        Assert.NotEqual(first, (await Status(c))!.SessionId); Assert.Equal(1, await Count(c));
        Assert.Equal(1, (await Status(c))!.Counters.GetValueOrDefault("CooldownRejected"));
        c.Clock.Now += TimeSpan.FromSeconds(61); RealtimeBooks(c); await c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().ProcessOnceAsync(); await Cycle(c);
        Assert.Equal(2, await Count(c));
    }
    [Fact] public async Task Automatic_and_manual_entries_have_identical_financial_facts()
    {
        await using var c = await Start(); await Configure(c); var preview = await c.Preview(1); Assert.True(preview.WouldExecute);
        (await Arm(c)).EnsureSuccessStatusCode(); await Cycle(c); var status = (await Status(c))!; Assert.Equal(1, status.ExecutionsCommitted);
        var automatic = Assert.Single((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        var balances = (await c.Account()).Balances.Select(b => (b.Exchange, b.Currency, b.AvailableCash)).ToArray();
        var positions = (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!.Select(p => (p.Exchange, p.Currency, p.Quantity, p.CostBasis, p.Fees, p.AverageEntry)).ToArray();
        (await c.Client.PostAsync(Path(c) + "/disarm", null)).EnsureSuccessStatusCode();
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, status.GenerationId, "Equivalent manual fixture", [new("Kalshi", "USD", 100), new("Polymarket", "USD", 100)]))).EnsureSuccessStatusCode();
        var manual = (await c.Execute(c.Request(await c.Preview(1)))).Execution!; Assert.Equal("Manual", manual.Origin);
        Assert.Equal(automatic.Cost, manual.Cost); Assert.Equal(.9268m, automatic.Cost);
        Assert.Equal(automatic.Fills.Select(f => (f.Exchange, f.Quantity, f.Price, f.Notional, f.Fee, f.Currency)), manual.Fills.Select(f => (f.Exchange, f.Quantity, f.Price, f.Notional, f.Fee, f.Currency)));
        Assert.Equal(balances, (await c.Account()).Balances.Select(b => (b.Exchange, b.Currency, b.AvailableCash)).ToArray());
        Assert.Equal(positions.OrderBy(p => p.Exchange), (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!.Select(p => (p.Exchange, p.Currency, p.Quantity, p.CostBasis, p.Fees, p.AverageEntry)).OrderBy(p => p.Exchange));
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            async Task<string[]> Ledger(Guid id)
            {
                var tx = await db.Set<PaperTransactionEntry>().Where(t => t.ExecutionId == id).Select(t => t.Id).SingleAsync();
                var rows = await db.Set<PaperLedgerEntry>().Where(l => l.TransactionId == tx).ToArrayAsync();
                return rows.Select(l => $"{l.Exchange}|{l.Currency}|{l.Reason}|{l.AvailableDelta}|{l.ReservedDelta}").Order(StringComparer.Ordinal).ToArray();
            }
            Assert.Equal(await Ledger(automatic.Id), await Ledger(manual.Id)); return 0;
        });
    }
}
