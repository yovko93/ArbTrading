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
    public OrderBookPanelViewModel OrderBook { get; }
    private long queryGeneration;
    private long detailGeneration;
    private bool active;
    private bool disposed;
    private bool refreshPending;
    private Task? refreshWorker;
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? detailCancellation;
    public string[] Exchanges { get; } = ["All", "Polymarket", "Kalshi"];
    public string[] Statuses { get; } = ["All", "Open", "Upcoming", "Paused", "Closed", "Determined", "Disputed", "Amended", "OpenOrPaused", "UpcomingOrPaused", "Finalized", "Unknown"];
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
    public string ScopeNotice => "Public non-finalized buckets: Polymarket closed=false; Kalshi unopened, open, paused, closed. Cached metadata, not an atomic snapshot.";
    public string PageLabel => $"Page {Page} · {Total} stored markets match";
    public bool CanPrevious => Page > 1 && !Loading;
    public bool CanNext => Page * 30 < Total && !Loading;
    public bool CanCancel => ExchangeStatuses.Any(s => s.LatestRun?.State == "Running");

    public MarketExplorerViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        OrderBook = new(state, backend);
        state.AccessInvalidated += AccessInvalidated;
        state.CatalogInvalidated += CatalogInvalidated;
        state.CatalogRefreshRequested += CatalogInvalidated;
        state.PropertyChanged += StateChanged;
    }

    public void Activate()
    {
        if (disposed) return;
        active = true;
        OrderBook.Activate();
        Observe(RequestRefreshAsync(supersede: true));
    }
    public void Deactivate()
    {
        active = false; refreshPending = false;
        OrderBook.Deactivate();
        Interlocked.Increment(ref queryGeneration);
        refreshCancellation?.Cancel(); detailCancellation?.Cancel();
        Loading = false;
    }

    private void AccessInvalidated(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref queryGeneration); Interlocked.Increment(ref detailGeneration);
        refreshPending = false; refreshCancellation?.Cancel(); detailCancellation?.Cancel(); Loading = false;
        Markets.Clear(); ExchangeStatuses.Clear(); Tags.Clear(); Tags.Add("All");
        SelectedMarket = null; Detail = null; Total = 0;
        Notice = "Local workspace access was denied. Refresh to reauthorize.";
    }
    private void CatalogInvalidated(object? sender, EventArgs args)
    { if (active) Observe(RequestRefreshAsync(supersede: false)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (active && args.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus == "Connected")
            Observe(RequestRefreshAsync(supersede: true));
    }
    private bool TryContext(out Guid workspace)
    {
        workspace = Guid.Empty;
        return state.ConnectionStatus == "Connected" && state.HasSnapshot &&
            Guid.TryParse(state.WorkspaceIdentifier, out workspace);
    }
    private bool Current(long generation, long access, Guid workspace, string instance) =>
        active && !disposed && generation == Volatile.Read(ref queryGeneration) && access == state.AccessGeneration &&
        state.WorkspaceIdentifier == workspace.ToString() && state.BackendInstance == instance &&
        state.ConnectionStatus == "Connected";

    [RelayCommand]
    private Task RefreshAsync() => RequestRefreshAsync(supersede: true);

    private Task RequestRefreshAsync(bool supersede)
    {
        if (disposed || !active && !supersede) return Task.CompletedTask;
        if (supersede) { active = true; Interlocked.Increment(ref queryGeneration); refreshCancellation?.Cancel(); }
        refreshPending = true;
        if (refreshWorker is { IsCompleted: false }) return refreshWorker;
        refreshWorker = RunRefreshesAsync();
        return refreshWorker;
    }

    private async Task RunRefreshesAsync()
    {
        try
        {
            while (refreshPending && !disposed)
            {
                refreshPending = false;
                using var cancellation = new CancellationTokenSource();
                refreshCancellation = cancellation;
                var generation = Volatile.Read(ref queryGeneration);
                await ReadRefreshAsync(generation, cancellation.Token);
                if (ReferenceEquals(refreshCancellation, cancellation)) refreshCancellation = null;
            }
        }
        finally { Loading = false; }
    }

    private async Task ReadRefreshAsync(long generation, CancellationToken cancellationToken)
    {
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
            var statusTask = backend.CatalogStatusAsync(workspace, cancellationToken);
            var pageTask = backend.CatalogMarketsAsync(workspace, exchange, Search, status, tag,
                SelectedSort, Page, 30, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (BackendFailure failure)
        {
            if (!Current(generation, access, workspace, instance)) return;
            if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
            else Notice = "Catalog request failed; cached data may still be available after Refresh.";
        }
        catch (Exception) { if (Current(generation, access, workspace, instance)) Notice = "Catalog request failed; use Refresh to retry."; }
        finally { if (generation == Volatile.Read(ref queryGeneration) && !refreshPending) Loading = false; }
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
        OrderBook.SelectMarket(value);
        var generation = Interlocked.Increment(ref detailGeneration);
        detailCancellation?.Cancel(); detailCancellation?.Dispose();
        Detail = value;
        if (value is null || !TryContext(out var workspace)) return;
        detailCancellation = new CancellationTokenSource();
        var access = state.AccessGeneration; var instance = state.BackendInstance;
        Observe(LoadDetailAsync(value, workspace, generation, access, instance, detailCancellation.Token));
    }
    private async Task LoadDetailAsync(MarketResponse value, Guid workspace, long generation, long access, string instance, CancellationToken ct)
    {
        try
        {
            var detail = await backend.CatalogMarketAsync(workspace, value.Exchange, value.NativeId, ct);
            if (generation == Volatile.Read(ref detailGeneration) && access == state.AccessGeneration &&
                active && !disposed && state.ConnectionStatus == "Connected" &&
                state.BackendInstance == instance && state.WorkspaceIdentifier == workspace.ToString()) Detail = detail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (BackendFailure failure)
        {
            if (generation == Volatile.Read(ref detailGeneration) && access == state.AccessGeneration &&
                state.BackendInstance == instance && failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        }
    }
    private static async void Observe(Task task)
    {
        try { await task; } catch (Exception) { /* Event-triggered work must be observed. */ }
    }
    partial void OnPageChanged(int value) { OnPropertyChanged(nameof(PageLabel)); OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); }
    partial void OnTotalChanged(int value) { OnPropertyChanged(nameof(PageLabel)); OnPropertyChanged(nameof(CanNext)); }
    partial void OnLoadingChanged(bool value) { OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); }
    public void Dispose()
    {
        disposed = true; Deactivate(); detailCancellation?.Dispose();
        OrderBook.Dispose();
        state.AccessInvalidated -= AccessInvalidated; state.CatalogInvalidated -= CatalogInvalidated;
        state.CatalogRefreshRequested -= CatalogInvalidated; state.PropertyChanged -= StateChanged;
    }
}
