using Arbitrage.Domain;

namespace Arbitrage.Application;

public sealed record OrderBookRequest(OrderBookInstrumentId Instrument, bool BinarySupported);
public interface IOrderBookSource
{
    string Exchange { get; }
    Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken cancellationToken);
}
public sealed record OrderBookFailure(string Code, DateTimeOffset AtUtc, DateTimeOffset? RetryAt = null);
public sealed record CachedOrderBook(OrderBookSnapshot? Snapshot, OrderBookFailure? Failure, BookEligibility Eligibility,
    BookSourceMode Source = BookSourceMode.RestSnapshot, RealtimeBookMetadata? Realtime = null, long Version = 0);

// One bounded cache, with a diagnostic REST lane while realtime owns the current view.
public sealed class OrderBookCache(TimeProvider clock, int capacity = 128, int freshnessSeconds = 5, int realtimeFreshnessSeconds = 10)
{
    private sealed record Entry(OrderBookSnapshot? Rest = null, OrderBookFailure? Failure = null,
        OrderBookSnapshot? Live = null, RealtimeBookMetadata? Metadata = null, long Version = 0, long LiveGeneration = 0);
    private readonly object gate = new();
    private readonly Dictionary<OrderBookInstrumentId, Entry> entries = [];
    private long sequence;
    public TimeSpan FreshnessThreshold { get; } = Validate(freshnessSeconds);
    public TimeSpan RealtimeFreshnessThreshold { get; } = Validate(realtimeFreshnessSeconds);
    private static TimeSpan Validate(int seconds) => seconds is >= 1 and <= 60 ? TimeSpan.FromSeconds(seconds) : throw new ArgumentOutOfRangeException(nameof(seconds));
    private readonly int limit = capacity is >= 1 and <= 1024 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count { get { lock (gate) return entries.Count; } }
    public CachedOrderBook Read(OrderBookInstrumentId id)
    {
        lock (gate)
        {
            var entry = entries.GetValueOrDefault(id) ?? new();
            if (entry.Metadata is not { } m)
                return new(entry.Rest, entry.Failure, BookEligibility.Evaluate(entry.Rest, clock.GetUtcNow(), FreshnessThreshold, entry.Failure?.Code), Version: entry.Version);
            var book = entry.Live ?? entry.Rest;
            var reason = m.State != RealtimeSubscriptionState.Streaming ? m.ResyncReason ?? m.State.ToString()
                : !m.Connected ? "Disconnected"
                : m.AnchorAt is null || entry.Live is null || entry.LiveGeneration != m.Generation ? "AwaitingAnchor"
                : m.Continuity != (id.Exchange == "Kalshi" ? BookContinuity.Continuous : BookContinuity.BestEffort) ? m.Continuity.ToString() : null;
            var eligibility = BookEligibility.Evaluate(book, clock.GetUtcNow(), RealtimeFreshnessThreshold, reason);
            if (eligibility.Freshness == BookFreshness.Stale && m.State == RealtimeSubscriptionState.Streaming)
                m = m with { State = RealtimeSubscriptionState.Stale };
            return new(book, entry.Failure, eligibility, BookSourceMode.Realtime, m, entry.Version);
        }
    }
    public void Store(OrderBookSnapshot book)
    {
        lock (gate)
        {
            var old = entries.GetValueOrDefault(book.Instrument) ?? new();
            if (old.Rest is not null && old.Rest.RetrievedAtUtc > book.RetrievedAtUtc) return;
            if (book.Validity == BookValidity.Invalid) { Fail(book.Instrument, "InvalidOrderBook"); return; }
            MakeRoom(book.Instrument);
            // Explicit REST refresh after Stop selects REST again; an active realtime lane is never overwritten.
            var stopped = old.Metadata?.State == RealtimeSubscriptionState.Stopped;
            entries[book.Instrument] = old with { Rest = book, Failure = null, Metadata = stopped ? null : old.Metadata, Version = ++sequence };
        }
    }
    public void Fail(OrderBookInstrumentId id, string code, DateTimeOffset? retryAt = null)
    {
        lock (gate)
        {
            var old = entries.GetValueOrDefault(id) ?? new(); MakeRoom(id);
            entries[id] = old with { Failure = new(code, clock.GetUtcNow(), retryAt), Version = ++sequence };
        }
    }
    public bool PublishRealtime(OrderBookInstrumentId id, RealtimeBookMetadata metadata, OrderBookSnapshot? book = null)
    {
        lock (gate)
        {
            var old = entries.GetValueOrDefault(id) ?? new();
            if (old.Metadata is { } previous && metadata.Generation < previous.Generation) return false;
            if (book is not null && (book.Instrument != id || book.Validity != BookValidity.Valid)) return false;
            MakeRoom(id);
            entries[id] = old with { Live = book ?? old.Live, Metadata = metadata, LiveGeneration = book is null ? old.LiveGeneration : metadata.Generation, Version = ++sequence };
            return true;
        }
    }
    private void MakeRoom(OrderBookInstrumentId id)
    {
        if (entries.ContainsKey(id) || entries.Count < limit) return;
        var candidate = entries.Where(p => p.Value.Metadata is null || p.Value.Metadata.State is RealtimeSubscriptionState.Stopped
            or RealtimeSubscriptionState.Faulted or RealtimeSubscriptionState.Unsupported or RealtimeSubscriptionState.AuthenticationRequired or RealtimeSubscriptionState.AuthenticationFailed)
            .MinBy(p => p.Value.Version);
        if (candidate.Key is null) throw new InvalidOperationException("RealtimeCapacity");
        entries.Remove(candidate.Key);
    }
}
