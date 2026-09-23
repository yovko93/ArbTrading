using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Arbitrage.Desktop.ViewModels;

namespace Arbitrage.Desktop.Views;

public partial class MonitoringView : UserControl
{
    public MonitoringView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MonitoringViewModel)?.Activate();
        Unloaded += (_, _) => (DataContext as MonitoringViewModel)?.Deactivate();
        foreach (var (property, label) in new[] {
            ("RelationshipLimit", "Relationship limit (1–1000)"), ("MinimumGrossEdge", "Ranking gross edge"),
            ("MinimumFeeAdjustedEdge", "Ranking fee-adjusted edge"), ("MaximumQuantity", "Quantity cap"),
            ("MaximumSkewMilliseconds", "Maximum skew ms"), ("NearEdgeWindow", "Near-edge window"),
            ("FeeAlertEdge", "Alert fee-adjusted edge"), ("FeeAlertProfit", "Alert fee-adjusted profit"),
            ("GrossAlertEdge", "Alert gross edge"), ("GrossAlertProfit", "Alert gross profit"),
            ("RearmHysteresis", "Rearm hysteresis"), ("CooldownSeconds", "Cooldown seconds"),
            ("CsvIntervalSeconds", "CSV interval seconds"), ("CsvTopRows", "CSV top rows (1–100)"),
            ("AlertRetentionCount", "Retained alerts (1–5000)"), ("AlertRetentionDays", "Retention days (1–30)") })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 12, 4), Width = 180 };
            panel.Children.Add(new TextBlock { Text = label });
            var box = new TextBox(); AutomationProperties.SetName(box, label);
            box.SetBinding(TextBox.TextProperty, new Binding("Profile." + property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            panel.Children.Add(box); ProfileFields.Children.Add(panel);
        }
    }
}
