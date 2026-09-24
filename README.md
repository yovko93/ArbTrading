# Arbitrage Trading

Phase 04F adds **deterministic adaptive paper sizing** alongside unchanged FixedQuantity automation. An explicitly saved adaptive profile selects the largest admissible quantity on its decimal Min/Max/Step grid using exact cached L2, fees, risk and session budgets. Search is descending, bounded at 256 quantities per opportunity and 512 exact evaluations per cycle, with no monotonicity assumption or portfolio optimization. A financial race rejects the selected quantity without a smaller fallback. Read-only sizing preview is diagnostic and never arms or authorizes execution. See [paper architecture](docs/Architecture/PaperExecution.md) and [Phase 04F verification](docs/Development/Phase04FVerification.md).

Automatic Paper retains the persistent emergency kill switch, monitoring input deduplication and session/hourly/cooldown limits. Configure paper risk first, save an automation profile, start monitoring, then explicitly arm. Mode remains Paper; no real orders are submitted. The backend continues after WPF closes or disconnects. Backend restart always requires explicit rearming; resetting a kill switch never arms.

Phase 04D introduced explicit, workspace-owned **Paper capital and risk policy**. Suggested form values remain inactive until saved. Both manual and automatic entries repeat admission inside the financial SQLite transaction. Existing settlement and valuation remain available; automation never bypasses risk admission. Adaptive sizing uses paper-simulation grid steps, not exchange lot-size claims, AI, Kelly sizing or real funds.

Phase 01A established persistent local identity and workspace ownership, authenticated loopback HTTP, and audited workspace-name updates. Phase 01B added the WPF workstation shell and Dark/Light/System themes. Phase 01C adds authorized realtime invalidation, automatic reconnect and resynchronization, recent backend diagnostics, and explicit **Local Backend** Start/Stop/Refresh controls. Phase 02A adds read-only public market discovery for Polymarket and Kalshi and an offline-capable local Market Explorer.

Phase 02B.1 adds explicitly refreshed, read-only REST orderbook snapshots, canonical decimal L2 normalization, a bounded in-memory cache, and gross executable-depth previews. In Market Explorer, select a market/outcome and choose **Refresh Order Book**. Navigation and header Refresh only read local data. Kalshi asks are explicitly derived from opposite-side bids for supported binary metadata; Polymarket uses native outcome-token bids and asks. Snapshots become stale after five seconds by default and disappear from the cache after backend restart.

Public metadata and REST orderbook reads require no exchange credentials. Phase 02B.2 adds explicitly started realtime orderbooks: public Polymarket WebSockets and optional authenticated Kalshi WebSockets. There is no live order submission or AI call. Phase 04A adds explicit paper snapshot simulation; Paper remains the only supported execution environment and all live execution capabilities remain unavailable. See [Phase 02B.2 verification](docs/Development/Phase02B.2Verification.md) and [exchange integration](docs/Development/ExchangeIntegration.md).

## Market relationships

Phase 03A adds **Market Matching**: persisted candidates, typed evidence and blockers, explicit native outcome mappings, independent exclusivity/exhaustiveness facts, and audited owner review. Choose **Generate Candidates** to examine a bounded recent/open catalog scope, optionally selecting an exchange and native market ID. Navigation, header Refresh, and Sync Markets never generate relationships or start related orderbooks. **Enrich public metadata** explicitly fetches the selected pair's public rules/metadata; no exchange account is required.

The validator never promotes title similarity to equivalence. Policy 1 requires complete evidenced semantics and compatible rules; ordinary catalog prose normally remains **NeedsReview**. Deterministic fixtures establish validator behavior, not real-world exchange equivalence. Manual approval requires a reason, explicit mappings and confirmation, and remains **VerifiedManual**. Changed source fingerprints or policy versions make reviews **Stale**; revalidate before reviewing again. See [Phase 03A verification and limitations](docs/Development/Phase03AVerification.md). Existing databases require the documented explicit `--migrate` operation before starting the upgraded backend; no normal user database was upgraded during implementation.

## Read-only pre-fee opportunities

Phase 03B adds **Opportunities** using approved Phase 03A mappings and already cached actionable books. **Evaluate Verified Relationships** scans a bounded scope; **Evaluate Selected** accepts a relationship ID copied from Market Matching. Deterministic trust is the default. Manual relationships require explicit opt-in and remain labeled Manual. Evaluation never refreshes books, syncs markets, enriches relationships or starts realtime.

The two-leg engine walks paired L2 asks with exact decimal values, stops at the positive minimum gross edge, checks native liquidity conflicts and rejects stale/skewed/changed inputs. Gross costs, guaranteed payouts and gross profit remain **PRE-FEE**. Same-outcome BUY/SELL spreads require unsupported inventory; a closed complementary BUY basket requires approved payout proof. Net profit/edge remain unknown and execution eligibility is always false.

