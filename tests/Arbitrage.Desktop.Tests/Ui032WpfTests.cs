using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Xunit.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui032WpfTests(WpfFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Trading_dashboard_uses_saved_data_and_preserves_editors_at_all_widths(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new Ui02BPortfolioWpfTests.PortfolioFixture();
        var paper = data.Portfolio;
        paper.RiskStatus = Ui01WpfTests.Risk(); paper.AutomationStatus = Ui01WpfTests.Automation();
        paper.RiskStatus = paper.RiskStatus with { Policy = paper.RiskStatus.Policy! with { Revision = Guid.Parse("00000000-0000-0000-0000-000000000061") } };
        paper.AutomationStatus = paper.AutomationStatus with { Profile = paper.AutomationStatus.Profile! with { Revision = Guid.Parse("00000000-0000-0000-0000-000000000062") } };
        paper.Notice = ""; paper.RiskNotice = ""; paper.AutomationNotice = "";
        using var errors = new BindingErrors();
        var view = new PaperTradingView { DataContext = paper };
        view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
        var layout = new LogicalLayoutHost(view) { Width = 1260, Height = 760 };
        try
        {
            Flush(layout);
            var grid = ById<Grid>(view, "TradingSummaryGrid");
            Assert.True(grid.ActualWidth > 900);
            Assert.Equal(3, grid.ColumnDefinitions.Count);
            var cards = new[] { "PaperAccountSummary", "RiskPolicySummary", "AutoPaperSummary" }.Select(id => ById<Border>(view, id)).ToArray();
            Assert.All(cards, c => Assert.Equal(0, Grid.GetRow(c)));
            Assert.Equal(new[] { 0, 1, 2 }, cards.Select(Grid.GetColumn));
            Assert.All(cards, c => Assert.True(c.IsVisible && c.ActualWidth > 250));
            Assert.Equal("920", ById<TextBlock>(view, "KalshiAvailable").Text);
            Assert.Equal("1900", ById<TextBlock>(view, "PolymarketAvailable").Text);
            Assert.Equal("USD", Assert.IsType<PaperBalanceResponse>(ById<StackPanel>(view, "KalshiBalanceSummary").DataContext).Currency);
            Assert.Equal("USDC", Assert.IsType<PaperBalanceResponse>(ById<StackPanel>(view, "PolymarketBalanceSummary").DataContext).Currency);
            AssertVenueMarks(view);
            foreach (var id in new[] { "KalshiAvailable", "PolymarketAvailable" })
                Assert.Equal("AvailableCash", BindingOperations.GetBindingExpression(ById<TextBlock>(view, id), TextBlock.TextProperty)!.ParentBinding.Path.Path);
            foreach (var venue in new[] { ("KalshiBalanceSummary", "Kalshi/USD"), ("PolymarketBalanceSummary", "Polymarket/USDC") })
            {
                var binding = BindingOperations.GetBindingExpression(ById<StackPanel>(view, venue.Item1), FrameworkElement.DataContextProperty)!.ParentBinding;
                Assert.Equal("Account.Balances", binding.Path.Path);
                Assert.Equal(venue.Item2, binding.ConverterParameter);
                Assert.IsType<PaperBalanceConverter>(binding.Converter);
            }
            Assert.Equal("No paper opportunity selected.", ById<TextBlock>(view, "CurrentOpportunityEmpty").Text);
            var equity = ById<Border>(view, "PaperEquitySnapshot");
            Assert.Contains(Descendants<TextBlock>(equity), t => t.Text == "Historical equity series is not available.");
            Assert.Empty(Descendants<System.Windows.Shapes.Polyline>(equity));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text is "Paper Trading" or "Paper capital" || t.Text.Contains("Combined Equity"));
            Assert.Equal("RiskStatus.Policy.Limits.MinimumCashReserveFraction", BindingOperations.GetBindingExpression(ById<TextBlock>(view, "SavedCashReserve"), TextBlock.TextProperty)!.ParentBinding.Path.Path);
            paper.RiskInputs[0].Text = "99"; paper.AutomationInputs[3].Text = "888"; paper.AutomationSizingMode = "LargestAdmissibleGridQuantity";
            Flush(layout);
            Assert.Equal("20%", string.Concat(ById<TextBlock>(view, "SavedCashReserve").Text.Where(c => !char.IsWhiteSpace(c))));
            Assert.Equal("7", ById<TextBlock>(view, "SavedAutomationCap").Text);
            Assert.Equal("Fixed quantity", ById<TextBlock>(view, "SavedSizingMode").Text);
            Capture(layout, theme, "connected-wide");

            layout.Width = 780; Flush(layout);
            Assert.InRange(grid.ActualWidth, 660, 899);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(1, Grid.GetRow(cards[2])); Assert.Equal(2, Grid.GetColumnSpan(cards[2]));
            var savedAutomation = paper.AutomationStatus!;
            paper.AutomationStatus = savedAutomation with { Profile = savedAutomation.Profile! with
            {
                Settings = savedAutomation.Profile.Settings with { SizingMode = "LargestAdmissibleGridQuantity", MinimumQuantity = 1, MaximumQuantity = 5, QuantityStep = .5m }
            } };
            paper.OpportunityKey = new string('A', 64); Flush(layout);
            Assert.Equal("Adaptive grid", ById<TextBlock>(view, "SavedSizingMode").Text);
            Assert.False(ById<TextBlock>(view, "SavedFixedQuantity").IsVisible);
            Assert.Contains(Descendants<TextBlock>(cards[2]), t => t.IsVisible && t.Text == "Maximum quantity");
            var opportunity = ById<Border>(view, "CurrentPaperOpportunity");
            Assert.Contains(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text.Contains(paper.OpportunityKey));
            Assert.Contains(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text == paper.PreviewText);
            var savedAccount = paper.Account;
            paper.Account = null; Flush(layout);
            Assert.DoesNotContain(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text.Contains(paper.OpportunityKey));
            Assert.Equal("Current paper opportunity unavailable.", ById<TextBlock>(view, "CurrentOpportunityEmpty").Text);
            paper.Account = savedAccount;
            paper.AutomationStatus = savedAutomation; paper.OpportunityKey = "";
            layout.Width = 1260; Flush(layout);

            foreach (var expander in Descendants<Expander>(view)) Assert.False(expander.IsExpanded);
            ExpandEditors(view); Flush(layout); AssertCommands(view);
            foreach (var command in new ICommand[] { paper.InitializeCommand, paper.RefreshCommand, paper.SaveRiskPolicyCommand,
                paper.SaveAutomationCommand, paper.ArmAutomationCommand, paper.DisarmAutomationCommand, paper.EmergencyStopAutomationCommand,
                paper.ResetAutomationKillCommand, paper.PreviewCommand, paper.ExecuteCommand, paper.PreviewSizingCommand })
                Assert.Contains(Descendants<Button>(view), b => ReferenceEquals(b.Command, command));
            Assert.False(Descendants<Button>(view).Single(b => ReferenceEquals(b.Command, paper.ExecuteCommand)).IsEnabled);
            foreach (var expander in Descendants<Expander>(view)) expander.IsExpanded = false;

            layout.Width = 560; layout.Height = 340; Flush(layout);
            Assert.True(grid.ActualWidth < 660);
            Assert.Single(grid.ColumnDefinitions.Cast<ColumnDefinition>());
            var scroll = (ScrollViewer)view.Content;
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Capture(layout, theme, "connected-minimum");
            paper.AutomationStatus = paper.AutomationStatus! with { State = "Armed", SessionId = Guid.Parse("00000000-0000-0000-0000-000000000063") };
            paper.Busy = true; Flush(layout);
            var safety = ById<Border>(view, "ArmedSessionControls");
            Assert.True(safety.IsVisible);
            foreach (var button in Descendants<Button>(safety))
            {
                Assert.True(button.IsVisible && button.IsEnabled);
                var point = button.TranslatePoint(new Point(), scroll);
                Assert.True(point.Y >= 0 && point.Y + button.ActualHeight <= scroll.ActualHeight);
                Assert.True(point.X >= 0 && point.X + button.ActualWidth <= scroll.ActualWidth);
            }
            Capture(layout, theme, "armed-minimum");
            paper.Busy = false;
            ExpandEditors(view); Flush(layout);
            foreach (var button in Descendants<Button>(view).Where(b => b.IsVisible && b.ActualWidth > 0))
            {
                var point = button.TranslatePoint(new Point(), view);
                Assert.True(point.X >= -1 && point.X + button.ActualWidth <= view.ActualWidth + 1, $"Clipped {button.Content}");
            }
            scroll.ScrollToBottom(); Flush(layout); Capture(layout, theme, "editors-minimum-bottom");
            foreach (var expander in Descendants<Expander>(view)) expander.IsExpanded = false;
            paper.Account = null; paper.RiskStatus = null; paper.AutomationStatus = null; paper.OpportunityKey = "";
            paper.Notice = "Workspace access changed; private paper data cleared.";
            layout.Width = 1260; layout.Height = 760; scroll.ScrollToTop(); Flush(layout);
            Assert.Equal(3, grid.ColumnDefinitions.Count);
            Assert.All(cards, c => Assert.True(c.IsVisible));
            Assert.All(cards, c => Assert.Contains(Descendants<StatusBadge>(c), b => b.Label == "Unavailable"));
            Assert.Equal("Unavailable", ById<TextBlock>(view, "KalshiAvailable").Text);
            Assert.Equal("Unavailable", ById<TextBlock>(view, "PolymarketAvailable").Text);
            Assert.Equal("Unavailable", ById<TextBlock>(view, "SavedCashReserve").Text);
            Assert.False(safety.IsVisible);
            AssertVenueMarks(view);
            Capture(layout, theme, "unavailable-wide");
            Assert.Equal(0, data.Handler.Requests); Assert.Empty(errors.Messages);
        }
        finally { Close(layout.Window); view.DataContext = null; }
    });

    internal static void ExpandEditors(DependencyObject view)
    {
        foreach (var expander in Descendants<Expander>(view).ToArray()) expander.IsExpanded = true;
        if (view is FrameworkElement element) element.UpdateLayout();
    }

    private static void AssertVenueMarks(PaperTradingView view)
    {
        var kalshi = Assert.IsType<DrawingImage>(view.FindResource("KalshiMark"));
        var polymarket = Assert.IsType<DrawingImage>(view.FindResource("PolymarketMark"));
        Assert.NotSame(kalshi, polymarket);
        Assert.NotSame(kalshi.Drawing, polymarket.Drawing);
        var account = ById<Border>(view, "PaperAccountSummary");
        foreach (var venue in new[] { ("Kalshi", kalshi, "Kalshi / USD"), ("Polymarket", polymarket, "Polymarket / USDC") })
        {
            var image = Assert.Single(Descendants<Image>(account), i => AutomationProperties.GetName(i) == venue.Item1);
            Assert.Same(venue.Item2, image.Source);
            Assert.True(image.IsVisible);
            Assert.Equal(28, image.ActualWidth); Assert.Equal(28, image.ActualHeight);
            Assert.Equal(8, image.Margin.Right);
            Assert.Contains(Descendants<TextBlock>(account), t => t.IsVisible && t.Text == venue.Item3);
        }
    }

    private static void Capture(LogicalLayoutHost layout, EffectiveTheme theme, string name)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
        Directory.CreateDirectory(directory); var content = layout.View;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"UI032-{theme}-{name}.png")); encoder.Save(file);
    }

    // Canvas arranges the page at its explicit size, independent of the HWND's work-area limits.
    // A small real window preserves visibility/materialization checks; its size is never asserted.
    private sealed class LogicalLayoutHost
    {
        public PaperTradingView View { get; }
        public Window Window { get; }
        public double Width { get; set; }
        public double Height { get; set; }

        public LogicalLayoutHost(PaperTradingView view)
        {
            View = view;
            var canvas = new Canvas { ClipToBounds = false };
            canvas.Children.Add(view);
            Window = new Window { Content = canvas, Width = 300, Height = 200, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        }
    }

    private void Flush(LogicalLayoutHost layout)
    {
        var size = new Size(layout.Width, layout.Height);
        layout.View.Width = layout.Width;
        layout.View.Height = layout.Height;
        if (!layout.Window.IsVisible) layout.Window.Show();
        layout.View.Measure(size);
        layout.View.Arrange(new Rect(size));
        layout.View.UpdateLayout();
        layout.View.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        // SizeChanged reconfigures the real production Grid; complete that layout pass.
        layout.View.Measure(size);
        layout.View.Arrange(new Rect(size));
        layout.View.UpdateLayout();
        Assert.Equal(layout.Width, layout.View.ActualWidth);
        Assert.Equal(layout.Height, layout.View.ActualHeight);
        var grid = ById<Grid>(layout.View, "TradingSummaryGrid");
        output.WriteLine($"Logical={layout.Width}x{layout.Height}, View={layout.View.ActualWidth}, SummaryGrid={grid.ActualWidth}, Columns={grid.ColumnDefinitions.Count}");
    }
}
