using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperConcurrencyTests
{
    [Theory] [InlineData("enough", 100, 1)] [InlineData("overspend", 8, 1)] [InlineData("duplicate", 100, 1)]
    public async Task Concurrent_baskets_serialize_cash_and_durable_request_identity(string scenario, decimal balance, int committed)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(balance);
        var p = await c.Preview(); Assert.True(p.WouldExecute, p.Rejection);
        var r1 = c.Request(p); var r2 = scenario == "duplicate" ? r1 : c.Request(p);
        using var barrier = new Barrier(2);
        Task<PaperCommitResponse> Send(ConfirmPaperRequest r) => Task.Run(async () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return await c.Execute(r); });
        var results = await Task.WhenAll(Send(r1), Send(r2));
        Assert.Equal(committed, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal(committed * 3, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperFillEntry>().CountAsync()));
        if (scenario == "duplicate") { Assert.All(results, r => Assert.Equal("Committed", r.State)); Assert.Single(results, r => r.Duplicate); }
        if (scenario == "overspend") Assert.Contains(results, r => r.Rejection == "InsufficientPaperFunds");
        if (scenario == "enough") Assert.Contains(results, r => r.Rejection == "FinancialStateChanged"); // Reviewed headroom changed; a fresh preview is required.
        Assert.All((await c.Account()).Balances, b => Assert.True(b.AvailableCash >= 0 && b.ReservedCash == 0));
    }
    [Theory] [InlineData("shutdown")] [InlineData("last-book-check")]
    public async Task Failure_after_plan_or_save_rolls_back_entire_basket(string failure)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var p = await c.Preview();
        var ticket = c.Fixture.Services.GetRequiredService<PaperPreviewCache>().Find(p.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        using var cancel = new CancellationTokenSource();
        await using (var scope = c.Fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PaperStore>(); var validations = 0;
            Task<PaperRejection> Validate(CancellationToken ct)
            { if (++validations == 2 && failure == "shutdown") cancel.Cancel(); return Task.FromResult(PaperRejection.None); }
            var task = store.CommitAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, ticket.Generation, Guid.NewGuid(), "fixture", ticket.Plan, "rollback-test", Validate, _ => false, cancel.Token, ticket.Risk);
            if (failure == "shutdown") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            else Assert.Equal(PaperRejection.MarketDataChanged, (await task).Rejection);
        }
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            Assert.Empty(await db.Set<PaperExecutionEntry>().ToArrayAsync()); Assert.Empty(await db.Set<PaperFillEntry>().ToArrayAsync());
            Assert.Empty(await db.Set<PaperLegEntry>().ToArrayAsync()); Assert.Empty(await db.Set<PaperPositionEntry>().ToArrayAsync());
            Assert.Single(await db.Set<PaperTransactionEntry>().ToArrayAsync()); Assert.Equal(2, await db.Set<PaperLedgerEntry>().CountAsync()); return 0;
        });
        Assert.All((await c.Account()).Balances, b => Assert.Equal(100, b.AvailableCash));
    }
    [Fact] public async Task Reset_waits_for_inflight_writer_then_closes_generation_without_partial_execution()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var p = await c.Preview();
        var ticket = c.Fixture.Services.GetRequiredService<PaperPreviewCache>().Find(p.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = Task.Run(async () =>
        {
            await using var scope = c.Fixture.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaperStore>().CommitAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, ticket.Generation,
                Guid.NewGuid(), "reset-barrier", ticket.Plan, "reset-barrier", async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return PaperRejection.None; },
                commit => { commit(); return true; }, default, ticket.Risk);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = Task.Run(async () =>
        {
            resetEntered.TrySetResult(); return await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, ticket.Generation,
                "Concurrent reset", [new("Kalshi", "USD", 200), new("Polymarket", "USD", 200)]));
        });
        await resetEntered.Task; release.TrySetResult(); Assert.NotNull((await execute).Execution); (await reset).EnsureSuccessStatusCode();
        var account = await c.Account(); Assert.Equal(2, account.History.Length); Assert.All(account.Balances, b => Assert.Equal(200, b.AvailableCash));
        Assert.Equal("GenerationChanged", (await c.Execute(c.Request(p))).Rejection);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
    }
    [Fact] public async Task Reopening_SQLite_preserves_fills_proofs_idempotency_and_reconciliation_without_replay()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var p = await c.Preview(); var request = c.Request(p); var result = await c.Execute(request);
        await using var reopened = new TradingDbContext(DatabaseOptions.ForFile(Path.Combine(c.Fixture.Root, "backend", "arbitrage.db")));
        var store = new PaperStore(reopened, new RelationshipStore(reopened, c.Clock), c.Clock);
        var record = await store.RequestAsync(c.Session.DefaultWorkspaceId, request.RequestId, default);
        Assert.Equal(result.Execution!.Id, record!.Id); var plan = JsonSerializer.Deserialize<PaperPlan>(record.PlanJson)!;
        Assert.Equal(3, plan.Fills.Length); Assert.Equal(.63208m, plan.ExpectedProfitAtResolution);
        Assert.Equal(PaperIntegrity.Healthy, await store.ReconcileAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, p.GenerationId!.Value, default));
        Assert.Equal(1, await reopened.Set<PaperExecutionEntry>().CountAsync()); Assert.Equal(3, await reopened.Set<PaperFillEntry>().CountAsync());
    }
}
