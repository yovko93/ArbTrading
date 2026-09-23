# Read-only gross and fee-adjusted opportunities (Phases 03B–03C)

`Arbitrage.Strategies` implements pure deterministic planning and paired-depth evaluation. It references Application and Domain, with no HTTP, SQLite, exchange clients or WPF. Backend composes the approved relationship provider, local catalog instrument resolver, existing canonical book cache and evaluator. Desktop references Contracts and reads business data over authenticated HTTP. Phase 03C adds a migration for bounded fee metadata and diagnostic profiles; no opportunity or fee tick table is introduced.

## Proof and trust

Only current `VerifiedDeterministic` relationships enter a default scan. `IncludeManualRelationships` is false unless explicitly requested; opted-in results carry `RelationshipTrust = Manual`. Proposed, NeedsReview, Rejected, Stale, changed fingerprints and obsolete policies are excluded by the real relationship provider. Selected ineligible IDs produce an empty run with an eligibility notice. Results invalidated after admission carry RelationshipIneligible.

The provider exposes a bounded page with raw scanned count and HasMore, so filtering obsolete approvals cannot disguise a truncated scan as complete. Detached reads recheck the current SQLite source descriptors. Results bind to relationship ID, policy, source fingerprints and a revision covering trust, type, update instant, set facts and ordered outcome mappings. The coordinator checks this again after calculation and on every retained-result read.

An inverse-worded row can map YES to NO **as the same economic outcome**. The planner follows the mapping type, not the row title, row type alone or label. Same-outcome BUY/SELL price spreads remain RequiresInventory and never establish a guaranteed payout. When the existing Phase 03A binary validator proves a venue's complementary partition, the planner can substitute its opposite native outcome to form a closed two-BUY basket. Direct approved opposite-outcome mappings or an explicitly exclusive and exhaustive selected two-outcome set also establish a two-BUY basket. Three-or-more outcome sets are unsupported. Conflicting same-outcome and complementary-partition claims fail closed as UnsupportedStrategy.

SingleMarketBinaryComplement requires an approved relationship's evidenced binary partition and distinct catalog-addressable native instruments. Polymarket native token IDs are resolved from current catalog data, never array positions. Kalshi requires currently supported binary metadata and retains the Phase 02B normalizer's crossed/locked-book rejection. This phase does not relax that guard to manufacture same-market candidates. Ordinary catalog prose still usually remains NeedsReview under Phase 03A; fixtures prove engine behavior, not live exchange equivalence.

## Arithmetic and native liquidity

For a proven complementary pair, consume the cheaper remaining asks from both immutable books in lockstep. Each segment takes the lesser remaining quantity at the two current levels, bounded by remaining diagnostic quantity and optional notional. Advance whichever level was exhausted. Stop when either side ends, a bound is reached, or marginal `1 - priceA - priceB` is not positive or falls below the configured minimum. **Positive equality at the minimum qualifies; zero edge never qualifies.**

Retain segment quantity, both prices, combined price, marginal edge and cumulative cost/payout/profit. Guaranteed gross payout equals paired quantity, gross profit equals payout minus cost, average gross edge equals profit/quantity, and gross return on cost is null when cost is zero. Canonical `ExecutableDepth` independently computes each consumed leg and must agree exactly with the paired cost. Checked decimal arithmetic is used; overflow or disagreement fails closed. No binary floating-point financial arithmetic or rounding to currency cents occurs. Tables use up to eight decimal places for display; the detail and API retain decimal values.

The optional API notional limit is diagnostic only. Decimal division that would round through the cap steps down by one representable decimal unit; the independently calculated total must still remain within the cap. Optional RequestedQuantity is distinct from actual available paired depth: FullyExecutableForRequestedQuantity is true only when an explicit request is fully covered. With no requested quantity, that flag remains false. Quantity/notional cap flags do not claim the returned quantity is the market maximum.

