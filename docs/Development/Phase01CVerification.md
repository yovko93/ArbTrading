# Phase 01C verification record

Implementation date: 2026-09-22. Existing checkout: `C:\Users\Yovko\source\repos\ArbTrading`, branch `main`. The checkout was clean before this phase; Phase 01A and 01B records remain in `Verification.md` and `Phase01BVerification.md`. No branch, remote, or history was changed, and no commit, push, or pull request was made.

## Delivered behavior

The backend exposes an authenticated SignalR hub at `/hubs/v1/application`. `SubscribeDefaultWorkspace` resolves the local actor on the server, verifies ownership through the existing workspace service, and returns the new per-process backend instance ID plus the authorized workspace ID. There is no client-selected group or publishing method. The existing local Bearer credential is sent in an authorization header, including during WebSocket negotiation; the URL contains no credential.

`GET /api/v1/workspaces/{id}/snapshot` is the version 1 authoritative desktop snapshot. It includes capture time, backend instance and local profile IDs, actual session/workspace/system state, Paper as effective environment, and the two NotImplemented exchange statuses. `GET /api/v1/workspaces/{id}/diagnostics?after={sequence}&take={1..100}` returns scoped structured history with instance, oldest/newest sequence, retained events, gap indication, and dropped live-queue count. Both routes use the existing authentication and workspace authorization; inaccessible workspaces return 404. Successful workspace writes enqueue `StateInvalidated` after the database commit. Failed writes do not emit a successful change notification. If enqueue/delivery fails after commit, the write remains committed and a later snapshot can recover it.

One desktop-wide realtime owner registers handlers, authenticates, subscribes, loads the snapshot, and then marks the application synchronized. Notifications coalesce into serialized snapshot refreshes, including a second fetch when an invalidation arrives during a fetch. Old connection generations and backend instances cannot apply stale responses. Reconnect and backend restart reauthorize and resubscribe. The header, Dashboard, Settings, and Diagnostics share this state. An unsaved Settings draft survives an incoming server change and displays a conflict notice; identity changes clear private state. Navigation and theme changes reuse the same connection.

Application heartbeats default to 15 seconds (`Local__HeartbeatSeconds`, 10–300). SignalR keepalive is 10 seconds and its server client timeout is 35 seconds. Desktop heartbeat freshness expires after 45 monotonic seconds (`ARBITRAGE_HEARTBEAT_STALE_SECONDS`, 30–900). A configurable 60-second snapshot consistency check (`ARBITRAGE_CONSISTENCY_SECONDS`, 30–300) recovers missed invalidations. Retries use one cancellable loop with exponential backoff capped at 30 seconds and small jitter; authentication or authorization denial pauses blind retries until Refresh or a newly read protected credential is validated. Transport connectivity, application synchronization, heartbeat freshness, and local process state have separate indicators.

Backend diagnostic history retains 256 sanitized events per workspace per process. A bounded 256-item outgoing queue never blocks a committed workspace write; overflow can drop live messages. Desktop retains up to 200 backend and 200 local events, deduplicates history/live overlap, and shows retention gaps and backend restarts. Diagnostic history is memory-only and resets on backend restart. It is neither the SQLite audit trail nor a durable ledger. This UI notification channel is not a trading execution bus or order acknowledgment mechanism. The local profile has no revocation operation yet; future multi-user access changes must remove or terminate live subscriptions on revocation.

The WPF header has explicit **Start**, **Stop**, and **Refresh** controls. Start requires a built artifact and an unoccupied configured local endpoint; it launches only after a click, under a cross-desktop launch lock, then records protected management metadata only after authenticated readiness. Stop requires confirmation, a matching managed instance/process/profile, local owner authentication, and a successful stop acknowledgment. It separately waits for actual process exit and never force-kills in production. A reachable externally launched backend is shown but cannot be stopped from Desktop. Refresh observes/resynchronizes without starting a process. Closing Desktop leaves the backend running. No startup, reconnect, or retry path launches a process.

## Automated verification

Run from the repository root:

```powershell
dotnet restore ArbitrageTrading.sln -p:NuGetAudit=false
dotnet build ArbitrageTrading.sln -c Release --no-restore
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore
git diff --check
```

