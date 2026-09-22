using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class OpportunityDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    internal static OpportunityResponse Result(string key = "fixture", string status = "Detected") => new(key, "CrossMarketBuyBothComplements", Guid.NewGuid(), "Manual", DateTimeOffset.UtcNow,
        status, status == "Detected" ? [] : ["Explicit fixture blocker"], ["Fees not evaluated"], "FreshRest", 12, 1000, true,
        [new("Kalshi", "a", "yes", "Yes", "Buy", 25, .4m, .4m, 10, "RestSnapshot", "NotApplicable", 3, DateTimeOffset.UtcNow, null, ["DerivedComplement"], []),
         new("Polymarket", "b", "456", "No", "Buy", 25, .5m, .5m, 12.5m, "RestSnapshot", "NotApplicable", 4, DateTimeOffset.UtcNow, null, ["NativeAsk"], [])],
        [], 25, 25, 22.5m, 0, 25, 2.5m, .1m, 2.5m / 22.5m, true, true, status == "Detected", false, false, false, 1, "source", "target", "revision", false, "NotEvaluated", null, null);
    private sealed class Handler : HttpMessageHandler
    {
        public int Evaluations, Cancels; public bool Deny, Delay, DelayMutation; public EvaluateOpportunitiesRequest? Request;
        public OpportunityJobResponse Job = new(Guid.NewGuid(), "Completed", 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal)) Cancels++;
                else { Evaluations++; Request = await request.Content!.ReadFromJsonAsync<EvaluateOpportunitiesRequest>(ct); }
                if (DelayMutation) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                return new(Deny ? HttpStatusCode.Unauthorized : HttpStatusCode.Accepted) { Content = JsonContent.Create(Job) };
            }
            if (Delay) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            if (Deny) return new(HttpStatusCode.Forbidden);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OpportunityPageResponse([Result()], 1, 1, 20, Job)) };
        }
    }
    private static (MainViewModel State, OpportunitiesViewModel Vm) Setup(Handler handler)
    {
        var client = new BackendClient(new HttpClient(handler), new Connection()); var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        var workspace = Guid.NewGuid(); state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A), new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, "Personal"), []), "http://127.0.0.1:5274");
        return (state, new(state, client));
    }
    [Fact] public async Task Navigation_header_catalog_and_realtime_events_never_evaluate()
    {
        var handler = new Handler(); var (state, vm) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null); state.NotifyCatalogInvalidated();
            state.NotifyOrderBookInvalidated(new(Guid.NewGuid(), Guid.NewGuid(), new("Kalshi", "a", "yes", "Yes"), 1, 1, "Streaming", "Continuous"));
            vm.Deactivate(); vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(0, handler.Evaluations); Assert.Empty(vm.Items); Assert.Null(vm.Job);
            vm.Dispose(); vm.Dispose();
        }
    }
    [Fact] public async Task Explicit_selected_verified_and_cancel_commands_and_details_work()
    {
        var handler = new Handler(); var (state, vm) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); var id = Guid.NewGuid(); vm.RelationshipId = id.ToString(); vm.IncludeManualRelationships = true;
            await vm.EvaluateSelectedCommand.ExecuteAsync(null); Assert.Equal(id, handler.Request!.RelationshipId); Assert.True(handler.Request.IncludeManualRelationships);
            vm.Selected = Assert.Single(vm.Items); Assert.Contains("DerivedComplement", vm.DetailText); Assert.Contains("Manual", vm.DetailText); Assert.Contains("PRE-FEE", vm.DetailText);
            await vm.CancelEvaluationCommand.ExecuteAsync(null); Assert.Equal(1, handler.Cancels);
            await vm.EvaluateVerifiedCommand.ExecuteAsync(null); Assert.Null(handler.Request!.RelationshipId); Assert.Equal(2, handler.Evaluations);
            vm.Selected = Result(status: "BookStale"); Assert.Contains("Explicit fixture blocker", vm.DetailText);
        }
    }
    [Theory] [InlineData("navigation")] [InlineData("access")] [InlineData("book")]
    public async Task Late_result_cannot_restore_invalidated_display(string invalidate)
    {
        var handler = new Handler { Delay = true }; var (state, vm) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); vm.Job = handler.Job;
            var pending = vm.RefreshCommand.ExecuteAsync(null); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (invalidate == "navigation") vm.Deactivate();
            if (invalidate == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture access revoked");
            if (invalidate == "book") state.NotifyOrderBookInvalidated(new(Guid.NewGuid(), Guid.NewGuid(), new("Kalshi", "a", "yes", "Yes"), 1, 1, "Streaming", "Continuous"));
            handler.Release.TrySetResult(); await pending; Assert.Empty(vm.Items); Assert.Null(vm.Selected);
        }
    }
    [Fact] public async Task Late_admission_after_navigation_is_ignored_and_401_mutation_is_not_replayed()
    {
        var handler = new Handler { DelayMutation = true }; var (state, vm) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); var pending = vm.EvaluateVerifiedCommand.ExecuteAsync(null); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Deactivate(); vm.Activate(); handler.Release.TrySetResult(); await pending;
            Assert.Null(vm.Job); Assert.Empty(vm.Items); handler.DelayMutation = false; handler.Deny = true;
            await vm.EvaluateVerifiedCommand.ExecuteAsync(null); Assert.Equal(2, handler.Evaluations); Assert.Equal("AuthenticationFailed", state.ConnectionStatus);
            Assert.Empty(vm.Items); Assert.Null(vm.Job);
        }
    }
    [Fact] public async Task Denied_result_read_clears_selected_details()
    {
        var handler = new Handler(); var (state, vm) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); await vm.EvaluateVerifiedCommand.ExecuteAsync(null); vm.Selected = Assert.Single(vm.Items);
            handler.Deny = true; await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("AuthorizationDenied", state.ConnectionStatus); Assert.Empty(vm.Items); Assert.Null(vm.Selected);
        }
    }
}
