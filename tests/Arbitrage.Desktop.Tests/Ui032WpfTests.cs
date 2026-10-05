using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using CommunityToolkit.Mvvm.Input;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui032WpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Trading_dashboard_uses_saved_data_and_preserves_editors_at_all_widths(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var settings = new Ui02BSettingsWpfTests.SettingsFixture(theme);
        using var data = new Ui02BPortfolioWpfTests.PortfolioFixture();
        var paper = data.Portfolio;
        paper.RiskStatus = Ui01WpfTests.Risk(); paper.AutomationStatus = Ui01WpfTests.Automation();
        paper.RiskStatus = paper.RiskStatus with { Policy = paper.RiskStatus.Policy! with { Revision = Guid.Parse("00000000-0000-0000-0000-000000000061") } };
        paper.AutomationStatus = paper.AutomationStatus with { Profile = paper.AutomationStatus.Profile! with { Revision = Guid.Parse("00000000-0000-0000-0000-000000000062") } };
        paper.Notice = ""; paper.RiskNotice = ""; paper.AutomationNotice = "";
        using var shell = new ShellViewModel(settings.State, settings.Theme, settings.Diagnostics, new BackendPresentation());
        shell.SelectedItem = shell.Navigation.Single(n => n.Destination == PageDestination.Trading);
        // Only the page payload is replaced with deterministic data; no polling/services are activated.
        shell.CurrentPage = new PaperTradingPageViewModel(paper);
        using var errors = new BindingErrors();
        var window = new MainWindow(shell) { Width = 1680, Height = 1000, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Flush(window);
            var view = Descendants<PaperTradingView>(window).Single();
            var grid = ById<Grid>(view, "TradingSummaryGrid");
            Assert.Equal(3, grid.ColumnDefinitions.Count);
            var cards = new[] { "PaperAccountSummary", "RiskPolicySummary", "AutoPaperSummary" }.Select(id => ById<Border>(view, id)).ToArray();
            Assert.All(cards, c => Assert.Equal(0, Grid.GetRow(c)));
            Assert.Equal(new[] { 0, 1, 2 }, cards.Select(Grid.GetColumn));
            Assert.All(cards, c => Assert.True(c.IsVisible && c.ActualWidth > 250));
            Assert.Equal("920", ById<TextBlock>(view, "KalshiAvailable").Text);
            Assert.Equal("1900", ById<TextBlock>(view, "PolymarketAvailable").Text);
            Assert.Equal("USD", Assert.IsType<PaperBalanceResponse>(ById<StackPanel>(view, "KalshiBalanceSummary").DataContext).Currency);
            Assert.Equal("USDC", Assert.IsType<PaperBalanceResponse>(ById<StackPanel>(view, "PolymarketBalanceSummary").DataContext).Currency);
            Assert.Equal("No paper opportunity selected.", ById<TextBlock>(view, "CurrentOpportunityEmpty").Text);
            var equity = ById<Border>(view, "PaperEquitySnapshot");
            Assert.Contains(Descendants<TextBlock>(equity), t => t.Text == "Historical equity series is not available.");
            Assert.Empty(Descendants<System.Windows.Shapes.Polyline>(equity));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text is "Paper Trading" or "Paper capital" || t.Text.Contains("Combined Equity"));
            Assert.Equal("RiskStatus.Policy.Limits.MinimumCashReserveFraction", BindingOperations.GetBindingExpression(ById<TextBlock>(view, "SavedCashReserve"), TextBlock.TextProperty)!.ParentBinding.Path.Path);
            paper.RiskInputs[0].Text = "99"; paper.AutomationInputs[3].Text = "888"; paper.AutomationSizingMode = "LargestAdmissibleGridQuantity";
            Flush(window);
            Assert.Equal("20%", string.Concat(ById<TextBlock>(view, "SavedCashReserve").Text.Where(c => !char.IsWhiteSpace(c))));
            Assert.Equal("7", ById<TextBlock>(view, "SavedAutomationCap").Text);
            Assert.Equal("Fixed quantity", ById<TextBlock>(view, "SavedSizingMode").Text);
            Capture(window, theme, "connected-wide");

            window.Width = 1100; Flush(window);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(1, Grid.GetRow(cards[2])); Assert.Equal(2, Grid.GetColumnSpan(cards[2]));
            var savedAutomation = paper.AutomationStatus!;
            paper.AutomationStatus = savedAutomation with { Profile = savedAutomation.Profile! with
            {
                Settings = savedAutomation.Profile.Settings with { SizingMode = "LargestAdmissibleGridQuantity", MinimumQuantity = 1, MaximumQuantity = 5, QuantityStep = .5m }
            } };
            paper.OpportunityKey = new string('A', 64); Flush(window);
            Assert.Equal("Adaptive grid", ById<TextBlock>(view, "SavedSizingMode").Text);
            Assert.False(ById<TextBlock>(view, "SavedFixedQuantity").IsVisible);
            Assert.Contains(Descendants<TextBlock>(cards[2]), t => t.IsVisible && t.Text == "Maximum quantity");
            var opportunity = ById<Border>(view, "CurrentPaperOpportunity");
            Assert.Contains(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text.Contains(paper.OpportunityKey));
            Assert.Contains(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text == paper.PreviewText);
            var savedAccount = paper.Account;
            paper.Account = null; Flush(window);
            Assert.DoesNotContain(Descendants<TextBlock>(opportunity), t => t.IsVisible && t.Text.Contains(paper.OpportunityKey));
            Assert.Equal("Current paper opportunity unavailable.", ById<TextBlock>(view, "CurrentOpportunityEmpty").Text);
            paper.Account = savedAccount;
            paper.AutomationStatus = savedAutomation; paper.OpportunityKey = "";
            window.Width = 1680; Flush(window);

            foreach (var expander in Descendants<Expander>(view)) Assert.False(expander.IsExpanded);
            ExpandEditors(view); Flush(window); AssertCommands(view);
            foreach (var command in new ICommand[] { paper.InitializeCommand, paper.RefreshCommand, paper.SaveRiskPolicyCommand,
                paper.SaveAutomationCommand, paper.ArmAutomationCommand, paper.DisarmAutomationCommand, paper.EmergencyStopAutomationCommand,
                paper.ResetAutomationKillCommand, paper.PreviewCommand, paper.ExecuteCommand, paper.PreviewSizingCommand })
                Assert.Contains(Descendants<Button>(view), b => ReferenceEquals(b.Command, command));
            Assert.False(Descendants<Button>(view).Single(b => ReferenceEquals(b.Command, paper.ExecuteCommand)).IsEnabled);
            foreach (var expander in Descendants<Expander>(view)) expander.IsExpanded = false;

            window.Width = 830; window.Height = 590; Flush(window);
            Assert.Single(grid.ColumnDefinitions.Cast<ColumnDefinition>());
            var scroll = (ScrollViewer)view.Content;
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Capture(window, theme, "connected-minimum");
            paper.AutomationStatus = paper.AutomationStatus! with { State = "Armed", SessionId = Guid.Parse("00000000-0000-0000-0000-000000000063") };
            paper.Busy = true; Flush(window);
            var safety = ById<Border>(view, "ArmedSessionControls");
            Assert.True(safety.IsVisible);
            foreach (var button in Descendants<Button>(safety))
            {
                Assert.True(button.IsVisible && button.IsEnabled);
                var point = button.TranslatePoint(new Point(), scroll);
                Assert.True(point.Y >= 0 && point.Y + button.ActualHeight <= scroll.ActualHeight);
                Assert.True(point.X >= 0 && point.X + button.ActualWidth <= scroll.ActualWidth);
            }
            Capture(window, theme, "armed-minimum");
            paper.Busy = false;
            ExpandEditors(view); Flush(window);
            foreach (var button in Descendants<Button>(view).Where(b => b.IsVisible && b.ActualWidth > 0))
            {
                var point = button.TranslatePoint(new Point(), view);
                Assert.True(point.X >= -1 && point.X + button.ActualWidth <= view.ActualWidth + 1, $"Clipped {button.Content}");
            }
            scroll.ScrollToBottom(); Flush(window); Capture(window, theme, "editors-minimum-bottom");
            foreach (var expander in Descendants<Expander>(view)) expander.IsExpanded = false;
            paper.Account = null; paper.RiskStatus = null; paper.AutomationStatus = null; paper.OpportunityKey = "";
            paper.Notice = "Workspace access changed; private paper data cleared.";
            settings.State.SetRealtimeStatus("AuthorizationDenied", "Isolated fixture: clear the previous workspace snapshot.");
            settings.State.SetRealtimeStatus("Disconnected", "Isolated unavailable fixture. No real backend connection.");
            window.Width = 1680; window.Height = 1000; scroll.ScrollToTop(); Flush(window);
            Assert.Equal(3, grid.ColumnDefinitions.Count);
            Assert.All(cards, c => Assert.True(c.IsVisible));
            Assert.All(cards, c => Assert.Contains(Descendants<StatusBadge>(c), b => b.Label == "Unavailable"));
            Assert.Equal("Unavailable", ById<TextBlock>(view, "KalshiAvailable").Text);
            Assert.Equal("Unavailable", ById<TextBlock>(view, "PolymarketAvailable").Text);
            Assert.Equal("Unavailable", ById<TextBlock>(view, "SavedCashReserve").Text);
            Assert.False(safety.IsVisible);
            Capture(window, theme, "unavailable-wide");
            Assert.Equal(0, data.Handler.Requests); Assert.Equal(0, settings.Handler.Requests); Assert.Empty(errors.Messages);
        }
        finally { Close(window); }
    });

    internal static void ExpandEditors(DependencyObject view)
    {
        foreach (var expander in Descendants<Expander>(view).ToArray()) expander.IsExpanded = true;
        if (view is FrameworkElement element) element.UpdateLayout();
    }

    private static void Capture(Window window, EffectiveTheme theme, string name)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } directory) return;
        Directory.CreateDirectory(directory); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"UI032-{theme}-{name}.png")); encoder.Save(file);
    }

    private sealed class BackendPresentation
    {
        public string ProcessStatus => "Running"; public string ManagementStatus => "ManagedLocal";
        public string Explanation => ""; public string StartExplanation => "Isolated fixture only."; public string StopExplanation => "Isolated fixture only.";
        public bool CanStart => false; public bool CanStop => false; public bool CanRefresh => false;
        public ICommand StartCommand { get; } = new RelayCommand(() => throw new InvalidOperationException(), () => false);
        public ICommand StopCommand { get; } = new RelayCommand(() => throw new InvalidOperationException(), () => false);
    }
}
