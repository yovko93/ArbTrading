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
public sealed class ValuationApiTests
{
    [Fact] public async Task Actual_backend_restart_retains_inventory_but_no_current_marks()
    {
        string root, path;
        await using (var c = new PaperApiTests.Case(preserveStorage: true))
        { await c.Start(); await SettlementTests.Execute(c); Bids(c); Assert.All((await Read(c)).Positions, p => Assert.Equal("Marked", p.MarkStatus)); root = c.Fixture.Root; path = c.Root; }
        await using var restarted = new BackendFixture(root: root); using var client = await restarted.AuthenticatedClientAsync();
        var r = (await client.GetFromJsonAsync<PaperValuationResponse>(path + "/paper/valuation"))!;
        Assert.Equal(2, r.Positions.Length); Assert.All(r.Positions, p => Assert.Equal("BookUnavailable", p.MarkStatus));
        Assert.Equal(9.36792m, r.Positions.Sum(p => p.Position.CostBasis));
    }
    [Fact] public async Task Authorized_other_workspace_cannot_read_generation_or_position_and_no_initialization_occurs()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c); var p = (await Read(c)).Positions[0]; var other = Guid.NewGuid();
        await c.Fixture.WithDatabaseAsync(async db => { db.Add(new Workspace(other, "Other", c.Clock.Now)); db.Add(new WorkspaceMembership(c.Session.UserId, other)); return await db.SaveChangesAsync(); });
        var path = $"/api/v1/workspaces/{other}/paper";
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync(path + $"/valuation?generationId={e.GenerationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync(path + $"/positions/{p.Position.Id}/valuation")).StatusCode);
        var empty = (await c.Client.GetFromJsonAsync<PaperValuationResponse>(path + "/valuation"))!; Assert.Equal("Uninitialized", empty.State); Assert.Empty(empty.Buckets);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperGenerationEntry>().CountAsync()));
    }
    [Fact] public async Task Oversized_generation_rejects_instead_of_summarizing_a_truncated_portfolio()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var g = (await c.Account()).Generation!.Id;
        await c.Fixture.WithDatabaseAsync(async db => { db.AddRange(Enumerable.Range(0, 1001).Select(i => new PaperPositionEntry {
            Id = Guid.NewGuid(), GenerationId = g, Exchange = "Kalshi", MarketId = "M" + i, InstrumentId = "yes", Outcome = "Yes", Currency = "USD", Quantity = 1, CostBasis = .5m, AverageEntry = .5m })); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.GetAsync(c.Root + "/paper/valuation")).StatusCode);
    }
    private static void Bids(PaperApiTests.Case c) => c.Fixture.Services.GetRequiredService<OrderBookCache>().Store(
        OrderBookNormalizer.Normalize(new("Polymarket", "b", "456", "No"), [new(.6m, 10, LiquidityOrigin.NativeBid)], [], c.Clock.Now));
    private static async Task<PaperValuationResponse> Read(PaperApiTests.Case c, string query = "") =>
        (await c.Client.GetFromJsonAsync<PaperValuationResponse>(c.Root + "/paper/valuation" + query))!;
    private static Task<string> FinancialSnapshot(PaperApiTests.Case c) => c.Fixture.WithDatabaseAsync(async db => JsonSerializer.Serialize(new {
        Generations = await db.Set<PaperGenerationEntry>().AsNoTracking().ToArrayAsync(), Balances = await db.Set<PaperBalanceEntry>().AsNoTracking().ToArrayAsync(),
        Positions = await db.Set<PaperPositionEntry>().AsNoTracking().ToArrayAsync(), Executions = await db.Set<PaperExecutionEntry>().AsNoTracking().ToArrayAsync(),
        Transactions = await db.Set<PaperTransactionEntry>().AsNoTracking().ToArrayAsync(), Ledger = await db.Set<PaperLedgerEntry>().AsNoTracking().ToArrayAsync(),
        Resolutions = await db.Set<PaperResolutionEntry>().AsNoTracking().ToArrayAsync(), Audit = await db.AuditRecords.AsNoTracking().ToArrayAsync() }));
    [Fact] public async Task Repeated_reads_never_mutate_financial_truth_or_acquire_external_data()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c); Bids(c);
        var before = await FinancialSnapshot(c);
        for (var i = 0; i < 3; i++)
        {
            var r = await Read(c); Assert.All(r.Positions, p => Assert.Equal("Marked", p.MarkStatus));
            Assert.Equal(6m, r.Positions.Single(p => p.Position.Exchange == "Polymarket").GrossLiquidationValue);
            Assert.Equal(100.80008m, r.Buckets.Single(b => b.Exchange == "Polymarket").GrossMarkedEquity);
            Assert.Equal(.80008m, r.Buckets.Single(b => b.Exchange == "Polymarket").GrossTotalPnl);
            var id = r.Positions[0].Position.Id;
            (await c.Client.GetAsync(c.Root + $"/paper/positions/{id}/valuation")).EnsureSuccessStatusCode();
            (await c.Client.GetAsync(c.Root + "/paper/risk")).EnsureSuccessStatusCode();
        }
        Assert.Equal(before, await FinancialSnapshot(c)); Assert.Equal("Healthy", await SettlementTests.Reconcile(c, e.GenerationId));
        c.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.All((await Read(c)).Positions, p => { Assert.Equal("BookStale", p.MarkStatus); Assert.Null(p.GrossLiquidationValue); });
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, e.GenerationId));
    }
    [Fact] public async Task Partial_settlement_excludes_settled_inventory_and_history_remains_realized_only()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c); Bids(c);
        await SettlementTests.Settle(c, e.GenerationId);
        var r = await Read(c); var k = r.Positions.Single(p => p.Position.Exchange == "Kalshi");
        Assert.Equal("NotApplicable", k.MarkStatus); Assert.Null(k.GrossLiquidationValue); Assert.Equal(5.832m, k.Position.RealizedPnl);
        var b = r.Buckets.Single(b => b.Exchange == "Kalshi"); Assert.Equal(0, b.OpenPositionCount); Assert.Equal(105.832m, b.GrossMarkedEquity); Assert.Equal(5.832m, b.GrossTotalPnl);
        Assert.Equal(1, r.Buckets.Single(b => b.Exchange == "Polymarket").OpenPositionCount);
    }
    [Fact] public async Task Historical_generation_native_mapping_and_paging_are_isolated()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c); Bids(c);
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, e.GenerationId, "Historical fixture", [new("Kalshi", "USD", 20), new("Polymarket", "USDC", 30)]))).EnsureSuccessStatusCode();
        var active = await Read(c); Assert.Empty(active.Positions); Assert.Equal(new[] { "USD", "USDC" }, active.Buckets.OrderBy(b => b.Exchange).Select(b => b.Currency));
        var old = await Read(c, $"?generationId={e.GenerationId}&pageSize=1"); Assert.True(old.HistoricalGeneration); Assert.True(old.HasMore); Assert.Single(old.Positions); Assert.Equal(2, old.Buckets.Length);
        (await c.Client.GetAsync(c.Root + $"/paper/positions/{old.Positions[0].Position.Id}/valuation")).EnsureSuccessStatusCode();
        var next = await Read(c, $"?generationId={e.GenerationId}&pageSize=1&page=2"); Assert.False(next.HasMore); Assert.NotEqual(old.Positions[0].Position.Id, next.Positions[0].Position.Id);
        await c.Fixture.WithDatabaseAsync(async db => { (await db.CatalogMarkets.SingleAsync(m => m.Exchange == "Polymarket")).OutcomesJson = "[]"; return await db.SaveChangesAsync(); });
        var changed = await Read(c, $"?generationId={e.GenerationId}"); Assert.Equal("InstrumentUnsupported", changed.Positions.Single(p => p.Position.Exchange == "Polymarket").MarkStatus);
        Assert.Null(changed.Buckets.Single(b => b.Exchange == "Polymarket").GrossMarkedEquity);
        Assert.Equal(20m, (await Read(c)).Buckets.Single(b => b.Exchange == "Kalshi").CurrentCash);
    }
    [Fact] public async Task Fee_conflict_is_fail_closed_without_disabling_gross()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); await SettlementTests.Execute(c); Bids(c);
        await c.Fixture.WithDatabaseAsync(async db => { var s = new FeeStore(db); var f = await s.ReadAsync("Kalshi", "a", default); await s.SaveAsync(f! with { VerificationIssue = "Official contract discrepancy" }, default); return 0; });
        var r = await Read(c); var k = r.Positions.Single(p => p.Position.Exchange == "Kalshi");
        Assert.Equal("Marked", k.MarkStatus); Assert.Equal("InvalidFeeMetadata", k.ExitFeeStatus); Assert.Null(k.FeeAdjustedLiquidationValue);
        Assert.NotNull(r.Buckets.Single(b => b.Exchange == "Kalshi").GrossMarkedEquity); Assert.Null(r.Buckets.Single(b => b.Exchange == "Kalshi").FeeAdjustedMarkedEquity);
    }
    [Theory] [InlineData("/valuation")] [InlineData("/risk")] [InlineData("/positions/00000000-0000-0000-0000-000000000001/valuation")]
    public async Task Authorization_and_cross_workspace_denial(string path)
    {
        await using var c = new PaperApiTests.Case(); await c.Start();
        using var anonymous = c.Fixture.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(c.Root + "/paper" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper" + path)).StatusCode);
    }
    [Fact] public async Task Unknown_generation_bounds_and_no_implicit_initialization()
    {
        await using var c = new PaperApiTests.Case(); await c.Start();
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync(c.Root + $"/paper/valuation?generationId={Guid.NewGuid()}")).StatusCode);
        foreach (var query in new[] { "page=0", "pageSize=0", "pageSize=1001", "page=100001" }) Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.GetAsync(c.Root + "/paper/valuation?" + query)).StatusCode);
        Assert.Empty((await Read(c)).Positions);
    }
    [Fact] public async Task Empty_cache_after_restart_keeps_positions_and_does_not_persist_marks()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); await SettlementTests.Execute(c);
        using var scope = c.Fixture.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<PaperStore>();
        var empty = new OrderBookCache(c.Clock); var reader = new PaperValuationCoordinator(store, empty, c.Clock);
        var r = await reader.ReadAsync(c.Session.DefaultWorkspaceId, null, 1, 100, default);
        Assert.All(r!.Positions, p => Assert.Equal("BookUnavailable", p.MarkStatus));
        Assert.All(r.Buckets, b => Assert.Null(b.GrossMarkedEquity));
        empty.Store(OrderBookNormalizer.Normalize(new("Polymarket", "b", "456", "No"), [new(.6m, 10, LiquidityOrigin.NativeBid)], [], c.Clock.Now));
        r = await reader.ReadAsync(c.Session.DefaultWorkspaceId, null, 1, 100, default); Assert.Contains(r!.Positions, p => p.MarkStatus == "Marked");
    }
    private sealed class BarrierClock(TimeProvider inner) : TimeProvider
    {
        public Action? OnRead;
        public override DateTimeOffset GetUtcNow() { var action = OnRead; OnRead = null; action?.Invoke(); return inner.GetUtcNow(); }
    }
    [Fact] public async Task Deterministic_book_change_after_capture_is_never_published_as_current()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); await SettlementTests.Execute(c);
        using var scope = c.Fixture.Services.CreateScope(); var cache = c.Fixture.Services.GetRequiredService<OrderBookCache>();
        using var captured = new ManualResetEventSlim(); using var changed = new ManualResetEventSlim();
        var clock = new BarrierClock(c.Clock) { OnRead = () => { captured.Set(); Assert.True(changed.Wait(TimeSpan.FromSeconds(10))); } };
        var reader = new PaperValuationCoordinator(scope.ServiceProvider.GetRequiredService<PaperStore>(), cache, clock);
        var read = Task.Run(() => reader.ReadAsync(c.Session.DefaultWorkspaceId, null, 1, 100, default));
        Assert.True(captured.Wait(TimeSpan.FromSeconds(10))); Bids(c); changed.Set();
        var r = await read; Assert.Equal("BookChangedDuringValuation", r!.Positions.Single(p => p.Position.Exchange == "Polymarket").MarkStatus);
        Assert.Null(r.Buckets.Single(b => b.Exchange == "Polymarket").GrossMarkedEquity);
    }
    [Theory] [InlineData("full")] [InlineData("missing")] [InlineData("partial")] [InlineData("fees")]
    public void Bucket_completeness_concentration_and_exact_equity(string condition)
    {
        var g = Guid.NewGuid(); PaperMark M(string market, decimal cost, decimal value) => new(new(Guid.NewGuid(), g, new("Kalshi", market, "yes", "Yes"), "USD", PaperPositionStatus.Open, 10, cost, 1, .5m, null, null), PaperMarkStatus.Marked, PaperMarkQuality.FreshRest, DateTimeOffset.UtcNow)
        { GrossLiquidationValue = value, FeeAdjustedLiquidationValue = value - 1 };
        var a = M("a", 12, 20); var b = M("b", 8, 15);
        if (condition is "missing" or "partial") b = b with { MarkStatus = condition == "partial" ? PaperMarkStatus.PartialDepth : PaperMarkStatus.BookUnavailable, GrossLiquidationValue = null, FeeAdjustedLiquidationValue = null };
        if (condition == "fees") b = b with { FeeAdjustedLiquidationValue = null };
        var r = PaperValuationCoordinator.Summarize(new() { GenerationId = g, Exchange = "Kalshi", Currency = "USD", InitialCash = 100, AvailableCash = 60 }, [a, b]);
        Assert.Equal(80, r.AccountingBookValue); Assert.Equal(.2m, r.CapitalUtilizationByCost); Assert.Equal(.6m, r.LargestPositionCostShare); Assert.Equal(2, r.UniqueMarketCount);
        if (condition is "full" or "fees") Assert.Equal(95m, r.GrossMarkedEquity); else { Assert.Null(r.GrossMarkedEquity); Assert.Equal(20m, r.KnownGrossMarkedOpenValue); Assert.Equal(.5m, r.ValuationCoverage); }
        if (condition == "full") Assert.Equal(93m, r.FeeAdjustedMarkedEquity); else Assert.Null(r.FeeAdjustedMarkedEquity);
    }
}
