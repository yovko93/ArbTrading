using System.Runtime.InteropServices;
using System.Net.Http;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

// Opt-in only: one production adapter page per exchange, then isolated SQLite verification.
public sealed class LiveMarketAdapterSample(ITestOutputHelper output)
{
    [Fact]
    public async Task Sample_public_adapters()
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_LIVE_MARKET_SAMPLE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-sample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var exchange in new[] { "Kalshi", "Polymarket" })
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                IMarketDiscoverySource source = exchange == "Kalshi" ? new KalshiMarketSource(http, maxAttempts: 1) : new PolymarketMarketSource(http, maxAttempts: 1);
                var scope = exchange == "Kalshi" ? "open" : "nonfinalized";
                var at = DateTimeOffset.UtcNow;
                try
                {
                    // This path is intentionally sampled: one page, five records at most, never a Complete run.
                    var page = await source.ReadPageAsync(scope, null, 5, at.AddSeconds(15), default);
                    await using var db = new TradingDbContext(DatabaseOptions.ForFile(Path.Combine(root, exchange + ".db")));
                    await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
                    var profile = await db.LocalProfiles.SingleAsync();
                    var run = new DiscoveryRunEntry { Id = Guid.NewGuid(), Exchange = exchange,
                        Scope = "Sampled one-page adapter check", OwnerUserId = profile.UserId,
                        WorkspaceId = profile.DefaultWorkspaceId, StartedAt = at };
                    var store = new MarketCatalogStore(db);
                    await store.CreateRunAsync(run, default);
                    await store.UpsertPageAsync(run.Id, scope, page.Markets, page.MalformedRecords, default);
                    await store.FinishRunAsync(run.Id, "Partial", "Sampled", null, default);
                    var count = await store.CountAsync(exchange, default);
                    output.WriteLine(JsonSerializer.Serialize(new { AtUtc = at, Exchange = exchange,
                        Host = exchange == "Kalshi" ? "external-api.kalshi.com" : "gamma-api.polymarket.com",
                        RequestBudget = 1, PageSize = 5, Classification = "Sampled",
                        Parsed = page.Markets.Length, Stored = count, NextCursorPresent = page.NextCursor is not null,
                        Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription }));
                }
                catch (Exception error)
                {
                    output.WriteLine(JsonSerializer.Serialize(new { AtUtc = at, Exchange = exchange,
                        Host = exchange == "Kalshi" ? "external-api.kalshi.com" : "gamma-api.polymarket.com",
                        RequestBudget = 1, PageSize = 5, Classification = error is MarketDiscoveryException discovery ? discovery.Code : error.GetType().Name,
                        ExceptionType = error.GetType().Name,
                        TransportError = (error.InnerException as HttpRequestException)?.HttpRequestError.ToString(),
                        CauseType = error.InnerException?.InnerException?.GetType().Name ?? error.InnerException?.GetType().Name,
                        Runtime = RuntimeInformation.FrameworkDescription,
                        OS = RuntimeInformation.OSDescription }));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
