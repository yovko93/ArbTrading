# Phase 04C verification — read-only executable paper valuation

Date: 2026-09-23. Runtime definitions: [PaperExecution](../Architecture/PaperExecution.md).

## A–C. Repository and observed baseline CI

Root: `C:\Users\Yovko\source\repos\ArbTrading`. Started on `main`, ordinary checkout, clean working tree, at `a4d9cd8ddb63c6d1f6e21e1addb4b2bc1556389b`, exactly the published Phase 04B baseline. Its Phase 04A.1 parent and all prior migrations/history remain preserved. Root AGENTS, prior 04A/04A.1/04B verification notes, paper architecture and existing depth/fee/cache/settlement code were inspected before extension. Remote names were inspected without printing credential-bearing URLs. No repository or branch was created, switched, reset, cleaned or stashed.

A read-only public GitHub Actions lookup for this exact HEAD observed [run 35854789800](https://github.com/yovko93/ArbTrading/actions/runs/35854789800): completed **failure**; `linux-backend` succeeded, `windows` failed its build/test step. This is the published baseline's status, not Phase 04C verification. No workflow was triggered or modified. Local sandbox initially blocked the lookup; a permitted public request completed it without credentials.

## D. Changed scope

Execution gains a pure typed valuation model. Domain's existing ExecutableDepth exposes its consumed levels so exit fees and provenance use the same walk. Application's coherent cache read expands from two to at most 1,000 instruments. Infrastructure gains a short deferred, read-only SQLite snapshot method with a strict position bound. Backend adds authenticated GET valuation/risk/single-position routes and current-input orchestration. Contracts and Desktop add current valuation, detail, coverage and per-currency summaries with delayed-response/invalidation guards. Existing fee and financial notifications invalidate current displayed marks. Tests and current documentation are extended; prior migration files and verification reports are untouched.

## E–H. Methodology, depth and source semantics

The default method is ExecutableLiquidation. For held long inventory it walks native canonical bid levels with SELL, exact decimal arithmetic and the existing executable-depth engine. It creates no sell order or completed fill. Kalshi YES/NO uses its own native bids; derived complement asks are never sale proceeds. Polymarket requires the persisted token/outcome to still map in the current local catalog. Title and array ordering are irrelevant.

Full quantity is mandatory for full-position fields. PartialDepth exposes only the executable quantity, unfilled quantity, partial gross subtotal and partial VWAP; full proceeds/P&L/equity remain null. Missing quantity is never filled at midpoint, best bid, entry price or zero. Settled inventory is NotApplicable with null current marks and preserved realized facts.

BookEligibility retains freshness/continuity policy: FreshRest, RealtimeContinuous and RealtimeBestEffort are distinct. Stale/invalid/missing/gapped/resynchronizing books cannot contribute a current mark. Results expose retrieval/source/valuation timestamps, age, version, continuity and native liquidity identities. No old persisted mark is loaded at restart.

## I–N. Fees, accounting, coverage and exposure

Current cached fee schedules are resolved at valuation time using the existing Phase 03C resolver/calculator, including pending effective transitions. Each consumed bid level is a hypothetical taker SELL fill, with original fee-model rounding/account-profile behavior. The output retains fee quote provenance and warnings. Actual fill fragmentation and market impact are unknown; exit fees are estimates. Entry fees/cost basis are never recalculated. The Kalshi verification conflict still leaves fee-adjusted values unresolved; account-profile uncertainty, stale/missing schedules and currency mismatch never become zero fees.

Gross unrealized P&L = gross proceeds − original cost basis, which already includes entry fees. Fee-adjusted unrealized P&L subtracts only the current hypothetical exit fee additionally. Original settlement realized P&L remains authoritative.

Accounting book value = cash + open cost. Full gross equity = cash + gross marks only with every open position fully marked. Fee-adjusted equity additionally requires every exit fee to resolve. Otherwise full equity/total P&L is null; a separately named known gross subtotal and full/partial/unavailable counts show coverage. Total P&L is realized plus the corresponding unrealized P&L only with complete coverage. No open positions means cash-only complete equity. Settled payout is never counted again.

Capital utilization uses open cost / starting cash; concentration uses largest position cost / total open cost, with null ratios for nonpositive denominators. Unique-market counts are transparent descriptive metrics, not probabilistic risk. Every summary is per generation/venue/currency. USD and USDC are never added or converted. Original hold-to-resolution execution projections remain separate from current liquidation estimates.

## O. Exact numerical fixtures

| Fixture | Exact result |
| --- | --- |
| 25 shares, cost 12.50; bids 10×.60 + 10×.59 + 5×.57 | Gross 14.75; VWAP .59; worst .57; gross unrealized P&L 2.25; return .18 |
| Same quantity, only first two levels | Executable 20; unfilled 5; partial gross 11.90; full gross/P&L null |
| 10 shares, cost 8; bid 10×.60 | Gross 6; gross unrealized −2 |
| 20 shares, cost 8; bid 20×.50; existing Polymarket fixture rate .05 | Gross 10; exit fee .25; adjusted value 9.75; adjusted P&L 1.75 |
| Multilevel fixture, existing Polymarket rate .10 | Exit fee .60445; adjusted value 14.14555; adjusted P&L 1.64555 |
| Cash 60, marks 20 and 15 | Gross equity 95; when second mark unavailable full equity null, known subtotal 20 and coverage 1/2 |
| Open costs 12 and 8, starting cash 100 | Utilization .20; largest-position share .60; two markets |

All financial assertions use exact decimals, with no floating-point tolerance.

## P–R. Acquisition, accounting and consistency evidence

The API tests use real migration-backed isolated SQLite storage and the existing acquisition spies for discovery, REST books, WebSocket construction, public fees and relationship enrichment. Repeated portfolio, single-position and risk reads assert zero calls. There is no private exchange API in the valuation dependency path. Local Refresh Valuation tests accept only the valuation GET.

Before/after serialization compares generations, balances, positions, executions, journal, ledger, resolutions and financial audit records across repeated valuation. They remain identical. Healthy reconciliation remains Healthy after successful and stale/unavailable valuation. Tests cover partially settled executions, immutable realized results, historical generation isolation, changed native mapping, denied access, pagination, no implicit initialization and oversized generation rejection. A real backend restart retains positions/costs but an empty book cache returns BookUnavailable.

A short deferred SQLite read snapshot ends before book calculation. Required immutable books are captured together under the cache gate, then versions/actionability are checked again before publishing. A deterministic barrier replaces V1 with V2 after capture; affected marks become BookChangedDuringValuation and full bucket equity is null. There are no unbounded retries or network calls. No DB query, fee calculation or UI operation holds the cache lock.

## S–V. Actual verification

- Focused calculation tests: 19 passed.
- Initial focused valuation API/summary tests: 14 passed; subsequent additions cover real restart, cross-workspace identity and 1,000-position bound.
- Focused desktop valuation tests: 7 passed.
- Existing settlement regression filter: 23 passed.
- `dotnet restore ArbitrageTrading.sln`: passed.
- Release solution build: passed, zero warnings/errors.
- `scripts/verify.ps1`: passed restore, Release build (zero warnings/errors), and **611 tests, 0 failures, 0 skips**: Domain 55, Application 223, Backend integration 317, Desktop 16. This preserves the 567-test baseline and adds 44 tests.
- The first sandboxed full run encountered access denials in pre-existing ACL-protected temp-directory/process/credential tests; it is not counted as a passing run. Required verification was rerun with permission for those isolated Windows facilities. An initial overlapping rebuild encountered a file lock held by the still-running sandbox testhost; after that test process exited, verification was restarted sequentially.
- EF `has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build`: no changes since last migration.
- NuGet vulnerable-package audit: passed; no vulnerable packages reported for all 13 projects. The sandbox initially blocked the feed; the permitted public-feed audit completed. Auditing remains enabled.
- `git diff --check`: passed, including the final report update.
- Actual permitted Ubuntu WSL invocation of `bash scripts/verify-backend.sh`: stopped at line 5, `dotnet: command not found`. No local Linux/.NET success is claimed. The observed baseline Linux CI result is separate.

## W. UI checks actually performed

XAML compiled and the automated WPF valuation fixture passed. Eighteen renders cover nine states in both Light and Dark: profitable, losing, partial depth, stale, fee unresolved, mixed coverage, fee adjusted, partial settlement and separate currencies, at 1440×1100 / 96 DPI. Artifacts are outside the repository in the task visualization folder under `phase04c`. Representative Light PartialDepth and Dark FeeUnresolved captures were visually inspected; crowded headings were corrected with row/header spacing and minimum column widths. All 18 captures were regenerated by a passing final WPF fixture run, and the final Dark FeeUnresolved result was inspected. Selected-position details also show key gross/adjusted values without horizontal scrolling. Current local estimates are separate from the historical realized curve.

Automated view-model tests cover local-only GET refresh, late generation/navigation/page/access responses and book/fee invalidation; access denial clears private marks. No manual mouse workflow, physical DPI matrix or screen-reader audit was performed. Controls have labels and keyboard-focusable standard controls; this is not an accessibility certification.

## X–AB. Limits and completion boundary

The whole generation is bounded to 1,000 retained positions, including settled history; oversized generations reject explicitly. API pages contain 1–1,000 positions (default 100), while summaries cover the entire supported generation. No optional top-market breakdown or probabilistic risk model is added. Risk uses the same transparent read model as valuation. Desktop invalidation is conservative and clears marks on book/catalog notifications; its existing active-page two-second local poll recomputes them. Rapid book changes can make a valuation unavailable rather than publish known-obsolete results. Exchange observation times are not atomic across venues.

No historical MTM tick persistence, financial mutation table, migration, fake historical equity curve, automatic close decision, paper SELL/close execution, real order/signing/cancellation, account/credential requirement, transfer or redemption was added. There is no Phase 04D work. Existing ignore rules cover build output, runtime databases/sidecars, logs and secrets; tracked sensitive/runtime filename inspection found none. No normal user storage was migrated. Changes remain uncommitted; no push, merge, PR or remote/history change was made.

## Changed-file inventory

- README.md
- docs/Architecture/PaperExecution.md
- docs/Architecture/Roadmap.md
- src/Arbitrage.Application/OrderBooks.cs
- src/Arbitrage.Backend/FeeEndpoints.cs
- src/Arbitrage.Backend/FeeJobs.cs
- src/Arbitrage.Backend/PaperEndpoints.cs
- src/Arbitrage.Backend/Program.cs
- src/Arbitrage.Backend/Realtime.cs
- src/Arbitrage.Desktop/Services/RealtimeSession.cs
- src/Arbitrage.Desktop/ViewModels/MainViewModel.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Settlement.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.cs
- src/Arbitrage.Desktop/Views/PaperTradingView.xaml
- src/Arbitrage.Domain/OrderBooks.cs
- src/Arbitrage.Execution/README.md
- tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj
- docs/Development/Phase04CVerification.md
- src/Arbitrage.Backend/ValuationEndpoints.cs
- src/Arbitrage.Contracts/ValuationContracts.cs
- src/Arbitrage.Desktop/Services/BackendClient.Valuation.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Valuation.cs
- src/Arbitrage.Desktop/Views/PaperValuationView.xaml
- src/Arbitrage.Desktop/Views/PaperValuationView.xaml.cs
- src/Arbitrage.Execution/PaperValuation.cs
- src/Arbitrage.Infrastructure/PaperValuationInputs.cs
- tests/Arbitrage.Application.Tests/ValuationTests.cs
- tests/Arbitrage.Backend.IntegrationTests/ValuationApiTests.cs
- tests/Arbitrage.Backend.IntegrationTests/ValuationDesktopTests.cs
- tests/Arbitrage.Desktop.Tests/ValuationWpfTests.cs
