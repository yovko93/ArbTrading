using System.Net.Http.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperSizingConcurrencyTests
{
    private static async Task<PaperSizingDecision> Select(PaperApiTests.Case c, PaperAutomationPermit permit)
    {
        await using var scope = c.Fixture.Services.CreateAsyncScope();
        var d = await scope.ServiceProvider.GetRequiredService<PaperCoordinator>().SizeAutomaticAsync(permit, c.Key, new(), default);
        Assert.Equal(PaperSizingState.Selected, d.State); return d;
    }
    private static async Task<PaperCommitResult> Commit(PaperApiTests.Case c, PaperSizingDecision decision, PaperAutomationPermit permit,
        Func<CancellationToken, Task<PaperRejection>>? validate = null)
    {
        await using var scope = c.Fixture.Services.CreateAsyncScope(); var store = scope.ServiceProvider.GetRequiredService<PaperStore>();
        var proof = decision.Proof!; var plan = decision.SelectedPlan!;
        return await store.CommitAsync(permit.ActorId, permit.WorkspaceId, permit.GenerationId,
            PaperAutomationPolicy.RequestId(permit.SessionId, c.Key, proof.TriggerInputStamp, plan.Quantity, proof), proof.DecisionFingerprint,
            plan, "adaptive-barrier", validate ?? (_ => Task.FromResult(PaperRejection.None)), action => { action(); return true; }, default,
            decision.SelectedRiskDecision, new(permit, proof.TriggerInputStamp, proof));
    }
    [Fact] public async Task Financial_race_rejects_selected_twenty_without_hidden_ten_fallback()
    {
        await using var c = await PaperSizingApiTests.Start();
        await PaperRiskApiTests.Save(c, PaperRiskApiTests.Permissive with { MaximumMarketCostBasisFraction = .11m, MaximumInstrumentCostBasisFraction = .11m });
        await PaperAutomationTests.Configure(c, PaperSizingApiTests.Settings with { MaximumQuantity = 20 });
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode();
        var worker = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>(); var permit = worker.Runtime(c.Session.DefaultWorkspaceId).Session!;
        var decision = await Select(c, permit); Assert.Equal(20, decision.SelectedQuantity);
        Assert.NotNull((await c.Execute(c.Request(await c.Preview(10)))).Execution);
        Assert.False((await c.Preview(20)).RiskApproved); Assert.True((await c.Preview(10)).RiskApproved);
        // A separate diagnostic sees changed capital but cannot manufacture a new market trigger.
        var after = await Select(c, permit); Assert.Equal(10, after.SelectedQuantity);
        Assert.Equal(decision.Proof!.TriggerInputStamp, after.Proof!.TriggerInputStamp);
        Assert.NotEqual(decision.Proof.FinancialRevision, after.Proof.FinancialRevision);
        Assert.NotEqual(decision.Proof.DecisionFingerprint, after.Proof.DecisionFingerprint);
        Assert.NotEqual(PaperAutomationPolicy.RequestId(permit.SessionId, c.Key, decision.Proof.TriggerInputStamp, 20, decision.Proof),
            PaperAutomationPolicy.RequestId(permit.SessionId, c.Key, after.Proof.TriggerInputStamp, 10, after.Proof));
        var rejected = await Commit(c, decision, permit); Assert.Null(rejected.Execution);
        Assert.True(rejected.Rejection is PaperRejection.FinancialStateChanged or PaperRejection.RiskLimitExceeded, rejected.Rejection.ToString());
        var history = (await c.Client.GetFromJsonAsync<PaperExecutionResponse[]>(c.Root + "/paper/executions"))!;
        Assert.Equal("Manual", Assert.Single(history).Origin);
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, permit.GenerationId));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Adaptive_kill_writer_order_preserves_latch_guarantee(bool automaticWins)
    {
        await using var c = await PaperSizingApiTests.Start(); await PaperAutomationTests.Configure(c, PaperSizingApiTests.Settings);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); var worker = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>();
        var permit = worker.Runtime(c.Session.DefaultWorkspaceId).Session!; var decision = await Select(c, permit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Kill() => Task.Run(async () => { killStarted.TrySetResult(); await worker.KillAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, true, null, false, "Adaptive barrier", default); });
        PaperCommitResult result;
        if (automaticWins)
        {
            var calls = 0; var automatic = Task.Run(() => Commit(c, decision, permit, async ct =>
            { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } return PaperRejection.None; }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var kill = Kill(); await killStarted.Task;
            release.TrySetResult(); result = await automatic; await kill; Assert.NotNull(result.Execution);
        }
        else
        {
            await Kill(); result = await Commit(c, decision, permit); Assert.Null(result.Execution);
            Assert.Equal(PaperAutomationReason.KillSwitchLatched, result.AutomationReason);
        }
        var rejected = await Commit(c, decision, permit with { SessionId = Guid.NewGuid() });
        Assert.Equal(PaperAutomationReason.KillSwitchLatched, rejected.AutomationReason);
        Assert.Equal(automaticWins ? 1 : 0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, permit.GenerationId));
    }
}
