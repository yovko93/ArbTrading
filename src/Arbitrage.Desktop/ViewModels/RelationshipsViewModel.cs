using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class OutcomeMappingEditor(RelationshipOutcomeResponse source, RelationshipOutcomeResponse[] targets) : ObservableObject
{
    public RelationshipOutcomeResponse Source { get; } = source;
    public RelationshipOutcomeResponse[] Targets { get; } = targets;
    [ObservableProperty] private RelationshipOutcomeResponse? target;
    [ObservableProperty] private string type = "EquivalentSameOutcome";
}

public partial class RelationshipsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private bool active, disposed;
    private long listGeneration, detailGeneration;
    private CancellationTokenSource lifetime = new();
    public Func<string, bool>? Confirm { get; set; }
    public ObservableCollection<RelationshipSummaryResponse> Items { get; } = [];
    public ObservableCollection<OutcomeMappingEditor> MappingEditors { get; } = [];
    public string[] Exchanges { get; } = ["All", "Polymarket", "Kalshi"];
    public string[] TruthValues { get; } = ["Unknown", "False", "True"];
    [ObservableProperty] private string mutuallyExclusive = "Unknown";
    [ObservableProperty] private string collectivelyExhaustive = "Unknown";
    public string[] States { get; } = ["All", "Proposed", "NeedsReview", "VerifiedDeterministic", "VerifiedManual", "Rejected", "Stale"];
    public string[] Types { get; } = ["All", "Unknown", "Candidate", "EquivalentSameOutcome", "EquivalentOppositeOutcome", "MutuallyExclusive", "Exhaustive", "MutuallyExclusiveAndExhaustive", "Subset", "Superset", "Overlapping", "Related", "Rejected"];
    public string[] ReviewTypes { get; } = ["EquivalentSameOutcome", "EquivalentOppositeOutcome", "MutuallyExclusive", "Exhaustive", "MutuallyExclusiveAndExhaustive", "Subset", "Superset", "Overlapping", "Related"];
    [ObservableProperty] private string exchange = "All";
    [ObservableProperty] private string status = "All";
    [ObservableProperty] private string type = "All";
    [ObservableProperty] private string? reviewType;
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private string selectedNativeId = "";
    [ObservableProperty] private bool crossExchange = true;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string notice = "Generate candidates explicitly from the local catalog. Similarity never proves equivalence.";
    [ObservableProperty] private RelationshipSummaryResponse? selected;
    [ObservableProperty] private RelationshipDetailResponse? detail;
    [ObservableProperty] private RelationshipJobResponse? job;
    public string PageLabel => $"Page {Page} · {Total} relationships";
    public string[] Blockers => Detail?.Evidence.Where(e => e.Blocking).Select(e => e.Detail).ToArray() ?? [];
    public bool CanReview => Detail is not null && !Busy && Detail.Summary.State != "Stale";
    public RelationshipsViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessInvalidated; state.CatalogRefreshRequested += ReadRequested; state.CatalogInvalidated += ReadRequested; state.PropertyChanged += StateChanged;
    }
    public void Activate() { if (disposed) return; active = true; Observe(RefreshAsync()); }
    public void Deactivate() { if (disposed) return; active = false; Invalidate(); }
    private void Invalidate() { listGeneration++; detailGeneration++; lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); Busy = false; }
    private void AccessInvalidated(object? sender, EventArgs e)
    {
        Invalidate(); Items.Clear(); Selected = null; Detail = null; Job = null; Total = 0; Reason = ""; ReviewType = null; MappingEditors.Clear();
        MutuallyExclusive = CollectivelyExhaustive = "Unknown";
        Notice = "Workspace access unavailable. Refresh to reauthorize.";
    }
    private void ReadRequested(object? sender, EventArgs e) { if (active) Observe(RefreshAsync()); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    { if (active && e.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus == "Connected") Observe(RefreshAsync()); }
    private (Guid Workspace, long Access, string Instance)? Context() => active && !disposed && state.ConnectionStatus == "Connected" && state.HasSnapshot && Guid.TryParse(state.WorkspaceIdentifier, out var workspace)
        ? (workspace, state.AccessGeneration, state.BackendInstance) : null;
    private bool Current((Guid Workspace, long Access, string Instance) context) => Context() == context;
    private void Failure(BackendFailure failure)
    {
        if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        else Notice = "Relationship request could not be completed. Refresh sources/status and retry explicitly.";
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Context() is not { } context) return;
        var generation = ++listGeneration; var ct = lifetime.Token;
        try
        {
            var result = await backend.RelationshipsAsync(context.Workspace, Null(Exchange), Null(Status), Null(Type), Page, ct);
            if (!Current(context) || generation != listGeneration) return;
            var selectedId = Selected?.Id;
            Items.Clear(); foreach (var item in result.Items) Items.Add(item); Total = result.Total;
            Selected = Items.FirstOrDefault(r => r.Id == selectedId);
            if (Job is { } job)
            {
                var status = await backend.RelationshipJobAsync(context.Workspace, job.Id, false, ct);
                if (Current(context) && generation == listGeneration) Job = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Current(context) && generation == listGeneration) Failure(failure); }
    }
    partial void OnSelectedChanged(RelationshipSummaryResponse? value)
    {
        var generation = ++detailGeneration; Detail = null; Reason = ""; ReviewType = null; MappingEditors.Clear();
        MutuallyExclusive = CollectivelyExhaustive = "Unknown";
        if (value is not null && Context() is { } context) Observe(LoadDetailAsync(value.Id, generation, context, lifetime.Token));
    }
    private async Task LoadDetailAsync(Guid id, long generation, (Guid Workspace, long Access, string Instance) context, CancellationToken ct)
    {
        try
        {
            var value = await backend.RelationshipAsync(context.Workspace, id, ct);
            if (Current(context) && generation == detailGeneration && Selected?.Id == id) SetDetail(value);
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Current(context) && generation == detailGeneration) Failure(failure); }
    }
    private void SetDetail(RelationshipDetailResponse value)
    {
        Detail = value; MappingEditors.Clear();
        foreach (var outcome in value.Source.Outcomes) MappingEditors.Add(new(outcome, value.Target.Outcomes));
    }
    [RelayCommand] private async Task ApplyFiltersAsync() { Page = 1; await RefreshAsync(); }
    [RelayCommand] private async Task NextAsync() { if (Page * 30 < Total) { Page++; await RefreshAsync(); } }
    [RelayCommand] private async Task PreviousAsync() { if (Page > 1) { Page--; await RefreshAsync(); } }
    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (Busy || Context() is not { } context) return;
        if (!string.IsNullOrWhiteSpace(SelectedNativeId) && Exchange == "All") { Notice = "Choose the selected market's exchange first."; return; }
        Busy = true;
        try
        {
            var job = await backend.GenerateRelationshipsAsync(context.Workspace, new(Null(Exchange), string.IsNullOrWhiteSpace(SelectedNativeId) ? null : SelectedNativeId.Trim(), CrossExchange: CrossExchange), lifetime.Token);
            if (Current(context)) { Job = job; Notice = "Generation admitted. Refresh to read progress; Cancel stops further comparisons."; }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Current(context)) Failure(failure); }
        finally { if (Current(context)) Busy = false; }
    }
    [RelayCommand]
    private async Task CancelJobAsync()
    {
        if (Context() is not { } context || Job is not { } job) return;
        try { var value = await backend.RelationshipJobAsync(context.Workspace, job.Id, true, lifetime.Token); if (Current(context)) Job = value; }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Current(context)) Failure(failure); }
    }
    [RelayCommand] private Task RevalidateAsync() => MutateAsync("revalidate");
    [RelayCommand] private Task EnrichAsync() => MutateAsync("enrich");
    [RelayCommand] private Task VerifyAsync() => MutateAsync("verify");
    [RelayCommand] private Task RejectAsync() => MutateAsync("reject");
    private async Task MutateAsync(string action)
    {
        if (Busy || Context() is not { } context || Detail is not { } detail) return;
        ReviewRelationshipRequest? request = null;
        if (action is "verify" or "reject")
        {
            if (!CanReview || string.IsNullOrWhiteSpace(Reason) || Reason.Length > 2000) { Notice = "Revalidate stale sources and enter a review reason (1–2000 characters)."; return; }
            var mappings = MappingEditors.Where(m => m.Target is not null).Select(m => new RelationshipMappingResponse(m.Source.NativeId, m.Target!.NativeId, m.Type)).ToArray();
            if (action == "verify" && (ReviewType is null || mappings.Length == 0)) { Notice = "Select a relationship type and explicitly map the outcomes being approved."; return; }
            if (Confirm?.Invoke($"{(action == "verify" ? "Manually verify" : "Reject")} {detail.Source.Exchange}:{detail.Source.NativeId} and {detail.Target.Exchange}:{detail.Target.NativeId}?\nType: {ReviewType}\nMappings: {string.Join(", ", mappings.Select(m => m.SourceOutcomeId + " → " + m.TargetOutcomeId + " (" + m.Type + ")"))}\nSet exclusive: {MutuallyExclusive}; exhaustive: {CollectivelyExhaustive}\nReason: {Reason}\n{Blockers.Length} blockers remain in the audit evidence. Manual trust stays separate from deterministic verification.") != true) return;
            request = new(detail.SourceFingerprint, detail.TargetFingerprint, Reason, action == "reject" ? "Rejected" : ReviewType!, mappings, true, MutuallyExclusive, CollectivelyExhaustive);
        }
        if (!Current(context) || Detail?.Summary.Id != detail.Summary.Id) return;
        var generation = detailGeneration; Busy = true;
        try
        {
            var result = await backend.MutateRelationshipAsync(context.Workspace, detail.Summary.Id, action, request, lifetime.Token);
            if (Current(context) && generation == detailGeneration && Selected?.Id == result.Summary.Id) { SetDetail(result); Notice = "Review state saved. Refresh the candidate list to update filters."; }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Current(context) && generation == detailGeneration) Failure(failure); }
        finally { if (Current(context)) Busy = false; }
    }
    private static string? Null(string value) => value == "All" ? null : value;
    partial void OnDetailChanged(RelationshipDetailResponse? value) { OnPropertyChanged(nameof(Blockers)); OnPropertyChanged(nameof(CanReview)); }
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanReview));
    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(PageLabel));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(PageLabel));
    private static async void Observe(Task task) { try { await task; } catch (Exception) { /* Event work is observed; no mutation replay. */ } }
    public void Dispose()
    {
        if (disposed) return;
        Deactivate(); disposed = true; lifetime.Dispose(); state.AccessInvalidated -= AccessInvalidated; state.CatalogRefreshRequested -= ReadRequested;
        state.CatalogInvalidated -= ReadRequested; state.PropertyChanged -= StateChanged;
    }
}
