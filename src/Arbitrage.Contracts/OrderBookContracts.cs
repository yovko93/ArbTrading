namespace Arbitrage.Contracts;

public sealed record BookInstrumentResponse(string Exchange, string NativeMarketId, string NativeInstrumentId, string Outcome);
public sealed record BookLevelResponse(decimal Price, decimal Quantity, string Origin);
public sealed record BookSnapshotResponse(Guid Id, BookInstrumentResponse Instrument, BookLevelResponse[] Bids,
    BookLevelResponse[] Asks, BookLevelResponse[] OppositeBids, DateTimeOffset RetrievedAtUtc, DateTimeOffset? SourceTimestamp,
    string? SourceMarketId, string? NativeHash, string Validity, string Completeness, string[] Warnings);
public sealed record BookFailureResponse(string Code, DateTimeOffset AtUtc, DateTimeOffset? RetryAt);
public sealed record OrderBookResponse(BookInstrumentResponse[] Instruments, string? SelectedInstrumentId,
    string State, string Freshness, bool IsActionable, string? Reason, decimal? AgeSeconds,
    int FreshnessSeconds, BookSnapshotResponse? Snapshot, BookFailureResponse? LastRefreshFailure, RealtimeBookResponse? Realtime = null);
public sealed record DepthPreviewRequest(string InstrumentId, string Action, decimal Quantity, bool DiagnosticOnly = false);
public sealed record GrossDepthResponse(decimal RequestedQuantity, decimal ExecutableQuantity, bool IsFullyExecutable,
    decimal GrossNotional, decimal? Vwap, decimal? BestPrice, decimal? WorstPrice, int LevelsConsumed,
    decimal UnfilledQuantity, Guid? SnapshotId, DateTimeOffset? SnapshotRetrievedAt, string SnapshotFreshness,
    bool IsActionable, string? Reason);
