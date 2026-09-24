using System.Collections.ObjectModel;
using System.Globalization;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class PaperTradingViewModel
{
    private long automationReadRevision;
    private long sizingReadRevision;
    [ObservableProperty] private string automationSizingMode = "FixedQuantity";
    [ObservableProperty] private string adaptiveMinimum = "1";
    [ObservableProperty] private string adaptiveMaximum = "25";
    [ObservableProperty] private string adaptiveStep = "1";
    [ObservableProperty] private PaperSizingPreviewResponse? sizingPreview;
    public bool IsAdaptiveSizing => AutomationSizingMode == "LargestAdmissibleGridQuantity";
    public bool IsFixedSizing => AutomationSizingMode == "FixedQuantity";
    public IEnumerable<PaperRiskInput> AutomationCommonInputs => AutomationInputs.Skip(1);
    partial void OnAutomationSizingModeChanged(string value) { OnPropertyChanged(nameof(IsAdaptiveSizing)); OnPropertyChanged(nameof(IsFixedSizing)); ClearSizingPreview(); }
    partial void OnSizingPreviewChanged(PaperSizingPreviewResponse? value) => OnPropertyChanged(nameof(SizingPreviewText));
    private void ClearSizingPreview() { sizingReadRevision++; SizingPreview = null; }
    private void SizingInvalidated(object? sender, EventArgs e) => ClearSizingPreview();
    private void SizingBookInvalidated(object? sender, OrderBookInvalidation e) => ClearSizingPreview();
    partial void OnAutomationStatusChanging(PaperAutomationStatusResponse? value)
    { if (value?.Profile?.Revision != AutomationStatus?.Profile?.Revision) ClearSizingPreview(); }
    public string SizingPreviewText => SizingPreview is not { } p ? "Preview uses the saved profile and selected opportunity. Diagnostic only; it neither arms nor creates an execution ticket." :
        $"Saved-profile diagnostic · {p.State} · {p.Mode}\nSelected quantity {p.SelectedQuantity}; evaluated {p.CandidatesEvaluated}; grid {p.LowestConfiguredQuantity}..{p.HighestConfiguredQuantity} step {p.QuantityStep}\n" +
        $"Exact cost {p.Cost}; fees {p.TotalFees}; payout {p.ExpectedPayout}; profit {p.ExpectedProfit}; edge/share {p.EdgePerShare}\n" +
        string.Join("; ", p.Rejections.Select(r => r.Reason + ": " + r.Count)) + "\nDiagnostic only. Financial and market inputs can change before execution.";
    [ObservableProperty] private PaperAutomationStatusResponse? automationStatus;
    [ObservableProperty] private bool allowPolymarketBestEffort;
    [ObservableProperty] private string automationNotice = "Not configured. Suggested values are inactive until saved. Backend restart always starts disarmed.";
    public ObservableCollection<PaperRiskInput> AutomationInputs { get; } = [new("Fixed quantity", "1"), new("Minimum edge per share", "0.005"),
        new("Minimum profit", "0.05"), new("Max executions / session", "10"), new("Max executions / hour", "10"),
        new("Max executions / relationship / session", "1"), new("Minimum seconds / opportunity", "5"), new("Relationship cooldown seconds", "60"),
        new("Session debit cap % per bucket", "25"), new("Max candidates / cycle", "20")];
    public string AutomationStatusText => AutomationStatus is not { } s ? "Automatic paper state unavailable. Refresh before sending a command." :
        $"{s.State} · {s.StopReason}\nProfile {s.Profile?.Revision} · risk {s.RiskRevision} · generation {s.GenerationId}\n" +
        $"Session {s.SessionId} · armed {s.ArmedAt:O} by {s.ArmedBy} · monitoring {s.MonitoringState}\n" +
        $"Committed {s.ExecutionsCommitted}; considered {s.CandidatesConsidered}; skipped {s.CandidatesSkipped}; rejected {s.ExecutionsRejected}; queued {s.QueueDepth}\n" +
        $"Last activity {s.LastActivityAt:O}; last commit {s.LastCommittedAt:O}\n" +
        $"Last sizing {s.LastSizingState}; selected {s.LastSelectedQuantity}; evaluated {s.LastSizingCandidatesEvaluated}\n" +
        $"Kill switch: {(s.KillSwitch.IsLatched ? "LATCHED" : "clear")} · {s.KillSwitch.Reason} · {s.KillSwitch.LatchedAt:O}\n" +
        string.Join("\n", s.SessionDebits.Select(d => $"Session gross entry debit: {d.Exchange} {d.Currency} {d.Total}"));
    partial void OnAutomationStatusChanged(PaperAutomationStatusResponse? value)
    {
        OnPropertyChanged(nameof(AutomationStatusText));
        ArmAutomationCommand.NotifyCanExecuteChanged(); SaveAutomationCommand.NotifyCanExecuteChanged(); ResetAutomationKillCommand.NotifyCanExecuteChanged();
    }
    private void ClearAutomation() { automationReadRevision++; ClearSizingPreview(); AutomationStatus = null; AutomationNotice = "Refresh automatic paper status. Closing this view does not disarm the backend."; }
    private void AutomationInvalidated(object? sender, EventArgs e) => ClearAutomation();
    private void ResetAutomationForm()
    {
        string[] values = ["1", "0.005", "0.05", "10", "10", "1", "5", "60", "25", "20"];
        for (var i = 0; i < values.Length; i++) AutomationInputs[i].Text = values[i];
        AllowPolymarketBestEffort = false;
        AutomationSizingMode = "FixedQuantity"; AdaptiveMinimum = "1"; AdaptiveMaximum = "25"; AdaptiveStep = "1";
    }
    [RelayCommand] private async Task RefreshAutomationAsync()
    {
        if (Context() is not { } context) return;
        var request = ++automationReadRevision; var view = pollVersion;
        try
        {
            var response = await backend.PaperAutomationStatusAsync(context.Workspace, lifetime.Token);
            if (Context() == context && request == automationReadRevision && view == pollVersion) AutomationStatus = response;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && request == automationReadRevision) { ClearAutomation(); Failure(e); AutomationNotice = e.Message; } }
    }
    [RelayCommand] private void LoadAutomationProfile()
    {
        if (AutomationStatus?.Profile?.Settings is not { } p) return;
        decimal[] values = [p.FixedQuantity, p.MinimumFeeAdjustedEdgePerShare, p.MinimumFeeAdjustedProfit, p.MaximumExecutionsPerSession,
            p.MaximumExecutionsPerHour, p.MaximumExecutionsPerRelationshipPerSession, p.MinimumSecondsBetweenExecutions, p.RelationshipCooldownSeconds,
            p.MaximumSessionDebitFractionPerBucket * 100, p.MaximumCandidatesPerCycle];
        for (var i = 0; i < values.Length; i++) AutomationInputs[i].Text = values[i].ToString("G29", CultureInfo.InvariantCulture);
        AllowPolymarketBestEffort = p.AllowPolymarketBestEffort;
        AutomationSizingMode = p.SizingMode;
        AdaptiveMinimum = p.MinimumQuantity?.ToString("G29", CultureInfo.InvariantCulture) ?? "1";
        AdaptiveMaximum = p.MaximumQuantity?.ToString("G29", CultureInfo.InvariantCulture) ?? "25";
        AdaptiveStep = p.QuantityStep?.ToString("G29", CultureInfo.InvariantCulture) ?? "1";
    }
    private bool CanSaveAutomation() => !Busy && Context() is not null && AutomationStatus is { State: not "Armed" };
    private bool CanArmAutomation() => !Busy && Context() is not null && AutomationStatus is { State: "Disarmed" or "Faulted", Profile: not null, RiskRevision: not null, GenerationId: not null, MonitoringState: "Running", KillSwitch.IsLatched: false };
    private bool CanResetAutomationKill() => !Busy && Context() is not null && AutomationStatus is { State: not "Armed", KillSwitch.IsLatched: true };
    [RelayCommand(CanExecute = nameof(CanSaveAutomation))] private async Task SaveAutomationAsync()
    {
        if (!CanSaveAutomation() || Context() is not { } context) return;
        var values = new decimal[10];
        values[0] = 1;
        for (var i = IsAdaptiveSizing ? 1 : 0; i < values.Length; i++)
            if (!decimal.TryParse(AutomationInputs[i].Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
            { AutomationNotice = "Use plain decimal numbers with a dot and no grouping separators."; return; }
        int[] counts = [3, 4, 5, 6, 7, 9];
        if (counts.Any(i => values[i] != decimal.Truncate(values[i]) || values[i] < 1 || values[i] > 86400)) { AutomationNotice = "Counts and seconds must be bounded positive whole numbers."; return; }
        var expected = AutomationStatus!.Profile?.Revision;
        decimal? min = null, max = null, step = null;
        if (IsAdaptiveSizing)
        {
            if (!TryAdaptiveGrid(out var a, out var b, out var c)) { AutomationNotice = "Use positive plain decimals: minimum ≤ maximum ≤ 1000, positive step, at most 256 grid points."; return; }
            min = a; max = b; step = c;
        }
        else if (!IsFixedSizing) { AutomationNotice = "Choose a supported sizing mode."; return; }
        var settings = new PaperAutomationSettingsRequest(AutomationSizingMode, values[0], values[1], values[2], (int)values[3], (int)values[4],
            (int)values[5], (int)values[6], (int)values[7], values[8] / 100, (int)values[9], true, AllowPolymarketBestEffort, min, max, step);
        if (!Confirm("Save inactive automatic PAPER execution settings?\n" + SizingDescription(settings) + "\n" + string.Join("\n", AutomationCommonInputs.Select(i => i.Name + ": " + i.Text)) +
            (AllowPolymarketBestEffort ? "\nI acknowledge Polymarket BestEffort has weaker continuity guarantees than Kalshi Continuous." : "\nPolymarket BestEffort excluded."))) return;
        if (Context() != context) return;
        Busy = true; var view = pollVersion;
        try { await backend.SavePaperAutomationAsync(context.Workspace, new(expected, true, settings), lifetime.Token); if (Context() == context && view == pollVersion) { AutomationNotice = "Profile saved. Explicit arm is still required."; await RefreshAutomationAsync(); } }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && view == pollVersion) { ClearAutomation(); Failure(e); AutomationNotice = "Save failed. Refresh before another attempt. " + e.Message; } }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanArmAutomation))] private async Task ArmAutomationAsync()
    {
        if (!CanArmAutomation() || AutomationStatus is not { } s) return;
        await AutomationActionAsync("arm", new ArmPaperAutomationRequest(s.Profile!.Revision, s.RiskRevision!.Value, s.GenerationId!.Value, s.KillSwitch.Revision, true),
            $"Arm AUTOMATIC PAPER EXECUTION now? Eligible current opportunities may execute immediately, without confirmation for each entry.\nNo real orders are submitted.\n{SizingDescription(s.Profile.Settings)}\nMinimum edge/share {s.Profile.Settings.MinimumFeeAdjustedEdgePerShare}; minimum profit {s.Profile.Settings.MinimumFeeAdjustedProfit}; session cap: {s.Profile.Settings.MaximumExecutionsPerSession}; hourly cap: {s.Profile.Settings.MaximumExecutionsPerHour}; session debit fraction per bucket: {s.Profile.Settings.MaximumSessionDebitFractionPerBucket}.\n" +
            $"Profile {s.Profile.Revision}; risk {s.RiskRevision}; generation {s.GenerationId}.\nPolymarket BestEffort allowed: {s.Profile.Settings.AllowPolymarketBestEffort} (weaker continuity).\nClosing WPF, navigating away, or losing its connection does NOT disarm the backend. Use Disarm or Emergency stop. Backend restart requires explicit rearming.");
    }
    private static string SizingDescription(PaperAutomationSettingsRequest p) => p.SizingMode == "FixedQuantity" ? $"Fixed quantity: {p.FixedQuantity}" :
        $"Largest admissible grid quantity: {p.MinimumQuantity}..{p.MaximumQuantity}, step {p.QuantityStep}; maximum 256 candidates. Actual quantity may vary with current local paper risk and cached market depth. Paper simulation only; no portfolio optimization or real-fund sizing.";
    private bool TryAdaptiveGrid(out decimal min, out decimal max, out decimal step)
    {
        min = max = step = 0;
        const NumberStyles style = NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(AdaptiveMinimum, style, CultureInfo.InvariantCulture, out min) ||
            !decimal.TryParse(AdaptiveMaximum, style, CultureInfo.InvariantCulture, out max) ||
            !decimal.TryParse(AdaptiveStep, style, CultureInfo.InvariantCulture, out step) || min <= 0 || max < min || max > 1000 || step <= 0) return false;
        decimal previous = 0;
        for (var n = 0; n <= 256; n++)
        {
            decimal q; try { q = checked(min + checked(n * step)); } catch (OverflowException) { return true; }
            if (q > max) return true;
            if (n == 256 || q <= previous) return false;
            previous = q;
        }
        return false;
    }
    [RelayCommand] private async Task PreviewSizingAsync()
    {
        if (Context() is not { } context || OpportunityKey.Length != 64) { AutomationNotice = "Select a current opportunity before requesting sizing diagnostics."; return; }
        var request = ++sizingReadRevision; var view = pollVersion; var key = OpportunityKey;
        try
        {
            var response = await backend.PaperSizingPreviewAsync(context.Workspace, new(key), lifetime.Token);
            if (Context() == context && view == pollVersion && request == sizingReadRevision && OpportunityKey == key) SizingPreview = response;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && request == sizingReadRevision) { ClearSizingPreview(); Failure(e); AutomationNotice = e.Message; } }
    }
    [RelayCommand] private Task DisarmAutomationAsync() => AutomationActionAsync("disarm", null, null);
    [RelayCommand] private Task EmergencyStopAutomationAsync() => AutomationActionAsync("emergency-stop",
        new PaperAutomationControlRequest(AutomationStatus?.KillSwitch.Revision, false, "Owner requested emergency stop"), null);
    [RelayCommand(CanExecute = nameof(CanResetAutomationKill))] private Task ResetAutomationKillAsync() => !CanResetAutomationKill() ? Task.CompletedTask : AutomationActionAsync("reset-kill-switch",
        new PaperAutomationControlRequest(AutomationStatus!.KillSwitch.Revision, true, "Owner explicitly reset kill switch"), "Reset the persistent paper kill switch? This does not rearm. A separate explicit Arm action is required.");
    private async Task AutomationActionAsync(string action, object? request, string? confirmation)
    {
        // Stop controls remain available while another desktop operation is pending.
        if (Context() is not { } context) return;
        if (confirmation is not null && !Confirm(confirmation) || Context() != context) return;
        var captured = ++automationReadRevision; var view = pollVersion;
        try
        {
            var response = await backend.ControlPaperAutomationAsync(context.Workspace, action, request, lifetime.Token);
            if (Context() == context && view == pollVersion && captured == automationReadRevision) { AutomationStatus = response; AutomationNotice = "Command completed: " + action; }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && view == pollVersion) { ClearAutomation(); Failure(e); AutomationNotice = "Command result unavailable. Refresh status before another attempt; no automatic retry. " + e.Message; } }
    }
}
