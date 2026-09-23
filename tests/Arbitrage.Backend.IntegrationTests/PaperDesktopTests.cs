using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly PaperPreviewResponse preview = Preview();
        public int Mutations, Executions; public bool DelayPreview, Deny, LoseResponse;
        public readonly List<ConfirmPaperRequest> Requests = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Mutations++; if (Deny) return new(HttpStatusCode.Unauthorized); }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/preview", StringComparison.Ordinal) && DelayPreview) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            if (path.EndsWith("/execute", StringComparison.Ordinal))
            {
                Executions++; Requests.Add((await request.Content!.ReadFromJsonAsync<ConfirmPaperRequest>(ct))!);
                if (LoseResponse) throw new HttpRequestException("Lost fixture response");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PaperCommitResponse("Rejected", "MarketDataChanged", false, null)) };
            }
            object result = path.EndsWith("/admission-status", StringComparison.Ordinal) ? new PaperRiskStatusResponse("WithinLimits",
                new(1, preview.RiskDecision!.PolicyRevision!.Value, PaperRiskApiTests.Permissive, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), new string('A', 64)), preview.RiskDecision) :
                path.EndsWith("/preview", StringComparison.Ordinal) ? preview : path.EndsWith("/positions", StringComparison.Ordinal) ? Array.Empty<PaperPositionResponse>() :
                path.EndsWith("/executions", StringComparison.Ordinal) ? Array.Empty<PaperExecutionResponse>() : new PaperAccountResponse("Uninitialized", null, [], []);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
    private static PaperPreviewResponse Preview() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), true, "None", new string('A', 64), 10, 10, [], [], 9.1m, .26792m, 9.36792m, 10, .63208m, null, ["PAPER SIMULATION"], PaperRiskDesktopTests.Decision());
    private static MainViewModel State(BackendClient backend)
    {
        var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance); var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(workspace, "Paper fixture"), []), "http://127.0.0.1:5274"); return state;
    }
    [Theory] [InlineData("selection")] [InlineData("access")] [InlineData("navigation")] [InlineData("quantity")]
    public async Task Late_preview_cannot_restore_changed_context(string change)
    {
        var handler = new Handler { DelayPreview = true }; var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new PaperTradingViewModel(state, backend) { OpportunityKey = new string('A', 64) };
        var pending = vm.PreviewCommand.ExecuteAsync(null); await handler.Entered.Task;
        if (change == "selection") vm.OpportunityKey = new string('B', 64);
        if (change == "quantity") vm.Quantity = 2;
        if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (change == "navigation") vm.Deactivate();
        handler.Release.TrySetResult(); await pending; Assert.Null(vm.Preview); Assert.False(vm.ExecuteCommand.CanExecute(null));
    }
    [Fact] public async Task Confirmation_is_explicit_lost_response_retains_id_and_mutations_do_not_retry_401()
    {
        var handler = new Handler(); var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new PaperTradingViewModel(state, backend);
        await vm.PreviewCommand.ExecuteAsync(null); await vm.ExecuteCommand.ExecuteAsync(null); Assert.Equal(0, handler.Executions);
        vm.Confirm = message => { Assert.Contains("NO REAL ORDERS", message); return true; }; handler.LoseResponse = true;
        await vm.ExecuteCommand.ExecuteAsync(null); await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(2, handler.Executions); Assert.Equal(handler.Requests[0].RequestId, handler.Requests[1].RequestId);
        handler.Deny = true; var before = handler.Mutations; await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, handler.Mutations); Assert.Equal("AuthenticationFailed", state.ConnectionStatus); Assert.Null(vm.Preview);
    }
    [Fact] public void Opportunity_controls_reject_manual_stale_and_unknown_fee_rows()
    {
        var row = OpportunityDesktopTests.Result(); Assert.False(PaperTradingViewModel.Eligible(row));
        Assert.False(PaperTradingViewModel.Eligible(row with { RelationshipTrust = "Deterministic" }));
        var fees = new OpportunityFeesResponse("FeeAdjustedDetected", "Estimated", "DirectMember", [], .1m, 9.1m, .9m, .09m, null, .001m);
        Assert.False(PaperTradingViewModel.Eligible(row with { RelationshipTrust = "Deterministic", Fees = fees, Status = "BookStale" }));
    }
}
