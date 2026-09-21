# Phase 01C.2 access invalidation verification

Date: 2026-09-22. The existing checkout at `C:\Users\Yovko\source\repos\ArbTrading` started clean on `main` at `a83e5ecef58ca0976369550b0aa22d6c72bfc061`. No branch, remote, history, or GitHub publication was changed. Changes remain uncommitted.

## Reproduction and correction

All three new production-path regression cases failed before the correction. With an authenticated real SignalR session, a Save returning 401 allowed an otherwise valid, held snapshot from the same backend instance to restore cleared private state. A Save returning 403 likewise allowed already queued private UI work to restore state. A delayed Save denial from connection A also cleared a freshly authorized connection B. These tests invoke the real `SaveCommand`, `MainViewModel`, and `RealtimeSession`; they do not directly invalidate access or substitute an authorization flag.

`MainViewModel.ClearPrivateState` now synchronously notifies `RealtimeSession` of confirmed access denial before clearing private fields. Realtime invalidates its generation, pauses recovery, and wakes its loop without waiting for dispatch or disposing the hub on the Save stack. Queued mutations check access validity and generation at execution, alongside the existing instance/workspace checks. Each fresh authenticated subscription advances the view-model command generation, rejecting delayed completions from a superseded connection, including reconnects to the same identity. Explicit Refresh establishes a new subscription and authoritative snapshot. The existing bounded credential reread/retry remains; recovery does not replay Save or start/stop a backend process.

Changed files:

- `src/Arbitrage.Desktop/ViewModels/MainViewModel.cs`: synchronous access-invalidated event and fresh-subscription command epoch.
- `src/Arbitrage.Desktop/Services/RealtimeSession.cs`: event wiring, generation invalidation, guarded private/status dispatch, and preservation of denial while the old connection closes.
- `tests/Arbitrage.Desktop.Tests/RealtimeProcessTests.cs`: partial declaration to reuse the existing isolated process helpers.
- `tests/Arbitrage.Desktop.Tests/SaveAccessInvalidationTests.cs`: three bounded regressions using controlled HTTP timing, a queued dispatcher, and real authenticated Kestrel/SignalR connections. They cover held snapshots, queued diagnostics, stale Save failures, explicit recovery with backend diagnostics, local diagnostic retention, and draft preservation through a transient interruption.
- This verification record.

The diagnostic cursor and process-handle implementations, backend APIs, packages, migrations, UI, and themes were not changed.

## Actual checks

Targeted regressions passed after the correction: **3 passed, 0 failed, 0 skipped**. Final solution checks:

```powershell
dotnet restore ArbitrageTrading.sln
dotnet build ArbitrageTrading.sln -c Release --no-restore
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore
git diff --check
```

Normal audit-enabled restore succeeded with all projects up to date. No audit setting was changed; a fresh vulnerability assessment was not separately verified. Release build: **0 warnings, 0 errors**. Full Windows suite: **107 passed, 0 failed, 0 skipped** (Domain 8, Application 5, Backend Integration 86, Desktop 8). Whitespace validation passed.

The repository-required `scripts/verify.ps1` also completed successfully, repeating audit-enabled restore, Release build, and all 107 tests and writing ignored TRX reports.

Process tests used unique temporary database, runtime/credential, log, and desktop roots and free loopback ports. The new tests preserve the actual same-instance snapshot payload and deliver it after the denial; queued actions execute their production guards. Recovery stays on the same backend instance. Cleanup targets only the isolated roots and owned processes with verified identities.

Linux/backend CI, screenshots, DPI, accessibility, and mouse-driven WPF checks were not run for this patch. Prior visual limitations remain. Diagnostic history remains bounded and process-local. Paper remains the effective environment; exchange connectivity and execution remain unavailable. No commit, push, merge, or pull request was made.
