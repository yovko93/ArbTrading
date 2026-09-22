using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Arbitrage.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class OpportunityApiTests
{
    internal static async Task<Guid> Seed(BackendFixture fixture, VerificationState state = VerificationState.VerifiedManual, string suffix = "") =>
        await fixture.WithDatabaseAsync(async db =>
        {
            var profile = await db.LocalProfiles.SingleAsync();
            var a = RelationshipPersistenceTests.Market("Kalshi", "a" + suffix); a.Classification = "binary";
            var b = RelationshipPersistenceTests.Market("Polymarket", "b" + suffix); b.Classification = "binary";
            b.OutcomesJson = JsonSerializer.Serialize(new MarketOutcome[] { new("Yes", "123"), new("No", "456") });
            db.AddRange(a, b); await db.SaveChangesAsync();
            var store = new RelationshipStore(db, TimeProvider.System);
            var row = await store.GeneratePairAsync(profile.UserId, profile.DefaultWorkspaceId, CatalogSemantics.Describe(a), CatalogSemantics.Describe(b), default);
            await store.ReviewAsync(profile.UserId, profile.DefaultWorkspaceId, row.Id, row.SourceFingerprint, row.TargetFingerprint, "Explicit fixture complement proof",
                RelationshipType.EquivalentOppositeOutcome, [new("yes", "456", RelationshipType.EquivalentOppositeOutcome)], false, default);
            // Exercise the production provider against persisted trust states; never replace eligibility with a stub.
            row.State = state; await db.SaveChangesAsync(); return row.Id;
        });
    internal static void Books(BackendFixture fixture, string suffix = "", DateTimeOffset? at = null)
    {
        var cache = fixture.Services.GetRequiredService<OrderBookCache>(); var now = at ?? DateTimeOffset.UtcNow;
        cache.Store(OrderBookNormalizer.NormalizeBinary(new("Kalshi", "a" + suffix, "yes", "Yes"), [new(.1m, 100, LiquidityOrigin.NativeBid)], [new(.6m, 25, LiquidityOrigin.NativeBid)], now));
        cache.Store(OrderBookNormalizer.Normalize(new("Polymarket", "b" + suffix, "456", "No"), [], [new(.5m, 25, LiquidityOrigin.NativeAsk)], now));
    }
    internal static async Task<OpportunityJobResponse> Finish(HttpClient client, string root, OpportunityJobResponse job)
    {
        for (var i = 0; i < 250 && job.State == "Running"; i++) { await Task.Delay(20); job = (await client.GetFromJsonAsync<OpportunityJobResponse>($"{root}/jobs/{job.Id}"))!; }
        Assert.NotEqual("Running", job.State); return job;
    }
    internal static async Task<OpportunityJobResponse> Start(HttpClient client, string root, EvaluateOpportunitiesRequest request)
    {
        using var response = await client.PostAsJsonAsync(root + "/evaluate", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await Finish(client, root, (await response.Content.ReadFromJsonAsync<OpportunityJobResponse>())!);
    }
    [Fact] public async Task API_default_manual_opt_in_paging_native_provenance_and_current_invalidation()
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var root = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/opportunities";
        var id = await Seed(f); await Seed(f, suffix: "2"); Books(f); Books(f, "2");
        var empty = await Start(client, root, new()); Assert.Equal(0, empty.Results);
        var run = await Start(client, root, new(IncludeManualRelationships: true)); Assert.Equal("Completed", run.State); Assert.Equal(2, run.Results);
        var page = (await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{run.Id}/results?pageSize=1&sort=grossProfit"))!;
        Assert.Equal(2, page.Total); var r = Assert.Single(page.Items); Assert.Equal("Detected", r.Status); Assert.Equal("Manual", r.RelationshipTrust);
        Assert.Equal(2.5m, r.GrossProfit); Assert.Null(r.NetProfit); Assert.Null(r.NetEdge); Assert.False(r.ExecutionEligible);
        Assert.Contains("DerivedComplement", r.Legs[0].LiquidityOrigins); Assert.Equal("456", r.Legs[1].InstrumentId);
        var next = (await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{run.Id}/results?pageSize=1&page=2"))!;
        Assert.NotEqual(r.OpportunityKey, Assert.Single(next.Items).OpportunityKey);
        Books(f, r.RelationshipId == id ? "" : "2");
        var changed = (await client.GetFromJsonAsync<OpportunityResponse>($"{root}/current/{r.OpportunityKey}"))!;
        Assert.Equal("StaleInput", changed.Status); Assert.False(changed.GrossArbitrageExists);
        await f.WithDatabaseAsync(async db => { (await db.MarketRelationships.SingleAsync(x => x.Id == next.Items[0].RelationshipId)).State = VerificationState.Rejected; return await db.SaveChangesAsync(); });
        var rejected = (await client.GetFromJsonAsync<OpportunityResponse>($"{root}/current/{next.Items[0].OpportunityKey}"))!;
        Assert.Equal("RelationshipIneligible", rejected.Status); Assert.False(rejected.RelationshipEligible);
        Assert.Empty((await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{run.Id}/results"))!.Items);
        Assert.Equal(2, (await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{run.Id}/results?diagnostics=true"))!.Total);
    }
    [Theory] [InlineData(VerificationState.Proposed)] [InlineData(VerificationState.NeedsReview)] [InlineData(VerificationState.Rejected)] [InlineData(VerificationState.Stale)]
    public async Task Persisted_unapproved_states_never_enter_evaluation(VerificationState state)
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync();
        var id = await Seed(f, state); Books(f);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync());
        var job = await Start(client, $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities", new(id, true)); Assert.Equal(0, job.Results);
    }
    [Theory] [InlineData("fingerprint")] [InlineData("policy")] [InlineData("mapping")]
    public async Task Retained_results_recheck_catalog_policy_and_approval_revision(string mutation)
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync();
        var id = await Seed(f, VerificationState.VerifiedDeterministic); Books(f);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        var job = await Start(client, root, new(id)); var result = Assert.Single((await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{job.Id}/results"))!.Items);
        await f.WithDatabaseAsync(async db =>
        {
            var row = await db.MarketRelationships.Include(r => r.Mappings).SingleAsync(r => r.Id == id);
            if (mutation == "fingerprint") (await db.CatalogMarkets.FirstAsync()).Rules = "materially changed";
            if (mutation == "policy") row.PolicyVersion = -1;
            if (mutation == "mapping") row.Mappings.First().Type = RelationshipType.EquivalentSameOutcome;
            return await db.SaveChangesAsync();
        });
        var current = (await client.GetFromJsonAsync<OpportunityResponse>($"{root}/current/{result.OpportunityKey}"))!;
        Assert.Equal("RelationshipIneligible", current.Status); Assert.False(current.GrossArbitrageExists);
    }
    [Fact] public async Task Authorization_bounds_workspace_isolation_and_selected_endpoint()
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync(); using var anonymous = f.CreateClient();
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(root + "/evaluate", new EvaluateOpportunitiesRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/opportunities/evaluate", new EvaluateOpportunitiesRequest())).StatusCode);
        foreach (var invalid in new[] { new EvaluateOpportunitiesRequest(MaximumRelationshipsPerRun: 501), new(MaximumOpportunitiesReturned: 51), new(RuntimeSeconds: 0), new(Exchange: "unknown"), new(MinimumGrossEdgePerShare: 1), new(MaximumEvaluationQuantity: 0), new(MaximumSkewMilliseconds: -1), new(MaximumEvaluationNotional: -1) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/evaluate", invalid)).StatusCode);
        var id = await Seed(f, VerificationState.VerifiedDeterministic);
        using var response = await client.PostAsJsonAsync($"{root}/relationships/{id}/evaluate", new EvaluateOpportunitiesRequest()); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await Finish(client, root, (await response.Content.ReadFromJsonAsync<OpportunityJobResponse>())!);
        var missing = Assert.Single((await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/jobs/{job.Id}/results?diagnostics=true"))!.Items); Assert.Equal("BookUnavailable", missing.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{root}/jobs/{job.Id}/results?pageSize=51")).StatusCode);
        var other = Guid.NewGuid(); await f.WithDatabaseAsync(async db => { db.AddRange(new Workspace(other, "Other", DateTimeOffset.UtcNow), new WorkspaceMembership(profile.UserId, other)); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{other}/opportunities/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{other}/opportunities/current/{missing.OpportunityKey}")).StatusCode);
        await f.WithDatabaseAsync(async db => { await db.LocalProfiles.ExecuteUpdateAsync(s => s.SetProperty(p => p.DefaultWorkspaceId, other)); db.Memberships.Remove(await db.Memberships.SingleAsync(m => m.WorkspaceId == profile.DefaultWorkspaceId)); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{root}/jobs/{job.Id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{root}/jobs/{job.Id}/cancel", null)).StatusCode);
    }
    private sealed class CallbackClock(Action callback) : TimeProvider { public override DateTimeOffset GetUtcNow() { callback(); return DateTimeOffset.UtcNow; } }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Mid_evaluation_cache_and_relationship_changes_are_discarded(bool semanticChange)
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync(); var id = await Seed(f, VerificationState.VerifiedDeterministic); Books(f);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); using var scope = f.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IRelationshipProvider>();
        var relationship = Assert.Single((await provider.ReadEvaluationPageAsync(profile.UserId, profile.DefaultWorkspaceId, false, id, null, 0, 1, default)).Items);
        var clock = new CallbackClock(() =>
        {
            if (!semanticChange) Books(f);
            else { using var other = f.Services.CreateScope(); var db = other.ServiceProvider.GetRequiredService<TradingDbContext>(); db.CatalogMarkets.First().Rules = "changed during calculation"; db.SaveChanges(); }
        });
        var coordinator = new OpportunityCoordinator(provider, scope.ServiceProvider.GetRequiredService<OrderBookService>(), f.Services.GetRequiredService<OrderBookCache>(), clock);
        var result = await coordinator.EvaluateAsync(profile.UserId, profile.DefaultWorkspaceId, Assert.Single(OpportunityPlanner.Plan(relationship)), new(), false, default);
        Assert.Equal(semanticChange ? OpportunityStatus.RelationshipIneligible : OpportunityStatus.BooksChangedDuringEvaluation, result.Status);
    }
    [Fact] public async Task Bounds_report_partial_and_no_external_book_requests_are_made()
    {
        var spy = new RejectBookSource();
        await using var f = new BackendFixture(s => { s.AddSingleton<IOrderBookSource>(spy); }); using var client = await f.AuthenticatedClientAsync();
        await Seed(f, VerificationState.VerifiedDeterministic); await Seed(f, VerificationState.VerifiedDeterministic, "2"); Books(f); Books(f, "2");
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        Assert.Equal("Partial", (await Start(client, root, new(MaximumRelationshipsPerRun: 1))).State);
        Assert.Equal("Partial", (await Start(client, root, new(MaximumOpportunitiesReturned: 1))).State);
        Assert.Equal(0, spy.Calls);
    }
    private sealed class RejectBookSource : IOrderBookSource
    {
        public string Exchange => "Kalshi"; public int Calls;
        public Task<OrderBookSnapshot> ReadAsync(OrderBookRequest request, CancellationToken ct) { Calls++; throw new InvalidOperationException("Forbidden external fetch"); }
    }
}
