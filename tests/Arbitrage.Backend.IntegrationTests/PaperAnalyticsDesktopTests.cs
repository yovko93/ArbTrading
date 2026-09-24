using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperAnalyticsDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('A', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Guid Workspace = Guid.NewGuid();
        public Guid Generation = Guid.NewGuid();
        public bool HasGeneration = true;
        public bool Executions;
        public bool ValuationAvailable = true;
        public bool AvailableResponseWithoutBooks;
        public bool Campaign;
        public bool DelayAccount;
        public readonly List<(HttpMethod Method, string Path)> Requests = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            if (DelayAccount && path.EndsWith("/paper/account", StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task; }
            object response = path switch
            {
                var p when p.EndsWith("/paper/account", StringComparison.Ordinal) => new PaperAccountResponse(HasGeneration ? "Active" : "Uninitialized",
                    HasGeneration ? new PaperGenerationResponse(Generation, DateTimeOffset.UtcNow, null, "Fixture", "Healthy") : null, [], []),
                var p when p.EndsWith("/paper/performance", StringComparison.Ordinal) => new PaperPerformanceResponse(Generation, "Active",
                    [Performance("Kalshi", "USD"), Performance("Polymarket", "USDC")]),
                var p when p.EndsWith("/paper/valuation", StringComparison.Ordinal) => new PaperValuationResponse(Generation,
                    ValuationAvailable || AvailableResponseWithoutBooks ? "Available" : "Unavailable", false, DateTimeOffset.UtcNow, [],
                    [Valuation("Kalshi", "USD"), Valuation("Polymarket", "USDC")], 1, false, []),
                var p when p.EndsWith("/paper/reliability/current", StringComparison.Ordinal) => new PaperReliabilityCurrentResponse(
                    Campaign ? new PaperReliabilityCampaignResponse(Guid.NewGuid(), Workspace, Guid.NewGuid(), Guid.NewGuid(), "Fixture campaign", "", DateTimeOffset.UtcNow, null, null, "Collecting", 1, "fixture", false, false, false, null) : null, null, 0),
                _ => throw new InvalidOperationException("Unexpected analytics route: " + path)
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }

        private PaperPerformanceBucketResponse Performance(string venue, string currency) =>
            new(venue, currency, 1000m, Executions ? 700m : 1000m, Executions ? 200m : 0m,
                Executions ? 100m : 0m, Executions ? 120m : 0m, Executions ? 20m : 0m,
                Executions ? 1 : 0, Executions ? 1 : 0, Executions ? 1 : 0, 0, Executions ? 1 : 0);

        private PaperValuationBucketResponse Valuation(string venue, string currency) =>
            new(venue, currency, 1000m, Executions ? 700m : 1000m, Executions ? 200m : 0m,
                Executions ? 20m : 0m, Executions ? 1 : 0, ValuationAvailable && Executions ? 1 : 0, 0,
                !ValuationAvailable && Executions ? 1 : 0, ValuationAvailable ? 210m : null,
                ValuationAvailable ? 205m : null, ValuationAvailable ? 210m : 0m,
                Executions ? 900m : 1000m, ValuationAvailable ? 910m : null,
                ValuationAvailable ? 905m : null, ValuationAvailable ? 30m : null,
                ValuationAvailable ? 25m : null, ValuationAvailable ? 1m : 0m, null, Executions ? 200m : 0m, null, Executions ? 1 : 0);
    }

    [Fact]
    public async Task Available_valuation_response_with_unmarked_positions_does_not_imply_market_value()
    {
        var handler = new Handler { Executions = true, ValuationAvailable = false, AvailableResponseWithoutBooks = true };
        var (s, v) = Create(handler); using var state = s; using var analytics = v;
        analytics.Activate(); await analytics.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Available", analytics.Valuation!.State);
        Assert.Contains("unavailable for some open positions", analytics.ValuationSummary);
        Assert.All(analytics.Buckets, b => Assert.Null(b.GrossMarkedEquity));
    }

    private static (MainViewModel State, PaperAnalyticsViewModel Analytics) Create(Handler handler)
    {
        var backend = new BackendClient(new HttpClient(handler), new Connection());
        var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        Snapshot(state, handler.Workspace);
        return (state, new PaperAnalyticsViewModel(state, backend));
    }

    private static void Snapshot(MainViewModel state, Guid workspace) =>
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A),
            new(workspace, "Fixture"), []), "http://127.0.0.1:5274");

    [Fact]
    public async Task No_generation_has_explicit_empty_state_and_only_local_reads()
    {
        var handler = new Handler { HasGeneration = false }; var (s, v) = Create(handler);
        using var state = s; using var analytics = v;
        analytics.Activate(); await analytics.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("until a paper generation is initialized", analytics.GenerationSummary);
        Assert.Empty(analytics.Buckets);
        Assert.Contains("No active", analytics.ReliabilitySummary);
        Assert.Equal(2, handler.Requests.Select(r => r.Path).Distinct().Count());
        Assert.All(handler.Requests, r => { Assert.Equal(HttpMethod.Get, r.Method); Assert.True(r.Path.EndsWith("/paper/account") || r.Path.EndsWith("/paper/reliability/current")); });
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public async Task Summary_preserves_venue_currency_and_typed_unavailable_values(bool executions, bool valuationAvailable, bool campaign)
    {
        var handler = new Handler { Executions = executions, ValuationAvailable = valuationAvailable, Campaign = campaign };
        var (s, v) = Create(handler); using var state = s; using var analytics = v;
        analytics.Activate(); await analytics.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, analytics.Buckets.Count);
        Assert.Equal(new[] { "USD", "USDC" }, analytics.Buckets.Select(b => b.Currency));
        Assert.All(analytics.Buckets, b => Assert.Equal(executions ? 20m : 0m, b.RealizedPnl));
        Assert.Equal(campaign ? "Collecting" : "absent", campaign ? analytics.Reliability!.Campaign!.State : "absent");
        if (!executions) Assert.Contains("No paper executions", analytics.ExecutionSummary);
        if (valuationAvailable) Assert.Equal(executions ? 10m : 30m, analytics.Buckets[0].GrossUnrealizedPnl);
        else { Assert.Null(analytics.Buckets[0].GrossMarkedEquity); Assert.Null(analytics.Buckets[0].GrossUnrealizedPnl); Assert.Contains("unavailable", analytics.ValuationSummary); }
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Contains($"/workspaces/{handler.Workspace}/paper/", r.Path);
            Assert.Contains(new[] { "/paper/account", "/paper/performance", "/paper/valuation", "/paper/reliability/current" }, suffix => r.Path.EndsWith(suffix, StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("access")]
    [InlineData("navigation")]
    public async Task Delayed_workspace_A_account_cannot_repopulate_after_context_changes(string change)
    {
        var handler = new Handler(); var (s, v) = Create(handler);
        using var state = s; using var analytics = v;
        analytics.Activate(); await analytics.RefreshCommand.ExecuteAsync(null);
        handler.Requests.Clear();
        handler.DelayAccount = true;
        var delayed = analytics.RefreshCommand.ExecuteAsync(null);
        await handler.Entered.Task;
        if (change == "workspace") Snapshot(state, Guid.NewGuid());
        if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "Fixture");
        if (change == "navigation") analytics.Deactivate();
        handler.Release.TrySetResult();
        await delayed;
        Assert.Null(analytics.Account); Assert.Empty(analytics.Buckets);
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/paper/performance"));
    }
}