Domain supplies native liquidity identities `(exchange, market, instrument, consuming action, native price)`. At aggregate L2 granularity, a Kalshi YES derived ask at 0.60 and its originating NO bid at 0.40 share the same identity. A combination sharing any source level is conservatively rejected as LiquidityConflict, including partial use; unused conflicting levels can therefore also reject a combination. Different levels, markets and exchanges remain distinct. Derived asks are allowed against independent liquidity and are explicitly marked in the result. There is no fabricated exchange order ID and no allocation of a shared source to two legs.

## Cache and observation consistency

Both immutable snapshots and versions are captured under the existing cache lock. Evaluation never refreshes a book, starts a subscription, syncs catalog or enriches metadata. Current instrument resolution reads local catalog only. Missing data stays BookUnavailable. Existing `BookEligibility` remains authoritative: fresh REST is diagnostic; Kalshi realtime must be anchored, connected and Continuous; Polymarket BestEffort remains explicitly diagnostic. Mixed source modes are labeled Mixed. Non-actionable results use NonActionable quality.

ObservedSkew is the absolute difference of canonical local `RetrievedAtUtc` observation/update instants. Source timestamps are separately retained. The default 1,000 ms is a conservative diagnostic coherence check, not a latency or trading assumption. REST snapshots are not described as synchronized realtime. A coherent local read is never an atomic cross-exchange observation.

After math, the coordinator rereads current relationship proof and cached book eligibility/versions. A version mismatch yields BooksChangedDuringEvaluation without an unbounded retry loop. Retained reads yield StaleInput for changed versions and BookStale when unchanged snapshots age out. Rejected or changed relationships yield RelationshipIneligible. All checks describe the observed state at validation time; they cannot freeze later exchange changes. Prior calculations may remain in diagnostic details, with GrossArbitrageExists false. The primary list includes only currently validated Detected results with positive gross profit/edge.

## Jobs, ownership and retention

Authenticated actor identity comes from request context. Every endpoint checks workspace membership, including status and current-key reads; start/cancel require Owner. Runs separately retain actor and workspace. Accepted runs are backend-owned and do not share request-disconnect cancellation. One global active run is admitted; competing starts get 409. No job starts on backend startup or desktop navigation.

| Bound | Default | Maximum |
| --- | ---: | ---: |
| Relationships scanned | 100 | 500 |
| Runtime seconds | 10 | 30 |
| Results per run | 30 | 50 |
| Diagnostic paired quantity | 1,000 | 1,000,000,000 |
| Minimum gross edge | 0.001 | range 0 inclusive to 1 exclusive |
| Observation skew milliseconds | 1,000 | 60,000 |
| Optional notional/requested quantity | omitted | 1,000,000,000 |
| Result page size | 20 | 50 |
| Retained runs | 4 | 4 |
| Retained paired segments per run | 100,000 | 100,000 |

Result, relationship, runtime or retained-depth budget exhaustion reports Partial, not Completed. Cancellation and backend shutdown report Cancelled. Errors fail closed, with bounded notices and no price-level logging. Runs and results are ephemeral; restart loses them, and a fifth run evicts the oldest terminal run. The cache's own 20,000-level side bound is unchanged. The optional source/target exchange filters narrow the bounded approved scope; the relationship cap is applied before the target-pair filter. A bounded scan is not a universal search.

Under `/api/v1/workspaces/{workspaceId}/opportunities`:

- `POST /evaluate`: bounded approved scan, with optional RelationshipId and manual opt-in.
- `POST /relationships/{relationshipId}/evaluate`: selected relationship only.
- `GET /jobs/{id}`: admission/terminal metadata and scanned/result counts.
- `GET /jobs/{id}/results?page=1&pageSize=20&diagnostics=false&sort=key`: current revalidated page. `sort=grossProfit` is presentation only.
- `POST /jobs/{id}/cancel`: cancellation request.
- `GET /current/{key}`: newest retained explicit result for that logical key, revalidated.

