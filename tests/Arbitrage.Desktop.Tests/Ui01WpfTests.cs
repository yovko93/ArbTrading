using System.IO;
using System.Net.Http;
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
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui01WpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Light)]
    [InlineData(EffectiveTheme.Dark)]
    public Task Trading_summaries_show_saved_snapshots_and_clear_when_unavailable(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
        using var http = new HttpClient();
        var backend = new BackendClient(http, new AbsentConnection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var paper = new PaperTradingViewModel(state, backend);
        var view = new PaperTradingView { DataContext = paper };
        var window = new Window { Content = view, Width = 1020, Height = 1400, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            paper.RiskStatus = Risk();
            paper.AutomationStatus = Automation();
            window.Show(); Flush(window);
            var risk = ById<Border>(view, "RiskPolicySummary");
            var automation = ById<Border>(view, "AutoPaperSummary");
            AssertSaved("20%", "13", "Fixed quantity", "7");
            Assert.Contains(Descendants<StatusBadge>(automation), badge => badge.Label == "Disarmed");
            Assert.Contains(Descendants<StatusBadge>(automation), badge => badge.Label == "Kill switch: clear");

            // Editing a proposed policy/profile must not make the saved summary claim it is active.
            paper.RiskInputs[0].Text = "99";
            paper.RiskInputs[6].Text = "777";
            paper.AutomationInputs[3].Text = "888";
            paper.AutomationSizingMode = "LargestAdmissibleGridQuantity";
            paper.AdaptiveMinimum = "3"; paper.AdaptiveMaximum = "30"; paper.AdaptiveStep = "3";
            Flush(window);
            AssertSaved("20%", "13", "Fixed quantity", "7");

            var savedRisk = paper.RiskStatus!;
            var savedAuto = paper.AutomationStatus!;
            var savedPolicy = Assert.IsType<PaperRiskPolicyResponse>(savedRisk.Policy);
            var savedProfile = Assert.IsType<PaperAutomationProfileResponse>(savedAuto.Profile);
            paper.RiskStatus = savedRisk with { Policy = savedPolicy with
            { Limits = savedPolicy.Limits with { MinimumCashReserveFraction = .3525m, MaximumOpenExecutions = 9 } } };
            var updatedAuto = savedAuto with { Profile = savedProfile with
            { Settings = savedProfile.Settings with { SizingMode = "LargestAdmissibleGridQuantity", MaximumExecutionsPerSession = 4,
                MinimumQuantity = 2, MaximumQuantity = 20, QuantityStep = 2 } } };
            paper.AutomationStatus = updatedAuto;
            Flush(window);
            AssertSaved("35.25%", "9", "Adaptive grid", "4");
            Assert.Equal("99", paper.RiskInputs[0].Text);
            Assert.Equal("888", paper.AutomationInputs[3].Text);

            paper.AutomationStatus = updatedAuto with { State = "Faulted", KillSwitch = savedAuto.KillSwitch with
            { IsLatched = true, Reason = "Fixture stop", LatchedAt = DateTimeOffset.Parse("2026-10-04T09:00:00Z") } };
            Flush(window);
            Assert.Contains(Descendants<StatusBadge>(automation), badge => badge.Label == "Kill switch: latched" && badge.Tone == "Error");
            Assert.Contains(Descendants<StatusBadge>(automation), badge => badge.Label == "Faulted" && badge.Tone == "Error");

            paper.RiskStatus = savedRisk with { State = "NotConfigured", Policy = null };
            paper.AutomationStatus = savedAuto with { State = "NotConfigured", Profile = null };
            Flush(window);
            AssertSaved("Unavailable", "Unavailable", "Unavailable", "Unavailable");

            paper.RiskStatus = null;
            paper.AutomationStatus = null;
            Flush(window);
            AssertSaved("Unavailable", "Unavailable", "Unavailable", "Unavailable");
            Assert.Contains(Descendants<StatusBadge>(automation), badge => badge.Label == "Kill switch: unknown" && badge.Tone != "Good");

            void AssertSaved(string reserve, string executions, string sizing, string cap)
            {
                // WPF may insert a culture-specific space before the percent symbol.
                var reserveText = ById<TextBlock>(risk, "SavedCashReserve").Text;
                Assert.Equal(reserve, string.Concat(reserveText.Where(character => !char.IsWhiteSpace(character))));
                Assert.Equal(executions, ById<TextBlock>(risk, "SavedExecutionLimit").Text);
                Assert.Equal(sizing, ById<TextBlock>(automation, "SavedSizingMode").Text);
                Assert.Equal(cap, ById<TextBlock>(automation, "SavedAutomationCap").Text);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(EffectiveTheme.Light)]
    [InlineData(EffectiveTheme.Dark)]
    public Task Sidebar_selection_preserves_all_destinations_and_two_way_navigation(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
        using var harness = new ShellHarness();
        var window = harness.Window;
        try
        {
            window.Show(); Flush(window);
            var navigation = Assert.Single(Descendants<ListBox>(window), list => AutomationProperties.GetName(list) == "Application navigation");
            Assert.Equal(11, navigation.Items.Count);
            Assert.Equal(Enum.GetValues<PageDestination>().Order(), harness.Shell.Navigation.Select(item => item.Destination).Order());
            var binding = BindingOperations.GetBindingExpression(navigation, ListBox.SelectedItemProperty);
            Assert.NotNull(binding);
            Assert.Equal(BindingStatus.Active, binding.Status);
            Assert.Equal(BindingMode.TwoWay, binding.ParentBinding.Mode);
            var pageHost = Assert.Single(Descendants<ContentControl>(window), control =>
                BindingOperations.GetBindingExpression(control, ContentControl.ContentProperty)?.ParentBinding.Path?.Path == "CurrentPage");
            foreach (var item in harness.Shell.Navigation)
            {
                navigation.ScrollIntoView(item); Flush(window);
                var container = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromItem(item));
                container.IsSelected = true; Flush(window);
                Assert.Same(item, harness.Shell.SelectedItem);
                Assert.Equal(item.Label, harness.Shell.PageTitle);
                Assert.NotNull(harness.Shell.CurrentPage);
                Assert.Same(harness.Shell.CurrentPage, pageHost.Content);
            }
            harness.Shell.SelectedItem = harness.Shell.Navigation[0]; Flush(window);
            Assert.Same(harness.Shell.Navigation[0], navigation.SelectedItem);
            Assert.Equal("Dashboard", harness.Shell.PageTitle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Window_icon_and_sidebar_mark_resolve_from_packaged_resources() => fixture.RunAsync(() =>
    {
        var uri = new Uri("/Arbitrage.Desktop;component/Assets/ArbitrageTrading.ico", UriKind.Relative);
        var resource = Application.GetResourceStream(uri);
        Assert.NotNull(resource);
        using (resource.Stream)
        {
            var decoder = new IconBitmapDecoder(resource.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(frame => frame.PixelWidth).Order());
            Assert.All(decoder.Frames, frame => Assert.Equal(frame.PixelWidth, frame.PixelHeight));
        }
        var mark = Assert.IsType<DrawingImage>(Application.Current.FindResource("AppMark"));
        Assert.True(mark.Width > 0 && mark.Height > 0);
        using var harness = new ShellHarness();
        try
        {
            harness.Window.Show(); Flush(harness.Window);
            Assert.NotNull(harness.Window.Icon);
            Assert.True(harness.Window.Icon.Width > 0 && harness.Window.Icon.Height > 0);
            Assert.Contains(Descendants<Image>(harness.Window), image => ReferenceEquals(image.Source, mark));
        }
        finally { harness.Window.Close(); }
    });

    private static PaperRiskStatusResponse Risk()
    {
        var at = DateTimeOffset.Parse("2026-10-04T08:00:00Z");
        var revision = Guid.NewGuid();
        return new("WithinLimits", new(1, revision, new(.2m, .1m, .6m, .2m, .2m, 20, 13, 2, 1000, .001m, 0),
            at, at, Guid.NewGuid(), new string('A', 64)),
            new("Approved", 1, revision, new string('A', 64), Guid.NewGuid(), 1, [], [], [], 1, 2, 1, 2, 0, 1, [], at));
    }

    private static PaperAutomationStatusResponse Automation()
    {
        var at = DateTimeOffset.Parse("2026-10-04T08:00:00Z");
        return new("Disarmed", "OwnerDisarmed", new(1, Guid.NewGuid(), new("FixedQuantity", 1, .005m, .05m, 7, 10, 1, 5, 60, .25m, 20, true, false),
            at, at, Guid.NewGuid(), new string('A', 64)), Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), false, null, null, "", null, null), null, null, null, "Running", 1, 4, 2, 1, at, at, [], 0, new Dictionary<string, long>());
    }

    private static T ById<T>(DependencyObject root, string id) where T : DependencyObject =>
        Assert.Single(Descendants<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class ShellHarness : IDisposable
    {
        private readonly HttpClient http = new();
        private readonly MainViewModel state;
        private readonly ThemeService theme;
        private readonly ThemeSelectionViewModel selection;
        public ShellViewModel Shell { get; }
        public MainWindow Window { get; }
        public ShellHarness()
        {
            state = new(new BackendClient(http, new AbsentConnection()), NullLogger<MainViewModel>.Instance);
            var diagnostics = new DesktopDiagnostics();
            theme = new(new Preferences(), new SystemTheme(), new WpfThemePaletteApplier(Application.Current.Resources), new ImmediateDispatcher());
            selection = new(theme, diagnostics);
            Shell = new(state, selection, diagnostics);
            Window = new(Shell) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        }
        public void Dispose() { Window.Close(); Shell.Dispose(); selection.Dispose(); theme.Dispose(); state.Dispose(); http.Dispose(); }
    }

    private sealed class AbsentConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException("No backend connection in UI fixture.");
        public Task WriteAsync(LocalConnection value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Preferences : IDesktopPreferencesStore
    {
        public Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new PreferencesLoadResult(ThemePreference.System));
        public Task SaveAsync(ThemePreference preference, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class SystemTheme : ISystemThemeProvider
    {
        public bool? ApplicationsUseLightTheme => true;
        public bool HighContrast => false;
        public event EventHandler? Changed { add { } remove { } }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
}
