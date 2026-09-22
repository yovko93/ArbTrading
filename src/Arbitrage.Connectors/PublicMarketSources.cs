using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Arbitrage.Application;

namespace Arbitrage.Connectors;

public sealed record PublicMarketPacingOptions(TimeSpan PolymarketInterval, TimeSpan KalshiInterval);

public static class PublicMarketTransport
{
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseProxy = false };
}

internal static class MarketJson
{
    public static string? Cursor(JsonElement root, string name, bool required)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MarketDiscoveryException("InvalidEnvelope", "Public market page is not an object.");
        if (!root.TryGetProperty(name, out var value))
        {
            if (!required) return null; // Polymarket documents omission on the final page.
            throw new MarketDiscoveryException("InvalidEnvelope", $"Public market page has no {name} continuation field.");
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new MarketDiscoveryException("InvalidEnvelope", $"Public market page has an invalid {name} continuation field.");
        var cursor = value.GetString();
        if (cursor is { Length: > 4096 } || cursor?.Any(char.IsControl) == true || !required && cursor == "")
            throw new MarketDiscoveryException("InvalidEnvelope", $"Public market page has an invalid {name} continuation field.");
        return cursor == "" ? null : cursor;
    }
    public static string? Text(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
    public static bool? Flag(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    public static DateTimeOffset? Date(JsonElement item, string name) =>
        DateTimeOffset.TryParse(Text(item, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
    public static JsonElement? Member(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : null;
    public static JsonElement? First(JsonElement item, string name)
    {
        var value = Member(item, name);
        return value is { ValueKind: JsonValueKind.Array } && value.Value.GetArrayLength() > 0 ? value.Value[0] : null;
    }
    public static string[] StringArray(JsonElement item, string name, List<string> warnings)
    {
        var value = Member(item, name);
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (value.Value.ValueKind == JsonValueKind.String)
        {
            try { using var decoded = JsonDocument.Parse(value.Value.GetString()!); return ReadArray(decoded.RootElement, warnings); }
            catch (JsonException) { warnings.Add($"Invalid {name} array"); return []; }
        }
        return ReadArray(value.Value, warnings);
    }
    private static string[] ReadArray(JsonElement array, List<string> warnings)
    {
        if (array.ValueKind != JsonValueKind.Array) { warnings.Add("Outcome array has invalid shape"); return []; }
        var result = new List<string>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) { warnings.Add("Outcome array contains invalid value"); return []; }
            result.Add(value.GetString()!);
        }
        return [.. result];
    }
}

// Dedicated clients are registered by the backend. URLs are fixed to documented public HTTPS hosts.
public abstract class PublicMarketSource(HttpClient http, TimeSpan minimumInterval, int maxAttempts = 3) : IMarketDiscoverySource
{
    private readonly SemaphoreSlim pageGate = new(1, 1);
    private DateTimeOffset nextRequest;
    public abstract string Exchange { get; }
    public abstract IReadOnlyList<string> Scopes { get; }
    protected abstract string Root { get; }
    protected abstract string BuildPath(string scope, string? cursor, int pageSize);
    protected abstract MarketDiscoveryPage Parse(JsonElement root, DateTimeOffset retrieved);

    public async Task<MarketDiscoveryPage> ReadPageAsync(string scope, string? cursor, int pageSize,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (!Scopes.Contains(scope, StringComparer.Ordinal) || pageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(scope));
        await pageGate.WaitAsync(cancellationToken);
        try
        {
            var path = BuildPath(scope, cursor, pageSize);
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var now = DateTimeOffset.UtcNow;
                var wait = nextRequest - now;
                if (wait > TimeSpan.Zero)
                {
                    if (now + wait >= deadline) throw new MarketDiscoveryException("BudgetExceeded", "Request pacing reached the run budget.");
                    await Task.Delay(wait, cancellationToken);
                }
                if (DateTimeOffset.UtcNow >= deadline) throw new MarketDiscoveryException("BudgetExceeded", "The run time budget was reached.");
                nextRequest = DateTimeOffset.UtcNow + minimumInterval;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(12, Math.Max(0.1, (deadline - DateTimeOffset.UtcNow).TotalSeconds))));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new MarketDiscoveryException("PublicAccessDenied", $"{Exchange} public market access was denied.");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    {
                        var retry = response.Headers.RetryAfter?.Date ??
                            DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1));
                        if (retry >= deadline || attempt == maxAttempts - 1)
                            throw new MarketDiscoveryException(response.StatusCode == HttpStatusCode.TooManyRequests ? "RateLimited" : "UpstreamUnavailable",
                                $"{Exchange} public market request could not continue.", retry);
                        nextRequest = retry > nextRequest ? retry : nextRequest;
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new MarketDiscoveryException("UpstreamRejected", $"{Exchange} public market request was rejected.");
                    if (response.Content.Headers.ContentLength is > 8_000_000)
                        throw new MarketDiscoveryException("PayloadTooLarge", "Public market page exceeded the response limit.");
                    await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    await using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int count;
                    while ((count = await body.ReadAsync(chunk, timeout.Token)) > 0)
                    {
                        if (buffer.Length + count > 8_000_000)
                            throw new MarketDiscoveryException("PayloadTooLarge", "Public market page exceeded the response limit.");
                        buffer.Write(chunk, 0, count);
                    }
                    buffer.Position = 0;
                    using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
                    return Parse(document.RootElement, DateTimeOffset.UtcNow);
                }
                catch (JsonException) { throw new MarketDiscoveryException("InvalidJson", $"{Exchange} returned invalid JSON."); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow >= deadline)
                { throw new MarketDiscoveryException("BudgetExceeded", "The run time budget was reached."); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == maxAttempts - 1)
                { throw new MarketDiscoveryException("RequestTimeout", $"{Exchange} public market request timed out."); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt + 1); }
                catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError ||
                    exception.InnerException is AuthenticationException)
                { throw new MarketDiscoveryException("SecureConnectionFailure", $"{Exchange} secure connection failed.", innerException: exception); }
                catch (HttpRequestException exception) when (attempt == maxAttempts - 1)
                { throw new MarketDiscoveryException("TransportFailure", $"{Exchange} public market transport failed.", innerException: exception); }
                catch (HttpRequestException)
                { nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt + 1); }
            }
            throw new MarketDiscoveryException("RequestTimeout", $"{Exchange} public market request timed out.");
        }
        finally { pageGate.Release(); }
    }
}

