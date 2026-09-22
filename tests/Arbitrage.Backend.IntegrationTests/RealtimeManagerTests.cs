using System.Collections.Concurrent;
using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RealtimeManagerTests
{
    private sealed class EmptyCredentials : IExchangeCredentialStore
    {
        public ExchangeCredentialLease? Open() => null;
        public ExchangeCredentialStatus Status() => new(false, null, null, null, null, "Test", "NotConfigured", Guid.Empty);
        public ExchangeCredentialStatus Import(string keyId, string path, bool confirmReplacement, Guid expectedVersion) => throw new NotSupportedException();
        public ExchangeCredentialStatus Remove(bool confirmed, Guid expectedVersion) => Status();
        public void RecordAuthentication(string result) { }
    }
    private sealed class Socket : IMarketWebSocket
    {
        public readonly Channel<string> Frames = Channel.CreateBounded<string>(512);
        public int Connected, Disposed;
        public readonly ConcurrentQueue<string> Sent = new();
        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct) { Connected++; return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken ct) { Sent.Enqueue(text); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await Frames.Reader.ReadAsync(ct);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
    private sealed class Factory : IMarketWebSocketFactory
    {
        public readonly ConcurrentQueue<Socket> Sockets = new();
        public IMarketWebSocket Create() { var socket = new Socket(); Sockets.Enqueue(socket); return socket; }
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); while (!condition()) await Task.Delay(10, timeout.Token); }
    [Fact]
    public async Task Single_owner_coalesces_bursts_without_slow_SignalR_backpressure_and_stops_cleanly()
    {
        var factory = new Factory(); var clock = TimeProvider.System; var cache = new OrderBookCache(clock); var instance = new BackendInstance();
        var publisher = new RealtimePublisher(new(instance), instance);
        using var manager = new RealtimeOrderBookManager(new(factory, new EmptyCredentials(), clock), cache, publisher, new(), clock);
        await manager.StartAsync(default);
        var workspace = Guid.NewGuid(); var id = new OrderBookInstrumentId("Polymarket", "market", "123", "A");
        manager.Start(workspace, new(id, false)); manager.Start(workspace, new(id, false));
        await Until(() => factory.Sockets.TryPeek(out var socket) && socket.Connected == 1);
        var first = factory.Sockets.Single();
        await first.Frames.Writer.WriteAsync("""{"event_type":"book","asset_id":"123","market":"condition","timestamp":"1790035200000","bids":[{"price":"0.4","size":"1"}],"asks":[{"price":"0.6","size":"10"}]}""");
        for (var n = 0; n < 200; n++) await first.Frames.Writer.WriteAsync($$"""{"event_type":"price_change","market":"condition","timestamp":"1790035200000","price_changes":[{"asset_id":"123","side":"BUY","price":"0.4","size":"{{n + 2}}"}]}""");
        await Until(() => cache.Read(id).Snapshot?.Bids[0].Quantity == 201m);
        Assert.Single(factory.Sockets); Assert.True(cache.Read(id).Eligibility.IsActionable);
        await Task.Delay(300); var events = new List<RealtimePublisher.Dispatch>(); while (publisher.Reader.TryRead(out var item)) events.Add(item);
        Assert.InRange(events.Count(e => e.OrderBook is not null), 1, 5);
        // Saturate the outbound queue. Ingestion must still complete independently.
        for (var n = 0; n < 300; n++) publisher.OrderBookChanged(workspace, id, cache.Read(id));
        await first.Frames.Writer.WriteAsync("""{"event_type":"price_change","market":"condition","timestamp":"1790035200000","price_changes":[{"asset_id":"123","side":"BUY","price":"0.4","size":"999"}]}""");
        await Until(() => cache.Read(id).Snapshot?.Bids[0].Quantity == 999m);
        manager.Stop(workspace, id); manager.Stop(workspace, id);
        Assert.False(cache.Read(id).Eligibility.IsActionable);
        await manager.StopAsync(default); Assert.Equal(1, first.Disposed); Assert.Contains(first.Sent, s => s.Contains("unsubscribe", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Instrument_limit_and_changed_set_invalidate_previous_anchor()
    {
        var factory = new Factory(); var clock = TimeProvider.System; var cache = new OrderBookCache(clock); var instance = new BackendInstance();
        using var manager = new RealtimeOrderBookManager(new(factory, new EmptyCredentials(), clock), cache, new(new(instance), instance), new() { RealtimeMaximumInstruments = 1 }, clock);
        await manager.StartAsync(default); var workspace = Guid.NewGuid(); var id = new OrderBookInstrumentId("Polymarket", "market", "123", "A");
        manager.Start(workspace, new(id, false));
        Assert.Throws<InvalidOperationException>(() => manager.Start(workspace, new(id with { NativeInstrumentId = "456" }, false)));
        Assert.Throws<ArgumentException>(() => manager.Start(Guid.NewGuid(), new(id, false)));
        await manager.StopAsync(default);
    }
    [Fact]
    public async Task Kalshi_without_credentials_does_not_stop_Polymarket_and_credential_change_stops_only_Kalshi()
    {
        var factory = new Factory(); var clock = TimeProvider.System; var cache = new OrderBookCache(clock); var instance = new BackendInstance();
        using var manager = new RealtimeOrderBookManager(new(factory, new EmptyCredentials(), clock), cache, new(new(instance), instance), new(), clock);
        await manager.StartAsync(default); var workspace = Guid.NewGuid(); var poly = new OrderBookInstrumentId("Polymarket", "market", "123", "A");
        var kalshi = new OrderBookInstrumentId("Kalshi", "TEST", "yes", "Yes");
        manager.Start(workspace, new(poly, false)); manager.Start(workspace, new(kalshi, true));
        await Until(() => cache.Read(kalshi).Realtime?.State == RealtimeSubscriptionState.AuthenticationRequired && cache.Read(poly).Realtime?.Connected == true);
        manager.CredentialsChanged(); Assert.Equal(RealtimeSubscriptionState.Stopped, cache.Read(kalshi).Realtime!.State);
        Assert.True(cache.Read(poly).Realtime!.Connected); await manager.StopAsync(default);
    }
}
