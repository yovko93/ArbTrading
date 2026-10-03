using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

public sealed class StatusToneTests
{
    [Theory]
    [InlineData("Running", "Good")]
    [InlineData("Connected", "Good")]
    [InlineData("Healthy", "Good")]
    [InlineData("Available", "Good")]
    [InlineData("NotRunning", "Neutral")]
    [InlineData("Disconnected", "Neutral")]
    [InlineData("Unavailable", "Neutral")]
    [InlineData("Inactive", "Neutral")]
    [InlineData("Disabled", "Neutral")]
    [InlineData("Unknown", "Neutral")]
    [InlineData("FutureUnrecognizedState", "Neutral")]
    [InlineData("StopRequested", "Warning")]
    [InlineData("Synchronizing", "Warning")]
    [InlineData("Stale", "Warning")]
    [InlineData("Partial", "Warning")]
    [InlineData("Local", "Info")]
    [InlineData("Paper", "Info")]
    [InlineData("Faulted", "Error")]
    [InlineData("AuthorizationDenied", "Error")]
    [InlineData("Invalid", "Error")]
    [InlineData("Corrupted", "Error")]
    [InlineData("KillSwitchLatched", "Error")]
    public void Unknown_and_unavailable_never_inherit_positive_status_colors(string status, string expected) =>
        Assert.Equal(expected, StatusToneConverter.Tone(status));

    [Theory]
    [InlineData("Running", "ManagedLocal", "Verified application-managed backend is running.", "Connected", "Good")]
    [InlineData("Running", "ManagedLocal", "Managed backend passed authenticated readiness; synchronization is in progress.", "Connected", "Warning")]
    [InlineData("Running", "ExternalUnmanaged", "Backend is reachable, but local management ownership is unverified.", "Connected", "Warning")]
    [InlineData("NotRunning", "NotRunning", "Managed backend process exit was confirmed.", "Unavailable", "Neutral")]
    [InlineData("Unknown", "ExternalUnmanaged", "Stop failed unexpectedly. Refresh to verify backend state.", "Connected", "Error")]
    [InlineData("Faulted", "Unknown", "Could not start backend.", "Unavailable", "Error")]
    public void Lifecycle_banner_prioritizes_failures_and_incomplete_readiness(string state, string management, string message, string connection, string expected) =>
        Assert.Equal(expected, new StatusToneConverter().Convert([state, management, message, connection], typeof(string), "", CultureInfo.InvariantCulture));

    [Fact]
    public void Missing_venue_bucket_is_unavailable_and_currencies_are_never_combined()
    {
        var converter = new PaperBalanceConverter();
        var usd = new PaperBalanceResponse("Kalshi", "USD", 100, 95.832m, 0, 95.832m, 1, DateTimeOffset.UtcNow);
        var usdc = new PaperBalanceResponse("Polymarket", "USDC", 200, 180, 0, 180, 1, DateTimeOffset.UtcNow);
        Assert.Same(usd, converter.Convert(new[] { usd, usdc }, typeof(object), "Kalshi/USD", CultureInfo.InvariantCulture));
        Assert.Same(usdc, converter.Convert(new[] { usd, usdc }, typeof(object), "Polymarket/USDC", CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert(new[] { usd }, typeof(object), "Polymarket/USDC", CultureInfo.InvariantCulture));
    }
}

[Collection("WPF")]
public sealed class SemanticPresentationWpfTests(WpfFixture fixture)
{
    [Fact]
    public Task Uninitialized_generation_and_missing_currency_buckets_render_as_unavailable() => fixture.RunAsync(() =>
    {
        using var http = new HttpClient();
        var backend = new BackendClient(http, new AbsentConnection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var paper = new PaperTradingViewModel(state, backend);
        var view = new PaperTradingView { DataContext = paper };
        var window = new Window { Content = view, Width = 560, Height = 790, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show();
            foreach (var theme in new[] { EffectiveTheme.Light, EffectiveTheme.Dark })
            {
                new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
                paper.Account = new("Uninitialized", null, [], []); window.UpdateLayout();
                var text = Descendants(view).OfType<TextBlock>().Select(block => block.Text).ToArray();
                Assert.Contains("Uninitialized — no paper funds exist.", text);
                Assert.Contains("No active generation", text);
                Assert.Contains("Kalshi / USD", text);
                Assert.Contains("Polymarket / USDC", text);
                Assert.True(text.Count(value => value == "Unavailable") >= 3);
                var at = DateTimeOffset.UtcNow;
                paper.Account = new("Active", new(Guid.NewGuid(), at, null, "fixture", "Corrupted"), [new("Kalshi", "USD", 100, 95.832m, 0, 95.832m, 1, at)], []);
                window.UpdateLayout();
                Assert.Contains(Descendants(view).OfType<StatusBadge>(), badge => badge.Label == "Corrupted" && badge.Tone == "Error");
                Assert.Contains(Descendants(view).OfType<TextBlock>(), block => block.Text == "95.832");
                Assert.Contains(Descendants(view).OfType<TextBlock>(), block => block.Text == "Unavailable");
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Existing_controls_follow_palette_changes_with_readable_semantics_and_disabled_actions() => fixture.RunAsync(() =>
    {
        var panel = new StackPanel();
        var badge = new StatusBadge { Label = "Connected", Tone = "Good" }; panel.Children.Add(badge);
        var styles = new[] { "SuccessButton", "DangerButton", "PrimaryButton", "SecondaryButton" };
        var buttons = styles.Select(key => new Button { Content = key, Style = (Style)Application.Current.FindResource(key) }).ToArray();
        foreach (var button in buttons) panel.Children.Add(button);
        var window = new Window { Content = panel, Width = 500, Height = 300, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var palette = new WpfThemePaletteApplier(Application.Current.Resources);
        try
        {
            window.Show();
            Color? previous = null;
            foreach (var theme in new[] { EffectiveTheme.Light, EffectiveTheme.Dark })
            {
                palette.Apply(theme, false); window.UpdateLayout();
                var border = (Border)badge.FindName("BadgeBorder");
                var text = (TextBlock)badge.FindName("BadgeText");
                var current = ((SolidColorBrush)border.Background).Color;
                if (previous is { } old) Assert.NotEqual(old, current);
                previous = current;
                Assert.True(Contrast(text.Foreground, border.Background) >= 4.5);
                foreach (var tone in new[] { "Good", "Warning", "Error", "Info", "Neutral" })
                {
                    badge.Tone = tone; window.UpdateLayout();
                    Assert.True(Contrast(text.Foreground, border.Background) >= 4.5, $"{theme} {tone} text contrast");
                }
                badge.Tone = "Good";
                foreach (var button in buttons)
                {
                    button.IsEnabled = true; window.UpdateLayout();
                    Assert.True(Contrast(button.Foreground, button.Background) >= 4.5);
                    button.IsEnabled = false; window.UpdateLayout();
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("DisabledBackground")).Color, ((SolidColorBrush)button.Background).Color);
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("DisabledText")).Color, ((SolidColorBrush)button.Foreground).Color);
                }
            }
            palette.Apply(EffectiveTheme.Light, true); window.UpdateLayout();
            Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)((Border)badge.FindName("BadgeBorder")).Background).Color);
        }
        finally { window.Close(); palette.Apply(EffectiveTheme.Light, false); }
    });

    private static double Contrast(Brush foreground, Brush background)
    {
        static double Luminance(Brush brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var first = Luminance(foreground); var second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class AbsentConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("No runtime connection in visual fixture.");
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
