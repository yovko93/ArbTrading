using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;
public sealed class RelationshipCandidateTests
{
    private static CanonicalMarketDescriptor[] Catalog(int size) => Enumerable.Range(0, size).Select(i => new CanonicalMarketDescriptor
    { Identity = new(i % 2 == 0 ? "Kalshi" : "Polymarket", i.ToString("D6")), Title = $"Entity{i / 2} event 2028" }).ToArray();
    [Fact]
    public void Large_fixture_is_bounded_deterministic_cross_exchange_without_reverse_duplicates()
    {
        var catalog = Catalog(10000); var generator = new CandidateGenerator(TimeProvider.System); var bounds = new CandidateBounds(1000, 5, 100, 15);
        var a = generator.Generate(catalog, bounds, true, null, default); var b = generator.Generate(catalog.Reverse().ToArray(), bounds, true, null, default);
        Assert.True(a.Partial); Assert.InRange(a.Comparisons, 1, 100); Assert.Equal(a.Pairs, b.Pairs);
        Assert.All(a.Pairs, p => { Assert.NotEqual(p.A.Identity.Exchange, p.B.Identity.Exchange); Assert.True(p.A.Identity.CompareTo(p.B.Identity) < 0); });
        Assert.Equal(a.Pairs.Length, a.Pairs.Select(p => (p.A.Identity, p.B.Identity)).Distinct().Count());
    }
    [Fact]
    public void Selected_market_scope_only_returns_that_market_and_honors_cancellation()
    {
        var catalog = Catalog(100); var selected = catalog[8].Identity; var generator = new CandidateGenerator(TimeProvider.System);
        var batch = generator.Generate(catalog, new(100, 3), true, selected, default);
        Assert.InRange(batch.Pairs.Length, 1, 3); Assert.All(batch.Pairs, p => Assert.True(p.A.Identity == selected || p.B.Identity == selected));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => generator.Generate(catalog, new(), true, null, cancellation.Token));
    }
    private sealed class ExpiredClock : TimeProvider
    {
        private long stamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Add(ref stamp, 100);
    }
    [Fact]
    public void Runtime_exhaustion_reports_partial() => Assert.True(new CandidateGenerator(new ExpiredClock()).Generate(Catalog(100), new(), true, null, default).Partial);
}
