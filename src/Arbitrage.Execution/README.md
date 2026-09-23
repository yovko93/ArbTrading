# Execution

Active Phase 04A pure paper-execution boundary. `PaperPlanner` projects existing paired-depth and fee evaluations into native-level snapshot fills. `PaperAccounting` provides checked decimal cash and position arithmetic. Infrastructure persists the ledger; Backend coordinates explicit authenticated preview/confirmation. Execution has no transports, credentials, EF/SQLite or WPF dependencies.

Only snapshot paper simulation exists. No real orders, signing, private balances/positions, transfers, settlement, market impact or capital allocation are implemented. Paper atomicity does not establish live cross-exchange atomicity. See [architecture](../../docs/Architecture/PaperExecution.md).
