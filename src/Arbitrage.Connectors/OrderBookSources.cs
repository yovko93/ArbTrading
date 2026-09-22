using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Connectors;

// Separate, fixed-host public clients. No local backend credentials or trading endpoints.
public abstract class RestOrderBookSource(HttpClient http, TimeProvider? clock = null, int maxAttempts = 2) : IOrderBookSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    protected TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public const int MaximumResponseBytes = 2_000_000;
    public abstract string Exchange { get; }
    protected abstract string Url(OrderBookRequest request);
    public abstract OrderBookSnapshot Parse(JsonElement root, OrderBookRequest request, DateTimeOffset retrieved);
    public async Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken cancellationToken)
    {
        if (request.Instrument.Exchange != Exchange || string.IsNullOrWhiteSpace(request.Instrument.NativeInstrumentId))
            throw Error("NotAddressable");
        // Do not inherit authorization accidentally if a caller supplies a misconfigured client.
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Any(h => h.Key.StartsWith("KALSHI-ACCESS-", StringComparison.OrdinalIgnoreCase)))
            throw Error("PublicClientMisconfigured");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = false;
        try
        {
            await gate.WaitAsync(budget.Token); entered = true;
            for (var attempt = 0; attempt < Math.Clamp(maxAttempts, 1, 2); attempt++)
            {
                var delay = nextRequest - Clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, budget.Token);
                nextRequest = Clock.GetUtcNow().AddMilliseconds(300);
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, Url(request));
                    using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw Error("PublicAccessDenied");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    {
                        var retry = response.Headers.RetryAfter?.Date ?? Clock.GetUtcNow() +
                            (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1));
                        var code = response.StatusCode == HttpStatusCode.TooManyRequests ? "RateLimited" : "UpstreamUnavailable";
                        if (attempt + 1 >= maxAttempts || retry - Clock.GetUtcNow() > TimeSpan.FromSeconds(3))
                            throw new MarketDiscoveryException(code, "Public orderbook request deferred.", retry);
                        nextRequest = retry > nextRequest ? retry : nextRequest; continue;
                    }
                    if (!response.IsSuccessStatusCode) throw Error("UpstreamRejected");
                    if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Error("PayloadTooLarge");
                    await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                    using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(bytes, budget.Token)) > 0)
                    {
                        if (buffer.Length + read > MaximumResponseBytes) throw Error("PayloadTooLarge");
                        buffer.Write(bytes, 0, read);
                    }
                    buffer.Position = 0;
                    using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: budget.Token);
                    return Parse(json.RootElement, request, Clock.GetUtcNow());
                }
                catch (JsonException) { throw Error("InvalidJson"); }
                catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.SecureConnectionError || e.InnerException is AuthenticationException)
                { throw new MarketDiscoveryException("SecureConnectionFailure", "Public orderbook TLS validation failed.", innerException: e); }
                catch (HttpRequestException e)
                {
                    if (attempt + 1 >= maxAttempts) throw new MarketDiscoveryException("TransportFailure", "Public orderbook transport failed.", innerException: e);
                }
            }
            throw Error("TransportFailure");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("RequestTimeout"); }
        finally { if (entered) gate.Release(); }
    }
    protected static MarketDiscoveryException Error(string code) => new(code, "Public orderbook could not be read safely.");
    protected static JsonElement Array(JsonElement root, string name)
    {
        var value = MarketJson.Member(root, name);
        if (value is not { ValueKind: JsonValueKind.Array }) throw Error("InvalidEnvelope");
        if (value.Value.GetArrayLength() > OrderBookNormalizer.MaximumLevelsPerSide) throw Error("InvalidOrderBook");
        return value.Value;
    }
    protected static decimal Number(JsonElement value, int maxScale = 28)
    {
        if (value.ValueKind != JsonValueKind.String) throw Error("InvalidOrderBook");
        var text = value.GetString()!;
        // Reject rounding, exponent notation, whitespace, NaN, overflow and locale-dependent input.
        var scale = text.Contains('.') ? text.Length - text.IndexOf('.') - 1 : 0;
        if (text.Length is 0 or > 29 || scale > maxScale || text.Any(c => c is not (>= '0' and <= '9') && c != '.' && c != '-') ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            throw Error("InvalidOrderBook");
        return number;
    }
}

