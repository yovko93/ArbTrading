# Paper execution and Manual Scenario Resolution

## Phase 04E explicitly armed automatic paper execution

Auto Paper remains the Paper environment. An owner must explicitly save a workspace automation profile and confirm Arm against current automation/risk revisions, active healthy generation, and kill-switch revision. There are no active default settings and no startup arming. Monitoring must be Running. Only FixedQuantity is supported: every entry is recomputed at exactly that quantity through the existing opportunity, fee, depth and PaperPlanner path. Insufficient depth/risk/funds rejects the entire attempt; no clipping, sizing search, SELL, exit or settlement is performed.

The singleton backend coordinator owns each memory-only session. Navigation, WPF closure and client disconnection do not stop it. Explicit Disarm clears pending work and prevents a later commit through a synchronized final gate. Backend shutdown stops sessions; restart retains profiles/history/kill state but never armed permits. Policy revision, active generation, monitoring availability, mode, integrity, and current risk are reconciled at least once per one-second worker sweep. Over-limit risk disarms. Transaction-time admission closes the gap between sweeps.

Current FeeAdjusted monitoring rows are the only source. A key-only coalescing queue holds at most 100 candidates per workspace; the worker uses existing deterministic ranking and the configured top-N (1–100). One semaphore serializes processing. A bounded 1,024-key latest-input memo suppresses repeated evaluation of unchanged candidates; persisted request/stamp uniqueness is authoritative for committed duplicates. No component here starts discovery, metadata enrichment, book fetches, fee refreshes or WebSockets. REST inputs are rejected in production. Kalshi requires Continuous realtime. Polymarket requires BestEffort realtime and the explicit saved acknowledgment; its quality is never relabeled Continuous.

The SHA-256 input stamp binds opportunity identity, relationship policy/revision/fingerprints, instrument book versions/source/continuity, effective fee fingerprints/profile revision, automation revision and exact quantity. Canonical invariant decimal serialization makes numeric scale and locale irrelevant; observation time is excluded. RequestId deterministically binds armed SessionId, key, stamp and quantity. An already committed request returns its durable result even if the session later stops. Unchanged rejected candidates wait for changed input or explicit rearming, rather than retrying on every timer tick.

Session/relationship counts and gross debit budgets use committed automatic facts for the session; hourly count and per-opportunity/per-relationship cooldowns span workspace sessions, restarts and generations. Manual entries do not consume automatic caps, but all entries share risk and funds. Session/hour exhaustion disarms; relationship/debit/cooldown violations skip. Gross debits include entry notional and fees, with no credit from settlement, P&L or account resets. Every `(exchange,currency)` budget is `InitialCash × MaximumSessionDebitFractionPerBucket`; currencies are never pooled or converted.

Emergency stop commits the persistent latch first, without waiting on the worker semaphore, then cancels runtime work. The shared `PaperStore.CommitAsync` reads latch/revision under the SAME SQLite writer reservation used for admission, balances, positions, journal and commit. If an automatic writer already owns that reservation, it may finish before the latch transaction; that earlier execution remains valid. Once the latch commits, no subsequent automatic financial commit is permitted until explicit owner reset and separate rearm. Reset uses expected revision, confirmation, and rejects an Armed session. Manual execution, settlement and valuation remain separately available. Routine disarm does not latch.

Execution history records Manual/AutomaticPaper origin plus session, policy version/revision/fingerprint and trigger stamp. Legacy rows default Manual. Reconciliation checks stored provenance together with existing risk/financial proofs; it does not apply current automation policy to historical executions. Lifecycle audits cover profile save/update, arm/disarm, emergency/reset. Bounded runtime counters record skips/rejections without writing a database row per tick. Small `PaperAutomationChanged` and paper-financial invalidations prompt authoritative REST refreshes.

Owner-only mutations under `/api/v1/workspaces/{workspaceId}/paper/automation`: `PUT profile`, `POST arm`, `POST disarm`, `POST emergency-stop`, `POST reset-kill-switch`. Authorized workspace reads: `GET profile`, `GET status`, `GET diagnostics`. Invalid/stale confirmations fail closed. Desktop performs one attempt per mutation, clears unavailable state, and protects against delayed responses across navigation/access/backend changes. Its arm confirmation warns about immediate execution, lack of per-entry confirmation and backend ownership.

