using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Connectors;

// This client has no access to BackendClient, signing keys or account services. Redirects disabled at registration.
public sealed class PublicFeeSource(HttpClient http, TimeProvider clock) : IPublicFeeSource
{
    private const string Kalshi = "https://external-api.kalshi.com/trade-api/v2/";
    public async Task<FeeSchedule> ReadAsync(string exchange, string marketId, CancellationToken ct)
    {
        var acquisitionStarted = clock.GetUtcNow();
        if (marketId.Length is < 1 or > 256 || marketId.Any(char.IsControl)) throw new ArgumentException("Invalid market identity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        if (exchange == "Polymarket")
        {
            var url = "https://gamma-api.polymarket.com/markets/" + Uri.EscapeDataString(marketId);
            using var document = await JsonAsync(url, token);
            return ParsePolymarket(document.RootElement, marketId, clock.GetUtcNow(), url);
        }
        if (exchange != "Kalshi") throw new ArgumentException("Unsupported exchange.");
        using var marketDoc = await JsonAsync(Kalshi + "markets/" + Uri.EscapeDataString(marketId), token);
        var market = marketDoc.RootElement.GetProperty("market");
        Require(MarketJson.Text(market, "ticker") == marketId);
        var eventId = Required(market, "event_ticker");
        using var eventDoc = await JsonAsync(Kalshi + "events/" + Uri.EscapeDataString(eventId), token);
        var ev = eventDoc.RootElement.GetProperty("event"); Require(Required(ev, "event_ticker") == eventId);
        var seriesId = Required(ev, "series_ticker");
        using var seriesDoc = await JsonAsync(Kalshi + "series/" + Uri.EscapeDataString(seriesId), token);
        var series = seriesDoc.RootElement.GetProperty("series"); Require(Required(series, "ticker") == seriesId);
        // Current snapshots establish a baseline at retrieval start. Only future changes can supersede that baseline.
        var baseline = acquisitionStarted;
        var rules = new List<FeeRule> { new("current-series", Required(series, "fee_type"), Number(series, "fee_multiplier"), baseline, FeeSourceLevel.Series) };
        rules.Add(Override(ev, "current-event", baseline));
        using var changes = await JsonAsync(Kalshi + "series/fee_changes?series_ticker=" + Uri.EscapeDataString(seriesId), token);
        foreach (var change in Bounded(changes.RootElement.GetProperty("series_fee_change_arr")))
        {
            Require(Required(change, "series_ticker") == seriesId);
            var at = Date(change, "scheduled_ts");
            // A change crossing the acquisition window is ambiguous; require an explicit subsequent refresh.
            Require(at > baseline);
            rules.Add(new(Required(change, "id"), Required(change, "fee_type"), Number(change, "fee_multiplier"), at, FeeSourceLevel.Series));
        }
        string? cursor = null; var cursors = new HashSet<string>();
        for (var page = 0; page < 4; page++)
        {
            using var changesDoc = await JsonAsync(Kalshi + "events/fee_changes?event_ticker=" + Uri.EscapeDataString(eventId) + "&limit=100" +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), token);
            foreach (var change in Bounded(changesDoc.RootElement.GetProperty("event_fee_changes")))
            {
                Require(Required(change, "event_ticker") == eventId && Required(change, "series_ticker") == seriesId);
                var at = Date(change, "scheduled_ts");
                // This endpoint may include history. Current event snapshot supersedes history strictly before baseline.
                if (at > baseline) rules.Add(Override(change, Required(change, "id"), at));
            }
            cursor = MarketJson.Text(changesDoc.RootElement, "cursor");
            if (string.IsNullOrEmpty(cursor)) break;
            Require(cursor.Length <= 4096 && cursors.Add(cursor) && page < 3);
        }
        Require(rules.Count <= 512 && !rules.Any(r => r.EffectiveFrom > baseline && r.EffectiveFrom <= clock.GetUtcNow()));
        var issue = "Verification 2026-09-23: regulatory PDF centicent alignment/whole-cent examples conflict with API six-decimal, account-specific rounding. Total unresolved.";
        if (MarketJson.Text(market, "mve_collection_ticker") is not null || MarketJson.Date(market, "fee_waiver_expiration_time") is not null)
            issue += " Combo or market fee-waiver semantics require verification.";
        return new(exchange, marketId, eventId, seriesId, "USD", baseline, MarketJson.Date(series, "last_updated_ts"),
            Kalshi + "series/" + Uri.EscapeDataString(seriesId) + " ; " + Kalshi + "events/" + Uri.EscapeDataString(eventId) +
            " ; https://docs.kalshi.com/getting_started/fee_rounding ; https://kalshi.com/docs/kalshi-fee-schedule.pdf",
            rules.OrderBy(r => r.Level).ThenBy(r => r.EffectiveFrom).ThenBy(r => r.Id, StringComparer.Ordinal).ToImmutableArray(), ["yes", "no"], issue);
    }
    public static FeeSchedule ParsePolymarket(JsonElement market, string marketId, DateTimeOffset now, string source)
    {
        Require(Required(market, "id") == marketId);
        var warnings = new List<string>(); var instruments = MarketJson.StringArray(market, "clobTokenIds", warnings);
        Require(warnings.Count == 0 && instruments.Length is > 0 and <= 100 && instruments.All(i => i.Length > 0 && i.All(char.IsAsciiDigit)));
        var enabled = MarketJson.Flag(market, "feesEnabled");
        var metadata = MarketJson.Member(market, "feeSchedule");
        decimal? rate = enabled == false ? 0 : metadata is { ValueKind: JsonValueKind.Object } ? Number(metadata.Value, "rate") : null;
        var type = "prediction-quadratic-v1";
        // The collateral page explicitly describes pUSD as a USDC claim and native-USDC settlement.
        // Keep the fee page's USDC denomination; cross-currency aggregation is separately blocked.
        string? issue = null;
        if (enabled is null || enabled == true && rate is null) return new("Polymarket", marketId, null, null, "USDC", now, null, source, [], [.. instruments], "Market fee parameters unavailable.");
        if (metadata is { ValueKind: JsonValueKind.Object })
        {
            if (Number(metadata.Value, "exponent") != 1 || MarketJson.Flag(metadata.Value, "takerOnly") != true) type = "unsupported-prediction-model";
            if (enabled == false && Number(metadata.Value, "rate") is > 0) issue = "Conflicting feesEnabled and feeSchedule.rate.";
        }
        return new("Polymarket", marketId, null, null, "USDC", now, MarketJson.Date(market, "updatedAt"), source,
            [new("market", type, rate, now, FeeSourceLevel.Market)], [.. instruments], issue);
    }
    private static FeeRule Override(JsonElement element, string id, DateTimeOffset at)
    {
        // Missing fields are not evidence of an explicit clear.
        Require(element.TryGetProperty("fee_type_override", out var type) && element.TryGetProperty("fee_multiplier_override", out _));
        var rate = Number(element, "fee_multiplier_override"); var name = MarketJson.Text(element, "fee_type_override");
        Require((name is null) == (rate is null));
        return new(id, name, rate, at, FeeSourceLevel.EventOverride, type.ValueKind == JsonValueKind.Null && rate is null);
    }
    private static IEnumerable<JsonElement> Bounded(JsonElement items)
    { Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 100); return items.EnumerateArray(); }
    private static string Required(JsonElement element, string name)
    { var value = MarketJson.Text(element, name); Require(value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl)); return value!; }
    private static DateTimeOffset Date(JsonElement element, string name) => MarketJson.Date(element, name) ?? throw new JsonException("Invalid fee effective time.");
    private static decimal? Number(JsonElement element, string name)
    {
        var item = MarketJson.Member(element, name);
        if (item is null || item.Value.ValueKind == JsonValueKind.Null) return null;
        if (decimal.TryParse(MarketJson.Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        throw new JsonException("Invalid decimal fee parameter.");
    }
    private static void Require(bool condition) { if (!condition) throw new JsonException("Invalid, ambiguous or excessive public fee metadata."); }
    private async Task<JsonDocument> JsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromSeconds(attempt + 1);
                // Never shorten Retry-After. Refuse a retry exceeding this bounded operation instead.
                if (delay > TimeSpan.FromSeconds(10)) throw new HttpRequestException("Public fee retry exceeds budget.");
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1_000_000) throw new JsonException("Fee response bound exceeded.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(chunk, ct)) > 0)
            { if (buffer.Length + count > 1_000_000) throw new JsonException("Fee response bound exceeded."); buffer.Write(chunk, 0, count); }
            return JsonDocument.Parse(buffer.ToArray());
        }
        throw new HttpRequestException("Public fee service unavailable.");
    }
}
