using System.Collections.ObjectModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class PaperTradingViewModel
{
    private long settlementRevision;
    private ConfirmResolutionRequest? pendingResolution;
    public ObservableCollection<PaperResolutionCandidateResponse> ResolutionCandidates { get; } = [];
    public ObservableCollection<PaperResolutionResponse> Resolutions { get; } = [];
    public ObservableCollection<PaperPositionResponse> PositionHistory { get; } = [];
    public ObservableCollection<PaperCurvePointResponse> CurvePoints { get; } = [];
    public string[] PositionFilters { get; } = ["Open", "Settled", "All"];
    [ObservableProperty] private PaperGenerationResponse? settlementGeneration;
    [ObservableProperty] private PaperResolutionCandidateResponse? selectedCandidate;
    [ObservableProperty] private PaperPayoutResponse? winningOutcome;
    [ObservableProperty] private PaperResolutionPreviewResponse? resolutionPreview;
    [ObservableProperty] private PaperResolutionResponse? selectedResolution;
    [ObservableProperty] private PaperPerformanceResponse? performance;
    [ObservableProperty] private PaperPerformanceBucketResponse? selectedBucket;
    [ObservableProperty] private string positionFilter = "Open";
    [ObservableProperty] private int settlementPage = 1;
    [ObservableProperty] private int candidatePage = 1;
    [ObservableProperty] private int positionPage = 1;
    [ObservableProperty] private int curvePage = 1;
    [ObservableProperty] private bool curveHasMore;
    public string CandidateText => SelectedCandidate is not { } c ? "Select a market with open paper positions." :
        $"{c.Exchange} · {c.MarketId} · {c.Title}\n{(c.Supported ? "Standard binary scenario" : "SettlementTypeUnsupported")} · executions {c.ExecutionIds.Length}\n" +
        string.Join("\n", c.Positions.Select(p => $"{p.Outcome} ({p.InstrumentId}) · quantity {p.Quantity} · cost {p.CostBasis} {p.Currency}"));
    public string ResolutionPreviewText => ResolutionPreview is { } p ? DescribeResolution(p) : "Preview a Manual Scenario Resolution. No result is inferred from prices or market status.";
    public string ResolutionDetailText => SelectedResolution is not { } r ? "Select settlement history for details." :
        $"Manual Scenario Resolution · {r.Id} · generation {r.GenerationId}\nActor {r.ActorId} · UTC {r.RecordedAt:O}\nLedger {r.LedgerTransactionId} · executions {string.Join(", ", r.ExecutionIds)}\n" +
        string.Join("\n", r.PayoutVector.Select(o => $"{o.Outcome} ({o.InstrumentId}): {o.PayoutPerShare}/share")) + "\n" +
        string.Join("\n", r.Positions.Select(p => $"{p.Currency}: payout {p.SettlementPayout}, realized P&L {p.RealizedPnl}"));
    public string PerformanceText => $"{Performance?.Lifecycle ?? "Select a generation"} · Realized performance = starting capital + cumulative realized P&L. Open-position market value is excluded. Each venue/currency is separate.";
    private void ClearResolutionPreview() { settlementRevision++; ResolutionPreview = null; pendingResolution = null; }
    private void ClearSettlement()
    {
        ClearResolutionPreview(); ResolutionCandidates.Clear(); Resolutions.Clear(); PositionHistory.Clear(); CurvePoints.Clear();
        SettlementGeneration = null; SelectedCandidate = null; WinningOutcome = null; SelectedResolution = null; Performance = null; SelectedBucket = null;
    }
    partial void OnSettlementGenerationChanged(PaperGenerationResponse? value)
    {
        ClearValuation(); ValuationPage = 1;
        ClearRisk();
        ClearResolutionPreview(); SelectedCandidate = null; SelectedResolution = null; Performance = null; SelectedBucket = null;
        ResolutionCandidates.Clear(); Resolutions.Clear(); PositionHistory.Clear(); CurvePoints.Clear(); SettlementPage = CandidatePage = PositionPage = CurvePage = 1;
    }
    partial void OnSelectedCandidateChanged(PaperResolutionCandidateResponse? value) { WinningOutcome = null; ClearResolutionPreview(); OnPropertyChanged(nameof(CandidateText)); }
    partial void OnWinningOutcomeChanged(PaperPayoutResponse? value) => ClearResolutionPreview();
    partial void OnResolutionPreviewChanged(PaperResolutionPreviewResponse? value) { OnPropertyChanged(nameof(ResolutionPreviewText)); ConfirmResolutionCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedResolutionChanged(PaperResolutionResponse? value) => OnPropertyChanged(nameof(ResolutionDetailText));
    partial void OnPerformanceChanged(PaperPerformanceResponse? value) => OnPropertyChanged(nameof(PerformanceText));
    partial void OnPositionFilterChanged(string value) { PositionPage = 1; PositionHistory.Clear(); }
    partial void OnSelectedBucketChanged(PaperPerformanceBucketResponse? value) { CurvePage = 1; CurvePoints.Clear(); }
    [RelayCommand] private async Task RefreshSettlementAsync()
    {
        if (Busy || Context() is not { } context || Account?.Generation is null) return;
        SettlementGeneration ??= Account.Generation;
        var viewVersion = pollVersion;
        var g = SettlementGeneration.Id; var captured = settlementRevision; var filter = PositionFilter;
        try
        {
            var candidates = await backend.ResolutionCandidatesAsync(context.Workspace, g, CandidatePage, lifetime.Token);
            var history = await backend.ResolutionHistoryAsync(context.Workspace, g, SettlementPage, lifetime.Token);
            var positions = await backend.PaperPositionHistoryAsync(context.Workspace, g, filter, PositionPage, lifetime.Token);
            var performance = await backend.PaperPerformanceAsync(context.Workspace, g, lifetime.Token);
            if (Context() != context || SettlementGeneration?.Id != g || captured != settlementRevision || filter != PositionFilter) return;
            // Preserve selection and a reviewed preview across periodic refetches unless its candidate vanished.
            var key = SelectedCandidate;
            if (key is not null && !candidates.Any(c => c.Exchange == key.Exchange && c.MarketId == key.MarketId)) SelectedCandidate = null;
            if (!ResolutionCandidates.Select(c => (c.Exchange, c.MarketId)).SequenceEqual(candidates.Select(c => (c.Exchange, c.MarketId))))
            { ResolutionCandidates.Clear(); foreach (var c in candidates) ResolutionCandidates.Add(c); }
            Resolutions.Clear(); foreach (var r in history) Resolutions.Add(r);
            PositionHistory.Clear(); foreach (var p in positions) PositionHistory.Add(p);
            Performance = performance;
            SelectedBucket ??= performance.Buckets.FirstOrDefault();
            await RefreshCurveAsync();
            if (viewVersion == pollVersion) await RefreshValuationAsync();
            if (viewVersion == pollVersion) await RefreshRiskAsync();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context) Failure(e); }
    }
    [RelayCommand] private async Task RefreshCurveAsync()
    {
        if (Context() is not { } context || SettlementGeneration is not { } g || SelectedBucket is not { } b) return;
        var page = CurvePage;
        try
        {
            var r = await backend.PaperCurveAsync(context.Workspace, g.Id, b.Exchange, b.Currency, page, lifetime.Token);
            if (Context() != context || SettlementGeneration != g || SelectedBucket != b || CurvePage != page) return;
            CurvePoints.Clear(); foreach (var p in r.Points) CurvePoints.Add(p); CurveHasMore = r.HasMore;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context) Failure(e); }
    }
    [RelayCommand] private async Task PreviewResolutionAsync()
    {
        if (Busy || Context() is not { } context || SelectedCandidate is not { } c || WinningOutcome is not { } o || SettlementGeneration?.Id != c.GenerationId) return;
        ClearResolutionPreview(); var captured = settlementRevision; Busy = true;
        try
        {
            var result = await backend.ResolutionPreviewAsync(context.Workspace, new(c.GenerationId, c.Exchange, c.MarketId, o.InstrumentId), lifetime.Token);
            if (Context() != context || captured != settlementRevision) return;
            ResolutionPreview = result; Notice = result.WouldSettle ? "Review the Manual Scenario Resolution, then explicitly confirm." : result.RejectionReason;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == settlementRevision) Failure(e); }
        finally { Busy = false; }
    }
    private bool CanConfirmResolution() => !Busy && Context() is not null && ResolutionPreview?.WouldSettle == true;
    [RelayCommand(CanExecute = nameof(CanConfirmResolution))] private async Task ConfirmResolutionAsync()
    {
        if (Busy || Context() is not { } context || ResolutionPreview is not { WouldSettle: true } p) return;
        if (!Confirm("PAPER SIMULATION ONLY\nThis permanently settles the selected paper positions for this generation. It does NOT report or alter the real exchange.\nA confirmed scenario cannot be changed. Use another generation to test another outcome.\n" + DescribeResolution(p))) return;
        if (Context() != context || ResolutionPreview != p) return;
        pendingResolution ??= new(Guid.NewGuid(), p.PreviewId, p.Selection, true); var captured = settlementRevision; Busy = true;
        try
        {
            var r = await backend.ConfirmResolutionAsync(context.Workspace, pendingResolution, lifetime.Token);
            if (Context() != context || captured != settlementRevision) return;
            Notice = r.Resolution is null ? r.Rejection : r.Duplicate ? "Original paper resolution returned; no duplicate payout." : "Manual Scenario Resolution committed.";
            SelectedResolution = r.Resolution; ClearResolutionPreview();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == settlementRevision) { Failure(e); Notice += " Check history or retry the same confirmation; no automatic retry."; } }
        finally { Busy = false; }
        await RefreshAsync();
    }
    [RelayCommand] private async Task NextSettlementsAsync() { SettlementPage++; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task PreviousSettlementsAsync() { if (SettlementPage > 1) SettlementPage--; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task NextCandidatesAsync() { CandidatePage++; SelectedCandidate = null; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task PreviousCandidatesAsync() { if (CandidatePage > 1) CandidatePage--; SelectedCandidate = null; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task NextPositionsAsync() { PositionPage++; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task PreviousPositionsAsync() { if (PositionPage > 1) PositionPage--; await RefreshSettlementAsync(); }
    [RelayCommand] private async Task NextCurveAsync() { if (CurveHasMore) CurvePage++; await RefreshCurveAsync(); }
    [RelayCommand] private async Task PreviousCurveAsync() { if (CurvePage > 1) CurvePage--; await RefreshCurveAsync(); }
    public static string DescribeResolution(PaperResolutionPreviewResponse p) =>
        $"MANUAL PAPER SCENARIO · {(p.WouldSettle ? "Would settle" : p.RejectionReason)} · expires {p.ExpiresAt:O}\n" +
        string.Join("\n", p.PayoutVector.Select(o => $"{o.Outcome} ({o.InstrumentId}): payout {o.PayoutPerShare}/share")) + "\n" +
        string.Join("\n", p.CashCredits.Select(c => $"{c.Exchange} {c.Currency}: cash credit {c.Payout}; resulting cash {c.AvailableAfter}")) + "\n" +
        string.Join("\n", p.AffectedPositions.Select(v => $"{v.Outcome}: quantity {v.Quantity}; cost {v.CostBasis}; payout {v.Payout}; realized P&L {v.RealizedPnl} {v.Currency}")) + "\n" +
        string.Join("\n", p.AffectedExecutions.Select(e => $"Execution {e.ExecutionId}: {e.Economics.State}; realized P&L to date {e.Economics.RealizedPnlToDate}; final profit {e.Economics.FinalRealizedProfit?.ToString() ?? "pending"}; expected/actual difference {e.Economics.ExpectedVsRealizedDifference}")) + "\n" + string.Join("\n", p.Warnings);
}
