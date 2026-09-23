using System.Collections.ObjectModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;
public partial class PaperTradingViewModel
{
    private long valuationRevision;
    private long markSelectionRevision;
    private void ValuationInvalidated(object? sender, EventArgs e) => ClearValuation();
    private void ValuationBookInvalidated(object? sender, OrderBookInvalidation e) => ClearValuation();
    public ObservableCollection<PaperPositionMarkResponse> Marks { get; } = [];
    [ObservableProperty] private PaperValuationResponse? valuation;
    [ObservableProperty] private PaperValuationBucketResponse? valuationBucket;
    [ObservableProperty] private PaperPositionMarkResponse? selectedMark;
    [ObservableProperty] private int valuationPage = 1;
    [ObservableProperty] private string valuationNotice = "Refresh Valuation recomputes local cached depth only. No market data is acquired.";
    public string MarkDetail => SelectedMark is not { } m ? "Select a position for source and partial-depth details." :
        $"{m.MarkStatus} · {m.Quality} · {m.Method}\n{m.Position.Exchange} / {m.Position.MarketId} / {m.Position.InstrumentId} / {m.Position.Outcome}\n" +
        $"Gross value {Show(m.GrossLiquidationValue)} · gross unrealized P&L {Show(m.GrossUnrealizedPnlBeforeExitFees)} · fee-adjusted value {Show(m.FeeAdjustedLiquidationValue)} · fee-adjusted unrealized P&L {Show(m.FeeAdjustedUnrealizedPnl)}\n" +
        $"Valued UTC {m.ValuedAt:O}; retrieved {m.BookRetrievedAt:O}; source {m.BookSourceTimestamp:O}\nVersion {m.BookVersion}; {m.SourceMode}/{m.Continuity}; age {m.BookAgeSeconds:0.###} seconds\n" +
        $"Executable {Show(m.ExecutableQuantity)}, unfilled {Show(m.UnfilledQuantity)}; partial gross {Show(m.PartialGrossLiquidationValue)}, partial VWAP {Show(m.PartialAveragePrice)}\n" +
        $"Historical entry fees {m.Position.Fees}; estimated exit fees {Show(m.EstimatedExitFees)} ({m.ExitFeeStatus})\n" +
        $"Settlement payout {Show(m.Position.SettlementPayout)}; realized P&L {Show(m.Position.RealizedPnl)}\n" + string.Join("\n", m.Warnings);
    public string ValuationSummary => ValuationBucket is not { } b ? "Current valuation unavailable." :
        $"{b.Exchange} · {b.Currency}\nCash {b.CurrentCash} · open cost basis {b.OpenCostBasis} · accounting book value {b.AccountingBookValue}\n" +
        $"Realized P&L {b.RealizedPnl} · gross marked equity {Show(b.GrossMarkedEquity)} · fee-adjusted marked equity {Show(b.FeeAdjustedMarkedEquity)}\n" +
        $"Gross total P&L {Show(b.GrossTotalPnl)} · fee-adjusted total P&L {Show(b.FeeAdjustedTotalPnl)}\n" +
        $"Coverage {b.FullyMarkedPositionCount}/{b.OpenPositionCount} ({b.ValuationCoverage:P0}) · partial {b.PartiallyMarkedPositionCount} · unavailable {b.UnavailablePositionCount}\n" +
        $"Capital utilization by cost {Show(b.CapitalUtilizationByCost)} · largest position cost {b.LargestPositionCostBasis} · concentration {Show(b.LargestPositionCostShare)} · markets {b.UniqueMarketCount}";
    private static string Show(decimal? value) => value?.ToString("0.########") ?? "Unavailable";
    partial void OnSelectedMarkChanged(PaperPositionMarkResponse? value) { markSelectionRevision++; OnPropertyChanged(nameof(MarkDetail)); }
    partial void OnValuationBucketChanged(PaperValuationBucketResponse? value) => OnPropertyChanged(nameof(ValuationSummary));
    private void ClearValuation()
    {
        valuationRevision++; Valuation = null; ValuationBucket = null; SelectedMark = null; Marks.Clear();
        ValuationNotice = "Current valuation unavailable; refresh local calculation.";
    }
    [RelayCommand] private async Task RefreshValuationAsync()
    {
        if (Context() is not { } context || SettlementGeneration is not { } generation) return;
        var selectedId = SelectedMark?.Position.Id; var selectedVenue = ValuationBucket?.Exchange; var selectedCurrency = ValuationBucket?.Currency;
        var page = ValuationPage; ClearValuation(); var captured = valuationRevision;
        var selectionVersion = markSelectionRevision;
        try
        {
            var result = await backend.PaperValuationAsync(context.Workspace, generation.Id, page, lifetime.Token);
            if (Context() != context || captured != valuationRevision || SettlementGeneration?.Id != generation.Id || page != ValuationPage) return;
            Valuation = result; foreach (var row in result.Positions) Marks.Add(row);
            ValuationBucket = result.Buckets.FirstOrDefault(b => b.Exchange == selectedVenue && b.Currency == selectedCurrency) ?? result.Buckets.FirstOrDefault();
            if (selectionVersion == markSelectionRevision) SelectedMark = Marks.FirstOrDefault(m => m.Position.Id == selectedId);
            ValuationNotice = $"{result.State} · current local snapshot {result.ValuedAt:O}. " + string.Join(" ", result.Warnings);
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (captured == valuationRevision && Context() == context) { ClearValuation(); Failure(e); ValuationNotice = e.Message; } }
    }
    [RelayCommand] private async Task NextValuationAsync() { if (Valuation?.HasMore == true) { ValuationPage++; await RefreshValuationAsync(); } }
    [RelayCommand] private async Task PreviousValuationAsync() { if (ValuationPage > 1) { ValuationPage--; await RefreshValuationAsync(); } }
}
