# Development and local operations

## Toolchain and verification

SDK 10.0.401 was observed with `dotnet --info` before pinning. Stable package versions are centralized in `Directory.Packages.props`; EF Core SQLite and design tooling use 10.0.12. Dependencies are restored from NuGet; the application itself needs no internet or exchange account to run.

Windows, including WPF:

```powershell
./scripts/verify.ps1
```

Equivalent commands:

```powershell
dotnet restore ArbitrageTrading.sln
dotnet build ArbitrageTrading.sln -c Release --no-restore
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=verification'
```

Linux/backend-only:

```bash
bash scripts/verify-backend.sh
dotnet run --project src/Arbitrage.Backend --no-launch-profile
```

The backend script restores/builds the backend project, then restores/tests the three non-WPF test projects. It never loads the WPF project. Windows solution verification includes dedicated STA WPF tests and the real-process SignalR/WebSocket test. CI has separate Windows and Linux jobs. Compilation and automated WPF resource tests do not replace an actual visual review; see `Phase01BVerification.md` and `Phase01CVerification.md` for checks actually executed.

Integration fixtures use unique temporary disk SQLite databases, apply the real migration, and authenticate using the protected runtime file with the production handler. Extra identities are inserted only in fixture code. Reverse-direction ownership checks invoke the production store with the second fixture actor, without exposing a production impersonation mechanism.

## Independent startup and configuration

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile
dotnet run --project src/Arbitrage.Desktop
```

Override non-secret local options with standard .NET configuration, for example:

```powershell
$env:Local__DataDirectory = Join-Path $env:LOCALAPPDATA 'ArbitrageTrading-dev/backend'
$env:Local__RuntimeDirectory = Join-Path $env:LOCALAPPDATA 'ArbitrageTrading-dev/runtime'
$env:Local__BaseUrl = 'http://127.0.0.1:5275'
dotnet run --project src/Arbitrage.Backend --no-launch-profile
```

In the desktop terminal, set `ARBITRAGE_RUNTIME_DIRECTORY` to that same absolute runtime directory before starting Desktop. Set `ARBITRAGE_DESKTOP_DIRECTORY` to an absolute desktop directory when isolating preferences and desktop logs; otherwise they live at `%LOCALAPPDATA%\ArbitrageTrading\desktop`. The versioned `preferences.json` stores only Dark, Light, or System. First run selects System. System follows the Windows **applications** theme; if detection fails it displays Light while keeping System saved. Windows High Contrast overrides the palette while active and does not change the saved choice. A failed preference save leaves the chosen theme in this session with a visible warning. The URL comes from validated protected metadata, not an arbitrary remote endpoint. Storage must be outside any Git checkout/worktree. Keep backend storage, desktop preferences/logs, and runtime metadata in distinct locations. Never put a credential in an environment variable or command argument. Do not use `ASPNETCORE_URLS`, `DOTNET_URLS`, HTTP/HTTPS port overrides, or Kestrel endpoint sections: the backend rejects them. Containers and reverse proxies are outside this local-only deployment.

For an isolated Windows smoke run, choose a new empty temporary root and an unused loopback port. Use **the same shell environment** for the backend and desktop, and set all paths before starting either process:

```powershell
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("ArbitrageTrading-smoke-" + [guid]::NewGuid().ToString('N'))
$env:Local__DataDirectory = Join-Path $smokeRoot 'backend'
$env:Local__RuntimeDirectory = Join-Path $smokeRoot 'runtime'
$env:ARBITRAGE_RUNTIME_DIRECTORY = $env:Local__RuntimeDirectory
$env:ARBITRAGE_DESKTOP_DIRECTORY = Join-Path $smokeRoot 'desktop'
$env:Local__BaseUrl = 'http://127.0.0.1:5276' # replace if this port is in use
dotnet run --project src/Arbitrage.Backend --no-launch-profile
```

After the backend starts, launch `dotnet run --project src/Arbitrage.Desktop` from another terminal with those **same** environment values. Stop only the processes you started, then remove only that smoke root after verifying its resolved absolute path is the newly created temporary directory. A disconnected-startup check can launch Desktop first with the same isolated values.

## Schema lifecycle

A missing database receives the initial EF migration and atomic local ownership initialization. A database with pending migrations requires explicit `--migrate`; a database containing unknown/newer migrations fails. There is no EnsureCreated, reset command, memory fallback, or destructive startup recovery.

Phase 02A adds the `PublicMarketCatalog` migration. Existing installations must stop the backend, back up the full backend directory, then run the same `--migrate` command below before normal startup. The migration adds catalog, tag, and run tables without replacing identity, workspace, settings, or audit records. A stopped/failed run retains previously saved pages; old Running records become Interrupted on the next backend start. No job automatically resumes.

To upgrade: stop all backends using the directory, back up the entire backend directory including SQLite sidecars while stopped, then:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile -- --migrate
```

Use the same non-secret directory configuration for upgrade and normal startup. The migration command holds the same exclusive storage locks, applies migrations, verifies/initializes ownership, then exits. It does not publish credentials or run HTTP. After a failed migration, preserve the database and backup, inspect the schema/migration history, and repair deliberately; never delete data to make health checks pass. In particular, existing user/workspace data with a missing LocalProfile needs authorized recovery, not automatic ownership reassignment.

