# Execution

Active Phase 04A pure paper-execution boundary. `PaperPlanner` projects existing paired-depth and fee evaluations into native-level snapshot fills. `PaperAccounting` provides checked decimal cash and position arithmetic. Infrastructure persists the ledger; Backend coordinates explicit authenticated preview/confirmation. Execution has no transports, credentials, EF/SQLite or WPF dependencies.

Phase 04B adds pure `PaperSettlement` rules for explicit ManualScenario binary 1/0 payouts, position P&L and partial/full execution economics. Captured deterministic complementary execution proof rejects contradictory payouts. Infrastructure coordinates atomic settlement and reconciliation; Backend exposes owner-only preview/confirm; no current market-data or fee source is required.

Only paper simulation exists. No automatic exchange-result ingestion, real orders, signing, private balances/positions, transfers, redemption, mark-to-market, market impact or capital allocation are implemented. Unsupported void/scalar/negative-risk/complex payouts fail closed. Paper atomicity does not establish live cross-exchange atomicity. See [architecture](../../docs/Architecture/PaperExecution.md).
