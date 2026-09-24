using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperSizingConsistencyTests
{
    private sealed class BarrierState { public int Reads; public bool Active; public Func<Task> Change = () => Task.CompletedTask; }
    private sealed class Fees(IFeeStore inner, BarrierState barrier) : IFeeStore
    {
        public async Task<FeeProfileState> ReadProfileAsync(Guid workspace, CancellationToken ct)
        {
            if (barrier.Active && Interlocked.Increment(ref barrier.Reads) == 3) await barrier.Change();
            return await inner.ReadProfileAsync(workspace, ct);
        }
        public Task<FeeSchedule?> ReadAsync(string exchange, string marketId, CancellationToken ct) => inner.ReadAsync(exchange, marketId, ct);
        public Task SaveAsync(FeeSchedule schedule, CancellationToken ct) => inner.SaveAsync(schedule, ct);
        public Task<KalshiFeeAccountProfile> ProfileAsync(Guid workspace, CancellationToken ct) => inner.ProfileAsync(workspace, ct);
        public Task SetProfileAsync(Guid workspace, KalshiFeeAccountProfile profile, CancellationToken ct) => inner.SetProfileAsync(workspace, profile, ct);
    }
    [Theory] [InlineData("books")] [InlineData("fees")]
    public async Task Change_after_captured_search_invalidates_selection_without_mixed_proof(string change)
    {
        var barrier = new BarrierState();
        await using var c = new PaperApiTests.Case(configure: s => s.AddScoped<IFeeStore>(sp => new Fees(new FeeStore(sp.GetRequiredService<TradingDbContext>()), barrier)));
        c.Clock.ManualTimers = true; await c.Start(); await PaperAutomationTests.Configure(c, PaperSizingApiTests.Settings);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
        barrier.Change = async () =>
        {
            if (change == "books") PaperAutomationTests.RealtimeBooks(c);
            else await c.Fixture.WithDatabaseAsync(async db => { await new FeeStore(db).SetProfileAsync(c.Session.DefaultWorkspaceId, KalshiFeeAccountProfile.DirectMember, default); return 0; });
        };
        barrier.Active = true;
        await using var scope = c.Fixture.Services.CreateAsyncScope();
        var permit = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().Runtime(c.Session.DefaultWorkspaceId).Session!;
        var decision = await scope.ServiceProvider.GetRequiredService<PaperCoordinator>().SizeAutomaticAsync(permit, c.Key, new(), default);
        Assert.Equal(3, barrier.Reads); Assert.Equal(PaperSizingState.InputUnavailable, decision.State);
        Assert.Null(decision.SelectedQuantity); Assert.Null(decision.Proof); Assert.Equal(1, decision.CandidatesEvaluated);
        Assert.Contains(decision.Rejections, r => r.Reason == "MarketDataChanged");
    }
}