The additive PaperAutomation migration adds two workspace tables and nullable execution provenance plus default Manual origin and indexes. Earlier migrations are untouched; the upgrade fixture compares all preexisting columns from the published 04D schema. Only isolated test storage is migrated during verification.

## Phase 04D capital and risk admission

NEW exposure requires an explicitly saved workspace risk profile. An absent profile is `NotConfigured`, yielding `RiskPolicyNotConfigured`; it does not block initialization/reset, historical reads, settlement, valuation or reconciliation. No startup path saves defaults. The desktop form suggests reserve 20%, single execution 10%, total open cost 60%, market 20%, instrument 20%, 20 open positions, 10 open executions, 2 executions per relationship, quantity 1,000, edge 0.001/share and profit 0. These are inactive form suggestions, not an optimal allocation. The hard planner still requires strictly positive fee-adjusted profit. Save requires a warning confirmation and one HTTP mutation attempt.

`PaperRiskProfiles` has one row per workspace, version 1, a new GUID revision for every successful mutation, UTC creation/update times, authenticated updater and a canonical SHA-256 fingerprint. Exact decimal limits are stored as owned scalar columns. Owner-only PUT requires the expected revision (null for create), and records the complete bounded profile in `PaperRiskPolicyConfigured`/`PaperRiskPolicyUpdated` audit. Two edits with the same revision cannot both succeed. Reset and restart preserve the policy. Invalid profiles are rejected without clamping; corrupt version/fingerprint/settings block new entry without repair. An owner may explicitly replace a damaged profile with valid settings and the current revision.

Fractions require reserve in [0,1), single and total in (0,1], market in (0,total], instrument in (0,market]. Position/execution counts are 1–1,000, relationship count 1–maximum executions. Quantity is positive and at most the immutable planner maximum of 1,000; edge is at least the hard floor 0.001 and below 1; minimum profit is nonnegative. Policy can only tighten deterministic relationship, fee, depth, freshness, skew, liquidity, currency, mode and funds eligibility. It does not change ranking or select quantity.

Admission uses each exchange/currency bucket's own **InitialCash**, never marked equity or unrealized P&L. `AvailableCash − plan debit >= InitialCash × reserve`; the fee-inclusive plan debit must not exceed `InitialCash × single fraction`. Open cost plus proposed fee-inclusive fill cost must not exceed `InitialCash × total fraction`. Native market exposure groups by generation/exchange/currency/market; native instrument exposure additionally groups by instrument and outcome. Equality qualifies. Every existing bucket and exposure is checked, so tightening an unrelated bucket can block new entry until all current exposure is within limits. USD and USDC are never netted or converted.

Only Open materialized positions count. Adding to an existing native identity does not increase position count. Only Committed and PartiallySettled executions count; captured relationship IDs control repetition counts. Settled executions/positions do not count. A new basket adds one execution. At most the top 20 current relationship counts are exposed. Existing cached external depth is never depleted by paper fills. State reads use indexed open positions/executions, with a fail-closed bound of 1,000 each; larger legacy states report integrity unavailable rather than silently truncating admission.

The pure evaluator runs after the exact requested-quantity planner. Typed decisions include policy and generation revisions, timestamp, current/projected counts, bucket cash and cost headroom, native exposure details and violation codes with nullable dimensional context. Rejected previews retain fills/economics for inspection. Both `WouldExecute` and `RiskApproved` must be true to enable desktop confirmation; the server remains authoritative.

Preview captures a coherent deferred SQLite snapshot of policy, generation revision, balances, open positions and open executions. Its server-owned ticket retains the decision, including bucket and exposure state. Confirmation first recovers a previously committed RequestId/body. For a new request, the existing immediate SQLite writer transaction revalidates hard eligibility and funds, reads current policy/accounting, and recomputes risk. A changed policy revision rejects `RiskPolicyChanged` even if looser. Failed current limits reject `RiskLimitExceeded` with specific violations. Otherwise a changed financial revision rejects `FinancialStateChanged`, requiring a fresh preview of headroom. No network or SignalR work is held inside this transaction. Risk approval, funds and the ledger commit share the same writer reservation; the existing final orderbook guard remains intact.

New executions retain nullable version/revision columns and a bounded serialized Approved decision bound to the committed plan ID, quantity and cost. Reconciliation verifies supported metadata, fingerprint format, approval, generation and debit identity without applying today's policy to history. Old executions with no risk proof remain valid and settle normally. Tightening may produce `OverLimit`; it never liquidates or alters cash/positions. Settlement reduces exposure independently and valuation remains diagnostic.