Results are bounded and ephemeral. The explicit evaluation tab revalidates results while open; new evaluation jobs require an explicit action. **Show diagnostics** exposes blockers without putting them in the primary candidate list. See [Phase 03B verification](docs/Development/Phase03BVerification.md) and [opportunity architecture, formulas, limits and API](docs/Architecture/ReadOnlyOpportunities.md).
## Fee-aware opportunities

Phase 03C adds **Evaluate cached fees**, enabled by default in Desktop (the API retains its gross-only default). Unknown fees are null, never zero. **Show diagnostics** retains gross opportunities with missing, stale or disputed fee metadata. Select a diagnostic result and choose **Refresh Fee Data for selected result**, then explicitly evaluate again. Refresh is bounded, public, account-free and cancellable; evaluation, navigation and header Refresh make no fee-network requests.

**Settings → Fee calculation profile (diagnostic assumption)** stores Unknown, DirectMember or NonDirectMember for this local workspace. This is not authenticated account information and is unrelated to WebSocket credentials. Saving it invalidates fee results without changing gross results or contacting an exchange. L2 fee results are estimates using one hypothetical taker order per leg and one modeled fill per consumed native price level, not completed trades.

Official sources verified on 2026-09-23 contain a Kalshi rounding discrepancy: the regulatory PDF describes centicent alignment and whole-cent examples, while the API describes six-decimal trade fees and account-specific alignment. Public Kalshi schedules therefore expose model components but leave totals unresolved. Unsupported fee types also fail closed. Polymarket uses explicit market fee parameters; five-decimal ceiling is a conservative modeled-fill assumption where the official rounding mode is unspecified, and program rebates are excluded. USDC fees are never silently converted to USD for cross-exchange totals. See [Phase 03C sources, numeric fixtures and verification](docs/Development/Phase03CVerification.md).

Phases 03A–03D add no balances, positions, capital allocation, simulated completed fills or order submission. Phase 04A paper accounting is described below. Existing databases require the explicit migration procedure; normal user data was not upgraded during development.

## Continuous read-only monitoring

Phase 03D adds **Opportunities → Continuous monitoring → Start Monitoring / Stop Monitoring**. Start consumes already approved relationships, cached books and persisted fee metadata. It never syncs markets, generates/enriches relationships, fetches books or fees, or starts subscriptions. Obtain those inputs separately using their existing explicit controls. Monitoring belongs to the backend and continues when Desktop closes; a backend restart always leaves monitoring **Stopped**.

The default scope is 250 deterministically ordered, deterministically verified relationships (configurable up to 1,000); manual trust requires opt-in. Coverage shows skipped relationships and partial coverage. Defaults preserve the separate **FeeAdjusted**, **GrossOnly**, **NearEdge**, and **Blocked** lanes. Unknown fees are not zero, Kalshi's unresolved public rounding conflict cannot enter fee-adjusted ranking or default alerts, and near-edge rows are not arbitrage opportunities. All economics remain read-only modeled estimates.

Expand **Monitoring profile**, edit, then **Save monitoring profile**. Ranking and alert thresholds are separate. Fee-adjusted alerts are enabled by default; gross-only alerts require explicit opt-in and are labeled **GROSS / FEES UNRESOLVED**. Alerts require a threshold crossing, a valid drop below the hysteresis boundary before rearming, and a 60-second default cooldown. Alert history is historical; **Inspect current opportunity** revalidates the current key.

