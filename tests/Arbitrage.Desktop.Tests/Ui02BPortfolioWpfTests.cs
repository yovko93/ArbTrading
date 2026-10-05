using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui02BPortfolioWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Portfolio_preserves_currency_buckets_positions_and_unknown_fee_valuation(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new PortfolioFixture();
        using var errors = new BindingErrors();
        var view = new PaperPortfolioView { DataContext = data.Portfolio };
        var window = Show(view);
        try
        {
            AssertCommands(view);
            var venueCards = ById<ItemsControl>(view, "PortfolioVenueCards");
            Assert.Equal(2, venueCards.Items.Count);
            var venueTexts = Descendants<TextBlock>(venueCards).Select(t => t.Text).ToArray();
            Assert.Contains("USD", venueTexts); Assert.Contains("USDC", venueTexts);
            Assert.Contains("920", venueTexts); Assert.Contains("1900", venueTexts);
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "PortfolioUnavailable").Visibility);
            Capture(view, theme, "portfolio");

            var positions = Descendants<DataGrid>(view).Single(g => AutomationProperties.GetName(g) == "Open and settled paper positions");
            ScrollTo(view, positions); Flush(window);
            Assert.Equal(3, positions.Items.Count);
            Assert.Contains(Descendants<StatusBadge>(positions), b => b.Label == "Open" && b.Tone == "Info");
            Assert.Contains(Descendants<StatusBadge>(positions), b => b.Label == "Settled" && b.Tone == "Good");
            Assert.Contains(positions.Columns, c => Equals(c.Header, "Market"));
            Assert.Contains(positions.Columns, c => Equals(c.Header, "Instrument"));
            Assert.Contains(positions.Columns, c => Equals(c.Header, "Fees"));
            Assert.False(Descendants<Button>(view).Single(b => Equals(b.Content, "Confirm Paper Resolution")).IsEnabled);
            Capture(view, theme, "portfolio-positions");

            ScrollTo(view, ById<Border>(view, "PortfolioValuationBuckets")); Flush(window);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Unknown exit fees remain unavailable, never zero", StringComparison.Ordinal));
            var valuation = Descendants<DataGrid>(view).Single(g => AutomationProperties.GetName(g) == "Separate venue and currency valuation buckets");
            Assert.Equal(2, valuation.Items.Count);
            Assert.Contains(Descendants<TextBlock>(valuation), t => t.Text == "Unavailable");
            Assert.Equal(2000m, data.Portfolio.ValuationBucket!.AccountingBookValue);
            Assert.Equal(2005m, data.Portfolio.ValuationBucket.GrossMarkedEquity);
            Assert.Null(data.Portfolio.ValuationBucket.FeeAdjustedMarkedEquity);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("estimated exit fees Unavailable (Unknown)", StringComparison.Ordinal));
            Capture(view, theme, "portfolio-valuation");

            ScrollTo(view, ById<Border>(view, "PortfolioRealizedPerformance")); Flush(window);
            var chart = Assert.Single(Descendants<PaperPerformanceChart>(view));
            Assert.Same(data.Portfolio.CurvePoints, chart.Points);
            Assert.Equal(2, data.Portfolio.CurvePoints.Count);
            Capture(view, theme, "portfolio-performance");
            CheckMinimum(view, window, theme, "portfolio");

            data.Portfolio.Account = null;
            data.Portfolio.PositionHistory.Clear();
            data.Portfolio.Valuation = null;
            data.Portfolio.Marks.Clear();
            Flush(window);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "PortfolioUnavailable").Visibility);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "PortfolioPositionsEmpty").Visibility);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "PortfolioValuationEmpty").Visibility);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "PortfolioMarksEmpty").Visibility);
            Capture(view, theme, "portfolio-empty");
        }
        finally { Close(window); }
        Assert.Empty(errors.Messages);
        Assert.Equal(0, data.Handler.Requests);
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Analytics_uses_existing_currency_rows_and_explicit_unavailable_states(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new PortfolioFixture();
        using var errors = new BindingErrors();
        var view = new PaperAnalyticsView { DataContext = data.Analytics };
        var window = Show(view);
        try
        {
            AssertCommands(view);
            Assert.Single(Descendants<Button>(view), b => ReferenceEquals(b.Command, data.Analytics.RefreshCommand));
            Assert.Empty(Descendants<PaperPerformanceChart>(view));
            Assert.Equal(2, data.Analytics.Buckets.Count);
            Assert.Equal(new[] { "USD", "USDC" }, data.Analytics.Buckets.Select(b => b.Currency));
            Assert.Equal(20m, data.Analytics.Buckets[0].RealizedPnl);
            Assert.Null(data.Analytics.Buckets[1].FeeAdjustedMarkedEquity);
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "AnalyticsCapitalEmpty").Visibility);
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "AnalyticsValuationEmpty").Visibility);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Unavailable");
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Paper evidence does not enable live trading", StringComparison.Ordinal));
            Capture(view, theme, "analytics");
            CheckMinimum(view, window, theme, "analytics");

            data.Analytics.Valuation = null;
            data.Analytics.Buckets.Clear();
            foreach (var bucket in data.Analytics.Performance!.Buckets) data.Analytics.Buckets.Add(new(bucket, null));
            // Production refresh publishes summary notifications together. Rebind this explicitly
            // assigned fixture snapshot so its computed summary properties are read together too.
            view.DataContext = null; view.DataContext = data.Analytics;
            Flush(window);
            Assert.All(data.Analytics.Buckets, row => Assert.Null(row.AccountingBookValue));
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Current executable valuation unavailable.");
            window.Width = 1160; window.Height = 1040; Flush(window);
            Capture(view, theme, "analytics-unavailable");

            data.Analytics.Account = null; data.Analytics.Performance = null; data.Analytics.Buckets.Clear();
            data.Analytics.Notice = "Private analytics cleared. Refresh after workspace access is restored.";
            view.DataContext = null; view.DataContext = data.Analytics;
            Flush(window);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "AnalyticsCapitalEmpty").Visibility);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "AnalyticsValuationEmpty").Visibility);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == data.Analytics.Notice);
            Capture(view, theme, "analytics-empty");
        }
        finally { Close(window); }
        Assert.Empty(errors.Messages);
        Assert.Equal(0, data.Handler.Requests);
    });

    internal sealed class PortfolioFixture : IDisposable
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
        private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
        private readonly HttpClient http;
        private readonly MainViewModel state;
        public RejectHandler Handler { get; } = new();
        public PaperTradingViewModel Portfolio { get; }
        public PaperAnalyticsViewModel Analytics { get; }

        public PortfolioFixture()
        {
            http = new HttpClient(Handler);
            var backend = new BackendClient(http, new MissingConnection());
            state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
            state.ApplyRealtimeSnapshot(new(1, Id(1), Id(2), At, new(Id(3), Id(4), "Local", Capabilities.Phase04A),
                new("UI-02B fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(Id(4), "Isolated paper fixture"), []), "http://127.0.0.1:5274");
            Portfolio = new PaperTradingViewModel(state, backend);
            Analytics = new PaperAnalyticsViewModel(state, null);
            var generation = new PaperGenerationResponse(Id(5), At.AddDays(-2), null, "Deterministic UI fixture", "Healthy");
            var account = new PaperAccountResponse("Active", generation,
                [new("Kalshi", "USD", 1000, 920, 0, 920, 1, At), new("Polymarket", "USDC", 2000, 1900, 0, 1900, 1, At)], [generation]);
            Portfolio.Account = account; Portfolio.SettlementGeneration = generation;
            Portfolio.PositionFilter = "All";
            var usd = new PaperPositionResponse(Id(10), generation.Id, "Kalshi", "FIXTURE-KALSHI-OPEN", "yes", "Yes", "USD", 200, 100, 1, .495m, At, At);
            var usdc = new PaperPositionResponse(Id(11), generation.Id, "Polymarket", "FIXTURE-POLY-OPEN", "asset-90001", "No", "USDC", 250, 100, 0, .4m, At, At);
            var settled = new PaperPositionResponse(Id(12), generation.Id, "Kalshi", "FIXTURE-KALSHI-SETTLED", "yes", "Yes", "USD", 60, 40, 1, .65m,
                At.AddDays(-1), At, "Settled", At, 60, 20, Id(13), "ManualScenario");
            foreach (var position in new[] { usd, usdc, settled }) Portfolio.PositionHistory.Add(position);
            Portfolio.Performance = new(generation.Id, "Active",
                [new("Kalshi", "USD", 1000, 920, 100, 40, 60, 20, 1, 1, 1, 0, 1), new("Polymarket", "USDC", 2000, 1900, 100, 0, 0, 0, 1, 0, 1, 0, 0)]);
            Portfolio.SelectedBucket = Portfolio.Performance.Buckets[0];
            Portfolio.CurvePoints.Add(new(At.AddDays(-1), "Kalshi", "USD", 900, 0, 0, 1000, "PaperExecution", Id(20), null));
            Portfolio.CurvePoints.Add(new(At, "Kalshi", "USD", 920, 20, 20, 1020, "ManualScenario", Id(21), Id(13)));
            var usdMark = new PaperPositionMarkResponse(usd, "Marked", "RealtimeVerified", At, 200, 0, 104, null, .52m, null, .51m,
                1, "Estimated", 103, 4, 3, .04m, .03m, 17, "Realtime", "Continuous", At, At, 0, [], []);
            var usdcMark = new PaperPositionMarkResponse(usdc, "Marked", "RestSnapshot", At, 250, 0, 105, null, .42m, null, .41m,
                null, "Unknown", null, 5, null, .05m, null, 18, "Rest", "Snapshot", At, At, 0, [], ["Exit fee model unresolved in fixture."]);
            var usdBucket = new PaperValuationBucketResponse("Kalshi", "USD", 1000, 920, 100, 20, 1, 1, 0, 0,
                104, 103, 104, 1020, 1024, 1023, 24, 23, 1, .1m, 100, 1, 1);
            var usdcBucket = new PaperValuationBucketResponse("Polymarket", "USDC", 2000, 1900, 100, 0, 1, 1, 0, 0,
                105, null, 105, 2000, 2005, null, 5, null, 1, .05m, 100, 1, 1);
            Portfolio.Valuation = new(generation.Id, "Available", false, At, [usdMark, usdcMark], [usdBucket, usdcBucket], 1, false, ["Fixture: unresolved exit fees are not zero."]);
            Portfolio.ValuationBucket = usdcBucket;
            Portfolio.Marks.Add(usdMark); Portfolio.Marks.Add(usdcMark); Portfolio.SelectedMark = usdcMark;
            Portfolio.Resolutions.Add(new(Id(13), generation.Id, Id(14), Id(3), "Kalshi", settled.MarketId, "ManualScenario", "Committed", At, At,
                [new("yes", "Yes", 1), new("no", "No", 0)], [settled], [Id(21)], Id(15)));
            Portfolio.SelectedResolution = Portfolio.Resolutions[0];
            var candidate = new PaperResolutionCandidateResponse(generation.Id, "Kalshi", usd.MarketId, "Deterministic paper market", true,
                [new("yes", "Yes", 0), new("no", "No", 0)], [usd], [Id(20)], []);
            Portfolio.ResolutionCandidates.Add(candidate); Portfolio.SelectedCandidate = candidate; Portfolio.WinningOutcome = candidate.Outcomes[0];
            Portfolio.Notice = "Deterministic paper fixture. No real runtime or exchange assets.";
            Portfolio.ValuationNotice = "Local executable bid-depth fixture. Unknown exit fees remain unavailable.";
            Analytics.Account = account; Analytics.Performance = Portfolio.Performance; Analytics.Valuation = Portfolio.Valuation;
            foreach (var bucket in Portfolio.Performance.Buckets)
                Analytics.Buckets.Add(new(bucket, Portfolio.Valuation.Buckets.Single(v => v.Exchange == bucket.Exchange && v.Currency == bucket.Currency)));
            Analytics.Notice = "Read-only fixture summary. USD and USDC are separate; no combined monetary total.";
        }

        public void Dispose() { Analytics.Dispose(); Portfolio.Dispose(); state.Dispose(); http.Dispose(); }
    }
}