Authenticated routes under `/api/v1/workspaces/{workspaceId}/paper`:

| Route | Purpose |
| --- | --- |
| GET `admission-policy` | Current saved profile, or null |
| PUT `admission-policy` | Owner-confirmed settings plus ExpectedRevision |
| GET `admission-status?generationId=` | NotConfigured / WithinLimits / OverLimit / IntegrityFailure, profile and accounting assessment |
| GET `admission-diagnostics` | Bounded workspace counters for evaluations, approvals, violations, changes and conflicts |

The existing valuation `risk` endpoint is unchanged. Historical generation status compares today's policy diagnostically; it never rewrites entry proof. Reads require workspace membership, writes require Owner, and actor IDs come from authenticated context. The current membership schema permits only Owner; no new role is introduced. Unknown/cross-workspace generation reads return 404 after membership checks. Small existing paper invalidations clear reviewed previews and trigger REST refetch through the active page's consistency loop. No policy/ledger payload is sent through SignalR.

Migration `20260923154728_PaperRiskPolicy` adds the policy table, nullable execution proof columns and open-state indexes. Earlier migrations are unchanged. Normal user storage must follow the documented explicit backup/upgrade procedure; development/tests migrate only isolated storage. Automatic sizing, private exchange balances and real trading remain unavailable. Explicitly armed automatic paper entries are described in Phase 04E above.

Execution contains the pure planner, typed rejection policy and checked decimal accounting. It references Application and transitively Domain, with no HTTP, EF/SQLite, WPF, transport or credential dependency. Infrastructure owns persistence; Backend coordinates authenticated requests; Desktop references Contracts and obtains business data over HTTP.

This is **Snapshot Paper Fill / Immediate Taker Simulation**, not a prediction of actual fills. **Paper atomicity is NOT evidence that live cross-exchange execution can be atomic.** There is no latency, maker queue, market impact, partial basket, private account API, signing, real order, transfer or capital allocation. Phase 04B adds explicit simulated resolution only. Exchange accounts and credentials are unnecessary. Existing optional Kalshi market-data credentials are never read by paper code.

## Accounts and journal

Startup creates no paper money. An owner explicitly confirms initial venue/currency amounts. Desktop prefills editable 10,000 Kalshi USD and 10,000 Polymarket USDC; these remain form values until confirmation. Up to four distinct buckets accept amounts from zero to 1,000,000,000. Currency labels are USD/USDC; funding never changes the execution currency established by fee metadata. Common-USD test fixtures do not override production Polymarket metadata. There is no FX, borrowing, margin or negative cash.

Reset requires the expected active generation ID and a reason. It atomically closes the old generation and creates the new generation and funding journal. Old balances, positions, fills and executions remain queryable. New execution cannot target a closed generation.

Persistent tables represent generations, venue balances, executions, legs, fills, ledger transactions, cash entries and positions. Journal transactions identify generation, actor, timestamp and execution (except initial funding). Reserve, fill-notional and modeled-fee entries explain every debit. Reservation and consumption occur in one transaction; successful fills leave reserved cash zero. Funding/reset and successful execution also write audit records. Monetary values are decimal; UTC DateTimeOffset values use the established SQLite UTC-ticks conversion.

Execution plans retain relationship ID/trust/revision/policy/fingerprints; canonical book versions/source/continuity/timestamps/skew; paired segments; and fee breakdowns/fingerprints/profile revision/effective rules. Separate leg/fill records retain native instruments/outcomes, exact quantities/prices/notionals, liquidity origin and native source identity.

Positions are keyed by generation + venue + native market + native instrument + outcome. Cost basis includes notional and modeled fees. Weighted average entry excludes fees: cumulative notional divided by quantity. Expected payout and expected profit **at resolution** are projections. Only confirmed Manual Scenario Resolution creates settlement cash and realized P&L. Phase 04C adds separate, read-only current liquidation estimates as described below.

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

Quantity, opportunity, navigation and access changes invalidate manual previews. Late responses cannot restore them; authentication/authorization failures clear private state. The five-second preview may expire while reading the dialog; request a fresh preview after rejection. This policy favors freshness over hidden re-pricing. Historical projections remain snapshots after source changes. There is no paper ledger CSV export or automatic allocation. The separately armed fixed-quantity entry path is described above.

## Phase 04B settlement