public sealed class PolymarketMarketSource(HttpClient http, PublicMarketPacingOptions? pacing = null, int maxAttempts = 3)
    : PublicMarketSource(http, pacing?.PolymarketInterval ?? TimeSpan.FromMilliseconds(300), maxAttempts)
{
    public override string Exchange => "Polymarket";
    public override IReadOnlyList<string> Scopes { get; } = ["nonfinalized"];
    protected override string Root => "https://gamma-api.polymarket.com";
    protected override string BuildPath(string scope, string? cursor, int pageSize) =>
        $"/markets/keyset?closed=false&limit={Math.Min(pageSize, 100)}" +
        (cursor is null ? "" : "&after_cursor=" + Uri.EscapeDataString(cursor));
    protected override MarketDiscoveryPage Parse(JsonElement root, DateTimeOffset retrieved)
    {
        var values = MarketJson.Member(root, "markets");
        if (values is not { ValueKind: JsonValueKind.Array })
            throw new MarketDiscoveryException("InvalidEnvelope", "Polymarket market page has no markets array.");
        var markets = new List<DiscoveredMarket>(); var malformed = 0;
        foreach (var raw in values.Value.EnumerateArray())
        {
            var id = MarketJson.Text(raw, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) { malformed++; continue; }
            var warnings = new List<string>();
            var labels = MarketJson.StringArray(raw, "outcomes", warnings);
            var tokens = MarketJson.StringArray(raw, "clobTokenIds", warnings);
            MarketOutcome[] outcomes = [];
            if (tokens.Length > 0 && labels.Length != tokens.Length) warnings.Add("Outcome labels and token IDs do not align");
            else outcomes = labels.Select((label, index) => new MarketOutcome(label, tokens.Length > 0 ? tokens[index] : null)).ToArray();
            var eventInfo = MarketJson.First(raw, "events");
            var tags = MarketJson.Member(raw, "tags");
            var tagNames = tags is { ValueKind: JsonValueKind.Array }
                ? tags.Value.EnumerateArray().Select(t => MarketJson.Text(t, "label") ?? MarketJson.Text(t, "slug"))
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Cast<string>().ToArray() : [];
            if (tagNames.Length == 0) warnings.Add("Tags unavailable in market listing");
            if (eventInfo is null) warnings.Add("Event reference unavailable in market listing");
            var closed = MarketJson.Flag(raw, "closed");
            var active = MarketJson.Flag(raw, "active");
            var status = closed == true ? "Closed" : closed == false && active == true ? "OpenOrPaused" :
                closed == false ? "UpcomingOrPaused" : "Unknown";
            var title = MarketJson.Text(raw, "question");
            if (string.IsNullOrWhiteSpace(title)) warnings.Add("Title missing");
            if (labels.Length == 0) warnings.Add("Outcome labels missing");
            var slug = MarketJson.Text(raw, "slug");
            markets.Add(new(Exchange, "Production", id, eventInfo is { } e ? MarketJson.Text(e, "id") : null,
                null, eventInfo is { } group ? MarketJson.Text(group, "negRiskMarketID") : null,
                MarketJson.Flag(raw, "negRisk") == true ? "NegativeRisk" : "Standard", title,
                eventInfo is { } parent ? MarketJson.Text(parent, "title") : null,
                null, tagNames, $"active={active?.ToString() ?? "unknown"};closed={closed?.ToString() ?? "unknown"};acceptingOrders={MarketJson.Flag(raw, "acceptingOrders")?.ToString() ?? "unknown"}",
                status, outcomes, MarketJson.Date(raw, "createdAt"), MarketJson.Date(raw, "startDate"),
                MarketJson.Date(raw, "endDate"), null, MarketJson.Date(raw, "closedTime"),
                MarketJson.Date(raw, "updatedAt"), MarketJson.Text(raw, "description"),
                MarketJson.Text(raw, "rules"), slug is null ? null : "https://polymarket.com/market/" + Uri.EscapeDataString(slug),
                retrieved, [.. warnings]));
        }
        var next = MarketJson.Cursor(root, "next_cursor", required: false);
        return new([.. markets], next, malformed, []);
    }
}

