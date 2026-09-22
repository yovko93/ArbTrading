using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class MarketRelationshipEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string SourceExchange { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string TargetExchange { get; set; } = "";
    public string TargetId { get; set; } = "";
    public RelationshipType Type { get; set; }
    public VerificationState State { get; set; }
    public TruthValue MutuallyExclusive { get; set; }
    public TruthValue CollectivelyExhaustive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public int PolicyVersion { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string TargetFingerprint { get; set; } = "";
    public string SourceSnapshot { get; set; } = "";
    public string TargetSnapshot { get; set; } = "";
    public string WarningsJson { get; set; } = "[]";
    public string? ReviewReason { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public List<RelationshipEvidenceEntry> Evidence { get; set; } = [];
    public List<RelationshipOutcomeMappingEntry> Mappings { get; set; } = [];
}
public sealed class RelationshipEvidenceEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RelationshipId { get; set; }
    public EvidenceKind Kind { get; set; }
    public string Dimension { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Blocking { get; set; }
    public bool Contradiction { get; set; }
}
public sealed class RelationshipOutcomeMappingEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RelationshipId { get; set; }
    public string SourceOutcomeId { get; set; } = "";
    public string TargetOutcomeId { get; set; } = "";
    public RelationshipType Type { get; set; }
}
public sealed class RelationshipJobEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid ActorId { get; set; }
    public string State { get; set; } = "Running";
    public string RequestJson { get; set; } = "";
    public int Sources { get; set; }
    public int Comparisons { get; set; }
    public int Written { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Notice { get; set; }
}

public static class CatalogSemantics
{
    public static CanonicalMarketDescriptor Describe(MarketCatalogEntry m)
    {
        var basic = BaseDescriptor(m);
        if (m.RelationshipMetadataJson is null || m.RelationshipMetadataBaseFingerprint != RelationshipPolicy.Fingerprint(basic)) return basic;
        var extra = JsonSerializer.Deserialize<RelationshipMetadata>(m.RelationshipMetadataJson)!;
        return basic with { Title = extra.Title ?? basic.Title, Description = extra.Description ?? basic.Description,
            RulesText = extra.Rules, ResolutionSource = extra.ResolutionSource, PublicMetadataReference = extra.PublicEndpoint,
            PublicSemanticMetadataJson = extra.SemanticMetadataJson, Semantics = RelationshipHints.FromTitle(extra.Title ?? basic.Title),
            Outcomes = extra.Outcomes is null ? basic.Outcomes : extra.Outcomes.Select(o => new SemanticOutcome(m.Exchange == "Kalshi" ? o.Label.ToLowerInvariant() : o.NativeTokenId ?? "", o.Label)).ToArray(),
            MarketOpen = extra.MarketOpen ?? basic.MarketOpen, MarketClose = extra.MarketClose ?? basic.MarketClose,
            ExpectedResolution = extra.ExpectedResolution ?? basic.ExpectedResolution, MarketStructure = extra.MarketStructure ?? basic.MarketStructure };
    }
    public static CanonicalMarketDescriptor BaseDescriptor(MarketCatalogEntry m) => new()
    {
        Identity = new(m.Exchange, m.NativeId), NativeEventId = m.EventId, NativeSeriesId = m.SeriesId, NativeGroupId = m.GroupId,
        Title = m.Title, Subtitle = m.Subtitle, Description = m.Description, RulesText = m.Rules, PublicMetadataReference = m.SourceReference,
        Category = m.Category, Tags = JsonSerializer.Deserialize<string[]>(m.TagsJson) ?? [], NativeStatus = m.NativeStatus,
        NormalizedStatus = m.Status, MarketStructure = m.Classification,
        Outcomes = (JsonSerializer.Deserialize<MarketOutcome[]>(m.OutcomesJson) ?? []).Select(o => new SemanticOutcome(
            m.Exchange == "Kalshi" ? o.Label.ToLowerInvariant() : o.NativeTokenId ?? "", o.Label)).ToArray(),
        MarketOpen = m.OpenAt, MarketClose = m.CloseAt, ExpectedResolution = m.ExpectedResolutionAt, ResolvedAt = m.ResolvedAt,
        RetrievedAt = m.RetrievedAt, SourceUpdatedAt = m.SourceUpdatedAt, Semantics = RelationshipHints.FromTitle(m.Title)
        // No arbitrary English parser is an authority. Unknown semantics remain null, including event times.
    };
}

public sealed class RelationshipStore(TradingDbContext db, TimeProvider clock) : IRelationshipProvider
{
    public async Task RequireMemberAsync(Guid actor, Guid workspace, bool owner, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.UserId == actor && m.WorkspaceId == workspace && (!owner || m.Role == WorkspaceRole.Owner), ct))
            throw new UnauthorizedAccessException();
    }
    public IQueryable<MarketRelationshipEntry> Query(Guid workspace) => db.MarketRelationships.Where(r => r.WorkspaceId == workspace);
    public async Task RefreshWorkspaceStalenessAsync(Guid workspace, CancellationToken ct)
    {
        // Batch source reads, never one query per pair across a large review queue.
        var ids = await Query(workspace).Select(r => r.Id).ToArrayAsync(ct);
        foreach (var batch in ids.Chunk(100))
        {
            var rows = await Query(workspace).Where(r => batch.Contains(r.Id)).ToArrayAsync(ct);
            var nativeIds = rows.SelectMany(r => new[] { r.SourceId, r.TargetId }).Distinct().ToArray();
            var markets = await db.CatalogMarkets.AsNoTracking().Where(m => nativeIds.Contains(m.NativeId)).ToArrayAsync(ct);
            var fingerprints = markets.ToDictionary(m => new MarketIdentity(m.Exchange, m.NativeId), m => RelationshipPolicy.Fingerprint(CatalogSemantics.Describe(m)));
            foreach (var row in rows)
                if (row.State != VerificationState.Stale && (row.PolicyVersion != RelationshipPolicy.Version ||
                    !fingerprints.TryGetValue(new(row.SourceExchange, row.SourceId), out var a) || a != row.SourceFingerprint ||
                    !fingerprints.TryGetValue(new(row.TargetExchange, row.TargetId), out var b) || b != row.TargetFingerprint))
                { row.State = VerificationState.Stale; row.UpdatedAt = clock.GetUtcNow(); }
            await db.SaveChangesAsync(ct);
        }
    }
    public async Task<MarketRelationshipEntry?> ReadAsync(Guid actor, Guid workspace, Guid id, CancellationToken ct)
    {
        await RequireMemberAsync(actor, workspace, false, ct);
        var row = await Query(workspace).Include(r => r.Evidence).Include(r => r.Mappings).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is not null) await RefreshStalenessAsync(row, ct);
        return row;
    }
    public async Task RefreshStalenessAsync(MarketRelationshipEntry row, CancellationToken ct)
    {
        var sources = await CurrentAsync(row, ct);
        if (row.State != VerificationState.Stale && (sources is null || row.PolicyVersion != RelationshipPolicy.Version ||
            RelationshipPolicy.Fingerprint(sources.Value.A) != row.SourceFingerprint || RelationshipPolicy.Fingerprint(sources.Value.B) != row.TargetFingerprint))
        {
            row.State = VerificationState.Stale; row.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct);
        }
    }
    public async Task<(CanonicalMarketDescriptor A, CanonicalMarketDescriptor B)?> CurrentAsync(MarketRelationshipEntry row, CancellationToken ct)
    {
        var a = await db.CatalogMarkets.AsNoTracking().SingleOrDefaultAsync(m => m.Exchange == row.SourceExchange && m.NativeId == row.SourceId, ct);
        var b = await db.CatalogMarkets.AsNoTracking().SingleOrDefaultAsync(m => m.Exchange == row.TargetExchange && m.NativeId == row.TargetId, ct);
        return a is null || b is null ? null : (CatalogSemantics.Describe(a), CatalogSemantics.Describe(b));
    }
    public async Task<MarketRelationshipEntry> GeneratePairAsync(Guid actor, Guid workspace, CanonicalMarketDescriptor a, CanonicalMarketDescriptor b, CancellationToken ct)
    {
        if (a.Identity == b.Identity) throw new ArgumentException("A pair must contain different markets.");
        if (a.Identity.CompareTo(b.Identity) > 0) (a, b) = (b, a);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireMemberAsync(actor, workspace, true, ct);
        var row = await Query(workspace).Include(r => r.Evidence).Include(r => r.Mappings).SingleOrDefaultAsync(r =>
            r.SourceExchange == a.Identity.Exchange && r.SourceId == a.Identity.NativeId && r.TargetExchange == b.Identity.Exchange && r.TargetId == b.Identity.NativeId, ct);
        // Generation never overwrites a human decision or silently revalidates it.
        if (row is not null) { await RefreshStalenessAsync(row, ct); await tx.CommitAsync(ct); return row; }
        row = new() { WorkspaceId = workspace, SourceExchange = a.Identity.Exchange, SourceId = a.Identity.NativeId,
            TargetExchange = b.Identity.Exchange, TargetId = b.Identity.NativeId, CreatedAt = clock.GetUtcNow() };
        ApplyValidation(row, a, b); db.MarketRelationships.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row;
    }
    public async Task<MarketRelationshipEntry?> RevalidateAsync(Guid actor, Guid workspace, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireMemberAsync(actor, workspace, true, ct);
        var row = await ReadAsync(actor, workspace, id, ct);
        if (row is null) return null;
        var current = await CurrentAsync(row, ct) ?? throw new ArgumentException("Source market unavailable.");
        ApplyValidation(row, current.A, current.B);
        Audit(actor, workspace, "RelationshipRevalidated", row);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row;
    }
    public async Task<MarketRelationshipEntry?> ReviewAsync(Guid actor, Guid workspace, Guid id, string fingerprintA, string fingerprintB,
        string reason, RelationshipType type, OutcomeMapping[] mappings, bool reject, CancellationToken ct,
        TruthValue mutuallyExclusive = TruthValue.Unknown, TruthValue collectivelyExhaustive = TruthValue.Unknown)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 || !Enum.IsDefined(type)) throw new ArgumentException("Review requires a bounded reason and typed relationship.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireMemberAsync(actor, workspace, true, ct);
        var row = await ReadAsync(actor, workspace, id, ct);
        if (row is null) return null;
        var current = await CurrentAsync(row, ct) ?? throw new ArgumentException("Source market unavailable.");
        if (RelationshipPolicy.Fingerprint(current.A) != fingerprintA || RelationshipPolicy.Fingerprint(current.B) != fingerprintB)
            throw new RelationshipConflictException();
        if (!reject)
        {
            if (!Enum.IsDefined(mutuallyExclusive) || !Enum.IsDefined(collectivelyExhaustive) ||
                type == RelationshipType.MutuallyExclusive && (mutuallyExclusive != TruthValue.True || collectivelyExhaustive == TruthValue.True) ||
                type == RelationshipType.Exhaustive && (collectivelyExhaustive != TruthValue.True || mutuallyExclusive == TruthValue.True) ||
                type == RelationshipType.MutuallyExclusiveAndExhaustive && (mutuallyExclusive != TruthValue.True || collectivelyExhaustive != TruthValue.True) ||
                type == RelationshipType.Overlapping && mutuallyExclusive != TruthValue.False)
                throw new ArgumentException("Set relationship requires explicit, consistent independent facts.");
            if (type is RelationshipType.Unknown or RelationshipType.Candidate or RelationshipType.Rejected || mappings.Length is < 1 or > 100 ||
                current.A.Outcomes.GroupBy(o => o.NativeId).Any(g => g.Count() > 1) || current.B.Outcomes.GroupBy(o => o.NativeId).Any(g => g.Count() > 1) ||
                mappings.Any(m => !Enum.IsDefined(m.Type) || m.Type is RelationshipType.Unknown or RelationshipType.Candidate or RelationshipType.Rejected ||
                    !current.A.Outcomes.Any(o => o.NativeId == m.SourceOutcomeId && !string.IsNullOrWhiteSpace(o.NativeId)) || !current.B.Outcomes.Any(o => o.NativeId == m.TargetOutcomeId && !string.IsNullOrWhiteSpace(o.NativeId))) ||
                mappings.Select(m => (m.SourceOutcomeId, m.TargetOutcomeId)).Distinct().Count() != mappings.Length)
                throw new ArgumentException("An explicit valid outcome mapping is required.");
        }
        ApplyValidation(row, current.A, current.B); // Keep negative evidence visible even when a human explicitly overrides it.
        row.Type = reject ? RelationshipType.Rejected : type;
        row.State = reject ? VerificationState.Rejected : VerificationState.VerifiedManual;
        row.MutuallyExclusive = reject ? TruthValue.Unknown : mutuallyExclusive;
        row.CollectivelyExhaustive = reject ? TruthValue.Unknown : collectivelyExhaustive;
        row.ReviewReason = reason.Trim(); row.ReviewerId = actor; row.ReviewedAt = clock.GetUtcNow();
        db.RelationshipOutcomeMappings.RemoveRange(row.Mappings); row.Mappings.Clear();
        if (!reject) row.Mappings.AddRange(mappings.Select(m => new RelationshipOutcomeMappingEntry { SourceOutcomeId = m.SourceOutcomeId, TargetOutcomeId = m.TargetOutcomeId, Type = m.Type }));
        db.RelationshipOutcomeMappings.AddRange(row.Mappings);
        Audit(actor, workspace, reject ? "RelationshipRejected" : "RelationshipManualVerified", row);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row;
    }
    private void ApplyValidation(MarketRelationshipEntry row, CanonicalMarketDescriptor a, CanonicalMarketDescriptor b)
    {
        var result = new RelationshipValidator().Validate(a, b);
        row.Type = result.Type; row.State = result.State; row.PolicyVersion = RelationshipPolicy.Version;
        row.MutuallyExclusive = row.CollectivelyExhaustive = TruthValue.Unknown;
        row.SourceFingerprint = RelationshipPolicy.Fingerprint(a); row.TargetFingerprint = RelationshipPolicy.Fingerprint(b);
        row.SourceSnapshot = JsonSerializer.Serialize(a); row.TargetSnapshot = JsonSerializer.Serialize(b);
        row.LastValidatedAt = row.UpdatedAt = clock.GetUtcNow(); row.ReviewReason = null; row.ReviewerId = null; row.ReviewedAt = null;
        row.WarningsJson = JsonSerializer.Serialize(result.Warnings);
        db.RelationshipEvidence.RemoveRange(row.Evidence); row.Evidence.Clear();
        row.Evidence.AddRange(result.Evidence.Select(e => new RelationshipEvidenceEntry { Kind = e.Kind, Dimension = e.Dimension, Detail = e.Detail, Blocking = e.Blocking, Contradiction = e.Contradiction }));
        db.RelationshipEvidence.AddRange(row.Evidence);
        db.RelationshipOutcomeMappings.RemoveRange(row.Mappings); row.Mappings.Clear();
        row.Mappings.AddRange(result.Mappings.Select(m => new RelationshipOutcomeMappingEntry { SourceOutcomeId = m.SourceOutcomeId, TargetOutcomeId = m.TargetOutcomeId, Type = m.Type }));
        db.RelationshipOutcomeMappings.AddRange(row.Mappings);
    }
    private void Audit(Guid actor, Guid workspace, string action, MarketRelationshipEntry row) => db.AuditRecords.Add(new(actor, workspace, clock.GetUtcNow(), row.Id.ToString(), action,
        JsonSerializer.Serialize(new { row.Id, row.SourceExchange, row.SourceId, row.TargetExchange, row.TargetId, row.PolicyVersion, row.SourceFingerprint, row.TargetFingerprint,
            row.ReviewReason, row.Type, row.State, row.MutuallyExclusive, row.CollectivelyExhaustive })));
    public async Task<IReadOnlyList<ApprovedRelationship>> ReadApprovedAsync(Guid actorId, Guid workspaceId, bool includeManual, CancellationToken ct)
        => (await ReadEvaluationPageAsync(actorId, workspaceId, includeManual, null, null, 0, int.MaxValue, ct)).Items;
    public async Task<ApprovedRelationshipPage> ReadEvaluationPageAsync(Guid actorId, Guid workspaceId, bool includeManual,
        Guid? relationshipId, string? exchange, int skip, int take, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireMemberAsync(actorId, workspaceId, false, ct);
        var query = Query(workspaceId).AsNoTracking().Include(r => r.Mappings).Where(r => r.State == VerificationState.VerifiedDeterministic || includeManual && r.State == VerificationState.VerifiedManual);
        if (relationshipId is { } id) query = query.Where(r => r.Id == id);
        if (exchange is not null) query = query.Where(r => r.SourceExchange == exchange || r.TargetExchange == exchange);
        var rows = await query.OrderBy(r => r.Id).Skip(skip).Take(take).ToArrayAsync(ct);
        var hasMore = take != int.MaxValue && await query.OrderBy(r => r.Id).Skip(skip + take).AnyAsync(ct);
        var result = new List<ApprovedRelationship>();
        foreach (var row in rows)
        {
            // Detached reads prevent a scoped context from returning an old tracked review decision.
            var current = await CurrentAsync(row, ct);
            if (current is null || row.PolicyVersion != RelationshipPolicy.Version ||
                RelationshipPolicy.Fingerprint(current.Value.A) != row.SourceFingerprint || RelationshipPolicy.Fingerprint(current.Value.B) != row.TargetFingerprint)
            {
                await Query(workspaceId).Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.State, VerificationState.Stale), ct);
                // Keep any caller's tracked view consistent without using it as the read authority.
                var tracked = db.ChangeTracker.Entries<MarketRelationshipEntry>().FirstOrDefault(e => e.Entity.Id == row.Id);
                if (tracked is not null) { var state = tracked.Property(r => r.State); state.CurrentValue = state.OriginalValue = VerificationState.Stale; state.IsModified = false; }
                continue;
            }
            if (!RelationshipPolicy.IsStrategyEligible(row.Type, row.State)) continue;
            var a = JsonSerializer.Deserialize<CanonicalMarketDescriptor>(row.SourceSnapshot)!;
            var b = JsonSerializer.Deserialize<CanonicalMarketDescriptor>(row.TargetSnapshot)!;
            result.Add(new(row.Id, row.Type, row.State, row.Mappings.Select(m => new OutcomeMapping(m.SourceOutcomeId, m.TargetOutcomeId, m.Type)).ToArray(),
                a.Identity, b.Identity, a.Semantics.OutcomeSet, b.Semantics.OutcomeSet,
                new(new(row.MutuallyExclusive, FactSource.ManualVerification, row.Id.ToString()), new(row.CollectivelyExhaustive, FactSource.ManualVerification, row.Id.ToString())))
            {
                PolicyVersion = row.PolicyVersion, SourceFingerprint = row.SourceFingerprint, TargetFingerprint = row.TargetFingerprint,
                SourceDescriptor = a, TargetDescriptor = b,
                Revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { row.State, row.Type, row.PolicyVersion, row.SourceFingerprint, row.TargetFingerprint, row.UpdatedAt, row.MutuallyExclusive, row.CollectivelyExhaustive,
                    Mappings = row.Mappings.OrderBy(m => m.SourceOutcomeId, StringComparer.Ordinal).ThenBy(m => m.TargetOutcomeId, StringComparer.Ordinal).ThenBy(m => m.Type).Select(m => new { m.SourceOutcomeId, m.TargetOutcomeId, m.Type }) })))
            });
        }
        await transaction.CommitAsync(ct); return new(result, rows.Length, hasMore);
    }
}
public sealed class RelationshipConflictException : Exception;
