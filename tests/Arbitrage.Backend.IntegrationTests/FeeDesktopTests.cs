using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class FeeDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls; public bool Delay;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Delay) await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new FeeProfileResponse("DirectMember")) }
                : new(HttpStatusCode.Accepted) { Content = JsonContent.Create(new FeeRefreshJobResponse(Guid.NewGuid(), "Completed", 2, 2, null)) };
        }
    }
    private static MainViewModel State(BackendClient backend)
    {
        var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance); var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A), new("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new(workspace, "Personal"), []), "http://127.0.0.1:5274"); return state;
    }
    [Fact] public async Task Navigation_never_refreshes_fees_but_explicit_selection_does()
    {
        var handler = new Handler(); var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new OpportunitiesViewModel(state, backend);
        vm.Activate(); await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(0, handler.Calls);
        vm.Selected = OpportunityDesktopTests.Result(); await vm.RefreshFeeDataCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.Calls); Assert.Equal("Completed", vm.FeeJob!.State);
        vm.Deactivate(); vm.Activate(); Assert.Equal(1, handler.Calls);
    }
    [Theory] [InlineData("access")] [InlineData("navigation")]
    public async Task Late_fee_admission_cannot_restore_invalidated_context(string action)
    {
        var handler = new Handler { Delay = true }; var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new OpportunitiesViewModel(state, backend);
        vm.Activate(); vm.Selected = OpportunityDesktopTests.Result(); var pending = vm.RefreshFeeDataCommand.ExecuteAsync(null);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "access") state.SetRealtimeStatus("AuthorizationDenied", "fixture"); else vm.Deactivate();
        handler.Release.TrySetResult(); await pending; Assert.Null(vm.FeeJob); Assert.Null(vm.Selected);
    }
    [Fact] public async Task Profile_is_explicit_and_late_response_cannot_restore_revoked_assumption()
    {
        var handler = new Handler(); var backend = new BackendClient(new HttpClient(handler), new Connection());
        using var state = State(backend); using var vm = new FeeProfileViewModel(state, backend);
        Assert.Equal("Unknown", vm.Profile); Assert.Equal(0, handler.Calls);
        await vm.SaveCommand.ExecuteAsync(null); Assert.Equal("DirectMember", vm.Profile); Assert.Equal(1, handler.Calls);
        handler.Delay = true; var pending = vm.LoadCommand.ExecuteAsync(null);
        state.SetRealtimeStatus("AuthorizationDenied", "fixture"); handler.Release.TrySetResult(); await pending;
        Assert.Equal("Unknown", vm.Profile); vm.Dispose(); vm.Dispose();
    }
}
