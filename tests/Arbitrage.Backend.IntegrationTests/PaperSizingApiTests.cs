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

public sealed class PaperSizingApiTests
{
    internal static PaperAutomationSettingsRequest Settings => PaperAutomationTests.Settings with
    { SizingMode = "LargestAdmissibleGridQuantity", MinimumQuantity = 5, MaximumQuantity = 25, QuantityStep = 5 };
    internal static async Task<PaperApiTests.Case> Start()
    { var c = new PaperApiTests.Case(); c.Clock.ManualTimers = true; await c.Start(); return c; }
    internal static async Task<PaperSizingPreviewResponse> Preview(PaperApiTests.Case c)
    {
        var response = await c.Client.PostAsJsonAsync(c.Root + "/paper/automation/sizing-preview", new PaperSizingPreviewRequest(c.Key));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<PaperSizingPreviewResponse>())!;
    }
    private static Task Cycle(PaperApiTests.Case c) => c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().ProcessOnceAsync();
    [Fact] public async Task Full_depth_exact_fees_preview_read_only_execution_proof_and_same_market_dedup()
    {
        await using var c = await Start(); await PaperAutomationTests.Configure(c, Settings);
        var account = JsonSerializer.Serialize(await c.Account()); var audits = await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync());
        var books = c.Fixture.Services.GetRequiredService<OrderBookCache>(); var version = books.Read(new("Kalshi", "a", "yes", "Yes")).Version;
        var preview = await Preview(c); Assert.Equal("Selected", preview.State); Assert.Equal(25, preview.SelectedQuantity); Assert.Equal(1, preview.CandidatesEvaluated);
        var manual = await c.Preview(25); Assert.True(manual.WouldExecute, manual.Rejection);
        Assert.Equal(23.35m, preview.GrossCost); Assert.Equal(manual.TotalFees, preview.TotalFees); Assert.Equal(manual.FeeAdjustedCost, preview.Cost);
        Assert.Equal(manual.ExpectedProfitAtResolution, preview.ExpectedProfit); Assert.Equal("Approved", preview.RiskDecision!.Decision);
        Assert.Equal(account, JsonSerializer.Serialize(await c.Account())); Assert.Equal(audits, await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync()));
        await Cycle(c); Assert.Empty((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        var execution = Assert.Single((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        Assert.Equal(25, execution.Quantity); Assert.Equal(preview.Cost, execution.Cost);
        var proof = execution.AutomationProof!.Sizing!; Assert.Equal(25, proof.SelectedQuantity); Assert.Equal(2, proof.Version); Assert.Equal(1, proof.CandidatesEvaluated);
        Assert.Equal(2, execution.AutomationProof.ProfileVersion); Assert.Equal(64, proof.DecisionFingerprint.Length);
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, execution.GenerationId));
        await Cycle(c); Assert.Single((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        Assert.Equal(version, books.Read(new("Kalshi", "a", "yes", "Yes")).Version);
        var status = (await PaperAutomationTests.Status(c))!; Assert.Equal("Selected", status.LastSizingState); Assert.Equal(25, status.LastSelectedQuantity);
    }
    [Theory] [InlineData("risk", 10)] [InlineData("budget", 10)] [InlineData("edge", 15)] [InlineData("profit", 25)]
    [InlineData("no-admissible", 0)] [InlineData("minimum", 0)]
    public async Task Exact_quantity_dependent_rejections_continue_down_grid(string limitation, decimal selected)
    {
        await using var c = await Start(); var settings = Settings;
        if (limitation == "risk") await PaperRiskApiTests.Save(c, PaperRiskApiTests.Permissive with { MaximumMarketCostBasisFraction = .063m, MaximumInstrumentCostBasisFraction = .063m });
        if (limitation == "budget") settings = settings with { MaximumSessionDebitFractionPerBucket = .063m };
        if (limitation == "minimum") settings = settings with { MaximumSessionDebitFractionPerBucket = .023m };
        if (limitation == "edge") settings = settings with { MaximumQuantity = 20, MinimumFeeAdjustedEdgePerShare = .049m };
        if (limitation == "profit") settings = settings with { MinimumFeeAdjustedProfit = .5m };
        if (limitation == "no-admissible") settings = settings with { MinimumFeeAdjustedProfit = 100 };
        await PaperAutomationTests.Configure(c, settings); var d = await Preview(c);
        Assert.True(d.SelectedQuantity == (selected == 0 ? null : selected), JsonSerializer.Serialize(d));
        Assert.Equal(selected == 0 ? "NoAdmissibleQuantity" : "Selected", d.State);
        if (selected == 10) { Assert.Equal(4, d.CandidatesEvaluated); Assert.Equal(3, d.Rejections.Sum(r => r.Count)); }
        if (limitation == "risk") { Assert.True((await c.Preview(12)).RiskApproved); Assert.False((await c.Preview(13)).RiskApproved); }
        if (limitation == "profit") Assert.True((await c.Preview(5)).ExpectedProfitAtResolution < settings.MinimumFeeAdjustedProfit);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        var history = (await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!;
        if (selected == 0) Assert.Empty(history); else Assert.Equal(selected, Assert.Single(history).Quantity);
    }
    [Fact] public async Task Thirteen_depth_selects_ten_on_five_grid()
    {
        await using var c = await Start(); await PaperAutomationTests.Configure(c, Settings with { MaximumQuantity = 20 });
        var id = new OrderBookInstrumentId("Polymarket", "b", "456", "No"); c.Clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.True(c.Fixture.Services.GetRequiredService<OrderBookCache>().PublishRealtime(id,
            new(1, RealtimeSubscriptionState.Streaming, BookContinuity.BestEffort, Connected: true, AnchorAt: c.Clock.Now, LastReceivedAt: c.Clock.Now),
            OrderBookNormalizer.Normalize(id, [], [new(.5m, 5, LiquidityOrigin.NativeAsk), new(.52m, 8, LiquidityOrigin.NativeAsk)], c.Clock.Now)));
        await c.Fixture.Services.GetRequiredService<MonitoringCoordinator>().ProcessOnceAsync(); var d = await Preview(c);
        Assert.True(d.SelectedQuantity == 10, JsonSerializer.Serialize(d)); Assert.Equal(3, d.CandidatesEvaluated);
        Assert.Contains(d.Rejections, r => r.Reason == "InsufficientDepth" && r.Count == 2);
    }
    [Theory] [InlineData(256, true)] [InlineData(257, false)]
    public async Task Profile_cardinality_is_validated_at_api(decimal maximum, bool allowed)
    {
        await using var c = await Start(); await PaperAutomationTests.Configure(c);
        var previous = (await PaperAutomationTests.Status(c))!.Profile!;
        var response = await c.Client.PutAsJsonAsync(c.Root + "/paper/automation/profile", new SavePaperAutomationRequest(previous.Revision, true,
            Settings with { MinimumQuantity = 1, MaximumQuantity = maximum, QuantityStep = 1 }));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
    }
    [Fact] public async Task Preview_authentication_and_membership_required_and_kill_stops_sizing()
    {
        await using var c = await Start(); await PaperAutomationTests.Configure(c, Settings); using var anonymous = c.Fixture.CreateClient();
        var request = new PaperSizingPreviewRequest(c.Key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(c.Root + "/paper/automation/sizing-preview", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.PostAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/automation/sizing-preview", request)).StatusCode);
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/automation/emergency-stop", new PaperAutomationControlRequest(null, false, "fixture"))).EnsureSuccessStatusCode();
        var response = await c.Client.PostAsJsonAsync(c.Root + "/paper/automation/sizing-preview", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("KillSwitchLatched", await response.Content.ReadAsStringAsync());
        await Cycle(c); Assert.Equal(0, (await PaperAutomationTests.Status(c))!.Counters.GetValueOrDefault("AdaptiveSizingAttempts"));
    }
    [Theory] [InlineData("quantity")] [InlineData("grid")] [InlineData("count")] [InlineData("financial")] [InlineData("fingerprint")] [InlineData("risk")]
    public async Task Adaptive_proof_corruption_is_detected(string field)
    {
        await using var c = await Start(); await PaperAutomationTests.Configure(c, Settings);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        var status = (await PaperAutomationTests.Status(c))!;
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var e = await db.Set<PaperExecutionEntry>().SingleAsync(); var proof = JsonSerializer.Deserialize<PaperAutomationProof>(e.AutomationProofJson!)!;
            var sizing = proof.Sizing!;
            sizing = field switch { "quantity" => sizing with { SelectedQuantity = 20 }, "grid" => sizing with { MinimumQuantity = 1 },
                "count" => sizing with { CandidatesEvaluated = 2 }, "financial" => sizing with { FinancialRevision = 99 },
                "risk" => sizing with { RiskPolicyRevision = Guid.NewGuid() }, _ => sizing with { DecisionFingerprint = new string('A', 64) } };
            e.AutomationProofJson = JsonSerializer.Serialize(proof with { Sizing = sizing }); return await db.SaveChangesAsync();
        });
        Assert.Equal("Corrupt", await SettlementTests.Reconcile(c, status.GenerationId!.Value));
    }
    [Fact] public async Task Worker_defers_third_256_grid_search_and_processes_it_next_cycle_in_rank_order()
    {
        await using var c = await Start();
        foreach (var suffix in new[] { "2", "3" })
        {
            await OpportunityApiTests.Seed(c.Fixture, VerificationState.VerifiedDeterministic, suffix);
            await c.Fixture.WithDatabaseAsync(async db =>
            {
                var fees = new FeeStore(db);
                await fees.SaveAsync(FeeApiTests.Schedule("Kalshi", "a" + suffix) with { RetrievedAt = c.Clock.Now }, default);
                await fees.SaveAsync(FeeApiTests.Schedule("Polymarket", "b" + suffix) with { RetrievedAt = c.Clock.Now }, default); return 0;
            });
            var cache = c.Fixture.Services.GetRequiredService<OrderBookCache>();
            foreach (var id in new[] { new OrderBookInstrumentId("Kalshi", "a" + suffix, "yes", "Yes"), new("Polymarket", "b" + suffix, "456", "No") })
                Assert.True(cache.PublishRealtime(id, new(1, RealtimeSubscriptionState.Streaming, id.Exchange == "Kalshi" ? BookContinuity.Continuous : BookContinuity.BestEffort,
                    Connected: true, AnchorAt: c.Clock.Now, LastReceivedAt: c.Clock.Now),
                    OrderBookNormalizer.Normalize(id, [], [new(id.Exchange == "Kalshi" ? .4m : .5m, 300, LiquidityOrigin.NativeAsk)], c.Clock.Now)));
        }
        await PaperAutomationTests.Configure(c, Settings with { MinimumQuantity = 1, MaximumQuantity = 256, QuantityStep = 1, MinimumFeeAdjustedProfit = 1000 });
        var monitor = c.Fixture.Services.GetRequiredService<MonitoringCoordinator>(); var ranked = monitor.AutomaticPaperCandidates(c.Session.DefaultWorkspaceId, 20);
        Assert.Equal(3, ranked.Length);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); await Cycle(c);
        var first = (await PaperAutomationTests.Status(c))!;
        Assert.Equal(512, first.Counters.GetValueOrDefault("AdaptiveCandidatesEvaluated")); Assert.Equal(2, first.CandidatesConsidered);
        Assert.Equal(1, first.QueueDepth); Assert.Equal("EvaluationBudgetExceeded", first.LastSizingState);
        await Cycle(c); var second = (await PaperAutomationTests.Status(c))!;
        Assert.Equal(768, second.Counters.GetValueOrDefault("AdaptiveCandidatesEvaluated")); Assert.Equal(3, second.CandidatesConsidered); Assert.Equal(0, second.QueueDepth);
        Assert.Equal(0, second.ExecutionsCommitted); Assert.Equal(3, second.Counters.GetValueOrDefault("AdaptiveNoAdmissible"));
    }
}
