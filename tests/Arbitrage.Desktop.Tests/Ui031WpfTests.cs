using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui031WpfTests(WpfFixture fixture)
{
    [Fact]
    public Task Canonical_A_and_three_bars_drive_both_brand_and_icon() => fixture.RunAsync(() =>
    {
        var mark = Assert.IsType<DrawingImage>(Application.Current.FindResource("AppMark"));
        var parts = Assert.IsType<DrawingGroup>(mark.Drawing).Children;
        Assert.Equal(5, parts.Count); // Blue A, green right leg, short/medium/tall bars.
        var shapes = parts.Cast<GeometryDrawing>().ToArray();
        Assert.True(shapes[0].Geometry.Bounds.Height > shapes[0].Geometry.Bounds.Width);
        Assert.True(shapes[1].Geometry.Bounds.Height > shapes[1].Geometry.Bounds.Width);
        var bars = shapes.Skip(2).Select(s => s.Geometry.Bounds).ToArray();
        Assert.True(bars[0].Height < bars[1].Height && bars[1].Height < bars[2].Height);
        Assert.True(bars[0].X < bars[1].X && bars[1].X < bars[2].X);
        Assert.All(bars, b => Assert.True(b.Height > b.Width));
        Assert.All(bars, b => Assert.Equal(bars[0].Bottom, b.Bottom));
        var icon = Assert.IsType<DrawingImage>(Application.Current.FindResource("AppIconArtwork"));
        var iconSymbol = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingGroup>(icon.Drawing).Children[1]);
        Assert.Same(parts, iconSymbol.Children);
        var resource = Application.GetResourceStream(new Uri("/Arbitrage.Desktop;component/Assets/ArbitrageTrading.ico", UriKind.Relative))!;
        using var stream = resource.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(f => f.PixelWidth).Order());
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Scrollbars_use_compact_templates_and_preserve_track_commands(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        foreach (var key in new[] { "ScrollTrack", "ScrollThumb", "ScrollThumbHover", "ScrollThumbPressed" })
            Assert.Equal((Color)Application.Current.FindResource("Color." + key), ((SolidColorBrush)Application.Current.FindResource(key)).Color);
        var vertical = new ScrollBar { Orientation = Orientation.Vertical, Maximum = 100, ViewportSize = 10, Value = 50, Height = 200 };
        var horizontal = new ScrollBar { Orientation = Orientation.Horizontal, Maximum = 100, ViewportSize = 10, Value = 50, Width = 200 };
        var view = new UserControl { Content = new StackPanel { Children = { vertical, horizontal } } };
        var window = Show(view, 400, 400);
        try
        {
            Assert.InRange(vertical.ActualWidth, 8, 10); Assert.InRange(horizontal.ActualHeight, 8, 10);
            foreach (var bar in new[] { vertical, horizontal })
            {
                var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
                Assert.Equal(bar.Orientation, track.Orientation); Assert.Equal(bar.Value, track.Value);
                Assert.Equal(bar.Orientation == Orientation.Vertical, track.IsDirectionReversed);
                Assert.NotNull(track.Thumb.Template);
                Assert.Equal(bar.Orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
                Assert.Equal(bar.Orientation == Orientation.Vertical ? ScrollBar.PageUpCommand : ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("ScrollTrack")).Color, ((SolidColorBrush)bar.Background).Color);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("ScrollThumb")).Color,
                    ((SolidColorBrush)((Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb)).Background).Color);
                bar.Value = 25; Flush(window); Assert.Equal(25, track.Value);
                var previous = bar.Value;
                Assert.IsType<RoutedCommand>(track.IncreaseRepeatButton.Command).Execute(null, bar); Flush(window); Assert.True(bar.Value > previous);
                previous = bar.Value;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(20, 20) { RoutedEvent = Thumb.DragDeltaEvent });
                Flush(window); Assert.True(bar.Value > previous);
            }
            var trackColor = ((SolidColorBrush)vertical.Background).Color;
            if (theme == EffectiveTheme.Dark) Assert.True(trackColor.R < 40 && trackColor.G < 40 && trackColor.B < 60);
            else Assert.True(trackColor.R > 200 && trackColor.G > 200 && trackColor.B > 200);
        }
        finally { Close(window); }
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Shell_branding_density_and_auto_scrolling_match_reference(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new Ui02BSettingsWpfTests.SettingsFixture(theme);
        using var shell = new ShellViewModel(data.State, data.Theme, data.Diagnostics, data.LocalBackend, credentials: data.Credentials, fees: data.Fees);
        using var errors = new BindingErrors();
        var window = new MainWindow(shell) { ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Flush(window);
            var nav = Descendants<ListBox>(window).Single(l => AutomationProperties.GetName(l) == "Application navigation");
            Assert.Equal(11, nav.Items.Count); Assert.Equal(Enum.GetValues<PageDestination>().Order(), shell.Navigation.Select(n => n.Destination).Order());
            var scroll = Descendants<ScrollViewer>(nav).Single();
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            var brand = Descendants<Image>(window).Single(i => AutomationProperties.GetName(i) == "Arbitrage Trading mark");
            Assert.InRange(brand.Width, 34, 38);
            var lockup = Assert.IsType<DockPanel>(brand.Parent);
            Assert.Equal(new[] { "ARBITRAGE", "TRADING" }, Descendants<TextBlock>(lockup).Select(t => t.Text));
            Assert.All(Descendants<TextBlock>(lockup), t => { Assert.Equal(TextWrapping.NoWrap, t.TextWrapping); Assert.Equal(FontWeights.SemiBold, t.FontWeight); });
            Assert.NotNull(window.Icon);
            Assert.Equal(new[] { "WORKSPACE", "PAPER OPERATIONS", "SYSTEM" }, Descendants<TextBlock>(nav).Where(t => t.Name == "GroupCaption" && t.Visibility == Visibility.Visible).Select(t => t.Text));
            foreach (var row in Descendants<ListBoxItem>(nav))
            {
                Assert.InRange(row.MinHeight, 34, 36); Assert.InRange(row.Padding.Top, 6, 7); Assert.InRange(row.Margin.Top, 0, 1);
                var point = row.TranslatePoint(new Point(), scroll);
                Assert.True(point.Y >= 0 && point.Y + row.ActualHeight <= scroll.ActualHeight + 1);
            }
            CaptureShell(window, theme, "Dashboard-normal");
            window.Width = 830; window.Height = 590; Flush(window);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            CaptureShell(window, theme, "Dashboard-minimum");
            scroll.PageDown(); Flush(window); Assert.True(scroll.VerticalOffset > 0);
            nav.ScrollIntoView(shell.Navigation.Last()); Flush(window); CaptureShell(window, theme, "Sidebar-minimum-bottom");
            nav.SelectedItem = shell.Navigation.Single(n => n.Destination == PageDestination.Settings); Flush(window);
            var page = Descendants<SettingsView>(window).Single();
            var contentScroll = Descendants<ScrollViewer>(page).First();
            Assert.Equal(Visibility.Visible, contentScroll.ComputedVerticalScrollBarVisibility);
            CaptureShell(window, theme, "Settings-minimum");
            contentScroll.ScrollToBottom(); Flush(window); Assert.True(contentScroll.VerticalOffset > 0);
            CaptureShell(window, theme, "Settings-bottom");
            Assert.Empty(errors.Messages); Assert.Equal(0, data.Handler.Requests);
        }
        finally { Close(window); }
    });

    private static void CaptureShell(Window window, EffectiveTheme theme, string name)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
        Directory.CreateDirectory(directory); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"UI031-{theme}-{name}.png")); encoder.Save(output);
    }
}