Keys are SHA-256 over strategy, relationship ID and canonically ordered exchange/market/native-instrument/action legs. Display names, enumeration order and newly generated snapshot GUIDs are excluded. Scoping changes and action directions change the key.

## Desktop and deferred concerns

Opportunities provides Evaluate Selected, Evaluate Verified Relationships and Cancel Evaluation. Copy a relationship ID from Market Matching for selected evaluation. Manual opt-in and diagnostic thresholds are explicit. The default list excludes blocked/no-edge results; Show diagnostics includes their reasons. Read-only polling every second while the page is active revalidates retained results and job progress without recalculation. Book/catalog notifications immediately clear displayed results; access, navigation and backend-generation guards reject late responses. External relationship changes are caught by the next retained read. Navigation away cancels local reads, not an accepted backend job.

The detail panel exposes approved mappings, relationship trust/policy/fingerprints/revision, versions, timestamps, continuity, liquidity sources, VWAP, worst prices and exact paired segments. PRE-FEE, unknown net values and unavailable execution remain visible. No Execute, Trade, Auto Trade or Paper Trade action exists.

Statuses distinguish Detected, NoGrossEdge, InsufficientLiquidity, RelationshipIneligible, BookUnavailable, BookStale, BookInvalid, BookContinuityInsufficient, BookSkewTooLarge, BooksChangedDuringEvaluation, UnsupportedStrategy, LiquidityConflict, RequiresInventory, StaleInput and ArithmeticOverflow. Separate facts include RelationshipEligible, BooksActionable, GrossArbitrageExists and FullyExecutableForRequestedQuantity. Gross-only requests retain FeeStatus NotEvaluated. Fee-aware requests expose a separate typed Fees result; NetProfit/NetEdge remain null and ExecutionEligible is false.

Balances, usable capital, allocation, inventory, simulated completed fills and order submission are absent. Phase 03C exchange-fee diagnostics are described below. Leg ordering, IOC/FOK, latency, partial fills, rollback/hedging, settlement lockup and transfers remain execution-layer concerns. No exchange account is required to evaluate cached books. Phase 03D adds explicitly started local monitoring, described below; it never starts opportunity-driven subscriptions.

## Phase 03C fee model

`Domain/Fees.cs` contains typed schedules, effective rules, decimal formulas and per-fill rounding components. Application composes fee quotes over the existing gross result; it never re-walks the books or changes gross selection/thresholds. Each leg merges adjacent paired segments at the same native price before fee calculation. It models one taker order per leg, carries the Kalshi rounding accumulator across its modeled price-level fills, and charges derived asks once for their underlying resting liquidity. Unknown roles fail closed.

L2 has no actual fill fragmentation. Therefore a completed opportunity fee result is **Estimated**, even if its individual hypothetical fill math is exact. Polymarket's unspecified five-decimal rounding direction is handled with a conservative ceiling per modeled fill (subminimum amounts below 0.00001 are zero per the fee page). This is not a bound on an arbitrary unknown number of actual fills. Program rebates, rewards and referrals are NotIncluded. Kalshi rounding rebates are a separate mechanical component, not account incentives.

Schedules resolve current series, future series changes, current event override, future event changes and explicit null clear. Event rules override series until cleared. Effective timestamps are inclusive; conflicting same-level/same-time rules fail closed. The acquisition window must not contain a scheduled transition. Freshness is one hour and independent of active effective time. Fingerprints include formula version, authoritative identities, rule history, source, currency, instruments and verification issues. Retained reads re-resolve current fingerprints and boundaries, verify the profile revision, and preserve gross amounts while nulling stale fee totals. Gross invalidation also invalidates fees.

Unknown fees are null. `KnownModelAccountRoundingUnknown` retains model/trade components while total and adjusted fields remain unresolved. Known diagnostic profiles are local assumptions, never authenticated account types. Profile revisions make even an A→B→A change invalidate retained fees. The local backend persists one profile per workspace and one bounded schedule bundle per catalog market, with foreign keys; no user identifiers are inferred from ownership. Profile saves and successful public refreshes audit the authenticated actor separately.

