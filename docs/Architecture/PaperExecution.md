# Paper execution and Manual Scenario Resolution

Execution contains the pure planner, typed rejection policy and checked decimal accounting. It references Application and transitively Domain, with no HTTP, EF/SQLite, WPF, transport or credential dependency. Infrastructure owns persistence; Backend coordinates authenticated requests; Desktop references Contracts and obtains business data over HTTP.

This is **Snapshot Paper Fill / Immediate Taker Simulation**, not a prediction of actual fills. **Paper atomicity is NOT evidence that live cross-exchange execution can be atomic.** There is no latency, maker queue, market impact, partial basket, private account API, signing, real order, transfer or capital allocation. Phase 04B adds explicit simulated resolution only. Exchange accounts and credentials are unnecessary. Existing optional Kalshi market-data credentials are never read by paper code.

## Accounts and journal

Startup creates no paper money. An owner explicitly confirms initial venue/currency amounts. Desktop prefills editable 10,000 Kalshi USD and 10,000 Polymarket USDC; these remain form values until confirmation. Up to four distinct buckets accept amounts from zero to 1,000,000,000. Currency labels are USD/USDC; funding never changes the execution currency established by fee metadata. Common-USD test fixtures do not override production Polymarket metadata. There is no FX, borrowing, margin or negative cash.

Reset requires the expected active generation ID and a reason. It atomically closes the old generation and creates the new generation and funding journal. Old balances, positions, fills and executions remain queryable. New execution cannot target a closed generation.

Persistent tables represent generations, venue balances, executions, legs, fills, ledger transactions, cash entries and positions. Journal transactions identify generation, actor, timestamp and execution (except initial funding). Reserve, fill-notional and modeled-fee entries explain every debit. Reservation and consumption occur in one transaction; successful fills leave reserved cash zero. Funding/reset and successful execution also write audit records. Monetary values are decimal; UTC DateTimeOffset values use the established SQLite UTC-ticks conversion.

Execution plans retain relationship ID/trust/revision/policy/fingerprints; canonical book versions/source/continuity/timestamps/skew; paired segments; and fee breakdowns/fingerprints/profile revision/effective rules. Separate leg/fill records retain native instruments/outcomes, exact quantities/prices/notionals, liquidity origin and native source identity.

Positions are keyed by generation + venue + native market + native instrument + outcome. Cost basis includes notional and modeled fees. Weighted average entry excludes fees: cumulative notional divided by quantity. Expected payout and expected profit **at resolution** are projections. Only confirmed Manual Scenario Resolution creates settlement cash and realized P&L. There is no mark-to-market.

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
| GET `positions?generationId=&status=&page=` | Open (default), Settled or All; 100 rows/page |
| GET `executions?page=&generationId=` | History, 50 rows per page |
| GET `executions/{id}` | Immutable committed detail |
| POST `reconcile?generationId=` | Explicit integrity diagnostic |
| GET `diagnostics` | Workspace-scoped bounded in-memory counters |

Small workspace StateInvalidated events carry PaperAccountChanged, PaperPositionsChanged, PaperExecutionChanged, PaperResolutionChanged and PaperPerformanceChanged. They contain no ledger payload and are not order acknowledgments. HTTP remains authoritative; the active paper page refreshes every two seconds. Monitoring CSV is separate from the financial ledger.

## Desktop and limits

Trading and Portfolio share the paper page: explicit funding/reset, venue balances, quantity preview, modeled native fills, open positions and paged execution history with selected details. Current explicit and monitoring rows expose Paper preview only when displayed deterministic/actionable/fee guards pass; the server is authoritative. Alerts never execute. A separate warning dialog requires confirmation and shows debits, fees, projected payout/profit, relationship proof, fee profile revision and book version/source/continuity/age.

Quantity, opportunity, navigation and access changes invalidate previews. Late responses cannot restore them; authentication/authorization failures clear private state. The five-second preview may expire while reading the dialog; request a fresh preview after rejection. This policy favors freshness over hidden re-pricing. Historical projections remain snapshots after source changes. There is no paper ledger CSV export, automatic allocation or automatic execution.

## Phase 04B settlement

The only source is typed `ManualScenario`, displayed as Manual Scenario Resolution. A server-created candidate uses persisted paper positions and native instrument definitions captured at entry. The additive migration snapshots retained local catalog definitions for Phase 04A entries. Legacy records without a usable snapshot can use retained local catalog metadata; missing/complex identities fail closed. Price, catalog status, time, monitoring and backend restart never produce resolution facts. The narrow supported shape is a standard two-outcome Yes/No binary contract with one payout/share of 1 and one of 0. No arbitrary payout vector, void, 0.5/0.5, scalar, negative-risk, multivariate, partial exit or SELL is supported. Kalshi quantities requiring sub-cent cash rounding are rejected. Source verification and collateral qualifications are recorded in the Phase 04B verification note.

