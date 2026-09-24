using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public sealed record ReliabilityCriterionRow(string Code, string ObservedValue, string RequiredValue, string State, string ExplanationCode);

public partial class PaperReliabilityViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state; private readonly BackendClient backend; private readonly CancellationTokenSource lifetime = new();
    private long revision, pollEpoch; private bool active, disposed;
    public Func<string, bool> Confirm { get; set; } = _ => false;
    public ObservableCollection<PaperReliabilityCampaignResponse> Campaigns { get; } = [];
    [ObservableProperty] private PaperReliabilityCampaignResponse? campaign;
    [ObservableProperty] private PaperReliabilityReportResponse? report;
    [ObservableProperty] private string name = "Paper reliability campaign";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string notice = "PAPER SIMULATION EVIDENCE ONLY. Start explicitly; criteria never enable live trading.";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private int historyPage = 1;
    public string Summary => Campaign is not { } c ? "No campaign. Suggested name is inactive." :
        $"{c.Name} · {c.State}\nStarted {c.StartedAt:O} · policy {c.PolicyVersion} · revision {c.Revision}\n" +
        $"Evidence: {(c.InvariantViolationDetected ? "InvariantViolation" : c.EvidenceGapDetected ? "InsufficientEvidence" : Report?.State ?? "InsufficientEvidence")} · gap: {c.EvidenceGapDetected || Report?.EvidenceGapDetected == true} · invariant violation: {c.InvariantViolationDetected}\n" +
        $"Last evaluation {Report?.EvaluatedAt:O}. Values are the last persisted evaluation, not live financial authority.";
    public string Runtime => Report is not { } r ? "Evaluate to obtain runtime evidence." : string.Join("\n", new[] { "BackendObservedTicks", "MonitoringRunningTicks", "AutomationArmedTicks", "AutomationHealthyTicks" }.Select(k => $"{k.Replace("Ticks", "", StringComparison.Ordinal)}: {TimeSpan.FromTicks(r.Counters.GetValueOrDefault(k))}"));
    public IReadOnlyList<ReliabilityCriterionRow> Criteria => (Report?.Criteria ?? []).Select(c => new ReliabilityCriterionRow(
        System.Text.RegularExpressions.Regex.Replace(c.Code.Replace("Ticks", " duration", StringComparison.Ordinal), "(?<=[a-z])(?=[A-Z])", " "),
        Display(c.Code, c.ObservedValue), Display(c.Code, c.RequiredValue), c.State, c.ExplanationCode)).ToArray();
    private static string Display(string code, decimal value) => code.EndsWith("Ticks", StringComparison.Ordinal) && value >= long.MinValue && value <= long.MaxValue
        ? TimeSpan.FromTicks((long)value).ToString("c", System.Globalization.CultureInfo.InvariantCulture) : value.ToString(System.Globalization.CultureInfo.CurrentCulture);
    partial void OnCampaignChanged(PaperReliabilityCampaignResponse? value) { revision++; Report = null; OnPropertyChanged(nameof(Summary)); }
    partial void OnReportChanged(PaperReliabilityReportResponse? value) { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Runtime)); OnPropertyChanged(nameof(Criteria)); }
    public PaperReliabilityViewModel(MainViewModel state, BackendClient backend)
    { this.state = state; this.backend = backend; state.AccessInvalidated += Clear; state.PropertyChanged += StateChanged; state.PaperValuationInvalidated += Invalidate; }
    private (Guid Workspace, long Access, string Backend)? Context() => !disposed && state.ConnectionStatus == "Connected" && state.HasSnapshot && Guid.TryParse(state.WorkspaceIdentifier, out var w) ? (w, state.AccessGeneration, state.BackendInstance) : null;
    private void Clear(object? sender, EventArgs e) { revision++; Campaign = null; Report = null; Campaigns.Clear(); Notice = "Private reliability data cleared. Refresh after access is restored."; }
    private void Invalidate(object? sender, EventArgs e) { revision++; Report = null; }
    private void StateChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.BackendInstance) || e.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus != "Connected") Clear(sender, EventArgs.Empty); }
    private void Failure(BackendFailure e) { Clear(null, EventArgs.Empty); Notice = e.Message; if (e.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(e.State.ToString(), e.Message); }
    public void Activate() { if (active || disposed) return; active = true; _ = PollAsync(++pollEpoch); }
    public void Deactivate() { active = false; pollEpoch++; Clear(null, EventArgs.Empty); }
    private async Task PollAsync(long epoch) { try { while (active && !disposed && epoch == pollEpoch) { await RefreshAsync(); await Task.Delay(5000, lifetime.Token); } } catch (OperationCanceledException) { } }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (Busy || Context() is not { } context) return; var captured = ++revision;
        try
        {
            var current = await backend.PaperReliabilityCurrentAsync(context.Workspace, lifetime.Token);
            var history = await backend.PaperReliabilityHistoryAsync(context.Workspace, HistoryPage, lifetime.Token);
            if (Context() != context || captured != revision) return;
            var selected = Campaign?.Id; Campaigns.Clear(); foreach (var c in history) Campaigns.Add(c);
            Campaign = selected == current.Campaign?.Id ? current.Campaign : history.FirstOrDefault(c => c.Id == selected) ?? current.Campaign;
            if (Campaign?.Id == current.Campaign?.Id) Report = current.Report;
            else await ReadReportAsync();
            if (current.TelemetryPersistenceFailures > 0) Notice = "Telemetry persistence failure observed; campaign evidence may be incomplete.";
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) Failure(e); }
    }
    [RelayCommand] private async Task ReadReportAsync()
    {
        if (Context() is not { } context || Campaign is not { } c) return; var captured = ++revision;
        try { var value = await backend.PaperReliabilityReportAsync(context.Workspace, c.Id, lifetime.Token); if (Context() == context && captured == revision && Campaign?.Id == c.Id) Report = value; }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) Failure(e); }
    }
    [RelayCommand] private async Task ActionAsync(string action)
    {
        if (Busy || Context() is not { } context) return;
        var c = Campaign;
        if (action != "start" && c is null) return;
        if (!Confirm($"{action} this PAPER reliability campaign? This records evidence only and does not arm, disarm or enable trading.")) return;
        if (Context() != context) return; var captured = ++revision; Busy = true;
        try
        {
            var result = await backend.PaperReliabilityActionAsync(context.Workspace, action, new(c?.Id, c?.Revision, Name, Notes), lifetime.Token);
            if (Context() == context && captured == revision) { Campaign = result; Notice = "Campaign action completed: " + action; await ReadReportAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) Failure(e); }
        finally { Busy = false; }
    }
    [RelayCommand] private async Task ExportAsync()
    {
        if (Context() is not { } context || Campaign is not { } c) return; var captured = ++revision;
        try { var result = await backend.PaperReliabilityExportAsync(context.Workspace, c.Id, lifetime.Token); if (Context() == context && captured == revision) Notice = "Report exported to " + result.Path; }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) Failure(e); }
    }
    [RelayCommand] private async Task NextHistoryAsync() { if (HistoryPage < 10000) HistoryPage++; await RefreshAsync(); }
    [RelayCommand] private async Task PreviousHistoryAsync() { if (HistoryPage > 1) HistoryPage--; await RefreshAsync(); }
    public void Dispose() { if (disposed) return; disposed = true; active = false; lifetime.Cancel(); lifetime.Dispose(); state.AccessInvalidated -= Clear; state.PropertyChanged -= StateChanged; state.PaperValuationInvalidated -= Invalidate; }
}