public sealed class KalshiMarketSource(HttpClient http, PublicMarketPacingOptions? pacing = null, int maxAttempts = 3)
    : PublicMarketSource(http, pacing?.KalshiInterval ?? TimeSpan.FromMilliseconds(300), maxAttempts)
{
    public override string Exchange => "Kalshi";
    public override IReadOnlyList<string> Scopes { get; } = ["unopened", "open", "paused", "closed"];
    protected override string Root => "https://external-api.kalshi.com";
    protected override string BuildPath(string scope, string? cursor, int pageSize) =>
        $"/trade-api/v2/markets?status={scope}&limit={pageSize}" +
        (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
    protected override MarketDiscoveryPage Parse(JsonElement root, DateTimeOffset retrieved)
    {
        var values = MarketJson.Member(root, "markets");
        if (values is not { ValueKind: JsonValueKind.Array })
            throw new MarketDiscoveryException("InvalidEnvelope", "Kalshi market page has no markets array.");
        var markets = new List<DiscoveredMarket>(); var malformed = 0;
        foreach (var raw in values.Value.EnumerateArray())
        {
            var ticker = MarketJson.Text(raw, "ticker");
            if (string.IsNullOrWhiteSpace(ticker) || ticker.Length > 256 || ticker.Any(char.IsControl)) { malformed++; continue; }
            var warnings = new List<string>();
            var title = MarketJson.Text(raw, "title");
            if (string.IsNullOrWhiteSpace(title)) warnings.Add("Title missing");
            // Kalshi's list response does not include series category or tags; no per-market enrichment.
            warnings.Add("Category unavailable in market listing");
            var status = MarketJson.Text(raw, "status");
            var normalized = MarketDiscoverySemantics.KalshiStatus(status);
            if (normalized == "Unknown") warnings.Add("Unknown native market status");
            var rules = string.Join("\n", new[] { MarketJson.Text(raw, "rules_primary"), MarketJson.Text(raw, "rules_secondary") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            markets.Add(new(Exchange, "Production", ticker, MarketJson.Text(raw, "event_ticker"),
                MarketJson.Text(raw, "series_ticker"), MarketJson.Text(raw, "mve_collection_ticker"),
                string.IsNullOrWhiteSpace(MarketJson.Text(raw, "mve_collection_ticker"))
                    ? MarketJson.Text(raw, "market_type") : "Multivariate", title, MarketJson.Text(raw, "subtitle"),
                null, [], status, normalized,
                [new("Yes", null), new("No", null)],
                MarketJson.Date(raw, "created_time"), MarketJson.Date(raw, "open_time"),
                MarketJson.Date(raw, "close_time"), MarketJson.Date(raw, "expected_expiration_time"),
                MarketJson.Date(raw, "settlement_ts"), MarketJson.Date(raw, "updated_time"),
                null, rules.Length == 0 ? null : rules,
                "https://external-api.kalshi.com/trade-api/v2/markets/" + Uri.EscapeDataString(ticker),
                retrieved, [.. warnings]));
        }
        var next = MarketJson.Cursor(root, "cursor", required: true);
        return new([.. markets], next, malformed, []);
    }
}