public sealed class PolymarketOrderBookSource(HttpClient http, TimeProvider? clock = null, int maxAttempts = 2)
    : RestOrderBookSource(http, clock, maxAttempts)
{
    public override string Exchange => "Polymarket";
    protected override string Url(OrderBookRequest request) => "https://clob.polymarket.com/book?token_id=" + Uri.EscapeDataString(request.Instrument.NativeInstrumentId);
    public override OrderBookSnapshot Parse(JsonElement root, OrderBookRequest request, DateTimeOffset retrieved)
    {
        var asset = MarketJson.Text(root, "asset_id"); var market = MarketJson.Text(root, "market");
        if (string.IsNullOrWhiteSpace(asset) || asset != request.Instrument.NativeInstrumentId || string.IsNullOrWhiteSpace(market))
            throw Error("InvalidOrderBook");
        DateTimeOffset? source = null;
        if (MarketJson.Member(root, "timestamp") is { } timestamp && timestamp.ValueKind != JsonValueKind.Null)
        {
            if (!long.TryParse(MarketJson.Text(root, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)) throw Error("InvalidOrderBook");
            try { source = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { throw Error("InvalidOrderBook"); }
        }
        var bids = Levels(Array(root, "bids"), LiquidityOrigin.NativeBid);
        var asks = Levels(Array(root, "asks"), LiquidityOrigin.NativeAsk);
        return OrderBookNormalizer.Normalize(request.Instrument, bids, asks, retrieved, source, market, MarketJson.Text(root, "hash"));
    }
    private static OrderBookLevel[] Levels(JsonElement array, LiquidityOrigin origin) => array.EnumerateArray().Select(l =>
        new OrderBookLevel(Number(MarketJson.Member(l, "price") ?? default), Number(MarketJson.Member(l, "size") ?? default), origin)).ToArray();
}

public sealed class KalshiOrderBookSource(HttpClient http, TimeProvider? clock = null, int maxAttempts = 2)
    : RestOrderBookSource(http, clock, maxAttempts)
{
    public override string Exchange => "Kalshi";
    protected override string Url(OrderBookRequest request) => "https://external-api.kalshi.com/trade-api/v2/markets/" +
        Uri.EscapeDataString(request.Instrument.NativeMarketId) + "/orderbook?depth=0";
    public override OrderBookSnapshot Parse(JsonElement root, OrderBookRequest request, DateTimeOffset retrieved)
    {
        if (request.Instrument.NativeInstrumentId is not ("yes" or "no")) throw Error("NotAddressable");
        var fp = MarketJson.Member(root, "orderbook_fp");
        if (fp is not { ValueKind: JsonValueKind.Object }) throw Error("InvalidEnvelope");
        var yes = Levels(Array(fp.Value, "yes_dollars")); var no = Levels(Array(fp.Value, "no_dollars"));
        var own = request.Instrument.NativeInstrumentId == "yes" ? yes : no;
        var opposite = request.Instrument.NativeInstrumentId == "yes" ? no : yes;
        // Validate every native level before taking complements; never transform invalid source data.
        var native = OrderBookNormalizer.Normalize(request.Instrument, own, [], retrieved, oppositeBids: opposite);
        if (native.Validity == BookValidity.Invalid) return native;
        var asks = request.BinarySupported ? opposite.Select(l => new OrderBookLevel(1m - l.Price, l.Quantity, LiquidityOrigin.DerivedComplement)) : [];
        return OrderBookNormalizer.Normalize(request.Instrument, own, asks, retrieved, sourceMarket: request.Instrument.NativeMarketId,
            oppositeBids: opposite, supported: request.BinarySupported);
    }
    private static OrderBookLevel[] Levels(JsonElement array) => array.EnumerateArray().Select(l =>
    {
        if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() != 2) throw Error("InvalidOrderBook");
        return new OrderBookLevel(Number(l[0], 4), Number(l[1], 2), LiquidityOrigin.NativeBid);
    }).ToArray();
}
