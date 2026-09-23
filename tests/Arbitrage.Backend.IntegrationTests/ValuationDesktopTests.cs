using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class ValuationDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Delay, Deny; public List<string> Requests = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/paper/valuation", request.RequestUri!.AbsolutePath); Requests.Add(request.RequestUri.PathAndQuery);
            if (Deny) return new(HttpStatusCode.Forbidden);
            if (Delay) { Entered.TrySetResult(); await Release.Task; }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PaperValuationResponse(Guid.NewGuid(), "Available", false, DateTimeOffset.UtcNow, [], [], 1, false, [])) };
        }
    }
    private static (MainViewModel State, PaperTradingViewModel Vm) Create(Handler h)
    {
        var backend = new BackendClient(new HttpClient(h), new Connection()); var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var w = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        return (state, new(state, backend) { SettlementGeneration = new(Guid.NewGuid(), at, null, "Fixture", "Healthy") });
    }
    [Theory] [InlineData("generation")] [InlineData("access")] [InlineData("navigation")] [InlineData("book")] [InlineData("fee")] [InlineData("page")]
    public async Task Delayed_local_valuation_cannot_restore_invalidated_state(string change)
    {
        var h = new Handler { Delay = true }; var (state, vm) = Create(h); using var a = state; using var b = vm;
        var pending = vm.RefreshValuationCommand.ExecuteAsync(null); await h.Entered.Task;
        if (change == "generation") vm.SettlementGeneration = null;
        if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (change == "navigation") vm.Deactivate();
        if (change == "book") state.NotifyOrderBookInvalidated(new(Guid.NewGuid(), Guid.NewGuid(), new("Kalshi", "a", "yes", "Yes"), 2, 1, "Realtime", "Streaming"));
        if (change == "fee") state.NotifyPaperValuationInvalidated();
        if (change == "page") vm.ValuationPage = 2;
        h.Release.TrySetResult(); await pending; Assert.Null(vm.Valuation); Assert.Empty(vm.Marks); Assert.Null(vm.ValuationBucket);
    }
    [Fact] public async Task Refresh_is_one_local_get_and_denial_clears_private_state()
    {
        var h = new Handler(); var (state, vm) = Create(h); using var a = state; using var b = vm;
        await vm.RefreshValuationCommand.ExecuteAsync(null); Assert.Single(h.Requests); Assert.NotNull(vm.Valuation);
        h.Deny = true; await vm.RefreshValuationCommand.ExecuteAsync(null); Assert.Null(vm.Valuation); Assert.Equal("AuthorizationDenied", state.ConnectionStatus);
        Assert.Equal(2, h.Requests.Count); Assert.Contains("unavailable", vm.ValuationSummary, StringComparison.OrdinalIgnoreCase);
    }
}
