using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public sealed class WpfResourceTests(WpfFixture fixture)
{
    [Fact]
    public Task Orderbook_panel_renders_decimal_levels_and_provenance_in_both_themes() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new RejectHandler()), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A), new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, "Personal"), []), "http://127.0.0.1:5274");
        using var explorer = new MarketExplorerViewModel(state, backend);
        var instrument = new BookInstrumentResponse("Kalshi", "FIXTURE-BINARY", "yes", "Yes");
        explorer.OrderBook.Instruments.Add(instrument); explorer.OrderBook.SelectedInstrument = instrument;
        explorer.OrderBook.Response = new([instrument], "yes", "Fresh", "Fresh", true, null, 0, 5,
            new(Guid.NewGuid(), instrument, [new(.2001m, 1.25m, "NativeBid")], [new(.63m, 12.50m, "DerivedComplement")], [],
                DateTimeOffset.UtcNow, null, "FIXTURE-BINARY", null, "Valid", "FullReturnedDepth", []), null);
        explorer.OrderBook.Notice = "Read-only fixture snapshot. No exchange request was made.";
        var view = new MarketExplorerView { DataContext = explorer };
        view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
        var window = new Window { Content = view, Width = 1180, Height = 850, ShowInTaskbar = false };
        try
        {
            foreach (var name in new[] { "Light", "Dark" })
            {
                new WpfThemePaletteApplier(Application.Current.Resources).Apply(name == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
                window.Measure(new Size(1180, 850)); window.Arrange(new Rect(0, 0, 1180, 850)); window.UpdateLayout();
                view.Measure(new Size(1180, 850)); view.Arrange(new Rect(0, 0, 1180, 850)); view.UpdateLayout();
                var text = Descendants<TextBlock>(view).Select(t => t.Text).ToArray();
                Assert.Contains(text, t => t.Contains("derived from opposite-side bids", StringComparison.Ordinal));
                Assert.Contains(text, t => t.Contains("0.2001", StringComparison.Ordinal));
                Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "Refresh Order Book"));
                var directory = Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY");
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(1180, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, "orderbook-" + name + ".png")); encoder.Save(stream);
                }
            }
        }
        finally { window.Close(); }
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var n = 0; n < VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var child = VisualTreeHelper.GetChild(root, n);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    [Fact]
    public Task Palettes_have_identical_semantic_keys_and_all_brushes_resolve() => fixture.RunAsync(() =>
    {
        var light = new ResourceDictionary { Source = PaletteUri("Light") };
        var dark = new ResourceDictionary { Source = PaletteUri("Dark") };
        Assert.Equal(light.Keys.Cast<object>().OrderBy(x => x.ToString()), dark.Keys.Cast<object>().OrderBy(x => x.ToString()));
        foreach (var key in new[] { "ApplicationBackground", "SurfaceBackground", "ElevatedSurfaceBackground",
            "PrimaryText", "SecondaryText", "DefaultBorder", "Accent", "FocusIndicator", "Success", "Warning", "Error" })
            Assert.IsType<SolidColorBrush>(Application.Current.TryFindResource(key));
    });

    [Fact]
    public Task Switching_resources_updates_an_actual_WPF_visual_without_accumulation() => fixture.RunAsync(() =>
    {
        var application = Application.Current;
        var palette = new WpfThemePaletteApplier(application.Resources);
        var initialCount = application.Resources.MergedDictionaries.Count;
        var border = new Border(); border.SetResourceReference(Border.BackgroundProperty, "ApplicationBackground");
        var window = new Window { Content = border, Width = 400, Height = 260, ShowInTaskbar = false };
        try
        {
            window.Measure(new Size(400, 260)); window.Arrange(new Rect(0, 0, 400, 260));
            palette.Apply(EffectiveTheme.Light, false);
            var light = Assert.IsType<SolidColorBrush>(border.Background).Color;
            palette.Apply(EffectiveTheme.Dark, false);
            var dark = Assert.IsType<SolidColorBrush>(border.Background).Color;
            Assert.NotEqual(light, dark);
            for (var i = 0; i < 20; i++) palette.Apply(i % 2 == 0 ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            Assert.Equal(initialCount, application.Resources.MergedDictionaries.Count);
            Assert.Equal(dark, Assert.IsType<SolidColorBrush>(border.Background).Color);
            palette.Apply(EffectiveTheme.Light, true);
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(border.Background).Color);
        }
        finally { palette.Apply(EffectiveTheme.Light, false); window.Close(); }
    });

    [Fact]
    public Task Navigation_views_and_representative_controls_load_on_STA() => fixture.RunAsync(() =>
    {
        using var state = new MainViewModel(new BackendClient(new HttpClient(new RejectHandler()), new Connection()), NullLogger<MainViewModel>.Instance);
        var diagnostics = new DesktopDiagnostics();
        using var theme = new ThemeService(new Preferences(), new SystemTheme(),
            new WpfThemePaletteApplier(Application.Current.Resources), new WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        theme.InitializeAsync(default).GetAwaiter().GetResult();
        using var selection = new ThemeSelectionViewModel(theme, diagnostics);
        var shell = new ShellViewModel(state, selection, diagnostics);
        var window = new MainWindow(shell);
        try
        {
            foreach (var item in shell.Navigation)
            {
                shell.SelectedItem = item;
                window.Measure(new Size(940, 650)); window.Arrange(new Rect(0, 0, 940, 650)); window.UpdateLayout();
                Assert.Equal(item.Label, shell.PageTitle);
            }
            var controls = new StackPanel();
            controls.Children.Add(new Button { Content = "Action" });
            controls.Children.Add(new TextBox { Text = "Fixture" });
            controls.Children.Add(new ComboBox { ItemsSource = new[] { "Dark", "Light", "System" } });
            controls.Children.Add(new CheckBox { Content = "Option" });
            controls.Children.Add(new DataGrid());
            controls.Children.Add(new ListView());
            controls.Children.Add(new ScrollBar());
            var controlWindow = new Window { Content = controls };
            try { controlWindow.Measure(new Size(500, 500)); controlWindow.Arrange(new Rect(0, 0, 500, 500)); controlWindow.UpdateLayout(); }
            finally { controlWindow.Close(); }
        }
        finally { window.Close(); }
    });

    private static Uri PaletteUri(string name) => new($"/Arbitrage.Desktop;component/Resources/Themes/Colors.{name}.xaml", UriKind.Relative);
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
