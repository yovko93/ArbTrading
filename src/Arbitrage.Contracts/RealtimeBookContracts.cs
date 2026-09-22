namespace Arbitrage.Contracts;

public sealed record RealtimeBookResponse(string SourceMode, string State, string Continuity, bool Connected,
    long Generation, long Version, long? SubscriptionId, string? SessionId, long? Sequence, DateTimeOffset? AnchorAt,
    DateTimeOffset? LastReceivedAt, DateTimeOffset? LastChangedAt, DateTimeOffset? LastControlAt,
    string? ResyncReason, decimal? TickSize, long DeltaCount = 0);
public sealed record OrderBookInvalidation(Guid BackendInstanceId, Guid WorkspaceId, BookInstrumentResponse Instrument,
    long Version, long Generation, string SourceMode, string State);
public sealed record CredentialStatusResponse(bool Configured, string? MaskedKeyId, string? PublicKeyFingerprint,
    DateTimeOffset? ConfiguredAt, DateTimeOffset? UpdatedAt, string StoreCapability, string LastAuthenticationResult, Guid Version);
public sealed record ImportCredentialRequest(string KeyId, string FilePath, bool ConfirmReplacement, Guid ExpectedVersion);
public sealed record RemoveCredentialRequest(bool Confirmed, Guid ExpectedVersion);