`EvaluateFees` is false by default in the API for backward compatibility; Desktop enables it by default. `MinimumFeeAdjustedEdgePerShare` is independent of the unchanged gross threshold; omitted means zero, range is [0,1). Positive equality qualifies; zero or negative profit never qualifies. Fee-adjusted cost is gross cost plus fees; adjusted guaranteed profit is gross profit minus fees; adjusted edge divides that profit by paired quantity; adjusted return divides by adjusted cost. NetProfit/NetEdge remain null. Currency-mismatched legs have no combined fee total. Fixture USD denomination is an explicit testing assumption, not a production USDC conversion.

The primary fee-aware list requires FeeAdjustedDetected. Gross-only jobs retain their earlier primary-list behavior. Diagnostics retain gross candidates with missing/disputed fees, removed edges and stale results. WPF columns support sorting fee-adjusted values on the displayed page; this is not monitoring or execution ranking. Details show sources, retrieval age, effective time, profile, individual components, warnings and assumptions. Settings loads/saves profiles only on explicit actions and does not contact exchanges.

Under `/api/v1/workspaces/{workspaceId}/fees`:

- `GET /profile`, `PUT /profile` with `{ "profile": "Unknown|DirectMember|NonDirectMember" }`.
- `GET /schedules/{exchange}/{marketId}` reads cached provenance/rules/status only.
- `POST /refresh` with `{ "markets": [{ "exchange": "Kalshi", "marketId": "..." }], "runtimeSeconds": 30 }` explicitly starts public metadata acquisition.
- `GET /jobs/{id}`, `POST /jobs/{id}/cancel` expose bounded backend-owned refresh progress.

All routes inherit authentication and check workspace membership; writes require Owner. Refresh is global single-flight (409 for duplicates), at most ten selected catalog markets, thirty seconds, four retained summaries. Per Kalshi market: one market, event and series, one bounded series-changes response and at most four 100-entry event pages. Each response is at most 1 MB, each operation at most 25 seconds, and each response permits at most three 429/5xx attempts. Retry-After is honored or the retry is refused, never shortened. Fixed public HTTPS hosts, no redirects, normal TLS, no bearer/signing-key dependency. Partial/cancelled/failed remain explicit; accepted work is independent of the request lifetime. Failed metadata replaces a prior success with an unavailable marker. No implicit fee refresh occurs from evaluation, header Refresh, navigation or startup.

`Arbitrage.Fees` publishes counters with finite enum labels for resolved quote statuses, gross candidates, fee-adjusted results, removed edge, unavailable/stale schedules and account profile requirements. No market/level tags or component logging are emitted. See [Phase 03C verification](../Development/Phase03CVerification.md) for exact official sources, conflicts, fixtures and performed checks.

## Phase 03D continuous local monitoring

`MonitoringCoordinator` is a backend-owned hosted service with explicit Stopped, Starting, Running, Degraded, Stopping and Faulted states. Start admits one workspace at a time; request cancellation or Desktop closure does not own that run. Stop cancels evaluations and clears current rows, then records an audit and settles. Shutdown uses the same path. Lifecycle state is not persisted, so startup never resumes a previous monitor. Profiles and compact alert events persist; current rankings, dirty work and alert rearm state are session-local. A fresh explicit start establishes a new crossing baseline.

The coordinator has no exchange transport dependency. It reuses `OpportunityCoordinator`, `OpportunityPlanner`, canonical cache eligibility, the paired L2 gross evaluator, and the Phase 03C fee evaluator. No monitor action invokes discovery, relationship generation/enrichment, REST book acquisition, subscription start, fee refresh, balances or execution.

### Bounded local work

