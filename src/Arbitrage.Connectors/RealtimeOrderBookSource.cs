using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Connectors;

public interface IMarketWebSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}
public interface IMarketWebSocketFactory { IMarketWebSocket Create(); }
public sealed class MarketWebSocketFactory : IMarketWebSocketFactory { public IMarketWebSocket Create() => new MarketWebSocket(); }
public sealed class MarketWebSocket : IMarketWebSocket
{
    private readonly ClientWebSocket socket = new();
    private readonly byte[] buffer = new byte[8192];
    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        foreach (var h in headers) socket.Options.SetRequestHeader(h.Key, h.Value);
        try { await socket.ConnectAsync(uri, ct); }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new RealtimeProtocolException(headers.Count > 0 ? "AuthenticationFailed" : "NetworkRestricted"); }
    }
    public Task SendAsync(string text, CancellationToken ct) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var output = new MemoryStream();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Cancelling ClientWebSocket.ReceiveAsync itself aborts the socket before unsubscribe
            // can be sent. Cancel the wait instead; disposal settles the one outstanding receive.
            var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            WebSocketReceiveResult frame;
            try { frame = await receive.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { _ = ObserveReceiveAsync(receive); throw; }
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text || output.Length + frame.Count > RealtimeBookSession.MaximumFrameBytes)
                throw new RealtimeProtocolException("FrameTooLargeOrBinary");
            output.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }
    }
    private static async Task ObserveReceiveAsync(Task<WebSocketReceiveResult> receive)
    { try { await receive; } catch (Exception) { /* Closing/disposal settles the bounded pending receive. */ } }
    public async Task CloseAsync(CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
    }
    public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class RealtimeOrderBookSource(IMarketWebSocketFactory factory, IExchangeCredentialStore credentials, TimeProvider clock)
{
    public const string PolymarketUrl = "wss://ws-subscriptions-clob.polymarket.com/ws/market";
    public const string KalshiUrl = "wss://external-api-ws.kalshi.com/trade-api/ws/v2";
    public const int MaximumAttempts = 5;
    private long generation;
    public async Task RunAsync(string exchange, OrderBookRequest[] requests,
        Action<OrderBookInstrumentId, RealtimeBookMetadata, OrderBookSnapshot?> publish, CancellationToken ct)
    {
        string? lastReason = null;
        void Publish(OrderBookInstrumentId id, RealtimeBookMetadata metadata, OrderBookSnapshot? book)
        {
            if (metadata.ResyncReason is not null) lastReason = metadata.ResyncReason;
            publish(id, metadata with { ResyncReason = lastReason }, book);
        }
        for (var attempt = 0; attempt < MaximumAttempts && !ct.IsCancellationRequested; attempt++)
        {
            var current = Interlocked.Increment(ref generation);
            void State(RealtimeSubscriptionState state, string? reason = null) {
                foreach (var r in requests) Publish(r.Instrument, new(current, state, BookContinuity.Disconnected, ResyncReason: reason), null);
            }
            State(RealtimeSubscriptionState.Connecting);
            await using var socket = factory.Create();
            RealtimeBookSession? session = null;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? ping = null;
            try
            {
                IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>();
                if (exchange == "Kalshi")
                {
                    using var lease = credentials.Open();
                    if (lease is null) { State(RealtimeSubscriptionState.AuthenticationRequired, "NotConfigured"); return; }
                    headers = KalshiWebSocketAuthentication.Headers(lease, clock.GetUtcNow());
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(new(exchange == "Kalshi" ? KalshiUrl : PolymarketUrl), headers, timeout.Token);
                }
                if (exchange == "Kalshi") credentials.RecordAuthentication("Authenticated");
                session = new(exchange, current, requests, clock, Publish);
                await socket.SendAsync(session.Subscribe(), ct);
                if (exchange == "Polymarket") ping = PingAsync(socket, connection.Token);
                while (!ct.IsCancellationRequested)
                {
                    using var receiveBudget = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    receiveBudget.CancelAfter(TimeSpan.FromSeconds(35));
                    var frame = await socket.ReceiveAsync(receiveBudget.Token);
                    if (frame is null) throw new RealtimeProtocolException("Disconnected");
                    session.Accept(frame, current);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                var reason = Classify(e);
                session?.Invalidate(reason, reason == "SequenceGap" ? BookContinuity.GapDetected : BookContinuity.Resynchronizing);
                if (reason == "AuthenticationFailed")
                { credentials.RecordAuthentication(reason); State(RealtimeSubscriptionState.AuthenticationFailed, reason); return; }
                if (e is CredentialOperationException)
                { State(RealtimeSubscriptionState.AuthenticationRequired, "CredentialStoreUnavailable"); return; }
                if (reason is "NetworkRestricted" or "SecureConnectionFailure")
                { State(RealtimeSubscriptionState.NetworkRestricted, reason); return; }
                if (attempt == MaximumAttempts - 1) { State(RealtimeSubscriptionState.Faulted, reason); return; }
            }
            finally
            {
                connection.Cancel();
                if (ping is not null) { try { await ping; } catch (Exception) { /* the receive budget bounds a failed keepalive */ } }
                using var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    if (session?.Unsubscribe() is { } unsubscribe) await socket.SendAsync(unsubscribe, closeBudget.Token);
                    await socket.CloseAsync(closeBudget.Token);
                }
                catch (Exception) { /* Disposal closes failed/aborted sockets; never log transport exception contents. */ }
            }
            if (ct.IsCancellationRequested) { State(RealtimeSubscriptionState.Stopped, "Stopped"); return; }
            var delay = TimeSpan.FromMilliseconds(Math.Min(16_000, 500 * (1 << attempt)) + Random.Shared.Next(0, 251));
            try { await Task.Delay(delay, clock, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { State(RealtimeSubscriptionState.Stopped); return; }
        }
    }
    private async Task PingAsync(IMarketWebSocket socket, CancellationToken ct)
    {
        while (true) { await Task.Delay(TimeSpan.FromSeconds(10), clock, ct); await socket.SendAsync("PING", ct); }
    }
    public static string Classify(Exception e)
    {
        if (e is RealtimeProtocolException protocol) return protocol.Message;
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
            if (inner is AuthenticationException || inner is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return "SecureConnectionFailure";
        return e is OperationCanceledException ? "ReceiveTimeout" : "TransportFailure";
    }
}
