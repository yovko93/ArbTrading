using System.Windows;
using System.Windows.Controls;
namespace Arbitrage.Desktop.Views;
public partial class PaperTradingView : UserControl
{
    public PaperTradingView()
    {
        InitializeComponent();
        // Trading owns the collapsed dashboard composition; standalone editor views keep their defaults.
        var risk = (Expander)RiskEditor.Content;
        risk.Header = "Risk Policy Configuration";
        risk.IsExpanded = false;
        var automation = (Expander)AutomationEditor.Content;
        automation.Header = "Auto Paper Configuration & Session Controls";
        automation.IsExpanded = false;
    }

    private void OnDashboardSizeChanged(object sender, SizeChangedEventArgs e) =>
        ArrangeCards(SummaryGrid, e.NewSize.Width >= 900 ? 3 : e.NewSize.Width >= 660 ? 2 : 1);

    private void OnSecondarySizeChanged(object sender, SizeChangedEventArgs e) =>
        ArrangeCards(SecondaryGrid, e.NewSize.Width >= 660 ? 2 : 1);

    private static void ArrangeCards(Grid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count == columns) return;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(columns == 3 ? new[] { 35d, 32d, 33d }[column] : 1, GridUnitType.Star) });
        for (var row = 0; row < (grid.Children.Count + columns - 1) / columns; row++)
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < grid.Children.Count; index++)
        {
            var card = (FrameworkElement)grid.Children[index];
            var span = columns == 2 && grid.Children.Count == 3 && index == 2 ? 2 : 1;
            Grid.SetColumn(card, index % columns);
            Grid.SetRow(card, index / columns);
            Grid.SetColumnSpan(card, span);
            card.Margin = new Thickness(0, 0, index % columns + span == columns ? 0 : 12, 12);
        }
    }
}