Optional local CSV output writes under `backend/monitoring/<workspace-id>/`: an atomic top-100 snapshot (30-second default interval) and transition-only alerts rotated at 5 MB across five files. Retained SQLite alerts are limited to 5,000 and 30 days; audit history is separate. Existing installations require migration `20260923064859_OpportunityMonitoring`. See [monitoring architecture](docs/Architecture/ReadOnlyOpportunities.md#phase-03d-continuous-local-monitoring) and [Phase 03D verification](docs/Development/Phase03DVerification.md).

## Paper execution and portfolio

Phase 04A activates **Trading / Portfolio** for explicit paper-only snapshot simulation. Initialize venue balances manually (the UI prefills Kalshi USD and Polymarket USDC, but creates no funds until confirmed). Choose **Paper preview** on an eligible current opportunity, enter quantity, create a preview, then explicitly confirm paper execution. Previews expire after five seconds. Both legs must be fully executable, deterministically verified, fresh and fee-resolved. USD and USDC are never converted or merged; Kalshi's unresolved public fee discrepancy still blocks execution.

Executions, native-level fills, proofs, cash debits and positions persist atomically in SQLite. Request IDs protect repeated confirmations after lost replies. Reset creates a new generation and retains old history. Expected payout/profit at resolution remains a projection until an explicit simulated settlement. Only a separately configured and explicitly armed Auto Paper session can consume current monitoring candidates for execution; alerts and CSV/history are never execution sources. Paper atomicity is **not** evidence that live cross-exchange execution can be atomic.

Existing installations require explicit migration `20260923092015_PaperExecution` using the established backup/`--migrate` procedure. No normal user database was migrated during development. See [paper architecture and API](docs/Architecture/PaperExecution.md) and [Phase 04A verification](docs/Development/Phase04AVerification.md).

Phase 04B adds **Simulated Resolution → MANUAL PAPER SCENARIO** in Trading / Portfolio. Select a generation, refresh settlement data, choose an unresolved standard binary market and winning outcome, preview, then explicitly **Confirm Paper Resolution**. The result is immutable within that generation and is not evidence of a real market result. Closed generations may settle their remaining positions; their proceeds never enter the active generation. This workflow needs no exchange account, current opportunity, orderbook, subscription or fee refresh.

Settlement records the payout vector and credits the original venue/currency bucket atomically. Settled positions retain entry cost and fees; executions become PartiallySettled or Settled. Realized P&L equals payout minus persisted fee-inclusive cost. Paper Performance exposes separate venue/currency cash history and a realized-performance curve: starting paper capital plus cumulative realized P&L. Open-position market values are excluded. No currency conversion is implemented. Phase 04C current marks are separate from this realized history. Void, scalar, ambiguous, negative-risk and complex settlement are unsupported. Migration `20260923105901_PaperSettlement` is additive; follow the existing explicit backup/upgrade procedure. See [Phase 04B verification](docs/Development/Phase04BVerification.md).

## Realtime orderbooks

Select a catalog market/outcome, then choose **Start Realtime**. **Stop Realtime** stops the selected backend subscription; navigating away or closing Desktop leaves backend subscriptions running. Navigation, header Refresh, Sync Markets, and REST refresh never start a WebSocket. Defaults are 16 explicit instruments, ten-second realtime freshness, and at most one cached desktop read per second. Every connection requires a current WebSocket snapshot before deltas become actionable. Kalshi sequence gaps invalidate the book; Polymarket is explicitly **BestEffort**, because its public feed supplies no strict sequence.

Kalshi credentials are optional. In **Settings → Kalshi Realtime Market Data**, enter the API key ID, select a private RSA PEM file, and import. The original file must already have private owner-only permissions; import never changes it. Windows CurrentUser DPAPI protects the backend copy under `backend/credentials/kalshi.dpapi`, outside SQLite and Git. Replacement/removal asks for confirmation and stops Kalshi subscriptions; start them again explicitly. Linux reports unsupported local secret storage while catalog/REST functionality remains available. Never put private keys in configuration, command lines, environment variables, or Git.

For an explicitly configured, running local backend, `scripts/sample-kalshi-realtime.ps1 -RuntimeDirectory <absolute-runtime-directory>` performs a bounded sample using one cached open binary market, or an explicit `-MarketTicker`. It reports `NotConfigured` without opening an exchange connection if credentials are absent. This script never imports credentials. Development live results: Kalshi **NotConfigured**; Polymarket **NotAttemptedInstrumentUnavailable**. Replay verification is separate from live compatibility.

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

Open **Market Explorer** to browse locally stored metadata. Select Polymarket, Kalshi, or All, then use **Sync Markets** to start an explicit backend-owned public discovery run. **Cancel Sync** settles the selected active run. The header **Refresh** reloads local state and catalog pages; it never starts exchange discovery. The default scope covers all categories in Polymarket's `closed=false` keyset and Kalshi's `unopened`, `open`, `paused`, and closed-but-not-finalized listings. Historical finalized-market backfill is deferred. A run that hits its configured time budget or an upstream limit is labeled Partial; cached records remain available without internet while the backend is running. Counts distinguish stored markets from markets observed in a run. See [exchange integration](docs/Development/ExchangeIntegration.md) for source contracts, [Phase 02A verification](docs/Development/Phase02AVerification.md) for the original results, and [Phase 02A.1 verification](docs/Development/Phase02A.1Verification.md) for the correction.

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

## Current paper valuation

Phase 04C adds **Current Executable Mark** in Paper Trading / Portfolio. **Refresh Valuation** recomputes from local canonical bid depth and cached fee schedules; it never acquires market data. Full-depth gross and fee-adjusted unrealized P&L, completeness-gated equity, coverage, capital utilization and concentration remain separate by venue/currency. Unknown marks or fees display Unavailable. Original entry accounting, confirmed settlement and the realized performance curve remain unchanged. No paper SELL/close action, exchange account or persisted MTM history is added. See [paper architecture](docs/Architecture/PaperExecution.md) and [Phase 04C verification](docs/Development/Phase04CVerification.md).
