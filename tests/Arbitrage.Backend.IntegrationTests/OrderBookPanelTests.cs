using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class OrderBookPanelTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Posts, Reads; public bool Deny, Block; public string BlockMarket = "old";
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++; else Reads++;
            if (request.Method == HttpMethod.Put || Deny) return new(HttpStatusCode.Forbidden);
            var id = request.RequestUri!.AbsolutePath.Contains("/old", StringComparison.Ordinal) ? "old" : "new";
            if (Block && id == BlockMarket)
            {
                using var registration = ct.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var instrument = new BookInstrumentResponse("Kalshi", id, "yes", "Yes");
            var snapshot = new BookSnapshotResponse(Guid.NewGuid(), instrument,
                [new(.2001m, 1.25m, "NativeBid")], [new(.63m, 12.50m, "DerivedComplement")], [],
                DateTimeOffset.UtcNow.AddSeconds(-10), null, id, null, "Valid", "FullReturnedDepth", []);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OrderBookResponse([instrument], "yes", "Fresh", "Fresh",
                true, null, 0, 5, snapshot, null)) };
        }
    }
    private static MarketResponse Market(string id) => new("Kalshi", "Production", id, null, null, null, "binary", id,
        null, null, [], "active", "Open", [new("Yes", null), new("No", null)], null, null, null, null, null, null,
        null, null, null, DateTimeOffset.UtcNow, [], false);
    private static (MainViewModel State, OrderBookPanelViewModel Panel) Setup(Handler handler)
    {
        var client = new BackendClient(new HttpClient(handler), new Connection());
        var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance); var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A), new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, "Personal"), []), "http://127.0.0.1:5274");
        return (state, new(state, client));
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    [Fact]
    public async Task Selection_is_cached_only_and_explicit_refresh_preserves_decimal_provenance()
    {
        var handler = new Handler(); var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("new")); await Until(() => panel.Response is not null && !panel.Loading);
            state.NotifyCatalogInvalidated();
            state.RefreshRequested += () => Task.CompletedTask;
            await state.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(0, handler.Posts); Assert.Equal(.2001m, panel.BestBid); Assert.Equal(.63m, panel.BestAsk);
            Assert.Equal(.4299m, panel.Spread); Assert.Contains("derived", panel.Provenance);
            Assert.Equal("Cached snapshot · stale", panel.SnapshotState);
            await panel.RefreshCommand.ExecuteAsync(null); Assert.Equal(1, handler.Posts); Assert.False(panel.Loading);
        }
    }
    [Fact]
    public async Task Selection_change_cancels_old_request_and_rejects_old_response()
    {
        var handler = new Handler { Block = true }; var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("old")); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            panel.SelectMarket(Market("new")); await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.Release.TrySetResult(); await Until(() => panel.Response?.Snapshot?.Instrument.NativeMarketId == "new" && !panel.Loading);
            Assert.Equal(0, handler.Posts);
        }
    }
    [Theory] [InlineData("access")] [InlineData("deactivate")] [InlineData("dispose")]
    public async Task Late_response_after_lifecycle_change_cannot_restore_state(string change)
    {
        var handler = new Handler { Block = true }; var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("old")); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "Test denial");
            else if (change == "dispose") panel.Dispose(); else panel.Deactivate();
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); handler.Release.TrySetResult();
            Assert.False(panel.Loading); Assert.Null(panel.Response); await Task.Delay(30); Assert.Null(panel.Response);
        }
    }
    [Fact]
    public async Task Refresh_denial_clears_current_view_and_settles_loading()
    {
        var handler = new Handler(); var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("new")); await Until(() => panel.Response is not null && !panel.Loading);
            handler.Deny = true; await panel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("AuthorizationDenied", state.ConnectionStatus); Assert.Null(panel.Response);
            Assert.Empty(panel.Instruments); Assert.False(panel.Loading); Assert.Equal(1, handler.Posts);
        }
    }
}
