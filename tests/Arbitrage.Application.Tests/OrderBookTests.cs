using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class OrderBookTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-22T00:00:00Z");
    private static readonly OrderBookInstrumentId Id = new("Kalshi", "market", "yes", "Yes");
    private static OrderBookSnapshot Book(OrderBookLevel[]? bids = null, OrderBookLevel[]? asks = null,
        DateTimeOffset? at = null, bool supported = true) => OrderBookNormalizer.Normalize(Id, bids ?? [], asks ?? [], at ?? At, supported: supported);
    private static BookEligibility Eligible(OrderBookSnapshot b) => BookEligibility.Evaluate(b, At, TimeSpan.FromSeconds(5));
    [Fact]
    public void Buy_walks_full_available_depth_in_price_order_with_exact_gross_vwap()
    {
        var book = Book(asks: [new(.45m, 50, LiquidityOrigin.NativeAsk), new(.40m, 10, LiquidityOrigin.NativeAsk), new(.42m, 20, LiquidityOrigin.NativeAsk)]);
        var result = ExecutableDepth.Calculate(book, Eligible(book), DepthAction.Buy, 25);
        Assert.Equal(25, result.ExecutableQuantity); Assert.Equal(10.30m, result.GrossNotional);
        Assert.Equal(.412m, result.Vwap); Assert.Equal(.40m, result.BestPrice); Assert.Equal(.42m, result.WorstPrice);
        Assert.Equal(2, result.LevelsConsumed); Assert.Equal(0, result.UnfilledQuantity);
        Assert.True(result.IsFullyExecutable); Assert.True(result.IsActionable); Assert.Equal(book.Id, result.SnapshotId);
    }
    [Fact]
    public void Sell_partial_depth_has_no_imaginary_liquidity()
    {
        var book = Book([new(.60m, 10, LiquidityOrigin.NativeBid), new(.61m, 8, LiquidityOrigin.NativeBid)]);
        var result = ExecutableDepth.Calculate(book, Eligible(book), DepthAction.Sell, 25);
        Assert.Equal(18, result.ExecutableQuantity); Assert.Equal(7, result.UnfilledQuantity);
        Assert.Equal(10.88m, result.GrossNotional); Assert.Equal(10.88m / 18, result.Vwap);
        Assert.Equal(.60m, result.WorstPrice); Assert.Equal(2, result.LevelsConsumed);
        Assert.False(result.IsFullyExecutable); Assert.Equal("PartialDepth", result.Reason);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void Quantity_must_be_positive(int quantity) => Assert.Throws<ArgumentOutOfRangeException>(() => ExecutableDepth.Calculate(Book(), Eligible(Book()), DepthAction.Buy, quantity));
    [Fact]
    public void Empty_book_and_zero_sizes_are_not_price_zero()
    {
        var book = Book(asks: [new(.42m, 0, LiquidityOrigin.NativeAsk)]);
        Assert.Equal(BookValidity.Valid, book.Validity); Assert.Empty(book.Asks);
        var result = ExecutableDepth.Calculate(book, Eligible(book), DepthAction.Buy, 1);
        Assert.Null(result.Vwap); Assert.Null(result.BestPrice); Assert.Null(result.WorstPrice);
        Assert.Equal("NoLiquidity", result.Reason); Assert.False(result.IsActionable);
    }
    [Theory]
    [InlineData("crossed")] [InlineData("locked")] [InlineData("duplicate")] [InlineData("negative")]
    [InlineData("range")] [InlineData("origin")]
    public void Invalid_books_never_produce_depth_even_in_diagnostic_mode(string kind)
    {
        var bids = new[] { new OrderBookLevel(kind == "crossed" ? .7m : .6m, 2, LiquidityOrigin.NativeBid) };
        var asks = kind switch
        {
            "duplicate" => new[] { new OrderBookLevel(.8m, 1, LiquidityOrigin.NativeAsk), new(.8m, 2, LiquidityOrigin.NativeAsk) },
            "negative" => [new(.8m, -1, LiquidityOrigin.NativeAsk)],
            "range" => [new(1.01m, 1, LiquidityOrigin.NativeAsk)],
            "origin" => [new(.8m, 1, LiquidityOrigin.NativeBid)],
            _ => new[] { new OrderBookLevel(.6m, 1, LiquidityOrigin.NativeAsk) }
        };
        var book = Book(bids, asks); Assert.Equal(BookValidity.Invalid, book.Validity);
        var result = ExecutableDepth.Calculate(book, Eligible(book), DepthAction.Buy, 1, true);
        Assert.Equal(0, result.ExecutableQuantity); Assert.False(result.IsActionable);
    }
    [Fact]
    public void Stale_requires_explicit_diagnostic_and_unsupported_remains_unusable()
    {
        var book = Book(asks: [new(.42m, 5, LiquidityOrigin.DerivedComplement)]);
        var eligibility = BookEligibility.Evaluate(book, At.AddSeconds(6), TimeSpan.FromSeconds(5));
        Assert.Equal(BookFreshness.Stale, eligibility.Freshness);
        Assert.Equal(0, ExecutableDepth.Calculate(book, eligibility, DepthAction.Buy, 2).ExecutableQuantity);
        var diagnostic = ExecutableDepth.Calculate(book, eligibility, DepthAction.Buy, 2, true);
        Assert.Equal(.84m, diagnostic.GrossNotional); Assert.False(diagnostic.IsActionable);
        var unsupported = Book([new(.2m, 5, LiquidityOrigin.NativeBid)], supported: false);
        Assert.Equal(0, ExecutableDepth.Calculate(unsupported, Eligible(unsupported), DepthAction.Sell, 1, true).ExecutableQuantity);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public void Cache_preserves_valid_book_on_failure_and_invalid_refresh_then_replaces_atomically()
    {
        var clock = new Clock(); var cache = new OrderBookCache(clock, 2); var original = Book(); cache.Store(original);
        cache.Fail(Id, "SecureConnectionFailure"); var failed = cache.Read(Id);
        Assert.Same(original, failed.Snapshot); Assert.Equal("SecureConnectionFailure", failed.Failure!.Code); Assert.False(failed.Eligibility.IsActionable);
        cache.Store(Book(asks: [new(2, 1, LiquidityOrigin.NativeAsk)])); Assert.Same(original, cache.Read(Id).Snapshot);
        clock.Now = At.AddSeconds(10); Assert.Equal(BookFreshness.Stale, cache.Read(Id).Eligibility.Freshness);
        var replacement = Book(at: clock.Now); cache.Store(replacement);
        Assert.Same(replacement, cache.Read(Id).Snapshot); Assert.Null(cache.Read(Id).Failure); Assert.True(cache.Read(Id).Eligibility.IsActionable);
        cache.Store(original); Assert.Same(replacement, cache.Read(Id).Snapshot);
        Assert.Null(new OrderBookCache(clock).Read(Id).Snapshot);
    }
    [Fact]
    public async Task Concurrent_cache_writes_keep_newest_and_bound_exchange_scoped_keys()
    {
        var cache = new OrderBookCache(new Clock(), 2);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(n => Task.Run(() => cache.Store(Book(at: At.AddTicks(n))))));
        Assert.Equal(At.AddTicks(49), cache.Read(Id).Snapshot!.RetrievedAtUtc);
        foreach (var exchange in new[] { "Polymarket", "Other" })
            cache.Store(OrderBookNormalizer.Normalize(Id with { Exchange = exchange }, [], [], At));
        Assert.Equal(2, cache.Count); Assert.Null(cache.Read(Id).Snapshot);
    }
}
