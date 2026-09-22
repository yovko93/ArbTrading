using System.Net;
using System.Text;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class OrderBookSourceTests
{
    private static readonly OrderBookRequest Poly = new(new("Polymarket", "gamma-id", "123", "Candidate C"), false);
    private static readonly OrderBookRequest Kalshi = new(new("Kalshi", "TICKER", "yes", "Yes"), true);
    private static string PolyJson(string bids = "[]", string asks = "[]", string asset = "123") =>
        $$"""{"asset_id":"{{asset}}","market":"condition-id","timestamp":"1789990000123","hash":"hash","bids":{{bids}},"asks":{{asks}}} """;
    private static OrderBookSnapshot Parse(string exchange, string json, OrderBookRequest? request = null)
    {
        using var http = new HttpClient(); using var document = JsonDocument.Parse(json);
        RestOrderBookSource source = exchange == "Polymarket" ? new PolymarketOrderBookSource(http) : new KalshiOrderBookSource(http);
        return source.Parse(document.RootElement, request ?? (exchange == "Polymarket" ? Poly : Kalshi), DateTimeOffset.UtcNow);
    }
    [Fact]
    public void Polymarket_keeps_native_sides_identity_source_time_hash_and_orders_levels()
    {
        var b = Parse("Polymarket", PolyJson("""[{"price":"0.20","size":"1.5"},{"price":"0.3","size":"2"}]""",
            """[{"price":"0.6","size":"4"},{"price":"0.4","size":"3"}]"""));
        Assert.Equal(.3m, b.Bids[0].Price); Assert.Equal(.4m, b.Asks[0].Price);
        Assert.All(b.Bids, l => Assert.Equal(LiquidityOrigin.NativeBid, l.Origin));
        Assert.All(b.Asks, l => Assert.Equal(LiquidityOrigin.NativeAsk, l.Origin));
        Assert.Equal("condition-id", b.SourceMarketId); Assert.Equal("gamma-id", b.Instrument.NativeMarketId);
        Assert.Equal("Candidate C", b.Instrument.Outcome); Assert.Equal("hash", b.NativeHash);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789990000123), b.SourceTimestamp);
    }
    [Theory]
    [InlineData("[]", "[]")] [InlineData("[]", "[{\"price\":\"0.5\",\"size\":\"1\"}]")]
    [InlineData("[{\"price\":\"0.5\",\"size\":\"1\"}]", "[]")]
    public void Polymarket_empty_sides_are_valid(string bids, string asks) => Assert.Equal(BookValidity.Valid, Parse("Polymarket", PolyJson(bids, asks)).Validity);
    [Theory]
    [InlineData("NaN", "1")] [InlineData("0.4", "NaN")] [InlineData("1e-3", "1")]
    [InlineData("0.40000000000000000000000000001", "1")] [InlineData("0.4", "999999999999999999999999999999")]
    public void Polymarket_rejects_malformed_and_unrepresentable_decimals(string price, string size) =>
        Assert.Equal("InvalidOrderBook", Assert.Throws<MarketDiscoveryException>(() => Parse("Polymarket", PolyJson($$"""[{"price":"{{price}}","size":"{{size}}"}]"""))).Code);
    [Theory]
    [InlineData("1.1", "1")] [InlineData("-0.1", "1")] [InlineData("0.2", "-1")]
    public void Polymarket_marks_illegal_levels_invalid(string price, string size) => Assert.Equal(BookValidity.Invalid,
        Parse("Polymarket", PolyJson($$"""[{"price":"{{price}}","size":"{{size}}"}]""")).Validity);
    [Theory] [InlineData("")] [InlineData("wrong")]
    public void Polymarket_rejects_missing_or_wrong_asset(string asset) => Assert.Throws<MarketDiscoveryException>(() => Parse("Polymarket", PolyJson(asset: asset)));
    [Fact]
    public void Polymarket_duplicate_l2_totals_are_not_summed() => Assert.Equal(BookValidity.Invalid,
        Parse("Polymarket", PolyJson("""[{"price":"0.3","size":"2"},{"price":"0.3","size":"2"}]""")).Validity);
    [Theory] [InlineData("yes", "0.63")] [InlineData("no", "0.7999")]
    public void Kalshi_exact_fixed_point_complements_preserve_both_native_bid_sides(string outcome, string expectedAsk)
    {
        var b = Parse("Kalshi", """{"orderbook_fp":{"yes_dollars":[["0.2001","1.25"]],"no_dollars":[["0.3700","12.50"]]}}""",
            Kalshi with { Instrument = Kalshi.Instrument with { NativeInstrumentId = outcome } });
        Assert.Equal(decimal.Parse(expectedAsk, System.Globalization.CultureInfo.InvariantCulture), b.Asks[0].Price);
        Assert.Equal(outcome == "yes" ? 12.50m : 1.25m, b.Asks[0].Quantity);
        Assert.Equal(LiquidityOrigin.DerivedComplement, b.Asks[0].Origin);
        Assert.Equal(b.OppositeBids[0].Quantity, b.Asks[0].Quantity); Assert.Null(b.SourceTimestamp);
    }
    [Fact]
    public void Kalshi_unsupported_retains_native_bids_without_fabricating_asks()
    {
        var b = Parse("Kalshi", """{"orderbook_fp":{"yes_dollars":[["0.2","1.25"]],"no_dollars":[["0.37","12.50"]]}}""", Kalshi with { BinarySupported = false });
        Assert.Equal(BookValidity.Unsupported, b.Validity); Assert.Single(b.Bids); Assert.Single(b.OppositeBids); Assert.Empty(b.Asks);
    }
    [Theory]
    [InlineData("NaN", "1.00")] [InlineData("0.2", "bad")] [InlineData("0.20001", "1")]
    [InlineData("0.2", "1.001")]
    public void Kalshi_rejects_malformed_fixed_point(string price, string quantity) => Assert.Throws<MarketDiscoveryException>(() =>
        Parse("Kalshi", $$$"""{"orderbook_fp":{"yes_dollars":[],"no_dollars":[["{{{price}}}","{{{quantity}}}"]]}}"""));
    [Theory] [InlineData("1.01", "1")] [InlineData("0.2", "-1")]
    public void Kalshi_never_derives_from_invalid_opposite_bids(string price, string quantity)
    {
        var b = Parse("Kalshi", $$$"""{"orderbook_fp":{"yes_dollars":[],"no_dollars":[["{{{price}}}","{{{quantity}}}"]]}}""");
        Assert.Equal(BookValidity.Invalid, b.Validity); Assert.Empty(b.Asks);
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"orderbook\":{\"yes\":[],\"no\":[]}}")]
    [InlineData("{\"orderbook_fp\":{\"yes_dollars\":[]}}")]
    public void Kalshi_does_not_fallback_to_legacy_or_missing_sides(string json) => Assert.Throws<MarketDiscoveryException>(() => Parse("Kalshi", json));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Assert.Null(request.Headers.Authorization); Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(response(request)); }
    }
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    [Theory] [InlineData("Polymarket")] [InlineData("Kalshi")]
    public async Task Dedicated_transport_uses_fixed_hosts_full_depth_and_no_credentials(string exchange)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(exchange == "Polymarket" ? "clob.polymarket.com" : "external-api.kalshi.com", request.RequestUri!.Host);
            Assert.Equal(exchange == "Polymarket" ? "?token_id=123" : "?depth=0", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(exchange == "Polymarket" ? PolyJson() : """{"orderbook_fp":{"yes_dollars":[],"no_dollars":[]}}""") };
        });
        using var http = new HttpClient(handler);
        IOrderBookSource source = exchange == "Polymarket" ? new PolymarketOrderBookSource(http) : new KalshiOrderBookSource(http);
        Assert.Equal(BookValidity.Valid, (await source.ReadAsync(exchange == "Polymarket" ? Poly : Kalshi, default)).Validity);
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData(401, "PublicAccessDenied")] [InlineData(403, "PublicAccessDenied")]
    [InlineData(429, "RateLimited")] [InlineData(302, "UpstreamRejected")]
    public async Task Http_failures_are_classified_without_following_redirects(int status, string expected)
    {
        using var handler = new Handler(_ => new((HttpStatusCode)status)); using var http = new HttpClient(handler);
        Assert.Equal(expected, (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new KalshiOrderBookSource(http, maxAttempts: 1).ReadAsync(Kalshi, default))).Code);
        Assert.Equal(1, handler.Calls);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Bounded_payload_rejects_declared_or_streamed_oversize(bool declared)
    {
        using var handler = new Handler(_ =>
        {
            HttpContent content = declared ? new StringContent(new string(' ', RestOrderBookSource.MaximumResponseBytes + 1)) :
                new StreamContent(new UnseekableStream(Encoding.UTF8.GetBytes(new string(' ', RestOrderBookSource.MaximumResponseBytes + 1))));
            if (!declared) content.Headers.ContentLength = null;
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        Assert.Equal("PayloadTooLarge", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new PolymarketOrderBookSource(http).ReadAsync(Poly, default))).Code);
    }
    [Fact]
    public async Task Retry_after_is_honored_without_unbounded_wait()
    {
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2)); return response;
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<MarketDiscoveryException>(() => new KalshiOrderBookSource(http).ReadAsync(Kalshi, default));
        Assert.Equal("RateLimited", failure.Code); Assert.NotNull(failure.RetryAt); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Secure_connection_failure_is_not_retried_and_malformed_json_is_distinct()
    {
        using var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture"));
        using var http = new HttpClient(handler);
        Assert.Equal("SecureConnectionFailure", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new PolymarketOrderBookSource(http).ReadAsync(Poly, default))).Code);
        Assert.Equal(1, handler.Calls);
        using var badHandler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not-json") });
        using var badHttp = new HttpClient(badHandler);
        Assert.Equal("InvalidJson", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new KalshiOrderBookSource(badHttp).ReadAsync(Kalshi, default))).Code);
    }
    [Fact]
    public async Task Safe_get_retry_is_bounded_and_a_client_with_credentials_is_rejected_before_send()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable)); using var http = new HttpClient(handler);
        Assert.Equal("UpstreamUnavailable", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new KalshiOrderBookSource(http).ReadAsync(Kalshi, default))).Code);
        Assert.Equal(2, handler.Calls);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-not-a-real-credential");
        Assert.Equal("PublicClientMisconfigured", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new KalshiOrderBookSource(http).ReadAsync(Kalshi, default))).Code);
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task Missing_requested_identifier_does_not_send_and_sample_filter_is_opt_in()
    {
        using var handler = new Handler(request =>
        {
            Assert.Contains("mve_filter=exclude", request.RequestUri!.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"markets":[],"cursor":""}""") };
        });
        using var http = new HttpClient(handler);
        Assert.Equal("NotAddressable", (await Assert.ThrowsAsync<MarketDiscoveryException>(() => new PolymarketOrderBookSource(http).ReadAsync(
            Poly with { Instrument = Poly.Instrument with { NativeInstrumentId = "" } }, default))).Code);
        Assert.Equal(0, handler.Calls);
        await new KalshiMarketSource(http, excludeMultivariate: true).ReadPageAsync("open", null, 5, DateTimeOffset.UtcNow.AddSeconds(5), default);
        Assert.Equal(1, handler.Calls);
    }
}
