# Phase 04H.5.1 — Managed ownership and Stop verification

## A–I. Baseline and reproduced cause

Repository: `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on `main`, starting HEAD `d23b66bc8ee9548c1b81fa42cb9ead3936f08d55`, initially clean. Exact-head [Actions run 37100695630](https://github.com/yovko93/ArbTrading/actions/runs/37100695630) completed successfully. Root AGENTS.md and the Phase 04H.5 report were inspected. No branch, remote or history changes were made.

Before any runtime mutation, there were **zero** Arbitrage Desktop/Backend processes and no listener on port 5274. The default runtime retained `connection.json`, `backend.lock` and `desktop-start.lock`; `managed-local.json` was **absent**. There was therefore no management JSON to deserialize, no management-file owner/ACL to validate, and no metadata-versus-live-process comparison to perform. `IManagedProcessInspector.Inspect` is not invoked when metadata is absent. The protected connection file passed the existing `ProtectedStorage.VerifyPrivateFile` implementation, including owner/ACL and reparse-point checks. No credentials were printed. The runtime directory had the current user's owner and protected ACL. No applicable environment overrides were present.

The configured normal paths were:

- BaseUrl: `http://127.0.0.1:5274`.
- DataDirectory: `C:\Users\Yovko\AppData\Local\ArbitrageTrading\backend`.
- RuntimeDirectory: `C:\Users\Yovko\AppData\Local\ArbitrageTrading\runtime`.
- Artifact: `C:\Users\Yovko\source\repos\ArbTrading\src\Arbitrage.Backend\bin\Release\net10.0\Arbitrage.Backend.exe`.

The existing SQLite database was about 360 MB. Read-only inspection found zero reliability campaigns and zero Auto Paper profiles. No financial command was used during the reproduction.

The unmodified Release Desktop was launched with those defaults. Actual keyboard activation of its Start button launched PID **17508**, parent Desktop PID **17052**, with exact OS start time **2026-10-03T07:48:36.9702922Z** and the expected executable. Start expired at its former 20-second readiness deadline and displayed Unknown with the explicit readiness-not-confirmed result. The process remained alive. Its logs recorded database-current at 10:48:48.624 +03:00 and backend-ready at **10:49:28.206 +03:00**, about **51.24 seconds after process creation**.

Realtime subsequently connected. Actual Refresh changed lifecycle status to **Running / ExternalUnmanaged** and reproduced the exact original text: “Backend is reachable, but local management ownership is unverified. Stop is unavailable.” Stop was disabled; there was no authorized Stop request. Authenticated snapshot identity was backend `165d19fd-adc7-4a41-a89b-b0ad68e1bda5`, local profile `acd68f6b-404a-4d5b-90ba-10b998dd9b9b`, workspace `1680e7cb-5915-4ffb-90fd-bfd00b7e626e`. Paper was available and all three live execution capabilities were false.

There was still exactly one backend, with the same PID/start time/executable/parent, and port 5274 belonged to it. No replacement or duplicate was observed. Management metadata remained absent throughout. The exact ObserveAsync branch was authenticated endpoint success, `metadata == null`, `metadataExists == false`, no process inspection, followed by the reachable-external result. All metadata identity comparisons were **not applicable**, not mismatches, because no record had been written.

**Proven cause:** startup exceeded the former readiness deadline before ownership registration. The existing code wrote management metadata only after authenticated readiness; timeout abandoned registration while leaving the child alive. This reproduced the reported end state on the user's existing profile. It did **not** reproduce a successful ManagedLocal Start later losing its record during Stop. The original historical click sequence cannot be independently reconstructed because that process and record were already gone when investigation began. No premature metadata deletion was found.

Only after preserving these facts was the exact reproduction process gracefully stopped through its authenticated instance-specific endpoint, with PID, full-precision start time, executable, parent, port owner and instance all checked and its OS handle retained. One initial diagnostic guard rejected a timestamp parsed using local-time conversion before sending anything; using an explicit UTC DateTimeOffset resolved that tooling error. Exit was confirmed without force kill or fabricated metadata. The reproduction Desktop was closed. Safe baseline evidence is in ignored `artifacts/04H51-baseline-evidence.json`.

## J–P. Correction and regression coverage

The production correction is confined to Desktop's `LocalBackendController`:

- Increase authenticated readiness from 20 seconds to a bounded **two minutes**, based on the measured 51-second persistent-profile startup. Package migration and shutdown retain their separate existing bounds. The UI remains busy/Starting while readiness is checked. Exhausting the new bound still fails closed and explicitly says ownership was not recorded.
- Report sanitized missing/invalid metadata, exited/unverified process, start-time, artifact, backend instance, profile, workspace, data-directory, BaseUrl and PID mismatch categories. Paths and credentials are not included in these normal UI reasons.
- Preserve the existing Stop sequence: protected record, stored identity, OS process inspection and retained handle, fresh authenticated observation, instance-specific request, confirmed exit, then cleanup. All three cleanup calls remain after confirmed/already-established exit.
- Compare the entire retained metadata record during cleanup rather than its instance GUID alone, preserving a record that changed concurrently even if its instance GUID is unchanged.

