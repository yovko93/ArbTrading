using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Connectors;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperApiTests
{
    internal sealed class Clock : TimeProvider
    {
        public bool ManualTimers;
        public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            ManualTimers ? new ManualTimer() : base.CreateTimer(callback, state, dueTime, period);
        private sealed class ManualTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class NetworkSpy(string exchange) : IOrderBookSource, IMarketDiscoverySource, IPublicFeeSource, IRelationshipMetadataSource, IMarketWebSocketFactory
    {
        public int Calls; public string Exchange => exchange; public IReadOnlyList<string> Scopes => ["all"];
        private Exception Deny() { Interlocked.Increment(ref Calls); return new InvalidOperationException("Unexpected external acquisition in paper test."); }
        public Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken ct) => throw Deny();
        public Task<MarketDiscoveryPage> ReadPageAsync(string scope, string? cursor, int pageSize, DateTimeOffset deadline, CancellationToken ct) => throw Deny();
        public Task<FeeSchedule> ReadAsync(string exchange, string market, CancellationToken ct) => throw Deny();
        public Task<RelationshipMetadata> ReadAsync(string exchange, string nativeId, string? eventId, CancellationToken ct) => throw Deny();
        public IMarketWebSocket Create() => throw Deny();
    }
    internal sealed class Case : IAsyncDisposable
    {
        public readonly Clock Clock = new();
        private readonly NetworkSpy kalshi = new("Kalshi"), poly = new("Polymarket");
        public BackendFixture Fixture { get; }
        public HttpClient Client { get; private set; } = null!;
        public SessionResponse Session { get; private set; } = null!;
        public Guid Relationship { get; private set; }
        public string Key { get; private set; } = "";
        public string Root => $"/api/v1/workspaces/{Session.DefaultWorkspaceId}";
        public Case(bool preserveStorage = false) => Fixture = new(s =>
        {
            s.AddSingleton<TimeProvider>(Clock); s.RemoveAll<IOrderBookSource>(); s.RemoveAll<IMarketDiscoverySource>();
            s.AddSingleton<IOrderBookSource>(kalshi); s.AddSingleton<IOrderBookSource>(poly);
            s.AddSingleton<IMarketDiscoverySource>(kalshi); s.AddSingleton<IMarketDiscoverySource>(poly);
            s.AddSingleton<IPublicFeeSource>(kalshi); s.AddSingleton<IRelationshipMetadataSource>(kalshi); s.AddSingleton<IMarketWebSocketFactory>(kalshi);
        }, preserveStorage: preserveStorage);
        public async Task Start(decimal money = 100, string currency = "USD", bool unresolved = false, bool manual = false, bool configureRisk = true)
        {
            Client = await Fixture.AuthenticatedClientAsync(); Session = (await Client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
            Relationship = await OpportunityApiTests.Seed(Fixture, manual ? VerificationState.VerifiedManual : VerificationState.VerifiedDeterministic);
            Clock.Now = DateTimeOffset.UtcNow;
            await Fixture.WithDatabaseAsync(async db =>
            {
                var fees = new FeeStore(db);
                await fees.SaveAsync(FeeApiTests.Schedule("Kalshi", "a") with { RetrievedAt = Clock.Now, VerificationIssue = unresolved ? "Unresolved public schedule conflict" : null }, default);
                await fees.SaveAsync(FeeApiTests.Schedule("Polymarket", "b") with { RetrievedAt = Clock.Now, Currency = currency }, default);
                await fees.SetProfileAsync(Session.DefaultWorkspaceId, KalshiFeeAccountProfile.DirectMember, default); return 0;
            });
            Books();
            var job = await OpportunityApiTests.Start(Client, Root + "/opportunities", new(Relationship, IncludeManualRelationships: manual, EvaluateFees: true));
            var page = (await Client.GetFromJsonAsync<OpportunityPageResponse>($"{Root}/opportunities/jobs/{job.Id}/results?diagnostics=true"))!;
            Key = Assert.Single(page.Items).OpportunityKey;
            using var response = await Client.PostAsJsonAsync(Root + "/paper/account/initialize", new InitializePaperRequest(true, null, "Explicit isolated fixture funding",
                [new("Kalshi", "USD", money), new("Polymarket", currency, money)])); response.EnsureSuccessStatusCode();
            if (configureRisk) (await Client.PutAsJsonAsync(Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(null, true, PaperRiskApiTests.Permissive))).EnsureSuccessStatusCode();
        }
        public void Books()
        {
            var cache = Fixture.Services.GetRequiredService<OrderBookCache>();
            cache.Store(OrderBookNormalizer.NormalizeBinary(new("Kalshi", "a", "yes", "Yes"), [new(.1m, 100, LiquidityOrigin.NativeBid)],
                [new(.60m, 10, LiquidityOrigin.NativeBid), new(.57m, 20, LiquidityOrigin.NativeBid)], Clock.Now));
            cache.Store(OrderBookNormalizer.Normalize(new("Polymarket", "b", "456", "No"), [], [new(.5m, 5, LiquidityOrigin.NativeAsk), new(.52m, 20, LiquidityOrigin.NativeAsk)], Clock.Now));
        }
        public async Task<PaperPreviewResponse> Preview(decimal qty = 10) => (await (await Client.PostAsJsonAsync(Root + "/paper/preview", new PaperPreviewRequest(Key, qty))).Content.ReadFromJsonAsync<PaperPreviewResponse>())!;
        public ConfirmPaperRequest Request(PaperPreviewResponse p) => new(Guid.NewGuid(), p.PreviewId, Key, p.RequestedQuantity, true);
        public async Task<PaperCommitResponse> Execute(ConfirmPaperRequest request) =>
            (await (await Client.PostAsJsonAsync(Root + "/paper/execute", request)).Content.ReadFromJsonAsync<PaperCommitResponse>())!;
        public async Task<PaperAccountResponse> Account() => (await Client.GetFromJsonAsync<PaperAccountResponse>(Root + "/paper/account"))!;
        public async ValueTask DisposeAsync() { Client?.Dispose(); await Fixture.DisposeAsync(); Assert.Equal(0, kalshi.Calls); Assert.Equal(0, poly.Calls); }
    }
    [Fact] public async Task Exact_persisted_basket_fees_cash_positions_history_idempotency_and_reconciliation()
    {
        await using var c = new Case(); await c.Start();
        var preview = await c.Preview(); Assert.True(preview.WouldExecute, preview.Rejection); Assert.Equal(9.36792m, preview.FeeAdjustedCost);
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        var request = c.Request(preview); var result = await c.Execute(request); Assert.Equal("Committed", result.State);
        Assert.Equal(3, result.Execution!.Fills.Length); Assert.Equal(.63208m, result.Execution.ExpectedProfitAtResolution);
        Assert.Contains(result.Execution.Fills, f => f.LiquidityOrigin == "DerivedComplement" && f.NativeLiquidityIdentity.InstrumentId == "no");
        var account = await c.Account(); Assert.Equal(95.832m, account.Balances.Single(b => b.Exchange == "Kalshi").AvailableCash);
        Assert.Equal(94.80008m, account.Balances.Single(b => b.Exchange == "Polymarket").AvailableCash); Assert.All(account.Balances, b => Assert.Equal(0, b.ReservedCash));
        var positions = (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!;
        Assert.Equal(2, positions.Length); Assert.All(positions, p => Assert.Equal(10, p.Quantity)); Assert.Equal(9.36792m, positions.Sum(p => p.CostBasis));
        var history = (await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!; Assert.Single(history);
        var reconcile = await (await c.Client.PostAsync(c.Root + $"/paper/reconcile?generationId={account.Generation!.Id}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>();
        Assert.Equal("Healthy", reconcile!.Integrity);
        // Retained idempotency is checked before ephemeral preview expiry and is independent of cache state.
        c.Clock.Now += TimeSpan.FromMinutes(10); var duplicate = await c.Execute(request);
        Assert.True(duplicate.Duplicate); Assert.Equal(result.Execution.Id, duplicate.Execution!.Id);
        Assert.Equal("DuplicateRequest", (await c.Execute(request with { Quantity = 11 })).Rejection);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
    }
    [Theory] [InlineData("book", "MarketDataChanged")] [InlineData("relationship", "RelationshipIneligible")]
    [InlineData("fee", "FeeChanged")] [InlineData("expired", "PreviewExpired")]
    [InlineData("mode", "NotPaperMode")] [InlineData("reset", "GenerationChanged")]
    public async Task Changed_proofs_or_environment_reject_without_financial_mutation(string change, string expected)
    {
        await using var c = new Case(); await c.Start(); var preview = await c.Preview(); Assert.True(preview.WouldExecute, preview.Rejection);
        if (change == "book") c.Books();
        if (change == "expired") c.Clock.Now += TimeSpan.FromSeconds(6);
        if (change == "mode") c.Fixture.Services.GetRequiredService<LocalOptions>().TradingMode = "Manual";
        if (change == "relationship") await c.Fixture.WithDatabaseAsync(async db => { (await db.MarketRelationships.SingleAsync()).UpdatedAt += TimeSpan.FromSeconds(1); return await db.SaveChangesAsync(); });
        if (change == "fee") await c.Fixture.WithDatabaseAsync(async db => { await new FeeStore(db).SetProfileAsync(c.Session.DefaultWorkspaceId, KalshiFeeAccountProfile.NonDirectMember, default); return 0; });
        if (change == "reset") (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, preview.GenerationId, "Reset race fixture", [new("Kalshi", "USD", 100), new("Polymarket", "USD", 100)]))).EnsureSuccessStatusCode();
        Assert.Equal(expected, (await c.Execute(c.Request(preview))).Rejection);
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperPositionEntry>().CountAsync()));
        Assert.All((await c.Account()).Balances, b => Assert.Equal(100, b.AvailableCash));
    }
    [Theory] [InlineData("funds", "InsufficientPaperFunds")] [InlineData("fees", "FeeModelUnresolved")]
    [InlineData("currency", "CurrencyModelUnsupported")] [InlineData("manual", "ManualRelationshipNotAllowed")]
    [InlineData("depth", "InsufficientDepth")] [InlineData("quantity", "RequestedQuantityInvalid")]
    public async Task Preview_rejections_are_read_only(string problem, string expected)
    {
        await using var c = new Case(); await c.Start(problem == "funds" ? 5 : 100, problem == "currency" ? "USDC" : "USD", problem == "fees", problem == "manual");
        var before = await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync());
        var p = await c.Preview(problem == "depth" ? 30 : problem == "quantity" ? -1 : 10);
        Assert.False(p.WouldExecute); Assert.Equal(expected, p.Rejection);
        if (problem == "funds") Assert.Equal("InsufficientPaperFunds", (await c.Execute(c.Request(p))).Rejection);
        Assert.Equal(before, await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync()));
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
    }
    [Fact] public async Task Reset_preserves_history_and_integrity_fault_blocks_new_execution()
    {
        await using var c = new Case(); await c.Start(); var p = await c.Preview(); var execution = (await c.Execute(c.Request(p))).Execution!;
        await c.Fixture.WithDatabaseAsync(async db => { (await db.Set<PaperBalanceEntry>().FirstAsync()).AvailableCash += 1; return await db.SaveChangesAsync(); });
        var integrity = await (await c.Client.PostAsync(c.Root + $"/paper/reconcile?generationId={p.GenerationId}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>();
        Assert.Equal("Corrupt", integrity!.Integrity); Assert.Equal("IntegrityFailure", (await c.Preview()).Rejection);
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, p.GenerationId, "Explicit new generation after diagnostic failure", [new("Kalshi", "USD", 500), new("Polymarket", "USD", 500)]))).EnsureSuccessStatusCode();
        var a = await c.Account(); Assert.Equal(2, a.History.Length); Assert.NotEqual(p.GenerationId, a.Generation!.Id);
        Assert.Empty((await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!);
        Assert.Single((await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!);
        Assert.Equal(execution.Id, (await c.Client.GetFromJsonAsync<PaperExecutionResponse>($"{c.Root}/paper/executions/{execution.Id}"))!.Id);
        Assert.Equal(2, (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>($"{c.Root}/paper/positions?generationId={p.GenerationId}"))!.Length);
    }
    [Fact] public async Task Authorization_and_untrusted_fields_fail_closed()
    {
        await using var c = new Case(); await c.Start(); using var anonymous = c.Fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(c.Root + "/paper/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/account")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.PostAsJsonAsync(c.Root + "/paper/preview", new { c.Key, quantity = 10, userId = Guid.NewGuid(), price = .01m })).StatusCode);
        var unknown = await (await c.Client.PostAsJsonAsync(c.Root + "/paper/preview", new PaperPreviewRequest(new string('A', 64), 10))).Content.ReadFromJsonAsync<PaperPreviewResponse>();
        Assert.Equal("OpportunityNotFound", unknown!.Rejection);
        var p = await c.Preview(); Assert.Equal("ConfirmationRequired", (await c.Execute(c.Request(p) with { ConfirmSimulation = false })).Rejection);
        await c.Fixture.WithDatabaseAsync(async db => { db.Memberships.Remove(await db.Memberships.SingleAsync()); await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProfiles"); return await db.SaveChangesAsync(); });
        // Removing the required local profile makes authentication storage unavailable before authorization.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.Client.PostAsJsonAsync(c.Root + "/paper/execute", c.Request(p))).StatusCode);
    }
}
