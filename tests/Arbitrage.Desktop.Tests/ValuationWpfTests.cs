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
public sealed class ValuationWpfTests(WpfFixture fixture)
{
    [Fact] public Task Valuation_states_render_light_and_dark_with_explicit_unknowns() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend); var at = DateTimeOffset.Parse("2026-09-23T12:00:00Z"); var g = Guid.NewGuid();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "Profitable", "Losing", "PartialDepth", "Stale", "FeeUnresolved", "MixedCoverage", "FeeAdjusted", "PartialSettlement", "SeparateCurrencies" })
        {
            vm.Marks.Clear(); var partial = scenario == "PartialDepth"; var stale = scenario == "Stale"; var full = !partial && !stale;
            var fees = full && scenario != "FeeUnresolved"; var cost = scenario == "Losing" ? 18m : 12.5m;
            var position = new PaperPositionResponse(Guid.NewGuid(), g, "Polymarket", "FIXTURE-NATIVE-TOKEN", "123", "Yes", "USDC", 25, cost, .25m, .49m, at, at);
            var mark = new PaperPositionMarkResponse(position, partial ? "PartialDepth" : stale ? "BookStale" : "Marked", full ? "RealtimeBestEffort" : "NonActionable", at,
                stale ? null : partial ? 20 : 25, stale ? null : partial ? 5 : 0, full ? 14.75m : null, partial ? 11.9m : null, full ? .59m : null, partial ? .595m : null, full ? .57m : null,
                fees ? .60445m : null, fees ? "Estimated" : "ScheduleUnavailable", fees ? 14.14555m : null, full ? 14.75m - cost : null, fees ? 14.14555m - cost : null,
                full ? (14.75m - cost) / cost : null, fees ? (14.14555m - cost) / cost : null, 42, "Realtime", "BestEffort", at.AddSeconds(-2), at.AddSeconds(-3), stale ? 30 : 2, [],
                ["Diagnostic local cached depth; no order is created."]);
            vm.Marks.Add(mark); vm.SelectedMark = mark;
            if (scenario == "PartialSettlement") vm.Marks.Add(mark with { Position = position with { Id = Guid.NewGuid(), Status = "Settled", SettlementPayout = 10, RealizedPnl = 5 }, MarkStatus = "NotApplicable", GrossLiquidationValue = null, FeeAdjustedLiquidationValue = null, GrossUnrealizedPnlBeforeExitFees = null, FeeAdjustedUnrealizedPnl = null });
            var mixed = scenario == "MixedCoverage";
            if (mixed) vm.Marks.Add(mark with { Position = position with { Id = Guid.NewGuid(), InstrumentId = "456", Outcome = "No" }, MarkStatus = "BookUnavailable", GrossLiquidationValue = null, FeeAdjustedLiquidationValue = null });
            var bucket = new PaperValuationBucketResponse("Polymarket", "USDC", 100, 80, cost, 0, mixed ? 2 : 1, full ? 1 : 0, partial ? 1 : 0, stale || mixed ? 1 : 0,
                full && !mixed ? 14.75m : null, fees && !mixed ? 14.14555m : null, full ? 14.75m : 0, 80 + cost, full && !mixed ? 94.75m : null, fees && !mixed ? 94.14555m : null,
                full && !mixed ? 14.75m - cost : null, fees && !mixed ? 14.14555m - cost : null, mixed ? .5m : full ? 1 : 0, cost / 100, cost, 1, 1);
            vm.Valuation = new(g, "Available", false, at, vm.Marks.ToArray(), scenario == "SeparateCurrencies" ? [bucket, bucket with { Exchange = "Kalshi", Currency = "USD", FeeAdjustedMarkedEquity = null }] : [bucket], 1, false, []);
            vm.ValuationBucket = bucket; vm.ValuationNotice = scenario + " · point-in-time executable liquidation estimate from LOCAL cached state.";
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var view = new PaperValuationView { DataContext = vm }; view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 1100)); view.Arrange(new Rect(0, 0, 1440, 1100)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); view.UpdateLayout();
            Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "Refresh Valuation"));
            Assert.DoesNotContain(Descendants<Button>(view), b => b.Content is "Sell Position" or "Close Position" or "Exit Trade");
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Current Executable Mark", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("Response {", StringComparison.Ordinal));
            if (!full || !fees || mixed) Assert.Contains("Unavailable", vm.ValuationSummary);
            if (scenario == "Losing") Assert.Contains("-3.25", vm.ValuationSummary);
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } capture)
            {
                Directory.CreateDirectory(capture); var bitmap = new RenderTargetBitmap(1440, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(capture, $"valuation-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var value in Descendants<T>(child)) yield return value; } }
    private sealed class Reject : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network."); }
    private sealed class Connection : ILocalConnectionFile
    { public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials."); public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException(); }
}
