using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class SettlementPersistenceTests
{
    [Fact] public async Task Restart_retains_resolution_history_cash_and_durable_idempotency_without_preview()
    {
        string root, path; Guid generation, id; ConfirmResolutionRequest request;
        await using (var c = new PaperApiTests.Case(preserveStorage: true))
        {
            await c.Start(); var e = await SettlementTests.Execute(c); generation = e.GenerationId; root = c.Fixture.Root; path = c.Root;
            request = SettlementTests.Request(await SettlementTests.Preview(c, generation)); id = (await SettlementTests.Confirm(c, request)).Resolution!.Id;
        }
        await using var restarted = new BackendFixture(root: root); using var client = await restarted.AuthenticatedClientAsync();
        var duplicate = (await (await client.PostAsJsonAsync(path + "/paper/resolutions/confirm", request)).Content.ReadFromJsonAsync<PaperResolutionCommitResponse>())!;
        Assert.True(duplicate.Duplicate); Assert.Equal(id, duplicate.Resolution!.Id);
        var history = (await client.GetFromJsonAsync<PaperResolutionResponse[]>(path + "/paper/resolutions"))!; Assert.Single(history);
        Assert.Equal(5.832m, Assert.Single(history[0].Positions).RealizedPnl);
        var performance = (await client.GetFromJsonAsync<PaperPerformanceResponse>(path + $"/paper/performance?generationId={generation}"))!;
        Assert.Equal(105.832m, performance.Buckets.Single(b => b.Exchange == "Kalshi").CurrentCash);
        Assert.Equal(1, await restarted.WithDatabaseAsync(db => db.Set<PaperResolutionEntry>().CountAsync()));
    }
    [Fact] public async Task Additive_migration_preserves_04A_positions_cost_and_committed_executions()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c);
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            // Isolated fixture only: remove the empty 04B extension to model a genuine 04A database.
            await db.Database.MigrateAsync("20260923092015_PaperExecution");
            await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
            var execution = await db.Set<PaperExecutionEntry>().SingleAsync(); Assert.Equal(e.Id, execution.Id); Assert.Equal(PaperExecutionState.Committed, execution.State);
            Assert.Equal(2, JsonSerializer.Deserialize<SettlementMarket[]>(execution.SettlementMarketsJson)!.Length);
            var positions = await db.Set<PaperPositionEntry>().ToArrayAsync(); Assert.Equal(9.36792m, positions.Sum(p => p.CostBasis));
            Assert.All(positions, p => { Assert.Equal(PaperPositionStatus.Open, p.Status); Assert.Equal(10, p.Quantity); Assert.Null(p.SettlementResolutionId); });
            Assert.False(db.Database.HasPendingModelChanges()); return 0;
        });
        await SettlementTests.Settle(c, e.GenerationId); Assert.Equal("Healthy", await SettlementTests.Reconcile(c, e.GenerationId));
    }
    [Fact] public async Task Auth_membership_owner_and_cross_workspace_resolution_boundaries()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c);
        var p = await SettlementTests.Preview(c, e.GenerationId); var request = SettlementTests.Request(p);
        using var anon = c.Fixture.CreateClient();
        foreach (var route in new[] { "/resolutions", $"/resolution-candidates?generationId={e.GenerationId}", $"/performance?generationId={e.GenerationId}" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(c.Root + "/paper" + route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(c.Root + "/paper/resolutions/confirm", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/resolutions")).StatusCode);
        Assert.Equal("ConfirmationRequired", (await SettlementTests.Confirm(c, request with { ConfirmSimulation = false })).Rejection);
        var resolved = (await SettlementTests.Confirm(c, request)).Resolution!;
        var other = Guid.NewGuid();
        await c.Fixture.WithDatabaseAsync(async db => { db.Add(new Workspace(other, "Another workspace", c.Clock.Now)); db.Add(new WorkspaceMembership(c.Session.UserId, other)); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync($"/api/v1/workspaces/{other}/paper/resolutions/{resolved.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync($"/api/v1/workspaces/{other}/paper/performance?generationId={e.GenerationId}")).StatusCode);
        // The current schema permits only Owner. Unknown roles are rejected by the database itself.
        await Assert.ThrowsAsync<DbUpdateException>(() => c.Fixture.WithDatabaseAsync(async db => { var member = await db.Memberships.SingleAsync(m => m.WorkspaceId == c.Session.DefaultWorkspaceId); db.Entry(member).Property(m => m.Role).CurrentValue = (WorkspaceRole)99; return await db.SaveChangesAsync(); }));
        await c.Fixture.WithDatabaseAsync(async db => { db.Memberships.Remove(await db.Memberships.SingleAsync(m => m.WorkspaceId == other)); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.OK, (await c.Client.GetAsync(c.Root + "/paper/resolutions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.PostAsJsonAsync($"/api/v1/workspaces/{other}/paper/resolutions/confirm", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.PostAsJsonAsync($"/api/v1/workspaces/{other}/paper/resolutions/preview", p.Selection)).StatusCode);
    }
    [Fact] public async Task Curve_exact_realized_deltas_separate_currencies_and_paged_carry_forward()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(currency: "USDC"); var generation = (await c.Account()).Generation!.Id;
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            // A curve-specific journal fixture, independent of the executor and current market prices.
            foreach (var (delta, market) in new[] { (.50m, "curve-one"), (-.20m, "curve-two") })
            {
                var id = Guid.NewGuid(); var at = c.Clock.Now.AddSeconds(market == "curve-one" ? 1 : 2);
                db.Add(new PaperResolutionEntry { Id = id, WorkspaceId = c.Session.DefaultWorkspaceId, GenerationId = generation, ActorId = c.Session.UserId, RequestId = Guid.NewGuid(), Exchange = "Kalshi", MarketId = market, RecordedAt = at, ResolvedAt = at });
                db.Add(new PaperTransactionEntry { Id = Guid.NewGuid(), GenerationId = generation, ActorId = c.Session.UserId, ResolutionId = id, Reason = "PaperSettlement", CreatedAt = at });
                db.Add(new PaperPositionEntry { Id = Guid.NewGuid(), GenerationId = generation, Exchange = "Kalshi", Currency = "USD", MarketId = market, InstrumentId = "yes", Outcome = "Yes", Quantity = 1,
                    CostBasis = delta > 0 ? .5m : .2m, Status = PaperPositionStatus.Settled, SettlementResolutionId = id, SettlementPayout = delta > 0 ? 1 : 0, RealizedPnl = delta, SettledAt = at });
            }
            return await db.SaveChangesAsync();
        });
        var root = c.Root + $"/paper/performance/curve?generationId={generation}";
        var usd = (await c.Client.GetFromJsonAsync<PaperCurveResponse>(root + "&exchange=Kalshi&currency=USD"))!;
        Assert.Equal(new[] { 100m, 100.50m, 100.30m }, usd.Points.Select(p => p.RealizedPerformance));
        var last = (await c.Client.GetFromJsonAsync<PaperCurveResponse>(root + "&exchange=Kalshi&currency=USD&pageSize=1&page=3"))!;
        Assert.Equal(100.30m, Assert.Single(last.Points).RealizedPerformance); Assert.False(last.HasMore);
        var usdc = (await c.Client.GetFromJsonAsync<PaperCurveResponse>(root + "&exchange=Polymarket&currency=USDC"))!;
        Assert.Equal(100, Assert.Single(usdc.Points).RealizedPerformance); Assert.Equal("USDC", usdc.Currency);
    }
}
