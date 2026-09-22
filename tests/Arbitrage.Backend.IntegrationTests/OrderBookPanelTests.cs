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
    [Theory] [InlineData("import")] [InlineData("replace")] [InlineData("remove")]
    public async Task Credential_mutations_are_never_replayed_after_401(string operation)
    {
        var handler = new Handler { Unauthorized = true };
        var client = new BackendClient(new HttpClient(handler), new Connection());
        await Assert.ThrowsAsync<BackendFailure>(() => operation == "remove"
            ? client.RemoveCredentialAsync(new(true, Guid.NewGuid()), default)
            : client.ImportCredentialAsync(new("fixture-key-id", "unused.pem", operation == "replace", Guid.Empty), default));
        Assert.Equal(1, handler.Posts);
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Posts, Reads; public bool Deny, Block, BlockPost, Unauthorized; public string BlockMarket = "old";
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++; else Reads++;
            if (Unauthorized) return new(HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Put || Deny) return new(HttpStatusCode.Forbidden);
            var id = request.RequestUri!.AbsolutePath.Contains("/old", StringComparison.Ordinal) ? "old" : "new";
            if (Block && id == BlockMarket || BlockPost && request.Method == HttpMethod.Post)
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
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Realtime_mutations_are_one_attempt_on_401(bool start)
    {
        var handler = new Handler(); var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("new")); await Until(() => panel.SelectedInstrument is not null && !panel.Loading);
            handler.Unauthorized = true;
            await (start ? panel.StartRealtimeCommand : panel.StopRealtimeCommand).ExecuteAsync(null);
            Assert.Equal(1, handler.Posts); Assert.Equal("AuthenticationFailed", state.ConnectionStatus); Assert.Null(panel.Response);
        }
    }
    [Fact]
    public async Task Late_start_cannot_restore_prior_market_or_authorization_and_navigation_does_not_stop_backend()
    {
        var handler = new Handler(); var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("new")); await Until(() => panel.SelectedInstrument is not null && !panel.Loading);
            handler.BlockPost = true;
            var start = panel.StartRealtimeCommand.ExecuteAsync(null); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            panel.SelectMarket(Market("old")); panel.Deactivate();
            handler.Release.TrySetResult(); await start;
            Assert.Equal(1, handler.Posts); Assert.DoesNotContain("start requested", panel.Notice);
            state.SetRealtimeStatus("AuthorizationDenied", "Denied"); Assert.Null(panel.Response);
        }
    }
    [Fact]
    public async Task Realtime_notice_bursts_are_coalesced_and_BestEffort_is_visible()
    {
        var handler = new Handler(); var (state, panel) = Setup(handler); using (state) using (panel)
        {
            panel.Activate(); panel.SelectMarket(Market("new")); await Until(() => panel.Response is not null && !panel.Loading);
            var response = panel.Response!;
            panel.Response = response with { Realtime = new("Realtime", "Streaming", "BestEffort", true, 1, 1, null, "1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null) };
            Assert.Contains("BestEffort", panel.RealtimeDetails); var reads = handler.Reads;
            for (var n = 0; n < 500; n++) state.NotifyOrderBookInvalidated(new(Guid.Parse(state.BackendInstance), Guid.Parse(state.WorkspaceIdentifier), panel.SelectedInstrument!, n, 1, "Realtime", "Streaming"));
            await Task.Delay(1200); Assert.InRange(handler.Reads - reads, 1, 2); Assert.Equal(0, handler.Posts);
            panel.Deactivate(); reads = handler.Reads; await Task.Delay(1100); Assert.Equal(reads, handler.Reads);
        }
    }}
