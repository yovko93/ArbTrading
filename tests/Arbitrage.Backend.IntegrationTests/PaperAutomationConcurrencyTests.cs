using System.Net.Http.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperAutomationConcurrencyTests
{
    private static async Task<(PaperApiTests.Case Case, PaperPreviewTicket Ticket, PaperAutomationPermit Permit)> Prepare()
    {
        var c = new PaperApiTests.Case(); c.Clock.ManualTimers = true; await c.Start(); await PaperAutomationTests.Configure(c);
        (await PaperAutomationTests.Arm(c)).EnsureSuccessStatusCode(); var preview = await c.Preview(1); Assert.True(preview.WouldExecute, preview.Rejection);
        var ticket = c.Fixture.Services.GetRequiredService<PaperPreviewCache>().Find(preview.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        return (c, ticket, c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>().Runtime(c.Session.DefaultWorkspaceId).Session!);
    }
    private static async Task<PaperCommitResult> Commit(PaperApiTests.Case c, PaperPreviewTicket ticket, PaperAutomationPermit permit,
        Func<CancellationToken, Task<PaperRejection>>? validate = null)
    {
        await using var scope = c.Fixture.Services.CreateAsyncScope(); var store = scope.ServiceProvider.GetRequiredService<PaperStore>();
        var stamp = PaperAutomationPolicy.Stamp(ticket.Plan.Proof, permit.Profile);
        return await store.CommitAsync(permit.ActorId, permit.WorkspaceId, permit.GenerationId,
            PaperAutomationPolicy.RequestId(permit.SessionId, ticket.Plan.Proof.OpportunityKey, stamp, ticket.Plan.Quantity), stamp, ticket.Plan, "automation-barrier",
            validate ?? (_ => Task.FromResult(PaperRejection.None)), action => { action(); return true; }, default, ticket.Risk, new(permit, stamp));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Kill_writer_order_guarantees_no_commit_after_latch(bool automaticWins)
    {
        var setup = await Prepare(); await using var c = setup.Case; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var killStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = c.Fixture.Services.GetRequiredService<PaperAutomationCoordinator>();
        Task Kill() => Task.Run(async () => { killStarted.TrySetResult(); await worker.KillAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, true, null, false, "Barrier emergency", default); });
        PaperCommitResult result;
        if (automaticWins)
        {
            var calls = 0; var automatic = Task.Run(() => Commit(c, setup.Ticket, setup.Permit, async ct =>
            { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } return PaperRejection.None; }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var kill = Kill(); await killStarted.Task;
            release.TrySetResult(); result = await automatic; await kill; Assert.NotNull(result.Execution);
        }
        else
        {
            var automatic = Task.Run(async () => { entered.TrySetResult(); await release.Task; return await Commit(c, setup.Ticket, setup.Permit); });
            await entered.Task; await Kill(); release.TrySetResult(); result = await automatic;
            Assert.Null(result.Execution); Assert.Equal(PaperAutomationReason.KillSwitchLatched, result.AutomationReason);
        }
        Assert.True((await PaperAutomationTests.Status(c))!.KillSwitch.IsLatched);
        // A distinct request cannot evade the persisted latch, even with a previously valid permit.
        var next = setup.Ticket with { Plan = setup.Ticket.Plan with { Id = Guid.NewGuid() } };
        var rejected = await Commit(c, next, setup.Permit with { SessionId = Guid.NewGuid() });
        Assert.Equal(PaperAutomationReason.KillSwitchLatched, rejected.AutomationReason);
        Assert.Equal(automaticWins ? 1 : 0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, setup.Permit.GenerationId));
    }
    [Theory] [InlineData("manual")] [InlineData("risk")] [InlineData("reset")] [InlineData("settlement")]
    public async Task Competing_writers_preserve_accounting_and_risk(string competitor)
    {
        var setup = await Prepare(); await using var c = setup.Case;
        PaperResolutionPreviewResponse? settlement = null;
        if (competitor == "settlement")
        {
            var p = await c.Preview(1); Assert.NotNull((await c.Execute(c.Request(p))).Execution);
            settlement = await SettlementTests.Preview(c, setup.Permit.GenerationId); Assert.True(settlement.WouldSettle);
            // Refresh the reviewed financial revision after creating the settlement fixture position.
            p = await c.Preview(1); setup.Ticket = c.Fixture.Services.GetRequiredService<PaperPreviewCache>().Find(p.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        }
        var manual = await c.Preview(1); using var barrier = new Barrier(2);
        var automatic = Task.Run(async () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(20))); return await Commit(c, setup.Ticket, setup.Permit); });
        var other = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(20)));
            if (competitor == "manual") await c.Execute(c.Request(manual));
            if (competitor == "risk") (await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(setup.Permit.RiskRevision, true,
                PaperRiskApiTests.Permissive with { MaximumRequestedQuantity = 1 }))).EnsureSuccessStatusCode();
            if (competitor == "reset") (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, setup.Permit.GenerationId, "Barrier reset", [new("Kalshi", "USD", 100), new("Polymarket", "USD", 100)]))).EnsureSuccessStatusCode();
            if (competitor == "settlement") await SettlementTests.Confirm(c, SettlementTests.Request(settlement!));
        });
        await Task.WhenAll(automatic, other);
        Assert.All((await c.Account()).Balances, b => Assert.True(b.AvailableCash >= 0));
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, setup.Permit.GenerationId));
        Assert.InRange(await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()), competitor == "settlement" ? 1 : 0, 2);
        var committed = await automatic;
        if (committed.Execution is not null)
        {
            var retry = await Commit(c, setup.Ticket, setup.Permit); Assert.True(retry.Duplicate); Assert.Equal(committed.Execution.Id, retry.Execution!.Id);
        }
    }
}
