using System.Windows;
using System.Windows.Controls;

namespace Arbitrage.Desktop.Views;
public partial class RelationshipsView : UserControl
{
    public RelationshipsView() => InitializeComponent();

    private void ReviewWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var split = e.NewSize.Width >= 1050;
        ReviewWorkspace.ColumnDefinitions[0].Width = new GridLength(split ? 2 : 1, GridUnitType.Star);
        ReviewWorkspace.ColumnDefinitions[1].Width = split ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ReviewScroll, split ? 1 : 0);
        Grid.SetRow(ReviewScroll, split ? 0 : 1);
        CandidatePanel.Margin = new Thickness(0, 0, split ? 12 : 0, 12);
    }

    private void MarketPairSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var split = e.NewSize.Width >= 620;
        MarketPair.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(TargetPanel, split ? 1 : 0);
        Grid.SetRow(TargetPanel, split ? 0 : 1);
        SourcePanel.Margin = new Thickness(0, 0, split ? 8 : 0, split ? 0 : 8);
    }
}
