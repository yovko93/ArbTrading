using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class RealtimeTransportTests
{
    private sealed class Credentials : IExchangeCredentialStore
    {
        public byte[]? Key; public string Result = "NotConfigured";
        public ExchangeCredentialLease? Open() => Key is null ? null : new("test-key-id", Key.ToArray());
        public ExchangeCredentialStatus Status() => new(Key is not null, null, null, null, null, "Test", Result, Guid.Empty);
        public ExchangeCredentialStatus Import(string id, string path, bool confirm, Guid expected) => throw new NotSupportedException();
        public ExchangeCredentialStatus Remove(bool confirmed, Guid expected) { Key = null; return Status(); }
        public void RecordAuthentication(string result) => Result = result;
    }
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(1), period);
    }
    private sealed class Socket : IMarketWebSocket
    {
        public readonly Channel<string> Frames = Channel.CreateBounded<string>(32);
        public readonly ConcurrentQueue<string> Sent = new();
        public IReadOnlyDictionary<string, string>? Headers; public Uri? Uri; public Exception? Failure;
        public bool Closed, Disposed;
        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        { Uri = uri; Headers = headers; return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
        public Task SendAsync(string text, CancellationToken ct) { Sent.Enqueue(text); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await Frames.Reader.ReadAsync(ct);
        public Task CloseAsync(CancellationToken ct) { Closed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Factory(Func<Socket> create) : IMarketWebSocketFactory
    {
        public readonly ConcurrentQueue<Socket> Created = new();
        public IMarketWebSocket Create() { var socket = create(); Created.Enqueue(socket); return socket; }
    }
    private static OrderBookRequest[] Requests(string exchange) => [new(new(exchange, "TEST", exchange == "Kalshi" ? "yes" : "123", "Yes"), true)];
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, timeout.Token); }
    [Fact]
    public async Task Missing_credentials_do_not_attempt_handshake_or_retry()
    {
        var socket = new Socket(); var factory = new Factory(() => socket); var states = new List<RealtimeSubscriptionState>();
        await new RealtimeOrderBookSource(factory, new Credentials(), TimeProvider.System).RunAsync("Kalshi", Requests("Kalshi"),
            (_,m,_) => states.Add(m.State), default);
        Assert.Null(socket.Uri); Assert.Single(factory.Created); Assert.Equal(RealtimeSubscriptionState.AuthenticationRequired, states.Last());
    }
    [Fact]
    public async Task Authentication_failure_pauses_until_explicit_new_start()
    {
        using var rsa = RSA.Create(2048); var credentials = new Credentials { Key = rsa.ExportPkcs8PrivateKey() };
        var factory = new Factory(() => new Socket { Failure = new RealtimeProtocolException("AuthenticationFailed") });
        var source = new RealtimeOrderBookSource(factory, credentials, new FastClock());
        await source.RunAsync("Kalshi", Requests("Kalshi"), (_,_,_) => { }, default);
        Assert.Single(factory.Created); Assert.Equal("AuthenticationFailed", credentials.Result);
        var first = factory.Created.Single(); Assert.Equal(RealtimeOrderBookSource.KalshiUrl, first.Uri!.ToString()); Assert.Equal(3, first.Headers!.Count);
        await source.RunAsync("Kalshi", Requests("Kalshi"), (_,_,_) => { }, default); Assert.Equal(2, factory.Created.Count);
    }
    [Fact]
    public async Task Transport_reconnect_attempts_are_bounded()
    {
        var factory = new Factory(() => new Socket { Failure = new IOException("not logged") });
        var states = new List<RealtimeSubscriptionState>();
        await new RealtimeOrderBookSource(factory, new Credentials(), new FastClock()).RunAsync("Polymarket", Requests("Polymarket"), (_,m,_) => states.Add(m.State), default);
        Assert.Equal(RealtimeOrderBookSource.MaximumAttempts, factory.Created.Count); Assert.Equal(RealtimeSubscriptionState.Faulted, states.Last());
        Assert.All(factory.Created, s => Assert.True(s.Disposed));
    }
    [Fact]
    public async Task Production_receive_loop_replays_anchor_and_delta_then_unsubscribes_on_shutdown()
    {
        using var rsa = RSA.Create(2048); var credentials = new Credentials { Key = rsa.ExportPkcs8PrivateKey() };
        var socket = new Socket(); var factory = new Factory(() => socket); var cache = new OrderBookCache(TimeProvider.System);
        using var stop = new CancellationTokenSource(); var id = Requests("Kalshi")[0].Instrument;
        var run = new RealtimeOrderBookSource(factory, credentials, TimeProvider.System).RunAsync("Kalshi", Requests("Kalshi"), (i,m,b) => cache.PublishRealtime(i,m,b), stop.Token);
        await socket.Frames.Writer.WriteAsync("""{"type":"subscribed","id":1,"msg":{"channel":"orderbook_delta","sid":2}}""");
        await socket.Frames.Writer.WriteAsync("""{"type":"orderbook_snapshot","sid":2,"seq":100,"msg":{"market_ticker":"TEST","market_id":"native","yes_dollars_fp":[["0.40","10"]],"no_dollars_fp":[["0.55","8"]]}}""");
        await socket.Frames.Writer.WriteAsync("""{"type":"orderbook_delta","sid":2,"seq":101,"msg":{"market_ticker":"TEST","market_id":"native","side":"yes","price_dollars":"0.40","delta_fp":"5"}}""");
        await Until(() => cache.Read(id).Realtime?.Sequence == 101);
        Assert.Equal(15m, cache.Read(id).Snapshot!.Bids[0].Quantity);
        stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(socket.Sent, s => s.Contains("unsubscribe", StringComparison.Ordinal)); Assert.True(socket.Closed); Assert.True(socket.Disposed);
        Assert.False(cache.Read(id).Eligibility.IsActionable); Assert.Equal("Authenticated", credentials.Result);
    }
    [Fact]
    public async Task Public_socket_has_no_auth_headers_and_sends_documented_ping()
    {
        var socket = new Socket(); var factory = new Factory(() => socket);
        using var stop = new CancellationTokenSource();
        var run = new RealtimeOrderBookSource(factory, new Credentials(), new FastClock()).RunAsync("Polymarket", Requests("Polymarket"), (_,_,_) => { }, stop.Token);
        await Until(() => socket.Sent.Contains("PING")); Assert.Empty(socket.Headers!); Assert.Equal(RealtimeOrderBookSource.PolymarketUrl, socket.Uri!.ToString());
        stop.Cancel(); await run; Assert.Contains(socket.Sent, s => s.Contains("unsubscribe", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Network_restriction_stops_without_bypass_retry()
    {
        var factory = new Factory(() => new Socket { Failure = new RealtimeProtocolException("NetworkRestricted") });
        RealtimeSubscriptionState? state = null;
        await new RealtimeOrderBookSource(factory, new Credentials(), new FastClock()).RunAsync("Polymarket", Requests("Polymarket"), (_,m,_) => state = m.State, default);
        Assert.Single(factory.Created); Assert.Equal(RealtimeSubscriptionState.NetworkRestricted, state);
    }
}
