# Read-only pre-fee opportunities (Phase 03B)

`Arbitrage.Strategies` implements pure deterministic planning and paired-depth evaluation. It references Application and Domain, with no HTTP, SQLite, exchange clients or WPF. Backend composes the approved relationship provider, local catalog instrument resolver, existing canonical book cache and evaluator. Desktop references Contracts and reads business data over authenticated HTTP. No new database migration or opportunity tick table is introduced.

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

Statuses distinguish Detected, NoGrossEdge, InsufficientLiquidity, RelationshipIneligible, BookUnavailable, BookStale, BookInvalid, BookContinuityInsufficient, BookSkewTooLarge, BooksChangedDuringEvaluation, UnsupportedStrategy, LiquidityConflict, RequiresInventory, StaleInput and ArithmeticOverflow. Separate facts include RelationshipEligible, BooksActionable, GrossArbitrageExists and FullyExecutableForRequestedQuantity. FeeStatus is always NotEvaluated; NetProfit/NetEdge are null and ExecutionEligible is false.

Fees/rebates, balances, usable capital, allocation, inventory, simulated fills and order submission are absent. Leg ordering, IOC/FOK, latency, partial fills, rollback/hedging, settlement lockup and transfers remain execution-layer concerns. No exchange account is required to evaluate cached books. There is no continuous scanner, opportunity-driven subscription or Phase 03C fee implementation.
