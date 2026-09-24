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
public sealed class Phase04H3WpfTests(WpfFixture fixture)
{
    [Fact]
    public Task Dashboard_strategies_and_analytics_render_in_both_themes() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(), new Connection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "SQLite", "Paper", "Paper", Capabilities.Phase04A),
            new(workspace, "Paper workspace"), []), "http://127.0.0.1:5274");
        Assert.Equal("Available", state.PaperExecutionLabel);
        Assert.Equal("Unavailable", state.LiveExecutionLabel);

        var generation = Guid.NewGuid();
        using var analytics = new PaperAnalyticsViewModel(state, null);
        analytics.Account = new("Active", new(generation, DateTimeOffset.UtcNow, null, "Fixture", "Healthy"), [], []);
        analytics.Performance = new(generation, "Active", [new("Kalshi", "USD", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1), new("Polymarket", "USDC", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1)]);
        analytics.Valuation = new(generation, "Available", false, DateTimeOffset.UtcNow, [], [], 1, false, []);
        analytics.Buckets.Add(new(new("Kalshi", "USD", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1),
            new("Kalshi", "USD", 1000m, 700m, 200m, 20m, 1, 1, 0, 0, 210m, 205m, 210m, 900m, 910m, 905m, 30m, 25m, 1m, null, 200m, null, 1)));
        analytics.Buckets.Add(new(new("Polymarket", "USDC", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1),
            new("Polymarket", "USDC", 1000m, 700m, 200m, 20m, 1, 1, 0, 0, 210m, 205m, 210m, 900m, 910m, 905m, 30m, 25m, 1m, null, 200m, null, 1)));
        analytics.Notice = "Read-only paper summary fixture.";

        foreach (var themeName in new[] { "Light", "Dark" })
        {
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(themeName == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            Capture(new DashboardView { DataContext = new DashboardViewModel(state) }, "dashboard-" + themeName);
            Capture(new StrategiesView { DataContext = new StrategiesViewModel(state) }, "strategies-" + themeName);
            Capture(new PaperAnalyticsView { DataContext = analytics }, "analytics-populated-" + themeName);
            analytics.Valuation = new(generation, "Unavailable", false, DateTimeOffset.UtcNow, [], [], 1, false, []);
            analytics.Buckets.Clear();
            analytics.Buckets.Add(new(new("Kalshi", "USD", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1), null));
            analytics.Buckets.Add(new(new("Polymarket", "USDC", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1), null));
            Capture(new PaperAnalyticsView { DataContext = analytics }, "analytics-unavailable-" + themeName);
            analytics.Account = null; analytics.Performance = null;
            analytics.Valuation = new(null, "Unavailable", false, DateTimeOffset.UtcNow, [], [], 1, false, []);
            analytics.Buckets.Clear(); analytics.Notice = "No paper generation.";
            Capture(new PaperAnalyticsView { DataContext = analytics }, "analytics-empty-" + themeName);
            analytics.Account = new("Active", new(generation, DateTimeOffset.UtcNow, null, "Fixture", "Healthy"), [], []);
            analytics.Performance = new(generation, "Active", [new("Kalshi", "USD", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1), new("Polymarket", "USDC", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1)]);
            analytics.Valuation = new(generation, "Available", false, DateTimeOffset.UtcNow, [], [], 1, false, []);
            analytics.Buckets.Add(new(new("Kalshi", "USD", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1),
                new("Kalshi", "USD", 1000m, 700m, 200m, 20m, 1, 1, 0, 0, 210m, 205m, 210m, 900m, 910m, 905m, 30m, 25m, 1m, null, 200m, null, 1)));
            analytics.Buckets.Add(new(new("Polymarket", "USDC", 1000m, 700m, 200m, 100m, 120m, 20m, 1, 1, 1, 0, 1),
                new("Polymarket", "USDC", 1000m, 700m, 200m, 20m, 1, 1, 0, 0, 210m, 205m, 210m, 900m, 910m, 905m, 30m, 25m, 1m, null, 200m, null, 1)));
        }
    });

    private static void Capture(UserControl view, string name)
    {
        view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
        view.Measure(new Size(1180, 1200)); view.Arrange(new Rect(0, 0, 1180, 1200)); view.UpdateLayout();
        if (view is StrategiesView)
        {
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Paper only", StringComparison.Ordinal));
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("Opportunities", StringComparison.Ordinal));
            Assert.Empty(Descendants<Button>(view));
        }
        if (view is DashboardView)
        {
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("No market or execution feed is active", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("Market counts, balances, P&L, and opportunities", StringComparison.Ordinal));
        }
        if (view is PaperAnalyticsView)
        {
            Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Refresh summary"));
        }
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1180, 1200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "phase04h3-" + name + ".png")); encoder.Save(stream);
    }

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
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No backend request expected.");
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
}