The cached package restore used `NuGetAudit=false` for this run because this environment could not reach NuGet's vulnerability service. It did not change repository-wide audit settings. The Release build passed with **0 warnings and 0 errors**. The full Windows test run passed with **90 passed, 0 failed, 0 skipped** (Domain 8, Application 5, Backend Integration 72, Desktop 5). The Windows ACL tests require permission to set private ACLs on their unique temporary directories. `git diff --check` passed. Linux/backend-only CI was not run here; the real-process WPF test is in the Windows desktop test project, leaving the independent backend test project free of WPF test execution.

The real-process test launches a built Release backend on a free loopback port with unique SQLite, runtime/credential, backend-log, and desktop roots. It uses two actual SignalR .NET clients over WebSockets, exercises production negotiate/authentication and workspace subscription, verifies both receive a committed workspace invalidation, and observes the desktop state update without Refresh. It checks anonymous/invalid credential rejection, inaccessible snapshot/history routes, stale-instance stop rejection, failed-write notification absence, authenticated managed Stop and actual process exit, restart with new instance and credential, old-credential rejection, reattachment, concurrent Start serialization, and absence of credentials from representative payloads and backend logs. Existing integration tests cover forged request identity and workspace ownership. Deterministic tests cover bounded retry/shutdown, diagnostic scope/retention/queue overflow, overlap deduplication, draft preservation, and identity changes. The test's cleanup tracks only its own process ID, start time, and artifact path.

## Isolated manual two-client check

Build Release, then run this in a PowerShell terminal at the repository root. The port is chosen from a free loopback socket; if another process takes it before Start, choose a new root and port. Do not use your normal runtime paths.

```powershell
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("ArbitrageTrading-01C-" + [guid]::NewGuid().ToString('N'))
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$env:Local__DataDirectory = Join-Path $smokeRoot 'backend'
$env:Local__RuntimeDirectory = Join-Path $smokeRoot 'runtime'
$env:ARBITRAGE_RUNTIME_DIRECTORY = $env:Local__RuntimeDirectory
$env:ARBITRAGE_DESKTOP_DIRECTORY = Join-Path $smokeRoot 'desktop-one'
$env:Local__BaseUrl = "http://127.0.0.1:$port"
$env:ARBITRAGE_BACKEND_ARTIFACT = (Resolve-Path 'src/Arbitrage.Backend/bin/Release/net10.0/Arbitrage.Backend.exe').Path
dotnet run --project src/Arbitrage.Desktop -c Release --no-build
```

Launch a second Desktop from another terminal with the same absolute data/runtime/URL/artifact values and `ARBITRAGE_DESKTOP_DIRECTORY` set to `$smokeRoot\desktop-two` (copy the generated root and URL values; shell variables do not transfer between terminals). Both Desktop directories must be distinct. Verify disconnected startup, click Start once, wait for both clients to synchronize, change the workspace name in one client, and observe the other without Refresh. Inspect actual backend diagnostics, navigate and switch themes while connected, click Stop with confirmation, wait for exit/stale indicators, then Start again and check recovery. Close both desktops and stop any backend started by this check before removing only the validated temporary root. Do not delete live storage.

## UI and platform limits

The WPF resource/navigation tests passed. A Phase 01C isolated smoke check launched a real backend and WPF desktop under unique backend/runtime/desktop roots and a free loopback port. Desktop remained alive during startup; after that exact desktop process was closed, the backend process remained alive. The smoke check stopped only its own verified processes and removed only its validated temporary root. This proves process-lifetime independence, not Start/Stop button interaction. The earlier Phase 01B disconnected-startup smoke check remains recorded separately. This phase did not complete screenshot, DPI, accessibility, or mouse-driven visual inspection; the earlier Windows capture helper failed with `SetIsBorderRequired: No such interface supported (0x80004002)`. The two-desktop control sequence above is a manual checklist, not a claim of agent-driven UI verification. Linux build/test and remote CI were not executed. Backend diagnostic delivery is best effort and process-local; a low-frequency snapshot corrects state after a missed invalidation. No exchange connectivity, order submission, or paper simulator was enabled.
