using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui04OperatorWpfTests(WpfFixture fixture)
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T12:00:02Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Catalog_transitions_and_book_provenance_are_truthful(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        var clock = new FixtureClock();
        using var data = new Ui02WpfTests.WorkflowFixture(clock);
        using var errors = new BindingErrors();
        var view = new MarketExplorerView { DataContext = data.Catalog };
        using var host = new PageHost(view);
        var failed = data.Catalog.ExchangeStatuses[0];
        data.Catalog.StatusCheckedAt = At;
        foreach (var (state, tone) in new[] { ("Failed", "Error"), ("Running", "Info"), ("Complete", "Good"), ("Partial", "Warning"), ("Cancelled", "Warning"), ("Never", "Neutral") })
        {
            data.Catalog.ExchangeStatuses[0] = failed with { LatestRun = state == "Never" ? null : failed.LatestRun! with { State = state, ErrorCode = state == "Failed" ? "InternalFailure" : null } };
            host.Layout();
            var panel = Descendants<Border>(view).First(b => AutomationProperties.GetAutomationId(b) == "CatalogSyncExchange");
            Assert.Contains(Descendants<StatusBadge>(panel), b => b.Label == state && b.Tone == tone);
            Assert.Contains("367848", Text(panel));
            if (state == "Failed") Assert.Contains("InternalFailure", Text(panel));
            else Assert.DoesNotContain("InternalFailure", Text(panel));
            if (state == "Never") Assert.Contains("Unavailable", Text(panel));
            if (state is "Failed" or "Running" or "Complete") host.Capture(theme, "catalog-" + state);
        }
        Assert.Contains("Polymarket", Text(view));
        Assert.Contains("Backend status checked: 2026-10-04 12:00:02 UTC", Text(view));
        data.SelectBook();
        var book = data.Catalog.OrderBook;
        var response = book.Response!;
        book.Response = response with { Realtime = new("Realtime", "Streaming", "Continuous", true, 1, 5, 1, null, 7, At.AddSeconds(-2), At, At, At, null, .01m) };
        host.Layout();
        var summary = ById<Border>(view, "OrderbookOperatorSummary");
        ScrollTo(view, summary); host.Layout();
        Assert.Contains(Descendants<StatusBadge>(summary), b => b.Label == "Continuous" && b.Tone == "Good");
        Assert.Contains("Age: 2.0s", Text(summary));
        Assert.Contains("0.2001", Text(summary));
        Assert.Equal("Fresh", book.DisplayFreshness);
        host.Capture(theme, "book-continuous");
        book.Response = book.Response with { Realtime = book.Response.Realtime! with { Continuity = "BestEffort" } };
        host.Layout();
        Assert.Contains(Descendants<StatusBadge>(summary), b => b.Label == "BestEffort" && b.Tone == "Warning");
        book.Response = response;
        host.Layout();
        Assert.Equal("REST Snapshot", book.SourceMode);
        Assert.DoesNotContain(Descendants<StatusBadge>(summary), b => b.Label == "Realtime");
        clock.Now = At.AddSeconds(10);
        book.Response = response with { Reason = "Fixture retained snapshot" };
        host.Layout();
        Assert.Equal("Stale", book.DisplayFreshness);
        Assert.Contains("retained levels are cached", Text(summary));
        Assert.Equal(2, book.Bids.Count());
        host.Capture(theme, "book-stale");
        book.Response = response with { Snapshot = null, State = "Unavailable", Freshness = "Unavailable", Reason = "NoSnapshot" };
        host.Layout();
        Assert.Contains("Orderbook unavailable", Text(summary));
        host.Capture(theme, "book-unavailable");
        AssertCommands(view);
        host.Minimum(theme, "catalog");
        data.Catalog.ExchangeStatuses.Clear(); host.Layout();
        Assert.Contains(Descendants<TextBlock>(view), t => t.IsVisible && t.Text.StartsWith("Catalog status unavailable", StringComparison.Ordinal));
        Assert.Equal(0, data.Handler.Requests);
        Assert.Empty(errors.Messages);
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Matching_split_blockers_trust_and_identifiers_preserve_review_commands(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new Ui02WpfTests.WorkflowFixture();
        using var errors = new BindingErrors();
        var vm = data.Relationships;
        Assert.False(vm.CrossExchange);
        vm.Job = new(Guid.Empty, "Running", 12, 30, 4, At, null, null);
        var view = new RelationshipsView { DataContext = vm };
        using var host = new PageHost(view);
        host.Layout();
        var review = (ScrollViewer)view.FindName("ReviewScroll");
        Assert.Equal(1, Grid.GetColumn(review));
        Assert.Equal(0, Grid.GetRow(review));
        var blockers = ById<Border>(view, "RelationshipBlockers");
        Assert.Contains("Predicate", Text(blockers));
        Assert.Contains("Comparisons: 30", Text(view));
        var copies = Descendants<CopyIdentifierButton>(view).ToArray();
        Assert.Equal(3, copies.Length);
        Assert.Equal(vm.Detail!.Summary.Id.ToString(), copies[0].Text);
        Assert.Equal(vm.Detail.Source.NativeId, copies[1].Text);
        Assert.Equal(vm.Detail.Target.NativeId, copies[2].Text);
        host.Capture(theme, "matching-blockers");
        vm.Detail = vm.Detail with { Summary = vm.Detail.Summary with { State = "VerifiedManual" }, Evidence = [] };
        host.Layout();
        Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "VerifiedManual" && b.Tone == "Warning");
        Assert.Contains("No blocking differences reported. Verification state remains authoritative.", Text(blockers));
        vm.Detail = vm.Detail with { Summary = vm.Detail.Summary with { State = "VerifiedDeterministic" }, Warnings = [] };
        host.Layout();
        Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "VerifiedDeterministic" && b.Tone == "Good");
        Assert.Contains("Automatic Paper requires deterministic verification.", Text(view));
        host.Capture(theme, "matching-verified");
        AssertCommands(view);
        host.Minimum(theme, "matching");
        Assert.Equal(0, Grid.GetColumn(review)); Assert.Equal(1, Grid.GetRow(review));
        vm.Detail = null; host.Layout();
        Assert.All(copies, c => Assert.False(c.IsEnabled));
        Assert.Contains("Verification is unavailable", Text(blockers));
        Assert.Empty(errors.Messages); Assert.Equal(0, data.Handler.Requests);
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Monitoring_controls_coverage_and_history_remain_distinct(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new Ui02WpfTests.WorkflowFixture();
        using var errors = new BindingErrors();
        var vm = data.Opportunities.Monitoring;
        vm.Status = vm.Status! with { State = "Running" };
        var view = new MonitoringView { DataContext = vm };
        using var host = new PageHost(view);
        host.Layout();
        Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "Running" && b.Tone == "Info");
        var save = Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Save monitoring profile"));
        Assert.Same(vm.SaveProfileCommand, save.Command);
        Assert.True(save.TranslatePoint(new Point(), view).Y < 400);
        Assert.Equal(new[] { "FeeAdjusted", "GrossOnly", "NearEdge", "Blocked" }, vm.Items.Select(x => x.Lane));
        Assert.Contains("HISTORICAL", Text(ById<Border>(view, "HistoricalAlerts")));
        Assert.Contains("250", Text(ById<WrapPanel>(view, "MonitoringSummary")));
        host.Capture(theme, "monitoring-running");
        vm.Status = vm.Status with { Coverage = vm.Status.Coverage with { RelationshipsMonitored = 0, PlansWithActionableBooks = 0, FeeAdjustedOpportunities = 0 } };
        host.Layout();
        var inputs = ById<TextBlock>(view, "MonitoringInputSummary");
        Assert.Contains("No relationships currently monitored.", inputs.Text);
        Assert.Contains("No actionable books.", inputs.Text);
        Assert.Contains("No fee-adjusted opportunities.", inputs.Text);
        Assert.Equal("Running", vm.Status.State);
        ScrollTo(view, inputs); host.Layout(); host.Capture(theme, "monitoring-no-actionable");
        AssertCommands(view);
        host.Minimum(theme, "monitoring");
        vm.Status = null; host.Layout();
        Assert.Equal("Monitoring coverage unavailable.", inputs.Text);
        Assert.Empty(errors.Messages); Assert.Equal(0, data.Handler.Requests);
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Reliability_remaining_evidence_uses_report_states_without_inventing_readiness(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var handler = new RejectHandler(); using var http = new HttpClient(handler);
        var client = new BackendClient(http, new MissingConnection());
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperReliabilityViewModel(state, client);
        using var errors = new BindingErrors();
        var campaign = Ui02BReliabilityWpfTests.Campaign();
        vm.Campaign = campaign; vm.Report = Ui02BReliabilityWpfTests.Report(campaign);
        var view = new PaperReliabilityView { DataContext = vm };
        using var host = new PageHost(view);
        host.Layout();
        Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "Collecting" && b.Tone == "Info");
        Assert.Contains("CriteriaMet unlocks no live execution capability.", Text(ById<Border>(view, "ReliabilityCampaignSummary")));
        Assert.Single(vm.RemainingEvidence);
        Assert.Equal("02:00:00", vm.RemainingEvidence[0].ObservedValue);
        Assert.Equal("04:00:00", vm.RemainingEvidence[0].RequiredValue);
        host.Capture(theme, "reliability-collecting");
        var remaining = ById<Border>(view, "ReliabilityRemainingEvidence");
        ScrollTo(view, remaining); host.Layout(); host.Capture(theme, "reliability-pending");
        vm.Report = vm.Report with { Invariants = [new("LedgerReconciliationHealthy", 0, 1, "Violated", "FixtureMismatch"), new("TelemetryComplete", 0, 0, "Unknown", "EvidenceGap")], EvidenceGapDetected = true };
        host.Layout();
        Assert.Equal(3, vm.RemainingEvidence.Count);
        Assert.Equal("Safety invariant", vm.RemainingEvidence[0].Kind);
        Assert.Contains(Descendants<StatusBadge>(remaining), b => b.Label == "Unknown" && b.Tone == "Warning");
        Assert.Contains(Descendants<StatusBadge>(remaining), b => b.Label == "Violated" && b.Tone == "Error");
        ScrollTo(view, ById<Border>(view, "ReliabilitySafetyInvariants")); host.Layout(); host.Capture(theme, "reliability-invariants");
        vm.Report = vm.Report with { Criteria = [], Invariants = [] };
        Assert.Empty(vm.RemainingEvidence);
        Assert.Contains("InsufficientEvidence", vm.RemainingEvidenceSummary);
        Assert.DoesNotContain("CriteriaMet", vm.RemainingEvidenceSummary);
        vm.Report = Ui02BReliabilityWpfTests.Report(campaign) with { State = "CriteriaMet", Criteria = [new("AutomationHealthyTicks", TimeSpan.FromHours(4).Ticks, TimeSpan.FromHours(4).Ticks, "Satisfied", "MinimumInclusive")] };
        host.Top();
        Assert.Equal("Good", ById<StatusBadge>(view, "ReliabilityEvidenceState").Tone);
        host.Capture(theme, "reliability-criteria-met");
        AssertCommands(view);
        host.Minimum(theme, "reliability");
        vm.Report = null; host.Layout();
        Assert.Contains("unknown", vm.RemainingEvidenceSummary);
        Assert.Empty(errors.Messages); Assert.Equal(0, handler.Requests);
    });

    [Fact]
    public Task Copy_helpers_copy_exact_identifiers_and_recover_without_touching_the_system_clipboard() => fixture.RunAsync(() =>
    {
        var copy = new CapturingCopy { IdentifierName = "source native ID", Text = "K-native/EXACT-01" };
        copy.InvokeClick(); Assert.Equal(copy.Text, copy.Copied); Assert.Equal("Copied", copy.Content);
        copy.Text = "replacement-id"; Assert.Equal("Copy", copy.Content);
        copy.Fail = true; copy.InvokeClick(); Assert.Equal("Retry copy", copy.Content);
        copy.Fail = false; copy.InvokeClick(); Assert.Equal("replacement-id", copy.Copied);
        copy.Text = null; Assert.False(copy.IsEnabled);
    });

    private sealed class CapturingCopy : CopyIdentifierButton
    {
        public string? Copied { get; private set; }
        public bool Fail { get; set; }
        protected override void WriteClipboard(string text) { if (Fail) throw new ExternalException(); Copied = text; }
        public void InvokeClick() => OnClick();
    }
    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants<TextBlock>(root).Select(t => t.Text));

    // Logical layout is independent of the automation desktop's physical screen size.
    private sealed class PageHost : IDisposable
    {
        private readonly UserControl view;
        private readonly Window window;
        private double width = 1260, height = 900;
        public PageHost(UserControl view)
        {
            this.view = view;
            var canvas = new Canvas(); canvas.Children.Add(view);
            window = new Window { Content = canvas, Width = 300, Height = 200, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            window.Show();
        }
        public void Layout()
        {
            view.Width = width; view.Height = height;
            for (var pass = 0; pass < 2; pass++)
            {
                view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }
        public void Top() { Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).ScrollToTop(); Layout(); }
        public void Minimum(EffectiveTheme theme, string name)
        {
            // 830x590 shell leaves at least this conservative page viewport after navigation/header.
            width = 510; height = 340; Top();
            foreach (var scroll in Descendants<ScrollViewer>(view).Where(s => s.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled))
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Clipped {name}");
            foreach (var button in Descendants<Button>(view).Where(b => BindingOperations.GetBindingExpression(b, Button.CommandProperty) is not null || b is CopyIdentifierButton))
            {
                var point = button.TranslatePoint(new Point(), view);
                Assert.True(point.X >= -1 && point.X + button.ActualWidth <= width + 1, $"Clipped action {button.Content} in {name}");
                Assert.True(button.Focusable);
            }
            Capture(theme, name + "-minimum");
        }
        public void Capture(EffectiveTheme theme, string name)
        {
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, $"UI04A-{theme}-{name}.png")); encoder.Save(output);
        }
        public void Dispose() => Close(window);
    }
}
