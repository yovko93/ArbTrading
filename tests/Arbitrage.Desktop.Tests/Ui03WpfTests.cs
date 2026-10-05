using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui03WpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Every_destination_preserves_navigation_and_renders_at_shell_minimum(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var settings = new Ui02BSettingsWpfTests.SettingsFixture(theme);
        using var markets = new Ui02WpfTests.WorkflowFixture();
        using var paper = new Ui02BPortfolioWpfTests.PortfolioFixture();
        using var http = new HttpClient(new RejectHandler());
        using var reliabilityState = new MainViewModel(new(http, new MissingConnection()), NullLogger<MainViewModel>.Instance);
        using var reliability = new PaperReliabilityViewModel(reliabilityState, new(http, new MissingConnection()));
        var campaign = Ui02BReliabilityWpfTests.Campaign();
        reliability.Campaign = campaign; reliability.Campaigns.Add(campaign); reliability.Report = Ui02BReliabilityWpfTests.Report(campaign);
        settings.PopulateDiagnostics(); markets.SelectBook();
        paper.Portfolio.RiskStatus = Ui01WpfTests.Risk(); paper.Portfolio.AutomationStatus = Ui01WpfTests.Automation();
        paper.Portfolio.RiskNotice = ""; paper.Portfolio.AutomationNotice = "";
        var local = new BackendPresentation();
        using var shell = new ShellViewModel(settings.State, settings.Theme, settings.Diagnostics, local);
        using var errors = new BindingErrors();
        var window = new MainWindow(shell) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Flush(window);
            var navigation = Descendants<ListBox>(window).Single(l => AutomationProperties.GetName(l) == "Application navigation");
            var host = Descendants<ContentControl>(window).Single(c => BindingOperations.GetBindingExpression(c, ContentControl.ContentProperty)?.ParentBinding.Path?.Path == "CurrentPage");
            foreach (var item in shell.Navigation)
            {
                navigation.SelectedItem = item; Flush(window);
                // Cached fixtures replace only the page payload; shell navigation itself still runs unchanged.
                shell.CurrentPage = item.Destination switch
                {
                    PageDestination.Dashboard => new DashboardViewModel(settings.State, local),
                    PageDestination.MarketExplorer => markets.Catalog,
                    PageDestination.MarketMatching => markets.Relationships,
                    PageDestination.Opportunities => markets.Opportunities,
                    PageDestination.Strategies => new StrategiesViewModel(settings.State),
                    PageDestination.Trading => new PaperTradingPageViewModel(paper.Portfolio),
                    PageDestination.Portfolio => new PaperPortfolioViewModel(paper.Portfolio),
                    PageDestination.Analytics => paper.Analytics,
                    PageDestination.PaperReliability => reliability,
                    PageDestination.Diagnostics => new DiagnosticsViewModel(settings.State, settings.Diagnostics),
                    PageDestination.Settings => new SettingsViewModel(settings.State, settings.Theme, local, settings.Credentials, settings.Fees),
                    _ => throw new InvalidOperationException()
                };
                window.Width = 1240; window.Height = 790; Flush(window);
                Assert.Same(item, shell.SelectedItem); Assert.Equal(item.Label, shell.PageTitle);
                var page = Descendants<UserControl>(host).First();
                Assert.True(page.IsVisible); AssertCommands(page);
                var busy = item.Destination switch
                {
                    PageDestination.MarketExplorer => (Action<bool>)(v => markets.Catalog.Loading = v),
                    PageDestination.MarketMatching => v => markets.Relationships.Busy = v,
                    PageDestination.Opportunities => v => markets.Opportunities.Busy = v,
                    PageDestination.Trading => v => paper.Portfolio.Busy = v,
                    PageDestination.Analytics => v => paper.Analytics.Busy = v,
                    PageDestination.PaperReliability => v => reliability.Busy = v,
                    _ => null
                };
                if (busy is not null)
                {
                    busy(true); Flush(window);
                    var indicator = Descendants<TextBlock>(page).Single(t => t.Style == Application.Current.FindResource("BusyText"));
                    Assert.True(indicator.IsVisible); Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(indicator));
                    CaptureShell(window, theme, item.Destination + "-busy");
                    busy(false); Flush(window); Assert.Equal(Visibility.Collapsed, indicator.Visibility);
                }
                CaptureShell(window, theme, item.Destination + "-normal");
                if (item.Destination == PageDestination.Settings)
                {
                    var about = ById<Border>(page, "ProductInformation"); ScrollTo(page, about); Flush(window);
                    Assert.Equal(new ProductInformation().BuildIdentity, ById<TextBlock>(about, "ProductBuildIdentity").Text);
                    CaptureShell(window, theme, "Settings-about");
                }
                window.Width = window.MinWidth; window.Height = window.MinHeight; Flush(window);
                Assert.True(host.ActualHeight > 200, $"No usable page area: {item.Label} ({host.ActualHeight})");
                foreach (var scroll in Descendants<ScrollViewer>(page).Where(s => s.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled))
                    Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Clipped {item.Label}: {scroll.ExtentWidth}/{scroll.ViewportWidth}");
                foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible && b.ActualWidth > 0 && b.Content is string))
                {
                    var point = button.TranslatePoint(new Point(), (FrameworkElement)window.Content);
                    Assert.True(point.X >= -1 && point.X + button.ActualWidth <= ((FrameworkElement)window.Content).ActualWidth + 1, $"Clipped action {button.Content} in {item.Label}");
                }
                navigation.ScrollIntoView(item); Flush(window);
                Assert.True(((ListBoxItem)navigation.ItemContainerGenerator.ContainerFromItem(item)).IsVisible);
                var outer = Descendants<ScrollViewer>(page).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
                outer.ScrollToTop(); Flush(window); CaptureShell(window, theme, item.Destination + "-minimum");
                outer.ScrollToBottom(); Flush(window); CaptureShell(window, theme, item.Destination + "-minimum-bottom");
                if (item.Destination == PageDestination.Opportunities)
                {
                    var tabs = Descendants<TabControl>(page).Single(); var originalTab = tabs.SelectedIndex;
                    tabs.SelectedIndex = 0; Flush(window); CaptureShell(window, theme, "Monitoring-minimum");
                    window.Width = 1240; window.Height = 790; Flush(window); CaptureShell(window, theme, "Monitoring-normal");
                    tabs.SelectedIndex = originalTab;
                }
                foreach (var combo in Descendants<ComboBox>(page).Where(c => c.IsVisible))
                { combo.IsDropDownOpen = true; Flush(window); Assert.NotNull(combo.Template.FindName("PART_Popup", combo)); combo.IsDropDownOpen = false; }
            }
            window.Width = 1240; window.Height = 790; Flush(window);
            foreach (var scale in new[] { 1d, 1.25d, 1.5d }) CaptureShell(window, theme, $"Settings-scale-{scale:0.00}", scale);
            Assert.Equal("Arbitrage Trading", window.Title); Assert.NotNull(window.Icon);
            Assert.Equal(0, markets.Handler.Requests); Assert.Equal(0, paper.Handler.Requests); Assert.Equal(0, settings.Handler.Requests);
            Assert.Empty(errors.Messages);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Authoritative_risk_and_auto_reads_clear_obsolete_notices_but_preserve_form_feedback() => fixture.RunAsync(() =>
    {
        using var handler = new ReadFixture(); using var http = new HttpClient(handler);
        var backend = new BackendClient(http, new FixtureConnection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        ApplySnapshot(state);
        using var paper = new PaperTradingViewModel(state, backend);
        paper.Deactivate(); // Reproduces the old unavailable notice on navigating away and back.
        paper.RefreshRiskCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        paper.RefreshAutomationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal("WithinLimits", paper.RiskStatus?.State); Assert.Empty(paper.RiskNotice); Assert.Empty(paper.AutomationNotice);
        paper.RiskNotice = "Count limits must be whole numbers from 1 to 1000.";
        paper.AutomationNotice = "Profile was not saved. Review before another attempt.";
        paper.RefreshRiskCommand.ExecuteAsync(null).GetAwaiter().GetResult(); paper.RefreshAutomationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.StartsWith("Count limits", paper.RiskNotice); Assert.StartsWith("Profile was not saved", paper.AutomationNotice);
        handler.Fail = true;
        paper.RefreshRiskCommand.ExecuteAsync(null).GetAwaiter().GetResult(); paper.RefreshAutomationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Null(paper.RiskStatus); Assert.Null(paper.AutomationStatus); Assert.NotEmpty(paper.RiskNotice); Assert.NotEmpty(paper.AutomationNotice);
        handler.Fail = false;
        paper.RefreshRiskCommand.ExecuteAsync(null).GetAwaiter().GetResult(); paper.RefreshAutomationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Empty(paper.RiskNotice); Assert.Empty(paper.AutomationNotice);
        var view = new PaperTradingView { DataContext = paper }; var window = Show(view);
        try { Assert.Equal(Visibility.Collapsed, ById<Border>(view, "RiskNotice").Visibility); Assert.Equal(Visibility.Collapsed, ById<Border>(view, "AutomationNotice").Visibility); }
        finally { Close(window); }
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    });

    [Fact]
    public Task Read_only_workflow_notices_recover_after_authoritative_reads() => fixture.RunAsync(() =>
    {
        using var data = new Ui02WpfTests.WorkflowFixture();
        using var handler = new ReadFixture { Fail = true }; using var http = new HttpClient(handler);
        var backend = new BackendClient(http, new FixtureConnection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance); ApplySnapshot(state);
        using var relationships = new RelationshipsViewModel(state, backend);
        using var opportunities = new OpportunitiesViewModel(state, backend);
        using var reliability = new PaperReliabilityViewModel(state, backend);
        var monitor = opportunities.Monitoring;
        var job = new OpportunityJobResponse(Guid.NewGuid(), "Complete", 1, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        handler.Responses["/relationships/"] = new RelationshipPageResponse([], 0, 1, 30);
        handler.Responses[$"/opportunities/jobs/{job.Id}/results"] = new OpportunityPageResponse([], 0, 1, 20, job);
        handler.Responses["/monitoring/status"] = data.Opportunities.Monitoring.Status! with { State = "Running" };
        handler.Responses["/monitoring/rankings"] = new MonitoringRankingPage([], 0, 1, 20);
        handler.Responses["/monitoring/alerts"] = new MonitoringAlertPage([], 0, 1, 20);
        handler.Responses["/monitoring/profile"] = new MonitoringProfileResponse();
        handler.Responses["/paper/reliability/current"] = new PaperReliabilityCurrentResponse(null, null, 0);
        handler.Responses["/paper/reliability/campaigns"] = Array.Empty<PaperReliabilityCampaignResponse>();
        relationships.Activate(); opportunities.Activate(); monitor.Activate(); opportunities.Job = job;
        relationships.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        opportunities.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        monitor.RefreshAsync().GetAwaiter().GetResult(); reliability.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Contains("could not be completed", relationships.Notice); Assert.Contains("unavailable", opportunities.Notice);
        Assert.Contains("failed", monitor.Notice); Assert.NotEmpty(reliability.Notice);
        handler.Fail = false;
        relationships.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult(); opportunities.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        monitor.RefreshAsync().GetAwaiter().GetResult(); reliability.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Empty(relationships.Notice); Assert.Empty(opportunities.Notice); Assert.Empty(monitor.Notice); Assert.Empty(reliability.Notice);
        Assert.Equal("Running", monitor.Status?.State);
        handler.Responses["/paper/reliability/current"] = new PaperReliabilityCurrentResponse(null, null, 2);
        reliability.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Assert.Contains("Telemetry persistence failure", reliability.Notice);
        relationships.Notice = "Review was not saved. Review before another attempt.";
        relationships.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Assert.StartsWith("Review was not saved", relationships.Notice);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        opportunities.Deactivate(); relationships.Deactivate();
    });

    [Fact]
    public Task Product_metadata_and_all_icon_frames_are_real_assembly_resources() => fixture.RunAsync(() =>
    {
        var assembly = typeof(ProductInformation).Assembly;
        Assert.Equal("Arbitrage Trading", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal("Arbitrage Trading Desktop", assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
        Assert.True(string.IsNullOrEmpty(assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company));
        var info = new ProductInformation(); Assert.Equal(assembly.GetName().Version?.ToString(), info.ApplicationVersion);
        Assert.Equal(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, info.BuildIdentity);
        var resource = Application.GetResourceStream(new Uri("/Arbitrage.Desktop;component/Assets/ArbitrageTrading.ico", UriKind.Relative))!;
        using (resource.Stream)
        {
            var decoder = new IconBitmapDecoder(resource.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(f => f.PixelWidth).Order());
            foreach (var frame in decoder.Frames)
            {
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[rgba.PixelWidth * rgba.PixelHeight * 4]; rgba.CopyPixels(pixels, rgba.PixelWidth * 4, 0);
                Assert.Equal(0, pixels[3]); Assert.Contains(pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 0);
            }
        }
    });

    [Fact]
    public Task Shared_status_and_action_styles_follow_canonical_meaning() => fixture.RunAsync(() =>
    {
        var resources = new ResourceDictionary { Source = new Uri("/Arbitrage.Desktop;component/Resources/Styles/MarketWorkflow.xaml", UriKind.Relative) };
        foreach (var (label, expected) in new[] { ("Running", "Good"), ("Available", "Good"), ("Approved", "Good"), ("Unknown", "Warning"), ("Denied", "Error"), ("FutureState", "Neutral") })
        {
            var badge = new StatusBadge { Label = label, Style = (Style)resources["WorkflowState"] };
            Assert.Equal(expected, badge.Tone); Assert.Equal(expected, StatusToneConverter.Tone(label));
        }
        var auto = new PaperAutomationView();
        auto.Measure(new Size(520, 1400)); auto.Arrange(new Rect(0, 0, 520, 1400)); auto.UpdateLayout();
        var reset = Descendants<Button>(auto).Single(b => Equals(b.Content, "Reset kill switch…"));
        Assert.Same(Application.Current.FindResource("WarningButton"), reset.Style);
        var emergency = Descendants<Button>(auto).Single(b => Equals(b.Content, "EMERGENCY STOP"));
        Assert.Same(Application.Current.FindResource("DangerButton"), emergency.Style);
        var selector = Descendants<ComboBox>(auto).Single(); Assert.Same(Application.Current.FindResource("ThemedComboBox"), selector.Style);
    });

    private static void CaptureShell(Window window, EffectiveTheme theme, string name, double scale = 1)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
        Directory.CreateDirectory(directory); var view = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"UI03-{theme}-{name}.png")); encoder.Save(output);
    }
    private static void ApplySnapshot(MainViewModel state) => state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        new(Guid.NewGuid(), Guid.NewGuid(), "Local", Capabilities.Phase04A), new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(Guid.NewGuid(), "Fixture workspace"), []), "http://127.0.0.1:5274");
    private sealed class FixtureConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", "isolated-fixture-token"));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ReadFixture : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public Dictionary<string, object> Responses { get; } = new()
        {
            ["/admission-status"] = Ui01WpfTests.Risk(), ["/automation/status"] = Ui01WpfTests.Automation()
        };
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var result = Responses.Single(p => request.RequestUri!.AbsolutePath.EndsWith(p.Key, StringComparison.Ordinal)).Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }
    private sealed class BackendPresentation
    {
        public string ProcessStatus => "Running"; public string ManagementStatus => "ManagedLocal";
        public string Explanation => "Fixture backend is owned by this Desktop session. No real backend is running.";
        public string StartExplanation => "Fixture only."; public string StopExplanation => "Fixture only.";
        public bool CanStart => false; public bool CanStop => false; public bool CanRefresh => false;
        public ICommand StartCommand { get; } = new RelayCommand(() => throw new InvalidOperationException(), () => false);
        public ICommand StopCommand { get; } = new RelayCommand(() => throw new InvalidOperationException(), () => false);
        public string DistributionSummary => "Distribution: Development · Manifest: Missing. Signing evidence unavailable in this fixture.";
    }
}
