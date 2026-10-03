# Phase 04H.5 — Desktop lifecycle controls

Completed verification: **873 passed, 0 failed, 0 skipped**; Release build **0 warnings, 0 errors**. Both canonical **Unsigned** and **TestEphemeral** packages passed extracted smoke. EF has no pending model changes; the transitive NuGet audit reported no vulnerabilities. Actual normal Desktop Start/Refresh/Stop and automated real WPF buttons were verified. Eight lifecycle renders were inspected across Light/Dark themes. The 17 changed/new files remain uncommitted on `main`; BurnIn was not started.

## Baseline and reproduction (A–H)

Repository: `C:\Users\Yovko\source\repos\ArbTrading`; branch `main`; starting HEAD `b69b4885d8415e8394081c4626ad4276e1a77de4`; initial working tree clean. Exact Actions run 36047366493 was confirmed completed/success for that HEAD. No branch, remote or history changes.

The normal Release Desktop, with no artifact override and an isolated profile, showed NotRunning / Disconnected / Workspace unavailable / Paper Unknown / Live Unknown. Its actual Start button was **disabled**, with the missing-artifact reason only in its tooltip. An enabled click disappearing was not reproduced in this baseline. The resolver looked beside Desktop rather than in the matching Backend repository output. No migration or backend child ran in that baseline; package/signature validation was not involved.

A deterministic notification-reentrancy test identifies a second defect: the old code published `IsBusy=false` before releasing the operation semaphore. A Start invoked synchronously from that availability notification could be lost. Ordinary held initial observation disables Start; the test verifies that actual button behavior. No stale WPF Command/IsEnabled binding defect was found; the existing Start/Stop bindings are retained.

## Changes and behavior (I–M)

- Added constrained repository build discovery with solution/project markers and matching configuration/framework/runtime identifier. Explicit artifact and DLL-host overrides remain supported. Package boundaries and identity validation remain authoritative.
- Release the operation gate before notifying availability. Initial observation, Start, Stop and Refresh share busy state; the header disables all three controls during an operation. Stop acquires the gate before entering its existing confirmation, preventing modal reentrancy.
- Show wrapped lifecycle explanation beneath the header. Missing artifacts, progress and controller outcomes are visible without a tooltip. Add safe requested/succeeded/failed/uncertain diagnostics and sanitize unexpected exceptions at lifecycle and refresh callback boundaries.
- Include the child exit code for early exit. Keep the existing 20-second readiness and migration timeouts, ownership checks and no-force-kill policy.
- Give only the three lifecycle buttons a theme-aware template because the native disabled template produced poor Dark-mode contrast.

Start now moves NotRunning → Starting → Running, or returns the controller's Faulted/Unknown outcome. Unexpected exceptions become Unknown with instructions to Refresh and inspect diagnostics. Retry requires fresh observation; no uncertain backend is silently treated as stopped. Successful Start signals realtime synchronization once. Stop still requires exact managed identity and confirmation; Refresh observes/resynchronizes only. Closing the shell does not send Stop.

Changed-file summary (17 files):

| Area | Files |
|---|---|
| Desktop behavior (5) | BackendArtifactLocator.cs (new), LocalBackendController.cs, BackendProcessViewModel.cs, MainViewModel.cs, MainWindow.xaml |
| Tests and test build (7) | BackendArtifactLocatorTests.cs, BackendLifecycleWpfTests.cs, PrimaryButtonBindingWpfTests.cs (new); BackendProcessViewModelTests.cs, RealtimeProcessTests.cs, PaperPersistenceTests.cs, Backend.IntegrationTests.csproj |
| Documentation (5) | README.md, Distribution.md, LocalDevelopment.md, PaperBurnInRunbook.md, this report (new) |

## Actual button evidence (N–R)

