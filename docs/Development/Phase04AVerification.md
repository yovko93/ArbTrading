# Phase 04A verification — paper execution and persistent accounting

Date: 2026-09-23. Architecture, API contracts and operating limits: [PaperExecution](../Architecture/PaperExecution.md).

## A–D. Repository and changed scope

- Root: `C:\Users\Yovko\source\repos\ArbTrading`.
- Starting branch: `main`, ordinary checkout, not detached. Starting working tree was clean.
- Starting and retained HEAD: `52a4065f6467f848448b23101483b29346a70660`, the supplied published Phase 03D baseline.
- Existing origin was inspected without printing credential-bearing URLs. No repository, branch, remote configuration or Git history was replaced. No commit, push or PR was made.
- GitHub combined-status lookup for this exact baseline returned an empty statuses array. This is not evidence of a green CI run; no CI-success claim is made.
- Source changes activate Execution's pure paper planner/accounting; add Infrastructure journal/materializations/reconciliation and additive migration; add Backend proof orchestration, authenticated APIs, bounded previews/counters and invalidations; add Contracts; wire the Desktop paper view, explicit opportunity actions and confirmation. Supporting changes add a cache commit guard, nonmutating relationship validation, current-result accessors and capability reporting. Tests and current documentation are updated. Existing read-only acquisition and monitoring controls remain explicit.
- Existing ignore rules already cover build artifacts, test results, SQLite files/sidecars, logs, runtime connections and credentials. Filename inspection found no tracked runtime databases, connection files, private-key or DPAPI files. No ignore replacement was needed.

## E–H. Architecture, accounts, journal and reset

Execution references Application and transitively Domain; it contains no EF, SQLite, HTTP, WPF or credentials. Infrastructure and Backend now reference Execution. Desktop continues to reference Contracts only for business data.

Paper funding is explicit and owner-confirmed. Venue/currency balances remain separate; USD is never silently converted to USDC. No automatic funds, borrowing or margin exists. Reset requires an expected generation ID and closes it atomically while creating the next generation. Old financial records remain retained and queryable.

One SQLite transaction commits execution, both legs, native fills, reservation/debit entries, balances, position accumulations and audit. Checked decimal arithmetic prevents negative cash/overflow. Fee-inclusive cost basis and fee-exclusive weighted average entry are independently reconstructable from fills. Explicit reconciliation checks journal and materializations, marks inconsistencies Corrupt and blocks new execution without repair.

## I–L. Eligibility and proof binding

Only current retained explicit/monitoring opportunities can start a preview. Deterministically verified complementary BUY baskets only; manual trust, SELL/inventory spreads, unavailable/nonactionable books, stale/gapped/skewed or changed versions, shared native liquidity, incomplete quantity and unsupported currencies reject. Quantity is user-specified, positive and at most 1,000, with fee-adjusted profit positive and edge at least 0.001/share.

Five-second server-owned previews bind actor/workspace/generation and capture relationship policy/fingerprints/revision, book source/continuity/version/timestamps/skew and resolved fee fingerprints/profile revision/effective-rule data. Confirmation revalidates under the database writer transaction and holds the cache version guard through COMMIT. No hidden refresh, acquisition, retry or re-pricing occurs. Kalshi's unresolved public fee discrepancy remains a blocking condition; profile selection never forces disputed totals to zero.

## M–O. Idempotency, concurrency and fill model

The workspace/RequestId unique constraint plus complete-body fingerprint returns the original committed result for an identical retry, including after a real backend restart and expired/lost preview. Altered duplicates reject. Desktop never retries mutations automatically and retains the same ID after lost responses.

SQLite writer serialization and balance revision tokens prevent concurrent overspending. Barrier tests cover two successful funded requests, two competing requests with funds for only one, concurrent identical requests, reset ordering, cancellation after SaveChanges and a failing final book guard. Failed commits leave no partial execution, fills, positions or cash journal.

Snapshot Paper Fill / Immediate Taker Simulation projects the existing paired depth walk into one fill per consumed native price level and reuses per-level fee calculations. It preserves derived/native provenance without counting the same native source twice. This does not model real exchange atomicity, latency, order queues, actual fragmentation or market impact.

## P. Exact numerical fixture

