using Arbitrage.Domain;

namespace Arbitrage.Application;

public sealed record OrderBookRequest(OrderBookInstrumentId Instrument, bool BinarySupported);
public interface IOrderBookSource
{
    string Exchange { get; }
    Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken cancellationToken);
}
public sealed record OrderBookFailure(string Code, DateTimeOffset AtUtc, DateTimeOffset? RetryAt = null);
public sealed record CachedOrderBook(OrderBookSnapshot? Snapshot, OrderBookFailure? Failure, BookEligibility Eligibility);

// Admitted by the catalog service only; no user-facing arbitrary cache insertion endpoint.
public sealed class OrderBookCache(TimeProvider clock, int capacity = 128, int freshnessSeconds = 5)
{
    private readonly object gate = new();
    private readonly Dictionary<OrderBookInstrumentId, (OrderBookSnapshot? Book, OrderBookFailure? Failure, long Sequence)> entries = [];
    private long sequence;
    public TimeSpan FreshnessThreshold { get; } = freshnessSeconds is >= 1 and <= 60
        ? TimeSpan.FromSeconds(freshnessSeconds) : throw new ArgumentOutOfRangeException(nameof(freshnessSeconds));
    private readonly int limit = capacity is >= 1 and <= 1024 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count { get { lock (gate) return entries.Count; } }
    public CachedOrderBook Read(OrderBookInstrumentId id)
    {
        lock (gate)
        {
            entries.TryGetValue(id, out var entry);
            return new(entry.Book, entry.Failure, BookEligibility.Evaluate(entry.Book, clock.GetUtcNow(), FreshnessThreshold, entry.Failure?.Code));
        }
    }
    public void Store(OrderBookSnapshot book)
    {
        lock (gate)
        {
            entries.TryGetValue(book.Instrument, out var old);
            if (old.Book is not null && old.Book.RetrievedAtUtc > book.RetrievedAtUtc) return;
            if (book.Validity == BookValidity.Invalid)
            { Fail(book.Instrument, "InvalidOrderBook"); return; }
            MakeRoom(book.Instrument);
            entries[book.Instrument] = (book, null, ++sequence);
        }
    }
    public void Fail(OrderBookInstrumentId id, string code, DateTimeOffset? retryAt = null)
    {
        lock (gate)
        {
            entries.TryGetValue(id, out var old); MakeRoom(id);
            entries[id] = (old.Book, new(code, clock.GetUtcNow(), retryAt), ++sequence);
        }
    }
    private void MakeRoom(OrderBookInstrumentId id)
    {
        if (!entries.ContainsKey(id) && entries.Count >= limit)
            entries.Remove(entries.MinBy(p => p.Value.Sequence).Key);
    }
}
