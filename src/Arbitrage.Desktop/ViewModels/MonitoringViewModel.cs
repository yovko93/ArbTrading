using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class MonitoringViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private CancellationTokenSource lifetime = new();
    private bool active, disposed, fetching, dirty = true;
    private long generation, readGeneration;
    private DateTimeOffset lastRead;
    public ObservableCollection<MonitoringRankingResponse> Items { get; } = [];
    public ObservableCollection<MonitoringAlertResponse> Alerts { get; } = [];
    public Action<OpportunityResponse>? PaperRequested { get; set; }
    public string PaperReason => PaperTradingViewModel.EligibilityReason(Selected?.Opportunity);
    private bool CanPaperPreview() => PaperRequested is not null && PaperTradingViewModel.Eligible(Selected?.Opportunity);
    [RelayCommand(CanExecute = nameof(CanPaperPreview))] private void PaperPreview() { if (Selected is { } r) PaperRequested?.Invoke(r.Opportunity); }
    public string[] Lanes { get; } = ["All", "FeeAdjusted", "GrossOnly", "NearEdge", "Blocked"];
    public string[] Sorts { get; } = ["default", "grossProfit", "grossEdge", "feeAdjustedProfit", "feeAdjustedEdge", "quantity", "nearEdgeDistance", "updated"];
    public string[] Strategies { get; } = ["All", "CrossMarketBuyBothComplements", "CrossMarketSameOutcomeSpread", "SingleMarketBinaryComplement"];
    public string[] Exchanges { get; } = ["All", "Kalshi", "Polymarket"];
    public string[] Trusts { get; } = ["All", "Deterministic", "Manual"];
    public string[] Qualities { get; } = ["All", "RealtimeContinuous", "FreshRest", "Mixed", "RealtimeBestEffort", "NonActionable"];
    [ObservableProperty] private string strategy = "All";
    [ObservableProperty] private string exchange = "All";
    [ObservableProperty] private string trust = "All";
    [ObservableProperty] private string quality = "All";
    [ObservableProperty] private string feeStatus = "";
    [ObservableProperty] private string lane = "All";
    [ObservableProperty] private string sort = "default";
    [ObservableProperty] private MonitoringProfileResponse profile = new();
    [ObservableProperty] private MonitoringStatusResponse? status;
    [ObservableProperty] private MonitoringRankingResponse? selected;
    [ObservableProperty] private MonitoringAlertResponse? selectedAlert;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private int alertPage = 1;
    [ObservableProperty] private int alertTotal;
    [ObservableProperty] private string notice = "Stopped by default. Start consumes local cached inputs only; it does not fetch or subscribe.";
    public string CoverageText => Status is not { } s ? "Monitoring status unavailable" :
        $"{s.State} · {s.Coverage.RelationshipsMonitored}/{s.Coverage.ApprovedRelationshipsAvailable} approved relationships · skipped {s.Coverage.RelationshipsSkippedByBound} · partial coverage: {s.Coverage.CoveragePartial}\n" +
        $"Plans {s.Coverage.PlansBuilt} · books {s.Coverage.PlansWithBooksAvailable} · actionable {s.Coverage.PlansWithActionableBooks} · resolved fees {s.Coverage.PlansWithResolvedFees} · queue {s.DirtyQueueDepth} · CSV {s.CsvExportStatus} {s.LastCsvErrorCode}\n" +
        $"Fee-adjusted {s.Coverage.FeeAdjustedOpportunities} · gross-only {s.Coverage.GrossOnlyOpportunities} · near-edge {s.Coverage.NearEdgeCandidates} · blocked {s.Coverage.BlockedCandidates} · last evaluation {s.LastEvaluationAt:O}";
    public string PageLabel => $"Page {Page} · {Total} current rows";
    public string AlertPageLabel => $"Page {AlertPage} · {AlertTotal} historical alerts";
    public string Detail => Selected is not { } r ? "Select a current row or inspect a historical alert's current opportunity." :
        $"{r.Lane} · {r.Opportunity.Strategy} · {r.Opportunity.RelationshipTrust} trust\n{r.Opportunity.SourceTitle}\n{r.Opportunity.TargetTitle}\n" +
        $"Key {r.Opportunity.OpportunityKey}\n{r.Opportunity.Status} · {r.Opportunity.InputQuality} · {r.AlertState}\n" +
        $"Best edge {r.BestEdge?.ToString() ?? "Unknown"} · required {r.RequiredEdge?.ToString() ?? "Unknown"} · distance {r.Distance?.ToString() ?? "Unknown"}\n" +
        string.Join("\n", r.Opportunity.Blockers.Concat(r.Opportunity.Warnings)) + "\nModeled read-only estimate. Net profit unknown; live execution unavailable. Paper preview requires explicit confirmation.";
    public MonitoringViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessChanged; state.PropertyChanged += StateChanged; state.MonitoringInvalidated += Invalidated;
    }
    private (Guid Workspace, long Access, string Instance, long Generation)? Context() => active && !disposed && state.ConnectionStatus == "Connected" && state.HasSnapshot && Guid.TryParse(state.WorkspaceIdentifier, out var id)
        ? (id, state.AccessGeneration, state.BackendInstance, generation) : null;
    public void Activate() { if (active || disposed) return; active = true; dirty = true; Observe(PollAsync(lifetime.Token)); }
    public void Deactivate() { active = false; Reset(); }
    private void Reset()
    {
        generation++; readGeneration++; lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); fetching = false; dirty = true;
        Items.Clear(); Alerts.Clear(); Selected = null; SelectedAlert = null; Status = null; Total = AlertTotal = 0; Profile = new();
    }
    private void AccessChanged(object? sender, EventArgs e) { Reset(); Notice = "Access changed; monitoring display cleared."; if (active) Observe(PollAsync(lifetime.Token)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.ConnectionStatus) or nameof(MainViewModel.BackendInstance)) { if (state.ConnectionStatus != "Connected") AccessChanged(sender, EventArgs.Empty); else dirty = true; } }
    private void Invalidated(object? sender, MonitoringInvalidation e)
    { if (Context() is { } c && e.WorkspaceId == c.Workspace && e.InstanceId.ToString() == c.Instance) dirty = true; }
    private async Task PollAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested && active) { if (dirty || DateTimeOffset.UtcNow - lastRead > TimeSpan.FromSeconds(5)) await RefreshAsync(); await Task.Delay(500, ct); } }
        catch (OperationCanceledException) { }
    }
    [RelayCommand] public async Task RefreshAsync()
    {
        if (fetching || Context() is not { } context) return;
        fetching = true; var read = ++readGeneration; dirty = false;
        try
        {
            var s = await backend.MonitoringStatusAsync(context.Workspace, null, lifetime.Token);
            var filters = string.Concat(new[] { ("strategy", Strategy), ("exchange", Exchange), ("trust", Trust), ("quality", Quality), ("feeStatus", FeeStatus) }
                .Where(x => x.Item2 != "All" && !string.IsNullOrWhiteSpace(x.Item2)).Select(x => "&" + x.Item1 + "=" + Uri.EscapeDataString(x.Item2)));
            var rows = await backend.MonitoringRankingsAsync(context.Workspace, Page, Lane, Sort, lifetime.Token, filters);
            var alerts = await backend.MonitoringAlertsAsync(context.Workspace, AlertPage, lifetime.Token);
            var p = Status is null ? await backend.MonitoringProfileAsync(context.Workspace, null, lifetime.Token) : null;
            if (Context() != context || read != readGeneration) return;
            Status = s; if (p is not null) { Profile = p; Sort = p.Sort; }
            var key = Selected?.Opportunity.OpportunityKey; Items.Clear(); foreach (var row in rows.Items) Items.Add(row); Total = rows.Total;
            Selected = Items.FirstOrDefault(x => x.Opportunity.OpportunityKey == key);
            Alerts.Clear(); foreach (var alert in alerts.Items) Alerts.Add(alert); AlertTotal = alerts.Total; lastRead = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure f) { if (Context() == context) Failure(f); }
        finally { if (Context() == context) fetching = false; }
    }
    private async Task MutationAsync(string? action)
    {
        if (Context() is not { } context) return;
        try
        {
            if (action is null) { var p = await backend.MonitoringProfileAsync(context.Workspace, Profile with { Sort = Sort }, lifetime.Token); if (Context() == context) Profile = p; }
            else { var s = await backend.MonitoringStatusAsync(context.Workspace, action, lifetime.Token); if (Context() == context) Status = s; }
            if (Context() == context) { InvalidateRead(); Notice = action == "stop" ? "Stop requested. The backend settles in-flight monitoring work." : "Request accepted. Monitoring is read-only and backend-owned; closing this page does not stop it."; }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure f) { if (Context() == context) Failure(f); }
    }
    [RelayCommand] private Task StartAsync() => MutationAsync("start");
    [RelayCommand] private Task StopAsync() => MutationAsync("stop");
    [RelayCommand] private Task SaveProfileAsync() => MutationAsync(null);
    [RelayCommand] private async Task InspectAlertAsync()
    {
        if (Context() is not { } context || SelectedAlert is not { } alert) return;
        try { var current = await backend.MonitoringCurrentAsync(context.Workspace, alert.Opportunity.Opportunity.OpportunityKey, lifetime.Token); if (Context() == context && SelectedAlert == alert) Selected = current; }
        catch (OperationCanceledException) { }
        catch (BackendFailure) { if (Context() == context) { Selected = null; Notice = "This historical alert has no available current opportunity. Its triggered-time values are not current."; } }
    }
    [RelayCommand] private void Next() { if (Page * 20 < Total) Page++; }
    [RelayCommand] private void Previous() { if (Page > 1) Page--; }
    [RelayCommand] private void NextAlerts() { if (AlertPage * 20 < AlertTotal) AlertPage++; }
    [RelayCommand] private void PreviousAlerts() { if (AlertPage > 1) AlertPage--; }
    partial void OnLaneChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnSortChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnStrategyChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnExchangeChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnTrustChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnQualityChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnFeeStatusChanged(string value) { Page = 1; InvalidateRead(); }
    partial void OnPageChanged(int value) { InvalidateRead(); OnPropertyChanged(nameof(PageLabel)); }
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(PageLabel));
    partial void OnAlertPageChanged(int value) { InvalidateRead(); OnPropertyChanged(nameof(AlertPageLabel)); }
    partial void OnAlertTotalChanged(int value) => OnPropertyChanged(nameof(AlertPageLabel));
    partial void OnStatusChanged(MonitoringStatusResponse? value) => OnPropertyChanged(nameof(CoverageText));
    partial void OnSelectedChanged(MonitoringRankingResponse? value) { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(PaperReason)); PaperPreviewCommand.NotifyCanExecuteChanged(); }
    private void InvalidateRead() { readGeneration++; dirty = true; Items.Clear(); Selected = null; }
    private void Failure(BackendFailure f)
    {
        Items.Clear(); Selected = null; Notice = "Monitoring request failed; displayed current rankings cleared. Retry explicitly.";
        if (f.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(f.State.ToString(), f.Message);
    }
    private static async void Observe(Task task) { try { await task; } catch (Exception) { /* Event work observed; never replay mutations. */ } }
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; lifetime.Dispose(); state.AccessInvalidated -= AccessChanged; state.PropertyChanged -= StateChanged; state.MonitoringInvalidated -= Invalidated; }
}
