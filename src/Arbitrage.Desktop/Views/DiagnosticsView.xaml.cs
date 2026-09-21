using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;

namespace Arbitrage.Desktop.Views;

public partial class DiagnosticsView : UserControl
{
    private DesktopDiagnostics? diagnostics;
    public DiagnosticsView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel model) return;
        diagnostics = model.Diagnostics;
        diagnostics.BackendChanged += OnBackendChanged;
        BackendNoticeText.Text = diagnostics.BackendNotice;
        RefreshFilter();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (diagnostics is not null) diagnostics.BackendChanged -= OnBackendChanged;
        diagnostics = null;
    }
    private void OnBackendChanged(object? sender, EventArgs e) => BackendNoticeText.Text = diagnostics?.BackendNotice ?? "";
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void RefreshFilter()
    {
        if (BackendList is null || SeverityFilter is null || SourceFilter is null) return;
        var severity = (SeverityFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var source = (SourceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var view = CollectionViewSource.GetDefaultView(BackendList.ItemsSource);
        if (view is null) return;
        view.Filter = item => item is BackendDiagnosticEvent entry &&
            (severity == "All" || entry.Severity == severity) && (source == "All" || entry.Source == source);
        view.Refresh();
    }
}
