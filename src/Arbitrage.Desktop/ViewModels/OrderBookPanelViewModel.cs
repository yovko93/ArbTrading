using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class OrderBookPanelViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private readonly TimeProvider clock;
    private MarketResponse? market;
    private CancellationTokenSource? requestCancellation;
    private CancellationTokenSource? ageCancellation;
    private Task? worker;
    private bool pending, pendingExternal, disposed, active, applying;
    private long generation;
    private bool invalidated, mutationBusy;
    private Guid? depthSnapshot;
    [ObservableProperty] private decimal depthQuantity = 1m;
    [ObservableProperty] private string depthSide = "Consume asks";
    [ObservableProperty] private string depthNotice = "Choose a quantity for a read-only gross depth estimate.";
    public string[] DepthSides { get; } = ["Consume asks", "Consume bids"];
    public string RealtimeDetails => Response?.Realtime is { } r
        ? $"Source: Realtime · {r.State} · {r.Continuity}\nConnection: {(r.Connected ? "connected" : "disconnected")} · Anchor: {(r.AnchorAt is null ? "awaiting" : r.AnchorAt.Value.ToString("HH:mm:ss 'UTC'"))} · Sequence: {r.Sequence?.ToString() ?? "not provided"}\nResync reason: {r.ResyncReason ?? "none"} · Version: {r.Version}"
        : "Source: REST Snapshot · Not subscribed";
    [RelayCommand] private Task StartRealtimeAsync() => MutateAsync(true);
    [RelayCommand] private Task StopRealtimeAsync() => MutateAsync(false);
    private async Task MutateAsync(bool start)
    {
        if (mutationBusy || !active || market is null || SelectedInstrument is null || state.ConnectionStatus != "Connected" ||
            !Guid.TryParse(state.WorkspaceIdentifier, out var workspace)) return;
        var version = generation; var access = state.AccessGeneration; var instance = state.BackendInstance;
        var selected = market; var instrument = SelectedInstrument;
        bool Current() => active && !disposed && version == generation && access == state.AccessGeneration &&
            instance == state.BackendInstance && state.WorkspaceIdentifier == workspace.ToString() && state.ConnectionStatus == "Connected";
        mutationBusy = true;
        try
        {
            var result = await backend.RealtimeOrderBookAsync(workspace, selected.Exchange, selected.NativeId,
                instrument.NativeInstrumentId, start, CancellationToken.None);
            if (Current()) { Response = result; invalidated = true; Notice = start ? "Realtime start requested. Waiting for an anchor." : "Realtime stop requested."; }
        }
        catch (BackendFailure e)
        {
            if (Current())
            {
                if (e.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(e.State.ToString(), e.Message);
                else Notice = "Realtime control failed. Retry explicitly.";
            }
        }
        finally { mutationBusy = false; }
    }
    [RelayCommand]
    private async Task PreviewDepthAsync()
    {
        if (!active || market is null || SelectedInstrument is null || state.ConnectionStatus != "Connected" ||
            DepthQuantity <= 0 || !Guid.TryParse(state.WorkspaceIdentifier, out var workspace)) return;
        var version = generation; var access = state.AccessGeneration; var instance = state.BackendInstance;
        try
        {
            var result = await backend.DepthAsync(workspace, market.Exchange, market.NativeId,
                new(SelectedInstrument.NativeInstrumentId, DepthSide == "Consume asks" ? "Buy" : "Sell", DepthQuantity), CancellationToken.None);
            if (active && !disposed && version == generation && access == state.AccessGeneration && instance == state.BackendInstance &&
                state.WorkspaceIdentifier == workspace.ToString() && state.ConnectionStatus == "Connected")
            {
                depthSnapshot = result.SnapshotId;
                DepthNotice = result.IsActionable
                    ? $"Gross estimate: {result.ExecutableQuantity:G29} units · notional {result.GrossNotional:G29} · VWAP {result.Vwap:G29} · {result.Reason ?? "Full depth"}"
                    : $"Not actionable: {result.Reason ?? "Unavailable"}";
            }
        }
        catch (BackendFailure e)
        {
            if (version != generation || access != state.AccessGeneration || instance != state.BackendInstance) return;
            if (e.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(e.State.ToString(), e.Message);
            else DepthNotice = "Depth preview unavailable.";
        }
    }
    private void BookInvalidated(object? sender, OrderBookInvalidation notice)
    {
        if (active && notice.WorkspaceId.ToString() == state.WorkspaceIdentifier && notice.Instrument.Exchange == market?.Exchange &&
            notice.Instrument.NativeMarketId == market.NativeId && notice.Instrument.NativeInstrumentId == SelectedInstrument?.NativeInstrumentId)
            invalidated = true;
    }
    public ObservableCollection<BookInstrumentResponse> Instruments { get; } = [];
    [ObservableProperty] private BookInstrumentResponse? selectedInstrument;
    [ObservableProperty] private OrderBookResponse? response;
    [ObservableProperty] private bool loading;
    [ObservableProperty] private string notice = "Select a market to inspect its cached orderbook.";
    public IEnumerable<BookLevelResponse> Bids => Response?.Snapshot?.Bids.Take(10) ?? [];
    public IEnumerable<BookLevelResponse> Asks => Response?.Snapshot?.Asks.Take(10) ?? [];
    public decimal? BestBid => Response?.Snapshot?.Bids.FirstOrDefault()?.Price;
    public decimal? BestAsk => Response?.Snapshot?.Asks.FirstOrDefault()?.Price;
    public decimal? Spread => BestBid is { } bid && BestAsk is { } ask && ask >= bid ? ask - bid : null;
    public string Provenance => Response?.Snapshot?.Asks.Any(l => l.Origin == "DerivedComplement") == true
        ? "Asks: derived from opposite-side bids" : market?.Exchange == "Kalshi" ? "Native YES/NO bids; binary asks are derived when supported" : "Native bids and asks";
    public string Age => Response?.Snapshot is { } b ? $"Age: {Math.Max(0, (clock.GetUtcNow() - b.RetrievedAtUtc).Ticks / (decimal)TimeSpan.TicksPerSecond):0.0}s" : "Age: unavailable";
    public string SnapshotState => state.ConnectionStatus != "Connected" ? "Disconnected · cached snapshot not actionable" :
        Response?.Realtime is { } live ? $"Realtime · {(Response.Snapshot is { } snapshot && clock.GetUtcNow() - snapshot.RetrievedAtUtc > TimeSpan.FromSeconds(Response.FreshnessSeconds) && live.State == "Streaming" ? "Stale" : live.State)} · {live.Continuity}" :
        Loading ? "Loading" : Response?.Snapshot is { } b && Response.State == "Fresh" &&
        clock.GetUtcNow() - b.RetrievedAtUtc > TimeSpan.FromSeconds(Response.FreshnessSeconds)
            ? "Cached snapshot · stale" : Response?.State == "Fresh" ? "Fresh REST snapshot" : Response?.State ?? "Unavailable";
    public OrderBookPanelViewModel(MainViewModel state, BackendClient backend, TimeProvider? clock = null)
    {
        this.state = state; this.backend = backend; this.clock = clock ?? TimeProvider.System;
        state.OrderBookInvalidated += BookInvalidated;
        state.AccessInvalidated += AccessInvalidated; state.PropertyChanged += StateChanged;
    }
    public void Activate()
    {
        if (disposed || active) return;
        active = true; ageCancellation = new(); Observe(UpdateAgeAsync(ageCancellation.Token));
        if (market is not null) Queue(false);
    }
    public void Deactivate()
    {
        active = false; pending = false; generation++; requestCancellation?.Cancel(); ageCancellation?.Cancel();
        ageCancellation?.Dispose(); ageCancellation = null; Loading = false;
    }
    public void SelectMarket(MarketResponse? value)
    {
        invalidated = false; DepthNotice = "Depth preview requires the current actionable book.";
        market = value; generation++; requestCancellation?.Cancel(); pending = false; Loading = false;
        applying = true; Instruments.Clear(); SelectedInstrument = null; Response = null; applying = false;
        Notice = value is null ? "Select a market to inspect its cached orderbook." : "Cached data only. Refresh Order Book explicitly to fetch a REST snapshot.";
        if (active && value is not null) Queue(false);
    }
    partial void OnSelectedInstrumentChanged(BookInstrumentResponse? value)
    {
        if (applying) return;
        generation++; requestCancellation?.Cancel(); Response = null; Loading = false;
        Queue(false);
    }
    [RelayCommand]
    private Task RefreshAsync()
    {
        if (!Loading) Queue(true);
        return worker ?? Task.CompletedTask;
    }
    private void Queue(bool external)
    {
        if (!active || disposed || market is null || state.ConnectionStatus != "Connected") return;
        pending = true; pendingExternal = external;
        if (worker is not { IsCompleted: false }) { worker = RunAsync(); Observe(worker); }
    }
    private async Task RunAsync()
    {
        while (pending && active && !disposed)
        {
            pending = false;
            var selected = market; var external = pendingExternal; var instrument = SelectedInstrument?.NativeInstrumentId;
            var version = generation; var access = state.AccessGeneration; var instance = state.BackendInstance;
            if (selected is null || !Guid.TryParse(state.WorkspaceIdentifier, out var workspace)) return;
            using var cancellation = new CancellationTokenSource(); requestCancellation = cancellation;
            bool Current() => active && !disposed && generation == version && state.AccessGeneration == access &&
                state.BackendInstance == instance && state.WorkspaceIdentifier == workspace.ToString() && state.ConnectionStatus == "Connected";
            Loading = true;
            try
            {
                var result = await backend.OrderBookAsync(workspace, selected.Exchange, selected.NativeId, instrument, external, cancellation.Token);
                if (!Current()) continue;
                applying = true;
                try
                {
                    Instruments.Clear(); foreach (var item in result.Instruments) Instruments.Add(item);
                    SelectedInstrument = Instruments.FirstOrDefault(i => i.NativeInstrumentId == result.SelectedInstrumentId);
                    Response = result;
                    Notice = result.LastRefreshFailure is { } failure ? $"Latest refresh: {failure.Code}. Prior cached snapshot retained if available." :
                        result.Reason ?? "Read-only gross depth data. No execution is available.";
                }
                finally { applying = false; }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (BackendFailure e)
            {
                if (!Current()) continue;
                if (e.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                    state.SetRealtimeStatus(e.State.ToString(), e.Message);
                else Notice = "Orderbook request failed. Retry explicitly when the backend is available.";
            }
            catch (Exception) { if (Current()) Notice = "Orderbook request failed."; }
            finally
            {
                if (ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
                if (version == generation) Loading = false;
            }
        }
    }
    private async Task UpdateAgeAsync(CancellationToken ct)
    {
        // Coalesced cached reads, at most once per second. Never starts an external subscription.
        try { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1), clock, ct); NotifyClock(); if (invalidated || Response?.Realtime is not null) { invalidated = false; if (!Loading) Queue(false); } } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private void NotifyClock()
    {
        OnPropertyChanged(nameof(Age)); OnPropertyChanged(nameof(SnapshotState));
        if (depthSnapshot is not null && (Response?.Snapshot?.Id != depthSnapshot || Response.IsActionable == false ||
            Response.Snapshot is { } book && clock.GetUtcNow() - book.RetrievedAtUtc > TimeSpan.FromSeconds(Response.FreshnessSeconds)))
        { depthSnapshot = null; DepthNotice = "Depth estimate expired. Preview the current actionable book again."; }
    }
    private void AccessInvalidated(object? sender, EventArgs args) => SelectMarket(null);
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MainViewModel.ConnectionStatus) or nameof(MainViewModel.BackendInstance) or nameof(MainViewModel.WorkspaceIdentifier))) return;
        if (args.PropertyName is nameof(MainViewModel.BackendInstance) or nameof(MainViewModel.WorkspaceIdentifier)) Response = null;
        generation++; requestCancellation?.Cancel(); Loading = false; NotifyClock();
        if (state.ConnectionStatus == "Connected") Queue(false);
    }
    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(SnapshotState));
    partial void OnResponseChanged(OrderBookResponse? value)
    {
        foreach (var property in new[] { nameof(Bids), nameof(Asks), nameof(BestBid), nameof(BestAsk), nameof(Spread), nameof(Provenance), nameof(RealtimeDetails) }) OnPropertyChanged(property);
        NotifyClock();
    }
    private static async void Observe(Task task) { try { await task; } catch (Exception) { /* Observe event work. */ } }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Deactivate(); state.OrderBookInvalidated -= BookInvalidated; state.AccessInvalidated -= AccessInvalidated; state.PropertyChanged -= StateChanged;
    }
}
