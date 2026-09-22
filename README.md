# Arbitrage Trading

Phase 01A established persistent local identity and workspace ownership, authenticated loopback HTTP, and audited workspace-name updates. Phase 01B added the WPF workstation shell and Dark/Light/System themes. Phase 01C adds authorized realtime invalidation, automatic reconnect and resynchronization, recent backend diagnostics, and explicit **Local Backend** Start/Stop/Refresh controls. Phase 02A adds read-only public market discovery for Polymarket and Kalshi and an offline-capable local Market Explorer.

Public metadata discovery uses separate exchange HTTPS clients and requires no exchange credentials. There is no exchange account connection, orderbook feed, order submission, AI call, arbitrage algorithm, or paper fill simulator. Paper is the only supported environment, and all execution capabilities are explicitly unavailable. No balances or profits are fabricated.

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

Build the backend artifact before using the desktop's **Start** control. For development, set `ARBITRAGE_BACKEND_ARTIFACT` to the absolute path of the Release backend executable; a published installation can place that executable beside the desktop executable. Opening Desktop never launches Backend automatically.

The two applications can still be started independently in separate terminals:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile
dotnet run --project src/Arbitrage.Desktop
```

The backend listens at `http://127.0.0.1:5274` by default. Desktop automatically connects to an already running backend and retries transient interruptions. **Start** explicitly launches the configured artifact when no backend is verified. **Stop** requires a verified managed-local instance and confirmation; an externally started backend remains observable but cannot be stopped from Desktop. **Refresh** observes and resynchronizes without launching or terminating a process. Closing Desktop does not stop Backend. Use Ctrl+C for an externally started backend.

Open **Market Explorer** to browse locally stored metadata. Select Polymarket, Kalshi, or All, then use **Sync Markets** to start an explicit backend-owned public discovery run. **Cancel Sync** settles the selected active run. The header **Refresh** reloads local state and catalog pages; it never starts exchange discovery. The default scope covers all categories in Polymarket's `closed=false` keyset and Kalshi's `unopened`, `open`, and `paused` listings. Historical settled-market backfill is deferred. A run that hits its configured time budget or an upstream limit is labeled Partial; cached records remain available without internet while the backend is running. Counts distinguish stored markets from markets observed in a run. See [exchange integration](docs/Development/ExchangeIntegration.md) for source contracts and [Phase 02A verification](docs/Development/Phase02AVerification.md) for actual results.

See the separate [Phase 01A](docs/Development/Verification.md), [Phase 01B](docs/Development/Phase01BVerification.md), and [Phase 01C](docs/Development/Phase01CVerification.md) verification records for actual results and remaining visual checks.

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
| Arbitrage.Connectors | Public, read-only Polymarket and Kalshi market-list adapters |
| Arbitrage.Strategies / Execution | Documented deferred module boundaries |

`tests/` contains Domain, Application, Backend.IntegrationTests, and Windows-only Desktop.Tests projects. Integration tests use temporary real SQLite databases and the production authentication handler. `src/Shared/LocalConnectionFile.cs` is a small source-linked OS adapter shared by Infrastructure and Desktop; it creates no business-layer dependency from Desktop.

## Runtime locations and maintenance

On Windows the application root is `%LOCALAPPDATA%\ArbitrageTrading`:

| Location | Contents |
|---|---|
| `backend/arbitrage.db` | Persistent identity, membership, workspace settings, audit, public catalog, discovery runs, migration history |
| `backend/logs/` | Backend rolling logs |
| `desktop/logs/` | Desktop rolling logs |
| `desktop/preferences.json` | Versioned per-user appearance preference, separate from backend settings |
| `runtime/connection.json` | Protected loopback address and rotating local credential; never share or commit |
| `runtime/managed-local.json` | Protected identity of a desktop-launched backend; no credential is stored here |
| `backend/backend.lock`, `runtime/backend.lock` | Exclusive backend leases; file presence alone does not mean a backend is running |

On Linux, paths derive from .NET's `LocalApplicationData` location (normally `$HOME/.local/share/ArbitrageTrading`). Use a private local disk filesystem with reliable file locks and OS permissions; network/shared filesystems are unsupported.

For a new directory, migration and ownership initialization happen automatically. Existing databases are never deleted or recreated. If an upgrade is needed, stop the backend, back up the complete backend directory, and run:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile -- --migrate
```

This explicitly migrates and verifies local ownership, then exits without serving HTTP. See [development and operations](docs/Development/LocalDevelopment.md) for configuration, migration authoring, verification, and recovery; [local security](docs/Architecture/LocalSecurity.md) for the trust boundary; and [identity/server ADR](docs/Architecture/ADR-001-LocalIdentityAndServerMigration.md) for future ownership and deployment rules.

## Next phases

Phase 01B provides full desktop navigation and exactly **Dark, Light, System** theme preferences. Phase 01C provides a workspace-authorized SignalR notification channel, versioned REST snapshots, bounded backend diagnostic history, reconnection, and explicit local backend process controls. This is eventual state synchronization, not durable event delivery or a trading execution bus. Backend rolling log files are not streamed; the diagnostics page displays selected structured backend events and separate desktop-local events. See [roadmap](docs/Architecture/Roadmap.md).
