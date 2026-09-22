# Phase 03B verification and completion report

## A. Starting checkout

Repository root: `C:\Users\Yovko\source\repos\ArbTrading`. Selected branch: `main`, ordinary existing checkout, not detached. Starting and final HEAD: `f5ffe7c280bb7d49f5b56016dbbbc50390b58653` (published Phase 03A). Starting working tree was clean. Existing remote name `origin` was inspected without printing its URL. No replacement repository, nested solution or worktree was created.

## B. Baseline preservation

Read root AGENTS.md and the Phase 03A, 02B.1 and 02B.2 verification records. Extended the existing approved relationship provider, book cache, canonical normalizer/provenance and executable-depth engine. Domain remains independent; EF/SQLite remain in Infrastructure; Desktop only consumes business DTOs over HTTP. Existing migrations, authentication, execution availability and unrelated source files remain intact. The 350 baseline tests are included in the full regression run. No normal user database or credential store was touched.

## C. Changed files

32 source/documentation files, including 17 additions:

- Domain: `src/Arbitrage.Domain/OrderBooks.cs` adds native resting-liquidity identity.
- Application: `OrderBooks.cs` adds coherent cache reads/version checks; `RelationshipCandidates.cs` adds bounded approved pages and proof stamps; new `Opportunities.cs` defines immutable results/settings/typed states.
- Strategies: new `OpportunityPlanner.cs` and `GrossOpportunityEvaluator.cs` implement proof-directed planning and exact paired depth.
- Infrastructure: `RelationshipStore.cs` implements detached bounded approval reads and current semantic/policy/revision validation.
- Backend: new `OpportunityCoordinator.cs`, `OpportunityJobs.cs`, `OpportunityEndpoints.cs`; composition/reference changes in `Program.cs` and `Arbitrage.Backend.csproj`.
- Contracts: new `src/Arbitrage.Contracts/Opportunities.cs` for evaluation requests, run/page responses and detailed results.
- Desktop: new `OpportunitiesViewModel.cs`, `OpportunitiesView.xaml`, `OpportunitiesView.xaml.cs`; integration in `App.xaml.cs`, `BackendClient.cs`, `ShellViewModel.cs`, `MainWindow.xaml`; copyable relationship ID in `RelationshipsView.xaml`.
- Tests: new `OpportunityTests.cs`, `OpportunityApiTests.cs`, `OpportunityJobTests.cs`, `OpportunityDesktopTests.cs`, `OpportunityWpfTests.cs`; project references/linked ViewModel in Application.Tests and Backend.IntegrationTests project files.
- Documentation: README, architecture Roadmap, new [ReadOnlyOpportunities.md](../Architecture/ReadOnlyOpportunities.md), this report.

Existing ignore rules already cover build output, TestResults, runtime databases/sidecars, logs, local configuration and credentials, so no wholesale or unnecessary .gitignore edit was made. A tracked-filename check found no runtime databases, private keys, protected credential blobs or local connection configuration. New source and documentation remain eligible for version control.

## D–F. Strategies, relationship trust and book eligibility

CrossMarketBuyBothComplements and SingleMarketBinaryComplement require approved payout proof and catalog-addressable native instruments. CrossMarketSameOutcomeSpread is represented but blocked as RequiresInventory; BUY/SELL price differences do not imply a locked payout. Same-proposition and inverse-proposition fixtures use actual economic mappings and the existing binary-complement validator. Conflicting same-outcome and partition facts fail closed. Unsupported larger sets are not evaluated as two-outcome guarantees.

Only VerifiedDeterministic is scanned by default. Manual trust is explicit opt-in and tagged Manual. The real SQLite provider excludes Proposed, NeedsReview, Rejected, Stale and mismatched policy/fingerprints. Approval revision and fingerprints are checked after calculation and before serving retained results. No eligibility test substitutes a permissive provider stub.

