using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class RelationshipDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Guid First { get; } = Guid.NewGuid(); public Guid Second { get; } = Guid.NewGuid();
        public bool DelayFirst; public bool Deny; public int Mutations; public int Page;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RelationshipSummaryResponse Summary(Guid id) => new(id, "Kalshi", "a", "Election", "Polymarket", "b", "Nomination", "Candidate", "NeedsReview", null, false);
        public RelationshipDetailResponse Detail(Guid id) => new(Summary(id), Market("Kalshi", "a"), Market("Polymarket", "b"), "fingerprint-a", "fingerprint-b", 1,
            [new("MissingRules", "Rules", "Rules unavailable", true, false)], [], [], null, null, null, false);
        private static RelationshipMarketResponse Market(string exchange, string id) => new(exchange, id, "Fixture", "Rules", null, null, null, null, null, "Binary", null, null, null, DateTimeOffset.UtcNow, null, [new("yes", "Yes"), new("no", "No")], "Unknown subject");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Mutations++; return new(Deny ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) { Content = JsonContent.Create(Detail(First)) }; }
            if (Deny) return new(HttpStatusCode.Forbidden);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/relationships/", StringComparison.Ordinal))
            {
                Page = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new RelationshipPageResponse([Summary(First), Summary(Second)], 60, Page, 30)) };
            }
            var id = Guid.Parse(path.Split('/')[^1]);
            if (id == First && DelayFirst) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Detail(id)) };
        }
    }
    private static (MainViewModel, RelationshipsViewModel, BackendClient) Setup(Handler handler)
    {
        var client = new BackendClient(new HttpClient(handler), new Connection()); var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance); var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A),
            new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A), new(workspace, "Personal"), []), "http://127.0.0.1:5274");
        return (state, new(state, client), client);
    }
    [Fact]
    public async Task Navigation_header_and_catalog_notifications_only_read_with_server_paging()
    {
        var handler = new Handler(); var (state, vm, _) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(2, vm.Items.Count);
            state.NotifyCatalogInvalidated(); await vm.NextCommand.ExecuteAsync(null); Assert.Equal(2, handler.Page);
            await vm.PreviousCommand.ExecuteAsync(null); Assert.Equal(1, handler.Page); Assert.Equal(0, handler.Mutations);
            vm.Dispose(); vm.Dispose(); // App shutdown and the DI container may both release a singleton.
        }
    }
    [Fact]
    public async Task Late_detail_cannot_populate_another_selection_or_restore_denied_state()
    {
        var handler = new Handler { DelayFirst = true }; var (state, vm, _) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null); vm.Selected = vm.Items[0]; await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Selected = vm.Items[1]; Assert.Equal(handler.Second, vm.Detail!.Summary.Id);
            handler.Release.TrySetResult(); await Task.Delay(50); Assert.Equal(handler.Second, vm.Detail!.Summary.Id);
            handler.Deny = true; await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("AuthorizationDenied", state.ConnectionStatus); Assert.Null(vm.Detail); Assert.Empty(vm.Items); Assert.Empty(vm.MappingEditors);
        }
    }
    [Theory] [InlineData("verify")] [InlineData("reject")]
    public async Task Manual_mutations_require_confirmation_and_never_retry_401(string action)
    {
        var handler = new Handler(); var (state, vm, _) = Setup(handler); using (state) using (vm)
        {
            vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null); vm.Selected = vm.Items[0];
            Assert.NotEmpty(vm.Blockers); vm.Reason = "Checked fixture"; vm.ReviewType = "EquivalentSameOutcome";
            vm.MappingEditors[0].Target = vm.MappingEditors[0].Targets[0];
            var confirmations = 0; vm.Confirm = _ => { confirmations++; return false; };
            var command = action == "verify" ? vm.VerifyCommand : vm.RejectCommand;
            await command.ExecuteAsync(null); Assert.Equal(1, confirmations); Assert.Equal(0, handler.Mutations);
            vm.Confirm = text => { Assert.Contains("Checked fixture", text); Assert.Contains("blockers", text); return true; }; handler.Deny = true;
            await command.ExecuteAsync(null); Assert.Equal(1, handler.Mutations); Assert.Equal("AuthenticationFailed", state.ConnectionStatus); Assert.Null(vm.Detail);
        }
    }
}
