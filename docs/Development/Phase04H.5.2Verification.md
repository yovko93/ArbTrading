# Phase 04H.5.2 — Debug/F5 backend build parity

## A–F. Baseline, reproduction, and correction

**A.** Repository root: `C:\Users\Yovko\source\repos\ArbTrading`; ordinary checkout on `main`, starting HEAD `0266ae4c3a996a3c794e18a607876262e754fe7b` (parent `d23b66bc8ee9548c1b81fa42cb9ead3936f08d55`). The working tree was clean. Root AGENTS.md and both preceding lifecycle verification reports were read. Exact-head [Actions run 37120109418](https://github.com/yovko93/ArbTrading/actions/runs/37120109418) was completed/success. Remotes were inspected without printing embedded credentials. No Git metadata, branch, remote, history, or user changes were altered.

**B–C.** Before a solution build, `dotnet build src/Arbitrage.Desktop/Arbitrage.Desktop.csproj -c Debug` succeeded but built only Contracts and Desktop. The existing Debug Backend DLL remained at version `1.0.0+4e5a4f43278f0c0c1f6065eb59fb32f18ae2c7f8`, timestamp `2026-09-24T12:47:42Z`; its SHA-256 before and after was `4828E9037A76F6C157A0D82D130970D18369B75F939A4CE665CA8F6E0A085BD4`.

A source-only copy beneath ignored `artifacts/04H52-fresh-dca121daa23c4c469456a2f64ca1f0a2` contained no Backend bin/obj output. The unchanged Desktop-only Debug build succeeded while leaving Backend absent. Launching that actual Debug Desktop in an isolated temporary profile displayed NotRunning/Disconnected, disabled Start, and the existing explicit matching-build guidance. There was no artifact override. The locator selected the matching Debug Backend path; the problem was its missing/stale output, rather than Release selection or a RID/configuration mismatch. The copied area contained no Git metadata or user runtime data.

**D–E.** Desktop had no Backend build dependency. The sole production change adds a Backend ProjectReference with `ReferenceOutputAssembly="false"` and `Private="false"`. MSBuild now includes Backend in the same configuration/RID build graph for Desktop project builds and Visual Studio startup builds. There is no custom recursive MSBuild target or cycle. The equivalent Desktop-only workflow was executed; an interactive Visual Studio F5/debugger session was not separately exercised.

**F.** `ReferenceOutputAssembly=false` excludes Backend from Desktop compilation; `Private=false` excludes its output/content from Desktop copy and publish traversal. The SDK's evaluated Debug `ReferencePath` contains only Arbitrage.Contracts among application assemblies, and no EF/SQLite references. A deterministic test verifies the exact project metadata, Contracts-only compile project reference, actual Desktop assembly references, and absence of implementation DLLs/backend appsettings in Desktop output. HTTP/Contracts remain the business-data boundary.

## G–N. Focused build and lifecycle evidence

**G.** After copying only the corrected Desktop project file into the fresh area, Desktop-only Debug build produced `src/Arbitrage.Backend/bin/Debug/net10.0/Arbitrage.Backend.exe`, with zero warnings/errors. No manual Backend/solution build preceded it. The same original-checkout Desktop-only build refreshed the stale Backend DLL to `1.0.0+0266ae4c3a996a3c794e18a607876262e754fe7b`, timestamp `2026-10-03T12:56:03.7437673Z`, SHA-256 `FEBAEFFF187F84444C339C159276F1D99866BA4641A5B5A530E5BD88BE88F349`.

**H.** Normal Debug resolution is `C:\Users\Yovko\source\repos\ArbTrading\src\Arbitrage.Backend\bin\Debug\net10.0\Arbitrage.Backend.exe`. The fresh copy resolved that same relative Debug path beneath its fixture root; no Release fallback was used.

**I–K.** The fresh Debug Desktop was launched again after its isolated empty database was explicitly migrated. Initial state was NotRunning with Start enabled: building/launching Desktop did not start Backend. Keyboard invocation of the actual visible WPF Start button reached Running / Connected / ManagedLocal, Paper available and Live unavailable. Backend PID **17428**, instance **a6d35af4-1623-4f05-b5d5-c38c4ec5d180**, ran the fresh-copy Debug executable. Actual Refresh preserved both identities and ownership. Actual Stop displayed the shared-backend confirmation; **Yes** produced NotRunning and “Managed backend process exit was confirmed.” OS process absence and removal of isolated managed-local.json were checked. The test Desktop was closed afterward.

The existing real WPF button tests now select the Backend artifact matching their own Debug/Release compilation. Debug focused run: **11 passed, 0 failed, 0 skipped**. Three real-process cases exercise development, portable fixture migration, and delayed readiness; they assert authenticated synchronization, same identity after Refresh, reattachment, confirmed Stop/exit, metadata cleanup, and no launch from initial observation/Refresh. These automated cases inject a true Stop-confirmation callback; the visible confirmation/Yes was additionally verified above. Two Light/Dark cases cover action gating, rapid clicks, and safe inline failures.

**L.** Release focused run: **11 passed, 0 failed, 0 skipped**, including the same three real-process Start/Connected/ManagedLocal/Refresh/reattach/confirmed-Stop cases. Production lifecycle/ownership logic was unchanged.

**M.** Five locator cases passed in both configurations: matching Debug/Release with and without win-x64 RID, plus explicit override, portable backend path, and unrelated directory fallback. Tests do not require a prebuilt artifact merely to assert path selection.

**N.** The build-only dependency/assembly/content boundary test passed in Debug and Release. Both solution builds completed with zero warnings/errors, proving the expanded graph builds without recursive or duplicate-build failure.

## O–Y. Complete verification and scope

**O.** Full verification passed: **893 tests, 893 passed, 0 failed, 0 skipped** (Domain 55, Application 277, Backend integration 520, Desktop 41). The canonical publisher invoked scripts/verify.ps1 without SkipTests, so its restore, Release solution build, and unfiltered Release tests were the single full verification run; the extra Debug solution build was run separately. An ignored local wrapper limited test project concurrency to one and xUnit workers to two without changing tracked scripts or filtering the suite. TRX results and the canonical transcript are retained beneath ignored TestResults/artifacts directories.

**P.** Final Desktop-only Debug, fresh-copy Desktop-only Debug, Debug/Release test-project builds, and Debug/Release solution builds succeeded with **zero warnings and zero errors**. A first draft of the new dependency test had a missing Views namespace and an xUnit analyzer error; both were corrected before the focused tests, with no production-code impact.

**Q.** Release EF `has-pending-model-changes --no-build`: passed, no model changes since the last migration.

**R.** Solution NuGet audit with `--vulnerable --include-transitive`: passed; no vulnerable packages reported across all 13 projects.

**S.** One canonical `scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot D:\ArbitrageTradingBuildWork -OutputDirectory artifacts/04H52-distribution-Unsigned` completed successfully, including the full source verification above. Its extracted ZIP smoke passed from a path containing spaces with no dotnet in child PATH: actual Desktop shell window, no automatic backend/migration start, bootstrap/idempotence, restart identity, Paper safety, package immutability, and tamper/missing/unsafe-path rejection. Staging and smoke workspaces were cleaned by the existing ownership-checked workflow.

Verified local-dirty archive: `artifacts/04H52-distribution-Unsigned/ArbitrageTrading-win-x64-g0266ae4c-local-dirty-unsigned.zip`. ZIP bytes: **120,528,688**; staged payload bytes: **270,027,010**; files: **791**. ZIP SHA-256: `E840D88150FFD668F5FE9E537EB5A559CD7B17CBA819344021997495F9B550A7`. Manifest records Unsigned, starting source HEAD, and SourceDirty=true.

**T.** Focused signing script passed: Unsigned, structured test signatures, post-sign hashes, wrong identity, missing timestamp/signature, spoof/tamper rejection, successful/failed ephemeral certificate and private-key cleanup, and no tracked private keys. Microsoft SDK SignTool was unavailable/unverifiable; the independent ephemeral test provider passed. No production certificate or full TestEphemeral package was used.

**U.** Independent read-only inspection of final ZIP entries confirmed Desktop at `ArbitrageTrading/Arbitrage.Desktop.exe`, Backend at `ArbitrageTrading/backend/Arbitrage.Backend.exe`, and Backend configuration under `backend/`. **Zero** Backend/Infrastructure/Execution/Strategies/Application/Domain/Connectors implementation files or backend appsettings were present at Desktop package root. The Desktop publish's build graph also produced the matching `Release/net10.0/win-x64` Backend before the separate Backend publish. The portable architecture was preserved.

**V–X.** No automatic Backend start was introduced. Locator, launch/Stop policy, ownership safeguards, API/execution capabilities, financial logic, and schema were unchanged. No live execution was enabled and no EF migration added. The default user backend/runtime/credentials were never reset or used for lifecycle mutations. No monitoring, campaign, automation, settlement, or BurnIn-01 was started.

**Y.** All changes remain uncommitted. No commit, push, merge, or pull request was performed.

Final `git diff --check` passed. HEAD and branch remained unchanged; no migration or unrelated source file changed.

Changed files: Desktop project file; locator tests; new DesktopBuildDependencyTests; configuration-aware real-process/WPF test helpers; LocalDevelopment.md; Distribution.md; this verification report. Logs and temporary build evidence are ignored under artifacts, not application source. After checking the exact test process identities had exited, only the owned fresh Debug source/output copy and its isolated temporary profile were removed; baseline/build/identity evidence was retained. Unrelated artifacts and user storage were preserved.