`BackendLifecycleWpfTests` constructs the real MainWindow and ShellViewModel on the WPF dispatcher, locates the three named buttons, and invokes their ButtonAutomationPeer. It asserts command resolution, actual IsEnabled, held observation, rapid interaction, visible failure text and sanitized diagnostics. Real process variants use repository discovery and a manifest-backed package fixture, launch Arbitrage.Backend.exe, authenticate, synchronize workspace/capabilities, refresh the same process/instance, close/reopen the shell without stopping Backend, then confirm Stop and verify exit. No financial commands execute.

The final development fixture prepares an empty migrated real SQLite workspace before showing the shell. The package fixture begins with no database and runs migration through the actual Start button. Both await the real command's ExecutionTask before asserting Running, then independently verify realtime synchronization. All four final focused lifecycle cases passed; the earlier normal standalone development launch also proved cold Start from a fresh profile.

Focused lifecycle tests passed (8); the subsequent binding/render check passed (3). One initial development fixture timed out while waiting for readiness and left a backend child; only that exact testhost-owned child was cleaned up. The unchanged bounded readiness behavior passed on rerun. A standalone-app attempt initially used a Git-contained scratch directory and correctly failed with exit code 1; the corrected profile is outside Git. These setup failures are not counted as successful acceptance evidence.

## Bounded primary-button audit (S–U)

The WPF audit loads Market Explorer (including order book), explicit opportunities, monitoring, paper trading, portfolio, risk, automation, settlement, valuation, reliability, analytics, relationships and Settings with their real view models. It checks at least 85 actual command bindings and evaluates CanExecute without invoking mutations. All checked bindings resolved; no additional dead binding was found or changed.

Classification: refresh/read/filter/pagination controls are command-bound; lifecycle and selection-dependent actions are intentionally gated; paper preview navigation remains navigation-only; confirmed mutations retain their existing confirmation and availability checks. Initialize/reset, risk/profile save, arm, emergency stop, kill reset, settlement and reliability completion/cancellation code was not changed. Their wiring is checked using isolated state. This is a bounded binding/state audit, not a claim that all financial actions were executed.

## Verification and safety (V–AF)

Final full-suite and distribution results are recorded below. EF reports no pending model changes. NuGet audit reports no vulnerable packages in any project including transitive dependencies. Light/Dark NotRunning, Starting, Running and Faulted renders were inspected; lifecycle text wraps, controls are legible, and no header overlap was found. Live capture could read accessibility but screenshot capture timed out; WPF RenderTargetBitmap images provide the visual regression evidence.

No backend financial, acquisition, execution, risk, settlement or reliability semantics changed. Live execution remains unavailable. No EF migration added. No monitoring, campaign, Auto Paper or BurnIn started against the user's runtime. Changes remain uncommitted; no push or PR.

### Standalone normal Desktop confirmation

Using a fresh profile under the OS temporary directory, without an artifact override, keyboard activation of the actual header controls produced:

1. Start: NotRunning → Starting → Running / ManagedLocal / Connected; Personal workspace appeared, Paper Available and Live Unavailable.
2. Refresh: visible Refreshing result → verified managed backend running; same PID 22080 and instance d26270e4-8306-4dae-aac6-5bdef1ee7260.
3. Stop cancellation: the existing shared-backend confirmation appeared; cancelling preserved the running backend.
4. Confirmed Stop: Running → Stopping → NotRunning; inline result said process exit confirmed. PID 22080 exited while Desktop PID 22424 remained alive. Start became available again and realtime showed StopRequested.

Only these lifecycle controls were exercised. The isolated Desktop was closed afterward. Native screenshot capture remained unavailable, but accessibility state and exact process identity verified the actual normal startup path. Keyboard input through the computer-use tool succeeded after window activation.

### Verification environment notes

The first full build exposed a missing linked-source include in Backend.IntegrationTests; adding BackendArtifactLocator.cs to that project's existing Desktop links fixed it. The next Release build had zero warnings/errors. A concurrent full-suite run then failed existing readiness/cleanup tests and a paper-persistence assertion; it was stopped after six reported failures to avoid accumulating test children. Only exact verified testhost processes and their backend children were terminated. No user's process was stopped.

