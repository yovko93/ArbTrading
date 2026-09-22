using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class MarketExplorerViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private long queryGeneration;
    private long detailGeneration;
    private bool active;
    public string[] Exchanges { get; } = ["All", "Polymarket", "Kalshi"];
    public string[] Statuses { get; } = ["All", "Open", "Upcoming", "Paused", "OpenOrPaused", "UpcomingOrPaused", "Finalized", "Unknown"];
    public string[] Sorts { get; } = ["title", "closing", "retrieved"];
    public ObservableCollection<MarketResponse> Markets { get; } = [];
    public ObservableCollection<ExchangeCatalogStatusResponse> ExchangeStatuses { get; } = [];
    public ObservableCollection<string> Tags { get; } = ["All"];
    [ObservableProperty] private string selectedExchange = "All";
    [ObservableProperty] private string selectedStatus = "All";
    [ObservableProperty] private string selectedTag = "All";
    [ObservableProperty] private string selectedSort = "title";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private bool loading;
    [ObservableProperty] private string notice = "Open Market Explorer to browse the local catalog.";
    [ObservableProperty] private MarketResponse? selectedMarket;
    [ObservableProperty] private MarketResponse? detail;
    public string ScopeNotice => "All public non-finalized markets: Polymarket closed=false; Kalshi unopened, open, paused. Cached metadata only.";
    public string PageLabel => $"Page {Page} · {Total} stored markets match";
    public bool CanPrevious => Page > 1 && !Loading;
    public bool CanNext => Page * 30 < Total && !Loading;
    public bool CanCancel => ExchangeStatuses.Any(s => s.LatestRun?.State == "Running");

    public MarketExplorerViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessInvalidated;
        state.CatalogInvalidated += CatalogInvalidated;
        state.CatalogRefreshRequested += CatalogInvalidated;
        state.PropertyChanged += StateChanged;
    }

    public void Activate()
    {
        active = true;
        _ = RefreshAsync();
    }
    public void Deactivate() { active = false; Interlocked.Increment(ref queryGeneration); }

    private void AccessInvalidated(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref queryGeneration); Interlocked.Increment(ref detailGeneration);
        Markets.Clear(); ExchangeStatuses.Clear(); Tags.Clear(); Tags.Add("All");
        SelectedMarket = null; Detail = null; Total = 0;
        Notice = "Local workspace access was denied. Refresh to reauthorize.";
    }
    private void CatalogInvalidated(object? sender, EventArgs args)
    { if (active) _ = RefreshAsync(); }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (active && args.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus == "Connected")
            _ = RefreshAsync();
    }
    private bool TryContext(out Guid workspace)
    {
        workspace = Guid.Empty;
        return state.ConnectionStatus == "Connected" && state.HasSnapshot &&
            Guid.TryParse(state.WorkspaceIdentifier, out workspace);
    }
    private bool Current(long generation, long access, Guid workspace, string instance) =>
        generation == Volatile.Read(ref queryGeneration) && access == state.AccessGeneration &&
        state.WorkspaceIdentifier == workspace.ToString() && state.BackendInstance == instance &&
        state.ConnectionStatus == "Connected";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref queryGeneration);
        if (!TryContext(out var workspace))
        {
            Notice = "Connect to the authorized local backend to browse cached markets.";
            return;
        }
        var access = state.AccessGeneration; var instance = state.BackendInstance;
        Loading = true; Notice = "Loading locally stored markets…";
        try
        {
            var exchange = SelectedExchange == "All" ? null : SelectedExchange;
            var status = SelectedStatus == "All" ? null : SelectedStatus;
            var tag = SelectedTag == "All" ? null : SelectedTag;
            var statusTask = backend.CatalogStatusAsync(workspace, CancellationToken.None);
            var pageTask = backend.CatalogMarketsAsync(workspace, exchange, Search, status, tag,
                SelectedSort, Page, 30, CancellationToken.None);
            await Task.WhenAll(statusTask, pageTask);
            if (!Current(generation, access, workspace, instance)) return;
            (string Exchange, string NativeId)? selectedKey = SelectedMarket is null ? null :
                (SelectedMarket.Exchange, SelectedMarket.NativeId);
            var result = await pageTask;
            Markets.Clear(); foreach (var item in result.Items) Markets.Add(item);
            Total = result.Total;
            var previousTag = SelectedTag;
            Tags.Clear(); Tags.Add("All"); foreach (var item in result.AvailableTags) Tags.Add(item);
            SelectedTag = Tags.Contains(previousTag) ? previousTag : "All";
            ExchangeStatuses.Clear(); foreach (var item in (await statusTask).Exchanges) ExchangeStatuses.Add(item);
            SelectedMarket = selectedKey is null ? null : Markets.FirstOrDefault(m =>
                m.Exchange == selectedKey.Value.Exchange && m.NativeId == selectedKey.Value.NativeId);
            Notice = ExchangeStatuses.Any(s => s.LatestRun?.State is "Partial" or "Failed" or "Cancelled")
                ? "Cached catalog; latest synchronization is incomplete. Previously stored markets remain available."
                : Total == 0 ? "No stored markets match. Sync Markets to request public metadata, or adjust filters."
                : "Cached public metadata · no live orderbook or execution capability.";
            OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(PageLabel));
        }
        catch (BackendFailure failure)
        {
            if (!Current(generation, access, workspace, instance)) return;
            if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
            else Notice = "Catalog request failed; cached data may still be available after Refresh.";
        }
        catch (Exception) { if (Current(generation, access, workspace, instance)) Notice = "Catalog request failed; use Refresh to retry."; }
        finally { if (generation == Volatile.Read(ref queryGeneration)) Loading = false; }
    }

    [RelayCommand]
    private async Task ApplyFiltersAsync() { Page = 1; await RefreshAsync(); }
    [RelayCommand]
    private async Task NextAsync() { if (CanNext) { Page++; await RefreshAsync(); } }
    [RelayCommand]
    private async Task PreviousAsync() { if (CanPrevious) { Page--; await RefreshAsync(); } }
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (!TryContext(out var workspace)) return;
        var access = state.AccessGeneration; var instance = state.BackendInstance;
        try
        {
            await backend.StartMarketSyncAsync(workspace, SelectedExchange, CancellationToken.None);
            if (access == state.AccessGeneration && state.BackendInstance == instance) await RefreshAsync();
        }
        catch (BackendFailure failure)
        {
            if (access != state.AccessGeneration || state.BackendInstance != instance) return;
            if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
            else Notice = "Synchronization could not start. Review backend status and retry explicitly.";
        }
    }
    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!TryContext(out var workspace)) return;
        var runs = ExchangeStatuses.Where(s => s.LatestRun?.State == "Running" &&
            (SelectedExchange == "All" || s.Exchange == SelectedExchange))
            .Select(s => s.LatestRun!).ToArray();
        if (runs.Length == 0) return;
        var access = state.AccessGeneration; var instance = state.BackendInstance;
        try
        {
            foreach (var run in runs)
                await backend.CancelMarketSyncAsync(workspace, run.Id, CancellationToken.None);
            if (access == state.AccessGeneration && state.BackendInstance == instance) await RefreshAsync();
        }
        catch (BackendFailure failure)
        {
            if (access != state.AccessGeneration || state.BackendInstance != instance) return;
            if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
            else Notice = "Cancellation could not be confirmed. Refresh synchronization status.";
        }
    }
    partial void OnSelectedMarketChanged(MarketResponse? value)
    {
        var generation = Interlocked.Increment(ref detailGeneration);
        Detail = value;
        if (value is null || !TryContext(out var workspace)) return;
        var access = state.AccessGeneration; var instance = state.BackendInstance;
        _ = LoadDetailAsync(value, workspace, generation, access, instance);
    }
    private async Task LoadDetailAsync(MarketResponse value, Guid workspace, long generation, long access, string instance)
    {
        try
        {
            var detail = await backend.CatalogMarketAsync(workspace, value.Exchange, value.NativeId, CancellationToken.None);
            if (generation == Volatile.Read(ref detailGeneration) && access == state.AccessGeneration &&
                state.BackendInstance == instance && state.WorkspaceIdentifier == workspace.ToString()) Detail = detail;
        }
        catch (BackendFailure failure)
        {
            if (generation == Volatile.Read(ref detailGeneration) && access == state.AccessGeneration &&
                state.BackendInstance == instance && failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        }
    }
    partial void OnPageChanged(int value) { OnPropertyChanged(nameof(PageLabel)); OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); }
    partial void OnTotalChanged(int value) { OnPropertyChanged(nameof(PageLabel)); OnPropertyChanged(nameof(CanNext)); }
    partial void OnLoadingChanged(bool value) { OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); }
    public void Dispose()
    {
        state.AccessInvalidated -= AccessInvalidated; state.CatalogInvalidated -= CatalogInvalidated;
        state.CatalogRefreshRequested -= CatalogInvalidated; state.PropertyChanged -= StateChanged;
    }
}
