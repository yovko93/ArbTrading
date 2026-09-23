using System.Collections.ObjectModel;
using System.ComponentModel;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arbitrage.Desktop.ViewModels;

public partial class PaperTradingViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel state;
    private readonly BackendClient backend;
    private CancellationTokenSource lifetime = new();
    private long revision;
    private long pollVersion;
    private bool active, disposed;
    private ConfirmPaperRequest? pendingRequest;
    public Func<string, bool> Confirm { get; set; } = _ => false;
    public ObservableCollection<PaperPositionResponse> Positions { get; } = [];
    public ObservableCollection<PaperExecutionResponse> Executions { get; } = [];
    [ObservableProperty] private PaperAccountResponse? account;
    [ObservableProperty] private PaperPreviewResponse? preview;
    [ObservableProperty] private PaperExecutionResponse? selectedExecution;
    [ObservableProperty] private string opportunityKey = "";
    [ObservableProperty] private decimal quantity = 1;
    [ObservableProperty] private decimal kalshiStartingCash = 10000;
    [ObservableProperty] private decimal polymarketStartingCash = 10000;
    [ObservableProperty] private string resetReason = "User-confirmed paper account initialization";
    [ObservableProperty] private string notice = "SIMULATION ONLY. Initialize paper funds explicitly. No real orders are submitted.";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private int historyPage = 1;
    public string AccountText => Account is null ? "Paper account unavailable" : Account.Generation is not { } g ? "Uninitialized — no paper funds exist." :
        $"{Account.State} generation {g.Id} · {g.Integrity} · created {g.CreatedAt:O}";
    public string PreviewText => Preview is not { } p ? "Choose a current opportunity and request a paper preview." : Describe(p);
    public string ExecutionText => SelectedExecution is not { } e ? "Select a historical execution." :
        $"{e.State} · {e.Id}\nActor {e.ActorId} · generation {e.GenerationId}\n{e.CreatedAt:O} · request {e.RequestId}\n" +
        $"Quantity {e.Quantity} · cost including fees {e.Cost}\nExpected payout at resolution {e.ExpectedPayoutAtResolution} · expected profit at resolution {e.ExpectedProfitAtResolution}\n" +
        (e.Settlement is { } s ? $"Realized payout to date {s.RealizedPayoutToDate}; realized P&L to date {s.RealizedPnlToDate}; remaining open cost {s.RemainingOpenCostBasis}\nExpected remaining payout at resolution {s.ExpectedRemainingPayout}; expected remaining profit {s.ExpectedRemainingPayout - s.RemainingOpenCostBasis}\nFinal realized profit {s.FinalRealizedProfit?.ToString() ?? "pending"}; return on cost {s.RealizedReturnOnCost}; expected/actual payout difference {s.ExpectedVsRealizedDifference}; settled {e.SettledAt:O}\n" : "Unresolved snapshot paper fill.\n") + string.Join("\n", e.Fills.Select(f =>
            $"{f.Exchange} {f.MarketId}/{f.InstrumentId} {f.Outcome}: BUY {f.Quantity} @ {f.Price}; fee {f.Fee} {f.Currency}; {f.LiquidityOrigin}; book {f.BookVersion}"));
    public PaperTradingViewModel(MainViewModel state, BackendClient backend)
    {
        this.state = state; this.backend = backend;
        state.AccessInvalidated += AccessChanged; state.PropertyChanged += StateChanged;
        state.PaperValuationInvalidated += ValuationInvalidated; state.CatalogInvalidated += ValuationInvalidated; state.OrderBookInvalidated += ValuationBookInvalidated;
    }
    public static string EligibilityReason(OpportunityResponse? r) => r is null ? "Select a current opportunity." :
        r.RelationshipTrust != "Deterministic" ? "Paper execution requires deterministic verification." :
        r.Status != "Detected" || !r.BooksActionable || !r.RelationshipEligible ? "Current books and relationship must be actionable." :
        r.Fees?.State != "FeeAdjustedDetected" ? "Resolved fees and positive fee-adjusted edge are required." :
        r.Fees.Breakdown.Select(f => f.Currency).Distinct().Count() != 1 ? "A verified common currency is required." : "Ready for a server-validated paper preview.";
    public static bool Eligible(OpportunityResponse? r) => EligibilityReason(r) == "Ready for a server-validated paper preview.";
    public void SelectOpportunity(OpportunityResponse value) { OpportunityKey = value.OpportunityKey; ClearPreview(); Notice = EligibilityReason(value); }
    public void Activate() { if (active || disposed) return; active = true; _ = PollAsync(++pollVersion); }
    public void Deactivate() { active = false; pollVersion++; ClearPreview(); ClearResolutionPreview(); ClearValuation(); }
    private (Guid Workspace, long Access, string Instance)? Context() => !disposed && state.ConnectionStatus == "Connected" && state.HasSnapshot &&
        Guid.TryParse(state.WorkspaceIdentifier, out var id) ? (id, state.AccessGeneration, state.BackendInstance) : null;
    private async Task PollAsync(long version)
    {
        try { while (active && !disposed && version == pollVersion) { await RefreshAsync(); await Task.Delay(2000, lifetime.Token); } }
        catch (OperationCanceledException) { }
    }
    private void ClearPreview() { revision++; Preview = null; pendingRequest = null; }
    private void Failure(BackendFailure failure)
    {
        if (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
            state.SetRealtimeStatus(failure.State.ToString(), failure.Message);
        Notice = failure.Message;
    }
    private void AccessChanged(object? sender, EventArgs e)
    {
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); ClearPreview(); Account = null; Positions.Clear(); Executions.Clear(); SelectedExecution = null;
        ClearSettlement();
        ClearValuation();
        Notice = "Workspace access changed; private paper data cleared.";
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.BackendInstance) || e.PropertyName == nameof(MainViewModel.ConnectionStatus) && state.ConnectionStatus != "Connected") AccessChanged(sender, EventArgs.Empty); }
    partial void OnOpportunityKeyChanged(string value) => ClearPreview();
    partial void OnQuantityChanged(decimal value) => ClearPreview();
    partial void OnAccountChanged(PaperAccountResponse? value) => OnPropertyChanged(nameof(AccountText));
    partial void OnPreviewChanged(PaperPreviewResponse? value) { OnPropertyChanged(nameof(PreviewText)); ExecuteCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedExecutionChanged(PaperExecutionResponse? value) => OnPropertyChanged(nameof(ExecutionText));
    partial void OnBusyChanged(bool value) { ExecuteCommand.NotifyCanExecuteChanged(); ConfirmResolutionCommand.NotifyCanExecuteChanged(); }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (Busy || Context() is not { } context) return;
        var viewVersion = pollVersion;
        try
        {
            var a = await backend.PaperAccountAsync(context.Workspace, lifetime.Token);
            var p = await backend.PaperPositionsAsync(context.Workspace, lifetime.Token);
            var h = await backend.PaperHistoryAsync(context.Workspace, HistoryPage, lifetime.Token);
            if (Context() != context || viewVersion != pollVersion) return;
            if (Account?.Generation?.Id != a.Generation?.Id && Account is not null) ClearPreview();
            Account = a; Positions.Clear(); foreach (var row in p) Positions.Add(row);
            Executions.Clear(); foreach (var row in h) Executions.Add(row);
            await RefreshSettlementAsync();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context) Failure(e); }
    }
    [RelayCommand] private async Task InitializeAsync()
    {
        if (Busy || Context() is not { } context) return;
        if (!Confirm($"SIMULATION ONLY — no real orders submitted.\n{(Account?.Generation is null ? "Initialize" : "Reset to a NEW generation; retain old history")}?\nKalshi: {KalshiStartingCash} USD\nPolymarket: {PolymarketStartingCash} USDC\nReason: {ResetReason}")) return;
        Busy = true; ClearPreview();
        try
        {
            var result = await backend.InitializePaperAsync(context.Workspace, new(true, Account?.Generation?.Id, ResetReason,
                [new("Kalshi", "USD", KalshiStartingCash), new("Polymarket", "USDC", PolymarketStartingCash)]), lifetime.Token);
            if (Context() == context) { Account = result; Notice = "New paper generation initialized. Historical records retained."; }
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context) Failure(e); }
        finally { Busy = false; }
        await RefreshAsync();
    }
    [RelayCommand] private async Task PreviewAsync()
    {
        if (Busy || Context() is not { } context) return;
        ClearPreview(); var captured = revision; Busy = true;
        try
        {
            var result = await backend.PaperPreviewAsync(context.Workspace, new(OpportunityKey, Quantity), lifetime.Token);
            if (Context() != context || captured != revision) return;
            Preview = result; Notice = result.WouldExecute ? "Review the preview, then explicitly confirm paper execution. Preview expires after five seconds." : result.Rejection;
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) Failure(e); }
        finally { Busy = false; }
    }
    private bool CanExecute() => !Busy && Preview?.WouldExecute == true && Context() is not null;
    [RelayCommand(CanExecute = nameof(CanExecute))] private async Task ExecuteAsync()
    {
        if (Preview is not { WouldExecute: true } p || Context() is not { } context || Busy) return;
        if (!Confirm("CONFIRM PAPER EXECUTION\nPAPER SIMULATION — NO REAL ORDERS\n" + Describe(p))) return;
        if (Preview != p || Context() != context) return;
        // Retain this request across a lost response. Never retry it automatically, including on 401.
        pendingRequest ??= new(Guid.NewGuid(), p.PreviewId, p.OpportunityKey, p.RequestedQuantity, true);
        var captured = revision; Busy = true;
        try
        {
            var result = await backend.ConfirmPaperAsync(context.Workspace, pendingRequest, lifetime.Token);
            if (Context() != context || captured != revision) return;
            Notice = result.Execution is null ? result.Rejection : "Paper execution committed. No real order was submitted.";
            SelectedExecution = result.Execution; ClearPreview();
        }
        catch (OperationCanceledException) { }
        catch (BackendFailure e) { if (Context() == context && captured == revision) { Failure(e); Notice += " Check history or retry this same confirmation; do not create a second request."; } }
        finally { Busy = false; }
        await RefreshAsync();
    }
    [RelayCommand] private async Task NextHistoryAsync() { HistoryPage++; await RefreshAsync(); }
    [RelayCommand] private async Task PreviousHistoryAsync() { if (HistoryPage > 1) HistoryPage--; await RefreshAsync(); }
    public static string Describe(PaperPreviewResponse p) =>
        $"Snapshot Paper Fill / Immediate Taker Simulation\n{(p.WouldExecute ? "Would execute" : "Rejected: " + p.Rejection)} · quantity {p.RequestedQuantity} · executable {p.ExecutableQuantity}\n" +
        $"Expires UTC {p.ExpiresAt:O}\n" + string.Join("\n", p.Debits.Select(d => $"{d.Exchange}: debit {d.Total} {d.Currency} (notional {d.Notional}, fees {d.Fees}); remaining {d.RemainingCash}")) +
        $"\nExpected payout at resolution {p.ExpectedPayoutAtResolution}; expected profit at resolution {p.ExpectedProfitAtResolution}\n" +
        $"Relationship trust: {p.Proof?.RelationshipTrust}; revision {p.Proof?.RelationshipRevision}\nFee profile: {p.Proof?.Fees?.Profile}; revision {p.Proof?.Fees?.ProfileRevision}\n" +
        string.Join("\n", p.Proof?.Legs.Select(l => $"{l.Exchange} book {l.SnapshotVersion}: {l.SourceMode}/{l.Continuity}; retrieved {l.RetrievedAt:O}; age at preview {(p.CreatedAt - l.RetrievedAt)?.TotalSeconds:0.###}s") ?? []) +
        "\n" + string.Join("\n", p.Warnings);
    public void Dispose() { if (disposed) return; disposed = true; active = false; lifetime.Cancel(); lifetime.Dispose(); state.AccessInvalidated -= AccessChanged; state.PropertyChanged -= StateChanged;
        state.PaperValuationInvalidated -= ValuationInvalidated; state.CatalogInvalidated -= ValuationInvalidated; state.OrderBookInvalidated -= ValuationBookInvalidated; }
}
