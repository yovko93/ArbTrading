using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MonitoringDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Mutations, Reads; public bool Delay, Deny;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Mutations++; if (Deny) return new(HttpStatusCode.Unauthorized); }
            else Reads++;
            var path = request.RequestUri!.AbsolutePath;
            if (Delay && path.EndsWith("/rankings", StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            object body = path.EndsWith("/profile", StringComparison.Ordinal) ? new MonitoringProfileResponse() :
                path.EndsWith("/rankings", StringComparison.Ordinal) ? new MonitoringRankingPage([new(1, "GrossOnly", 1, OpportunityDesktopTests.Result(), .1m, .001m, null, 25, "Armed")], 1, 1, 20) :
                path.EndsWith("/alerts", StringComparison.Ordinal) ? new MonitoringAlertPage([], 0, 1, 20) :
                new MonitoringStatusResponse("Running", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, new(1, 1, 0, false, 1, 1, 1, 0, 0, 1, 0, 0), 0, false, null, 1, 0, 0, 0, 1, 1, 1, 0, 1, 0, 0, 0, "Disabled", null, 0, 0);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
    private static MainViewModel State(BackendClient backend)
    {
        var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance); var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A), new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A), new(workspace, "Personal"), []), "http://127.0.0.1:5274"); return state;
    }
    [Theory] [InlineData("navigation")] [InlineData("access")] [InlineData("filter")] [InlineData("stop")] [InlineData("profile")]
    public async Task Monitoring_late_rankings_cannot_restore_invalidated_display(string action)
    {
        var handler = new Handler(); var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new MonitoringViewModel(state, backend);
        await vm.RefreshAsync(); Assert.Equal(0, handler.Reads);
        vm.Activate(); Assert.Single(vm.Items); handler.Delay = true;
        var pending = vm.RefreshAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "navigation") vm.Deactivate();
        if (action == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (action == "filter") vm.Lane = "Blocked";
        if (action == "stop") await vm.StopCommand.ExecuteAsync(null);
        if (action == "profile") await vm.SaveProfileCommand.ExecuteAsync(null);
        handler.Release.TrySetResult(); await pending; Assert.Empty(vm.Items); Assert.Null(vm.Selected); Assert.Equal(action is "stop" or "profile" ? 1 : 0, handler.Mutations);
    }
    [Fact] public async Task Monitoring_start_is_explicit_and_unauthorized_mutations_are_not_replayed()
    {
        var handler = new Handler(); var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new MonitoringViewModel(state, backend);
        vm.Activate(); Assert.Equal(0, handler.Mutations); handler.Deny = true;
        await vm.StartCommand.ExecuteAsync(null); Assert.Equal(1, handler.Mutations); Assert.Equal("AuthenticationFailed", state.ConnectionStatus); Assert.Empty(vm.Items);
        vm.Deactivate(); var reads = handler.Reads; await vm.RefreshAsync(); Assert.Equal(reads, handler.Reads);
    }
}