The profile selects up to 250 approved relationships by default, at most 1,000, ordered by persisted relationship GUID. Manual verification is excluded unless explicitly enabled. Invalid current fingerprints/policy are rejected by the existing provider. At most 2,000 plans/results and two alert-state entries per plan are retained. Partial coverage and skipped counts are explicit; the available count describes locally stored approved rows, not all exchange markets or globally revalidated candidates outside the selected bound. The canonical cache remains separately bounded (128 instruments by default, configurable up to 1,024).

`LocalInputChanges` accepts bounded instrument, market, relationship and workspace-profile invalidations. Cache publication and successful EF saves enqueue only small identifiers, with no HTTP, callbacks or disk work on receive loops. The capacity is 2,048 distinct notices; duplicate notices coalesce. EF notifications can precede an outer transaction commit, and bulk updates may emit no notice, so they are hints rather than a correctness boundary.

The dependency index maps instrument/market/relationship identifiers to stable opportunity keys. A 200 ms shared timer drains notices into a deduplicated dirty set capped at 1,024. Each pass evaluates up to 32 keys. Notification overflow requests a bounded full local reconciliation; a queue of at most 2,000 plan keys fills available dirty slots over successive batches. Overflow is observable, not silent correctness loss. Counters report received/coalesced/dropped notices, reconciliation passes, evaluations, failures, ranking updates, alert suppression and CSV failures. These are session diagnostics, with no per-market metric labels.

A shared one-second sweep rereads the bounded approval scope and compares book versions/actionability and resolved fee/profile stamps. It catches aging without updates, effective fee boundaries, invalid approval/fingerprints/policy, missed notifications and transaction timing. Unchanged plans are not economically reevaluated. Fee reads are memoized per market within a sweep. One worker serializes evaluation batches; there is no timer per relationship or task per tick. An item exception is isolated, drops its result and queues a retry; access loss faults the session closed.

Each result contains stable key, generation, relationship revision/fingerprints, canonical book versions, and fee/profile stamps. Profile save commits under the worker gate, clears prior current rows and schedules reevaluation. Publication discards cancelled or superseded generations. The existing local validation path runs again before publication and HTTP ranking reads, including a final canonical version/eligibility check after asynchronous fee reads. Invalid current inputs cannot remain in an active lane. Reads never call exchange APIs or recalculate economics. They validate the bounded in-memory set, then filter/sort/page; maximum-size reads can cost more than a small page because correctness checks precede ranking.

### Lanes, ranking and near-edge

Lanes are always kept separate, in order FeeAdjusted, GrossOnly, NearEdge, Blocked. FeeAdjusted requires a detected fee result with known exchange-fee totals and positive modeled profit. GrossOnly requires positive gross economics with unresolved totals; it is diagnostic. Kalshi public source conflicts remain in that lane and cannot trigger default fee alerts. Blocked inputs never become near-edge merely because old economics exist.

Default sort tuples are:

- FeeAdjusted: fee-adjusted profit descending, edge descending, quantity descending, quality, skew ascending, stable key ordinal.
- GrossOnly: gross profit descending, edge descending, quantity descending, quality, skew ascending, stable key ordinal.
- NearEdge: distance ascending, available quantity descending, skew ascending, stable key ordinal.

Quality is an explicit tie-break: RealtimeContinuous, FreshRest, Mixed, RealtimeBestEffort, NonActionable. No weighted score exists. Alternate per-lane sorts are grossProfit, grossEdge, feeAdjustedProfit, feeAdjustedEdge, quantity, nearEdgeDistance and updated. Null fee metrics follow known values; stable key breaks all ties.

After all existing gross guards pass, the gross evaluator exposes best observed gross edge (`1 - best ask A - best ask B`) and top-level paired quantity capped by the diagnostic quantity limit, including when the ranking threshold prevents an economic candidate. Near-edge distance is required edge minus observed edge, within the configured nonnegative window. When a fee-aware evaluated basket has known totals but misses its fee threshold, the fee-adjusted edge/threshold are used. Otherwise near-edge remains explicitly gross-based; it never invents a fee-adjusted result for an unconsumed basket. Missing, stale, gapped, skewed, unapproved or conflicting-liquidity inputs are blocked.

