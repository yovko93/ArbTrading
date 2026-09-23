using System.Collections.ObjectModel;
using System.Globalization;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public sealed partial class PaperRiskInput(string name, string value) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string text = value;
}
public partial class PaperTradingViewModel
{
    private long riskReadRevision;
    private void RiskInvalidated(object? sender, EventArgs e) { ClearRisk(); ClearPreview(); }
    private void ResetRiskForm()
    {
        string[] values = ["20", "10", "60", "20", "20", "20", "10", "2", "1000", "0.001", "0"];
        for (var i = 0; i < values.Length; i++) RiskInputs[i].Text = values[i];
    }
    [ObservableProperty] private PaperRiskStatusResponse? riskStatus;
    [ObservableProperty] private string riskNotice = "Not configured. Suggested form values are inactive until explicitly saved.";
    public ObservableCollection<PaperRiskInput> RiskInputs { get; } = [new("Cash reserve %", "20"), new("Max single execution %", "10"),
        new("Max total open cost %", "60"), new("Max market exposure %", "20"), new("Max instrument exposure %", "20"),
        new("Max open positions", "20"), new("Max open executions", "10"), new("Max executions / relationship", "2"),
        new("Max requested quantity", "1000"), new("Minimum fee-adjusted edge", "0.001"), new("Minimum fee-adjusted profit", "0")];
    public string RiskStatusText => RiskStatus is not { } s ? RiskNotice : $"{s.State} · policy revision {s.Policy?.Revision}\n" + RiskDescription(s.Assessment);
    partial void OnRiskStatusChanged(PaperRiskStatusResponse? value) => OnPropertyChanged(nameof(RiskStatusText));
    partial void OnRiskNoticeChanged(string value) => OnPropertyChanged(nameof(RiskStatusText));
    private void ClearRisk() { riskReadRevision++; RiskStatus = null; RiskNotice = "Risk state unavailable; refresh local policy."; }
    public static string RiskDescription(PaperRiskDecisionResponse? d) => d is null ? "Risk decision unavailable." :
        $"Risk: {d.Decision} · revision {d.PolicyRevision}\nPositions {d.ProjectedOpenPositions}; open executions {d.ProjectedOpenExecutions}; relationship {d.ProjectedRelationshipOpenExecutions}\n" +
        string.Join("\n", d.BucketAssessments.Select(b => $"{b.Exchange} {b.Currency}: cash after {b.ProjectedAvailableCash}; reserve {b.RequiredCashReserve}; open cost {b.ProjectedOpenCostBasis} / {b.MaximumOpenCostBasis}")) + "\n" +
        string.Join("\n", d.Exposures.Where(e => e.ProposedDelta != 0).Select(e => $"{e.Exchange} {e.Currency} {e.MarketId}/{e.InstrumentId}: current {e.CurrentValue}, proposed +{e.ProposedDelta}, projected {e.ProjectedValue} / {e.LimitValue}; headroom {e.RemainingHeadroom}")) + "\n" +
        string.Join("\n", d.Violations.Select(v => $"{v.Code} {v.Exchange} {v.Currency} {v.MarketId}/{v.InstrumentId}: current {v.CurrentValue}, proposed {v.ProposedDelta}, projected {v.ProjectedValue}, limit {v.LimitValue}, headroom {v.RemainingHeadroom}"));
    [RelayCommand] private async Task RefreshRiskAsync()
    {
        if (Context() is not { } context) return;
        var version = ++riskReadRevision; var view = pollVersion; var generation = SettlementGeneration?.Id;
        try
        {
            var r = await backend.PaperRiskStatusAsync(context.Workspace, generation, lifetime.Token);
            if (Context() != context || version != riskReadRevision || view != pollVersion || generation != SettlementGeneration?.Id) return;
            if (Preview?.RiskDecision is { } reviewed && (reviewed.PolicyRevision != r.Policy?.Revision || reviewed.FinancialRevision != r.Assessment.FinancialRevision)) ClearPreview();
            RiskStatus = r;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && version == riskReadRevision) { ClearRisk(); Failure(e); RiskNotice = e.Message; } }
    }
    [RelayCommand] private void LoadRiskPolicy()
    {
        if (RiskStatus?.Policy?.Limits is not { } l) return;
        decimal[] values = [l.MinimumCashReserveFraction * 100, l.MaximumSingleExecutionDebitFraction * 100, l.MaximumOpenCostBasisFraction * 100,
            l.MaximumMarketCostBasisFraction * 100, l.MaximumInstrumentCostBasisFraction * 100, l.MaximumOpenPositions, l.MaximumOpenExecutions,
            l.MaximumOpenExecutionsPerRelationship, l.MaximumRequestedQuantity, l.MinimumFeeAdjustedEdgePerShare, l.MinimumFeeAdjustedProfit];
        for (var i = 0; i < values.Length; i++) RiskInputs[i].Text = values[i].ToString("G29", CultureInfo.InvariantCulture);
    }
    [RelayCommand] private async Task SaveRiskPolicyAsync()
    {
        if (Busy || Context() is not { } context || RiskStatus is null) return;
        var values = new decimal[11];
        for (var i = 0; i < values.Length; i++)
            if (!decimal.TryParse(RiskInputs[i].Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out values[i])) { RiskNotice = "Use plain decimal numbers with a dot; no grouping separators."; return; }
        if (values.Skip(5).Take(3).Any(v => v != decimal.Truncate(v) || v < 1 || v > 1000)) { RiskNotice = "Count limits must be whole numbers from 1 to 1000."; return; }
        var limits = new PaperRiskLimitsRequest(values[0] / 100m, values[1] / 100m, values[2] / 100m, values[3] / 100m, values[4] / 100m,
            (int)values[5], (int)values[6], (int)values[7], values[8], values[9], values[10]);
        var expected = RiskStatus.Policy?.Revision;
        if (!Confirm("Save paper risk policy for NEW entries? Existing positions and balances are unchanged.\n" +
            string.Join("\n", RiskInputs.Select(i => i.Name + ": " + i.Text)))) return;
        if (Context() != context) return;
        Busy = true; ClearPreview(); var view = pollVersion; var captured = ++riskReadRevision;
        try
        {
            await backend.SavePaperRiskPolicyAsync(context.Workspace, new(expected, true, limits), lifetime.Token);
            if (Context() == context && view == pollVersion && captured == riskReadRevision) { RiskNotice = "Paper risk policy saved."; await RefreshRiskAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && view == pollVersion && captured == riskReadRevision) { Failure(e); RiskNotice = "Policy not saved. Refresh before another attempt. " + e.Message; } }
        finally { Busy = false; }
    }
}