Migration authoring:

```powershell
dotnet tool restore
dotnet ef migrations add DescriptiveName --project src/Arbitrage.Infrastructure --output-dir Migrations
dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure
```

The design-time factory is for schema generation only and uses a temporary placeholder path. Use the backend `--migrate` command for actual local databases so the exclusive lease and ownership checks are enforced. Do not run design-time `database update` against production storage.

## Failure behavior and API

Startup exits nonzero on invalid deployment/execution/binding configuration, storage permissions, lock conflicts, corrupt storage, or required migration. Raw exception messages are intentionally suppressed to avoid leaking secrets from configuration. Check each listed category, verify per-user permissions, and stop the other backend if present. A leftover lock filename is harmless; the open file handle is the lease. Never remove/replace a live backend's data directory.

All `/api/v1` routes require Bearer authentication: `system/status`, `session`, `exchanges/status`, `trading/mode`, and GET/PUT `workspaces/{workspaceId}/settings`. PUT accepts only `{ "displayName": "Name" }`, trims whitespace, rejects blank/control/overlong names, and audits successful changes atomically. Server-generated correlation IDs accompany responses. Invalid names are 400, missing/invalid credentials 401, inaccessible workspaces 404, and storage failures 503. No endpoint can activate live execution. Phase 04A adds separately confirmed paper-only endpoints, documented in PaperExecution.md.

## Phase 01C realtime and local backend controls

The authenticated hub is `/hubs/v1/application`. Its only subscription method, `SubscribeDefaultWorkspace`, resolves the local profile and checks membership; it acknowledges the backend instance and workspace. The desktop then fetches the authoritative versioned snapshot at `GET /api/v1/workspaces/{id}/snapshot`. Successful workspace writes publish scoped `StateInvalidated` notifications **after** commit. The hub also carries selected structured `BackendDiagnostic` events and an `ApplicationHeartbeat`. Recent diagnostics are available through `GET /api/v1/workspaces/{id}/diagnostics?after={sequence}&take={1..100}`. History is per workspace and per backend instance; it reports oldest/newest sequence, retention gaps, and dropped live-queue count. It is not SQLite audit, a rolling-log parser, or durable event delivery.

The backend heartbeat defaults to 15 seconds (`Local__HeartbeatSeconds`, valid 10–300). SignalR keepalive is 10 seconds and client timeout is 35 seconds. Desktop marks an application heartbeat stale after 45 monotonic seconds (`ARBITRAGE_HEARTBEAT_STALE_SECONDS`, valid 30–900). A consistency snapshot every 60 seconds (`ARBITRAGE_CONSISTENCY_SECONDS`, valid 30–300) recovers missed invalidations without rapid HTTP polling. Connecting, synchronizing, connected, reconnecting, authentication failure, and access denial are separate from local process state. Reconnect reauthenticates, resubscribes, and fetches a new snapshot before claiming synchronization. State and diagnostics are eventual; queue overflow or expired history shows a gap notice. No notification is an order acknowledgment or trading execution signal.

**Start** explicitly launches a built backend executable configured by the absolute `ARBITRAGE_BACKEND_ARTIFACT` path (or one placed beside the desktop executable). A built DLL requires an absolute `ARBITRAGE_DOTNET_HOST`. Start never restores/builds, invokes a shell, or downloads an artifact. It passes the configured data/runtime paths, `Local__BaseUrl`, and managed-local opt-in as process environment values. A profile launch lock serializes concurrent desktop Start clicks; the backend's existing data/runtime leases still enforce single-instance ownership. Protected `runtime/managed-local.json` records the instance, profile, process ID/start time, and artifact after authenticated readiness. A later desktop can reattach and Stop only after verifying those values. An externally started backend is observable but not stoppable through Desktop. Stop requires confirmation, backend authentication, the current instance ID and local owner authority; its acknowledgment means **requested**, while Desktop waits separately for actual process exit. Refresh only observes and resynchronizes. Closing Desktop never sends Stop; neither reconnect nor retry launches an OS process. There is no Force Kill control.

For a safe two-desktop manual check, build Release first, then choose a fresh temporary root and unused port. In one PowerShell terminal from the repository root:

```powershell
dotnet build ArbitrageTrading.sln -c Release
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("ArbitrageTrading-01C-manual-" + [guid]::NewGuid().ToString('N'))
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

Open a second PowerShell terminal with the **same** data/runtime/URL/artifact values, but set `ARBITRAGE_DESKTOP_DIRECTORY` to `$smokeRoot\desktop-two` before launching Desktop. The second client shares the backend profile, not desktop preferences. Start once from either client; the other should connect automatically. Change the workspace name in one client and check the other without pressing Refresh. Confirm Stop from the managed client and check both clients' state. Close both desktops before deleting only the verified temporary root. Do not point these commands at the default runtime directory or remove a live backend's storage.

The local profile currently has one owner and no user-management or revocation operation. Future server identity work must add a hook to remove/terminate live subscriptions on revocation. Backend diagnostic retention is 256 events per workspace and the outgoing queue is capped at 256; the desktop retains at most 200 backend and 200 local events. Live delivery can be missed; the snapshot and recent history recover what is still available, with explicit gap/restart notices when complete recovery is impossible.
