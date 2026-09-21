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

The backend script restores/builds the backend project, then restores/tests the three non-WPF test projects. It never loads the WPF project. Windows solution verification includes the dedicated STA WPF tests. CI has separate Windows and Linux jobs. Compilation and automated WPF resource tests do not replace an actual visual review; see `Phase01BVerification.md` for checks actually executed.

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

All `/api/v1` routes require Bearer authentication: `system/status`, `session`, `exchanges/status`, `trading/mode`, and GET/PUT `workspaces/{workspaceId}/settings`. PUT accepts only `{ "displayName": "Name" }`, trims whitespace, rejects blank/control/overlong names, and audits successful changes atomically. Server-generated correlation IDs accompany responses. Invalid names are 400, missing/invalid credentials 401, inaccessible workspaces 404, and storage failures 503. No endpoint can activate execution.

For desktop smoke testing: open with backend stopped; verify Disconnected; start backend and Refresh; compare displayed values with API; rename and Refresh; restart backend and Refresh to verify stable IDs and new credential; close desktop and check liveness remains available. Use an isolated test data/runtime directory and avoid displaying the credential file.