The only source is typed `ManualScenario`, displayed as Manual Scenario Resolution. A server-created candidate uses persisted paper positions and native instrument definitions captured at entry. The additive migration snapshots retained local catalog definitions for Phase 04A entries. Legacy records without a usable snapshot can use retained local catalog metadata; missing/complex identities fail closed. Price, catalog status, time, monitoring and backend restart never produce resolution facts. The narrow supported shape is a standard two-outcome Yes/No binary contract with one payout/share of 1 and one of 0. No arbitrary payout vector, void, 0.5/0.5, scalar, negative-risk, multivariate, partial exit or SELL is supported. Kalshi quantities requiring sub-cent cash rounding are rejected. Source verification and collateral qualifications are recorded in the Phase 04B verification note.

Owner-only preview performs no persistent mutation. A maximum of 256 tickets live for 30 seconds and bind authenticated actor/workspace, generation financial revision, position identities/revisions/quantities/costs, captured execution plans/states, existing resolutions/outcomes, balance revisions and the exact payout vector. Confirm reauthorizes and reconstructs that proof inside the SQLite writer transaction. Changes reject rather than silently recalculate a reviewed preview. New execution increments the generation revision and rejects `MarketAlreadyResolved` before creating exposure. Writer serialization covers execution, resolution and reset races. Reset does not change old financial facts; a reviewed historical generation can still settle after reset.

One transaction persists `PaperResolutionEntry`, native `PaperResolutionOutcomeEntry` mappings, `PaperSettlement` journal, positive cash credits, position closure, execution economics, generation revision and bounded audit. Resolution identity is unique per generation/exchange/native market; workspace/request ID is independently unique. Fingerprints bind the full confirmation body. Identical requests return the durable original resolution after restart or ticket expiry; changed bodies reject. No update/delete resolution API exists. Cancellation or storage failure rolls back the transaction. Zero-payout positions still close with an auditable resolution and journal, without invented cash entries. Each credit remains in the position's original exchange/currency bucket.

Positions retain quantity, entry cost, fees, average entry and opening time, and gain typed Open/Settled status, resolution reference, payout, realized P&L and settlement time. `Payout = Quantity × PayoutPerShare`; `RealizedPnl = Payout − CostBasis`, using checked decimal. The original modeled entry fee is never refreshed. Execution economics allocate each leg's persisted fills to its market resolution. PartiallySettled executions expose payout/P&L to date, remaining open cost and the original expected remaining payout; final profit and return remain null. Fully settled profit is total payout minus original cost, with exact return-on-cost and expected-versus-actual payout difference. The captured deterministic complementary BUY proof rejects incompatible outcome facts before any ledger write; it never revalidates against a newer relationship policy.

Reconciliation also verifies payout vectors and native identities, request fingerprints, positions' resolution references/payouts/P&L, exact settlement credits and venue/currency, journal identity, execution partial/full state, final profit and settlement timestamps. Corrupt generations remain blocked without automatic repair; historical reads remain available.

## Realized performance and settlement HTTP

Performance groups by generation, exchange and exact currency. It exposes starting cash, current cash, open/settled cost, payout, realized P&L and position/execution counts. Closed generations report ClosedWithOpenPositions or FullySettled; active generations remain Active. No combined USD/USDC number is produced.

Curve points derive from the immutable cash journal and settled positions. Realized P&L is allocated at each position's settlement, including partial execution settlement; completing a basket does not realize the same cost twice. Realized performance is starting capital plus cumulative realized P&L. Cash and open cost are separate accounting diagnostics, never a claim about open-position market value. Funding/execution cash events also appear, with zero realized delta. Ties use SQLite journal insertion order after UTC timestamp. Pages return at most 1,000 points with carried-forward cumulative values; a page can be selected in Desktop. No redundant curve rows or external chart package exists. Phase 04C current marks are separate from this historical realized curve.

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

## Phase 04C current executable liquidation valuation

`PaperValuation` is an ephemeral read model, never ledger accounting. A short deferred SQLite read transaction captures a generation's positions, balances, catalog identities and cached fee schedules/profile together; it is disposed before depth calculation. Up to 1,000 total retained positions (open and settled) per generation are supported. Larger generations reject explicitly instead of presenting a truncated equity summary. No migration, mark table, persisted tick, price history or financial mutation is added.

The existing `ExecutableDepth` SELL action walks the held native instrument's bids and returns exact consumed levels. Kalshi YES/NO liquidation uses that outcome's native bids, never complement asks. Polymarket uses the catalog-mapped token and exact persisted outcome, never title or array index. Missing or changed native mappings are InstrumentUnsupported. Historical position identities are not rewritten.

