using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.Views;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class TradingHeaderWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Light)]
    [InlineData(EffectiveTheme.Dark)]
    public Task Configuration_headers_have_contrast_and_preserve_accessible_expansion(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new Ui02BPortfolioWpfTests.PortfolioFixture();
        using var errors = new BindingErrors();
        var view = new PaperTradingView { DataContext = data.Portfolio, Width = 900, Height = 760 };
        view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
        var canvas = new Canvas { Children = { view } };
        var window = new Window { Content = canvas, Width = 300, Height = 200, Left = -20000, Top = -20000, ShowInTaskbar = false };
        window.Show(); Flush(window);
        try
        {
            var expanders = Descendants<Expander>(view).ToArray();
            Assert.Equal(new[] { "Paper Generation", "Risk Policy Configuration", "Auto Paper Configuration & Session Controls", "Explicit Paper Entry" },
                expanders.Select(e => e.Header));
            Assert.All(expanders, expander =>
            {
                Assert.False(expander.IsExpanded);
                var header = Header(expander);
                var chrome = Part<Border>(header, "HeaderChrome");
                Assert.True(Contrast(header.Foreground, chrome.Background) >= 4.5);
                Assert.True(Contrast(chrome.BorderBrush, chrome.Background) >= 3);
                Assert.NotEqual(ColorOf(view.Background), ColorOf(chrome.Background));
                Assert.NotEqual(ColorOf(expander.Background), ColorOf(chrome.Background));
                Assert.Equal(FontWeights.SemiBold, header.FontWeight);
                var label = Assert.Single(Descendants<TextBlock>(header));
                Assert.Equal(14, label.FontSize);
                Assert.True(Contrast(label.Foreground, chrome.Background) >= 4.5);
                Assert.True(header.Focusable && header.IsTabStop);
                Assert.Equal(0, ((RotateTransform)Part<Path>(header, "ExpandArrow").RenderTransform).Angle);
            });
            ScrollTo(view, expanders[0]); Flush(window);
            Capture(view, theme, "TradingHeaders-collapsed");

            foreach (var expander in expanders)
            {
                var header = Header(expander);
                var chrome = Part<Border>(header, "HeaderChrome");
                var height = header.ActualHeight;
                var collapsed = ColorOf(chrome.Background);
                var peer = new ToggleButtonAutomationPeer(header);
                Assert.Equal(expander.Header, peer.GetName());
                var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
                toggle.Toggle(); Flush(window);
                Assert.True(expander.IsExpanded);
                Assert.True(header.IsChecked);
                Assert.NotEqual(collapsed, ColorOf(chrome.Background));
                Assert.True(Contrast(Assert.Single(Descendants<TextBlock>(header)).Foreground, chrome.Background) >= 4.5);
                Assert.True(Contrast(header.Foreground, chrome.Background) >= 4.5);
                Assert.True(Contrast(chrome.BorderBrush, chrome.Background) >= 3);
                Assert.Equal(height, header.ActualHeight);
                Assert.Equal(90, ((RotateTransform)Part<Path>(header, "ExpandArrow").RenderTransform).Angle);
                Assert.Equal(Visibility.Visible, ((ContentPresenter)expander.Template.FindName("ContentSite", expander)).Visibility);
                ScrollTo(view, expander); Flush(window);
                Capture(view, theme, "TradingHeaders-expanded-" + Array.IndexOf(expanders, expander));
                toggle.Toggle(); Flush(window); Assert.False(expander.IsExpanded);
            }

            var first = Header(expanders[0]);
            FocusManager.SetFocusedElement(window, first);
            Keyboard.Focus(first); Flush(window);
            Assert.True(first.IsKeyboardFocused);
            var focus = Part<Border>(first, "HeaderFocus");
            Assert.Equal(Visibility.Visible, focus.Visibility);
            Assert.True(Contrast(focus.BorderBrush, Part<Border>(first, "HeaderChrome").Background) >= 3);
            ScrollTo(view, expanders[0]); Flush(window);
            Capture(view, theme, "TradingHeaders-focus");
            var focusedHeight = first.ActualHeight;
            first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            Flush(window);
            Assert.True(first.IsPressed);
            Assert.Equal(focusedHeight, first.ActualHeight);
            Assert.True(Contrast(Assert.Single(Descendants<TextBlock>(first)).Foreground, Part<Border>(first, "HeaderChrome").Background) >= 4.5);
            Capture(view, theme, "TradingHeaders-pressed");
            // Cancel the synthetic press rather than depending on physical keyboard state.
            first.IsEnabled = false; Flush(window);
            Assert.False(first.IsPressed);
            Assert.Equal(Visibility.Collapsed, focus.Visibility);
            first.IsEnabled = true;

            // Check the palette pairs used by hover and pressed states, including Windows High Contrast.
            Assert.True(Contrast(Resource("SelectionText"), Resource("SelectionBackground")) >= 4.5);
            Assert.True(Contrast(Resource("FocusIndicator"), Resource("SelectionBackground")) >= 3);
            var palette = new WpfThemePaletteApplier(Application.Current.Resources);
            palette.Apply(theme, true); Flush(window);
            foreach (var expander in expanders)
            {
                var header = Header(expander);
                Assert.Equal(SystemColors.WindowTextColor, ColorOf(header.Foreground));
                expander.IsExpanded = true; Flush(window);
                Assert.Equal(SystemColors.WindowTextColor, ColorOf(header.Foreground));
                Assert.Equal(SystemColors.WindowColor, ColorOf(Part<Border>(header, "HeaderChrome").Background));
            }
            Assert.Equal(SystemColors.HighlightTextColor, ColorOf(Resource("SelectionText")));
            Assert.Equal(SystemColors.HighlightColor, ColorOf(Resource("SelectionBackground")));
            Assert.Empty(errors.Messages); Assert.Equal(0, data.Handler.Requests);
        }
        finally { ApplyTheme(theme); Close(window); view.DataContext = null; }
    });

    private static ToggleButton Header(Expander expander) => (ToggleButton)expander.Template.FindName("HeaderSite", expander);
    private static T Part<T>(ToggleButton header, string name) where T : FrameworkElement => (T)header.Template.FindName(name, header);
    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static double Contrast(Brush first, Brush second)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = Luminance(ColorOf(first)); var b = Luminance(ColorOf(second));
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
