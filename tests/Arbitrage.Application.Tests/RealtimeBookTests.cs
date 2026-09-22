using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class RealtimeBookTests
{
    [Fact]
    public void Cache_requires_connected_current_generation_book_and_respects_realtime_pinning()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock, 1);
        var book = OrderBookNormalizer.Normalize(Yes, [], [], At);
        var metadata = new RealtimeBookMetadata(1, RealtimeSubscriptionState.Streaming, BookContinuity.Continuous,
            true, AnchorAt: At, LastChangedAt: At);
        cache.PublishRealtime(Yes, metadata, book); Assert.True(cache.Read(Yes).Eligibility.IsActionable);
        cache.PublishRealtime(Yes, metadata with { Generation = 2 }); Assert.False(cache.Read(Yes).Eligibility.IsActionable);
        Assert.False(cache.PublishRealtime(Yes, metadata, book));
        cache.PublishRealtime(Yes, metadata with { Generation = 2, Connected = false }, book);
        Assert.False(cache.Read(Yes).Eligibility.IsActionable);
        Assert.Throws<InvalidOperationException>(() => cache.Store(OrderBookNormalizer.Normalize(No, [], [], At)));
        cache.PublishRealtime(Yes, metadata with { Generation = 2, State = RealtimeSubscriptionState.Stopped });
        cache.Store(book); Assert.Equal(BookSourceMode.RestSnapshot, cache.Read(Yes).Source);
    }
    [Fact]
    public void Oversized_frames_invalidate_without_replacing_visible_snapshot()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var s = Session("Polymarket", cache, clock);
        s.Accept(PolyBook(), 1); var previous = cache.Read(Poly).Snapshot;
        Assert.Throws<RealtimeProtocolException>(() => s.Accept(new string('x', RealtimeBookSession.MaximumFrameBytes + 1), 1));
        Assert.Same(previous, cache.Read(Poly).Snapshot); Assert.False(cache.Read(Poly).Eligibility.IsActionable);
    }
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-22T00:00:00Z");
    private static readonly OrderBookInstrumentId Yes = new("Kalshi", "TEST", "yes", "Yes"), No = new("Kalshi", "TEST", "no", "No");
    private static readonly OrderBookInstrumentId Poly = new("Polymarket", "gamma-1", "123", "A");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private static string Snapshot(long seq = 100, string ticker = "TEST") => JsonSerializer.Serialize(new { type = "orderbook_snapshot", sid = 2, seq,
        msg = new { market_ticker = ticker, market_id = "native", yes_dollars_fp = new[] { new[] { "0.40", "10.00" } }, no_dollars_fp = new[] { new[] { "0.55", "8.00" } } } });
    private static string Delta(long seq = 101, string delta = "5.00", string ticker = "TEST", int sid = 2, string price = "0.40", string side = "yes") =>
        JsonSerializer.Serialize(new { type = "orderbook_delta", sid, seq, msg = new { market_ticker = ticker, market_id = "native", price_dollars = price, delta_fp = delta, side, ts_ms = At.ToUnixTimeMilliseconds() } });
    private static string PolyBook(string asset = "123", string bid = "0.40") => JsonSerializer.Serialize(new { event_type = "book", asset_id = asset,
        market = "condition", timestamp = At.ToUnixTimeMilliseconds().ToString(), bids = new[] { new { price = bid, size = "10" } }, asks = new[] { new { price = "0.60", size = "8" } } });
    private static string Change(string size = "15", string price = "0.40", string asset = "123", string side = "BUY") => JsonSerializer.Serialize(new {
        event_type = "price_change", market = "condition", timestamp = At.ToUnixTimeMilliseconds().ToString(), price_changes = new[] { new { asset_id = asset, price, size, side } } });
    private static RealtimeBookSession Session(string exchange, OrderBookCache cache, TimeProvider clock, long gen = 1) =>
        new(exchange, gen, exchange == "Kalshi" ? [new(Yes, true), new(No, true)] : [new(Poly, false)], clock,
            (id, meta, book) => cache.PublishRealtime(id, meta, book));
    private static void Ack(RealtimeBookSession session, long gen = 1) => session.Accept("""{"id":1,"type":"subscribed","msg":{"channel":"orderbook_delta","sid":2}}""", gen);
    [Fact]
    public void Exact_Kalshi_fixture_add_remove_gap_and_reanchor()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var session = Session("Kalshi", cache, clock);
        Ack(session); session.Accept(Snapshot(), 1);
        Assert.Equal(.45m, cache.Read(Yes).Snapshot!.Asks[0].Price);
        session.Accept(Delta(), 1);
        Assert.Equal(15m, cache.Read(Yes).Snapshot!.Bids[0].Quantity);
        Assert.Equal(.60m, cache.Read(No).Snapshot!.Asks[0].Price);
        Assert.Equal(15m, cache.Read(No).Snapshot!.Asks[0].Quantity);
        session.Accept(Delta(102, "-15.00"), 1); Assert.Empty(cache.Read(Yes).Snapshot!.Bids);
        var preserved = cache.Read(Yes).Snapshot;
        Assert.Throws<RealtimeProtocolException>(() => session.Accept(Delta(104), 1));
        Assert.Same(preserved, cache.Read(Yes).Snapshot); Assert.False(cache.Read(Yes).Eligibility.IsActionable);
        Assert.Equal(BookContinuity.GapDetected, cache.Read(Yes).Realtime!.Continuity);
        Assert.Throws<RealtimeProtocolException>(() => session.Accept(Delta(103), 1));
        var next = Session("Kalshi", cache, clock, 2); Ack(next, 2); next.Accept(Snapshot(200), 2);
        Assert.True(cache.Read(Yes).Eligibility.IsActionable); Assert.Equal(BookContinuity.Continuous, cache.Read(Yes).Realtime!.Continuity);
    }
    [Theory]
    [InlineData(100, "5", "TEST", 2, "0.40", "yes")]
    [InlineData(99, "5", "TEST", 2, "0.40", "yes")]
    [InlineData(102, "5", "TEST", 2, "0.40", "yes")]
    [InlineData(101, "5", "OTHER", 2, "0.40", "yes")]
    [InlineData(101, "5", "TEST", 3, "0.40", "yes")]
    [InlineData(101, "-11", "TEST", 2, "0.40", "yes")]
    [InlineData(101, "1", "TEST", 2, "1.1", "yes")]
    [InlineData(101, "1.001", "TEST", 2, "0.40", "yes")]
    [InlineData(101, "1", "TEST", 2, "0.40001", "yes")]
    [InlineData(101, "1", "TEST", 2, "0.40", "invalid")]
    public void Kalshi_anomalies_preserve_visible_book_and_fail_closed(long sequence, string delta, string ticker, int sid, string price, string side)
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var s = Session("Kalshi", cache, clock); Ack(s); s.Accept(Snapshot(), 1);
        var original = cache.Read(Yes).Snapshot;
        Assert.Throws<RealtimeProtocolException>(() => s.Accept(Delta(sequence, delta, ticker, sid, price, side), 1));
        Assert.Same(original, cache.Read(Yes).Snapshot); Assert.False(cache.Read(Yes).Eligibility.IsActionable);
    }
    [Fact]
    public void Fractional_subcent_and_sequence_scope_include_control_and_other_markets()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var other = Yes with { NativeMarketId = "OTHER" };
        var s = new RealtimeBookSession("Kalshi", 1, [new(Yes, true), new(other, true)], clock, (id, m, b) => cache.PublishRealtime(id, m, b));
        Ack(s); s.Accept(Snapshot(), 1); s.Accept(Snapshot(101, "OTHER"), 1);
        s.Accept("""{"type":"ok","sid":2,"seq":102,"msg":{"market_tickers":["TEST","OTHER"]}}""", 1);
        s.Accept(Delta(103, "1.25", price: "0.4001"), 1);
        Assert.Equal(.4001m, cache.Read(Yes).Snapshot!.Bids[0].Price); Assert.Equal(1.25m, cache.Read(Yes).Snapshot!.Bids[0].Quantity);
        Assert.Equal(.40m, cache.Read(other).Snapshot!.Bids[0].Price);
    }
    [Theory] [InlineData("Kalshi")] [InlineData("Polymarket")]
    public void Rest_and_prior_generation_cannot_anchor_new_delta(string exchange)
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var id = exchange == "Kalshi" ? Yes : Poly;
        cache.Store(OrderBookNormalizer.Normalize(id, [], [], At)); var session = Session(exchange, cache, clock);
        if (exchange == "Kalshi") Ack(session);
        Assert.Throws<RealtimeProtocolException>(() => session.Accept(exchange == "Kalshi" ? Delta() : Change(), 1));
        var next = Session(exchange, cache, clock, 2);
        next.Accept(exchange == "Kalshi" ? Snapshot() : PolyBook(), 1);
        Assert.False(cache.Read(id).Eligibility.IsActionable); Assert.Null(cache.Read(id).Realtime!.AnchorAt);
    }
    [Fact]
    public void Polymarket_absolute_sizes_removal_replacement_best_effort_and_tick()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var s = Session("Polymarket", cache, clock);
        s.Accept(PolyBook(), 1); s.Accept(Change(), 1);
        Assert.Equal(15m, cache.Read(Poly).Snapshot!.Bids[0].Quantity); Assert.Equal(BookContinuity.BestEffort, cache.Read(Poly).Realtime!.Continuity);
        s.Accept(Change("0"), 1); Assert.Empty(cache.Read(Poly).Snapshot!.Bids);
        s.Accept(PolyBook(bid: "0.42"), 1); Assert.Single(cache.Read(Poly).Snapshot!.Bids);
        s.Accept("""{"event_type":"tick_size_change","asset_id":"123","market":"condition","old_tick_size":"0.01","new_tick_size":"0.0025"}""", 1);
        Assert.Equal(.0025m, cache.Read(Poly).Realtime!.TickSize); s.Accept(Change("2", "0.4025"), 1);
        Assert.Throws<RealtimeProtocolException>(() => s.Accept(Change("2", "0.4026"), 1));
    }
    [Theory]
    [InlineData("-1", "0.4", "123", "BUY")] [InlineData("1", "1.1", "123", "BUY")]
    [InlineData("NaN", "0.4", "123", "BUY")] [InlineData("1", "1e-1", "123", "BUY")]
    [InlineData("1", "0.4", "bad", "BUY")] [InlineData("1", "0.4", "123", "bad")]
    public void Polymarket_malformed_delta_rejects_entire_update(string size, string price, string asset, string side)
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var s = Session("Polymarket", cache, clock); s.Accept(PolyBook(), 1);
        var snapshot = cache.Read(Poly).Snapshot;
        Assert.Throws<RealtimeProtocolException>(() => s.Accept(Change(size, price, asset, side), 1));
        Assert.Same(snapshot, cache.Read(Poly).Snapshot); Assert.False(cache.Read(Poly).Eligibility.IsActionable);
    }
    [Fact]
    public void Polymarket_multi_asset_atomic_changes_do_not_cross_contaminate()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var second = Poly with { NativeInstrumentId = "456" };
        var s = new RealtimeBookSession("Polymarket", 1, [new(Poly, false), new(second, false)], clock, (id,m,b) => cache.PublishRealtime(id,m,b));
        s.Accept(PolyBook(), 1); s.Accept(PolyBook("456"), 1);
        s.Accept(Change("3", "0.6", "456", "SELL"), 1);
        Assert.Equal(8m, cache.Read(Poly).Snapshot!.Asks[0].Quantity); Assert.Equal(3m, cache.Read(second).Snapshot!.Asks[0].Quantity);
        var before = cache.Read(Poly).Snapshot;
        s.Accept("PONG", 1); Assert.Same(before, cache.Read(Poly).Snapshot);
        var malformed = """{"event_type":"price_change","market":"condition","timestamp":"1790035200000","price_changes":[{"asset_id":"123","price":"0.4","size":"20","side":"BUY"},{"asset_id":"456","price":"bad","size":"20","side":"BUY"}]}""";
        Assert.Throws<RealtimeProtocolException>(() => s.Accept(malformed, 1)); Assert.Same(before, cache.Read(Poly).Snapshot);
    }
    [Fact]
    public void Control_does_not_freshen_and_REST_cannot_replace_realtime_lane()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var s = Session("Polymarket", cache, clock); s.Accept(PolyBook(), 1);
        var realtime = cache.Read(Poly).Snapshot; clock.Now = At.AddSeconds(11); s.Accept("PONG", 1);
        cache.Store(OrderBookNormalizer.Normalize(Poly, [], [], clock.Now));
        Assert.Same(realtime, cache.Read(Poly).Snapshot); Assert.False(cache.Read(Poly).Eligibility.IsActionable);
        Assert.Equal(RealtimeSubscriptionState.Stale, cache.Read(Poly).Realtime!.State);
        Assert.Equal(At, cache.Read(Poly).Realtime!.LastChangedAt); Assert.Equal(clock.Now, cache.Read(Poly).Realtime!.LastControlAt);
    }
    [Fact]
    public void Subscription_commands_and_unsubscribe_are_market_data_only()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock); var p = Session("Polymarket", cache, clock);
        using var json = JsonDocument.Parse(p.Subscribe()); Assert.Equal("123", json.RootElement.GetProperty("assets_ids")[0].GetString());
        Assert.True(json.RootElement.GetProperty("initial_dump").GetBoolean()); Assert.Contains("unsubscribe", p.Unsubscribe());
        var k = Session("Kalshi", cache, clock); Assert.Contains("orderbook_delta", k.Subscribe()); Assert.DoesNotContain("market_ids", k.Subscribe());
        Assert.Null(k.Unsubscribe()); Ack(k); Assert.Contains("[2]", k.Unsubscribe());
    }
    [Fact]
    public void Signing_uses_exact_verified_input_RSA_PSS_SHA256_and_base64()
    {
        using var rsa = RSA.Create(2048); using var lease = new ExchangeCredentialLease("fixture-key", rsa.ExportPkcs8PrivateKey());
        var headers = KalshiWebSocketAuthentication.Headers(lease, At);
        Assert.Equal("fixture-key", headers["KALSHI-ACCESS-KEY"]);
        var timestamp = At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(timestamp, headers["KALSHI-ACCESS-TIMESTAMP"]);
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(timestamp + "GET/trade-api/ws/v2"),
            Convert.FromBase64String(headers["KALSHI-ACCESS-SIGNATURE"]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        lease.Dispose(); Assert.All(lease.PrivateKey, b => Assert.Equal(0, b));
    }
    [Fact]
    public void Secure_connection_failure_is_not_downgraded_to_plain_transport() =>
        Assert.Equal("SecureConnectionFailure", RealtimeOrderBookSource.Classify(new IOException("hidden", new AuthenticationException("hidden"))));
}
