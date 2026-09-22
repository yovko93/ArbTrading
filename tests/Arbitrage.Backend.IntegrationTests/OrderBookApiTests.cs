using System.Net;
using System.Net.Http.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class OrderBookApiTests
{
    private sealed class Source(string exchange = "Kalshi") : IOrderBookSource
    {
        public string Exchange => exchange;
        public OrderBookRequest? LastRequest;
        public int Calls; public string? Failure; public bool Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken ct)
        {
            Calls++; LastRequest = request; Entered.TrySetResult(); if (Block) await Release.Task.WaitAsync(ct);
            if (Failure is not null) throw new MarketDiscoveryException(Failure, "Test failure");
            return OrderBookNormalizer.Normalize(request.Instrument, [new(.3m, 2, LiquidityOrigin.NativeBid)],
                request.BinarySupported ? [new(.4m, 3, LiquidityOrigin.DerivedComplement)] : [], DateTimeOffset.UtcNow,
                oppositeBids: [new(.6m, 3, LiquidityOrigin.NativeBid)], supported: request.BinarySupported);
        }
    }
    private static BackendFixture Fixture(Source source) => new(services =>
    { services.RemoveAll<IOrderBookSource>(); services.AddSingleton<IOrderBookSource>(source); });
    private static async Task<string> Seed(BackendFixture app, HttpClient client, string? classification = "binary")
    {
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        await app.WithDatabaseAsync(async db =>
        {
            db.CatalogMarkets.Add(new MarketCatalogEntry { Exchange = "Kalshi", NativeId = "TEST", Classification = classification,
                OutcomesJson = """[{"Label":"Yes","NativeTokenId":null},{"Label":"No","NativeTokenId":null}]""", RetrievedAt = DateTimeOffset.UtcNow });
            db.CatalogMarkets.Add(new MarketCatalogEntry { Exchange = "Polymarket", NativeId = "MISSING", OutcomesJson = "[]" });
            return await db.SaveChangesAsync();
        });
        return $"/api/v1/workspaces/{session.DefaultWorkspaceId}/orderbooks/Kalshi/TEST";
    }
    [Fact]
    public async Task Cache_and_preview_are_offline_explicit_refresh_preserves_snapshot_after_failure()
    {
        var source = new Source(); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client);
        var empty = (await client.GetFromJsonAsync<OrderBookResponse>(path))!;
        Assert.Equal("Unavailable", empty.State); Assert.Null(empty.Snapshot); Assert.Equal(0, source.Calls);
        var preview = await client.PostAsJsonAsync(path + "/depth", new DepthPreviewRequest("yes", "Buy", 1));
        Assert.False((await preview.Content.ReadFromJsonAsync<GrossDepthResponse>())!.IsActionable); Assert.Equal(0, source.Calls);
        var refresh = await client.PostAsync(path + "/refresh?instrumentId=yes", null);
        var book = (await refresh.Content.ReadFromJsonAsync<OrderBookResponse>())!;
        Assert.True(book.IsActionable); Assert.Equal(1, source.Calls); Assert.Equal("DerivedComplement", book.Snapshot!.Asks[0].Origin);
        preview = await client.PostAsJsonAsync(path + "/depth", new DepthPreviewRequest("yes", "Buy", 2));
        var depth = (await preview.Content.ReadFromJsonAsync<GrossDepthResponse>())!;
        Assert.Equal(.8m, depth.GrossNotional); Assert.Equal(book.Snapshot.Id, depth.SnapshotId); Assert.Equal(1, source.Calls);
        source.Failure = "SecureConnectionFailure";
        refresh = await client.PostAsync(path + "/refresh?instrumentId=yes", null);
        var failure = (await refresh.Content.ReadFromJsonAsync<OrderBookResponse>())!;
        Assert.Equal("NetworkRestricted", failure.State); Assert.False(failure.IsActionable);
        Assert.Equal(book.Snapshot.Id, failure.Snapshot!.Id); Assert.Equal("SecureConnectionFailure", failure.LastRefreshFailure!.Code);
        Assert.Equal(2, source.Calls);
    }
    [Fact]
    public async Task Authorization_and_unknown_instruments_block_external_traffic()
    {
        var source = new Source(); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client);
        using var anonymous = app.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var denied = $"/api/v1/workspaces/{Guid.NewGuid()}/orderbooks/Kalshi/TEST";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(denied)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(denied + "/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(denied + "/depth", new DepthPreviewRequest("yes", "Buy", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + "/refresh?instrumentId=arbitrary", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path.Replace("TEST", "UNKNOWN") + "/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/depth", new DepthPreviewRequest("yes", "Buy", 0))).StatusCode);
        Assert.Equal(0, source.Calls);
        var missing = await client.PostAsync(path.Replace("Kalshi/TEST", "Polymarket/MISSING") + "/refresh", null);
        Assert.Equal("NotAddressable", (await missing.Content.ReadFromJsonAsync<OrderBookResponse>())!.State);
    }
    [Theory] [InlineData(null)] [InlineData("scalar")] [InlineData("Multivariate")]
    public async Task Unsupported_catalog_structure_has_native_data_but_no_executable_preview(string? classification)
    {
        var source = new Source(); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client, classification);
        var refresh = await client.PostAsync(path + "/refresh", null);
        var book = (await refresh.Content.ReadFromJsonAsync<OrderBookResponse>())!;
        Assert.Equal("Unsupported", book.State); Assert.False(book.IsActionable); Assert.Empty(book.Snapshot!.Asks);
        Assert.Single(book.Snapshot.Bids); Assert.Single(book.Snapshot.OppositeBids);
        var preview = await client.PostAsJsonAsync(path + "/depth", new DepthPreviewRequest("yes", "Sell", 1, true));
        Assert.Equal(0, (await preview.Content.ReadFromJsonAsync<GrossDepthResponse>())!.ExecutableQuantity);
    }
    [Fact]
    public async Task Concurrent_refresh_is_rejected_while_cache_remains_readable()
    {
        var source = new Source { Block = true }; await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client);
        var first = client.PostAsync(path + "/refresh", null); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + "/refresh", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode); Assert.Equal(1, source.Calls);
        }
        finally { source.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }
    [Fact]
    public async Task Cached_stale_snapshot_is_retained_and_default_preview_is_nonactionable()
    {
        var source = new Source(); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client);
        app.Services.GetRequiredService<OrderBookCache>().Store(OrderBookNormalizer.Normalize(new("Kalshi", "TEST", "yes", "Yes"),
            [], [new(.4m, 3, LiquidityOrigin.DerivedComplement)], DateTimeOffset.UtcNow.AddMinutes(-1)));
        var book = (await client.GetFromJsonAsync<OrderBookResponse>(path))!; Assert.Equal("Stale", book.State); Assert.NotNull(book.Snapshot);
        var response = await client.PostAsJsonAsync(path + "/depth", new DepthPreviewRequest("yes", "Buy", 1));
        var preview = (await response.Content.ReadFromJsonAsync<GrossDepthResponse>())!;
        Assert.False(preview.IsActionable); Assert.Equal(0, preview.ExecutableQuantity); Assert.Equal(book.Snapshot.Id, preview.SnapshotId); Assert.Equal(0, source.Calls);
    }
    [Fact]
    public async Task Catalog_type_change_removes_old_complements_without_external_refresh()
    {
        var source = new Source(); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = await Seed(app, client); await client.PostAsync(path + "/refresh", null);
        await app.WithDatabaseAsync(async db =>
        {
            (await db.CatalogMarkets.SingleAsync(m => m.Exchange == "Kalshi")).Classification = "scalar";
            return await db.SaveChangesAsync();
        });
        var cached = (await client.GetFromJsonAsync<OrderBookResponse>(path))!;
        Assert.Equal("Unsupported", cached.State); Assert.False(cached.IsActionable);
        Assert.Empty(cached.Snapshot!.Asks); Assert.Single(cached.Snapshot.OppositeBids); Assert.Equal(1, source.Calls);
    }
    [Fact]
    public async Task Polymarket_uses_catalog_outcome_token_mapping_without_yes_index_assumptions()
    {
        var source = new Source("Polymarket"); await using var app = Fixture(source); using var client = await app.AuthenticatedClientAsync();
        var path = (await Seed(app, client)).Replace("Kalshi/TEST", "Polymarket/MISSING");
        await app.WithDatabaseAsync(async db =>
        {
            (await db.CatalogMarkets.SingleAsync(m => m.Exchange == "Polymarket")).OutcomesJson =
                """[{"Label":"Candidate C","NativeTokenId":"123"},{"Label":"Candidate A","NativeTokenId":"456"},{"Label":"Unknown","NativeTokenId":null}]""";
            return await db.SaveChangesAsync();
        });
        var cached = (await client.GetFromJsonAsync<OrderBookResponse>(path))!; Assert.Equal(2, cached.Instruments.Length); Assert.Equal(0, source.Calls);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "/refresh?instrumentId=456", null)).StatusCode);
        Assert.Equal("Candidate A", source.LastRequest!.Instrument.Outcome); Assert.Equal("456", source.LastRequest.Instrument.NativeInstrumentId);
        Assert.Equal("MISSING", source.LastRequest.Instrument.NativeMarketId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + "/refresh?instrumentId=MISSING", null)).StatusCode); Assert.Equal(1, source.Calls);
    }
}
