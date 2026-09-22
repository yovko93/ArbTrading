using System.Collections.Immutable;

namespace Arbitrage.Domain;

public enum LiquidityOrigin { NativeBid, NativeAsk, DerivedComplement }
public enum BookValidity { Valid, Invalid, Unsupported }
public enum BookFreshness { Unavailable, Fresh, Stale }
public enum DepthAction { Buy, Sell }
public sealed record OrderBookInstrumentId(string Exchange, string NativeMarketId, string NativeInstrumentId, string Outcome);
public sealed record LiquiditySourceId(string Exchange, string MarketId, string InstrumentId, DepthAction NativeSide, decimal NativePrice);
public sealed record OrderBookLevel(decimal Price, decimal Quantity, LiquidityOrigin Origin);

// Construct only through the normalizer. Immutable levels prevent cache readers from corrupting shared state.
public sealed class OrderBookSnapshot
{
    internal OrderBookSnapshot(OrderBookInstrumentId instrument, ImmutableArray<OrderBookLevel> bids,
        ImmutableArray<OrderBookLevel> asks, ImmutableArray<OrderBookLevel> oppositeBids,
        DateTimeOffset retrieved, DateTimeOffset? source, string? nativeMarket, string? hash,
        BookValidity validity, ImmutableArray<string> warnings)
    {
        Id = Guid.NewGuid(); Instrument = instrument; Bids = bids; Asks = asks; OppositeBids = oppositeBids;
        RetrievedAtUtc = retrieved; SourceTimestamp = source; SourceMarketId = nativeMarket; NativeHash = hash;
        Validity = validity; Warnings = warnings;
    }
    public Guid Id { get; }
    public OrderBookInstrumentId Instrument { get; }
    public ImmutableArray<OrderBookLevel> Bids { get; }
    public ImmutableArray<OrderBookLevel> Asks { get; }
    public ImmutableArray<OrderBookLevel> OppositeBids { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public DateTimeOffset? SourceTimestamp { get; }
    public string? SourceMarketId { get; }
    public string? NativeHash { get; }
    public BookValidity Validity { get; }
    public string Completeness => "FullReturnedDepth";
    public ImmutableArray<string> Warnings { get; }
    public LiquiditySourceId? LiquiditySource(OrderBookLevel level) => OrderBookNormalizer.LiquiditySource(Instrument, level);
}

public static class OrderBookNormalizer
{
    public static OrderBookSnapshot NormalizeBinary(OrderBookInstrumentId instrument,
        IEnumerable<OrderBookLevel> yes, IEnumerable<OrderBookLevel> no, DateTimeOffset retrieved,
        bool supported = true, DateTimeOffset? source = null)
    {
        var own = (instrument.NativeInstrumentId == "yes" ? yes : no).ToArray();
        var opposite = (instrument.NativeInstrumentId == "yes" ? no : yes).ToArray();
        var native = Normalize(instrument, own, [], retrieved, oppositeBids: opposite);
        if (native.Validity == BookValidity.Invalid) return native;
        var asks = supported ? opposite.Select(l => new OrderBookLevel(1m - l.Price, l.Quantity, LiquidityOrigin.DerivedComplement)) : [];
        return Normalize(instrument, own, asks, retrieved, source, instrument.NativeMarketId,
            oppositeBids: opposite, supported: supported);
    }
    public const int MaximumLevelsPerSide = 20_000;
    public static OrderBookSnapshot Normalize(OrderBookInstrumentId instrument, IEnumerable<OrderBookLevel> bids,
        IEnumerable<OrderBookLevel> asks, DateTimeOffset retrieved, DateTimeOffset? source = null,
        string? sourceMarket = null, string? hash = null, IEnumerable<OrderBookLevel>? oppositeBids = null,
        bool supported = true)
    {
        var warnings = ImmutableArray.CreateBuilder<string>();
        var b = Side(bids, true, warnings); var a = Side(asks, false, warnings);
        var opposite = Side(oppositeBids ?? [], true, warnings);
        if (b.Length > 0 && a.Length > 0 && b[0].Price >= a[0].Price)
            warnings.Add(b[0].Price > a[0].Price ? "CrossedBook" : "LockedBookNotActionable");
        var validity = warnings.Count > 0 ? BookValidity.Invalid : supported ? BookValidity.Valid : BookValidity.Unsupported;
        if (!supported) warnings.Add("UnsupportedMarketStructure");
        return new(instrument, b, a, opposite, retrieved.ToUniversalTime(), source?.ToUniversalTime(),
            sourceMarket, hash, validity, warnings.ToImmutable());
    }

