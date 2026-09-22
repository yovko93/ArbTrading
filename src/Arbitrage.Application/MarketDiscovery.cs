namespace Arbitrage.Application;

// Public metadata only. Unknown fields remain null; no price or execution capability is implied.
public sealed record MarketOutcome(string Label, string? NativeTokenId);

public sealed record DiscoveredMarket(
    string Exchange, string Environment, string NativeId, string? EventId, string? SeriesId,
    string? GroupId, string? Classification, string? Title, string? Subtitle,
    string? Category, string[] Tags, string? NativeStatus, string Status,
    MarketOutcome[] Outcomes, DateTimeOffset? CreatedAt, DateTimeOffset? OpenAt,
    DateTimeOffset? CloseAt, DateTimeOffset? ExpectedResolutionAt, DateTimeOffset? ResolvedAt,
    DateTimeOffset? SourceUpdatedAt, string? Description, string? Rules,
    string? SourceReference, DateTimeOffset RetrievedAt, string[] Warnings)
{
    public bool IsIncomplete => Warnings.Length > 0;
}

public sealed record MarketDiscoveryPage(DiscoveredMarket[] Markets, string? NextCursor,
    int MalformedRecords, string[] Warnings);

public interface IMarketDiscoverySource
{
    string Exchange { get; }
    IReadOnlyList<string> Scopes { get; }
    Task<MarketDiscoveryPage> ReadPageAsync(string scope, string? cursor, int pageSize,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

public sealed class MarketDiscoveryException(string code, string message, DateTimeOffset? retryAt = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
