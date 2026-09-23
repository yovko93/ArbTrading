# Phase 03C — fee diagnostics and verification

## A. Baseline

Implementation date: 2026-09-23 (local). Root: `C:\Users\Yovko\source\repos\ArbTrading`. Selected branch `main`, starting HEAD `6376bb5c4a1597413b096acb0f78689bb40bef09`, clean working tree, existing `origin`. No repository/remote/branch change, commit, push or PR. Root AGENTS.md and Phase 03A/03B verification and opportunity architecture were reviewed. The previous baseline reported 411 tests.

## B. Official sources reviewed before implementation

All technical fee rules below come from official sources reviewed on 2026-09-23, not category heuristics or account data:

- [Polymarket index](https://docs.polymarket.com/llms.txt), [prediction fees](https://docs.polymarket.com/trading/fees), [market details](https://docs.polymarket.com/market-data/market-details), [Gamma market-by-ID API](https://docs.polymarket.com/api-reference/markets/get-market-by-id), [collateral explanation](https://docs.polymarket.com/concepts/pusd).
- [Kalshi index](https://docs.kalshi.com/llms.txt), [fee rounding](https://docs.kalshi.com/getting_started/fee_rounding), [series](https://docs.kalshi.com/api-reference/market/get-series), [event](https://docs.kalshi.com/api-reference/events/get-event), [series fee changes](https://docs.kalshi.com/api-reference/exchange/get-series-fee-changes), [event fee changes](https://docs.kalshi.com/api-reference/events/get-event-fee-changes), [OpenAPI YAML](https://docs.kalshi.com/openapi.yaml), [regulatory fee schedule PDF](https://kalshi.com/docs/kalshi-fee-schedule.pdf), [fee schedule landing page](https://kalshi.com/fee-schedule).

The Kalshi PDF served during implementation is effective July 7, 2026. Its general taker formula is multiplier × 0.07 × contracts × price × (1−price); maker formula uses 0.0175. Multiplier is an absolute multiplicative factor. The schema maps quadratic types to this schedule, including maker variants; `flat` refers to a specific table absent from the current PDF. The combo maker schema description also does not safely establish agreement with the PDF. Only quadratic and quadratic_with_maker_fees formulas are implemented. Flat, combo-maker and future types remain unsupported.

**Discrepancy:** PDF page 1 describes fee-plus-position-cost centicent alignment, while its general examples round to whole cents. The API rounding guide separately describes six-decimal trade fees and profile-dependent balance alignment/rebates. Public Kalshi acquisition records this unresolved disagreement in VerificationIssue; model components remain visible, but TotalFee is null even if a diagnostic profile is selected. There is no production switch that dismisses the conflict. Fixture schedules can explicitly establish the API-guide assumption for numerical tests. A later verified contract update is needed to enable affected public totals.

Polymarket's current market-specific `feesEnabled` and `feeSchedule` object are verified in the Gamma API and market-details page. The raw CLOB YAML fetch was blocked locally by TLS certificate validation; it was not bypassed. No legacy fee-rate/basis-point field or unverified token endpoint is used. The documented Gamma contract is sufficient for the supported metadata path. The collateral page calls pUSD a USDC claim and says settlement uses native USDC; quotes keep the fee page's USDC denomination, with no silent cross-exchange USD conversion.

## C. Changed components

Domain fee types/resolver/calculators; Application fee composition/store/source contracts; dedicated public connector; Infrastructure fee store and additive VerifiedFeeSchedules migration; backend fee API/jobs/counters and opportunity integration; Contracts DTOs; WPF Opportunities and diagnostic profile Settings; focused Domain/Application/API/job/migration/Desktop tests; README and opportunity architecture. Existing gross evaluator and planner are unchanged. No quote ticks or new secrets are persisted.

## D–E. Polymarket

`C × rate × p × (1−p)` uses each consumed native level, not VWAP. Gamma ID and outcome-token IDs must match. Supported exponent is 1, takerOnly must be true; unsupported parameters fail closed. Explicit feesEnabled=false establishes a fee-free market unless metadata contradicts it. Missing rate never becomes zero. Maker trading fee is zero under the verified prediction-market contract.

Official precision is five decimal places, minimum charged fee 0.00001 USDC; smaller model fees are zero. The source does not specify the rounding tie/direction mode. A five-decimal ceiling is labeled ConservativeEstimate at hypothetical-fill level when it differs from the model. Exact-grid and documented subminimum cases are known for that hypothetical fill. No unspecified rounding mode is presented as verified exact. Program rebates/rewards/referrals are NotIncluded and never subtracted.

## F–H. Kalshi hierarchy, formulas and rounding

Series current baseline → effective scheduled series changes → current/effective event override; explicit null clear falls back to the then-current series. Retrieval time does not replace effective time. Both override fields must be explicit; partial or ambiguous override metadata fails closed. Conflicting same-time rules and changes crossing an acquisition window fail closed. Bounded future history is retained.

The pure rounding helper exactly implements the API guide: ceil model fee to 0.000001; floor signed revenue minus trade fee to the account unit; add the resulting rounding fee to the order accumulator; rebate complete account units capped by the fill's nonnegative fee. Unit is 0.0001 for DirectMember and 0.01 for NonDirectMember. Unknown retains model/trade values but leaves dependent components and total null. One accumulator per hypothetical taker leg, across native price-level fills; derived asks do not create another charge. This is fee modeling, not completed-fill simulation.

Official example: model 0.00363825 and buyer revenue −0.055 gives trade 0.003639; NonDirect rounding 0.001361 and total 0.005. Direct rounding is 0.000061 and total 0.0037. Accumulator tests include whole-unit rebates and a fill whose fee is too small to rebate the accumulated unit.

## I–J. Persistence, freshness and result semantics

One bundle per catalog market, at most 512 rules and 100 instruments, exact decimal JSON, source/identifiers/effective/retrieval/source-update timestamps, formula version and semantic SHA-256. Schedule bundles cascade with catalog deletion. A separate local-workspace diagnostic profile persists Unknown/DirectMember/NonDirectMember and a revision GUID. Successful refresh and profile-save audit records identify actor separately from workspace. No account identity is inferred.

Freshness is one hour, distinct from effective boundaries. Retained results validate current fee fingerprints, active rules and profile revision in addition to all unchanged gross guards. Fee invalidation clears adjusted totals while preserving gross values. API EvaluateFees defaults false for compatibility; WPF defaults true. Fee primary results require completed positive FeeAdjustedDetected; diagnostics retain gross candidates. Exact decimal per-level math is still **Estimated** at L2 opportunity level because actual fill fragmentation is unknown. Currency mismatch blocks aggregation. NetProfit and NetEdge remain null.

MinimumFeeAdjustedEdgePerShare is optional, separate from MinimumGrossEdge, defaults zero, range [0,1). Positive equality qualifies; zero profit never qualifies. Other trading/transfer/bridge/gas costs remain excluded.

## K–L. Deterministic numerical fixtures

Case A preserves quantity 25, cost 23.35, payout 25 and gross profit 1.65. Kalshi consumes 10 at 0.40 and 15 at 0.43; Polymarket consumes 5 at 0.50 and 20 at 0.52. Fixtures explicitly assume a common USD unit, API-guide Kalshi rounding, NonDirectMember, multiplier 1 and Polymarket rate 0.04.

| Value | Exact decimal |
| --- | ---: |
| Kalshi modeled leg fee | 0.43 |
| Polymarket modeled leg fee | 0.24968 |
| Total | 0.67968 |
| Fee-adjusted cost | 24.02968 |
| Fee-adjusted profit | 0.97032 |
| Fee-adjusted edge | 0.0388128 |
| Return on cost | 0.97032 / 24.02968 |

Changing the Kalshi fixture multiplier to 4 gives its leg fee 1.71, total 1.95968 and adjusted profit −0.30968, despite positive gross profit 1.65. This retains a capped rounding overpayment; it is not obtained by applying a formula to VWAP. The paired walk has three segments but each leg has only two fee-assessment levels. Tests also cover symmetry, fractions, zero/maker/subminimum fees, invalid/missing models, profile precision, future overrides/clears, conflicts, no assumed incentive rebate and stale/currency-mismatched results.

## M. API/jobs

Authenticated workspace routes provide cached metadata/profile reads, owner profile writes and explicit selected-market refresh jobs. At most ten markets, four event pages per market, thirty seconds, four retained jobs and one active fee refresh globally. Duplicate admission is 409. Request disconnect does not cancel admitted work. User cancellation and shutdown settle Cancelled; runtime exhaustion settles Partial. Per-source 25-second timeout, 1 MB response, bounded retries and Retry-After handling; no redirects or credentials. No network request is issued by fee evaluation or header/navigation refresh.

## N–Q. Verification record

Performed on 2026-09-23, using isolated test runtime directories and real migration-backed SQLite:

| Check | Actual result |
| --- | --- |
| `dotnet restore ArbitrageTrading.sln` | Passed; NuGet audit enabled |
| Release build, `--no-restore` | Passed; 0 warnings, 0 errors |
| Full solution tests, `--no-build --no-restore` | 455 passed, 0 failed, 0 skipped |
| Domain | 55 passed |
| Application/connectors | 185 passed |
| Backend/integration/desktop viewmodels | 203 passed |
| WPF | 12 passed |
| `scripts/verify.ps1` | Passed: restore, Release build, all 455 tests |
| `git diff --check` | Passed |
| EF pending-model check | No changes since the new migration |
| NuGet vulnerable packages, including transitive | None reported across all solution projects |

All 411 existing tests remain green; 44 tests were added. Focused fee tests ran before the full suite. Exact model tests, offline public HTTP fixtures, authentication/membership, independent refresh jobs, cancellation/budget/shutdown/disconnect, profile round-trip invalidation, scheduled-boundary invalidation, no hidden network, SQLite upgrade/restart, and delayed desktop response guards passed. A numerical fixture expectation was corrected after independently checking the rebate cap; test compiler/analyzer issues were corrected before the successful runs.

WPF rendered eight new combinations: unknown, removed edge, stale result and required profile, each in Light and Dark. Images are in ignored `TestResults/Phase03C/fees-*.png`; representative Dark unknown and Light negative renders were visually inspected. They show Unknown instead of zero, preserve gross amounts, show fee source/age/profile, and expose explicit refresh controls. The full WPF suite also covers existing gross/provenance states. This was isolated fixture rendering, not a live exchange or real-user desktop session. No new live Settings interaction was performed; profile behavior was exercised through viewmodel/API tests.

The optional live public market sample was not run. The official Kalshi OpenAPI was successfully read without credentials; the local Polymarket raw YAML read failed normal certificate validation, and no bypass was attempted. Repository hygiene retained the existing ignore rules; no tracked database/key/secret file matched the filename checks. The final code was reverified after the no-gross label and failed-refresh membership recheck were tightened.

## R–U. Boundaries

Unresolved Kalshi source conflicts intentionally prevent public fee-adjusted totals. Flat/combo/unknown formulas, market waiver/combo semantics and ambiguous metadata fail closed. Polymarket local live access remains constrained by normal TLS/network behavior. L2 fragmentation is unknown; no exact execution fee or all-cost net profit is claimed. USD/USDC conversion is not assumed. The optional live market sample was not run; official documentation reads used no credentials. Default tests are offline with isolated runtime storage. No exchange accounts, balances, positions, capital allocation, completed-fill simulation, orders or Phase 03D monitoring were added. Changes remain uncommitted.
