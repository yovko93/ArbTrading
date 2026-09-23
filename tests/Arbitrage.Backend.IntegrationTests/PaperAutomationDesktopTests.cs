using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;
namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperAutomationDesktopTests
{
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Writes; public bool Delay; public HttpStatusCode Code = HttpStatusCode.OK; public string? LastAction; public ArmPaperAutomationRequest? LastArm;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PaperAutomationStatusResponse Status = new("Disarmed", "None", new(1, Guid.NewGuid(), PaperAutomationTests.Settings, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), new string('A', 64)),
            Guid.NewGuid(), Guid.NewGuid(), new(null, false, null, null, "", null, null), null, null, null, "Running", 0, 0, 0, 0, null, null, [], 0, new Dictionary<string, long>());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method != HttpMethod.Get)
            {
                Writes++; LastAction = r.RequestUri!.Segments.Last();
                if (LastAction == "arm") LastArm = await r.Content!.ReadFromJsonAsync<ArmPaperAutomationRequest>(ct);
                if (Code != HttpStatusCode.OK) return new(Code) { Content = JsonContent.Create(new { code = "RevisionConflict" }) };
                Status = Status with { State = LastAction == "arm" ? "Armed" : LastAction == "emergency-stop" ? "KillSwitchLatched" : "Disarmed",
                    KillSwitch = Status.KillSwitch with { IsLatched = LastAction == "emergency-stop", Revision = Guid.NewGuid() } };
            }
            if (Delay) { Entered.TrySetResult(); await Release.Task; }
            return new(Code) { Content = JsonContent.Create(Status) };
        }
    }
    private static (MainViewModel State, PaperTradingViewModel Vm) Create(Handler h)
    {
        var backend = new BackendClient(new HttpClient(h), new Connection()); var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance); var w = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        return (state, new(state, backend));
    }
    [Fact] public async Task Explicit_arm_confirmation_stop_and_reset_controls_never_rearm_implicitly()
    {
        var h = new Handler(); var (s, vm) = Create(h); using var state = s; using var model = vm;
        Assert.Equal(0, h.Writes); await vm.RefreshAutomationCommand.ExecuteAsync(null);
        await vm.ArmAutomationCommand.ExecuteAsync(null); Assert.Equal(0, h.Writes);
        string warning = ""; vm.Confirm = text => { warning = text; return true; }; await vm.ArmAutomationCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Writes); Assert.Contains("immediately", warning); Assert.Contains("does NOT disarm", warning); Assert.Contains("No real orders", warning);
        Assert.True(h.LastArm!.ConfirmSimulation); Assert.Equal(h.Status.Profile!.Revision, h.LastArm.ExpectedProfileRevision);
        vm.Deactivate(); Assert.Equal(1, h.Writes); Assert.Null(vm.AutomationStatus);
        await vm.RefreshAutomationCommand.ExecuteAsync(null); await vm.DisarmAutomationCommand.ExecuteAsync(null); Assert.Equal("Disarmed", vm.AutomationStatus!.State);
        await vm.EmergencyStopAutomationCommand.ExecuteAsync(null); Assert.Equal("KillSwitchLatched", vm.AutomationStatus!.State);
        await vm.ResetAutomationKillCommand.ExecuteAsync(null); Assert.Equal("Disarmed", vm.AutomationStatus!.State); Assert.Equal(4, h.Writes);
    }
    [Theory] [InlineData("access")] [InlineData("navigation")] [InlineData("invalidation")]
    public async Task Late_status_cannot_restore_cleared_state(string change)
    {
        var h = new Handler { Delay = true }; var (s, vm) = Create(h); using var state = s; using var model = vm;
        var read = vm.RefreshAutomationCommand.ExecuteAsync(null); await h.Entered.Task;
        if (change == "access") s.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (change == "navigation") vm.Deactivate();
        if (change == "invalidation") s.NotifyPaperValuationInvalidated();
        h.Release.TrySetResult(); await read; Assert.Null(vm.AutomationStatus); Assert.Equal(0, h.Writes);
    }
    [Theory] [InlineData(HttpStatusCode.Conflict)] [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.Forbidden)]
    public async Task Failed_commands_are_one_attempt_and_clear_stale_controls(HttpStatusCode code)
    {
        var h = new Handler(); var (s, vm) = Create(h); using var state = s; using var model = vm;
        await vm.RefreshAutomationCommand.ExecuteAsync(null); vm.Confirm = _ => true; h.Code = code;
        await vm.ArmAutomationCommand.ExecuteAsync(null); Assert.Equal(1, h.Writes); Assert.Null(vm.AutomationStatus);
        Assert.False(vm.ArmAutomationCommand.CanExecute(null)); Assert.Contains("no automatic retry", vm.AutomationNotice);
    }
}
