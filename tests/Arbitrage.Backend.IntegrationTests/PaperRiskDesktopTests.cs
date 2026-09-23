using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class PaperRiskDesktopTests
{
    internal static PaperRiskDecisionResponse Decision(string decision = "Approved") => new(decision, 1, Guid.NewGuid(), new string('A', 64),
        Guid.NewGuid(), 0, [], [], [], 0, 2, 0, 1, 0, 1, [], DateTimeOffset.UtcNow);
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Writes; public bool Delay; public HttpStatusCode Response = HttpStatusCode.OK;
        public SavePaperRiskPolicyRequest? Last;
        public PaperRiskStatusResponse Status = new("NotConfigured", null, Decision("NotConfigured"));
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method == HttpMethod.Put)
            {
                Writes++; Last = await r.Content!.ReadFromJsonAsync<SavePaperRiskPolicyRequest>(ct);
                if (Response != HttpStatusCode.OK) return new(Response);
                Status = new("WithinLimits", new(1, Guid.NewGuid(), Last!.Limits, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), new string('A', 64)), Decision());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Status.Policy) };
            }
            if (Delay) { Entered.TrySetResult(); await Release.Task; }
            return new(Response) { Content = JsonContent.Create(Status) };
        }
    }
    private static (MainViewModel State, PaperTradingViewModel Vm) Create(Handler h)
    {
        var backend = new BackendClient(new HttpClient(h), new Connection()); var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var w = Guid.NewGuid(); state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        return (state, new(state, backend));
    }
    [Fact] public async Task Suggested_values_require_confirmation_and_exact_percentage_conversion()
    {
        var h = new Handler(); var (s, vm) = Create(h); using var state = s; using var model = vm;
        Assert.Null(vm.RiskStatus); Assert.Equal(0, h.Writes); await vm.RefreshRiskCommand.ExecuteAsync(null);
        Assert.Equal("NotConfigured", vm.RiskStatus!.State);
        await vm.SaveRiskPolicyCommand.ExecuteAsync(null); Assert.Equal(0, h.Writes);
        vm.Confirm = _ => true; await vm.SaveRiskPolicyCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Writes); Assert.Equal(.20m, h.Last!.Limits.MinimumCashReserveFraction); Assert.Null(h.Last.ExpectedRevision);
        Assert.Equal("WithinLimits", vm.RiskStatus!.State);
        vm.RiskInputs[0].Text = "20,1"; await vm.SaveRiskPolicyCommand.ExecuteAsync(null); Assert.Equal(1, h.Writes);
        vm.LoadRiskPolicyCommand.Execute(null); Assert.Equal("20", vm.RiskInputs[0].Text);
    }
    [Theory] [InlineData(HttpStatusCode.Conflict)] [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.Forbidden)]
    public async Task Mutations_are_single_attempt_on_conflict_or_denial(HttpStatusCode code)
    {
        var h = new Handler(); var (s, vm) = Create(h); using var state = s; using var model = vm;
        await vm.RefreshRiskCommand.ExecuteAsync(null); vm.Confirm = _ => true; h.Response = code;
        await vm.SaveRiskPolicyCommand.ExecuteAsync(null); Assert.Equal(1, h.Writes);
        if (code == HttpStatusCode.Conflict) Assert.Contains("RiskPolicyChanged", vm.RiskNotice);
        else Assert.Null(vm.RiskStatus);
    }
    [Theory] [InlineData("navigation")] [InlineData("access")] [InlineData("generation")]
    public async Task Stale_risk_read_cannot_restore_private_state(string cause)
    {
        var h = new Handler { Delay = true }; var (s, vm) = Create(h); using var state = s; using var model = vm;
        var read = vm.RefreshRiskCommand.ExecuteAsync(null); await h.Entered.Task;
        if (cause == "navigation") vm.Deactivate();
        if (cause == "access") s.SetRealtimeStatus("AuthorizationDenied", "fixture");
        if (cause == "generation") vm.SettlementGeneration = new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "Fixture", "Healthy");
        h.Release.TrySetResult(); await read; Assert.Null(vm.RiskStatus);
    }
    [Theory] [InlineData("MinimumCashReserve")] [InlineData("MarketCostBasisLimit")] [InlineData("OpenPositionCountLimit")]
    public void Rejected_preview_has_visible_reason_and_disabled_confirmation(string code)
    {
        var h = new Handler(); var (s, vm) = Create(h); using var state = s; using var model = vm;
        var risk = Decision("Rejected") with { Violations = [new(code, "Kalshi", "USD", "a", null, 18, 5, 23, 20, -3)] };
        vm.Preview = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), false, "RiskLimitExceeded", new string('A', 64), 10, 10, [], [], 9, 0, 9, 10, 1, null, [], risk);
        Assert.False(vm.ExecuteCommand.CanExecute(null)); Assert.Contains(code, vm.PreviewText);
        vm.Preview = vm.Preview with { WouldExecute = true }; Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.Preview = vm.Preview with { RiskDecision = Decision() }; Assert.True(vm.ExecuteCommand.CanExecute(null));
    }
}