Requested quantity 10. A asks: 10 @ 0.40, then 20 @ 0.43. B asks: 5 @ 0.50, then 20 @ 0.52. Consumed fills: A 10 @ 0.40; B 5 @ 0.50 and 5 @ 0.52. The known fee inputs are isolated authoritative fixtures, not a claim that production Kalshi fees have been resolved.

| Value | Exact decimal result |
| --- | ---: |
| Gross cost | 9.10 |
| Expected payout at resolution | 10 |
| Gross projected profit | 0.90 |
| A modeled fees | 0.168 |
| B modeled fees | 0.09992 |
| Total modeled fees | 0.26792 |
| Fee-adjusted cost | 9.36792 |
| Expected fee-adjusted profit at resolution | 0.63208 |
| A remaining cash from 100 | 95.832 |
| B remaining cash from 100 | 94.80008 |
| A quantity / cost basis / average entry | 10 / 4.168 / 0.40 |
| B quantity / cost basis / average entry | 10 / 5.19992 / 0.51 |

Projected payout is never credited as cash.

## Q–R. Persistence and authenticated API verification

Migration `20260923092015_PaperExecution` adds paper tables/indexes/foreign keys without replacing Phase 03D tables. A real SQLite migration fixture preserves users, workspace, local profile/membership, catalog, relationship, fees/profile, monitoring profile/alert and audit, and starts with no paper generation/funds. Tests reopen the same SQLite file and restart the actual backend against retained isolated storage; fills, positions, proofs, request identity and reconciliation survive without replay.

API tests use production Bearer authentication and explicit membership. They cover missing authentication, cross-workspace denial, malformed/untrusted fields, unknown keys, non-Paper mode, explicit confirmation, invalid quantities, insufficient funds, manual trust, disputed fees, currency mismatch, book/relationship/fee changes, preview expiry, generation reset, corruption and duplicate requests. Network spies assert zero market discovery, metadata enrichment, book acquisition, fee acquisition and WebSocket creation during paper operations and reads. Removing required local identity storage fails closed with 503 before authorization.

## S. WPF checks actually performed

XAML compiled. Automated view-model tests check late previews after selection/quantity/navigation/access changes, explicit confirmation, retained IDs after lost replies, mutation single-attempt behavior on 401 and cleared private state. Trading/Portfolio are wired to the same paper view; explicit and monitoring current-row buttons route into it.

Automated WPF fixtures render seven states in both Light and Dark at 1440×1400 / 96 DPI: Uninitialized, InitializedEmpty, EligiblePreview, InsufficientPaperFunds, OpenPositionsHistory, FeeModelUnresolved, MarketDataChanged. Captures are stored outside the repository in the task visualization directory. Visual inspection covered representative preview/history screens in both themes; table headings were corrected to explicit readable columns. Fixture rendering and assertions are distinct from human mouse testing.

No manual mouse workflow, physical monitor/DPI matrix or screen-reader audit was performed. The view uses keyboard-focusable controls, descriptive names for inputs/grids and a live status label, but these are not a claim of a complete accessibility audit. No exchange account or normal runtime profile was used for UI checks.

## T–U. Build, tests and dependency audit

- `dotnet restore ArbitrageTrading.sln`: passed; auditing remains enabled.
- Release build: passed, zero warnings, zero errors.
- Focused pure paper tests: 13 passed. Focused backend/desktop paper filter: 31 passed (includes one existing matching test). Paper WPF render fixture: passed.
- `scripts/verify.ps1`: passed, including restore, Release build and the full suite: **531 passed, 0 failed, 0 skipped** (Domain 55, Application 204, Backend integration 258, WPF 14). This retains the 487-test baseline and adds 44 tests.
- The first standalone full run found two test-integration issues: an outdated expectation of unavailable paper capability, and an overly broad test-fixture readiness probe affecting an intentional authorization denial. Both were corrected before the green verification-script run; live capability denial assertions remain intact.
- After the full script, the paper-only WPF fixture passed again with stronger assertions for disabled rejection controls and realistic unresolved-fee/insufficient-funds render data. No production code changed after the green script run.
- `git diff --check`: passed.
- EF `has-pending-model-changes`: no changes since the last migration.
- NuGet vulnerable-package audit: no vulnerable packages reported for all 13 projects. The initial sandbox network attempt failed; the authorized audit with public-feed access succeeded. No audit setting was disabled.

## V–Z. Limits and boundaries

