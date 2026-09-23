# Phase 03D verification — continuous read-only monitoring

Date: 2026-09-23. Implementation details and limits are in [ReadOnlyOpportunities](../Architecture/ReadOnlyOpportunities.md#phase-03d-continuous-local-monitoring).

## A–D. Repository and changed scope

- Repository root: `C:\Users\Yovko\source\repos\ArbTrading`.
- Starting branch: `main`, ordinary existing checkout, not detached. Starting working tree was clean.
- Starting and retained HEAD: `87e0da03fbb1eba73d22df1e03d94ad288d83295`, the supplied published Phase 03C baseline.
- Existing `origin` was inspected without displaying a credential-bearing URL. No repository, branch, remote, history or Git metadata was created or replaced.
- GitHub CI: unavailable from this session. `gh` was unavailable and the read-only public Actions lookup could not be retrieved. No CI-success claim is made.
- Source changes: Application monitoring profiles, lanes, ranking, alert state, invalidation feed and dependency index; gross evaluator near-edge observations; cache publication hooks; backend coordinator/API/CSV/SignalR; Contracts DTOs; Infrastructure profile/alert store, notifications and additive migration; Desktop monitoring tab, HTTP methods and active-tab invalidation handling; shared link rejection; tests and documentation. Existing explicit opportunity evaluation remains available.
- New migration: `20260923064859_OpportunityMonitoring`, plus its designer and model snapshot. Earlier migrations remain unchanged. `.gitignore` extends the existing rules with local export filenames. The tracked-sensitive-file name check found no credential/database/key files. Normal user storage was not migrated or inspected.

## E–H. Lifecycle and bounded scheduling

Start/Stop are explicit, authenticated owner operations. One backend-owned workspace runs at a time. Desktop closure/navigation does not stop it. Startup is Stopped; there is no persisted auto-resume switch. Stop cancels current evaluation, clears current results, records lifecycle audit and settles. Faulted and Degraded remain visible, with bounded error codes.

Instrument, market and relationship indexes map local changes to stable keys. A 2,048-entry identifier feed coalesces duplicate notices. The 200 ms shared worker uses a 1,024-key dirty set, at most 32 evaluations per pass, and at most 2,000 selected plans/current results. Overflow requests reconciliation rather than silently losing correctness; the bounded remaining-work queue drains across passes. No per-relationship timers or per-tick tasks are created.

The one-second local sweep catches approval/policy/fingerprint changes, missed notifications, clock-driven book aging, fee schedule effective boundaries and profile changes. It compares local stamps and queues affected plans; it does not rerun unchanged strategy calculations or fetch exchange inputs. Generation/profile stamps suppress superseded results. Current reads revalidate through the existing coordinator, including a final book check after asynchronous fee/profile reads.

## I–L. Economics, near-edge and alerts

FeeAdjusted, GrossOnly, NearEdge and Blocked remain separate lanes. Default economic tuples use profit, edge, available quantity, explicit quality order, skew and ordinal stable key. NearEdge uses distance, quantity, skew and key. Alternate sorts remain within lanes; unknown fee values follow known values. There is no weighted score.

The existing gross engine exposes `1 - best ask A - best ask B` only after its proof, freshness, continuity, skew and shared-liquidity guards. Near-edge distance is threshold minus observed edge within the configured window. Known fee-adjusted baskets that miss their fee threshold use the fee metric; unevaluated fee baskets remain gross-based. Near-edge is not labeled current arbitrage.

Default alerts require known fee-adjusted economics. Gross-only alerts are opt-in and labeled `GROSS / FEES UNRESOLVED`. Kalshi public official-source conflicts continue to leave totals unresolved and cannot trigger default fee alerts. Net profit remains unknown and execution eligibility false.

TimeProvider fixtures verify the exact sequence: 0.0049 no alert; 0.0050 alert; 0.0060 no repeat; 0.0045 no rearm; 0.0039 rearm; 0.0051 within 60 seconds suppressed; after cooldown, a new drop/rearm/cross alerts. A continuously qualifying key alerts once. Invalid input does not rearm. Disabled lanes do not consume crossings. Zero threshold requires zero hysteresis and still requires positive economics. The sole typed severity is Opportunity. Alert rearm/cooldown state is local to the explicitly started session.

## M–O. Persistence, CSV and coverage

Only profiles and compact alert events persist; no reevaluation-tick, book or paired-depth history table is added. Defaults/maxima are 5,000 alerts and 30 days per workspace. Pruning is transactional and never deletes audit records. Start/stop/profile changes record authenticated actor and workspace separately. The real SQLite upgrade fixture starts at the actual Phase 03C migration and verifies preservation of identity, memberships, audit, catalog, relationships, mappings, evidence, relationship jobs, fee schedules and fee profiles. Reopened storage preserves monitoring settings and retention behavior.

CSV is optional, fixed under the protected data directory's workspace monitoring child. Current snapshots cap at 100 rows, default to 30-second intervals, flush a sibling temporary file and atomically replace the target. Graceful stop clears current rows. Alert output appends transitions and rotates at 5,000,000 bytes across five files. UTF-8, invariant numbers/UTC, RFC quoting, formula-prefix neutralization, text-prefixed numeric IDs and title truncation are tested. A linked-directory fixture confirms no target write. Locked-target failure reports `CsvWriteUnavailable`, then recovery succeeds; it does not stop monitoring. No arbitrary client-supplied export path exists.

Coverage distinguishes locally approved relationships available/monitored/skipped, partial coverage, plans, books present/actionable, resolved fee plans and each lane. The default scope is 250 relationships, maximum 1,000, deterministic trust only unless manual is enabled. The available count is stored local approvals under that profile, not all markets or a proof of globally validated coverage outside the selected bound. The pre-existing canonical cache defaults to 128 instruments, independently configurable to 1,024.

## P–Q. Deterministic and regression evidence

- Offline structural fixture: 500 relationships, 1,000 distinct instruments and 500 stable results. A 1,000-notification same-instrument burst retains one notice, coalesces 999, and affects one result. A 100-instrument burst affects exactly 100 results; retained results remain 500.
- Overflow fixture: a deliberately reduced 128-entry feed receives 1,000 unique instrument notices, retains 128, reports 872 drops and requests reconciliation. Production-coordinator integration separately verifies overflow revisits all selected plans and ordinary notices reevaluate only the affected plan.
- Observed structural-fixture duration in one full verification run: approximately 0.475 seconds. This is diagnostic, not a machine-dependent pass threshold. The 500-plan fixture exercises pure evaluation/index/ranking bounds; the real SQLite coordinator fixtures use smaller deterministic scopes. No live-exchange throughput claim is made.
- Backend fixtures verify explicit lifecycle, default manual exclusion/opt-in labels, stale-book aging without notification, rejection/policy invalidation, fee effective boundaries, fee-profile changes, public Kalshi source conflicts, transition-only history, authorization/query bounds, and no hidden book/fee fetches or WebSocket starts.
- Barrier fixtures verify that Stop, backend shutdown or a book change during evaluation cannot publish an active late result or alert.
- Desktop fixtures verify inactive reads, explicit Start, no 401 mutation replay, and rejection of late reads after access loss, navigation, filters, Stop or profile save.

## R–T. Verification and UI

Final `scripts/verify.ps1`: **passed**. Restore succeeded; Release build completed with **0 warnings and 0 errors**; **487 tests passed, 0 failed, 0 skipped**:

| Project | Passed |
|---|---:|
| Domain | 55 |
| Application | 191 |
| Backend integration | 228 |
| Desktop/WPF | 13 |
| Total | 487 |

The supplied 455-test Phase 03C suite remains green, with 32 added test cases. The final full run includes the real-process fixture that failed during one earlier cleanup attempt. `git diff --check` passed. No tests or audit checks were disabled.

The required `scripts/verify.ps1` runs restore, Release build and the whole test suite with audit enabled. Tests use isolated temporary databases/runtime directories and local loopback fixtures; no normal user database or credentials are used. One repeated full run encountered an existing real-process fixture's temporary `backend.lock` cleanup failure. Its isolated rerun passed; the final whole-suite rerun is reported separately rather than concealing that attempt.

`dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` reported no model changes. `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` reported no vulnerable packages for all 13 projects using the current configured sources; NuGet audit was not disabled.

WPF XAML compiles. Automated STA fixtures exercise Light and Dark, Stopped/Running/Degraded, all four lanes, manual trust, long titles, historical alerts, unknown fees and an actual profile binding edit. Rendered PNGs are in `%TEMP%\ArbTrading-03D-ui`; representative Light/Dark images were visually inspected, and a tab-background contrast issue was fixed. Automated HTTP/view-model checks are distinct from these renders. No manual mouse/keyboard pass, high-DPI sweep, screen-reader audit or new High Contrast inspection was performed.

## U–Y. Limits and safety confirmations

This remains a bounded local diagnostic monitor, not an all-market scanner, execution system or live compatibility certification. Missing inputs remain missing until existing explicit acquisition controls are used. Fee-adjusted values are modeled estimates; Kalshi public rounding and cross-currency limitations remain. Alert events are historical, and CSV files can age between writes. Ranking reads validate the entire bounded result set before paging, so large scopes involve more SQLite work than the requested page size. Desktop updates are coalesced at 500 ms with a five-second fallback while its tab is active. Alerts/rearm state reset on a new explicit monitoring session; historical alerts persist.

No exchange accounts were required. Monitoring causes no hidden external exchange acquisition: rejecting book/fee/socket fixtures record zero calls, and the coordinator has no discovery/enrichment or exchange transport dependency. No balances, capital allocation, simulated fills, order submission or execution capability was added. Phase 04 was not started. Changes remain uncommitted on `main`; nothing was pushed or submitted as a pull request.

## Changed-file inventory

- `.gitignore`
- `docs/Architecture/ReadOnlyOpportunities.md`
- `docs/Architecture/Roadmap.md`
- `docs/Development/Phase03DVerification.md`
- `README.md`
- `src/Arbitrage.Application/Monitoring.cs`
- `src/Arbitrage.Application/Opportunities.cs`
- `src/Arbitrage.Application/OrderBooks.cs`
- `src/Arbitrage.Backend/MonitoringCoordinator.cs`
- `src/Arbitrage.Backend/MonitoringCsv.cs`
- `src/Arbitrage.Backend/MonitoringEndpoints.cs`
- `src/Arbitrage.Backend/OpportunityCoordinator.cs`
- `src/Arbitrage.Backend/Program.cs`
- `src/Arbitrage.Backend/Realtime.cs`
- `src/Arbitrage.Contracts/Monitoring.cs`
- `src/Arbitrage.Desktop/Services/BackendClient.cs`
- `src/Arbitrage.Desktop/Services/RealtimeSession.cs`
- `src/Arbitrage.Desktop/ViewModels/MainViewModel.cs`
- `src/Arbitrage.Desktop/ViewModels/MonitoringViewModel.cs`
- `src/Arbitrage.Desktop/ViewModels/OpportunitiesViewModel.cs`
- `src/Arbitrage.Desktop/Views/MonitoringView.xaml`
- `src/Arbitrage.Desktop/Views/MonitoringView.xaml.cs`
- `src/Arbitrage.Desktop/Views/OpportunitiesView.xaml`
- `src/Arbitrage.Infrastructure/Migrations/20260923064859_OpportunityMonitoring.cs`
- `src/Arbitrage.Infrastructure/Migrations/20260923064859_OpportunityMonitoring.Designer.cs`
- `src/Arbitrage.Infrastructure/Migrations/TradingDbContextModelSnapshot.cs`
- `src/Arbitrage.Infrastructure/MonitoringStore.cs`
- `src/Arbitrage.Infrastructure/TradingDbContext.cs`
- `src/Arbitrage.Strategies/GrossOpportunityEvaluator.cs`
- `src/Shared/LocalConnectionFile.cs`
- `tests/Arbitrage.Application.Tests/MonitoringTests.cs`
- `tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj`
- `tests/Arbitrage.Backend.IntegrationTests/MonitoringApiTests.cs`
- `tests/Arbitrage.Backend.IntegrationTests/MonitoringDesktopTests.cs`
- `tests/Arbitrage.Backend.IntegrationTests/MonitoringStorageTests.cs`
- `tests/Arbitrage.Desktop.Tests/OpportunityWpfTests.cs`
