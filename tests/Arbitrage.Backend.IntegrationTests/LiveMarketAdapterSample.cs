using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Infrastructure;
using Arbitrage.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

// Opt-in only. Uses production adapter, transport policy, parser, and isolated SQLite.
public sealed class LiveMarketAdapterSample(ITestOutputHelper output)
{
    [Fact]
    public async Task Sample_orderbook()
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_LIVE_ORDERBOOK_SAMPLE") != "1") return;
        output.WriteLine(JsonSerializer.Serialize(new { Exchange = "Polymarket", Classification = "NotAttemptedBecauseCatalogInstrumentUnavailable",
            Reason = "Gamma acquisition is network-restricted; no current catalog token safely available in this isolated sample." }));
        using var discoveryProbe = new ProbeHandler(PublicMarketTransport.CreateHandler());
        using var discoveryHttp = new HttpClient(discoveryProbe) { Timeout = TimeSpan.FromSeconds(12) };
        using var bookProbe = new ProbeHandler(PublicMarketTransport.CreateHandler());
        using var bookHttp = new HttpClient(bookProbe) { Timeout = TimeSpan.FromSeconds(12) };
        string? market = null; OrderBookSnapshot? book = null; string classification = "Unverified";
        var parsed = false; var normalized = false;
        try
        {
            // Only this diagnostic sample narrows discovery; normal catalog sync still includes MVE.
            var source = new KalshiMarketSource(discoveryHttp, maxAttempts: 1, excludeMultivariate: true);
            var page = await source.ReadPageAsync("open", null, 5, DateTimeOffset.UtcNow.AddSeconds(15), default);
            var candidate = page.Markets.FirstOrDefault(m => m.Classification == "binary");
            if (candidate is null) throw new MarketDiscoveryException("NoSupportedBinaryMarketInSample", "No suitable binary market in bounded sample.");
            market = candidate.NativeId;
            book = await new KalshiOrderBookSource(bookHttp, maxAttempts: 1).ReadAsync(new(new("Kalshi", market, "yes", "Yes"), true), default);
            parsed = true; normalized = book.Validity == BookValidity.Valid;
            Assert.True(normalized, "Sampled orderbook failed normalization.");
            if (book.Asks.Length > 0 || book.Bids.Length > 0)
            {
                var eligibility = BookEligibility.Evaluate(book, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
                var depth = ExecutableDepth.Calculate(book, eligibility, book.Asks.Length > 0 ? DepthAction.Buy : DepthAction.Sell, 1m, true);
                Assert.False(depth.IsActionable); Assert.True(depth.ExecutableQuantity > 0);
            }
            classification = book.Bids.Length + book.Asks.Length == 0 ? "SampledEmptyOrderBookVerified" : "SampledOrderBookVerified";
        }
        catch (Exception e) { classification = e is MarketDiscoveryException failure ? failure.Code : e.GetType().Name; throw; }
        finally
        {
            output.WriteLine(JsonSerializer.Serialize(new { Exchange = "Kalshi", Market = market, InstrumentId = market is null ? null : "yes",
                DiscoveryFilter = "open; mve_filter=exclude; limit=5", DiscoveryRequests = discoveryProbe.Requests, OrderBookRequests = bookProbe.Requests, HttpResponseReceived = bookProbe.ResponseReceived,
                HttpStatus = bookProbe.LastHttpStatus, ParsingSucceeded = parsed, NormalizationSucceeded = normalized,
                BidLevelCount = book?.Bids.Length, AskLevelCount = book?.Asks.Length,
                DerivedLevelCount = book?.Asks.Count(l => l.Origin == LiquidityOrigin.DerivedComplement),
                BestBid = book?.Bids.FirstOrDefault()?.Price, BestAsk = book?.Asks.FirstOrDefault()?.Price,
                RetrievedUtc = book?.RetrievedAtUtc, Classification = classification }));
        }
    }
    private sealed class ProbeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Requests { get; private set; }
        public bool ResponseReceived { get; private set; }
        public int? LastHttpStatus { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = await base.SendAsync(request, ct);
            ResponseReceived = true;
            LastHttpStatus = (int)response.StatusCode;
            return response;
        }
    }

    [Fact]
    public async Task Sample_public_adapters()
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_LIVE_MARKET_SAMPLE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-sample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var allSucceeded = true;
        try
        {
            foreach (var exchange in new[] { "Kalshi", "Polymarket" })
            {
                using var probe = new ProbeHandler(PublicMarketTransport.CreateHandler());
                using var http = new HttpClient(probe) { Timeout = TimeSpan.FromSeconds(15) };
                IMarketDiscoverySource source = exchange == "Kalshi"
                    ? new KalshiMarketSource(http, maxAttempts: 1)
                    : new PolymarketMarketSource(http, maxAttempts: 1);
                var scope = exchange == "Kalshi" ? "open" : "nonfinalized";
                var at = DateTimeOffset.UtcNow;
                var parsedPages = 0; var parsedRecords = 0; var stored = 0;
                var parsingSucceeded = false; var persistenceVerified = false;
                var classification = "Unverified"; string? transportError = null;
                string? socketError = null; int? nativeError = null;
                try
                {
                    await using var db = new TradingDbContext(DatabaseOptions.ForFile(Path.Combine(root, exchange + ".db")));
                    await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
                    var profile = await db.LocalProfiles.SingleAsync();
                    var run = new DiscoveryRunEntry { Id = Guid.NewGuid(), Exchange = exchange,
                        Scope = "Sampled two-page adapter check", OwnerUserId = profile.UserId,
                        WorkspaceId = profile.DefaultWorkspaceId, StartedAt = at };
                    var store = new MarketCatalogStore(db);
                    await store.CreateRunAsync(run, default);
                    string? cursor = null;
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    for (var pageNumber = 0; pageNumber < 2; pageNumber++)
                    {
                        // One attempt per page; never follow more than one continuation.
                        var page = await source.ReadPageAsync(scope, cursor, 5, at.AddSeconds(15), default);
                        parsingSucceeded = true; parsedPages++; parsedRecords += page.Markets.Length;
                        foreach (var item in page.Markets) ids.Add(item.NativeId);
                        await store.UpsertPageAsync(run.Id, scope, page.Markets, page.MalformedRecords, default);
                        stored = await store.CountAsync(exchange, default);
                        persistenceVerified = stored == ids.Count;
                        cursor = page.NextCursor;
                        if (cursor is null) break;
                    }
                    await store.FinishRunAsync(run.Id, "Partial", "Sampled", null, default);
                    if (!persistenceVerified) throw new InvalidOperationException("Sample persistence count mismatch.");
                    classification = parsedRecords == 0 ? "SampledEmptyParsed" : "SampledRecordsVerified";
                }
                catch (Exception error)
                {
                    allSucceeded = false;
                    classification = error is MarketDiscoveryException discovery ? discovery.Code : error.GetType().Name;
                    for (var cause = error; cause is not null; cause = cause.InnerException)
                    {
                        if (cause is HttpRequestException transport) transportError = transport.HttpRequestError.ToString();
                        if (cause is SocketException socket)
                        {
                            socketError = socket.SocketErrorCode.ToString(); nativeError = socket.NativeErrorCode;
                        }
                    }
                }
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    AtUtc = at, Exchange = exchange,
                    Host = exchange == "Kalshi" ? "external-api.kalshi.com" : "gamma-api.polymarket.com",
                    RequestBudget = 2, Requests = probe.Requests, EffectivePageSize = 5,
                    HttpResponseReceived = probe.ResponseReceived, HttpStatus = probe.LastHttpStatus,
                    ParsingSucceeded = parsingSucceeded, PersistenceVerified = persistenceVerified,
                    ParsedPages = parsedPages, ParsedRecords = parsedRecords, Stored = stored,
                    Classification = classification, TransportError = transportError,
                    SocketErrorCode = socketError, NativeErrorCode = nativeError,
                    Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription
                }));
            }
        }
        finally { Directory.Delete(root, true); }
        Assert.True(allSucceeded, "One or more sampled public adapter checks failed; see per-exchange output.");
    }
}
