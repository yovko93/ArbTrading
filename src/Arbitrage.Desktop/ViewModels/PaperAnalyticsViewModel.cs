using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public sealed record PaperAnalyticsBucketRow(PaperPerformanceBucketResponse Performance, PaperValuationBucketResponse? Valuation)
{
    public string Exchange => Performance.Exchange;
    public string Currency => Performance.Currency;
    public decimal StartingCash => Performance.StartingCash;
    public decimal CurrentCash => Performance.CurrentCash;
    public decimal OpenCostBasis => Performance.OpenCostBasis;
    public decimal RealizedPnl => Performance.CumulativeRealizedPnl;
    public int OpenPositions => Performance.OpenPositionCount;
    public int SettledPositions => Performance.SettledPositionCount;
    public decimal? AccountingBookValue => Valuation?.AccountingBookValue;
    public decimal? GrossMarkedEquity => Valuation?.GrossMarkedEquity;
    public decimal? FeeAdjustedMarkedEquity => Valuation?.FeeAdjustedMarkedEquity;
    public decimal? GrossUnrealizedPnl => Valuation?.GrossTotalPnl is decimal total ? total - Valuation.RealizedPnl : null;
    public decimal? FeeAdjustedUnrealizedPnl => Valuation?.FeeAdjustedTotalPnl is decimal total ? total - Valuation.RealizedPnl : null;
    public string Coverage => Valuation is null ? "Unavailable" : $"{Valuation.ValuationCoverage:P0} · {Valuation.FullyMarkedPositionCount}/{Valuation.OpenPositionCount} fully marked";
}

public partial class PaperAnalyticsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient? backend;
    private readonly CancellationTokenSource lifetime = new();
    private long revision;
    private bool active, disposed;

    public ObservableCollection<PaperAnalyticsBucketRow> Buckets { get; } = [];
    [ObservableProperty] private PaperAccountResponse? account;
    [ObservableProperty] private PaperPerformanceResponse? performance;
    [ObservableProperty] private PaperValuationResponse? valuation;
    [ObservableProperty] private PaperReliabilityCurrentResponse? reliability;
    [ObservableProperty] private string notice = "Open Analytics with an authorized local workspace to read the paper summary.";
    [ObservableProperty] private bool busy;

    public string GenerationSummary => Account?.Generation is { } generation
        ? $"Current paper generation · {generation.Id} · created {generation.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "Paper analytics unavailable until a paper generation is initialized.";
    public string ExecutionSummary => Performance is null ? "No paper performance summary available."
        : Performance.Buckets.Length == 0 || Performance.Buckets.All(b => b.CommittedExecutionCount == 0)
            ? "No paper executions recorded for this generation."
            : "Committed paper executions recorded. See Portfolio for execution and position details.";
    public string ValuationSummary => Valuation is null ? "Current executable valuation unavailable."
        : $"{(Valuation.State != "Available" || Valuation.Buckets.Any(b => b.OpenPositionCount > 0 && b.GrossMarkedEquity is null) ? "Current executable valuation unavailable for some open positions" : "Executable valuation")} · {Valuation.State} · as of {Valuation.ValuedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · coverage varies by venue and currency. Missing values are unavailable, not zero.";
    public string ReliabilitySummary => Reliability?.Campaign is not { } campaign ? "No active Paper Reliability campaign."
        : $"{campaign.Name} · {campaign.State} · evaluation: {Reliability.Report?.State ?? "Not evaluated"} · evidence gap: {(campaign.EvidenceGapDetected ? "Yes" : "No")}";
    public string ReliabilityRuntime => Reliability?.Report is not { } report ? "Observed healthy automatic paper runtime unavailable."
        : report.Counters.TryGetValue("AutomationHealthyTicks", out var ticks)
            ? $"Observed healthy automatic paper runtime: {TimeSpan.FromTicks(ticks):c}"
            : "Observed healthy automatic paper runtime unavailable.";

    public PaperAnalyticsViewModel(MainViewModel state, BackendClient? backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessInvalidated;
        state.PropertyChanged += StateChanged;
    }

    private (Guid Workspace, long Access, string Backend)? Context() =>
        active && !disposed && backend is not null && state.ConnectionStatus == "Connected" && state.HasSnapshot
        && Guid.TryParse(state.WorkspaceIdentifier, out var workspace)
            ? (workspace, state.AccessGeneration, state.BackendInstance) : null;

    private void Clear(string message)
    {
        revision++;
        Account = null; Performance = null; Valuation = null; Reliability = null;
        Buckets.Clear(); Notice = message; Busy = false;
        NotifySummary();
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(GenerationSummary)); OnPropertyChanged(nameof(ExecutionSummary));
        OnPropertyChanged(nameof(ValuationSummary)); OnPropertyChanged(nameof(ReliabilitySummary));
        OnPropertyChanged(nameof(ReliabilityRuntime));
    }

    private void AccessInvalidated(object? sender, EventArgs e) => Clear("Private analytics cleared. Refresh after workspace access is restored.");
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.WorkspaceIdentifier) or nameof(MainViewModel.BackendInstance)
            || e.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus != "Connected")
            Clear("Workspace or backend changed. Refresh the analytics summary.");
    }

    public void Activate()
    {
        if (active || disposed) return;
        active = true;
        _ = RefreshAsync();
    }

    public void Deactivate()
    {
        active = false;
        Clear("Analytics is inactive. Open it to refresh the paper summary.");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Context() is not { } context) { Clear("Connect to an authorized local workspace to read analytics."); return; }
        var captured = ++revision;
        var token = lifetime.Token;
        Busy = true;
        Notice = "Refreshing paper summary…";
        try
        {
            var nextAccount = await backend!.PaperAccountAsync(context.Workspace, token);
            if (Context() != context || captured != revision) return;
            PaperPerformanceResponse? nextPerformance = null;
            PaperValuationResponse? nextValuation = null;
            if (nextAccount.Generation is { } generation)
            {
                nextPerformance = await backend.PaperPerformanceAsync(context.Workspace, generation.Id, token);
                if (Context() != context || captured != revision) return;
                nextValuation = await backend.PaperValuationAsync(context.Workspace, generation.Id, 1, token);
                if (Context() != context || captured != revision) return;
            }
            var nextReliability = await backend.PaperReliabilityCurrentAsync(context.Workspace, token);
            if (Context() != context || captured != revision) return;

            Account = nextAccount; Performance = nextPerformance; Valuation = nextValuation; Reliability = nextReliability;
            Buckets.Clear();
            foreach (var bucket in nextPerformance?.Buckets ?? [])
                Buckets.Add(new(bucket, nextValuation?.Buckets.FirstOrDefault(v => v.Exchange == bucket.Exchange && v.Currency == bucket.Currency)));
            Notice = nextAccount.Generation is null ? "No current paper generation. Reliability is shown when available." :
                "Read-only paper summary. Values are separated by venue and currency; valuation uses locally cached executable depth.";
            NotifySummary();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure)
        {
            if (Context() != context || captured != revision) return;
            Clear("Analytics unavailable: " + failure.Message);
            if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
                state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        }
        finally { if (captured == revision) Busy = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; active = false; Clear("Analytics closed.");
        lifetime.Cancel(); lifetime.Dispose();
        state.AccessInvalidated -= AccessInvalidated; state.PropertyChanged -= StateChanged;
    }
}