Canonical BookEligibility is reused: Fresh REST is diagnostic, Kalshi realtime requires current connected anchored Continuous state, and Polymarket BestEffort remains labeled diagnostic. Stale, invalid, absent, unanchored, gapped and resynchronizing inputs cannot produce Detected. Current Kalshi binary support and Polymarket native token mappings are resolved from local catalog only.

## G–I. Depth, provenance and consistency

The paired ask walk consumes the smaller remaining quantity at each pair of levels. It stops at exhaustion, a diagnostic bound, zero/negative marginal edge or edge below the minimum. Positive equality qualifies. Each segment retains exact quantity/prices/edge/cumulative cost/payout/profit. The independent canonical depth estimates must match the paired total exactly. Decimal overflow and arithmetic disagreement fail closed; no floating tolerance is used.

Native source identity covers exchange, market, native instrument, consuming action and native price. A Kalshi derived YES ask and its native NO bid share identity and cannot count as independent liquidity, including partial use. Different native levels and independent exchange books remain distinct. Derived liquidity is allowed and shown when it is independent. Conflicts are conservatively rejected rather than allocated.

Both cached snapshots are captured under one cache lock, with immutable references and versions. Cheap post-calculation validation yields BooksChangedDuringEvaluation on version change; retained reads yield StaleInput, BookStale or RelationshipIneligible as appropriate. Local observation skew defaults to 1,000 ms and is configurable. This is a diagnostic coherence bound, never an atomic exchange-read claim.

## J–K. Typed outcomes and pre-fee boundary

Statuses distinguish Detected, NoGrossEdge, InsufficientLiquidity, RelationshipIneligible, BookUnavailable, BookStale, BookInvalid, BookContinuityInsufficient, BookSkewTooLarge, BooksChangedDuringEvaluation, UnsupportedStrategy, LiquidityConflict, RequiresInventory, StaleInput and ArithmeticOverflow.

RelationshipEligible, BooksActionable, GrossArbitrageExists and FullyExecutableForRequestedQuantity are separate facts. FeeStatus is NotEvaluated; NetProfit and NetEdge remain null; ExecutionEligible is always false. Only currently validated positive Detected results enter the primary list. Blocked diagnostics may preserve historical calculations but are explicitly labeled noncurrent.

## L. Exact deterministic results

| Fixture | Paired quantity | Gross cost | Guaranteed payout | Pre-fee gross profit |
| --- | ---: | ---: | ---: | ---: |
| A: 10@.40 + 20@.43 against 5@.50 + 20@.52 | 25 | 23.35 | 25 | 1.65 |
| B: 100@.49 against 10@.48 + 100@.52 | 10 | 9.70 | 10 | .30 |
| C: 7@.40 against 100@.50 | 7 | 6.30 | 7 | .70 |
| D: combined .9995, minimum .001 | 0 | 0 | 0 | no candidate |
| E: combined .999, minimum .001 | 1 | .999 | 1 | .001 |

Case A segments are 5@.90, 5@.92, 15@.95, with cumulative costs 4.50, 9.10, 23.35. Tests also cover zero edge, empty/one-sided books, explicit requested quantity, 0.000000001 quantity, billion-share caps against decimal.MaxValue depth, notional caps and upward decimal division, zero-cost null return, invalid bounds, stable keys, mixed native origins and immutable cache captures.

## M. Job/API behavior

Jobs are explicit, authenticated and workspace-scoped. Start/cancel require Owner; actor and workspace are separate. One globally active job; duplicates receive 409. Accepted jobs outlive request disconnect. Runtime, relationship, result and retained-depth limits report Partial. Cancel and backend shutdown settle as Cancelled. Four runs are retained, at most fifty results and 100,000 segments per run; restart clears them. No high-frequency persistence or migration was added.

