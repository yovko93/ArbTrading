using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class OpportunitiesViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private CancellationTokenSource lifetime = new();
    private bool active, disposed;
    private long generation, readGeneration;
    public ObservableCollection<OpportunityResponse> Items { get; } = [];
    public string[] Exchanges { get; } = ["All", "Kalshi", "Polymarket"];
    [ObservableProperty] private string exchange = "All";
    [ObservableProperty] private string targetExchange = "All";
    [ObservableProperty] private string relationshipId = "";
    [ObservableProperty] private bool includeManualRelationships;
    [ObservableProperty] private bool showDiagnostics;
    [ObservableProperty] private bool sortByGrossProfit;
    [ObservableProperty] private decimal minimumGrossEdgePerShare = .001m;
    [ObservableProperty] private decimal maximumEvaluationQuantity = 1000m;
    [ObservableProperty] private int maximumSkewMilliseconds = 1000;
    [ObservableProperty] private OpportunityJobResponse? job;
    [ObservableProperty] private OpportunityResponse? selected;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private string notice = "Explicit evaluation uses cached books only. Missing inputs must be obtained separately in Market Explorer.";
    public string PageLabel => $"Page {Page} · {Total} {(ShowDiagnostics ? "diagnostic results" : "pre-fee candidates")}";
    public string DetailText => Selected is not { } r ? "Select a result to inspect its proof, depth and liquidity origins." : Describe(r);
    public OpportunitiesViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessInvalidated; state.OrderBookInvalidated += BookInvalidated;
        state.CatalogInvalidated += SourcesInvalidated; state.PropertyChanged += StateChanged;
    }
    public void Activate()
    {
        if (disposed || active) return;
        active = true; Observe(PollAsync(lifetime.Token));
    }
    public void Deactivate()
    {
        if (disposed) return;
        active = false; Reset(false);
    }
    private void Reset(bool forgetJob)
    {
        generation++; readGeneration++; lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        Items.Clear(); Selected = null; Total = 0; Busy = false;
        if (forgetJob) Job = null;
    }
    private void AccessInvalidated(object? sender, EventArgs e)
    {
        Reset(true); Notice = "Workspace access unavailable; results cleared.";
        if (active) Observe(PollAsync(lifetime.Token));
    }
    private void SourcesInvalidated(object? sender, EventArgs e)
    {
        readGeneration++; Items.Clear(); Selected = null; Total = 0;
        Notice = "Inputs changed. Retained results are being checked; evaluate explicitly for new calculations.";
    }
    private void BookInvalidated(object? sender, OrderBookInvalidation e) => SourcesInvalidated(sender, EventArgs.Empty);
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ConnectionStatus) or nameof(MainViewModel.BackendInstance) && state.ConnectionStatus != "Connected")
            AccessInvalidated(sender, EventArgs.Empty);
    }
    private (Guid Workspace, long Access, string Instance, long Generation)? Context() => active && !disposed && state.ConnectionStatus == "Connected" && state.HasSnapshot && Guid.TryParse(state.WorkspaceIdentifier, out var id)
        ? (id, state.AccessGeneration, state.BackendInstance, generation) : null;
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && active)
            {
                await RefreshAsync();
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
    [RelayCommand] private Task EvaluateSelectedAsync()
    {
        if (!Guid.TryParse(RelationshipId, out var id) || id == Guid.Empty) { Notice = "Enter the relationship ID to evaluate."; return Task.CompletedTask; }
        return EvaluateAsync(id);
    }
    [RelayCommand] private Task EvaluateVerifiedAsync() => EvaluateAsync(null);
    private async Task EvaluateAsync(Guid? id)
    {
        if (Busy || Job?.State == "Running" || Context() is not { } context) return;
        if (MinimumGrossEdgePerShare is < 0 or >= 1 || MaximumEvaluationQuantity is <= 0 or > 1_000_000_000m || MaximumSkewMilliseconds is < 0 or > 60000)
        { Notice = "Use edge 0–<1, quantity >0–1,000,000,000 and skew 0–60,000 ms."; return; }
        Busy = true; readGeneration++; Items.Clear(); Selected = null; Total = 0; Page = 1; Job = null;
        try
        {
            var result = await backend.EvaluateOpportunitiesAsync(context.Workspace, new(id, IncludeManualRelationships,
                Exchange == "All" ? null : Exchange, TargetExchange == "All" ? null : TargetExchange,
                MinimumGrossEdgePerShare: MinimumGrossEdgePerShare, MaximumEvaluationQuantity: MaximumEvaluationQuantity,
                MaximumSkewMilliseconds: MaximumSkewMilliseconds), lifetime.Token);
            if (Context() != context) return;
            Job = result; Notice = "Evaluation admitted. PRE-FEE only; execution is unavailable.";
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Context() == context) Failure(failure); }
        finally { if (Context() == context) Busy = false; }
    }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (Context() is not { } context || Job is not { } job) return;
        var read = ++readGeneration;
        try
        {
            var result = await backend.OpportunityResultsAsync(context.Workspace, job.Id, Page, ShowDiagnostics, SortByGrossProfit, lifetime.Token);
            if (Context() != context || read != readGeneration || Job?.Id != job.Id) return;
            var key = Selected?.OpportunityKey;
            Items.Clear(); foreach (var item in result.Items) Items.Add(item);
            Total = result.Total; Job = result.Job;
            Selected = Items.FirstOrDefault(i => i.OpportunityKey == key);
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Context() == context && read == readGeneration) { Items.Clear(); Selected = null; Total = 0; Failure(failure); } }
    }
    [RelayCommand] private async Task CancelEvaluationAsync()
    {
        if (Context() is not { } context || Job is not { } job) return;
        try
        {
            var result = await backend.CancelOpportunityEvaluationAsync(context.Workspace, job.Id, lifetime.Token);
            if (Context() == context && Job?.Id == job.Id) Job = result;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure failure) { if (Context() == context) Failure(failure); }
    }
    [RelayCommand] private async Task NextAsync() { if (Page * 20 < Total) { Page++; await RefreshAsync(); } }
    [RelayCommand] private async Task PreviousAsync() { if (Page > 1) { Page--; await RefreshAsync(); } }
    partial void OnShowDiagnosticsChanged(bool value) { Page = 1; SourcesInvalidated(this, EventArgs.Empty); OnPropertyChanged(nameof(PageLabel)); Observe(RefreshAsync()); }
    partial void OnSortByGrossProfitChanged(bool value) { Page = 1; Observe(RefreshAsync()); }
    partial void OnSelectedChanged(OpportunityResponse? value) => OnPropertyChanged(nameof(DetailText));
    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(PageLabel));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(PageLabel));
    private void Failure(BackendFailure failure)
    {
        if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied) state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        else Notice = "Result validation unavailable; displayed results cleared. Retry explicitly.";
    }
    private static string Describe(OpportunityResponse r)
    {
        var text = new StringBuilder();
        text.AppendLine($"{r.Status} · {r.Strategy} · trust: {r.RelationshipTrust}");
        if (r.Status != "Detected") text.AppendLine("Diagnostic only — displayed calculations do not establish a current candidate.");
        text.AppendLine($"{r.SourceTitle}\n{r.TargetTitle}");
        foreach (var mapping in r.OutcomeMappings) text.AppendLine($"Approved mapping: {mapping.SourceOutcomeId} → {mapping.TargetOutcomeId} ({mapping.Type})");
        text.AppendLine($"Relationship {r.RelationshipId} · policy {r.RelationshipPolicyVersion}");
        text.AppendLine($"Relationship eligible: {r.RelationshipEligible}; books actionable: {r.BooksActionable}; gross arbitrage exists: {r.GrossArbitrageExists}");
        text.AppendLine($"Requested quantity fully covered: {r.FullyExecutableForRequestedQuantity}; quantity capped: {r.EvaluationQuantityCapped}");
        text.AppendLine($"Input quality: {r.InputQuality}; observation skew {r.ObservedSkewMilliseconds} / {r.MaximumAllowedSkewMilliseconds} ms");
        text.AppendLine("PRE-FEE · Fees not evaluated · Net profit/edge unknown · Execution eligible: false");
        foreach (var blocker in r.Blockers) text.AppendLine("Blocker: " + blocker);
        foreach (var warning in r.Warnings) text.AppendLine("Warning: " + warning);
        foreach (var l in r.Legs)
        {
            text.AppendLine($"\n{l.Action} {l.Exchange}:{l.MarketId}:{l.InstrumentId} — {l.Outcome}");
            text.AppendLine($"Quantity {l.Quantity}; VWAP {l.AveragePrice}; worst {l.WorstPrice}; gross notional {l.GrossNotional}");
            text.AppendLine($"{l.SourceMode} / {l.Continuity} · version {l.SnapshotVersion} · observed {l.RetrievedAt:O} · source {l.SourceTimestamp:O}");
            text.AppendLine("Origins: " + string.Join(", ", l.LiquidityOrigins));
            foreach (var source in l.LiquiditySources) text.AppendLine($"Native liquidity: {source.Exchange}:{source.MarketId}:{source.InstrumentId} {source.NativeSide} @ {source.NativePrice}");
        }
        text.AppendLine($"\nSource fingerprint: {r.SourceFingerprint}\nTarget fingerprint: {r.TargetFingerprint}\nRevision: {r.RelationshipRevision}\nKey: {r.OpportunityKey}");
        return text.ToString();
    }
    private static async void Observe(Task task) { try { await task; } catch (Exception) { /* Observed event task; no mutation replay. */ } }
    public void Dispose()
    {
        if (disposed) return;
        Deactivate(); disposed = true; lifetime.Dispose();
        state.AccessInvalidated -= AccessInvalidated; state.OrderBookInvalidated -= BookInvalidated;
        state.CatalogInvalidated -= SourcesInvalidated; state.PropertyChanged -= StateChanged;
    }
}
