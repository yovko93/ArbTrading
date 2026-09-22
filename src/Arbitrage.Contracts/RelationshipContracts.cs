namespace Arbitrage.Contracts;

public sealed record RelationshipSummaryResponse(Guid Id, string SourceExchange, string SourceId, string? SourceTitle,
    string TargetExchange, string TargetId, string? TargetTitle, string Type, string State, DateTimeOffset? LastValidatedAt, bool IsStale);
public sealed record RelationshipPageResponse(RelationshipSummaryResponse[] Items, int Total, int Page, int PageSize);
public sealed record RelationshipOutcomeResponse(string NativeId, string Label);
public sealed record RelationshipMarketResponse(string Exchange, string NativeId, string? Title, string? Rules, string? Description,
    string? ResolutionSource, string? EventId, string? SeriesId, string? GroupId, string? Structure,
    DateTimeOffset? MarketOpen, DateTimeOffset? MarketClose, DateTimeOffset? ExpectedResolution, DateTimeOffset RetrievedAt,
    DateTimeOffset? SourceUpdatedAt, RelationshipOutcomeResponse[] Outcomes, string SemanticDetails);
public sealed record RelationshipEvidenceResponse(string Kind, string Dimension, string Detail, bool Blocking, bool Contradiction);
public sealed record RelationshipMappingResponse(string SourceOutcomeId, string TargetOutcomeId, string Type);
public sealed record RelationshipDetailResponse(RelationshipSummaryResponse Summary, RelationshipMarketResponse Source,
    RelationshipMarketResponse Target, string SourceFingerprint, string TargetFingerprint, int PolicyVersion,
    RelationshipEvidenceResponse[] Evidence, RelationshipMappingResponse[] Mappings, string[] Warnings,
    string? ReviewReason, Guid? ReviewerId, DateTimeOffset? ReviewedAt, bool IsStrategyEligible,
    string MutuallyExclusive = "Unknown", string CollectivelyExhaustive = "Unknown");
public sealed record GenerateRelationshipsRequest(string? Exchange = null, string? NativeId = null, string? EventId = null,
    bool CrossExchange = true, int MaximumSources = 500, int PerSource = 20, int MaximumComparisons = 2000, int RuntimeSeconds = 15);
public sealed record ReviewRelationshipRequest(string SourceFingerprint, string TargetFingerprint, string Reason,
    string Type, RelationshipMappingResponse[] Mappings, bool Confirmed,
    string MutuallyExclusive = "Unknown", string CollectivelyExhaustive = "Unknown");
public sealed record RelationshipJobResponse(Guid Id, string State, int Sources, int Comparisons, int Written,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Notice);
