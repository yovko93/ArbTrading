using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Connectors;

public sealed class RealtimeProtocolException(string code) : Exception(code);

// Single writer: the exchange receive loop owns this generation. Published books are immutable.
public sealed class RealtimeBookSession
{
    private sealed class Book(OrderBookRequest request)
    {
        public OrderBookRequest Request { get; } = request;
        public Dictionary<decimal, decimal> Bids { get; set; } = [];
        public Dictionary<decimal, decimal> Asks { get; set; } = [];
        public string? Market { get; set; }
        public RealtimeBookMetadata Metadata { get; set; } = new(0, RealtimeSubscriptionState.AwaitingSnapshot, BookContinuity.AwaitingAnchor);
    }
    private readonly string exchange;
    private readonly long generation;
    private readonly TimeProvider clock;
    private readonly Action<OrderBookInstrumentId, RealtimeBookMetadata, OrderBookSnapshot?> publish;
    private readonly Book[] books;
    private long? sid, sequence;
    public long? SubscriptionId => sid;
    public const int MaximumFrameBytes = 2_000_000;
    public RealtimeBookSession(string exchange, long generation, IEnumerable<OrderBookRequest> requests, TimeProvider clock,
        Action<OrderBookInstrumentId, RealtimeBookMetadata, OrderBookSnapshot?> publish)
    {
        this.exchange = exchange; this.generation = generation; this.clock = clock; this.publish = publish;
        books = requests.Select(r => new Book(r) { Metadata = new(generation, RealtimeSubscriptionState.AwaitingSnapshot,
            BookContinuity.AwaitingAnchor, true, SessionId: generation.ToString(System.Globalization.CultureInfo.InvariantCulture)) }).ToArray();
        if (books.Length is < 1 or > 32 || books.Any(b => b.Request.Instrument.Exchange != exchange)) throw new ArgumentException("InvalidSubscriptions");
        foreach (var b in books) Emit(b);
    }
    public string Subscribe() => exchange == "Polymarket"
        ? JsonSerializer.Serialize(new { assets_ids = books.Select(b => b.Request.Instrument.NativeInstrumentId).Distinct().ToArray(), type = "market", initial_dump = true, level = 2 })
        : JsonSerializer.Serialize(new { id = 1, cmd = "subscribe", @params = new { channels = new[] { "orderbook_delta" }, market_tickers = books.Select(b => b.Request.Instrument.NativeMarketId).Distinct().ToArray() } });
    public string? Unsubscribe() => exchange == "Polymarket"
        ? JsonSerializer.Serialize(new { operation = "unsubscribe", assets_ids = books.Select(b => b.Request.Instrument.NativeInstrumentId).Distinct().ToArray() })
        : sid is { } s ? JsonSerializer.Serialize(new { id = 2, cmd = "unsubscribe", @params = new { sids = new[] { s } } }) : null;
    public void Accept(string frame, long frameGeneration)
    {
        if (frameGeneration != generation) return;
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(frame) > MaximumFrameBytes) throw new RealtimeProtocolException("FrameTooLarge");
            if (exchange == "Polymarket" && frame == "PONG") { Control(); return; }
            using var json = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (exchange != "Polymarket" || json.RootElement.GetArrayLength() > 256) throw new RealtimeProtocolException("InvalidEnvelope");
                foreach (var item in json.RootElement.EnumerateArray()) Poly(item);
            }
            else if (exchange == "Polymarket") Poly(json.RootElement);
            else Kalshi(json.RootElement);
        }
        catch (Exception e) when (e is JsonException or MarketDiscoveryException or InvalidOperationException or KeyNotFoundException
            or FormatException or ArgumentException or OverflowException or RealtimeProtocolException)
        {
            var code = e is RealtimeProtocolException p ? p.Message : "MalformedBookEvent";
            Invalidate(code, code == "SequenceGap" ? BookContinuity.GapDetected : BookContinuity.Resynchronizing);
            throw new RealtimeProtocolException(code);
        }
    }
    public void Invalidate(string reason, BookContinuity continuity = BookContinuity.Disconnected)
    {
        sequence = null;
        foreach (var b in books)
        {
            b.Bids.Clear(); b.Asks.Clear();
            b.Metadata = b.Metadata with { State = RealtimeSubscriptionState.Resynchronizing, Continuity = continuity,
                Connected = false, AnchorAt = null, ResyncReason = reason };
            Emit(b);
        }
    }
    private void Control()
    {
        foreach (var b in books) { b.Metadata = b.Metadata with { LastReceivedAt = clock.GetUtcNow(), LastControlAt = clock.GetUtcNow() }; Emit(b); }
    }
    private void Poly(JsonElement root)
    {
        var type = Text(root, "event_type");
        if (type is "last_trade_price" or "best_bid_ask" or "new_market" or "market_resolved") { Control(); return; }
        if (type == "price_change")
        {
            var changes = RestOrderBookSource.Array(root, "price_changes");
            var staged = new Dictionary<Book, (Dictionary<decimal, decimal> Bids, Dictionary<decimal, decimal> Asks)>();
            foreach (var change in changes.EnumerateArray())
            {
                var b = FindAsset(Text(change, "asset_id")); RequireAnchor(b);
                if (Text(root, "market") != b.Market) throw new RealtimeProtocolException("InstrumentMismatch");
                if (!staged.TryGetValue(b, out var levels)) levels = (new(b.Bids), new(b.Asks));
                var price = Number(change, "price"); var size = Number(change, "size");
                Check(price, size, b.Metadata.TickSize);
                var side = Text(change, "side");
                var target = side == "BUY" ? levels.Bids : side == "SELL" ? levels.Asks : throw new RealtimeProtocolException("InvalidSide");
                Set(target, price, size); staged[b] = levels;
            }
            // Validate the entire message before committing any asset.
            var pending = staged.Select(p => (Book: p.Key, Levels: p.Value,
                Snapshot: NormalizePoly(p.Key, p.Value.Bids, p.Value.Asks, root))).ToArray();
            foreach (var p in pending) Valid(p.Snapshot);
            foreach (var p in pending) { p.Book.Bids = p.Levels.Bids; p.Book.Asks = p.Levels.Asks; Commit(p.Book, p.Snapshot, false); }
            return;
        }
        var book = FindAsset(Text(root, "asset_id"));
        if (type == "book")
        {
            var market = Text(root, "market");
            if (market.Length == 0 || book.Market is not null && book.Market != market) throw new RealtimeProtocolException("InstrumentMismatch");
            var bids = PolyLevels(root, "bids"); var asks = PolyLevels(root, "asks");
            book.Market = market;
            var snapshot = NormalizePoly(book, bids, asks, root); Valid(snapshot);
            book.Bids = bids; book.Asks = asks; Commit(book, snapshot, true);
        }
        else if (type == "tick_size_change")
        {
            if (book.Market is not null && Text(root, "market") != book.Market) throw new RealtimeProtocolException("InstrumentMismatch");
            var tick = Number(root, "new_tick_size");
            if (tick <= 0 || tick > 1) throw new RealtimeProtocolException("InvalidTickSize");
            book.Metadata = book.Metadata with { TickSize = tick, LastReceivedAt = clock.GetUtcNow(), LastControlAt = clock.GetUtcNow() }; Emit(book);
        }
        else throw new RealtimeProtocolException("UnknownBookEvent");
    }
    private void Kalshi(JsonElement root)
    {
        var type = Text(root, "type"); root.TryGetProperty("msg", out var msg);
        if (type == "error") throw new RealtimeProtocolException(msg.GetProperty("code").GetInt32() == 9 ? "AuthenticationFailed" : "SubscriptionRejected");
        if (type == "subscribed")
        {
            if (sid is not null || root.GetProperty("id").GetInt64() != 1 || Text(msg, "channel") != "orderbook_delta") throw new RealtimeProtocolException("SubscriptionMismatch");
            sid = msg.GetProperty("sid").GetInt64();
            if (sid < 1) throw new RealtimeProtocolException("SubscriptionMismatch");
            foreach (var b in books) { b.Metadata = b.Metadata with { SubscriptionId = sid }; Emit(b); }
            return;
        }
        if (sid is null || root.GetProperty("sid").GetInt64() != sid) throw new RealtimeProtocolException("SubscriptionMismatch");
        var next = root.GetProperty("seq").GetInt64();
        if (next < 1 || sequence is { } prior && next != prior + 1) throw new RealtimeProtocolException("SequenceGap");
        if (type is "ok" or "unsubscribed") { sequence = next; Control(); return; }
        var ticker = Text(msg, "market_ticker");
        var matching = books.Where(b => b.Request.Instrument.NativeMarketId == ticker).ToArray();
        if (matching.Length == 0) throw new RealtimeProtocolException("InstrumentMismatch");
        var market = Text(msg, "market_id");
        if (market.Length == 0 || matching.Any(b => b.Market is not null && b.Market != market)) throw new RealtimeProtocolException("InstrumentMismatch");
        Dictionary<decimal, decimal> yes, no;
        if (type == "orderbook_snapshot")
        { yes = KalshiLevels(msg, "yes_dollars_fp"); no = KalshiLevels(msg, "no_dollars_fp"); }
        else if (type == "orderbook_delta")
        {
            foreach (var b in matching) RequireAnchor(b);
            yes = new(matching[0].Bids); no = new(matching[0].Asks);
            var price = Number(msg, "price_dollars", 4); var delta = Number(msg, "delta_fp", 2);
            var side = Text(msg, "side");
            var target = side == "yes" ? yes : side == "no" ? no : throw new RealtimeProtocolException("InvalidSide");
            var quantity = checked(target.GetValueOrDefault(price) + delta);
            Check(price, quantity, null); Set(target, price, quantity);
        }
        else throw new RealtimeProtocolException("UnknownBookEvent");
        DateTimeOffset? source = msg.TryGetProperty("ts_ms", out var ts) ? DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()) : null;
        var snapshots = matching.Select(b => OrderBookNormalizer.NormalizeBinary(b.Request.Instrument,
            Levels(yes, LiquidityOrigin.NativeBid), Levels(no, LiquidityOrigin.NativeBid), clock.GetUtcNow(), b.Request.BinarySupported, source)).ToArray();
        foreach (var snapshot in snapshots) Valid(snapshot);
        sequence = next;
        for (var i = 0; i < matching.Length; i++)
        {
            var b = matching[i]; b.Bids = yes; b.Asks = no; b.Market = market;
            Commit(b, snapshots[i], type == "orderbook_snapshot");
        }
    }
    private void Commit(Book b, OrderBookSnapshot snapshot, bool anchor)
    {
        b.Metadata = b.Metadata with { State = RealtimeSubscriptionState.Streaming,
            Continuity = exchange == "Kalshi" ? BookContinuity.Continuous : BookContinuity.BestEffort,
            Connected = true, SubscriptionId = sid, Sequence = sequence,
            AnchorAt = anchor ? clock.GetUtcNow() : b.Metadata.AnchorAt, LastReceivedAt = clock.GetUtcNow(),
            LastChangedAt = clock.GetUtcNow(), DeltaCount = anchor ? 0 : b.Metadata.DeltaCount + 1 };
        publish(b.Request.Instrument, b.Metadata, snapshot);
    }
    private void Emit(Book b) => publish(b.Request.Instrument, b.Metadata, null);
    private Book FindAsset(string asset) => books.SingleOrDefault(b => b.Request.Instrument.NativeInstrumentId == asset)
        ?? throw new RealtimeProtocolException("InstrumentMismatch");
    private static void RequireAnchor(Book b)
    { if (b.Metadata.AnchorAt is null) throw new RealtimeProtocolException("AwaitingAnchor"); }
    private static void Valid(OrderBookSnapshot b)
    { if (b.Validity != BookValidity.Valid) throw new RealtimeProtocolException("InvalidOrderBook"); }
    private static string Text(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new RealtimeProtocolException("InvalidEnvelope");
    private static decimal Number(JsonElement root, string name, int scale = 28) => RestOrderBookSource.Number(root.GetProperty(name), scale);
    private static void Check(decimal price, decimal quantity, decimal? tick)
    { if (price < 0 || price > 1 || quantity < 0 || tick is { } t && price % t != 0) throw new RealtimeProtocolException("InvalidLevel"); }
    private static void Set(Dictionary<decimal, decimal> side, decimal price, decimal size)
    {
        if (size == 0) side.Remove(price); else side[price] = size;
        if (side.Count > OrderBookNormalizer.MaximumLevelsPerSide) throw new RealtimeProtocolException("TooManyLevels");
    }
    private static Dictionary<decimal, decimal> PolyLevels(JsonElement root, string side)
    {
        var result = new Dictionary<decimal, decimal>();
        foreach (var l in RestOrderBookSource.Array(root, side).EnumerateArray())
        {
            var p = Number(l, "price"); var q = Number(l, "size"); Check(p, q, null);
            if (!result.TryAdd(p, q)) throw new RealtimeProtocolException("DuplicatePriceLevel");
        }
        return result;
    }
    private static Dictionary<decimal, decimal> KalshiLevels(JsonElement root, string side)
    {
        var result = new Dictionary<decimal, decimal>();
        // Optional empty side fields are omitted by the current Kalshi schema.
        if (!root.TryGetProperty(side, out _)) return result;
        foreach (var l in RestOrderBookSource.Array(root, side).EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() != 2) throw new RealtimeProtocolException("InvalidLevel");
            var p = RestOrderBookSource.Number(l[0], 4); var q = RestOrderBookSource.Number(l[1], 2); Check(p, q, null);
            if (!result.TryAdd(p, q)) throw new RealtimeProtocolException("DuplicatePriceLevel");
        }
        return result;
    }
    private static IEnumerable<OrderBookLevel> Levels(Dictionary<decimal, decimal> levels, LiquidityOrigin origin) => levels.Select(p => new OrderBookLevel(p.Key, p.Value, origin));
    private OrderBookSnapshot NormalizePoly(Book b, Dictionary<decimal, decimal> bids, Dictionary<decimal, decimal> asks, JsonElement root)
    {
        var source = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture));
        return OrderBookNormalizer.Normalize(b.Request.Instrument, Levels(bids, LiquidityOrigin.NativeBid), Levels(asks, LiquidityOrigin.NativeAsk),
            clock.GetUtcNow(), source, b.Market, root.TryGetProperty("hash", out var h) ? h.GetString() : null);
    }
}
