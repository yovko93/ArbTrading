namespace Arbitrage.Application;

public sealed record RelationshipMetadata(string? Title, string? Description, string? Rules, string? ResolutionSource,
    string PublicEndpoint, string SemanticMetadataJson, DateTimeOffset RetrievedAt, MarketOutcome[]? Outcomes = null,
    DateTimeOffset? MarketOpen = null, DateTimeOffset? MarketClose = null, DateTimeOffset? ExpectedResolution = null,
    string? MarketStructure = null);
public interface IRelationshipMetadataSource
{
    Task<RelationshipMetadata> ReadAsync(string exchange, string nativeId, string? eventId, CancellationToken ct);
}
