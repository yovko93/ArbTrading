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
public sealed class SettlementWpfTests(WpfFixture fixture)
{
    [Fact] public Task Settlement_states_render_light_and_dark_with_separate_realized_series() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend); var at = DateTimeOffset.Parse("2026-09-23T12:00:00Z"); var w = Guid.NewGuid(); var g = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        var generation = new PaperGenerationResponse(g, at, null, "Explicit paper scenario", "Healthy"); var resolution = Guid.NewGuid();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "OpenPositions", "ResolutionPreview", "WinningScenario", "LosingPosition", "PartialExecution", "FullySettled", "SettlementHistory", "PositivePnl", "NegativePnl", "SeparateCurrencies" })
        {
            vm.Account = new("Active", generation, [], [generation]); vm.SettlementGeneration = generation;
            vm.ResolutionCandidates.Clear(); vm.PositionHistory.Clear(); vm.Resolutions.Clear(); vm.CurvePoints.Clear(); vm.ResolutionPreview = null; vm.SelectedResolution = null;
            var closed = scenario is not ("OpenPositions" or "ResolutionPreview"); var loss = scenario is "LosingPosition" or "NegativePnl";
            var pnl = loss ? -4.168m : 5.832m; var payout = loss ? 0m : 10m;
            var position = new PaperPositionResponse(Guid.NewGuid(), g, "Kalshi", "FIXTURE-A", "yes", "Yes", "USD", 10, 4.168m, .168m, .4m, at, at,
                closed ? "Settled" : "Open", closed ? at : null, closed ? payout : null, closed ? pnl : null, closed ? resolution : null, closed ? "ManualScenario" : null);
            vm.PositionHistory.Add(position);
            var candidate = new PaperResolutionCandidateResponse(g, "Kalshi", "FIXTURE-A", "Fixture standard binary market", true,
                [new("yes", "Yes", 0), new("no", "No", 0)], [position], [Guid.NewGuid()], []);
            vm.ResolutionCandidates.Add(candidate); vm.SelectedCandidate = candidate; vm.WinningOutcome = candidate.Outcomes[loss ? 1 : 0];
            var economics = new PaperExecutionSettlementResponse(scenario == "FullySettled" ? "Settled" : "PartiallySettled", payout, pnl, 5.19992m, 10 - payout,
                scenario == "FullySettled" ? .63208m : null, scenario == "FullySettled" ? .63208m / 9.36792m : null, scenario == "FullySettled" ? 0 : null);
            vm.ResolutionPreview = new(Guid.NewGuid(), at, at.AddSeconds(30), new(g, "Kalshi", "FIXTURE-A", loss ? "no" : "yes"), scenario == "ResolutionPreview", closed ? "Recorded scenario" : "None",
                [new("yes", "Yes", loss ? 0 : 1), new("no", "No", loss ? 1 : 0)], [new(position.Id, "yes", "Yes", "USD", 10, 4.168m, payout, pnl)],
                [new(Guid.NewGuid(), economics)], [new("Kalshi", "USD", payout, 95.832m, 95.832m + payout)], ["Confirmed scenarios are immutable. Use another generation for another outcome."]);
            if (closed)
            {
                var r = new PaperResolutionResponse(resolution, g, Guid.NewGuid(), Guid.NewGuid(), "Kalshi", "FIXTURE-A", "ManualScenario", "Committed", at, at,
                    vm.ResolutionPreview.PayoutVector, [position], candidate.ExecutionIds, Guid.NewGuid()); vm.Resolutions.Add(r); vm.SelectedResolution = r;
            }
            vm.Performance = new(g, "Active", [new("Kalshi", "USD", 100, 95.832m + (closed ? payout : 0), closed ? 0 : 4.168m, closed ? 4.168m : 0,
                closed ? payout : 0, closed ? pnl : 0, closed ? 0 : 1, closed ? 1 : 0, closed ? 0 : 1, scenario == "PartialExecution" ? 1 : 0, scenario == "FullySettled" ? 1 : 0),
                new("Polymarket", "USDC", 100, 94.80008m, 5.19992m, 0, 0, 0, 1, 0, 1, 0, 0)]);
            vm.SelectedBucket = vm.Performance.Buckets[0];
            vm.CurvePoints.Add(new(at.AddMinutes(-1), "Kalshi", "USD", 100, 0, 0, 100, "ExplicitInitialFunding", null, null));
            if (closed) vm.CurvePoints.Add(new(at, "Kalshi", "USD", 95.832m + payout, pnl, pnl, 100 + pnl, "PaperSettlement", null, resolution));
            vm.Notice = scenario + " · Manual Scenario Resolution";
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var view = new PaperSettlementView { DataContext = vm }; view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 2000)); view.Arrange(new Rect(0, 0, 1440, 2000)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); view.UpdateLayout();
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("MANUAL PAPER SCENARIO", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("Response {", StringComparison.Ordinal));
            Assert.Equal(scenario == "ResolutionPreview", Descendants<Button>(view).Single(b => Equals(b.Content, "Confirm Paper Resolution")).IsEnabled);
            Assert.DoesNotContain(Descendants<Button>(view), b => b.Content is "Redeem" or "Settle Live" or "Resolve Exchange");
            Assert.Equal(new[] { "USD", "USDC" }, vm.Performance.Buckets.Select(b => b.Currency));
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } capture)
            {
                Directory.CreateDirectory(capture); var bitmap = new RenderTargetBitmap(1440, 2000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(capture, $"settlement-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var value in Descendants<T>(child)) yield return value; } }
    private sealed class Reject : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network."); }
    private sealed class Connection : ILocalConnectionFile
    { public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials."); public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException(); }
}
