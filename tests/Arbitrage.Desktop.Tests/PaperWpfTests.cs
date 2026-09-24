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
public sealed class PaperWpfTests(WpfFixture fixture)
{
    [Fact] public Task Paper_states_render_in_both_themes_with_explicit_confirmation() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend);
        var at = DateTimeOffset.Parse("2026-09-23T12:00:00Z"); var generation = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(workspace, "Paper fixture"), []), "http://127.0.0.1:5274");
        var proof = OpportunityWpfTests.Result("Detected") with { RelationshipTrust = "Deterministic", RelationshipRevision = "verified-fixture-revision",
            Fees = new("FeeAdjustedDetected", "Estimated", "DirectMember", [], .26792m, 9.36792m, .63208m, .063208m, null, .001m),
            Legs = OpportunityWpfTests.Result("Detected").Legs.Select(l => l with { RetrievedAt = at.AddSeconds(-1) }).ToArray() };
        PaperFillResponse[] fills = [new(Guid.NewGuid(), Guid.NewGuid(), "Kalshi", "FIXTURE-A", "yes", "Yes", "Buy", 10, .4m, 4, .168m, "USD", "DerivedComplement", new("Kalshi", "FIXTURE-A", "no", "Sell", .6m), 3, at),
            new(Guid.NewGuid(), Guid.NewGuid(), "Polymarket", "FIXTURE-B", "456", "No", "Buy", 5, .5m, 2.5m, .05m, "USD", "NativeAsk", new("Polymarket", "FIXTURE-B", "456", "Buy", .5m), 4, at),
            new(Guid.NewGuid(), Guid.NewGuid(), "Polymarket", "FIXTURE-B", "456", "No", "Buy", 5, .52m, 2.6m, .04992m, "USD", "NativeAsk", new("Polymarket", "FIXTURE-B", "456", "Buy", .52m), 4, at)];
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "Uninitialized", "InitializedEmpty", "EligiblePreview", "InsufficientPaperFunds", "OpenPositionsHistory", "FeeModelUnresolved", "MarketDataChanged" })
        {
            vm.Quantity = 10;
            vm.Account = scenario == "Uninitialized" ? new("Uninitialized", null, [], []) : new("Active", new(generation, at, null, "Explicit paper fixture", "Healthy"),
                [new("Kalshi", "USD", 100, 95.832m, 0, 95.832m, 2, at), new("Polymarket", "USD", 100, 94.80008m, 0, 94.80008m, 2, at)], []);
            vm.Preview = null; vm.Positions.Clear(); vm.Executions.Clear(); vm.SelectedExecution = null;
            vm.Notice = scenario == "InitializedEmpty" ? "Initialized paper generation — no executions yet." : "SIMULATION ONLY — " + scenario;
            if (scenario is "EligiblePreview" or "InsufficientPaperFunds" or "FeeModelUnresolved" or "MarketDataChanged")
                vm.Preview = new(Guid.NewGuid(), generation, at, at.AddSeconds(5), scenario == "EligiblePreview", scenario == "EligiblePreview" ? "None" : scenario,
                    new string('A', 64), 10, 10, fills, [new("Kalshi", "USD", 4, .168m, 4.168m, 100, 95.832m), new("Polymarket", "USD", 5.1m, .09992m, 5.19992m, 100, 94.80008m)],
                    9.1m, .26792m, 9.36792m, 10, .63208m, proof, ["Snapshot Paper Fill — no real orders submitted.", "Expected payout and profit at resolution are projections; no settlement or realized profit."]);
            if (scenario is "FeeModelUnresolved" or "MarketDataChanged")
                vm.Preview = vm.Preview! with { Fills = [], Debits = [], ExecutableQuantity = 0, GrossCost = null, TotalFees = null, FeeAdjustedCost = null,
                    ExpectedPayoutAtResolution = null, ExpectedProfitAtResolution = null, Proof = null };
            if (scenario == "EligiblePreview") vm.Preview = vm.Preview! with { RiskDecision = new("Approved", 1, Guid.NewGuid(), new string('A', 64), generation, 0, [], [], [], 0, 2, 0, 1, 0, 1, [], at) };
            if (scenario == "InsufficientPaperFunds")
            {
                vm.Account = vm.Account! with { Balances = vm.Account!.Balances.Select(b => b with { InitialCash = 5, AvailableCash = 5, TotalCash = 5 }).ToArray() };
                vm.Preview = vm.Preview! with { Debits = vm.Preview!.Debits.Select(d => d with { AvailableCash = 5, RemainingCash = 5 - d.Total }).ToArray() };
            }
            if (scenario == "OpenPositionsHistory")
            {
                vm.Positions.Add(new(Guid.NewGuid(), generation, "Kalshi", "FIXTURE-A", "yes", "Yes", "USD", 10, 4.168m, .168m, .4m, at, at));
                vm.Positions.Add(new(Guid.NewGuid(), generation, "Polymarket", "FIXTURE-B", "456", "No", "USD", 10, 5.19992m, .09992m, .51m, at, at));
                var execution = new PaperExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), generation, Guid.NewGuid(), at, "Committed", new string('A', 64), 10, 9.36792m, 10, .63208m, fills, proof);
                vm.Executions.Add(execution); vm.SelectedExecution = execution;
            }
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            UserControl view = scenario == "OpenPositionsHistory" ? new PaperPortfolioView { DataContext = vm } : new PaperTradingView { DataContext = vm };
            view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 1400)); view.Arrange(new Rect(0, 0, 1440, 1400)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var texts = Descendants<TextBlock>(view).Select(t => t.Text).ToArray();
            Assert.Contains(texts, t => t.Contains("SIMULATION ONLY", StringComparison.Ordinal));
            if (view is PaperTradingView)
            {
                Assert.Contains(texts, t => t.Contains("No real orders", StringComparison.Ordinal));
                Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "Confirm paper execution…"));
                Assert.Equal(scenario == "EligiblePreview", Descendants<Button>(view).Single(b => Equals(b.Content, "Confirm paper execution…")).IsEnabled);
            }
            Assert.DoesNotContain(Descendants<Button>(view), b => b.Content is "Trade" or "Execute live" or "Buy now");
            if (scenario == "Uninitialized") Assert.Contains(texts, t => t.Contains("Uninitialized", StringComparison.Ordinal));
            if (scenario == "OpenPositionsHistory") Assert.Contains(texts, t => t.Contains("Committed", StringComparison.Ordinal));
            if (scenario == "EligiblePreview") Assert.Contains(texts, t => t.Contains("Would execute", StringComparison.Ordinal));
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } capture)
            {
                Directory.CreateDirectory(capture); var bitmap = new RenderTargetBitmap(1440, 1400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(capture, $"paper-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var value in Descendants<T>(child)) yield return value; }
    }
    private sealed class Reject : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network in WPF fixture."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials in WPF fixture.");
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
}
