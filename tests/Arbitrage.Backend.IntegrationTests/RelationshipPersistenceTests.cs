using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RelationshipPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N"));
    public RelationshipPersistenceTests() => Directory.CreateDirectory(root);
    private TradingDbContext Open() => new(DatabaseOptions.ForFile(Path.Combine(root, "relationships.db")));
    public static MarketCatalogEntry Market(string exchange, string id) => new()
    {
        Exchange = exchange, NativeId = id, Title = "Will Person A win election 2028?", Rules = "fixture rules", Classification = "Binary", Status = "Open", RetrievedAt = DateTimeOffset.UtcNow,
        OutcomesJson = JsonSerializer.Serialize(new MarketOutcome[] { new("Yes", "token-yes"), new("No", "token-no") })
    };
    [Fact]
    public async Task Migrating_phase02_preserves_identity_catalog_run_and_audit()
    {
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var profile = Guid.NewGuid(); var audit = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.UtcTicks;
        await using (var db = Open())
        {
            await db.Database.MigrateAsync("20260922003301_PublicMarketCatalog");
            db.AddRange(new ApplicationUser(user, DateTimeOffset.UtcNow), new Workspace(workspace, "Before relationships", DateTimeOffset.UtcNow), new WorkspaceMembership(user, workspace), new LocalProfile(profile, user, workspace));
            await db.SaveChangesAsync();
            // Seed the old schema with old columns, not the new EF model.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditRecords (Id, ActorId, WorkspaceId, OccurredAt, Action, CorrelationId) VALUES ({audit}, {user}, {workspace}, {now}, {"Workspace.DisplayNameUpdated"}, {"before-relationships"})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CatalogMarkets (Exchange, NativeId, Environment, Title, TagsJson, Status, OutcomesJson, FirstRetrievedAt, RetrievedAt, LastSeenRunId, WarningsJson, IsIncomplete) VALUES ({"Kalshi"}, {"preserved"}, {"Production"}, {"Preserved title"}, {"[]"}, {"Open"}, {"[]"}, {now}, {now}, {Guid.NewGuid()}, {"[]"}, {false})");
            db.DiscoveryRuns.Add(new() { Id = Guid.NewGuid(), Exchange = "Kalshi", Scope = "open", State = "Partial", OwnerUserId = user, WorkspaceId = workspace, StartedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, false, default));
        await using (var db = Open())
        {
            await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(true, true, default);
            Assert.Equal(profile, (await db.LocalProfiles.SingleAsync()).Id); Assert.Equal(user, (await db.Users.SingleAsync()).Id);
            Assert.Equal("Before relationships", (await db.Workspaces.SingleAsync()).DisplayName);
            Assert.Equal("Preserved title", (await db.CatalogMarkets.SingleAsync()).Title); Assert.Single(await db.DiscoveryRuns.ToListAsync());
            Assert.Equal(audit, (await db.AuditRecords.SingleAsync()).Id); Assert.Empty(await db.MarketRelationships.ToListAsync());
        }
    }
    [Theory]
    [InlineData("retrieval", false)] [InlineData("rules", true)] [InlineData("outcomes", true)] [InlineData("policy", true)] [InlineData("removed", true)]
    public async Task Approval_persists_audit_and_current_provider_rechecks_source_and_policy(string change, bool stale)
    {
        await using var db = Open(); await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
        var profile = await db.LocalProfiles.SingleAsync(); var a = Market("Kalshi", "a"); var b = Market("Polymarket", "b"); db.AddRange(a, b); await db.SaveChangesAsync();
        var store = new RelationshipStore(db, TimeProvider.System);
        var pair = await store.GeneratePairAsync(profile.UserId, profile.DefaultWorkspaceId, CatalogSemantics.Describe(a), CatalogSemantics.Describe(b), default);
        var reverse = await store.GeneratePairAsync(profile.UserId, profile.DefaultWorkspaceId, CatalogSemantics.Describe(b), CatalogSemantics.Describe(a), default);
        Assert.Equal(pair.Id, reverse.Id); Assert.Single(await db.MarketRelationships.ToListAsync()); Assert.NotEmpty(pair.Evidence);
        await store.ReviewAsync(profile.UserId, profile.DefaultWorkspaceId, pair.Id, pair.SourceFingerprint, pair.TargetFingerprint, "Reviewed fixture settlement text", RelationshipType.EquivalentSameOutcome,
            [new("yes", "token-yes", RelationshipType.EquivalentSameOutcome)], false, default);
        db.ChangeTracker.Clear(); pair = (await store.ReadAsync(profile.UserId, profile.DefaultWorkspaceId, pair.Id, default))!;
        Assert.Equal(VerificationState.VerifiedManual, pair.State); Assert.Single(pair.Mappings);
        Assert.Empty(await store.ReadApprovedAsync(profile.UserId, profile.DefaultWorkspaceId, false, default));
        Assert.Single(await store.ReadApprovedAsync(profile.UserId, profile.DefaultWorkspaceId, true, default));
        var audit = await db.AuditRecords.SingleAsync(); Assert.Equal("RelationshipManualVerified", audit.Action); Assert.Contains(pair.Id.ToString(), audit.DetailsJson!); Assert.Equal(profile.UserId, audit.ActorId);
        a = await db.CatalogMarkets.SingleAsync(m => m.Exchange == "Kalshi");
        if (change == "retrieval") { a.RetrievedAt = a.RetrievedAt.AddHours(2); a.SourceUpdatedAt = DateTimeOffset.UtcNow; }
        if (change == "rules") a.Rules = "changed settlement rules";
        if (change == "outcomes") a.OutcomesJson = "[]";
        if (change == "policy") pair.PolicyVersion = 0;
        if (change == "removed") db.CatalogMarkets.Remove(a);
        await db.SaveChangesAsync();
        var approved = await store.ReadApprovedAsync(profile.UserId, profile.DefaultWorkspaceId, true, default);
        Assert.Equal(stale ? 0 : 1, approved.Count); Assert.Equal(stale ? VerificationState.Stale : VerificationState.VerifiedManual, pair.State);
        Assert.Single(await db.MarketRelationships.ToListAsync()); // Source disappearance never deletes the review.
    }
    [Fact]
    public async Task Rejection_is_audited_generation_preserves_it_and_obsolete_fingerprints_cannot_approve()
    {
        await using var db = Open(); await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
        var profile = await db.LocalProfiles.SingleAsync(); var a = Market("Kalshi", "a"); var b = Market("Polymarket", "b"); db.AddRange(a, b); await db.SaveChangesAsync();
        var store = new RelationshipStore(db, TimeProvider.System);
        var pair = await store.GeneratePairAsync(profile.UserId, profile.DefaultWorkspaceId, CatalogSemantics.Describe(a), CatalogSemantics.Describe(b), default);
        await store.ReviewAsync(profile.UserId, profile.DefaultWorkspaceId, pair.Id, pair.SourceFingerprint, pair.TargetFingerprint, "Different observation window", RelationshipType.Rejected, [], true, default);
        Assert.Equal("RelationshipRejected", (await db.AuditRecords.SingleAsync()).Action);
        await store.GeneratePairAsync(profile.UserId, profile.DefaultWorkspaceId, CatalogSemantics.Describe(a), CatalogSemantics.Describe(b), default);
        Assert.Equal(VerificationState.Rejected, pair.State);
        a.Rules = "new rules"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<RelationshipConflictException>(() => store.ReviewAsync(profile.UserId, profile.DefaultWorkspaceId, pair.Id, pair.SourceFingerprint, pair.TargetFingerprint, "Obsolete", RelationshipType.Related, [new("yes", "token-yes", RelationshipType.Related)], false, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RevalidateAsync(Guid.NewGuid(), profile.DefaultWorkspaceId, pair.Id, default));
    }
    [Fact]
    public async Task Manual_exclusive_nonexhaustive_set_is_persisted_separately_from_equivalence()
    {
        await using var db = Open(); await new DatabaseInitializer(db, TimeProvider.System).InitializeAsync(false, false, default);
        var p = await db.LocalProfiles.SingleAsync(); var a = Market("Kalshi", "a"); var b = Market("Polymarket", "b"); db.AddRange(a, b); await db.SaveChangesAsync();
        var store = new RelationshipStore(db, TimeProvider.System);
        var row = await store.GeneratePairAsync(p.UserId, p.DefaultWorkspaceId, CatalogSemantics.Describe(a), CatalogSemantics.Describe(b), default);
        await store.ReviewAsync(p.UserId, p.DefaultWorkspaceId, row.Id, row.SourceFingerprint, row.TargetFingerprint, "Fixture set leaves other outcomes possible", RelationshipType.MutuallyExclusive,
            [new("yes", "token-yes", RelationshipType.MutuallyExclusive)], false, default, TruthValue.True, TruthValue.False);
        db.ChangeTracker.Clear();
        var approved = Assert.Single(await store.ReadApprovedAsync(p.UserId, p.DefaultWorkspaceId, true, default));
        Assert.Equal(RelationshipType.MutuallyExclusive, approved.Type); Assert.Equal(TruthValue.True, approved.SelectedOutcomeSet.MutuallyExclusive!.Value);
        Assert.Equal(TruthValue.False, approved.SelectedOutcomeSet.CollectivelyExhaustive!.Value);
        Assert.Equal(FactSource.ManualVerification, approved.SelectedOutcomeSet.MutuallyExclusive.Source);
    }
    public void Dispose() => Directory.Delete(root, true);
}
