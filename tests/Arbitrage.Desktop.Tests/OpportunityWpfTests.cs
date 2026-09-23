using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class OpportunityWpfTests(WpfFixture fixture)
{
    [Fact] public Task Fee_unknown_negative_and_stale_render_without_zero_in_both_themes() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new RejectHandler()), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new OpportunitiesViewModel(state, backend) { ShowDiagnostics = true };
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var status in new[] { "GrossDetectedFeeUnknown", "FeeAdjustedNoEdge", "FeeResultStale", "AccountFeeProfileRequired" })
        {
            var negative = status == "FeeAdjustedNoEdge";
            var quote = new FeeQuoteResponse("Kalshi", "FIXTURE-A", "yes", "Taker", 25, .4m, .42m, .42m, null, null, null, "USD", "KnownModelAccountRoundingUnknown",
                "https://docs.kalshi.com/getting_started/fee_rounding", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "fixture-fingerprint", ["Diagnostic fixture"], "NotIncluded");
            var item = Result("Detected") with { Warnings = ["Gross values exclude fees. See the separate fee diagnostic."], Fees = new(status, negative ? "Estimated" : "KnownModelAccountRoundingUnknown", negative ? "NonDirectMember" : "Unknown", [quote], negative ? 1.95968m : null,
                negative ? 25.30968m : null, negative ? -.30968m : null, negative ? -.0123872m : null, null, 0) };
            vm.Items.Clear(); vm.Items.Add(item); vm.Selected = item; vm.Total = 1;
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var view = new OpportunitiesView { DataContext = vm }; view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1400, 1100)); view.Arrange(new Rect(0, 0, 1400, 1100)); view.UpdateLayout();
            var texts = Descendants<TextBlock>(view).Select(t => t.Text).ToArray();
            Assert.Contains(texts, t => t.Contains("diagnostic assumption", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("fee_rounding", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains(status, StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains(negative ? "-0.30968" : "Exchange fees: Unknown", StringComparison.Ordinal));
            Assert.Contains(Descendants<Button>(view), b => b.Content is "Refresh Fee Data for selected result");
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } capture)
            {
                Directory.CreateDirectory(capture); var bitmap = new RenderTargetBitmap(1400, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(capture, $"fees-{theme}-{status}.png")); encoder.Save(stream);
            }
        }
    });
    [Fact] public Task Prefee_provenance_trust_and_blocked_states_render_in_light_and_dark() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new RejectHandler()), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new OpportunitiesViewModel(state, backend) { ShowDiagnostics = true, IncludeManualRelationships = true, Notice = "Isolated read-only fixture. No exchange request was made." };
        var statuses = new[] { "Detected", "NoGrossEdge", "BookStale", "BookSkewTooLarge", "RelationshipIneligible", "LiquidityConflict" };
        foreach (var status in statuses) vm.Items.Add(Result(status));
        vm.Total = vm.Items.Count;
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var item in vm.Items)
        {
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            vm.Selected = item;
            var view = new OpportunitiesView { DataContext = vm }; view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1180, 1000)); view.Arrange(new Rect(0, 0, 1180, 1000)); view.UpdateLayout();
            var texts = Descendants<TextBlock>(view).Select(t => t.Text).ToArray();
            Assert.Contains(texts, t => t.Contains("PRE-FEE", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("DerivedComplement", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("Manual", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("Long relationship title", StringComparison.Ordinal));
            if (item.Status != "Detected") Assert.Contains(texts, t => t.Contains("Blocker:", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<Button>(view), b => b.Content is "Execute" or "Trade" or "Auto Trade" or "Paper Trade");
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } capture)
            {
                Directory.CreateDirectory(capture); var bitmap = new RenderTargetBitmap(1180, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(capture, $"opportunities-{theme}-{item.Status}.png")); encoder.Save(stream);
            }
        }
    });
    private static OpportunityResponse Result(string status)
    {
        var result = new OpportunityResponse("fixture", "CrossMarketBuyBothComplements", Guid.NewGuid(), "Manual", DateTimeOffset.UtcNow,
        status, status == "Detected" ? [] : ["Cached inputs do not establish a current candidate. Explicitly reevaluate after correcting inputs."],
        ["PRE-FEE: fees and execution are not evaluated.", "Cross-exchange observations are not atomic."], status is "Detected" or "NoGrossEdge" ? "RealtimeBestEffort" : "NonActionable", status == "BookSkewTooLarge" ? 1500 : 12, 1000, status != "BookSkewTooLarge",
        [new("Kalshi", "FIXTURE-A", "yes", "Yes", "Buy", 25, .418m, .43m, 10.45m, "Realtime", "Continuous", 3, DateTimeOffset.UtcNow, null, ["DerivedComplement"],
            [new("Kalshi", "FIXTURE-A", "no", "Sell", .6m)]),
         new("Polymarket", "FIXTURE-B", "456", "No", "Buy", 25, .516m, .52m, 12.9m, "Realtime", "BestEffort", 4, DateTimeOffset.UtcNow, null, ["NativeAsk"], [])],
        [new(5, .4m, .5m, .9m, .1m, 4.5m, 5, .5m), new(5, .4m, .52m, .92m, .08m, 9.1m, 10, .9m), new(15, .43m, .52m, .95m, .05m, 23.35m, 25, 1.65m)],
        25, 25, 23.35m, 0, 25, 1.65m, .066m, 1.65m / 23.35m, status != "RelationshipIneligible", status is "Detected" or "NoGrossEdge", status == "Detected", false, false, false, 1,
        new string('A', 64), new string('B', 64), new string('C', 64), false, "NotEvaluated", null, null)
        { SourceTitle = "Long relationship title — final certified election outcome subject to the approved observation window, settlement authority and explicit cancellation conditions",
          TargetTitle = "Complementary outcome under the same verified settlement conditions", OutcomeMappings = [new("yes", "456", "EquivalentOppositeOutcome")] };
        return status != "NoGrossEdge" ? result : result with { PairedQuantity = 0, FullyExecutableQuantity = 0, GrossCost = 0, GuaranteedGrossPayout = 0, GrossProfit = 0, GrossEdgePerShare = null, GrossReturnOnCost = null, Segments = [],
            Legs = result.Legs.Select(l => l with { Quantity = 0, AveragePrice = null, WorstPrice = null, GrossNotional = 0 }).ToArray() };
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class RejectHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Fixture must not make HTTP requests."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("Fixture must not read runtime credentials.");
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
}
