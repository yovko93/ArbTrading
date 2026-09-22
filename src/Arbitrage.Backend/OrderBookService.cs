using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

// A bounded admission gate also prevents old concurrent refreshes from overwriting newer failures.
public sealed class OrderBookRefreshGate { public SemaphoreSlim Semaphore { get; } = new(1, 1); }

public sealed class OrderBookService(MarketCatalogStore catalog, IEnumerable<IOrderBookSource> sources,
    OrderBookCache cache, OrderBookRefreshGate refreshGate)
{
    public async Task<OrderBookResponse?> ReadAsync(string exchange, string marketId, string? instrumentId,
        bool refresh, CancellationToken ct)
    {
        var resolved = await ResolveAsync(exchange, marketId, instrumentId, ct);
        if (resolved is null) return null;
        var (instruments, request) = resolved.Value;
        if (request is null) return new(instruments.Select(Map).ToArray(), null, "NotAddressable", "Unavailable", false,
            "MissingInstrumentIdentifier", null, (int)cache.FreshnessThreshold.TotalSeconds, null, null);
        if (refresh)
        {
            if (!await refreshGate.Semaphore.WaitAsync(0, ct)) throw new InvalidOperationException("RefreshBusy");
            try
            {
                var source = sources.Single(s => s.Exchange == exchange);
                var book = await source.ReadAsync(request, ct);
                ct.ThrowIfCancellationRequested();
                cache.Store(book);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { cache.Fail(request.Instrument, "RefreshCancelled"); throw; }
            catch (MarketDiscoveryException failure) { cache.Fail(request.Instrument, failure.Code, failure.RetryAt); }
            finally { refreshGate.Semaphore.Release(); }
        }
        var entry = cache.Read(request.Instrument);
        var state = entry.Failure?.Code switch
        {
            "SecureConnectionFailure" or "PublicAccessDenied" => "NetworkRestricted",
            "InvalidJson" or "InvalidEnvelope" or "InvalidOrderBook" => "Invalid",
            not null => "Unavailable",
            _ => entry.Snapshot?.Validity is BookValidity.Unsupported ? "Unsupported" : entry.Eligibility.Freshness.ToString()
        };
        if (entry.Realtime is { } realtime) state = realtime.State.ToString();
        // Re-resolved catalog structure remains authoritative even if an older binary view is cached.
        var supported = exchange != "Kalshi" || request.BinarySupported;
        var snapshot = entry.Snapshot is { } cached ? Map(cached) : null;
        if (!supported && snapshot is not null)
            snapshot = snapshot with { Asks = [], Validity = "Unsupported", Warnings = [.. snapshot.Warnings, "UnsupportedMarketStructure"] };
        return new(instruments.Select(Map).ToArray(), request.Instrument.NativeInstrumentId, supported ? state : "Unsupported",
            entry.Eligibility.Freshness.ToString(), supported && entry.Eligibility.IsActionable,
            supported ? entry.Eligibility.Reason : "UnsupportedMarketStructure",
            entry.Eligibility.Age is { } age ? (decimal)age.Ticks / TimeSpan.TicksPerSecond : null,
            (int)(entry.Source == BookSourceMode.Realtime ? cache.RealtimeFreshnessThreshold : cache.FreshnessThreshold).TotalSeconds, snapshot,
            entry.Failure is { } f ? new(f.Code, f.AtUtc, f.RetryAt) : null,
            entry.Realtime is { } m ? new(entry.Source.ToString(), m.State.ToString(), m.Continuity.ToString(), m.Connected, m.Generation, entry.Version, m.SubscriptionId, m.SessionId, m.Sequence, m.AnchorAt, m.LastReceivedAt, m.LastChangedAt, m.LastControlAt, m.ResyncReason, m.TickSize, m.DeltaCount) : null);
    }
    public async Task<GrossDepthResponse?> PreviewAsync(string exchange, string marketId, DepthPreviewRequest input, CancellationToken ct)
    {
        if (input.Action is not ("Buy" or "Sell") || input.Quantity <= 0) throw new ArgumentException("InvalidDepthRequest");
        var resolved = await ResolveAsync(exchange, marketId, input.InstrumentId, ct);
        if (resolved is null) return null;
        var request = resolved.Value.Request;
        var entry = request is null ? new CachedOrderBook(null, null, new(BookFreshness.Unavailable, false, "NotAddressable", null)) : cache.Read(request.Instrument);
        if (request is { BinarySupported: false } && exchange == "Kalshi")
            entry = entry with { Snapshot = null, Eligibility = entry.Eligibility with { IsActionable = false, Reason = "UnsupportedMarketStructure" } };
        var result = ExecutableDepth.Calculate(entry.Snapshot, entry.Eligibility, Enum.Parse<DepthAction>(input.Action), input.Quantity, input.DiagnosticOnly && entry.Source == BookSourceMode.RestSnapshot);
        return new(result.RequestedQuantity, result.ExecutableQuantity, result.IsFullyExecutable, result.GrossNotional,
            result.Vwap, result.BestPrice, result.WorstPrice, result.LevelsConsumed, result.UnfilledQuantity,
            result.SnapshotId, result.SnapshotRetrievedAt, result.SnapshotFreshness.ToString(), result.IsActionable, result.Reason);
    }
    public async Task<(OrderBookInstrumentId[] Instruments, OrderBookRequest? Request)?> ResolveAsync(
        string exchange, string marketId, string? instrumentId, CancellationToken ct)
    {
        if (exchange is not ("Polymarket" or "Kalshi") || marketId.Length is < 1 or > 256 || marketId.Any(char.IsControl) || instrumentId?.Length > 256)
            throw new ArgumentException("InvalidInstrument");
        var market = await catalog.FindAsync(exchange, marketId, ct);
        if (market is null) return null;
        var outcomes = JsonSerializer.Deserialize<MarketOutcome[]>(market.OutcomesJson) ?? [];
        OrderBookInstrumentId[] instruments = exchange == "Kalshi"
            ? [new(exchange, marketId, "yes", "Yes"), new(exchange, marketId, "no", "No")]
            : outcomes.Where(o => o.NativeTokenId is { Length: > 0 and <= 256 } token && token.All(char.IsAsciiDigit))
                .Select(o => new OrderBookInstrumentId(exchange, marketId, o.NativeTokenId!, o.Label)).ToArray();
        if (instruments.GroupBy(i => i.NativeInstrumentId).Any(g => g.Count() > 1)) instruments = [];
        var selected = instrumentId is null ? instruments.FirstOrDefault() : instruments.SingleOrDefault(i => i.NativeInstrumentId == instrumentId);
        if (instrumentId is not null && selected is null) throw new ArgumentException("UnknownInstrument");
        var binary = market.Classification == "binary" && outcomes.Length == 2 &&
            outcomes.Any(o => o.Label.Equals("Yes", StringComparison.OrdinalIgnoreCase)) && outcomes.Any(o => o.Label.Equals("No", StringComparison.OrdinalIgnoreCase));
        return (instruments, selected is null ? null : new(selected, binary));
    }
    private static BookInstrumentResponse Map(OrderBookInstrumentId i) => new(i.Exchange, i.NativeMarketId, i.NativeInstrumentId, i.Outcome);
    private static BookLevelResponse Map(OrderBookLevel l) => new(l.Price, l.Quantity, l.Origin.ToString());
    private static BookSnapshotResponse Map(OrderBookSnapshot b) => new(b.Id, Map(b.Instrument), b.Bids.Select(Map).ToArray(),
        b.Asks.Select(Map).ToArray(), b.OppositeBids.Select(Map).ToArray(), b.RetrievedAtUtc, b.SourceTimestamp,
        b.SourceMarketId, b.NativeHash, b.Validity.ToString(), b.Completeness, b.Warnings.ToArray());
}
