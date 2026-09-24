using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class ShellCapabilityWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task Shell_badges_and_sidebar_render_capabilities_without_static_execution_claims(string themeName) => fixture.RunAsync(() =>
    {
        new WpfThemePaletteApplier(Application.Current.Resources).Apply(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
        foreach (var scenario in new[] { "NoSnapshot", "PaperOnly", "Stale", "FutureLive" })
        {
            var backend = new BackendClient(new HttpClient(), new Connection());
            using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
            var diagnostics = new DesktopDiagnostics();
            using var theme = new ThemeService(new Preferences(), new SystemTheme(), new WpfThemePaletteApplier(Application.Current.Resources), new Dispatcher());
            using var selection = new ThemeSelectionViewModel(theme, diagnostics);
            if (scenario != "NoSnapshot")
            {
                var capabilities = new Capabilities(true, true, scenario == "FutureLive", false, false);
                var workspace = Guid.NewGuid();
                state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-24T12:00:00Z"),
                    new(Guid.NewGuid(), workspace, "Local", capabilities),
                    new("fixture", 1, "Healthy", "Local", "Paper", "Paper", capabilities),
                    new(workspace, "Paper workspace"), []), "http://127.0.0.1:5274");
                if (scenario == "Stale") state.SetRealtimeStatus("Disconnected", "Fixture transport interrupted");
            }
            var shell = new ShellViewModel(state, selection, diagnostics);
            var window = new MainWindow(shell) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            try
            {
                window.Show();
                window.Measure(new Size(1240, 790)); window.Arrange(new Rect(0, 0, 1240, 790)); window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var badges = Descendants<StatusBadge>(window).ToArray();
                Assert.Contains(badges, b => b.Label == state.PaperStatusLabel && b.Tone == state.PaperStatusTone);
                Assert.Contains(badges, b => b.Label == state.LiveStatusLabel && b.Tone == state.LiveStatusTone);
                Assert.DoesNotContain(badges, b => b.Label == "Execution unavailable");
                Assert.Contains(Descendants<TextBlock>(window), t => t.Text == state.LocalModeSummary);
                if (scenario == "NoSnapshot") Assert.All(badges.Where(b => b.Label?.StartsWith("Paper:") == true || b.Label?.StartsWith("Live:") == true), b => Assert.Equal("Neutral", b.Tone));
                if (scenario == "PaperOnly") { Assert.Equal("Paper: Available", state.PaperStatusLabel); Assert.Equal("Live: Unavailable", state.LiveStatusLabel); }
                if (scenario == "Stale") Assert.Contains("stale", state.LocalModeSummary);
                if (scenario == "FutureLive") Assert.Equal("Live: Available", state.LiveStatusLabel);
                if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(1240, 790, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"shell-capability-{themeName}-{scenario}.png")); encoder.Save(stream);
                }
            }
            finally { window.Close(); }
        }
    });

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
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime connection in visual fixture.");
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Preferences : IDesktopPreferencesStore
    {
        public Task<PreferencesLoadResult> LoadAsync(CancellationToken ct) => Task.FromResult(new PreferencesLoadResult(ThemePreference.System));
        public Task SaveAsync(ThemePreference value, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class SystemTheme : ISystemThemeProvider
    {
        public bool? ApplicationsUseLightTheme => true;
        public bool HighContrast => false;
        public event EventHandler? Changed { add { } remove { } }
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
}
