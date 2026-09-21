using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class WpfResourceTests(WpfFixture fixture)
{
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
