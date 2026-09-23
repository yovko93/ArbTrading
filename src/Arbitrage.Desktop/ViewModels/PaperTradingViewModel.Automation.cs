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
        $"Kill switch: {(s.KillSwitch.IsLatched ? "LATCHED" : "clear")} · {s.KillSwitch.Reason} · {s.KillSwitch.LatchedAt:O}\n" +
        string.Join("\n", s.SessionDebits.Select(d => $"Session gross entry debit: {d.Exchange} {d.Currency} {d.Total}"));
    partial void OnAutomationStatusChanged(PaperAutomationStatusResponse? value)
    {
        OnPropertyChanged(nameof(AutomationStatusText));
        ArmAutomationCommand.NotifyCanExecuteChanged(); SaveAutomationCommand.NotifyCanExecuteChanged(); ResetAutomationKillCommand.NotifyCanExecuteChanged();
    }
    private void ClearAutomation() { automationReadRevision++; AutomationStatus = null; AutomationNotice = "Refresh automatic paper status. Closing this view does not disarm the backend."; }
    private void AutomationInvalidated(object? sender, EventArgs e) => ClearAutomation();
    private void ResetAutomationForm()
    {
        string[] values = ["1", "0.005", "0.05", "10", "10", "1", "5", "60", "25", "20"];
        for (var i = 0; i < values.Length; i++) AutomationInputs[i].Text = values[i];
        AllowPolymarketBestEffort = false;
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
    }
    private bool CanSaveAutomation() => !Busy && Context() is not null && AutomationStatus is { State: not "Armed" };
    private bool CanArmAutomation() => !Busy && Context() is not null && AutomationStatus is { State: "Disarmed" or "Faulted", Profile: not null, RiskRevision: not null, GenerationId: not null, MonitoringState: "Running", KillSwitch.IsLatched: false };
    private bool CanResetAutomationKill() => !Busy && Context() is not null && AutomationStatus is { State: not "Armed", KillSwitch.IsLatched: true };
    [RelayCommand(CanExecute = nameof(CanSaveAutomation))] private async Task SaveAutomationAsync()
    {
        if (!CanSaveAutomation() || Context() is not { } context) return;
        var values = new decimal[10];
        for (var i = 0; i < values.Length; i++)
            if (!decimal.TryParse(AutomationInputs[i].Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
            { AutomationNotice = "Use plain decimal numbers with a dot and no grouping separators."; return; }
        int[] counts = [3, 4, 5, 6, 7, 9];
        if (counts.Any(i => values[i] != decimal.Truncate(values[i]) || values[i] < 1 || values[i] > 86400)) { AutomationNotice = "Counts and seconds must be bounded positive whole numbers."; return; }
        var expected = AutomationStatus!.Profile?.Revision;
        var settings = new PaperAutomationSettingsRequest("FixedQuantity", values[0], values[1], values[2], (int)values[3], (int)values[4],
            (int)values[5], (int)values[6], (int)values[7], values[8] / 100, (int)values[9], true, AllowPolymarketBestEffort);
        if (!Confirm("Save inactive automatic PAPER execution settings? Only FixedQuantity is supported.\n" + string.Join("\n", AutomationInputs.Select(i => i.Name + ": " + i.Text)) +
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
            $"Arm AUTOMATIC PAPER EXECUTION now? Eligible current opportunities may execute immediately, without confirmation for each entry.\nNo real orders are submitted.\nFixed quantity: {s.Profile.Settings.FixedQuantity}; session cap: {s.Profile.Settings.MaximumExecutionsPerSession}; hourly cap: {s.Profile.Settings.MaximumExecutionsPerHour}; session debit fraction per bucket: {s.Profile.Settings.MaximumSessionDebitFractionPerBucket}.\n" +
            $"Profile {s.Profile.Revision}; risk {s.RiskRevision}; generation {s.GenerationId}.\nPolymarket BestEffort allowed: {s.Profile.Settings.AllowPolymarketBestEffort} (weaker continuity).\nClosing WPF, navigating away, or losing its connection does NOT disarm the backend. Use Disarm or Emergency stop. Backend restart requires explicit rearming.");
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
