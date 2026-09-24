using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;
namespace Arbitrage.Backend.IntegrationTests;
public sealed class PaperReliabilityDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Writes; public bool Delay; public HttpStatusCode Code = HttpStatusCode.OK;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PaperReliabilityCampaignResponse Campaign = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fixture", "", DateTimeOffset.UtcNow, null, null, "Collecting", 1, new string('A', 64), false, false, false, null);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method != HttpMethod.Get) { Writes++; return new(Code) { Content = Code == HttpStatusCode.OK ? JsonContent.Create(Campaign) : JsonContent.Create(new { code = "RevisionConflict" }) }; }
            if (Delay && r.RequestUri!.AbsolutePath.EndsWith("/current", StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task; }
            var path = r.RequestUri!.AbsolutePath;
            return new(HttpStatusCode.OK) { Content = path.EndsWith("/current", StringComparison.Ordinal) ? JsonContent.Create(new PaperReliabilityCurrentResponse(Campaign, null, 0)) :
                path.EndsWith("/campaigns", StringComparison.Ordinal) ? JsonContent.Create(new[] { Campaign }) : JsonContent.Create<PaperReliabilityReportResponse?>(null) };
        }
    }
    private static (MainViewModel, PaperReliabilityViewModel) Create(Handler h)
    {
        var client = new BackendClient(new HttpClient(h), new Connection()); var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance); var w = h.Campaign.WorkspaceId;
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        return (state, new(state, client));
    }
    [Theory] [InlineData("access")] [InlineData("navigation")] [InlineData("invalidation")] [InlineData("selection")]
    public async Task Stale_campaign_response_cannot_repopulate_cleared_or_changed_context(string change)
    {
        var h = new Handler { Delay = true }; var (s, v) = Create(h); using var state = s; using var vm = v;
        var read = vm.RefreshCommand.ExecuteAsync(null); await h.Entered.Task;
        if (change == "access") state.SetRealtimeStatus("AuthorizationDenied", "Fixture");
        if (change == "navigation") vm.Deactivate();
        if (change == "invalidation") state.NotifyPaperValuationInvalidated();
        if (change == "selection") vm.Campaign = h.Campaign with { Id = Guid.NewGuid() };
        h.Release.TrySetResult(); await read; Assert.Null(vm.Report); Assert.Empty(vm.Campaigns); Assert.Equal(0, h.Writes);
    }
    [Theory] [InlineData(HttpStatusCode.Conflict)] [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.Forbidden)]
    public async Task Commands_require_explicit_confirmation_and_fail_without_retry(HttpStatusCode code)
    {
        var h = new Handler(); var (s, v) = Create(h); using var state = s; using var vm = v;
        await vm.RefreshCommand.ExecuteAsync(null); await vm.ActionCommand.ExecuteAsync("pause"); Assert.Equal(0, h.Writes);
        vm.Confirm = _ => true; h.Code = code; await vm.ActionCommand.ExecuteAsync("pause"); Assert.Equal(1, h.Writes); Assert.Null(vm.Campaign); Assert.Null(vm.Report);
    }
}
