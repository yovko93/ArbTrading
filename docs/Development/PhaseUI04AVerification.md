# UI-04A — Operator workflows

## A–B. Baseline

- Repository: `C:\Users\Yovko\source\repos\ArbTrading`.
- Current ordinary checkout: `main`, initially clean.
- Starting HEAD: `197c7c4272481694853fb871e9970189c9f5142e` (`Improve Trading accordion header contrast`).
- GitHub Actions run [37598791963](https://github.com/yovko93/ArbTrading/actions/runs/37598791963) was independently read through the GitHub connector: completed/success, main, exact matching HEAD. Workflow: Build and portable distribution.
- Existing root AGENTS.md applied. No branch/worktree/repository/remote changes.

## C–D. Changed files and shared resources

Only Desktop presentation, its tests, and this report changed:

| File | Change |
| --- | --- |
| `src/Arbitrage.Desktop/Controls/OperatorPresentation.cs` | Exact-state tone mapping, UTC formatting, DTO-backed explanatory text |
| `src/Arbitrage.Desktop/Controls/CopyIdentifierButton.cs` | Public-ID clipboard control with success/retry feedback |
| `src/Arbitrage.Desktop/Resources/Styles/OperatorWorkflow.xaml` | Scoped operator panel/state/empty styles using existing palette and control templates |
| `src/Arbitrage.Desktop/ViewModels/MarketExplorerViewModel.cs` | Expose existing status CheckedAt; forward optional display clock for deterministic fixtures |
| `src/Arbitrage.Desktop/ViewModels/OrderBookPanelViewModel.cs` | Source, freshness, retained-snapshot descriptions and display notifications |
| `src/Arbitrage.Desktop/ViewModels/RelationshipsViewModel.cs` | Initial Cross exchange checkbox is unchecked; operator opts in explicitly |
| `src/Arbitrage.Desktop/ViewModels/PaperReliabilityViewModel.cs` | Unsatisfied report rows; missing runtime counters display Unavailable |
| `src/Arbitrage.Desktop/Views/MarketExplorerView.xaml` | Catalog sync and orderbook operator panels |
| `src/Arbitrage.Desktop/Views/RelationshipsView.xaml` | Generation counts, review hierarchy, trust state and identifier controls |
| `src/Arbitrage.Desktop/Views/RelationshipsView.xaml.cs` | Responsive layout only |
| `src/Arbitrage.Desktop/Views/MonitoringView.xaml` | Top operations, coverage states, current/history distinction |
| `src/Arbitrage.Desktop/Views/PaperReliabilityView.xaml` | Campaign header, safety evidence and remaining-evidence rows |
| `tests/Arbitrage.Desktop.Tests/Ui02WpfTests.cs` | Optional clock in existing reusable visual fixture |
| `tests/Arbitrage.Desktop.Tests/OpportunityWpfTests.cs` | Locate the profile editor by its existing header instead of assuming it is the only expander |
| `tests/Arbitrage.Desktop.Tests/Ui04OperatorWpfTests.cs` | Nine deterministic fixture tests, optional captures and safe clipboard stub |
| `docs/Development/PhaseUI04AVerification.md` | This report |

The new resource dictionary is opt-in on these four views. Existing global palettes, Trading accordion styles, branding, icons and unrelated pages are unchanged. Existing semantic colors supply Light/Dark/focus/disabled styling. Exact state text remains visible; colors are supplemental.

## E–F. Catalog sync

Both exchanges remain visible. Each card exposes exact latest state, stored/observed/pages/malformed counts, current reason, cache retrieval time and separate last successful complete time. Missing run counters display Unavailable. The status timestamp is the DTO's backend `CheckedAt`, not a fabricated UI refresh time.

Running is blue, Complete green, Partial amber, Failed red, Cancelled/Interrupted amber, and Never neutral. The active banner is bound to the current exchange status; fixture transitions confirm a prior failure reason disappears after Running/Complete replaces it. Failed/partial/cancelled copy does not claim completeness. Complete refers only to the last run's scope, with the existing non-atomic cache limitation. No percentage, ETA, throughput or invented total is shown. Sync, cancel and filter command bindings remain intact.

## G–I. Matching

Generation controls stay above the workspace; existing Sources/Comparisons/Written counts are exposed. Review splits into list and inspector at 1050 page DIPs and stacks below that width. Source/target metadata also stacks when the inspector is narrow. Tables retain horizontal scrolling.

Blocking differences lead the inspector, expanded by default. A warning appears only when the DTO contains blocking evidence. No-blocker copy explicitly leaves verification authoritative. VerifiedDeterministic is green; VerifiedManual/Manual amber; stale/rejected/unknown states remain distinct. Static text says Automatic Paper requires deterministic verification; Desktop does not recalculate eligibility.

Three Copy controls copy the exact relationship/source-native/target-native identifier. Missing IDs disable copying. Copied/Retry copy feedback includes automation help text. Tests intercept the clipboard writer and do not overwrite the user's clipboard. Original selectable relationship ID, review commands and confirmations remain in place. Cross exchange now begins unchecked as explicitly requested; generation request construction and backend behavior are unchanged.

## J–K. Orderbook and realtime

The inspector begins with source, continuity, current display freshness, book availability, realtime state, reasons, prices, spread, age and retrieval time. Continuous is green; BestEffort amber; REST Snapshot blue and explicitly named. The age display uses the existing clock and DTO freshness threshold. Retained levels remain visible with an explicit stale label. The older backend book state is separately labeled, so it cannot masquerade as the current freshness badge.

Current connection limitations and provenance remain visible. No source/continuity combination is called Healthy. Refresh, Start Realtime, Stop Realtime and depth commands are unchanged; no automatic subscriptions were added.

## L–M. Monitoring

The top operator card shows exact state, last backend evaluation, queue and alerts, with Start/Stop/Refresh/Save Profile together. Existing coverage counters remain visible; detailed coverage diagnostics are expandable. Explicit no-monitored-relationships/no-actionable-books/no-fee-adjusted-opportunities messages follow actual zero counters without declaring a failure.

Ranking order, filters, economics and lanes are unchanged. FeeAdjusted/GrossOnly/NearEdge/Blocked are green/blue/amber/red. Current ranking snapshots and historical alerts have explicit separate labels; inspecting current remains a separate existing command. No historical opportunity is recreated.

## N–Q. Reliability

The campaign header shows name, exact campaign/report states, start/evaluation timestamps and report gap flag. Identity/policy diagnostics are expandable. The result carries the explicit text: **CriteriaMet unlocks no live execution capability.**

Remaining evidence is exactly report invariants with State != Satisfied, followed by criteria with State != Satisfied. Rows show original code, observed, required, exact state and explanation. Duration formatting reuses existing code semantics. Missing runtime counters now read Unavailable instead of zero. Empty unsatisfied lists display the authoritative report state; they never synthesize CriteriaMet. Safety invariants retain their stronger warning outline and precede ordinary criteria. Unknown is amber, Violated red, Satisfied green. No progress bars, hardcoded thresholds, eligibility or financial calculations were added.

Campaign action/confirmation bindings, exports and history remain unchanged.

## R–T. Visual verification

Deterministic fixture captures under ignored `artifacts/UI04A-renders/` cover both Dark and Light:

- Catalog Running, Failed with reason, Complete.
- Matching blocking differences and VerifiedDeterministic, plus assertions for manual trust.
- Orderbook Continuous, stale cached levels and unavailable; BestEffort/REST checked structurally.
- Monitoring Running with coverage and no actionable inputs.
- Reliability Collecting/pending evidence, Unknown/Violated invariants and CriteriaMet with the no-live warning.
- Each page at a conservative 510×340 content viewport, plus the existing real 830×590 shell tests across all destinations.

Assertions check semantic state/tone, bindings, command identity, no outbound fixture HTTP requests, visible panels, keyboard-focusable operator actions and horizontal bounds. Existing interaction binding contracts pass. No pixel-equality assertions were added.

Visual tradeoffs: narrow windows require vertical scrolling; wide tables use their own horizontal scrollbars. Matching splits only when there is sufficient page width. Long diagnostics are expandable; blocking differences and remaining evidence remain open. Fixture screenshots do not claim real exchange connectivity or a completed BurnIn campaign.

## U–AA. Verification results

- Focused: 27 distinct tests passed (26 UI/resource/contract cases including 9 new UI-04A cases, plus the corrected legacy profile-editing case). Two catalog cases also passed again after the final state-label clarification.
- Release solution build through `scripts/verify.ps1`: passed, 0 warnings and 0 errors.
- EF pending-model check: "No changes have been made to the model since the last migration."
- Full suite: **983 passed, 0 failed, 0 skipped** — Domain 55, Application 277, Backend integration 520, Desktop 131. Canonical final TRX suffixes: Domain `20261007141839`, Application `20261007141848`, Desktop `20261007142509`, Backend `20261007142622`.
- One unsigned portable publish and extracted-ZIP smoke passed, exit 0. Desktop window observed; bootstrap, migration idempotence, restart identity, Paper safety, package immutability, tamper/missing/path rejection all passed with spaces in the extraction path and no dotnet in the child PATH.
- Focused signing regressions passed: unsigned policy, ephemeral untrusted signatures, post-sign hashes, wrong identity, missing timestamp, spoof/missing signature, native tamper rejection and successful/failed certificate/private-key cleanup. No private-key-looking files are tracked. Microsoft SDK SignTool was unavailable or unverifiable; the independent test signer was used. Production Authenticode signing was not performed or claimed.
- Release and final-ZIP Desktop executable icon payloads match the unchanged source ICO at all nine sizes: 16, 20, 24, 32, 40, 48, 64, 128 and 256. Existing WPF branding/resource regressions also passed.
- NuGet vulnerability audit, including transitive dependencies: no vulnerable packages in any of the 13 solution projects with the configured current sources.
- Verification uses the canonical `scripts/publish.ps1` invocation, which calls `scripts/verify.ps1` for restore, Release build and the full suite before one unsigned publish/smoke. `SkipTests` is not used. The first full pass found one legacy test's single-expander assumption (982 passed, 1 failed); after correcting its profile selector, the publisher's mandatory full verification gate was repeated. The failed gate never reached publishing or package smoke.
- Full tests and publish use a fresh, short external TEMP/TMP root and external WorkRoot; no staging beneath the repository or user runtime database.

Package: `artifacts/UI04A-distribution/ArbitrageTrading-win-x64-g197c7c42-local-dirty-unsigned.zip`.

- SHA-256: `DF854C6FCF55FB1731F71F9B11C8C2474DFE049D9C9033D6F52FDE203628295C`, independently matched against its sidecar.
- ZIP: 120,650,892 bytes. Payload: 270,242,562 bytes. Inventory: 790 files plus manifest (791 total).
- Manifest: exact baseline commit, SourceDirty true, win-x64, PortableZip, Paper, UnsignedSnapshot, SigningRequired false.
- Both task scratch roots (`AT-U4-0bf5d1c8`, `AT-U4-6b2be862`) beneath the user's temporary directory were removed. The first cleanup required graceful compiler-server shutdown to release an analyzer DLL; the final run disabled shared compilation and cleaned successfully. No unrelated process was stopped.
- The temporary extracted executable used for icon inspection was removed. Application/test source hashes remained unchanged through the successful verification and packaging run; this report was then completed.
- `git diff --check` passed. The checkout remains `main` at the starting HEAD, with 16 changed source/test/documentation files and no commit or push.

Local evidence is under ignored `artifacts/`: `UI04A-baseline-ci.json`, `UI04A-final-test-results.json`, `UI04A-full-publish.log`, `UI04A-validation.log`, `UI04A-ef-model.log`, `UI04A-vulnerabilities.log`, `UI04A-signing.log`, `UI04A-package-metadata.json`, `UI04A-source-hashes.json`, `UI04A-renders/` (36 UI-04A captures: 18 per theme, plus existing regression captures), and `UI04A-full-renders/`. Focused and canonical full-suite TRX files remain in each test project's ignored `TestResults` directory.

## AB–AF. Scope and source state

No real runtime was mutated. Visual fixtures use rejecting HTTP handlers and isolated data. The full suite and portable smoke operate on their own temporary databases/processes. `%LOCALAPPDATA%\ArbitrageTrading\backend` was not opened or used for this task.

No backend, connector, domain/application, financial/trading, lifecycle, authentication, API/data-contract, schema or migration code changed. No migration was added. All edits remain uncommitted; nothing was pushed.

Suggested commit message: `Improve operator status and evidence workflows`