No ownership is inferred from loopback reachability. Missing metadata remains external/unmanaged and is not recreated from an endpoint. Existing protected metadata is sufficient for another Desktop to reattach only when process and authenticated identities match. Realtime does not write/remove management metadata. No generic kill logic was added.

Focused controller coverage includes a held process-exit wait proving that metadata survives both request acceptance and the shutdown interval; failed/lost-ack/timeout retention; removal after confirmed exit; preservation of changed records; stale exited-process handling; PID/instance/profile/workspace/path/URL rejection; missing/invalid JSON; and real OS start-time/artifact mismatch checks against a live process without terminating it.

The WPF integration test uses actual MainWindow ButtonAutomationPeer invocation with real backend executables in development and manifest-backed layouts. It asserts protected metadata creation, byte-for-byte metadata continuity through Refresh, unchanged PID/instance and ManagedLocal capability, then closes and disposes the original shell/realtime/controller-facing state. A new controller and new shell/view models must reattach from the existing file before the actual Stop button is enabled and invoked. A second controller independently observes the same process. Confirmed exit removes metadata; Refresh never starts a backend. An additional case withholds readiness responses for 25 seconds to cover the old deadline failure deterministically, while still launching and authenticating a real backend.

An initial computer-use launch normalized the executable path to lowercase, exposing the pre-existing case-sensitive development-layout recognition; that launch showed disabled Start and launched no backend. The reproduction above used the original filesystem spelling. This phase does not broaden into artifact-discovery changes. Native screenshots timed out; normal-window accessibility and keyboard input were usable after reselecting/activating the window.

## Q–W. Verification

Release build: **0 warnings, 0 errors**. Focused controller tests: **25 passed**; focused WPF/view-model tests: **8 passed**, including delayed readiness, development and packaged backend runs, full view-model/controller recreation, and Light/Dark cases. EF reports no pending model changes. The transitive NuGet audit reports no vulnerable packages. Focused signing tests passed, including native tamper rejection and ephemeral certificate/private-key cleanup. Final normal-profile Stop, full-suite and Unsigned distribution results will be recorded after completion.

The corrected normal-profile Start launched PID **7608**, parent Desktop **1420**, at **2026-10-03T08:10:50.2366735Z**; readiness was logged at 11:11:20.765 +03:00 (about **30.53 seconds**, still beyond the former limit). Actual UI reached Running / ManagedLocal / Connected. Backend instance: `8138b459-3da5-414c-b841-9c4432fc3ecc`; profile/workspace were unchanged. Management JSON deserialized, the existing protected-storage verifier accepted it, owner was the current user, no reparse point existed, and the production OS-inspector source returned **Running**. Every comparison was **MATCH**: PID, full-precision start time, executable/artifact, data directory, BaseUrl, backend instance, profile and workspace.

Actual Refresh retained all those matches. Closing Desktop 1420 left the backend and metadata alive. Reopened Desktop **21876**, with a new controller and realtime session, restored Running / ManagedLocal / Connected and enabled Stop. Safe stage evidence is in ignored `artifacts/04H51-BeforeStart.json`, `04H51-AfterStart.json`, `04H51-AfterRefresh.json`, and `04H51-BeforeStop.json`. The diagnostic helper compiles the existing protected-storage and OS-inspector source verbatim and emits only selected non-secret fields.

Native Stop opens the existing confirmation with all lifecycle controls disabled while it is pending. The UI automation helper repeatedly cancelled rather than selected Yes; cancellation truthfully preserved Running / ManagedLocal and the management record. Assistance selecting Stop → Yes was requested from the user. This pending native confirmation is not counted as successful Stop evidence; the isolated actual-WPF Stop tests did pass. The full gate has not started while that normal-profile acceptance step is pending.

The full verification will run once through canonical Unsigned publishing, which invokes the unchanged `scripts/verify.ps1` (restore, Release build, complete solution tests) before packaging and extracted smoke. Focused TestEphemeral signing checks are used instead of a second full signed-package pipeline. No canonical test gate will be bypassed.

## X–AA. Scope and safety

No backend financial/trading implementation changed: ledger, balances, risk, fees, monitoring, opportunity evaluation, automation, settlement, reliability criteria and acquisition are untouched. No EF migration is added. Live execution remains unavailable. No generation reset, monitoring start, automation arm, campaign, settlement or BurnIn was triggered. Existing paper-table row hashes/counts are being compared across the final normal-profile lifecycle check. Changes remain uncommitted; no push or PR.