The next full run used the unchanged scripts/verify.ps1 through a process-local dotnet wrapper adding `-m:1 --settings artifacts/04H5-serial.runsettings` to test commands. That attempt set RunConfiguration.MaxCpuCount=1, xUnit.MaxParallelThreads=1 and xUnit.ParallelizeTestCollections=false. DOTNET_PROCESSOR_COUNT=2 remains process-local. No tests are filtered or skipped; no production timeout or financial logic is relaxed. The final concurrency settings and one justified test-only reconnect wait adjustment are recorded below.

Distribution workspace regression: 42 checks passed. Focused signing-policy checks passed, including native tamper rejection and ephemeral certificate/private-key cleanup.

### Source button inventory

All 94 Button declarations in Views have Command bindings. The real-view audit resolves the corresponding commands without executing mutations; the separate MainWindow lifecycle tests exercise its three controls.

| View | Buttons | Action classification |
|---|---:|---|
| MainWindow | 3 | Explicit lifecycle actions; Stop confirmation; Refresh observation |
| MarketExplorer | 9 | Filter/page/read; explicit sync and realtime subscription controls; state/selection guards |
| Monitoring | 10 | Explicit start/stop/profile; cached display and paging; preview navigation |
| Opportunities | 8 | Explicit evaluation/fee refresh and cancellation; paging; preview navigation |
| PaperAnalytics | 1 | Read summary |
| PaperAutomation | 8 | Read/load/preview; confirmed profile/arm/kill controls and existing disarm policy |
| PaperPortfolio | 3 | Read portfolio and history pages |
| PaperReliability | 11 | Confirmed campaign actions; read/export/history controls |
| PaperRisk | 3 | Read/load policy; confirmed save |
| PaperSettlement | 13 | Read/page/preview; confirmation-gated resolution |
| PaperTrading | 4 | Read/preview; confirmed generation reset and execution |
| PaperValuation | 3 | Read and page valuation |
| Relationships | 10 | Read/filter/page, explicit jobs, confirmed review |
| Settings | 8 | Local form/load/read; existing save and credential confirmation/file-selection policies |

No unavailable/deferred page contains an unbound action button. No financial, credential or campaign mutation was triggered by this audit.

The one-worker attempt was stopped after 429 integration passes and no reported integration failures; it was incomplete, not a passing full run. Stack samples showed repeated ASP.NET endpoint-delegate compilation. Its runner continued into Desktop after cancellation, so the exact original runner tree was terminated too. The final verification configuration keeps projects sequential (`-m:1`, MaxCpuCount=1), restores two xUnit workers with collection parallelism, and enables normal console verbosity. This same unfiltered configuration is used for canonical publish verification. No production rules or timeouts were changed to accommodate these runs.

### Completed full verification

`scripts/verify.ps1` completed successfully using the sequential-project/two-worker configuration: restore succeeded; Release build had **0 warnings and 0 errors**; **873 tests passed, 0 failed, 0 skipped** (Domain 55, Application 277, Backend Integration 506, Desktop 35). The backend integration suite took 19.4 minutes and Desktop 8.7 minutes on this run. Both real WPF lifecycle variants, observation/race cases, exception boundaries and primary-binding audit passed. The same source remains uncommitted.

The first subsequent Unsigned publish gate encountered the pre-existing intermittent paper-persistence null dereference again and was stopped before packaging. Its restart/replay fixture used wall-clock time across a preview whose production validity is five seconds. The fixture now injects the existing fixed test clock into both backend lifetimes and stamps its books/fees consistently; it also asserts the commit state with the rejection reason before reading Execution. This removes scheduler-delay exposure from a persistence test without changing production expiry, financial rules or expiry-specific tests. Both focused persistence tests passed. The original null dereference did not record its rejection, so expiry is the identified timing vulnerability, not a captured rejection code from the failed run.

