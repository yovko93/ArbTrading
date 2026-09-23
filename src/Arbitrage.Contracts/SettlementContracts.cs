namespace Arbitrage.Contracts;

public sealed record PaperResolutionSelection(Guid GenerationId, string Exchange, string MarketId, string WinningInstrumentId);
public sealed record ConfirmResolutionRequest(Guid RequestId, Guid PreviewId, PaperResolutionSelection Selection, bool ConfirmSimulation);
public sealed record PaperPayoutResponse(string InstrumentId, string Outcome, decimal PayoutPerShare);
public sealed record PaperResolutionCandidateResponse(Guid GenerationId, string Exchange, string MarketId, string? Title, bool Supported,
    PaperPayoutResponse[] Outcomes, PaperPositionResponse[] Positions, Guid[] ExecutionIds, Guid[] RelationshipIds, string Source = "ManualScenario");
public sealed record PaperPositionSettlementResponse(Guid PositionId, string InstrumentId, string Outcome, string Currency, decimal Quantity, decimal CostBasis, decimal Payout, decimal RealizedPnl);
public sealed record PaperSettlementCreditResponse(string Exchange, string Currency, decimal Payout, decimal AvailableBefore, decimal AvailableAfter);
public sealed record PaperExecutionSettlementEffectResponse(Guid ExecutionId, PaperExecutionSettlementResponse Economics);
public sealed record PaperResolutionPreviewResponse(Guid PreviewId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, PaperResolutionSelection Selection,
    bool WouldSettle, string RejectionReason, PaperPayoutResponse[] PayoutVector, PaperPositionSettlementResponse[] AffectedPositions,
    PaperExecutionSettlementEffectResponse[] AffectedExecutions, PaperSettlementCreditResponse[] CashCredits, string[] Warnings, string Source = "ManualScenario");
public sealed record PaperResolutionResponse(Guid Id, Guid GenerationId, Guid RequestId, Guid ActorId, string Exchange, string MarketId,
    string Source, string Status, DateTimeOffset ResolvedAt, DateTimeOffset RecordedAt, PaperPayoutResponse[] PayoutVector,
    PaperPositionResponse[] Positions, Guid[] ExecutionIds, Guid? LedgerTransactionId);
public sealed record PaperResolutionCommitResponse(string Rejection, bool Duplicate, PaperResolutionResponse? Resolution);
public sealed record PaperPerformanceBucketResponse(string Exchange, string Currency, decimal StartingCash, decimal CurrentCash, decimal OpenCostBasis,
    decimal SettledCostBasis, decimal SettlementPayout, decimal CumulativeRealizedPnl, int OpenPositionCount, int SettledPositionCount,
    int CommittedExecutionCount, int PartiallySettledExecutionCount, int SettledExecutionCount);
public sealed record PaperPerformanceResponse(Guid GenerationId, string Lifecycle, PaperPerformanceBucketResponse[] Buckets);
public sealed record PaperCurvePointResponse(DateTimeOffset Timestamp, string Exchange, string Currency, decimal CashAvailable, decimal CumulativeRealizedPnl,
    decimal RealizedPnlDelta, decimal RealizedPerformance, string Reason, Guid? ExecutionId, Guid? ResolutionId);
public sealed record PaperCurveResponse(Guid GenerationId, string Exchange, string Currency, int Page, bool HasMore, PaperCurvePointResponse[] Points);
public sealed record PaperSettlementDiagnosticsResponse(long Previewed, long Committed, long Conflicts, long PositionsSettled, long ExecutionsFullySettled,
    long PartialSettlements, long IntegrityFailures, long Duplicates);
