using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class RelationshipApiTests
{
    [Fact]
    public async Task Auth_membership_generation_paging_manual_review_and_stale_filter_are_enforced()
    {
        await using var fixture = new BackendFixture(); using var client = await fixture.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var root = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/relationships";
        using var anonymous = fixture.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/relationships/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/relationships/generate", new GenerateRelationshipsRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/?pageSize=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/?state=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/generate", new GenerateRelationshipsRequest(NativeId: "missing-exchange"))).StatusCode);
        await fixture.WithDatabaseAsync(async db => { db.AddRange(RelationshipPersistenceTests.Market("Kalshi", "a"), RelationshipPersistenceTests.Market("Polymarket", "b")); return await db.SaveChangesAsync(); });
        var response = await client.PostAsJsonAsync(root + "/generate", new GenerateRelationshipsRequest()); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<RelationshipJobResponse>())!;
        for (var i = 0; i < 100 && job.State == "Running"; i++) { await Task.Delay(20); job = (await client.GetFromJsonAsync<RelationshipJobResponse>(root + "/jobs/" + job.Id))!; }
        Assert.Equal("Complete", job.State);
        var page = (await client.GetFromJsonAsync<RelationshipPageResponse>(root + "/?pageSize=1"))!; Assert.Equal(1, page.Total); Assert.Single(page.Items);
        var id = page.Items[0].Id; var detail = (await client.GetFromJsonAsync<RelationshipDetailResponse>(root + "/" + id))!;
        Assert.Equal("NeedsReview", detail.Summary.State); Assert.Contains(detail.Evidence, e => e.Blocking);
        var review = new ReviewRelationshipRequest(detail.SourceFingerprint, detail.TargetFingerprint, "Fixture review", "EquivalentSameOutcome", [new("yes", "token-yes", "EquivalentSameOutcome")], true);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/" + id + "/verify", review with { Confirmed = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/" + id + "/verify", review with { Mappings = [new("unknown", "unknown", "EquivalentSameOutcome")] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root + "/" + id + "/verify", review)).StatusCode);
        await fixture.WithDatabaseAsync(async db => { (await db.CatalogMarkets.FirstAsync()).Rules = "changed"; return await db.SaveChangesAsync(); });
        var stale = (await client.GetFromJsonAsync<RelationshipPageResponse>(root + "/?state=Stale"))!; Assert.Single(stale.Items);
        Assert.False((await client.GetFromJsonAsync<RelationshipDetailResponse>(root + "/" + id))!.IsStrategyEligible);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/" + id + "/verify", review)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(root + "/" + id + "/revalidate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(root + "/jobs/" + job.Id + "/cancel", null)).StatusCode);
    }
}
