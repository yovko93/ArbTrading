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
public sealed class SettingsCapabilityWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task Settings_separates_paper_and_live_capabilities_in_both_themes(string themeName) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
        foreach (var scenario in new[] { "PaperOnly", "FutureAutomaticLive", "NoSnapshot", "Stale", "Denied" })
        {
            var backend = new BackendClient(new HttpClient(), new Connection());
            using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
            var diagnostics = new DesktopDiagnostics();
            using var selection = new ThemeSelectionViewModel(new StubTheme(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark), diagnostics);
            if (scenario != "NoSnapshot")
            {
                var capabilities = new Capabilities(true, true, false, false, scenario == "FutureAutomaticLive");
                var workspace = Guid.NewGuid();
                state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                    new(Guid.NewGuid(), workspace, "Local", capabilities),
                    new("fixture", 1, "Healthy", "Local", "Paper", "Paper", capabilities),
                    new(workspace, "Paper workspace"), []), "http://127.0.0.1:5274");
                if (scenario == "Stale") state.SetRealtimeStatus("Disconnected", "Fixture transport interruption");
                if (scenario == "Denied") state.SetRealtimeStatus("AuthorizationDenied", "Fixture denial");
            }

            var view = new SettingsView { DataContext = new SettingsViewModel(state, selection) };
            view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1050, 900)); view.Arrange(new Rect(0, 0, 1050, 900)); view.UpdateLayout();
            var grid = Descendants<Grid>(view).Single(g => g.RowDefinitions.Count == 4 && g.ColumnDefinitions.Count == 2
                && g.Children.OfType<TextBlock>().Any(t => t.Text == "Paper execution"));
            Assert.Equal(state.PaperExecutionLabel, Value(grid, 0));
            Assert.Equal(state.LiveExecutionLabel, Value(grid, 1));
            Assert.Equal(state.ManualExecutionLabel, Value(grid, 2));
            Assert.Equal(state.AutomaticExecutionLabel, Value(grid, 3));
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Automatic Paper is an explicitly armed Paper-mode simulation", StringComparison.Ordinal)
                && t.Text.Contains("separate from Automatic live execution", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("Manual and Automatic operation are unavailable", StringComparison.Ordinal));
            if (scenario == "PaperOnly")
            {
                Assert.Equal("Available", Value(grid, 0));
                Assert.Equal("Unavailable", Value(grid, 1));
                Assert.Equal("Unavailable", Value(grid, 2));
                Assert.Equal("Unavailable", Value(grid, 3));
            }
            if (scenario == "FutureAutomaticLive") Assert.Equal("Available", Value(grid, 3));
            if (scenario is "NoSnapshot" or "Denied") Assert.All(Enumerable.Range(0, 4), row => Assert.Equal("Unknown", Value(grid, row)));
            if (scenario == "Stale") Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Last-known snapshot · stale", StringComparison.Ordinal));

            if ((scenario is "PaperOnly" or "NoSnapshot") && Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory);
                ((ScrollViewer)view.Content).ScrollToEnd(); view.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1050, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, $"settings-capability-{themeName}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });

    private static string Value(Grid grid, int row) => grid.Children.OfType<TextBlock>()
        .Single(t => Grid.GetRow(t) == row && Grid.GetColumn(t) == 1).Text;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No backend read expected.");
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubTheme(EffectiveTheme effectiveTheme) : IThemeService
    {
        public ThemePreference Preference => effectiveTheme == EffectiveTheme.Light ? ThemePreference.Light : ThemePreference.Dark;
        public EffectiveTheme EffectiveTheme => effectiveTheme;
        public bool HighContrast => false;
        public bool IsSaved => true;
        public string? PersistenceNotice => null;
        public event EventHandler? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SetPreferenceAsync(ThemePreference preference, CancellationToken ct = default) => Task.CompletedTask;
    }
}
