using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class SettlementDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Delay, Deny, Lose, Duplicate; public string Rejection = "MarketAlreadyResolved"; public int Mutations;
        public List<ConfirmResolutionRequest> Requests = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) { Mutations++; if (Deny) return new(HttpStatusCode.Unauthorized); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/resolutions/preview", StringComparison.Ordinal))
            {
                var selection = (await request.Content!.ReadFromJsonAsync<PaperResolutionSelection>(ct))!;
                if (Delay) { Entered.TrySetResult(); await Release.Task; }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PaperResolutionPreviewResponse(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30), selection,
                    true, "None", [new("yes", "Yes", 1), new("no", "No", 0)], [], [], [], ["Manual Scenario Resolution"])) };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/resolutions/confirm", StringComparison.Ordinal))
            {
                Requests.Add((await request.Content!.ReadFromJsonAsync<ConfirmResolutionRequest>(ct))!);
                if (Lose) throw new HttpRequestException("Lost test response");
                if (Duplicate)
                {
                    var r = Requests.Last(); var at = DateTimeOffset.UtcNow;
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PaperResolutionCommitResponse("None", true,
                        new(Guid.NewGuid(), r.Selection.GenerationId, r.RequestId, Guid.NewGuid(), r.Selection.Exchange, r.Selection.MarketId,
                            "ManualScenario", "Committed", at, at, [], [], [], Guid.NewGuid()))) };
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PaperResolutionCommitResponse(Rejection, false, null)) };
            }
            object value = request.RequestUri.AbsolutePath.EndsWith("/positions", StringComparison.Ordinal) ? Array.Empty<PaperPositionResponse>() :
                request.RequestUri.AbsolutePath.EndsWith("/executions", StringComparison.Ordinal) ? Array.Empty<PaperExecutionResponse>() : new PaperAccountResponse("Uninitialized", null, [], []);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }
    }
    private static (MainViewModel State, PaperTradingViewModel Vm) Create(Handler handler)
    {
        var backend = new BackendClient(new HttpClient(handler), new Connection()); var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var w = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        var vm = new PaperTradingViewModel(state, backend) { SettlementGeneration = new(Guid.NewGuid(), at, null, "fixture", "Healthy") };
        vm.SelectedCandidate = new(vm.SettlementGeneration.Id, "Kalshi", "market", "Question", true, [new("yes", "Yes", 0), new("no", "No", 0)], [], [], []);
        vm.WinningOutcome = vm.SelectedCandidate.Outcomes[0]; return (state, vm);
    }
    [Theory] [InlineData("outcome")] [InlineData("market")] [InlineData("generation")] [InlineData("access")] [InlineData("navigation")]
    public async Task Delayed_resolution_preview_cannot_restore_invalid_context(string change)
    {
        var handler = new Handler { Delay = true }; var (state, vm) = Create(handler); using var a = state; using var b = vm;
        var pending = vm.PreviewResolutionCommand.ExecuteAsync(null); await handler.Entered.Task;
        if (change == "outcome") vm.WinningOutcome = vm.SelectedCandidate!.Outcomes[1];
        if (change == "market") vm.SelectedCandidate = null;
        if (change == "generation") vm.SettlementGeneration = null;
        if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (change == "navigation") vm.Deactivate();
        handler.Release.TrySetResult(); await pending; Assert.Null(vm.ResolutionPreview); Assert.False(vm.ConfirmResolutionCommand.CanExecute(null)); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task Explicit_confirmation_lost_reply_retains_id_and_401_is_one_attempt()
    {
        var handler = new Handler(); var (state, vm) = Create(handler); using var a = state; using var b = vm;
        await vm.PreviewResolutionCommand.ExecuteAsync(null); Assert.Contains("MANUAL PAPER SCENARIO", vm.ResolutionPreviewText);
        await vm.ConfirmResolutionCommand.ExecuteAsync(null); Assert.Empty(handler.Requests);
        vm.Confirm = text => { Assert.Contains("PAPER SIMULATION ONLY", text); Assert.Contains("cannot be changed", text); return true; };
        handler.Lose = true; await vm.ConfirmResolutionCommand.ExecuteAsync(null); await vm.ConfirmResolutionCommand.ExecuteAsync(null);
        Assert.Equal(2, handler.Requests.Count); Assert.Equal(handler.Requests[0], handler.Requests[1]);
        handler.Deny = true; var before = handler.Mutations; await vm.ConfirmResolutionCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, handler.Mutations); Assert.Null(vm.ResolutionPreview); Assert.Empty(vm.Resolutions); Assert.Equal("AuthenticationFailed", state.ConnectionStatus);
    }
    [Fact] public async Task Conflicting_resolution_is_displayed_and_does_not_auto_retry()
    {
        var handler = new Handler(); var (state, vm) = Create(handler); using var a = state; using var b = vm;
        vm.Confirm = _ => true; await vm.PreviewResolutionCommand.ExecuteAsync(null); await vm.ConfirmResolutionCommand.ExecuteAsync(null);
        Assert.Equal("MarketAlreadyResolved", vm.Notice); Assert.Single(handler.Requests); Assert.Null(vm.ResolutionPreview);
    }
    [Fact] public async Task Duplicate_confirmation_displays_original_result_without_repeating_payout()
    {
        var handler = new Handler { Duplicate = true }; var (state, vm) = Create(handler); using var a = state; using var b = vm;
        vm.Confirm = _ => true; await vm.PreviewResolutionCommand.ExecuteAsync(null); await vm.ConfirmResolutionCommand.ExecuteAsync(null);
        Assert.Contains("no duplicate payout", vm.Notice); Assert.NotNull(vm.SelectedResolution); Assert.Single(handler.Requests); Assert.Null(vm.ResolutionPreview);
    }
}
