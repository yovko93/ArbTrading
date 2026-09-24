using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
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
public sealed class PaperPageSplitWpfTests(WpfFixture fixture)
{
    [Fact]
    public Task Populated_pages_have_exclusive_composition_in_light_and_dark() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend);
        var at = DateTimeOffset.Parse("2026-09-23T12:00:00Z"); var g = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(workspace, "Split fixture"), []), "http://127.0.0.1:5274");
        var generation = new PaperGenerationResponse(g, at, null, "Split page fixture", "Healthy");
        vm.Account = new("Active", generation, [new("Kalshi", "USD", 100, 95.832m, 0, 95.832m, 1, at)], [generation]);
        vm.SettlementGeneration = generation;
        var position = new PaperPositionResponse(Guid.NewGuid(), g, "Kalshi", "FIXTURE-A", "yes", "Yes", "USD", 10, 4.168m, .168m, .4m, at, at);
        vm.PositionHistory.Add(position);
        var execution = new PaperExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), g, Guid.NewGuid(), at, "Committed", new string('A', 64), 10, 4.168m, 10, 5.832m, [], OpportunityWpfTests.Result("Detected"));
        vm.Executions.Add(execution); vm.SelectedExecution = execution;
        var candidate = new PaperResolutionCandidateResponse(g, "Kalshi", "FIXTURE-A", "Fixture binary market", true,
            [new("yes", "Yes", 0), new("no", "No", 0)], [position], [execution.Id], []);
        vm.ResolutionCandidates.Add(candidate); vm.SelectedCandidate = candidate; vm.WinningOutcome = candidate.Outcomes[0];
        vm.Performance = new(g, "Active", [new("Kalshi", "USD", 100, 95.832m, 4.168m, 0, 0, 0, 1, 0, 1, 0, 0)]);
        vm.SelectedBucket = vm.Performance.Buckets[0];
        vm.CurvePoints.Add(new(at, "Kalshi", "USD", 95.832m, 0, 0, 100, "PaperExecution", execution.Id, null));
        var mark = new PaperPositionMarkResponse(position, "Marked", "RealtimeVerified", at, 10, 0, 5m, null, .5m, null, .49m,
            .175m, "Estimated", 4.825m, .832m, .657m, .1996m, .1576m, 42, "Realtime", "Verified", at, at, 0, [], []);
        var bucket = new PaperValuationBucketResponse("Kalshi", "USD", 100, 95.832m, 4.168m, 0, 1, 1, 0, 0,
            5m, 4.825m, 5m, 100m, 100.832m, 100.657m, .832m, .657m, 1, .04168m, 4.168m, 1, 1);
        vm.Valuation = new(g, "Available", false, at, [mark], [bucket], 1, false, []);
        vm.ValuationBucket = bucket; vm.Marks.Add(mark);
        var decision = new PaperRiskDecisionResponse("Approved", 1, Guid.NewGuid(), new string('A', 64), g, 1, [], [], [], 1, 2, 1, 2, 0, 1, [], at);
        vm.RiskStatus = new("WithinLimits", new(1, decision.PolicyRevision!.Value, new(.2m, .1m, .6m, .2m, .2m, 20, 10, 2, 1000, .001m, 0), at, at, Guid.NewGuid(), new string('A', 64)), decision);
        vm.AutomationStatus = new("Disarmed", "OwnerDisarmed", new(1, Guid.NewGuid(), new("FixedQuantity", 1, .005m, .05m, 10, 10, 1, 5, 60, .25m, 20, true, false), at, at, Guid.NewGuid(), new string('A', 64)),
            Guid.NewGuid(), g, new(Guid.NewGuid(), false, null, null, "", null, null), null, null, null, "Running", 1, 4, 2, 1, at, at, [], 0, new Dictionary<string, long>());
        vm.OpportunityKey = new string('A', 64);
        vm.Quantity = 10;
        vm.Notice = "Paper generation healthy. All amounts are simulated; no real orders submitted.";
        vm.RiskNotice = "Saved paper policy applies to new entries only.";
        vm.AutomationNotice = "Saved profile is disarmed. Arming requires explicit confirmation.";
        vm.ValuationNotice = "Local executable bid-depth mark. No live assets or exit orders.";
        vm.Preview = new(Guid.NewGuid(), g, at, at.AddSeconds(5), true, "None", vm.OpportunityKey, 10, 10,
            [new(Guid.NewGuid(), Guid.NewGuid(), "Kalshi", "FIXTURE-A", "yes", "Yes", "Buy", 10, .4m, 4, .168m, "USD", "NativeAsk", new("Kalshi", "FIXTURE-A", "yes", "Buy", .4m), 42, at)],
            [new("Kalshi", "USD", 4, .168m, 4.168m, 95.832m, 91.664m)], 4, .168m, 4.168m, 10, 5.832m, null, ["Snapshot paper fixture"], decision);
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var trading in new[] { true, false })
        {
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            UserControl view = trading ? new PaperTradingView() : new PaperPortfolioView();
            view.DataContext = vm; view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 3200)); view.Arrange(new Rect(0, 0, 1440, 3200)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); view.UpdateLayout();
            var nodes = Descendants(view).ToArray();
            Assert.Equal(trading, nodes.OfType<PaperRiskView>().Any());
            Assert.Equal(trading, nodes.OfType<PaperAutomationView>().Any());
            Assert.Equal(!trading, nodes.OfType<PaperSettlementView>().Any());
            Assert.Equal(!trading, nodes.OfType<PaperValuationView>().Any());
            foreach (var name in new[] { "Paper venue balances", "Open and settled paper positions", "Paper execution history", "Paper settlement history" })
                Assert.Equal(!trading, nodes.Any(n => AutomationProperties.GetName(n) == name));
            foreach (var command in new object[] { vm.SaveRiskPolicyCommand, vm.ArmAutomationCommand, vm.EmergencyStopAutomationCommand, vm.PreviewCommand, vm.ExecuteCommand, vm.InitializeCommand })
                Assert.Equal(trading, nodes.OfType<Button>().Any(b => ReferenceEquals(b.Command, command)));
            Assert.Equal(trading, nodes.Any(n => AutomationProperties.GetName(n) == "Preview snapshot fills"));
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(1440, 3200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, $"split-{(trading ? "Trading" : "Portfolio")}-{theme}.png")); encoder.Save(stream);
            }
        }
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime connection in visual fixture.");
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
}
