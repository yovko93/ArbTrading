using System.Net;
using System.Net.Http.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Connectors;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MonitoringApiTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        // Fixtures drive ProcessOnceAsync directly; no wall-clock sleeps or timer races.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class RejectFees : IPublicFeeSource
    { public int Calls; public Task<FeeSchedule> ReadAsync(string exchange, string marketId, CancellationToken ct) { Calls++; throw new InvalidOperationException("No external calls allowed."); } }
    private sealed class RejectBooks(string exchange) : IOrderBookSource
    { public string Exchange => exchange; public int Calls; public Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken ct) { Calls++; throw new InvalidOperationException("No book fetch allowed."); } }
    private sealed class RejectSockets : IMarketWebSocketFactory
    { public int Calls; public IMarketWebSocket Create() { Calls++; throw new InvalidOperationException("No subscriptions allowed."); } }
    private sealed class Gate
    {
        public int Reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class DelayedProvider(RelationshipStore store, Gate gate) : IRelationshipProvider
    {
        public Task<IReadOnlyList<ApprovedRelationship>> ReadApprovedAsync(Guid actor, Guid workspace, bool manual, CancellationToken ct) => store.ReadApprovedAsync(actor, workspace, manual, ct);
        public async Task<ApprovedRelationshipPage> ReadEvaluationPageAsync(Guid actor, Guid workspace, bool manual, Guid? id, string? exchange, int skip, int take, CancellationToken ct)
        {
            var page = await store.ReadEvaluationPageAsync(actor, workspace, manual, id, exchange, skip, take, ct);
            if (id is not null && Interlocked.Increment(ref gate.Reads) == 1) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(ct); }
            return page;
        }
    }
    [Theory] [InlineData("stop")] [InlineData("shutdown")] [InlineData("book")]
    public async Task Late_evaluation_never_publishes_after_stop_shutdown_or_input_change(string action)
    {
        var clock = new Clock(); var gate = new Gate();
        await using var f = new BackendFixture(s => { s.AddSingleton<TimeProvider>(clock); s.AddScoped<IRelationshipProvider>(sp => new DelayedProvider(sp.GetRequiredService<RelationshipStore>(), gate)); });
        using var client = await f.AuthenticatedClientAsync(); await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic); OpportunityApiTests.Books(f, at: clock.Now);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var monitor = f.Services.GetRequiredService<MonitoringCoordinator>();
        monitor.StartMonitoring(identity.UserId, identity.DefaultWorkspaceId, new(EnableGrossOnlyAlerts: true)); var pending = monitor.ProcessOnceAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "stop") monitor.StopMonitoring(identity.UserId, identity.DefaultWorkspaceId);
        if (action == "shutdown") await monitor.StopAsync(default);
        if (action == "book") OpportunityApiTests.Books(f, at: clock.Now);
        gate.Release.TrySetResult(); await pending;
        if (action == "stop") await monitor.ProcessOnceAsync();
        var rows = await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default);
        Assert.All(rows, row => Assert.Equal(RankingLane.Blocked, row.Lane));
        Assert.Equal(0, monitor.Status(identity.DefaultWorkspaceId).AlertsRaised);
        if (action != "book") { Assert.Empty(rows); Assert.Equal(MonitoringState.Stopped, monitor.Status(identity.DefaultWorkspaceId).State); }
    }
    [Fact] public async Task Fee_boundary_profile_changes_and_public_source_conflict_fail_closed()
    {
        var clock = new Clock(); var source = new RejectFees();
        await using var f = new BackendFixture(s => { s.AddSingleton<TimeProvider>(clock); s.AddSingleton<IPublicFeeSource>(source); });
        using var client = await f.AuthenticatedClientAsync(); await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync());
        await f.WithDatabaseAsync(async db =>
        {
            var store = new FeeStore(db); var k = FeeApiTests.Schedule("Kalshi", "a") with { RetrievedAt = clock.Now };
            await store.SaveAsync(k with { Rules = k.Rules.Add(new("future", "quadratic", 2, clock.Now.AddSeconds(1), FeeSourceLevel.Series)) }, default);
            await store.SaveAsync(FeeApiTests.Schedule("Polymarket", "b") with { RetrievedAt = clock.Now }, default);
            await store.SetProfileAsync(identity.DefaultWorkspaceId, KalshiFeeAccountProfile.DirectMember, default); return 0;
        });
        OpportunityApiTests.Books(f, at: clock.Now); var monitor = f.Services.GetRequiredService<MonitoringCoordinator>();
        monitor.StartMonitoring(identity.UserId, identity.DefaultWorkspaceId, new()); await monitor.ProcessOnceAsync();
        var before = Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)); Assert.Equal(RankingLane.FeeAdjusted, before.Lane);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(RankingLane.GrossOnly, Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)).Lane);
        await monitor.ProcessOnceAsync(); var after = Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default));
        Assert.Equal(before.Result.GrossProfit, after.Result.GrossProfit); Assert.NotEqual(before.Result.Fees!.TotalExchangeFees, after.Result.Fees!.TotalExchangeFees);
        await f.WithDatabaseAsync(async db => { await new FeeStore(db).SetProfileAsync(identity.DefaultWorkspaceId, KalshiFeeAccountProfile.Unknown, default); return 0; });
        await monitor.ProcessOnceAsync(); Assert.Equal(RankingLane.GrossOnly, Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)).Lane);
        await f.WithDatabaseAsync(async db =>
        {
            var store = new FeeStore(db); await store.SetProfileAsync(identity.DefaultWorkspaceId, KalshiFeeAccountProfile.DirectMember, default);
            await store.SaveAsync(FeeApiTests.Schedule("Kalshi", "a") with { RetrievedAt = clock.Now, VerificationIssue = "Unresolved official public rounding-source conflict" }, default); return 0;
        });
        var alerts = monitor.Status(identity.DefaultWorkspaceId).AlertsRaised; await monitor.ProcessOnceAsync();
        var conflict = Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default));
        Assert.Equal(RankingLane.GrossOnly, conflict.Lane); Assert.Null(conflict.Result.Fees!.TotalExchangeFees);
        Assert.Equal(alerts, monitor.Status(identity.DefaultWorkspaceId).AlertsRaised); Assert.Equal(0, source.Calls);
    }
    [Fact] public async Task Explicit_lifecycle_no_fetch_default_manual_exclusion_and_stop_audit()
    {
        var clock = new Clock(); var source = new RejectFees(); var kalshi = new RejectBooks("Kalshi"); var poly = new RejectBooks("Polymarket"); var sockets = new RejectSockets();
        await using var f = new BackendFixture(s => { s.AddSingleton<TimeProvider>(clock); s.AddSingleton<IPublicFeeSource>(source);
            s.RemoveAll<IOrderBookSource>(); s.AddSingleton<IOrderBookSource>(kalshi); s.AddSingleton<IOrderBookSource>(poly); s.AddSingleton<IMarketWebSocketFactory>(sockets); });
        using var client = await f.AuthenticatedClientAsync(); await OpportunityApiTests.Seed(f); OpportunityApiTests.Books(f, at: clock.Now);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{identity.DefaultWorkspaceId}/monitoring";
        var monitor = f.Services.GetRequiredService<MonitoringCoordinator>();
        Assert.Equal("Stopped", (await client.GetFromJsonAsync<MonitoringStatusResponse>(root + "/status"))!.State);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(root + "/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(root + "/start", null)).StatusCode);
        await monitor.ProcessOnceAsync(); Assert.Equal(0, monitor.Status(identity.DefaultWorkspaceId).Coverage.PlansBuilt);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(root + "/profile", new MonitoringProfileResponse(IncludeManualRelationships: true))).StatusCode);
        await monitor.ProcessOnceAsync();
        var row = Assert.Single((await client.GetFromJsonAsync<MonitoringRankingPage>(root + "/rankings"))!.Items);
        Assert.Equal("GrossOnly", row.Lane); Assert.Equal("Manual", row.Opportunity.RelationshipTrust); Assert.False(row.Opportunity.ExecutionEligible); Assert.Null(row.Opportunity.NetProfit);
        Assert.Empty((await client.GetFromJsonAsync<MonitoringAlertPage>(root + "/alerts"))!.Items);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, kalshi.Calls + poly.Calls + sockets.Calls);
        await client.PostAsync(root + "/stop", null); await monitor.ProcessOnceAsync(); Assert.Equal(MonitoringState.Stopped, monitor.Status(identity.DefaultWorkspaceId).State);
        Assert.Empty((await client.GetFromJsonAsync<MonitoringRankingPage>(root + "/rankings"))!.Items);
        var audits = await f.WithDatabaseAsync(db => db.AuditRecords.Select(a => a.Action).ToArrayAsync());
        Assert.Contains("MonitoringStarted", audits); Assert.Contains("MonitoringStopped", audits); Assert.Contains("MonitoringProfileUpdated", audits);
    }
    [Fact] public async Task Read_validation_and_sweep_remove_stale_positive_results_without_notifications()
    {
        var clock = new Clock(); await using var f = new BackendFixture(s => s.AddSingleton<TimeProvider>(clock));
        using var client = await f.AuthenticatedClientAsync(); var id = await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic); OpportunityApiTests.Books(f, at: clock.Now);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var monitor = f.Services.GetRequiredService<MonitoringCoordinator>();
        monitor.StartMonitoring(identity.UserId, identity.DefaultWorkspaceId, new()); await monitor.ProcessOnceAsync();
        var before = Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)); Assert.Equal(RankingLane.GrossOnly, before.Lane);
        clock.Now += TimeSpan.FromSeconds(6);
        var stale = Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)); Assert.Equal(RankingLane.Blocked, stale.Lane);
        Assert.Equal(OpportunityStatus.BookStale, stale.Result.Status); Assert.Null(stale.BestEdge);
        OpportunityApiTests.Books(f, at: clock.Now); await monitor.ProcessOnceAsync();
        Assert.Equal(RankingLane.GrossOnly, Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)).Lane);
        await f.WithDatabaseAsync(db => db.MarketRelationships.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.State, VerificationState.Rejected)));
        Assert.Equal(RankingLane.Blocked, Assert.Single(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default)).Lane);
        clock.Now += TimeSpan.FromSeconds(1); await monitor.ProcessOnceAsync(); Assert.Empty(await monitor.RankingsAsync(identity.UserId, identity.DefaultWorkspaceId, default));
    }
    [Fact] public async Task Gross_alert_opt_in_is_transition_only_and_history_survives_stop()
    {
        var clock = new Clock(); await using var f = new BackendFixture(s => s.AddSingleton<TimeProvider>(clock));
        using var client = await f.AuthenticatedClientAsync(); await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic); OpportunityApiTests.Books(f, at: clock.Now);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var monitor = f.Services.GetRequiredService<MonitoringCoordinator>();
        monitor.StartMonitoring(identity.UserId, identity.DefaultWorkspaceId, new(EnableGrossOnlyAlerts: true)); await monitor.ProcessOnceAsync();
        for (var i = 0; i < 10; i++) { OpportunityApiTests.Books(f, at: clock.Now); await monitor.ProcessOnceAsync(); }
        var root = $"/api/v1/workspaces/{identity.DefaultWorkspaceId}/monitoring";
        var alert = Assert.Single((await client.GetFromJsonAsync<MonitoringAlertPage>(root + "/alerts"))!.Items);
        Assert.Equal("GrossOnly", alert.Lane); Assert.Contains("FEES UNRESOLVED", alert.Reason); Assert.Empty(alert.Opportunity.Opportunity.Segments);
        monitor.StopMonitoring(identity.UserId, identity.DefaultWorkspaceId); await monitor.ProcessOnceAsync();
        Assert.Single((await client.GetFromJsonAsync<MonitoringAlertPage>(root + "/alerts"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/current/" + alert.Opportunity.Opportunity.OpportunityKey)).StatusCode);
    }
    [Fact] public async Task Authentication_membership_and_query_bounds_are_enforced()
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync(); using var anonymous = f.CreateClient();
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{identity.DefaultWorkspaceId}/monitoring";
        foreach (var route in new[] { "/status", "/profile", "/rankings", "/alerts" })
        { Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + route)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/monitoring" + route)).StatusCode); }
        foreach (var query in new[] { "pageSize=101", "page=0", "lane=99", "strategy=invalid", "sort=unknown", "quality=0", "feeStatus=0", "trust=Unknown" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/rankings?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root + "/profile", new MonitoringProfileResponse(RelationshipLimit: 1001))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/workspaces/{Guid.NewGuid()}/monitoring/start", null)).StatusCode);
    }
    [Fact] public async Task Overflow_reconciles_and_dependency_notifications_only_reevaluate_affected_plans()
    {
        var clock = new Clock(); await using var f = new BackendFixture(s => s.AddSingleton<TimeProvider>(clock)); using var client = await f.AuthenticatedClientAsync();
        await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic); await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic, "2");
        OpportunityApiTests.Books(f, at: clock.Now); OpportunityApiTests.Books(f, "2", clock.Now);
        var identity = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var monitor = f.Services.GetRequiredService<MonitoringCoordinator>(); var feed = f.Services.GetRequiredService<LocalInputChanges>();
        monitor.StartMonitoring(identity.UserId, identity.DefaultWorkspaceId, new()); await monitor.ProcessOnceAsync(); var count = monitor.Status(identity.DefaultWorkspaceId).EvaluationsCompleted;
        for (var i = 0; i < 1000; i++) feed.Publish(new(LocalChangeKind.Instrument, "Kalshi", "a", "yes"));
        await monitor.ProcessOnceAsync(); Assert.Equal(count + 1, monitor.Status(identity.DefaultWorkspaceId).EvaluationsCompleted);
        for (var i = 0; i < 3000; i++) feed.Publish(new(LocalChangeKind.Market, "Polymarket", "unused" + i));
        await monitor.ProcessOnceAsync(); var status = monitor.Status(identity.DefaultWorkspaceId);
        Assert.True(status.DirtyNotificationsDropped > 0); Assert.True(status.ReconciliationPasses >= 2); Assert.Equal(count + 3, status.EvaluationsCompleted);
    }
}