A coherent bounded cache capture reads the required immutable books under one brief gate. A final capture detects version replacement and returns BookChangedDuringValuation for affected positions without retries. SQLite, HTTP, UI and fee calculations never run under the cache lock. BookEligibility controls freshness and continuity: fresh REST, Kalshi Continuous and Polymarket BestEffort remain distinct. Invalid, stale, missing, disconnected/gapped/resynchronizing inputs produce unavailable current marks. Source time, retrieval time, age, version, continuity, native liquidity provenance and per-level fee quotes remain inspectable.

The whole position quantity must be executable for a full gross mark. PartialDepth exposes executable/unfilled quantity and a partial gross subtotal/VWAP only; all full value and P&L fields remain null. Empty bids likewise yield incomplete depth. There is no midpoint, last-price, best-bid, zero or entry-price fallback for missing quantity. Settled inventory is NotApplicable with null mark fields and retains settlement payout/realized P&L.

Gross unrealized P&L is gross liquidation value minus original fee-inclusive cost basis. Historical entry fees are never subtracted again or recalculated. Current hypothetical exit fees use Phase 03C FeeScheduleResolver and FeeMath per consumed bid level, as a taker SELL diagnostic, including current effective rules and diagnostic account precision profile. One hypothetical order per position and one modeled fill per native level are assumptions; real fragmentation, impact and program rebates are excluded. Schedule/account/verification/currency uncertainty leaves adjusted values null while gross remains available. The existing Kalshi public-contract discrepancy remains fail-closed. No conflicting source is selected to force an exact exit fee. A fee currency different from recorded inventory currency is unresolved, with no implicit relabeling or FX.

Summaries and risk diagnostics remain separate by generation/exchange/currency:

- Accounting book value = current cash + open cost basis; independent of books.
- Gross marked equity = cash + full gross marks only when every open position has a full current mark.
- Fee-adjusted marked equity additionally requires every open position's exit fees to resolve.
- Total P&L = cumulative settlement realized P&L + current unrealized P&L, and is null unless the corresponding coverage is complete.
- KnownGrossMarkedOpenValue is a labeled subtotal of fully marked positions, never a replacement for full equity.
- Coverage reports full/partial/unavailable counts and the full-count ratio. No open inventory means complete coverage and cash-only equity.
- Capital utilization by cost = open cost / starting cash when positive. Concentration = largest position cost / open cost when positive; unique-market counts are descriptive, not risk probabilities.

USD and USDC are never summed. Closed generations can use current books, labeled current marks applied to historical inventory. Partial settlement includes only the remaining open leg in exposure; settled cash/payout is not added twice. Captured execution hold-to-resolution projections stay separate from current liquidation estimates. The existing realized performance curve is unchanged and is not a historical MTM curve.

All routes inherit authenticated workspace membership and accept no actor identity:

| GET route under paper | Result |
| --- | --- |
| `valuation?generationId=&page=&pageSize=` | Active or historical portfolio marks and full bucket summaries |
| `risk?generationId=&page=&pageSize=` | Same bounded current read model with coverage, utilization and concentration |
| `positions/{positionId}/valuation?generationId=` | Single position; omitted generation resolves its authorized retained generation |

Page sizes are 1–1,000, default 100. Summary coverage always includes the whole supported generation, not just the page. Unknown/cross-workspace generation or position is 404 after membership authorization; uninitialized active reads are empty without creating funds. Oversized generations are 400, aggregate decimal overflow is 409, and individual mark overflow is typed ArithmeticOverflow.

Desktop adds Current Executable Mark, per-bucket summaries, paged marks and selected provenance details. Refresh Valuation performs only a local GET. The active paper-page loop refetches at its existing two-second interval; navigation stops that loop and invalidates delayed work. Book/catalog/financial/fee invalidations clear displayed marks; the next active-page read recomputes locally. Notifications carry no depth or portfolio values. Backend/workspace/generation/page/navigation/access guards prevent delayed responses from restoring old private data. No always-running valuation scanner exists. Realized history and original execution projections remain separately labeled.

There is no market discovery, REST book acquisition, WebSocket start, fee refresh, relationship enrichment, account requirement, SELL/close action, cash reservation or automatic decision in valuation. After backend restart an empty cache yields BookUnavailable; only a separate explicit market-data operation can populate it.
