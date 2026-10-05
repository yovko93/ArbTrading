using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui02WpfTests(WpfFixture fixture)
{
    // This contract was captured from 0960e8e before layout changes. It protects bindings,
    // explicit enablement, native-ID columns, and accessibility names independently of layout.
    [Fact]
    public void Original_interactive_bindings_and_accessibility_contract_are_preserved()
    {
        var repo = RealtimeProcessTests.RepositoryRoot();
        var contract = JsonSerializer.Deserialize<Dictionary<string, BindingContract[]>>(File.ReadAllText(
            Path.Combine(repo, "tests", "Arbitrage.Desktop.Tests", "Ui02BindingContract.json")))!;
        foreach (var (view, entries) in contract)
        {
            var document = XDocument.Load(Path.Combine(repo, "src", "Arbitrage.Desktop", "Views", view + ".xaml"));
            foreach (var entry in entries)
                Assert.True(document.Descendants().Any(e => e.Name.LocalName == entry.Type &&
                    entry.Attributes.All(a => (string?)e.Attribute(a.Key) == a.Value)), $"Lost {view} {entry.Type}: {JsonSerializer.Serialize(entry.Attributes)}");
        }
    }

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Workflow_fixtures_render_with_bound_controls_and_truthful_states(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
        using var fixtureData = new WorkflowFixture();
        var catalog = new MarketExplorerView { DataContext = fixtureData.Catalog };
        var matching = new RelationshipsView { DataContext = fixtureData.Relationships };
        var opportunities = new OpportunitiesView { DataContext = fixtureData.Opportunities };
        var monitoring = new MonitoringView { DataContext = fixtureData.Opportunities.Monitoring };
        using var errors = new BindingErrors();
        foreach (var (name, view) in new (string, UserControl)[] { ("catalog", catalog), ("matching", matching), ("evaluation", opportunities), ("monitoring", monitoring) })
        {
            var window = new Window { Content = view, Width = 1160, Height = 1040, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            try
            {
                window.Show(); Flush(window);
                AssertCommands(view);
                if (view == catalog)
                {
                    Assert.Equal(Visibility.Visible, ById<Border>(view, "MarketEmptyState").Visibility);
                    Assert.Equal(Visibility.Collapsed, ById<Border>(view, "MarketInspector").Visibility);
                    Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "Failed" && b.Tone == "Error");
                    Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("InternalFailure", StringComparison.Ordinal));
                    Capture(view, theme, "catalog");
                    fixtureData.SelectBook(); Flush(window);
                    Assert.Equal(Visibility.Collapsed, ById<Border>(view, "MarketEmptyState").Visibility);
                    Assert.Equal(Visibility.Visible, ById<Border>(view, "MarketInspector").Visibility);
                    var outcomes = Descendants<ComboBox>(view).Single(c => AutomationProperties.GetName(c) == "Orderbook outcome");
                    Assert.Contains(Descendants<TextBlock>(outcomes), t => t.Text == "Yes");
                    Assert.DoesNotContain(Descendants<TextBlock>(outcomes), t => t.Text.Contains("BookInstrumentResponse", StringComparison.Ordinal));
                    ById<Border>(view, "MarketInspector").BringIntoView(); Flush(window);
                    Capture(view, theme, "orderbook");
                    Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "0.2001");
                    fixtureData.Catalog.OrderBook.Response = fixtureData.Catalog.OrderBook.Response! with { State = "Stale", Freshness = "Stale", IsActionable = false };
                    Flush(window);
                    Assert.Contains(Descendants<StatusBadge>(view), b => b.Label == "Stale" && b.Tone == "Warning");
                    fixtureData.Catalog.Notice = "Error"; Flush(window);
                    var neutralNotice = Descendants<Border>(view).Single(b => b.Child is TextBlock { Text: "Error" });
                    Assert.Equal(Visibility.Visible, neutralNotice.Visibility);
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("SemanticBrush.NeutralBackground")).Color, ((SolidColorBrush)neutralNotice.Background).Color);
                    fixtureData.Catalog.Notice = ""; Flush(window); Assert.Equal(Visibility.Collapsed, neutralNotice.Visibility);
                    fixtureData.Catalog.Notice = "Fixture notice restored."; Flush(window); Assert.Equal(Visibility.Visible, neutralNotice.Visibility);
                }
                else if (view == matching)
                {
                    var blockers = ById<Border>(view, "RelationshipBlockers");
                    Assert.Equal("Warning", blockers.Tag);
                    Assert.Contains(Descendants<TextBlock>(blockers), t => t.Text.Contains("Predicate", StringComparison.Ordinal));
                    Assert.False(Descendants<Button>(view).Single(b => Equals(b.Content, "Verify Manually…")).IsEnabled);
                    var mapping = Descendants<ComboBox>(view).First(c => AutomationProperties.GetName(c) == "Mapped target native outcome");
                    mapping.SelectedIndex = 0; Flush(window);
                    Assert.Contains(Descendants<TextBlock>(mapping), t => t.Text == "p-yes");
                    Capture(view, theme, name);
                    ScrollTo(view, blockers); Flush(window); Capture(view, theme, "review");
                }
                else if (view == opportunities)
                {
                    var tabs = Assert.IsType<TabControl>(opportunities.Content);
                    Assert.Equal(1, tabs.SelectedIndex);
                    Assert.Equal(2, tabs.Items.Count);
                    Capture(view, theme, name);
                    ScrollTo(view, ById<Border>(view, "OpportunityInspector")); Flush(window); Capture(view, theme, "opportunity-detail");
                    Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Unknown");
                    Assert.False(Descendants<Button>(view).Single(b => Equals(b.Content, "Paper preview…")).IsEnabled);
                    Assert.Equal(Visibility.Collapsed, ById<Border>(view, "OpportunityBlockers").Visibility);
                    fixtureData.Opportunities.Selected = fixtureData.Opportunities.Selected! with { Blockers = ["Fixture blocker must stay prominent."] };
                    Flush(window);
                    Assert.Equal(Visibility.Visible, ById<Border>(view, "OpportunityBlockers").Visibility);
                    tabs.SelectedIndex = 0; Flush(window); AssertCommands(view);
                    tabs.SelectedIndex = 1; Flush(window);
                }
                else
                {
                    fixtureData.PopulateMonitoring(); Flush(window);
                    Assert.Contains(Descendants<TextBlock>(ById<WrapPanel>(view, "MonitoringSummary")), t => t.Text == "250");
                    Capture(view, theme, name);
                    var rankings = Descendants<DataGrid>(view).Single(g => AutomationProperties.GetName(g) == "Current monitoring rankings");
                    ScrollTo(view, rankings); Flush(window); Capture(view, theme, "rankings");
                    Assert.Contains(Descendants<StatusBadge>(rankings), b => b.Label == "NearEdge" && b.Tone == "Warning");
                    fixtureData.Opportunities.Monitoring.Notice = "This historical alert has no available current opportunity. Its triggered-time values are not current.";
                    Flush(window);
                    Assert.Contains(Descendants<TextBlock>(view), t => t.Text.StartsWith("This historical alert has no available current opportunity", StringComparison.Ordinal));
                    fixtureData.Opportunities.Monitoring.Status = null; Flush(window);
                    Assert.DoesNotContain(Descendants<TextBlock>(ById<WrapPanel>(view, "MonitoringSummary")), t => t.Text == "250");
                }
                // The shell minimum leaves about 510 DIPs after navigation/margins. Check narrow layout,
                // scrolling, focusable actions and dropdown contents without executing any command.
                window.Width = 520; window.Height = 430; Flush(window);
                foreach (var scroll in Descendants<ScrollViewer>(view).Where(s => s.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled))
                    Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Clipped page content in {name}");
                var selector = Descendants<ComboBox>(view).FirstOrDefault();
                if (selector is not null)
                {
                    selector.BringIntoView(); selector.IsDropDownOpen = true; Flush(window);
                    Assert.NotNull(selector.Template.FindName("PART_Popup", selector));
                    selector.IsDropDownOpen = false; Flush(window);
                }
                Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).ScrollToTop();
                Flush(window); Capture(view, theme, name + "-minimum");
            }
            finally
            {
                window.Close();
                // Drain WPF's deferred Unloaded handlers while fixture view models still exist.
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }
        Assert.Empty(errors.Messages);
        Assert.Equal(0, fixtureData.Handler.Requests);
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Semantic_styles_preserve_disabled_actions_and_selected_table_contrast(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
        var resources = new ResourceDictionary { Source = new Uri("/Arbitrage.Desktop;component/Resources/Styles/MarketWorkflow.xaml", UriKind.Relative) };
        foreach (var (label, tone) in new[] { ("Complete", "Good"), ("Running", "Good"), ("Partial", "Warning"), ("Failed", "Error"), ("Cancelled", "Neutral"), ("Never", "Neutral"), ("Unrecognized", "Neutral"), ("FeeAdjusted", "Good"), ("GrossOnly", "Info"), ("NearEdge", "Warning"), ("Blocked", "Error") })
        {
            var badge = new StatusBadge { Label = label, Style = (Style)resources["WorkflowState"] };
            Assert.Equal(tone, badge.Tone);
        }
        var panel = new StackPanel { Resources = resources };
        foreach (var action in new[] { "PrimaryButton", "SuccessButton", "DangerButton", "SecondaryButton" })
            panel.Children.Add(new Button { Content = action, IsEnabled = false, Style = (Style)Application.Current.FindResource(action) });
        var grid = new DataGrid { ItemsSource = new[] { new { Id = "Native-ID-with-a-long-identifier", Value = "Unknown" } }, AutoGenerateColumns = true, Height = 140, IsReadOnly = true };
        panel.Children.Add(grid);
        var window = new Window { Content = panel, Width = 600, Height = 400, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); grid.SelectedIndex = 0; Flush(window);
            foreach (var button in Descendants<Button>(panel).Where(b => !b.IsEnabled))
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("DisabledBackground")).Color, ((SolidColorBrush)button.Background).Color);
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("SelectionBackground")).Color, ((SolidColorBrush)row.Background).Color);
            Assert.NotEqual(((SolidColorBrush)row.Background).Color, ((SolidColorBrush)row.Foreground).Color);
        }
        finally { window.Close(); }
    });

    private sealed record BindingContract(string Type, Dictionary<string, string> Attributes);
    private static void ScrollTo(DependencyObject view, FrameworkElement target)
    {
        var scroll = Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y);
    }
    private static void AssertCommands(DependencyObject root)
    {
        foreach (var button in Descendants<Button>(root))
        {
            var binding = BindingOperations.GetBindingExpression(button, Button.CommandProperty);
            if (binding is null) continue;
            Assert.Equal(BindingStatus.Active, binding.Status);
            Assert.NotNull(button.Command);
            if (!button.Command.CanExecute(button.CommandParameter)) Assert.False(button.IsEnabled);
        }
    }
    private static T ById<T>(DependencyObject root, string id) where T : DependencyObject => Assert.Single(Descendants<T>(root), e => AutomationProperties.GetAutomationId(e) == id);
    private static void Flush(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement view, EffectiveTheme theme, string name)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } path) return;
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, $"UI02-{theme}-{name}.png")); encoder.Save(output);
    }
    internal sealed class BindingErrors : TraceListener, IDisposable
    {
        public List<string> Messages { get; } = [];
        public BindingErrors() => PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message?.Contains("Error:", StringComparison.Ordinal) == true) Messages.Add(message); }
        protected override void Dispose(bool disposing) { PresentationTraceSources.DataBindingSource.Listeners.Remove(this); base.Dispose(disposing); }
    }
    internal sealed class RejectHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("No runtime or network access in visual fixtures."); }
    }
    internal sealed class MissingConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException("Isolated visual fixture.");
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    internal sealed class WorkflowFixture : IDisposable
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        public RejectHandler Handler { get; } = new();
        private readonly HttpClient http;
        private readonly MainViewModel state;
        public MarketExplorerViewModel Catalog { get; }
        public RelationshipsViewModel Relationships { get; }
        public OpportunitiesViewModel Opportunities { get; }
        public WorkflowFixture()
        {
            http = new(Handler); var client = new BackendClient(http, new MissingConnection());
            state = new(client, NullLogger<MainViewModel>.Instance);
            Catalog = new(state, client) { Notice = "Deterministic visual fixture · cached public metadata", Total = 2 };
            Catalog.Markets.Add(Market("Kalshi", "K-FIXTURE-NATIVE-ID-2026-EXTENDED", "Will the certified measure exceed the published threshold?"));
            Catalog.Markets.Add(Market("Polymarket", "P-FIXTURE-NATIVE-ID-2026-EXTENDED", "Will the published measure meet the threshold by the deadline?"));
            Catalog.ExchangeStatuses.Add(new("Kalshi", "Public", 367848, At.AddHours(-2), At, new(Guid.Empty, "Kalshi", "Public", null, "Failed", At, At, 42, 8400, 3, "InternalFailure", null), "Available", "Public", "Available", "Unavailable"));
            Catalog.ExchangeStatuses.Add(new("Polymarket", "Public", 24160, At, At, new(Guid.Empty, "Polymarket", "Public", null, "Complete", At, At, 121, 24160, 0, null, null), "Available", "Public", "Available", "Unavailable"));
            Relationships = new(state, client) { Notice = "Fixture evidence only. Settlement equivalence requires review.", Total = 1 };
            var summary = new RelationshipSummaryResponse(Guid.Empty, "Kalshi", "K-FIXTURE", "Will Person A win the election?", "Polymarket", "P-FIXTURE", "Will Person A become the nominee?", "Candidate", "Stale", At, true);
            Relationships.Items.Add(summary);
            var source = new RelationshipMarketResponse("Kalshi", "K-FIXTURE", summary.SourceTitle, "Final certified results apply. Recount and cancellation conditions require review.", null, "Fixture authority", "event", "series", null, "Binary", null, At, null, At, null, [new("yes", "Yes"), new("no", "No")], "Predicate: win election; timezone: unknown");
            Relationships.Detail = new(summary, source, source with { Exchange = "Polymarket", Title = summary.TargetTitle, Rules = "Nominee selection is the settlement predicate; election results do not apply." }, "SOURCE-FIXTURE", "TARGET-FIXTURE", 1, [new("ContradictorySemanticField", "Predicate", "Predicate: win election versus become nominee.", true, true)], [], ["Source changed; revalidate before approval."], null, null, null, false);
            foreach (var outcome in source.Outcomes) Relationships.MappingEditors.Add(new(outcome, [new("p-yes", "Yes"), new("p-no", "No")]));
            Opportunities = new(state, client) { Notice = "Deterministic fixture · no evaluation request was made", Total = 1 };
            var seed = OpportunityWpfTests.Result("Detected");
            var result = seed with { RelationshipId = Guid.Empty, EvaluatedAt = At,
                Legs = seed.Legs.Select(leg => leg with { RetrievedAt = At, SourceTimestamp = At }).ToArray(),
                SourceTitle = "Fixture · certified measure above threshold", TargetTitle = "Fixture · complementary outcome", Fees = null };
            Opportunities.Items.Add(result); Opportunities.Selected = result;
            PopulateMonitoring();
        }
        public void SelectBook()
        {
            Catalog.SelectedMarket = Catalog.Markets[0]; Catalog.Detail = Catalog.Markets[0];
            var instrument = new BookInstrumentResponse("Kalshi", Catalog.Markets[0].NativeId, "yes", "Yes");
            Catalog.OrderBook.Instruments.Add(instrument); Catalog.OrderBook.SelectedInstrument = instrument;
            Catalog.OrderBook.Response = new([instrument], "yes", "Fresh", "Fresh", true, null, 0, 5,
                new(Guid.Empty, instrument, [new(.2001m, 1.25m, "NativeBid"), new(.19m, 25m, "NativeBid")], [new(.63m, 12.50m, "DerivedComplement"), new(.64m, 40m, "DerivedComplement")], [], At, At, instrument.NativeMarketId, null, "Valid", "FullReturnedDepth", []), null);
            Catalog.OrderBook.Notice = "Fixture snapshot for visual inspection. No live feed is connected.";
        }
        public void PopulateMonitoring()
        {
            var m = Opportunities.Monitoring;
            m.Status = new("Degraded", At, null, At, new(500, 250, 250, true, 250, 200, 180, 60, 20, 120, 30, 80), 12, false, null, 1, 1000, 999, 0, 1, 250, 250, 0, 1, 4, 0, 0, "Disabled", null, 0, 0);
            m.Items.Clear(); m.Alerts.Clear();
            foreach (var lane in new[] { "FeeAdjusted", "GrossOnly", "NearEdge", "Blocked" })
            {
                var result = Opportunities.Items[0] with { Status = lane == "Blocked" ? "BookStale" : "Detected" };
                if (lane == "FeeAdjusted") result = result with { Fees = new("FeeAdjustedDetected", "Estimated", "DirectMember", [], .25m, 23.6m, 1.4m, .056m, null, 0) };
                m.Items.Add(new(m.Items.Count + 1, lane, 1, result, .0049m, .005m, .0001m, 25, "Cooldown"));
            }
            m.Selected = m.Items[2]; m.Total = 4;
            m.Alerts.Add(new(Guid.Empty, At.AddMinutes(-5), "GrossOnly", "GROSS / FEES UNRESOLVED threshold crossing", m.Items[1])); m.AlertTotal = 1;
        }
        private static MarketResponse Market(string exchange, string id, string title) => new(exchange, "Public", id, "fixture-event", "fixture-series", "fixture-group", "Binary", title, null, "Economics", ["Economics"], "open", "Open", [new("Yes", "yes"), new("No", "no")], At, At, At.AddDays(2), null, null, At, "Deterministic visual fixture, not operational data.", "Certified source rules apply; review the full settlement terms.", null, At, [], false);
        public void Dispose() { Opportunities.Dispose(); Relationships.Dispose(); Catalog.Dispose(); state.Dispose(); http.Dispose(); }
    }
}