Selected-relationship and bounded-scan POSTs, status GET, validated result paging, cancel POST and current-key GET are implemented. Missing inputs remain blockers. Tests verify native provenance, manual exclusion/opt-in, retained and mid-evaluation invalidation, unauthorized requests, membership removal, cross-workspace isolation, paging, bound validation, duplicate admission, cancellation, runtime Partial, shutdown and eviction. An exchange-source spy records zero evaluation-triggered fetches.

## N. Actual build/test verification

- Focused pure evaluator/planner tests: **36 passed**.
- Focused API/job/desktop lifecycle tests: **24 passed**.
- Focused WPF fixture test: **1 passed**, covering six result states in both themes.
- Explicit `dotnet restore ArbitrageTrading.sln`: passed with NuGet auditing enabled.
- Explicit Release build: passed, **0 warnings, 0 errors**.
- Initial complete `dotnet test ... --no-build --no-restore`: **410 passed**, before the final contradictory-partition regression was added.
- Final `scripts/verify.ps1`: **passed: 411 tests, 0 failed, 0 skipped; Release build 0 warnings/0 errors**. Totals: Domain 40, Application 172, Backend.Integration 188, Desktop 11. This includes all 350 baseline tests and 61 new tests.
- `git diff --check`: passed.

Default tests use isolated temporary directories and require no internet or exchange account. Integration tests use migration-backed real SQLite and production local credential transport with temporary local credentials, never real exchange secrets. Windows protected-file/ACL and process tests were run with the required tool permissions. TRX files are generated under ignored project TestResults directories.

## O. Dependency audit

`dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` passed and reported **no vulnerable packages** for all thirteen projects against the configured NuGet sources. Auditing remained enabled.

## P. UI verification actually performed

XAML compiled successfully. Automated tests verify no automatic evaluation on navigation/local refresh/catalog/book notifications, explicit selected/verified/cancel commands, stale response and late admission rejection, authorization failure clearing, trust/details, pre-fee wording and native/derived markers. Existing Light/Dark resource tests remain included.

Rendered Opportunities fixtures at 1180×1000, 96 DPI in both Light and Dark: Detected, NoGrossEdge, BookStale, BookSkewTooLarge, RelationshipIneligible and LiquidityConflict. Fixtures include Manual trust, DerivedComplement, BestEffort, long relationship titles, explicit mappings and prominent pre-fee warnings. Images are in ignored `TestResults/Phase03B/opportunities-*.png`. Visual inspection checked readable contrast, wrapped detail/title text and the diagnostics boundary. Narrow grid columns use horizontal scrolling; full strategy/status values are available in details.

Mouse interaction was **not** performed. Multi-monitor/scaled-DPI testing was **not** performed. A full keyboard/screen-reader/accessibility audit was **not** performed; compiled controls and automation names do not establish accessibility compliance. Fixture renders do not establish live exchange behavior or account integration.

## Q–T. Limitations and explicit confirmations

- Results are point-in-time diagnostics, not execution promises. Book/catalog notifications clear visible results; an active-page read loop catches freshness and external relationship changes on its next one-second validation cycle. It never recalculates automatically. Source changes after a validation instant remain possible.
- Ordinary catalog semantic evidence often does not meet deterministic proof requirements. Manual approval remains a separate explicit trust choice. Nothing in this phase certifies a live venue pair from similar titles.
- Shared-level conflicts are conservatively rejected, even if partial allocation could be possible in a future model. No baskets beyond two outcomes or inventory-dependent strategy is enabled.
- Jobs/results are ephemeral; selection uses a copied relationship GUID. Wide grids/details scroll. No continuous scanner or automatic subscription management exists.
- No exchange credentials/accounts were required for implementation or default tests. Evaluation reads only already cached books and local catalog/approval data.
- No fees, balances, capital allocation, paper fills, orders or other execution mechanics were added. No Phase 03C work was started.
- All Phase 03B changes remain **uncommitted** on `main`. No commit, push, merge, PR, reset, clean, stash, branch switch, history rewrite or remote change was performed.
