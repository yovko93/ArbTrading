using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arbitrage.Desktop.ViewModels;

public enum PageDestination
{
    Dashboard, Opportunities, MarketExplorer, MarketMatching, Strategies,
    Trading, Portfolio, Analytics, Diagnostics, Settings
}

public sealed record NavigationItem(PageDestination Destination, string Label, string Icon);
public sealed record DashboardViewModel(MainViewModel State, object? LocalBackend = null);
public sealed record SettingsViewModel(MainViewModel State, ThemeSelectionViewModel Theme, object? LocalBackend = null);
public sealed record DiagnosticsViewModel(MainViewModel State, DesktopDiagnostics Diagnostics);
public sealed record TradingViewModel(MainViewModel State);
public sealed record UnavailablePageViewModel(string Title, string Purpose, string Dependency)
{
    public string DataNotice => "No operational data is being generated for this module.";
}

public partial class ShellViewModel : ObservableObject
{
    private readonly Dictionary<PageDestination, object> pages;
    public MainViewModel State { get; }
    public ThemeSelectionViewModel Theme { get; }
    public object? LocalBackend { get; }
    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new(PageDestination.Dashboard, "Dashboard", "M 1,13 L 1,4 L 9,4 L 9,13 Z M 12,13 L 12,1 L 20,1 L 20,13 Z"),
        new(PageDestination.Opportunities, "Opportunities", "M 1,12 L 7,6 L 11,10 L 19,2 M 14,2 L 19,2 L 19,7"),
        new(PageDestination.MarketExplorer, "Market Explorer", "M 9,1 A 8,8 0 1 1 8.99,1 M 15,15 L 20,20"),
        new(PageDestination.MarketMatching, "Market Matching", "M 1,4 L 9,4 L 12,7 L 20,7 M 1,16 L 9,16 L 12,13 L 20,13"),
        new(PageDestination.Strategies, "Strategies", "M 2,3 L 18,3 L 18,17 L 2,17 Z M 6,7 L 14,7 M 6,11 L 13,11"),
        new(PageDestination.Trading, "Trading", "M 2,15 L 8,9 L 12,13 L 19,5 M 15,5 L 19,5 L 19,9"),
        new(PageDestination.Portfolio, "Portfolio", "M 2,5 L 18,5 L 18,17 L 2,17 Z M 6,5 L 6,2 L 14,2 L 14,5"),
        new(PageDestination.Analytics, "Analytics", "M 2,17 L 2,12 L 6,12 L 6,17 M 9,17 L 9,7 L 13,7 L 13,17 M 16,17 L 16,2 L 20,2 L 20,17"),
        new(PageDestination.Diagnostics, "Logs & Diagnostics", "M 2,2 L 18,2 L 18,18 L 2,18 Z M 5,6 L 15,6 M 5,10 L 15,10 M 5,14 L 11,14"),
        new(PageDestination.Settings, "Settings", "M 10,2 A 8,8 0 1 1 9.99,2 M 10,7 A 3,3 0 1 1 9.99,7")
    ];

    [ObservableProperty] private NavigationItem? selectedItem;
    [ObservableProperty] private object? currentPage;
    [ObservableProperty] private string pageTitle = "Dashboard";

    public ShellViewModel(MainViewModel state, ThemeSelectionViewModel theme, DesktopDiagnostics diagnostics,
        object? localBackend = null, MarketExplorerViewModel? marketExplorer = null)
    {
        State = state; Theme = theme; LocalBackend = localBackend;
        pages = new()
        {
            [PageDestination.Dashboard] = new DashboardViewModel(state, localBackend),
            [PageDestination.Settings] = new SettingsViewModel(state, theme, localBackend),
            [PageDestination.Diagnostics] = new DiagnosticsViewModel(state, diagnostics),
            [PageDestination.Trading] = new TradingViewModel(state),
            [PageDestination.Opportunities] = new UnavailablePageViewModel("Opportunities", "Discover and evaluate arbitrage opportunities across markets.", "Market ingestion and strategy evaluation are not implemented."),
            [PageDestination.MarketExplorer] = (object?)marketExplorer ?? new UnavailablePageViewModel("Market Explorer", "Browse locally cached public markets.", "Market Explorer is not available in this shell instance."),
            [PageDestination.MarketMatching] = new UnavailablePageViewModel("Market Matching", "Compare market rules and candidate equivalents.", "Matching and independent rule validation are not implemented."),
            [PageDestination.Strategies] = new UnavailablePageViewModel("Strategies", "Configure and review arbitrage strategies.", "Strategy evaluation and optimization are not implemented."),
            [PageDestination.Portfolio] = new UnavailablePageViewModel("Portfolio", "Review positions, balances, and execution history.", "Exchange accounts and portfolio ingestion are not implemented."),
            [PageDestination.Analytics] = new UnavailablePageViewModel("Analytics", "Analyze actual historical performance when it exists.", "Market recording and execution history are not implemented.")
        };
        SelectedItem = Navigation[0];
    }

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        if (value is null) return;
        PageTitle = value.Label;
        CurrentPage = pages[value.Destination];
        if (pages[PageDestination.MarketExplorer] is MarketExplorerViewModel explorer)
        {
            if (value.Destination == PageDestination.MarketExplorer) explorer.Activate();
            else explorer.Deactivate();
        }
    }
}
