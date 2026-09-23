namespace Arbitrage.Contracts;

public sealed record PaperFundingRequest(string Exchange, string Currency, decimal Amount);
public sealed record InitializePaperRequest(bool ConfirmSimulation, Guid? ExpectedGenerationId, string Reason, PaperFundingRequest[] Balances);
public sealed record PaperPreviewRequest(string OpportunityKey, decimal Quantity);
public sealed record ConfirmPaperRequest(Guid RequestId, Guid PreviewId, string OpportunityKey, decimal Quantity, bool ConfirmSimulation);
public sealed record PaperBalanceResponse(string Exchange, string Currency, decimal InitialCash, decimal AvailableCash, decimal ReservedCash, decimal TotalCash, long Revision, DateTimeOffset UpdatedAt);
public sealed record PaperGenerationResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, string Reason, string Integrity);
public sealed record PaperAccountResponse(string State, PaperGenerationResponse? Generation, PaperBalanceResponse[] Balances, PaperGenerationResponse[] History);
public sealed record PaperPositionResponse(Guid Id, Guid GenerationId, string Exchange, string MarketId, string InstrumentId, string Outcome, string Currency,
    decimal Quantity, decimal CostBasis, decimal Fees, decimal AverageEntry, DateTimeOffset OpenedAt, DateTimeOffset UpdatedAt);
public sealed record PaperFillResponse(Guid Id, Guid LegId, string Exchange, string MarketId, string InstrumentId, string Outcome, string Action,
    decimal Quantity, decimal Price, decimal Notional, decimal Fee, string Currency, string LiquidityOrigin, OpportunityLiquidityResponse NativeLiquidityIdentity,
    long BookVersion, DateTimeOffset FilledAt);
public sealed record PaperDebitResponse(string Exchange, string Currency, decimal Notional, decimal Fees, decimal Total, decimal AvailableCash, decimal RemainingCash);
public sealed record PaperPreviewResponse(Guid PreviewId, Guid? GenerationId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool WouldExecute, string Rejection,
    string OpportunityKey, decimal RequestedQuantity, decimal ExecutableQuantity, PaperFillResponse[] Fills, PaperDebitResponse[] Debits,
    decimal? GrossCost, decimal? TotalFees, decimal? FeeAdjustedCost, decimal? ExpectedPayoutAtResolution, decimal? ExpectedProfitAtResolution,
    OpportunityResponse? Proof, string[] Warnings);
public sealed record PaperExecutionResponse(Guid Id, Guid RequestId, Guid GenerationId, Guid ActorId, DateTimeOffset CreatedAt, string State,
    string OpportunityKey, decimal Quantity, decimal Cost, decimal ExpectedPayoutAtResolution, decimal ExpectedProfitAtResolution,
    PaperFillResponse[] Fills, OpportunityResponse Proof);
public sealed record PaperCommitResponse(string State, string Rejection, bool Duplicate, PaperExecutionResponse? Execution);
public sealed record PaperIntegrityResponse(Guid GenerationId, string Integrity);
public sealed record PaperDiagnosticsResponse(long Attempts, long Committed, long Rejected, long InsufficientFunds, long StaleInputs, long Duplicates, long IntegrityFailures);