### Alerts and persistence

Each key/lane has Armed, PreviouslyQualified, LastAlertAt, LastAlertMetric, LastQualifiedAt and LastUnqualifiedAt state using TimeProvider. Alerts require a positive edge and profit meeting their independent alert thresholds, an eligible lane, and an unqualified-to-qualified transition. After a crossing, a trusted edge strictly below `threshold - hysteresis` rearms it. Invalid inputs do not rearm. A zero edge threshold requires zero hysteresis; a nonpositive trusted edge rearms but never qualifies. A cooldown-suppressed crossing is consumed; continued qualification after cooldown does not spontaneously alert. It must drop, rearm and cross again. A disabled lane does not consume a crossing. The sole typed severity is Opportunity.

Default fee alerts are enabled; gross alerts are disabled and require explicit opt-in. Gross alert reasons include `GROSS / FEES UNRESOLVED`. Modeled fee estimates are never labeled net or confirmed profit. Alert events retain trigger-time economics, versions, trust, revision, fee fingerprints, quality and reason. Paired segments and full liquidity arrays are excluded. Current-key inspection is a separate HTTP validation; history never claims current eligibility.

Migration `20260923064859_OpportunityMonitoring` adds only MonitoringProfiles and MonitoringAlerts, with workspace foreign keys and a workspace/time/id alert index. Writes and reads prune alert history transactionally: default and maximum 5,000 rows / 30 days per workspace, oldest first, incremental deletion batches. Audit records are never pruned by this feature. Profile changes and lifecycle start/stop are audited with authenticated actor separate from workspace ownership. No reevaluation-tick table is added.

### CSV and desktop delivery

Optional output uses the fixed protected data-directory child `monitoring/<workspace GUID N>/`. Clients cannot choose an arbitrary output path. `current-opportunities.csv` contains at most 100 rows, written at most once per configured interval (30 seconds default, minimum 5), through a flushed sibling temporary file and atomic replacement. Graceful stop writes an empty snapshot. Snapshot timestamps indicate when inputs were evaluated; a file is a diagnostic snapshot, not a live execution feed. `alerts.csv` appends only persisted transitions and rotates at 5,000,000 bytes, retaining the active file and four archives. CSV errors surface a bounded code and do not stop evaluation.

Output is UTF-8, RFC-quoted, invariant decimal/UTC formatting, with stable columns. Text beginning with spreadsheet operators (including after whitespace) or tab/newline prefixes receives an apostrophe. Numeric-looking native IDs also receive a text prefix so spreadsheet imports do not round them; their digit sequences remain intact. Titles are truncated to 256 characters. Credentials, tokens and PEM material never enter these rows. Existing/dangling links and reparse-point parents are rejected before file operations, using the shared protected-storage checks. Runtime paths remain outside Git; export filenames are also ignored defensively.

The existing bounded SignalR dispatcher emits small `MonitoringChanged` workspace notifications, never full books. Desktop consumes these only in its authorized instance/workspace generation. The monitoring tab coalesces refetches at 500 ms while visible, with a five-second missed-event fallback; HTTP remains authoritative. Leaving the tab cancels reads without stopping the backend. Navigation, access loss, changed filters and newer generations prevent late responses from restoring rows. Mutations retain the existing no-401-replay behavior.

Authenticated routes under `/api/v1/workspaces/{workspaceId}/monitoring`: GET status/profile/rankings/alerts/current/{key}, PUT profile, POST start/stop. Every route checks membership; profile/start/stop require Owner. Rankings accept exact lane/strategy/exchange/trust/quality/feeStatus filters and validated sort; rankings/alerts page size is 1–100. The WPF tab exposes controls, profile settings, filters, separate lanes, coverage, near-edge fields and historical/current inspection alongside the preserved explicit evaluation tab. No execution actions are present.