Owner-only preview performs no persistent mutation. A maximum of 256 tickets live for 30 seconds and bind authenticated actor/workspace, generation financial revision, position identities/revisions/quantities/costs, captured execution plans/states, existing resolutions/outcomes, balance revisions and the exact payout vector. Confirm reauthorizes and reconstructs that proof inside the SQLite writer transaction. Changes reject rather than silently recalculate a reviewed preview. New execution increments the generation revision and rejects `MarketAlreadyResolved` before creating exposure. Writer serialization covers execution, resolution and reset races. Reset does not change old financial facts; a reviewed historical generation can still settle after reset.

One transaction persists `PaperResolutionEntry`, native `PaperResolutionOutcomeEntry` mappings, `PaperSettlement` journal, positive cash credits, position closure, execution economics, generation revision and bounded audit. Resolution identity is unique per generation/exchange/native market; workspace/request ID is independently unique. Fingerprints bind the full confirmation body. Identical requests return the durable original resolution after restart or ticket expiry; changed bodies reject. No update/delete resolution API exists. Cancellation or storage failure rolls back the transaction. Zero-payout positions still close with an auditable resolution and journal, without invented cash entries. Each credit remains in the position's original exchange/currency bucket.

Positions retain quantity, entry cost, fees, average entry and opening time, and gain typed Open/Settled status, resolution reference, payout, realized P&L and settlement time. `Payout = Quantity × PayoutPerShare`; `RealizedPnl = Payout − CostBasis`, using checked decimal. The original modeled entry fee is never refreshed. Execution economics allocate each leg's persisted fills to its market resolution. PartiallySettled executions expose payout/P&L to date, remaining open cost and the original expected remaining payout; final profit and return remain null. Fully settled profit is total payout minus original cost, with exact return-on-cost and expected-versus-actual payout difference. The captured deterministic complementary BUY proof rejects incompatible outcome facts before any ledger write; it never revalidates against a newer relationship policy.

Reconciliation also verifies payout vectors and native identities, request fingerprints, positions' resolution references/payouts/P&L, exact settlement credits and venue/currency, journal identity, execution partial/full state, final profit and settlement timestamps. Corrupt generations remain blocked without automatic repair; historical reads remain available.

## Realized performance and settlement HTTP

Performance groups by generation, exchange and exact currency. It exposes starting cash, current cash, open/settled cost, payout, realized P&L and position/execution counts. Closed generations report ClosedWithOpenPositions or FullySettled; active generations remain Active. No combined USD/USDC number is produced.

Curve points derive from the immutable cash journal and settled positions. Realized P&L is allocated at each position's settlement, including partial execution settlement; completing a basket does not realize the same cost twice. Realized performance is starting capital plus cumulative realized P&L. Cash and open cost are separate accounting diagnostics, never a claim about open-position market value. Funding/execution cash events also appear, with zero realized delta. Ties use SQLite journal insertion order after UTC timestamp. Pages return at most 1,000 points with carried-forward cumulative values; a page can be selected in Desktop. No redundant curve rows, external chart package or price-based valuation exists.

All routes below inherit the authenticated paper membership filter; POST additionally requires Owner.

| Route | Purpose |
| --- | --- |
| GET `resolution-candidates?generationId=&page=` | Unresolved local markets, 50/page, server-owned outcomes |
| POST `resolutions/preview` | generationId, exchange, marketId, winningInstrumentId |
| POST `resolutions/confirm` | requestId, previewId, selection, confirmSimulation |
| GET `resolutions?generationId=&page=` | Immutable history, 50/page |
| GET `resolutions/{id}` | Payout mappings, settled positions, execution and ledger references |
| GET `performance?generationId=` | Separate venue/currency summaries and lifecycle |
| GET `performance/curve?generationId=&exchange=&currency=&page=&pageSize=` | Cash and realized performance points; pageSize 1–1,000 |
| GET `resolutions/diagnostics` | Bounded workspace counters |

Desktop lets the owner select active or retained historical generations, preview a winner and explicitly confirm after an immutability warning. Generation, market, outcome, navigation and access changes invalidate resolution previews; delayed responses cannot restore them. Lost replies retain the request ID, and mutations never retry automatically. The history includes detailed payout mappings and ledger references. Position filters distinguish exposure from settled history. A lightweight WPF line and exact-value table show the selected venue/currency series.
