using System.Net;
using System.Net.Http.Json;
using System.Text;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Arbitrage.Backend;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MarketCatalogApiTests
{
    private sealed class PagesHandler(string exchange, bool cycle, bool terminalOmitted = false) : HttpMessageHandler
    {
        private int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (exchange == "Polymarket") Assert.Contains("limit=100", request.RequestUri!.Query);
            var first = Interlocked.Increment(ref requests) == 1;
            var id = exchange == "Kalshi" ? "ticker" : "id";
            var cursor = exchange == "Kalshi" ? "cursor" : "next_cursor";
            var body = first
                ? "{\"markets\":[{\"" + id + "\":\"stored\",\"status\":\"active\"}],\"" + cursor + "\":\"next\"}"
                : terminalOmitted
                    ? "{\"markets\":[{\"id\":\"terminal\"}]}"
                    : cycle
                    ? "{\"markets\":[{\"" + id + "\":\"stored\"}],\"" + cursor + "\":\"next\"}"
                    : "{\"markets\":[],\"" + cursor + "\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Polymarket_documented_omitted_terminal_cursor_completes_and_persists_both_pages()
    {
        using var http = new HttpClient(new PagesHandler("Polymarket", false, terminalOmitted: true));
        await using var app = new BackendFixture(services =>
        {
            services.RemoveAll<IMarketDiscoverySource>();
            services.AddSingleton<IMarketDiscoverySource>(new PolymarketMarketSource(http));
        });
        using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var basePath = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog";
        await client.PostAsJsonAsync(basePath + "/sync", new StartMarketSyncRequest("Polymarket"));
        CatalogStatusResponse? status = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            status = await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status", timeout.Token);
            if (status!.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun?.State == "Complete") break;
            await Task.Delay(20, timeout.Token);
        }
        var result = status!.Exchanges.Single(s => s.Exchange == "Polymarket");
        Assert.Equal("Complete", result.LatestRun!.State);
        Assert.Equal(2, result.StoredMarkets);
        Assert.NotNull(result.LastCompletedAt);
        var page = (await client.GetFromJsonAsync<MarketPageResponse>(basePath + "/markets?page=1&pageSize=10"))!;
        Assert.Contains(page.Items, m => m.NativeId == "stored");
        Assert.Contains(page.Items, m => m.NativeId == "terminal");
    }

    [Theory]
    [InlineData("Kalshi", false)]
    [InlineData("Polymarket", false)]
    [InlineData("Kalshi", true)]
    [InlineData("Polymarket", true)]
    public async Task Production_parser_bad_or_repeated_second_cursor_keeps_first_page_and_no_completion(string exchange, bool cycle)
    {
        using var http = new HttpClient(new PagesHandler(exchange, cycle));
        IMarketDiscoverySource source = exchange == "Kalshi" ? new KalshiMarketSource(http) : new PolymarketMarketSource(http);
        await using var app = new BackendFixture(services =>
        {
            services.RemoveAll<IMarketDiscoverySource>();
            services.AddSingleton(source);
        });
        using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var basePath = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog";
        await client.PostAsJsonAsync(basePath + "/sync", new StartMarketSyncRequest(exchange));
        CatalogStatusResponse? status = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            status = await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status", timeout.Token);
            if (status!.Exchanges.Single(s => s.Exchange == exchange).LatestRun?.State == "Failed") break;
            await Task.Delay(20, timeout.Token);
        }
        var result = status!.Exchanges.Single(s => s.Exchange == exchange);
        Assert.Equal("Failed", result.LatestRun!.State);
        Assert.Equal(cycle ? "CursorStalled" : "InvalidEnvelope", result.LatestRun.ErrorCode);
        Assert.Equal(1, result.StoredMarkets);
        Assert.Null(result.LastCompletedAt);
    }
    private sealed class ControlledSource : IMarketDiscoverySource
    {
        public string Exchange => "Polymarket";
        public IReadOnlyList<string> Scopes => ["nonfinalized"];
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public async Task<MarketDiscoveryPage> ReadPageAsync(string scope, string? cursor, int pageSize,
            DateTimeOffset deadline, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests); Entered.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
            return new([new DiscoveredMarket("Polymarket", "Production", "1", null, null, null,
                null, "Stored question", null, null, ["Politics"], "open", "Open", [],
                null, null, null, null, null, null, null, "Rules", null, DateTimeOffset.UtcNow, [])],
                null, 0, []);
        }
    }
    private static BackendFixture App(ControlledSource source) => new(services =>
    {
        services.RemoveAll<IMarketDiscoverySource>();
        services.AddSingleton<IMarketDiscoverySource>(source);
    });

    private sealed class CursorSource(bool cycle) : IMarketDiscoverySource
    {
        public string Exchange => "Polymarket";
        public IReadOnlyList<string> Scopes => ["nonfinalized"];
        public Task<MarketDiscoveryPage> ReadPageAsync(string scope, string? cursor, int pageSize,
            DateTimeOffset deadline, CancellationToken ct)
        {
            var index = cycle ? 0 : cursor switch { null => 0, "next-1" => 1, "next-2" => 2, _ => 1 };
            var count = cycle ? 1 : index == 2 ? 301 : 400;
            var markets = Enumerable.Range(index * 400, count).Select(i => new DiscoveredMarket(
                "Polymarket", "Production", i.ToString(), null, null, null, null,
                "Question " + i, null, null, [], "open", "Open", [], null, null, null,
                null, null, null, null, null, null, DateTimeOffset.UtcNow, [])).ToArray();
            return Task.FromResult(new MarketDiscoveryPage(markets,
                cycle ? "repeat" : index == 2 ? null : "next-" + (index + 1), 0, []));
        }
    }

    [Theory]
    [InlineData(false, "Complete", 1101)]
    [InlineData(true, "Failed", 1)]
    public async Task Worker_traverses_beyond_one_thousand_or_reports_cursor_cycle(bool cycle, string outcome, int expected)
    {
        var source = new CursorSource(cycle);
        await using var app = new BackendFixture(services =>
        {
            services.RemoveAll<IMarketDiscoverySource>();
            services.AddSingleton<IMarketDiscoverySource>(source);
        });
        using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var basePath = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog";
        await client.PostAsJsonAsync(basePath + "/sync", new StartMarketSyncRequest("Polymarket"));
        CatalogStatusResponse? status = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            status = await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status", timeout.Token);
            if (status!.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun?.State is "Complete" or "Failed") break;
            await Task.Delay(20, timeout.Token);
        }
        var result = status!.Exchanges.Single(s => s.Exchange == "Polymarket");
        Assert.Equal(outcome, result.LatestRun!.State);
        Assert.Equal(expected, result.StoredMarkets);
        if (cycle) Assert.Equal("CursorStalled", result.LatestRun.ErrorCode);
        else Assert.Equal(expected, result.LatestRun.MarketsObserved);
    }

    [Fact]
    public async Task Authenticated_catalog_is_empty_until_explicit_sync_and_duplicate_admission_reuses_run()
    {
        var source = new ControlledSource();
        await using var app = App(source);
        using var anonymous = app.CreateClient(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var basePath = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(basePath + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/catalog/status")).StatusCode);
        var before = (await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status"))!;
        Assert.All(before.Exchanges, s => Assert.Equal(0, s.StoredMarkets));
        Assert.Equal(0, source.Requests); // Opening/reading never starts external discovery.
        var firstResponse = await client.PostAsJsonAsync(basePath + "/sync", new StartMarketSyncRequest("Polymarket"));
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<StartMarketSyncResponse>())!.Runs.Single();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = (await (await client.PostAsJsonAsync(basePath + "/sync",
            new StartMarketSyncRequest("Polymarket"))).Content.ReadFromJsonAsync<StartMarketSyncResponse>())!.Runs.Single();
        Assert.Equal(first.Id, second.Id); Assert.Equal(1, source.Requests);
        var cancellation = await client.PostAsync(basePath + $"/sync/{first.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Equal("Cancelled", (await cancellation.Content.ReadFromJsonAsync<DiscoveryRunResponse>())!.State);
        var after = (await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status"))!;
        Assert.Equal("Cancelled", after.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun!.State);
        Assert.Equal(0, after.Exchanges.Single(s => s.Exchange == "Polymarket").StoredMarkets);
    }

    [Fact]
    public async Task Cancellation_checks_job_owner_and_duplicate_admission_does_not_take_over_job()
    {
        var source = new ControlledSource();
        await using var app = App(source);
        using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var coordinator = app.Services.GetRequiredService<MarketDiscoveryCoordinator>();
        var foreign = (await coordinator.StartRunsAsync("Polymarket", Guid.NewGuid(),
            session.DefaultWorkspaceId, default)).Single();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var path = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog/sync";
        var visible = (await client.GetFromJsonAsync<CatalogStatusResponse>(
            $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog/status"))!;
        Assert.Null(visible.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(path, new StartMarketSyncRequest("Polymarket"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync(path + $"/{foreign.Id}/cancel", null)).StatusCode);
        source.Release.TrySetResult(true);
    }

    [Fact]
    public async Task Completed_sync_updates_offline_page_and_notifies_scope_without_exposing_execution()
    {
        var source = new ControlledSource();
        await using var app = App(source);
        using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var basePath = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/catalog";
        var started = (await (await client.PostAsJsonAsync(basePath + "/sync",
            new StartMarketSyncRequest("Polymarket"))).Content.ReadFromJsonAsync<StartMarketSyncResponse>())!.Runs.Single();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Release.TrySetResult(true);
        CatalogStatusResponse? status = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            status = await client.GetFromJsonAsync<CatalogStatusResponse>(basePath + "/status", timeout.Token);
            if (status!.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun?.State == "Complete") break;
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal("Complete", status!.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRun!.State);
        Assert.Equal(1, status.Exchanges.Single(s => s.Exchange == "Polymarket").StoredMarkets);
        Assert.Equal("Unavailable", status.Exchanges.Single(s => s.Exchange == "Polymarket").ExecutionCapability);
        Assert.NotNull(status.Exchanges.Single(s => s.Exchange == "Polymarket").LastCompletedAt);
        Assert.NotNull(status.Exchanges.Single(s => s.Exchange == "Polymarket").LatestRetrievedAt);
        var page = (await client.GetFromJsonAsync<MarketPageResponse>(basePath + "/markets?search=Stored&page=1&pageSize=1"))!;
        Assert.Equal(1, page.Total); Assert.Equal("1", page.Items.Single().NativeId);
        var details = (await client.GetFromJsonAsync<MarketResponse>(basePath + "/markets/Polymarket/1"))!;
        Assert.Equal("Rules", details.Rules);
        Assert.Contains("non-finalized", page.ScopeNotice);
        Assert.Equal(1, source.Requests); // Paging/detail are SQLite-only.
    }
}
