using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class PrimaryButtonBindingWpfTests(WpfFixture fixture)
{
    [Fact]
    public Task Primary_views_resolve_actual_button_commands_without_executing_mutations() => fixture.RunAsync(() =>
    {
        using var http = new HttpClient();
        var client = new BackendClient(http, new AbsentConnection());
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        using var markets = new MarketExplorerViewModel(state, client);
        using var opportunities = new OpportunitiesViewModel(state, client);
        using var paper = new PaperTradingViewModel(state, client);
        using var relationships = new RelationshipsViewModel(state, client);
        using var credentials = new KalshiCredentialsViewModel(state, client);
        using var fees = new FeeProfileViewModel(state, client);
        using var analytics = new PaperAnalyticsViewModel(state, client);
        var views = new (UserControl View, object Context)[]
        {
            (new MarketExplorerView(), markets), (new OpportunitiesView(), opportunities),
            (new MonitoringView(), opportunities.Monitoring), (new PaperTradingView(), paper),
            (new PaperPortfolioView(), paper), (new PaperAutomationView(), paper),
            (new PaperRiskView(), paper), (new PaperSettlementView(), paper), (new PaperValuationView(), paper),
            (new PaperReliabilityView(), paper.Reliability), (new RelationshipsView(), relationships),
            (new PaperAnalyticsView(), analytics),
            (new SettingsView(), new { State = state, Credentials = credentials, Fees = fees })
        };
        var count = 0;
        foreach (var (view, context) in views)
        {
            view.DataContext = context;
            var window = new Window { Content = view, Width = 1240, Height = 790, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            try
            {
                window.Show(); window.UpdateLayout();
                foreach (var button in Descendants<Button>(window))
                {
                    var binding = BindingOperations.GetBindingExpression(button, Button.CommandProperty);
                    if (binding is null) continue; // Framework chrome (e.g. scroll buttons).
                    Assert.True(binding.Status == BindingStatus.Active, $"{view.GetType().Name}: {button.Content} binding is {binding.Status}");
                    Assert.True(button.Command is not null, $"{view.GetType().Name}: {button.Content} command unresolved");
                    _ = button.Command.CanExecute(button.CommandParameter);
                    count++;
                }
            }
            finally { window.Close(); }
        }
        Assert.True(count >= 85, $"Unexpectedly few primary button bindings: {count}");
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class AbsentConnection : Arbitrage.LocalTransport.ILocalConnectionFile
    {
        public Task<Arbitrage.LocalTransport.LocalConnection> ReadAsync(CancellationToken ct) => throw new System.IO.FileNotFoundException();
        public Task WriteAsync(Arbitrage.LocalTransport.LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
}
