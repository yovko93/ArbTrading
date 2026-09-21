# Arbitrage Trading

Phase 01A established persistent local identity and workspace ownership, authenticated loopback HTTP, and audited workspace-name updates. Phase 01B adds a WPF workstation shell with ten navigation destinations, actual dashboard/settings/diagnostics state, and Dark, Light, and System appearance preferences.

There is no exchange connection, exchange credential collection, order submission, AI call, arbitrage algorithm, or paper fill simulator. Paper is the only supported environment, and all execution capabilities are explicitly unavailable. No balances or profits are fabricated.

## Requirements and quick start

- .NET SDK **10.0.401** (verified on the implementation machine; pinned in `global.json` with latest-patch roll-forward).
- Windows for the WPF client. The backend and non-WPF tests also target Linux.
- NuGet access for initial restore. No PostgreSQL, Docker, cloud service, account, or exchange credentials are required.

From the repository root:

```powershell
dotnet restore ArbitrageTrading.sln
dotnet build ArbitrageTrading.sln -c Release --no-restore
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore
```

Start the two applications in separate terminals:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile
dotnet run --project src/Arbitrage.Desktop
```

The backend listens at `http://127.0.0.1:5274`. The desktop can open before the backend: use **Refresh** after starting it. Closing the desktop does not stop the backend. Use Ctrl+C in the backend terminal to stop it.

See the [Phase 01A verification record](docs/Development/Verification.md) and [Phase 01B verification record](docs/Development/Phase01BVerification.md) for actual results and remaining visual checks.

The only anonymous endpoint is `GET /health/live`, which returns minimal process liveness. API data requires a per-user credential delivered through protected local storage; no API returns that credential.

## Repository structure

| Project | Responsibility |
|---|---|
| Arbitrage.Domain | Identity, ownership, settings invariants, audit, trading-mode vocabulary |
| Arbitrage.Application | Request actor, workspace use cases and persistence ports |
| Arbitrage.Contracts | Versioned public HTTP DTOs and capabilities |
| Arbitrage.Infrastructure | EF Core SQLite, migrations, atomic initialization, scoped stores, local storage lease |
| Arbitrage.Backend | Independent ASP.NET Core host, local authentication and authorized API |
| Arbitrage.Desktop | WPF, CommunityToolkit.Mvvm, DI and typed HTTP client |
| Arbitrage.Connectors / Strategies / Execution | Documented deferred module boundaries |

`tests/` contains Domain, Application, Backend.IntegrationTests, and Windows-only Desktop.Tests projects. Integration tests use temporary real SQLite databases and the production authentication handler. `src/Shared/LocalConnectionFile.cs` is a small source-linked OS adapter shared by Infrastructure and Desktop; it creates no business-layer dependency from Desktop.

## Runtime locations and maintenance

On Windows the application root is `%LOCALAPPDATA%\ArbitrageTrading`:

| Location | Contents |
|---|---|
| `backend/arbitrage.db` | Persistent identity, membership, workspace settings, audit, migration history |
| `backend/logs/` | Backend rolling logs |
| `desktop/logs/` | Desktop rolling logs |
| `desktop/preferences.json` | Versioned per-user appearance preference, separate from backend settings |
| `runtime/connection.json` | Protected loopback address and rotating local credential; never share or commit |
| `backend/backend.lock`, `runtime/backend.lock` | Exclusive backend leases; file presence alone does not mean a backend is running |

On Linux, paths derive from .NET's `LocalApplicationData` location (normally `$HOME/.local/share/ArbitrageTrading`). Use a private local disk filesystem with reliable file locks and OS permissions; network/shared filesystems are unsupported.

For a new directory, migration and ownership initialization happen automatically. Existing databases are never deleted or recreated. If an upgrade is needed, stop the backend, back up the complete backend directory, and run:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile -- --migrate
```

This explicitly migrates and verifies local ownership, then exits without serving HTTP. See [development and operations](docs/Development/LocalDevelopment.md) for configuration, migration authoring, verification, and recovery; [local security](docs/Architecture/LocalSecurity.md) for the trust boundary; and [identity/server ADR](docs/Architecture/ADR-001-LocalIdentityAndServerMigration.md) for future ownership and deployment rules.

## Next phases

Phase 01B provides full desktop navigation and exactly **Dark, Light, System** theme preferences, persisted per OS user, with dynamic switching and Windows application-theme change handling. When Windows theme detection is unavailable, System uses Light while remaining saved as System; Windows High Contrast overrides appearance without changing that preference. Set `ARBITRAGE_DESKTOP_DIRECTORY` to an absolute path to isolate preferences and desktop logs. Phase 01C adds authorized workspace-scoped SignalR, backend log streaming, and comprehensive reconnect behavior. See [roadmap](docs/Architecture/Roadmap.md).
