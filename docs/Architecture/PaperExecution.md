# Phase 04A — explicit paper snapshot execution

Execution contains the pure planner, typed rejection policy and checked decimal accounting. It references Application and transitively Domain, with no HTTP, EF/SQLite, WPF, transport or credential dependency. Infrastructure owns persistence; Backend coordinates authenticated requests; Desktop references Contracts and obtains business data over HTTP.

This is **Snapshot Paper Fill / Immediate Taker Simulation**, not a prediction of actual fills. **Paper atomicity is NOT evidence that live cross-exchange execution can be atomic.** There is no latency, maker queue, market impact, partial basket, private account API, signing, real order, transfer, settlement, resolution or capital allocation. Exchange accounts and credentials are unnecessary. Existing optional Kalshi market-data credentials are never read by paper code.

## Accounts and journal

Startup creates no paper money. An owner explicitly confirms initial venue/currency amounts. Desktop prefills editable 10,000 Kalshi USD and 10,000 Polymarket USDC; these remain form values until confirmation. Up to four distinct buckets accept amounts from zero to 1,000,000,000. Currency labels are USD/USDC; funding never changes the execution currency established by fee metadata. Common-USD test fixtures do not override production Polymarket metadata. There is no FX, borrowing, margin or negative cash.

Reset requires the expected active generation ID and a reason. It atomically closes the old generation and creates the new generation and funding journal. Old balances, positions, fills and executions remain queryable. New execution cannot target a closed generation.

Persistent tables represent generations, venue balances, executions, legs, fills, ledger transactions, cash entries and positions. Journal transactions identify generation, actor, timestamp and execution (except initial funding). Reserve, fill-notional and modeled-fee entries explain every debit. Reservation and consumption occur in one transaction; successful fills leave reserved cash zero. Funding/reset and successful execution also write audit records. Monetary values are decimal; UTC DateTimeOffset values use the established SQLite UTC-ticks conversion.

Execution plans retain relationship ID/trust/revision/policy/fingerprints; canonical book versions/source/continuity/timestamps/skew; paired segments; and fee breakdowns/fingerprints/profile revision/effective rules. Separate leg/fill records retain native instruments/outcomes, exact quantities/prices/notionals, liquidity origin and native source identity.

Positions are keyed by generation + venue + native market + native instrument + outcome. Cost basis includes notional and modeled fees. Weighted average entry excludes fees: cumulative notional divided by quantity. Expected payout and expected profit **at resolution** are projections, never cash or realized profit. There is no settlement, closure or mark-to-market.

An explicit reconciliation diagnostic compares journal cash with balances/funding, executions/proofs with persisted legs/fills, and fill-derived quantities/costs/fees with positions. Failure marks Corrupt and blocks further execution. No silent repair occurs, including after a later good diagnostic. Healthy and NeedsReconciliation are also modeled states. Reconciliation is potentially expensive and is not performed on every request. Direct database edits are unsupported.

## Preview, proof and atomic commit

The input key must identify a retained explicit evaluation or current monitoring result. Clients do not supply authoritative prices, market IDs, fees, versions or profit. Deterministic relationships only; no manual-paper override exists. Both legs must BUY a proven complementary basket. Quantity must be positive, at most 1,000 and fully executable. Known modeled fees must leave positive profit and at least 0.001 fee-adjusted edge per share. Unresolved Kalshi public fee metadata still blocks execution; USD/USDC aggregation remains unsupported.

The existing gross paired walker and exact per-level fee evaluator re-evaluate requested quantity. The paper planner projects their segments into native levels and merges repeated segments at the same leg price; there is no independent second depth walk or VWAP fee shortcut. Shared native sources within a basket are rejected. Separate explicit paper requests can simulate an unchanged snapshot; cached market depth is not decremented and no market-impact claim is made.

Preview does not mutate persistent storage or fetch externally. At most 256 in-memory tickets retain server-owned proof for five seconds, bound to actor/workspace/generation. Cash requirements and remaining venue cash are shown. Insufficient-funds previews can retain proof for a typed confirmation rejection; Desktop disables confirmation for them.

Confirmation rechecks relationship semantics/revision/policy, fee fingerprints/profile revision/effective rule, book versions/freshness/continuity and full quantity. Changed inputs reject without retries or hidden re-pricing. A serializable SQLite writer transaction rechecks active generation, integrity and venue funds; cash, positions, fills, audit and idempotency data commit together. A final coherent book check holds the cache gate across the short synchronous COMMIT, closing the version-check/commit race. Database writer serialization, balance revision tokens and a unique workspace/request index protect concurrent requests. Cancellation before COMMIT rolls back all financial writes.

Each execution needs a nonempty RequestId. A fingerprint binds the entire confirmation body. The same workspace/RequestId/body returns the committed result even after restart or preview expiry; changed bodies reject. Rejected attempts create no financial records; bounded diagnostics count them. Desktop retains the RequestId after a lost reply and never automatically replays a mutation, including after 401. New previews after restart need fresh evaluation and book state.

## HTTP

All routes below are under `/api/v1/workspaces/{workspaceId}/paper`, require Bearer authentication and explicit membership. Mutations require Owner; DTOs accept no actor ID. Unknown request fields reject. Execution rejects NotPaperMode rather than downgrading an unavailable mode. Malformed requests are 400, missing credentials 401, membership failures 403 and unavailable storage 503. Financial rejections use typed `State=Rejected` responses.

| Route | Purpose |
| --- | --- |
| GET `account?generationId=` | Active or selected historical balances; recent 100 generation descriptors |
| POST `account/initialize` | confirmSimulation, null expectedGenerationId, reason, balances |
| POST `account/reset` | Same shape with required expectedGenerationId |
| POST `preview` | opportunityKey, quantity |
| POST `execute` | requestId, previewId, opportunityKey, quantity, confirmSimulation |
| GET `positions?generationId=` | Active/historical positions; bounded to 1,000 rows |
| GET `executions?page=&generationId=` | History, 50 rows per page |
| GET `executions/{id}` | Immutable committed detail |
| POST `reconcile?generationId=` | Explicit integrity diagnostic |
| GET `diagnostics` | Workspace-scoped bounded in-memory counters |

Small workspace StateInvalidated events carry PaperAccountChanged, PaperPositionsChanged and PaperExecutionChanged. They contain no ledger payload and are not order acknowledgments. HTTP remains authoritative; the active paper page refreshes every two seconds. Monitoring CSV is separate from the financial ledger.

## Desktop and limits

Trading and Portfolio share the paper page: explicit funding/reset, venue balances, quantity preview, modeled native fills, open positions and paged execution history with selected details. Current explicit and monitoring rows expose Paper preview only when displayed deterministic/actionable/fee guards pass; the server is authoritative. Alerts never execute. A separate warning dialog requires confirmation and shows debits, fees, projected payout/profit, relationship proof, fee profile revision and book version/source/continuity/age.

Quantity, opportunity, navigation and access changes invalidate previews. Late responses cannot restore them; authentication/authorization failures clear private state. The five-second preview may expire while reading the dialog; request a fresh preview after rejection. This policy favors freshness over hidden re-pricing. Historical projections remain snapshots after source changes. There is no paper ledger CSV export, automatic allocation or automatic execution.