    public static LiquiditySourceId? LiquiditySource(OrderBookInstrumentId instrument, OrderBookLevel l) => l.Origin switch
        {
            LiquidityOrigin.NativeBid => new(instrument.Exchange, instrument.NativeMarketId, instrument.NativeInstrumentId, DepthAction.Sell, l.Price),
            LiquidityOrigin.NativeAsk => new(instrument.Exchange, instrument.NativeMarketId, instrument.NativeInstrumentId, DepthAction.Buy, l.Price),
            LiquidityOrigin.DerivedComplement when instrument.Exchange == "Kalshi" && instrument.NativeInstrumentId is "yes" or "no" =>
                new(instrument.Exchange, instrument.NativeMarketId, instrument.NativeInstrumentId == "yes" ? "no" : "yes", DepthAction.Sell, 1m - l.Price),
            _ => null
        };

    private static ImmutableArray<OrderBookLevel> Side(IEnumerable<OrderBookLevel> source, bool bid,
        ImmutableArray<string>.Builder warnings)
    {
        var levels = source.Take(MaximumLevelsPerSide + 1).ToArray();
        if (levels.Length > MaximumLevelsPerSide) { warnings.Add("TooManyLevels"); return []; }
        if (levels.Any(l => l.Price < 0m || l.Price > 1m || l.Quantity < 0m ||
            (bid ? l.Origin != LiquidityOrigin.NativeBid : l.Origin is not (LiquidityOrigin.NativeAsk or LiquidityOrigin.DerivedComplement))))
        { warnings.Add("InvalidLevel"); return []; }
        // L2 already aggregates a price. Duplicates can be repeated totals; summing could double liquidity.
        if (levels.GroupBy(l => l.Price).Any(g => g.Count() > 1)) warnings.Add("DuplicatePriceLevel");
        return (bid ? levels.Where(l => l.Quantity > 0).OrderByDescending(l => l.Price)
            : levels.Where(l => l.Quantity > 0).OrderBy(l => l.Price)).ToImmutableArray();
    }
}

public sealed record BookEligibility(BookFreshness Freshness, bool IsActionable, string? Reason, TimeSpan? Age)
{
    public static BookEligibility Evaluate(OrderBookSnapshot? book, DateTimeOffset now, TimeSpan threshold, string? failure = null)
    {
        if (book is null) return new(BookFreshness.Unavailable, false, failure ?? "Unavailable", null);
        var age = now - book.RetrievedAtUtc;
        var fresh = age >= TimeSpan.Zero && age <= threshold;
        var reason = failure ?? (book.Validity != BookValidity.Valid ? book.Validity.ToString() : !fresh ? "Stale" : null);
        return new(fresh ? BookFreshness.Fresh : BookFreshness.Stale, reason is null, reason, age);
    }
}

public sealed record GrossDepthEstimate(decimal RequestedQuantity, decimal ExecutableQuantity, bool IsFullyExecutable,
    decimal GrossNotional, decimal? Vwap, decimal? BestPrice, decimal? WorstPrice, int LevelsConsumed,
    decimal UnfilledQuantity, Guid? SnapshotId, DateTimeOffset? SnapshotRetrievedAt, BookFreshness SnapshotFreshness,
    bool IsActionable, string? Reason);

public static class ExecutableDepth
{
    public static GrossDepthEstimate Calculate(OrderBookSnapshot? book, BookEligibility eligibility,
        DepthAction action, decimal quantity, bool diagnosticOnly = false)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        decimal executed = 0, gross = 0; decimal? best = null, worst = null; var count = 0;
        // Invalid/unsupported data is never calculable, including diagnostic mode.
        if (book?.Validity == BookValidity.Valid && (eligibility.IsActionable || diagnosticOnly))
            foreach (var level in action == DepthAction.Buy ? book.Asks : book.Bids)
            {
                var taken = Math.Min(quantity - executed, level.Quantity);
                executed += taken; gross += taken * level.Price; best ??= level.Price; worst = level.Price; count++;
                if (executed == quantity) break;
            }
        var actionable = eligibility.IsActionable && !diagnosticOnly && executed > 0;
        return new(quantity, executed, executed == quantity, gross, executed == 0 ? null : gross / executed,
            best, worst, count, quantity - executed, book?.Id, book?.RetrievedAtUtc, eligibility.Freshness,
            actionable, diagnosticOnly ? "DiagnosticOnly" : eligibility.Reason ?? (executed == 0 ? "NoLiquidity" : executed < quantity ? "PartialDepth" : null));
    }
}
