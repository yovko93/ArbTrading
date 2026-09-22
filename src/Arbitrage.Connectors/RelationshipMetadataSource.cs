using System.Text.Json;
using Arbitrage.Application;

namespace Arbitrage.Connectors;

// Explicit one-market enrichment, at most market + event + series; no document URLs are followed.
public sealed class RelationshipMetadataSource(HttpClient http) : IRelationshipMetadataSource
{
    public async Task<RelationshipMetadata> ReadAsync(string exchange, string nativeId, string? eventId, CancellationToken ct)
    {
        if (nativeId.Length is < 1 or > 256 || nativeId.Any(char.IsControl)) throw new ArgumentException("Invalid identity.");
        var endpoint = exchange switch { "Polymarket" => "https://gamma-api.polymarket.com/markets/", "Kalshi" => "https://external-api.kalshi.com/trade-api/v2/markets/", _ => throw new ArgumentException("Unknown exchange.") } + Uri.EscapeDataString(nativeId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var document = await ReadJsonAsync(endpoint, timeout.Token);
        var market = exchange == "Kalshi" ? document.RootElement.GetProperty("market") : document.RootElement;
        if (MarketJson.Text(market, exchange == "Kalshi" ? "ticker" : "id") != nativeId) throw new JsonException("Mismatched market identity.");
        string? source = MarketJson.Text(market, "resolutionSource");
        var evidence = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        string[] fields = exchange == "Kalshi"
            ? ["ticker", "title", "subtitle", "rules_primary", "rules_secondary", "yes_sub_title", "no_sub_title", "early_close_condition", "floor_strike", "cap_strike", "functional_strike", "custom_strike", "mve_collection_ticker", "mve_selected_legs", "occurrence_datetime", "market_type", "expiration_time", "close_time"]
            : ["id", "question", "description", "rules", "resolutionSource", "outcomes", "clobTokenIds", "negRisk", "negRiskMarketID", "startDate", "endDate"];
        foreach (var field in fields) if (market.TryGetProperty(field, out var value)) evidence[field] = value.Clone();
        if (exchange == "Kalshi")
        {
            var nativeEvent = MarketJson.Text(market, "event_ticker") ?? eventId;
            if (!string.IsNullOrWhiteSpace(nativeEvent))
            {
                using var parent = await ReadJsonAsync("https://external-api.kalshi.com/trade-api/v2/events/" + Uri.EscapeDataString(nativeEvent), timeout.Token);
                var ev = parent.RootElement.GetProperty("event");
                foreach (var field in new[] { "event_ticker", "series_ticker", "mutually_exclusive", "strike_date", "strike_period", "collateral_return_type" })
                    if (ev.TryGetProperty(field, out var value)) evidence["event." + field] = value.Clone();
                var series = MarketJson.Text(ev, "series_ticker");
                if (!string.IsNullOrWhiteSpace(series))
                {
                    using var seriesDoc = await ReadJsonAsync("https://external-api.kalshi.com/trade-api/v2/series/" + Uri.EscapeDataString(series), timeout.Token);
                    var seriesRoot = seriesDoc.RootElement.GetProperty("series");
                    if (seriesRoot.TryGetProperty("settlement_sources", out var sources)) { evidence["settlement_sources"] = sources.Clone(); source = sources.GetRawText(); }
                    foreach (var field in new[] { "contract_url", "contract_terms_url", "additional_prohibitions" })
                        if (seriesRoot.TryGetProperty(field, out var value)) evidence[field] = value.Clone();
                }
            }
        }
        var rules = exchange == "Polymarket" ? MarketJson.Text(market, "rules") ?? MarketJson.Text(market, "description") :
            string.Join("\n", new[] { MarketJson.Text(market, "rules_primary"), MarketJson.Text(market, "rules_secondary") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var warnings = new List<string>();
        var labels = MarketJson.StringArray(market, "outcomes", warnings); var tokens = MarketJson.StringArray(market, "clobTokenIds", warnings);
        MarketOutcome[] outcomes = exchange == "Kalshi" ? [new("Yes", null), new("No", null)] :
            labels.Length == tokens.Length ? labels.Select((label, i) => new MarketOutcome(label, tokens[i])).ToArray() : [];
        return new(MarketJson.Text(market, exchange == "Polymarket" ? "question" : "title"), MarketJson.Text(market, "description"), rules, source, endpoint,
            JsonSerializer.Serialize(evidence), DateTimeOffset.UtcNow, outcomes,
            MarketJson.Date(market, exchange == "Kalshi" ? "open_time" : "startDate"), MarketJson.Date(market, exchange == "Kalshi" ? "close_time" : "endDate"),
            exchange == "Kalshi" ? MarketJson.Date(market, "expected_expiration_time") : null,
            exchange == "Kalshi" ? MarketJson.Text(market, "mve_collection_ticker") is not null ? "Multivariate" : MarketJson.Text(market, "market_type") : MarketJson.Flag(market, "negRisk") == true ? "NegativeRisk" : "Standard");
    }
    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 1_000_000) throw new JsonException("Metadata response exceeded bound.");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
