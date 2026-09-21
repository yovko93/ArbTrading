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
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=phase01a'
```

Linux/backend-only:

```bash
bash scripts/verify-backend.sh
dotnet run --project src/Arbitrage.Backend --no-launch-profile
```

The backend script restores/builds the backend project, then restores/tests the three non-WPF test projects. It never loads the WPF project. CI has separate Windows and Linux jobs. WPF compilation is not a UI smoke check; see `Verification.md` for checks actually executed.

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

In the desktop terminal, set `ARBITRAGE_RUNTIME_DIRECTORY` to that same absolute runtime directory before starting Desktop. The URL comes from validated protected metadata, not an arbitrary remote endpoint. Storage must be outside any Git checkout/worktree. Keep backend storage, desktop preferences/logs, and runtime metadata in distinct locations. Never put a credential in an environment variable or command argument. Do not use `ASPNETCORE_URLS`, `DOTNET_URLS`, HTTP/HTTPS port overrides, or Kestrel endpoint sections: the backend rejects them. Containers and reverse proxies are outside this local-only deployment.

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