The next canonical gate completed with 872 passing tests and one failing existing packaged two-controller test. Both new WPF real-button variants passed. The old test assumed both simultaneous Start calls always returned Running, although the competing controller has a bounded 20-second launch-lock wait and package migration plus readiness can exceed that duration. Cleanup masked the underlying assertion with a locked backend.lock error and left one exact test-owned backend; that process was identified by its unique fixture executable path and parent and stopped. The test now accepts only Running or the specific bounded competing-start Unknown result, requires at least one successful start, and verifies that observation from the other controller returns the same managed PID and backend instance. It records ownership before subsequent assertions and preserves uncertain fixtures rather than masking failures during deletion. Production launch-lock, readiness and migration timeouts remain unchanged.

A focused rerun exposed an additional race in that existing test: its inline dispatcher runs on a worker thread, and the assertion could read AuthenticationFailed between that assignment and the rest of private-state invalidation. The test now waits for the completed invalidation (failed authentication, cleared snapshot, empty diagnostics and reset history cursor) before asserting those same requirements. This changes test synchronization only; it does not weaken authentication or suppress a failing state. Locked fixture cleanup checks the lease and preserves evidence on IOException rather than replacing the original assertion.

With the original failure no longer masked, a development fixture reported the expected bounded readiness timeout: its process started at 22:58:58 and completed backend initialization at 22:59:21, about 23 seconds later. The older realtime/concurrent-controller fixture now explicitly migrates its isolated development database before testing realtime and restart behavior. Its package variant still exercises package migration. At that stage the new actual-button development test still exercised cold bootstrap; its later fixture adjustment is recorded below. No readiness timeout was extended and no timeout result is counted as Running. The exact timed-out test child was cleaned up using verified parent/process identity.

Both corrected realtime process variants passed in the focused rerun. The next complete canonical test gate then passed all **873 tests**, including all 35 Desktop tests, on the final source. Publishing stopped afterward because the temporary PowerShell dotnet wrapper split unquoted `-p:` arguments. No package was accepted from that attempt. The wrapper now removes itself immediately after the single full-solution test invocation, allowing the unchanged publisher to invoke native dotnet directly. Native-command restoration was checked before restarting the canonical pipelines. The canonical test gate and its SkipTests restriction were not bypassed.

### Canonical distribution results

Unsigned: full gate **873 passed, 0 failed, 0 skipped**; extracted smoke passed with no dotnet in child PATH. The real Desktop window appeared; bootstrap/idempotence, restart identity, Paper-only capabilities, package immutability, tamper/missing-file/path rejection all passed. Archive: `artifacts/04H5-distribution-Unsigned/ArbitrageTrading-win-x64-gb69b4885-local-dirty-unsigned.zip` (120,528,308 bytes; 791 package files). SHA-256: `242CB22E45A91C9828C3D14AD6524A6739D04FA5DFB08DA368F1546E449D6002`.

An additional standalone launch from that verified ZIP used a fresh external profile, PATH restricted to System32 and a nonexistent DOTNET_ROOT. Desktop PID 21528 opened and created neither a database nor managed-backend metadata. The live UI helper repeatedly returned no accessibility tree for this extracted window, so no lifecycle inputs were guessed. The isolated Desktop closed, and package integrity remained unchanged. This additional launch proves no-auto-start and standalone shell launch; it is **not** claimed as an extracted-app Start/Stop click proof. Actual portable button coverage is the manifest-backed WPF integration test; self-contained distribution behavior is covered separately by canonical extracted smoke. The normal development Desktop's actual keyboard Start/Refresh/Stop evidence is recorded above.

