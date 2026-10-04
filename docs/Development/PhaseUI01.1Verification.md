# Phase UI-01.1 — Desktop shutdown ownership

## A. Starting state and exact-head CI gate

Repository: `C:\Users\Yovko\source\repos\ArbTrading`; ordinary checkout on `main`, starting HEAD `f2498f0359d161e4609ba279f025975986f8f306` (Redesign desktop visuals and Trading summaries). Working tree was clean. Root, branch, remotes (without credential-bearing URLs), applicable AGENTS.md, and existing files were inspected before edits.

The exact-head push workflow [Build and portable distribution, run 36](https://github.com/yovko93/ArbTrading/actions/runs/37228053163) was initially in progress, permitting this narrow correction under the specification. It subsequently completed **successfully**. Linux and Windows validation passed for that exact starting commit. No remote workflow was rerun or modified.

## B–D. Reproduced exception and previous ownership

**B.** Before editing, a real Release Desktop process was launched with isolated data/runtime/desktop directories and an unused loopback port. Closing its actual WPF window with WM_CLOSE reproduced:

`System.ObjectDisposedException: The CancellationTokenSource has been disposed.`

The throwing object is MainViewModel's lifetime CancellationTokenSource. The process exited with code **-532462766**, without forced termination. Evidence: `artifacts/UI01-startup-7dc6bc06083a4478988e19239d8ebc6e/result.json` and `stderr.log`.

**C.** First disposal: App.OnExit explicitly called `MainViewModel.Dispose`, which cancelled/disposed its source. Second disposal: App.OnExit called `services.Dispose`; the DI provider's disposal scope invoked MainViewModel.Dispose again, reaching CancellationTokenSource.Cancel at baseline line 321 (App.OnExit/provider disposal at baseline line 126).

**D.** App manually disposed ShellViewModel, RealtimeSession, MainViewModel, MarketExplorerViewModel, RelationshipsViewModel, OpportunitiesViewModel, FeeProfileViewModel, PaperTradingViewModel, and KalshiCredentialsViewModel, although those services are DI-owned. The provider then traversed the same owned objects. Cleanup also risked resolving previously unconstructed services via GetService during an early exit.

## E. New disposal ownership and changed files

App delegates cleanup to DesktopLifetime. This App-owned lifetime cancels application work, disposes the provider once, disposes the external logger once after provider cleanup, and disposes its own token source. Nested finally blocks preserve cleanup of later owned resources if an earlier cleanup fails. An Interlocked guard makes repeated cleanup safe. App.OnExit calls base.OnExit in finally.

The provider is the only disposer of its constructed services; shutdown no longer resolves or manually disposes individual singleton services. MainViewModel receives a defensive idempotency guard for legitimate duplicate disposal by test/caller cleanup. RealtimeSession already has an idempotency guard; its code and backend-independent cancellation semantics remain unchanged. Shell continues owning its internally constructed Analytics view model, and the provider owns Shell.

Changed/new files:

- `src/Arbitrage.Desktop/App.xaml.cs`: register ownership during construction, explicitly retain external logger ownership, and remove individual service disposal calls.
- `src/Arbitrage.Desktop/Services/DesktopLifetime.cs`: new App resource owner with ordered, repeatable cleanup.
- `src/Arbitrage.Desktop/ViewModels/MainViewModel.cs`: defensive Dispose guard only.
- `tests/Arbitrage.Desktop.Tests/DesktopLifetimeTests.cs`: ownership/counter, logger, partial startup, early exit, and idempotency tests.
- `tests/Arbitrage.Desktop.Tests/DesktopShutdownProcessTests.cs`: real process/window close, backend survival/reattachment, startup failure, and package-validation tests.
- This report.

Local wrappers, logs, captures, and test outputs are already ignored. No tracked build, signing, or distribution script changed.

## F. Logger ownership

The Serilog logger is constructed manually before building the provider. The installed Serilog.Extensions.Logging 10.0.0 documentation specifies that the dispose argument controls whether the logging provider disposes that logger. Registration now explicitly uses `AddSerilog(logger, dispose: false)`; App owns the logger through DesktopLifetime.

A real Serilog disposable sink test proves provider disposal leaves the external logger alive, and App lifetime disposal tears it down once, after provider/service disposal. Existing log location, retention, rolling, size limit, shared-file behavior, and sanitization are unchanged.

## G. Startup-failure cleanup and validate-package

DesktopLifetime exists before normal startup. Logger ownership is assigned immediately after construction, and provider ownership immediately after BuildServiceProvider. Thus early failure can have neither resource, only a logger, or a provider with some constructed services; cleanup disposes exactly the resources that exist and never resolves additional services.

Tests cover partial provider resolution while preserving the original startup exception, and real startup failures both before logger construction and after logger construction/before provider creation. The normal startup error dialog is closed by the isolated harness; these processes exit with code 1 and no secondary unhandled/disposal exception. Created logs can be opened exclusively afterward.

The development executable's `--validate-package` early rejection exits normally with code 2 and creates no normal Desktop profile. Canonical extracted smoke separately checks a valid package and expected rejection paths.

## H–K. Actual Desktop close and backend survival

**H.** Real Desktop close without a backend passes for both one WM_CLOSE signal and duplicate close signals: exit code 0, no unhandled/ObjectDisposedException, no error dialog, and no database/connection creation.

**I–J.** A separate isolated test starts the exact test-owned backend using the existing controller and migration path. The real Desktop discovers it as ManagedLocal with Stop enabled. Closing Desktop exits with code 0 while that same backend PID and instance remain alive. Managed-local metadata and protected connection bytes are unchanged; backend diagnostics contain no StopRequested event before test cleanup. Paper generation, automation state, monitoring state, and reliability campaign remain unchanged in this fresh fixture.

**K.** The actual Desktop is reopened against the same runtime/profile. It again shows ManagedLocal with Stop enabled, then exits cleanly after duplicate close signals. Survival and unchanged ownership/metadata are asserted again before the test stops only its retained, exact owned backend process. Tests never search by process name or manipulate normal user runtime.

The six process cases use native window handles scoped to each retained Desktop process; ManagedLocal and Stop availability are verified through the actual WPF UI Automation tree. Isolated logs are retained under `%TEMP%\ArbitrageTrading-UI011\<unique-id>`; no credential values are printed or added to reports.

## L. Focused tests

Final focused Release run: **12 passed, 0 failed, 0 skipped** (six disposal/ownership cases and six real-process cases). Evidence: `tests/Arbitrage.Desktop.Tests/TestResults/UI011-focused.trx`.

The first test compilation exposed three test-only signature/analyzer/member errors; those were corrected. The next run passed the ownership and real managed-backend cases but two immediate-close assertions incorrectly assumed lazy file logging had already created its directory. That assertion was corrected; the final 12-case rerun passed. No production exception was hidden by those corrections. After the full runs, metadata assertions were changed to compare bytes without letting xUnit print protected connection bytes on failure. The affected real backend-survival/reopen test was rebuilt and passed again (1/1; `UI011-protected-metadata.trx`).

## M–N. Full verification and builds

Initial unfiltered full verification passed: **941 passed, 0 failed, 0 skipped**.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Domain | 55 | 0 | 0 |
| Application | 277 | 0 | 0 |
| Backend integration | 520 | 0 | 0 |
| Desktop | 89 | 0 | 0 |
| Total | 941 | 0 | 0 |

The full solution Release build succeeded with **0 warnings and 0 errors**.

The initial full run was the canonical publish command's built-in `scripts/verify.ps1` step, which performs solution restore, Release build (`--no-restore`), and unfiltered tests (`--no-build --no-restore`). An ignored local wrapper limited test projects to one and xUnit workers to two without filtering cases. However, PowerShell function argument parsing in that wrapper stripped the `-p:` publish prefixes after verification; Desktop publishing failed with MSB1008 before an archive was produced.

Recovery invokes the unmodified canonical publish script directly, without the wrapper or CI skip flags. Because publishing requires verification within that invocation, this necessarily repeats full verification. This is a deviation from the requested one-full-run efficiency target caused by the local wrapper, not by a source/test failure. Required Windows protected-storage tests run with their normal filesystem permissions. The direct retry also passed all 941 tests and built with 0 warnings/errors. Publishing completed, but the default extracted smoke runtime directories were beneath the Git checkout and were correctly rejected by the existing backend storage boundary. A transient executable lock during cleanup masked that original failure. Recovery below completed the extracted smoke without a third full run.

Initial verification evidence: `artifacts/UI011-verification-publish.log`; initial TRX names are `verification_net10.0_20261004224135.trx` (Domain), `verification_net10.0_20261004224145.trx` (Application), `verification_net10.0_20261004224829.trx` (Backend integration), and `verification_net10.0_20261004225338.trx` (Desktop), each in its test project's TestResults directory.

Direct retry evidence: `artifacts/UI011-publish-final.log`; final TRX names are `verification_net10.0_20261004225635.trx` (Domain), `verification_net10.0_20261004225643.trx` (Application), `verification_net10.0_20261004230408.trx` (Backend integration), and `verification_net10.0_20261004230240.trx` (Desktop). Both unfiltered full runs passed all 941 cases.

## O. EF

`dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` passed: no changes since the last migration.

## P. NuGet

`dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` passed: no vulnerable packages reported across all 13 projects using configured sources.

## Q. Distribution and signing

Canonical command: `./scripts/publish.ps1 -AllowDirty -SigningMode Unsigned`. Both full verification runs passed, and the direct native retry successfully published the self-contained Desktop and Backend. The publish command itself exited unsuccessfully during extracted smoke: its default scratch directory is beneath the checkout, while backend runtime storage rejects Git checkout/worktree ancestors. Cleanup then encountered a transient lock and removed four payload files before failing. These are existing distribution workspace behaviors; no distribution or backend source was changed in this lifecycle-only task.

Recovery retained the next smoke workspace to expose the original exit-1 migration failure. The four missing files were copied from the native publish build output only after each matched its original manifest SHA-256. The complete package then passed canonical inventory, hash, configuration, and signature-policy validation. Recreating the candidate ZIP yielded 120,571,621 bytes. The unmodified `scripts/verify-distribution.ps1` passed against that recovered candidate with its supported `-ScratchRoot` option pointing to a unique temporary directory outside Git, and `-KeepWorkDirectory` for diagnostics. No CI variables or SkipTests gates were spoofed, and full verification was not repeated a third time.

**Final extracted smoke passed**: actual WPF shell observed; no automatic backend/migration; forbidden package-local data rejected; two migrations; backend readiness and restart identity; Paper safety and uninitialized financial/automation/reliability state; package immutability; tamper, missing executable, unsafe path, and signing-spoof rejection; no installed dotnet in child PATH. Evidence: `artifacts/UI011-smoke-final.log`. Failure/recovery evidence: `artifacts/UI011-publish-final.log`, `artifacts/UI011-smoke-retained.log`, and sanitized `artifacts/UI011-packaged-migrate.stderr.log`.

After that successful canonical extracted smoke, the existing `Publish-DistributionArchive` helper atomically promoted the recovered candidate and wrote its SHA-256 sidecar:

- ZIP: `artifacts/distribution/ArbitrageTrading-win-x64-gf2498f03-local-dirty-unsigned.zip`
- SHA-256: `2606A7AA5931E985A3A6476B75F647C0482A8C3A4A940BFB91C1793527679142`
- ZIP bytes: 120,571,621; self-contained payload bytes: 270,084,866.

This is an unsigned local-dirty verification package. The plain publish command did not finish end-to-end successfully; its published payload was recovered, canonically validated and smoke-tested, then promoted using the existing helper. A future distribution task should reconcile the default scratch location with the runtime storage boundary. After verification, only this task's three marked scratch workspaces and redundant candidate ZIP were cleaned with the existing ownership/boundary checks; final ZIP, sidecar, sanitized logs, and test evidence remain.

`scripts/test-distribution-signing.ps1` passed focused unsigned/signature/hash/identity/timestamp/spoof/tamper and cleanup regression checks. The Microsoft SDK SignTool was unavailable or unverifiable; the existing temporary, explicitly untrusted test provider was used. No production signing identity or secret was used. Signing and distribution source code are unchanged.

## R–U. Scope and final state

**R.** UI-01 icon, colors, Trading/Dashboard layout, navigation, chips, buttons, theme resources, and WPF bindings remain byte-for-byte unchanged in the Git diff.

**S.** No backend, contracts, persistence, financial/trading logic, managed ownership rules, monitoring, automation, reliability, fees, settlement, or orderbook code changed. Disposal cancels Desktop work only. It does not call backend Stop, disarm automation, stop monitoring, cancel campaigns, mutate funds, delete runtime metadata, or reset credentials.

**T.** No migration was added or changed.

**U.** All six changed/new source, test, and report files remain uncommitted on `main` at the starting HEAD. No commit, push, branch switch, reset, history rewrite, merge, or pull request was performed.

Suggested commit message: `Fix Desktop shutdown disposal ownership`