Paper simulation only. Production unresolved Kalshi fees and unsupported cross-currency baskets remain blocked. Preview retention is bounded to 256 tickets/five seconds; paper quantity is capped at 1,000. Account history lists 100 recent generation descriptors, with older generations addressable by ID; position responses are bounded to 1,000 and history pages to 50. Reconciliation is an explicit diagnostic, not an automatic repair or per-request full scan. Rejected attempts are counted rather than retained as financial executions. No ledger CSV export is provided.

No settlement/resolution logic, realized-profit credit, real exchange balance/position access, signing, live orders, transfers, capital allocation or Phase 04B work was added. No exchange accounts or real credentials were required. Normal user storage was not migrated or used. Changes remain uncommitted; no push or PR was made.

## Changed-file inventory

- docs/Architecture/PaperExecution.md
- docs/Architecture/Roadmap.md
- docs/Development/LocalDevelopment.md
- docs/Development/Phase04AVerification.md
- README.md
- src/Arbitrage.Application/OrderBooks.cs
- src/Arbitrage.Backend/Arbitrage.Backend.csproj
- src/Arbitrage.Backend/MonitoringCoordinator.cs
- src/Arbitrage.Backend/OpportunityCoordinator.cs
- src/Arbitrage.Backend/OpportunityEndpoints.cs
- src/Arbitrage.Backend/OpportunityJobs.cs
- src/Arbitrage.Backend/PaperCoordinator.cs
- src/Arbitrage.Backend/PaperEndpoints.cs
- src/Arbitrage.Backend/Program.cs
- src/Arbitrage.Backend/Realtime.cs
- src/Arbitrage.Contracts/ApiContracts.cs
- src/Arbitrage.Contracts/Fees.cs
- src/Arbitrage.Contracts/PaperContracts.cs
- src/Arbitrage.Desktop/App.xaml.cs
- src/Arbitrage.Desktop/Services/BackendClient.cs
- src/Arbitrage.Desktop/ViewModels/MainViewModel.cs
- src/Arbitrage.Desktop/ViewModels/MonitoringViewModel.cs
- src/Arbitrage.Desktop/ViewModels/OpportunitiesViewModel.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.cs
- src/Arbitrage.Desktop/ViewModels/ShellViewModel.cs
- src/Arbitrage.Desktop/Views/MainWindow.xaml
- src/Arbitrage.Desktop/Views/MonitoringView.xaml
- src/Arbitrage.Desktop/Views/OpportunitiesView.xaml
- src/Arbitrage.Desktop/Views/PaperTradingView.xaml
- src/Arbitrage.Desktop/Views/PaperTradingView.xaml.cs
- src/Arbitrage.Execution/PaperPlan.cs
- src/Arbitrage.Execution/README.md
- src/Arbitrage.Infrastructure/Arbitrage.Infrastructure.csproj
- src/Arbitrage.Infrastructure/Migrations/20260923092015_PaperExecution.cs
- src/Arbitrage.Infrastructure/Migrations/20260923092015_PaperExecution.Designer.cs
- src/Arbitrage.Infrastructure/Migrations/TradingDbContextModelSnapshot.cs
- src/Arbitrage.Infrastructure/PaperEntries.cs
- src/Arbitrage.Infrastructure/PaperStore.cs
- src/Arbitrage.Infrastructure/RelationshipStore.cs
- src/Arbitrage.Infrastructure/TradingDbContext.cs
- tests/Arbitrage.Application.Tests/Arbitrage.Application.Tests.csproj
- tests/Arbitrage.Application.Tests/PaperTests.cs
- tests/Arbitrage.Backend.IntegrationTests/ApiTests.cs
- tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj
- tests/Arbitrage.Backend.IntegrationTests/BackendFixture.cs
- tests/Arbitrage.Backend.IntegrationTests/DesktopClientTests.cs
- tests/Arbitrage.Backend.IntegrationTests/PaperApiTests.cs
- tests/Arbitrage.Backend.IntegrationTests/PaperConcurrencyTests.cs
- tests/Arbitrage.Backend.IntegrationTests/PaperDesktopTests.cs
- tests/Arbitrage.Backend.IntegrationTests/PaperPersistenceTests.cs
- tests/Arbitrage.Desktop.Tests/OpportunityWpfTests.cs
- tests/Arbitrage.Desktop.Tests/PaperWpfTests.cs
