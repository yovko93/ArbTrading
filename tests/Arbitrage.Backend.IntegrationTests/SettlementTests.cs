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

public sealed class SettlementTests
{
    internal static async Task<PaperExecutionResponse> Execute(PaperApiTests.Case c)
    { var p = await c.Preview(); Assert.True(p.WouldExecute, p.Rejection); return (await c.Execute(c.Request(p))).Execution!; }
    internal static async Task<PaperResolutionPreviewResponse> Preview(PaperApiTests.Case c, Guid g, string exchange = "Kalshi", string market = "a", string winner = "yes") =>
        (await (await c.Client.PostAsJsonAsync(c.Root + "/paper/resolutions/preview", new PaperResolutionSelection(g, exchange, market, winner))).Content.ReadFromJsonAsync<PaperResolutionPreviewResponse>())!;
    internal static ConfirmResolutionRequest Request(PaperResolutionPreviewResponse p) => new(Guid.NewGuid(), p.PreviewId, p.Selection, true);
    internal static async Task<PaperResolutionCommitResponse> Confirm(PaperApiTests.Case c, ConfirmResolutionRequest r) =>
        (await (await c.Client.PostAsJsonAsync(c.Root + "/paper/resolutions/confirm", r)).Content.ReadFromJsonAsync<PaperResolutionCommitResponse>())!;
    internal static async Task<PaperResolutionResponse> Settle(PaperApiTests.Case c, Guid g, string exchange = "Kalshi", string market = "a", string winner = "yes")
    { var p = await Preview(c, g, exchange, market, winner); Assert.True(p.WouldSettle, p.RejectionReason); var r = await Confirm(c, Request(p)); Assert.Equal("None", r.Rejection); return r.Resolution!; }
    internal static async Task<string> Reconcile(PaperApiTests.Case c, Guid g) =>
        (await (await c.Client.PostAsync(c.Root + $"/paper/reconcile?generationId={g}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>())!.Integrity;
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Exact_basket_async_settlement_payout_and_opposite_winner_preserve_invariant(bool aWins)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c); var g = e.GenerationId;
        var p = await Preview(c, g, winner: aWins ? "yes" : "no"); Assert.True(p.WouldSettle, p.RejectionReason);
        Assert.Empty(await c.Fixture.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().ToArrayAsync()));
        Assert.Equal(aWins ? 10 : 0, Assert.Single(p.CashCredits).Payout);
        Assert.Equal(aWins ? 5.832m : -4.168m, Assert.Single(p.AffectedPositions).RealizedPnl);
        Assert.Null(Assert.Single(p.AffectedExecutions).Economics.FinalRealizedProfit);
        var first = await Confirm(c, Request(p)); Assert.NotNull(first.Resolution); Assert.Equal("Healthy", await Reconcile(c, g));
        var partial = (await c.Client.GetFromJsonAsync<PaperExecutionResponse>(c.Root + $"/paper/executions/{e.Id}"))!;
        Assert.Equal("PartiallySettled", partial.State); Assert.Null(partial.Settlement!.FinalRealizedProfit); Assert.Equal(5.19992m, partial.Settlement.RemainingOpenCostBasis);
        Assert.Single((await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!);
        await Settle(c, g, "Polymarket", "b", aWins ? "123" : "456");
        var final = (await c.Client.GetFromJsonAsync<PaperExecutionResponse>(c.Root + $"/paper/executions/{e.Id}"))!;
        Assert.Equal("Settled", final.State); Assert.Equal(10, final.Settlement!.RealizedPayoutToDate); Assert.Equal(.63208m, final.Settlement.FinalRealizedProfit);
        Assert.Equal(.63208m / 9.36792m, final.Settlement.RealizedReturnOnCost); Assert.Equal(0, final.Settlement.ExpectedVsRealizedDifference);
        Assert.Empty((await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!);
        var history = (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions?status=Settled"))!;
        Assert.Equal(2, history.Length); Assert.Equal(.63208m, history.Sum(p => p.RealizedPnl)); Assert.Equal("Healthy", await Reconcile(c, g));
        var balances = (await c.Account()).Balances;
        Assert.Equal(95.832m + (aWins ? 10 : 0), balances.Single(b => b.Exchange == "Kalshi").AvailableCash);
        Assert.Equal(94.80008m + (aWins ? 0 : 10), balances.Single(b => b.Exchange == "Polymarket").AvailableCash);
        var curve = (await c.Client.GetFromJsonAsync<PaperCurveResponse>(c.Root + $"/paper/performance/curve?generationId={g}&exchange=Kalshi&currency=USD"))!;
        Assert.Equal(aWins ? 105.832m : 95.832m, curve.Points.Last().RealizedPerformance);
        Assert.Equal(balances.Single(b => b.Exchange == "Kalshi").AvailableCash, curve.Points.Last().CashAvailable);
        Assert.Equal(4, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperTransactionEntry>().CountAsync()));
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperLedgerEntry>().CountAsync(l => l.Reason == "PaperSettlement")));
    }
    [Fact] public async Task Immutable_idempotency_and_contradiction_do_not_mutate_ledger()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c);
        var p = await Preview(c, e.GenerationId); var request = Request(p); var first = await Confirm(c, request);
        c.Clock.Now += TimeSpan.FromMinutes(2);
        var duplicate = await Confirm(c, request); Assert.True(duplicate.Duplicate); Assert.Equal(first.Resolution!.Id, duplicate.Resolution!.Id);
        Assert.Equal("IdempotencyConflict", (await Confirm(c, request with { Selection = request.Selection with { WinningInstrumentId = "no" } })).Rejection);
        Assert.Equal("MarketAlreadyResolved", (await Preview(c, e.GenerationId, winner: "no")).RejectionReason);
        Assert.Equal("ResolutionContradictsExecutionProof", (await Preview(c, e.GenerationId, "Polymarket", "b", "456")).RejectionReason);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().CountAsync()));
        await Settle(c, e.GenerationId, "Polymarket", "b", "123"); Assert.Equal("Healthy", await Reconcile(c, e.GenerationId));
    }
    [Theory] [InlineData("expiry", "PreviewExpired")] [InlineData("quantity", "PreviewChanged")] [InlineData("generation", "GenerationNotFound")]
    [InlineData("integrity", "IntegrityFailure")] [InlineData("winner", "OutcomeInvalid")] [InlineData("unsupported", "SettlementTypeUnsupported")]
    public async Task Revalidation_rejects_changed_or_unsupported_state(string change, string expected)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c); var p = await Preview(c, e.GenerationId);
        if (change == "expiry") c.Clock.Now += TimeSpan.FromSeconds(31);
        if (change == "quantity") await Execute(c);
        if (change == "integrity") await c.Fixture.WithDatabaseAsync(async db => { (await db.Set<PaperGenerationEntry>().SingleAsync()).Integrity = PaperIntegrity.Corrupt; return await db.SaveChangesAsync(); });
        if (change == "unsupported") await c.Fixture.WithDatabaseAsync(async db => { var row = await db.Set<PaperExecutionEntry>().SingleAsync(); var m = JsonSerializer.Deserialize<SettlementMarket[]>(row.SettlementMarketsJson)!;
            row.SettlementMarketsJson = JsonSerializer.Serialize(m.Select(x => x with { Structure = "NegativeRisk" })); return await db.SaveChangesAsync(); });
        var rejection = change switch { "generation" => (await Preview(c, Guid.NewGuid())).RejectionReason,
            "winner" => (await Preview(c, e.GenerationId, winner: "untrusted-label")).RejectionReason, _ => (await Confirm(c, Request(p))).Rejection };
        Assert.Equal(expected, rejection); Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().CountAsync()));
    }
    [Fact] public async Task No_price_or_status_inference_and_settlement_uses_no_current_sources()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c);
        c.Fixture.Services.GetRequiredService<Arbitrage.Application.OrderBookCache>().Store(Arbitrage.Domain.OrderBookNormalizer.Normalize(
            new("Kalshi", "a", "yes", "Yes"), [], [new(.9999m, 10, Arbitrage.Domain.LiquidityOrigin.NativeAsk)], c.Clock.Now));
        await c.Fixture.WithDatabaseAsync(async db => { foreach (var m in await db.CatalogMarkets.ToArrayAsync()) { m.Status = "Finalized"; m.OutcomesJson = "[]"; }
            (await db.MarketRelationships.SingleAsync()).State = Arbitrage.Domain.VerificationState.Rejected;
            db.FeeSchedules.RemoveRange(await db.FeeSchedules.ToArrayAsync()); return await db.SaveChangesAsync(); });
        c.Clock.Now += TimeSpan.FromDays(30);
        Assert.Equal(2, (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!.Length);
        Assert.Empty((await c.Client.GetFromJsonAsync<PaperResolutionResponse[]>(c.Root + "/paper/resolutions"))!);
        await Settle(c, e.GenerationId); await Settle(c, e.GenerationId, "Polymarket", "b", "123");
        Assert.Equal("Healthy", await Reconcile(c, e.GenerationId)); // Case disposal asserts every network spy stayed at zero.
    }
    [Fact] public async Task Closed_generation_settlement_and_read_paging_leave_active_cash_unchanged()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c);
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, e.GenerationId, "Another scenario", [new("Kalshi", "USD", 200), new("Polymarket", "USDC", 300)]))).EnsureSuccessStatusCode();
        await Settle(c, e.GenerationId); await Settle(c, e.GenerationId, "Polymarket", "b", "123");
        var account = await c.Account(); Assert.Equal(200, account.Balances.Single(b => b.Currency == "USD").AvailableCash); Assert.Equal(300, account.Balances.Single(b => b.Currency == "USDC").AvailableCash);
        var old = (await c.Client.GetFromJsonAsync<PaperPerformanceResponse>(c.Root + $"/paper/performance?generationId={e.GenerationId}"))!; Assert.Equal("FullySettled", old.Lifecycle);
        var current = (await c.Client.GetFromJsonAsync<PaperPerformanceResponse>(c.Root + $"/paper/performance?generationId={account.Generation!.Id}"))!;
        Assert.Equal(2, current.Buckets.Length); Assert.All(current.Buckets, b => Assert.Equal(0, b.CumulativeRealizedPnl));
        Assert.Empty((await c.Client.GetFromJsonAsync<PaperResolutionResponse[]>(c.Root + "/paper/resolutions?page=2"))!);
        var curve = (await c.Client.GetFromJsonAsync<PaperCurveResponse>(c.Root + $"/paper/performance/curve?generationId={e.GenerationId}&exchange=Kalshi&currency=USD&pageSize=1"))!; Assert.Single(curve.Points); Assert.True(curve.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.GetAsync(c.Root + $"/paper/performance/curve?generationId={e.GenerationId}&exchange=Kalshi&currency=USD&pageSize=1001")).StatusCode);
    }
    [Theory] [InlineData("payout")] [InlineData("pnl")] [InlineData("status")] [InlineData("ledger")] [InlineData("mapping")] [InlineData("execution")] [InlineData("request")] [InlineData("duplicate-mapping")]
    public async Task Reconciliation_detects_settlement_corruption_without_repair(string mutation)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c); await Settle(c, e.GenerationId);
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var p = await db.Set<PaperPositionEntry>().SingleAsync(p => p.Exchange == "Kalshi");
            if (mutation == "payout") p.SettlementPayout++;
            if (mutation == "pnl") p.RealizedPnl++;
            if (mutation == "status") p.Status = PaperPositionStatus.Open;
            if (mutation == "ledger") (await db.Set<PaperLedgerEntry>().SingleAsync(l => l.Reason == "PaperSettlement")).AvailableDelta++;
            if (mutation == "mapping") (await db.Set<PaperResolutionOutcomeEntry>().FirstAsync()).PayoutPerShare = .5m;
            if (mutation == "duplicate-mapping") { var o = await db.Set<PaperResolutionOutcomeEntry>().FirstAsync(); db.Add(new PaperResolutionOutcomeEntry { ResolutionId = o.ResolutionId, InstrumentId = "extra", Outcome = o.Outcome, PayoutPerShare = o.PayoutPerShare }); }
            if (mutation == "execution") (await db.Set<PaperExecutionEntry>().SingleAsync()).State = PaperExecutionState.Settled;
            if (mutation == "request") (await db.Set<PaperResolutionEntry>().SingleAsync()).RequestFingerprint = "tampered";
            return await db.SaveChangesAsync();
        });
        Assert.Equal("Corrupt", await Reconcile(c, e.GenerationId)); Assert.Equal("IntegrityFailure", (await Preview(c, e.GenerationId, "Polymarket", "b", "123")).RejectionReason);
    }
    [Fact] public async Task Journal_failure_rolls_back_resolution_positions_cash_and_audit()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c); var p = await Preview(c, e.GenerationId);
        var ticket = c.Fixture.Services.GetRequiredService<SettlementMemory>().Find(p.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        await c.Fixture.WithDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_settlement BEFORE INSERT ON PaperTransactionEntry WHEN NEW.Reason = 'PaperSettlement' BEGIN SELECT RAISE(ABORT, 'isolated journal failure'); END"));
        await using (var scope = c.Fixture.Services.CreateAsyncScope())
        {
            var input = new ResolutionConfirmation(Guid.NewGuid(), p.PreviewId, ticket.Selection, true);
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<PaperStore>().CommitResolutionAsync(c.Session.UserId,
                c.Session.DefaultWorkspaceId, input, ticket.Projection.Fingerprint, ticket.CreatedAt, "rollback-fixture", default));
        }
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().CountAsync()));
        Assert.Equal(2, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperPositionEntry>().CountAsync(p => p.Status == PaperPositionStatus.Open)));
        Assert.Equal(2, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperTransactionEntry>().CountAsync()));
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync(a => a.Action == "PaperResolutionCommitted")));
        Assert.Equal(95.832m, (await c.Account()).Balances.Single(b => b.Exchange == "Kalshi").AvailableCash);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_identical_or_conflicting_resolution_has_one_financial_commit(bool different)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c);
        var r1 = Request(await Preview(c, e.GenerationId)); var r2 = different ? Request(await Preview(c, e.GenerationId, winner: "no")) : r1;
        using var barrier = new Barrier(2);
        Task<PaperResolutionCommitResponse> Send(ConfirmResolutionRequest r) => Task.Run(async () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return await Confirm(c, r); });
        var results = await Task.WhenAll(Send(r1), Send(r2));
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().CountAsync()));
        if (different) Assert.Single(results, r => r.Rejection == "MarketAlreadyResolved"); else Assert.Single(results, r => r.Duplicate);
        Assert.Equal("Healthy", await Reconcile(c, e.GenerationId));
    }
    [Fact] public async Task Resolution_races_execution_and_reset_without_cross_generation_credit()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await Execute(c);
        var rp = Request(await Preview(c, e.GenerationId)); var ep = c.Request(await c.Preview());
        using var barrier = new Barrier(2);
        var settle = Task.Run(async () => { barrier.SignalAndWait(); return await Confirm(c, rp); });
        var execute = Task.Run(async () => { barrier.SignalAndWait(); return await c.Execute(ep); });
        await Task.WhenAll(settle, execute);
        var settled = await settle; var executed = await execute;
        Assert.True(settled.Resolution is not null && executed.Rejection == "MarketAlreadyResolved" || settled.Rejection == "PreviewChanged" && executed.Execution is not null);
        if (settled.Resolution is null) await Settle(c, e.GenerationId);
        var second = Request(await Preview(c, e.GenerationId, "Polymarket", "b", "123"));
        using var resetBarrier = new Barrier(2);
        var settleSecond = Task.Run(async () => { resetBarrier.SignalAndWait(); return await Confirm(c, second); });
        var reset = Task.Run(async () => { resetBarrier.SignalAndWait(); return await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, e.GenerationId, "Race", [new("Kalshi", "USD", 500)])); });
        await Task.WhenAll(settleSecond, reset); (await reset).EnsureSuccessStatusCode(); Assert.NotNull((await settleSecond).Resolution);
        Assert.Equal(500, Assert.Single((await c.Account()).Balances).AvailableCash); Assert.Equal("Healthy", await Reconcile(c, e.GenerationId));
    }
}
