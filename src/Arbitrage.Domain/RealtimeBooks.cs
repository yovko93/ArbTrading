namespace Arbitrage.Domain;

public enum BookSourceMode { RestSnapshot, Realtime }
public enum BookContinuity { NotApplicable, AwaitingAnchor, Continuous, BestEffort, GapDetected, Resynchronizing, Disconnected }
public enum RealtimeSubscriptionState
{
    NotSubscribed, Connecting, AwaitingSnapshot, Streaming, Resynchronizing, Stale,
    AuthenticationRequired, AuthenticationFailed, NetworkRestricted, Unsupported, Stopped, Faulted
}

public sealed record RealtimeBookMetadata(long Generation, RealtimeSubscriptionState State,
    BookContinuity Continuity, bool Connected = false, long? SubscriptionId = null,
    string? SessionId = null, long? Sequence = null, DateTimeOffset? AnchorAt = null,
    DateTimeOffset? LastReceivedAt = null, DateTimeOffset? LastChangedAt = null,
    DateTimeOffset? LastControlAt = null, string? ResyncReason = null, decimal? TickSize = null, long DeltaCount = 0);