The first TestEphemeral gate passed all Backend integration tests but failed the older packaged automatic-reconnection assertion after its third backend had reached Running. That assertion waited 12 seconds although the unchanged production retry policy permits 30 seconds plus jitter and an eight-second hub handshake. Only this automatic-reconnection test assertion now allows 45 seconds; it still requires the expected backend instance and Connected state without invoking Refresh or Resume. The default test wait stays 12 seconds, and production startup stays bounded at 20 seconds. The already-failed gate was stopped before signing; its exact runner tree was cleaned up. This test-only change does not affect the verified Unsigned payload.

Both focused reconnect variants then passed, and the next canonical gate passed that regression but ended with 872 passes and one packaged WPF test timeout. The new WPF test had incorrectly combined migration, startup and synchronization into one 35-second wait. It now captures the ExecutionTask produced by the actual Button invocation, requires that task to exist, awaits command completion within a three-minute outer test budget covering the existing two-minute migration plus 20-second readiness limits, and asserts the actual Running result with its diagnostic explanation. A separate 35-second wait then verifies realtime synchronization. This improves the button-path proof and preserves every production timeout and outcome assertion.

The focused package-button rerun passed after that correction. A development fixture then exposed the same locked-file cleanup masking issue; WPF cleanup now retains the kernel process handle before further awaits, checks the runtime lease before deletion, and preserves a locked fixture on IOException. It does not suppress lifecycle assertions or change shutdown ownership. The exact package child left by the earlier premature test timeout was identified by its unique fixture path and testhost parent and stopped.

Once unmasked, the development result was the expected 20-second readiness timeout during cold bootstrap. The final development button fixture explicitly migrates an empty real SQLite workspace before launching the shell, then verifies the actual Start/Refresh/Stop path with no running backend. The package button fixture still begins with no database and proves migration through Start. This separates control-path proof from cold development bootstrap timing on this loaded machine; it does not accept Unknown as Running, extend production timeouts or introduce a fake backend. The earlier normal standalone development launch used a fresh profile and successfully exercised cold Start, as recorded above. Only the exact timed-out fixture child was terminated.

### Final verification on the completed source

The final TestEphemeral canonical pipeline completed successfully on 2026-10-03. Its unchanged `scripts/publish.ps1` called `scripts/verify.ps1`, including restore, Release build and the entire solution test suite. The process-local wrapper limited project concurrency and used two xUnit workers; it applied no test filter, skipped no tests and did not bypass the canonical test gate. Results: Domain **55**, Application **277**, Backend Integration **506**, Desktop **35**; total **873 passed, 0 failed, 0 skipped**. Build: **0 warnings, 0 errors**. Backend Integration took 22.7 minutes; Desktop took 6.5 minutes. Both final real-button process variants and both Light/Dark observation/race cases passed.

TestEphemeral extracted smoke passed with spaces in its path and no installed dotnet in the child PATH. The actual Desktop window appeared; bootstrap/idempotence, restart identity, Paper-only capability safety, package immutability and tamper/missing-file/path rejection passed. Temporary signing certificate and private-key cleanup was verified. Archive: `artifacts/04H5-distribution-TestEphemeral/ArbitrageTrading-win-x64-gb69b4885-local-dirty-test-signed.zip` (120,545,447 bytes; payload 270,043,836 bytes; 791 files). SHA-256: `CF98BB470780FC12A23CABBEA8CAAF2F6A2EE091961D370BBDCF5532C3016131`.

The Unsigned result above remains valid for the final production payload: subsequent changes affected test synchronization/fixtures and this report only. TestEphemeral is a test-signing artifact, not a production-trusted signing release. Both packages are intentionally marked local-dirty. Final evidence logs are ignored local artifacts: `artifacts/04H5-canonical-Unsigned.log`, `artifacts/04H5-canonical-TestEphemeral-current.log`, `artifacts/04H5-workspace-checks.log`, and `artifacts/04H5-signing-checks.log`. `git diff --check` passed. No repository metadata or remote configuration was changed.
