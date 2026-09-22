# Phase 02A verification

Date: 2026-09-22. The existing checkout at `C:\Users\Yovko\source\repos\ArbTrading` started clean on `main` at `d7aeadbee4ee8d8bbb90fe1b253a9bedadc20b7c`. History, remotes, branch, and unrelated files were preserved. This work is uncommitted and unpushed.

## Delivered scope

The desktop now has a functional Market Explorer backed by authenticated, server-paged local SQLite catalog queries. Public discovery starts only when **Sync Markets** is selected. The backend owns each run after HTTP acceptance, supports cancellation, and settles old Running records as Interrupted on restart. It stores pages incrementally, keeps previous records after partial/failing runs, and distinguishes stored count, current run observations, completion, and cache timestamps. Realtime messages contain only catalog invalidation scope; the desktop refetches its visible page and status. Header Refresh reloads local data without starting exchange discovery.

Polymarket and Kalshi have separate C# public REST adapters and managed HTTP clients. Scope: all categories publicly enumerable by Polymarket `closed=false` keyset, and Kalshi `unopened`, `open`, `paused` listings including returned multivariate markets. No historical settled backfill, invented combinations, or unrelated product families. A Complete run requires reaching the end of every scope; time/request budget or upstream rate-limit deferral is Partial. These are changing upstream feeds, not atomic global snapshots. Catalog fields preserve native IDs as strings and leave unavailable fields unknown. Outcomes, tags, raw status, separate timestamps, rules text, retrieval time, last seen run, and completeness warnings are stored; no listing price is presented as an executable quote.

Mutation replay was corrected first: `BackendClient` now retries a local 401 only for GET. Save, Stop, Sync start, and Sync cancel issue one outbound attempt. The real Save/realtime 401 and 403 races still pass with one Save attempt; bounded credential reread for safe reads remains.

Main changed areas: `Arbitrage.Connectors/PublicMarketSources.cs` (fixed-host public adapters, paging, parsing, pacing/retry); `Arbitrage.Application/MarketDiscovery.cs` (normalized metadata boundary); `Arbitrage.Infrastructure/MarketCatalog.cs`, `TradingDbContext.cs`, and the new `PublicMarketCatalog` migration (persistent catalog/tags/runs); backend coordinator, authenticated endpoints, DI and small scoped realtime notification; versioned catalog contracts; desktop client, Market Explorer view/model, navigation, and guarded refresh; focused fixture, SQLite, API/job, mutation, and notification tests; README, integration notes, and explicit sample script. Existing process-handle, diagnostic cursor, theme, and trading capability designs were preserved.

## Contract and live verification

Official documentation checked on 2026-09-22: [Polymarket discovery](https://docs.polymarket.com/market-data/discover-markets), [Polymarket market details](https://docs.polymarket.com/market-data/market-details), [Kalshi public market data](https://docs.kalshi.com/getting_started/quick_start_market_data), and [Kalshi Get Markets](https://docs.kalshi.com/api-reference/market/get-markets). Raw REST fields were distinguished from SDK-normalized examples. `ExchangeIntegration.md` records endpoint, cursor, status, field, and documentation differences.

The explicit, one-request-per-exchange sample ran at 2026-09-22 00:27 UTC. Kalshi `GET /trade-api/v2/markets?status=open&limit=5` returned five records in a `markets` envelope (`SampledPublicReadSucceeded`). Polymarket `GET /markets/keyset?closed=false&limit=5` failed before a parsed response (`TransportOrTimeoutFailure`); a status-only follow-up identified an untrusted TLS chain (`SEC_E_UNTRUSTED_ROOT`). No certificate bypass or proxy workaround was used. Polymarket live compatibility remains unverified. No live complete traversal was performed for either exchange. Default automated tests use fake HTTP and isolated SQLite, not internet or accounts.

## Checks and operation

Normal audit-enabled `dotnet restore ArbitrageTrading.sln` succeeded after the initial sandboxed vulnerability-feed access produced `NU1900`; audit settings were not disabled. A separately fresh vulnerability assessment was not verified. Release build completed with **0 warnings and 0 errors**. The full Windows test suite and `scripts/verify.ps1` completed with **130 passed, 0 failed, 0 skipped** (Domain 8, Application 13, Backend Integration 101, Desktop 8). `git diff --check` passed. Fixture tests covered more than 1,000 records, cursor cycles, malformed data, 429/Retry-After, transient GETs, public access denial, no bearer leakage, migration from Phase 01, ownership preservation, offline queries, duplicate admission, cancellation, no false Complete, and stale Market Explorer responses after a real Save denial. Existing real Kestrel/SignalR/WebSocket lifecycle and Save/realtime tests remained green.

For an existing installation: stop the backend, back up its full backend directory including SQLite sidecars, then use the same configured directories with:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile -- --migrate
```

For a new isolated local run, choose a fresh root and an unused loopback port, set `Local__DataDirectory`, `Local__RuntimeDirectory`, `ARBITRAGE_RUNTIME_DIRECTORY`, and `ARBITRAGE_DESKTOP_DIRECTORY` to separate subdirectories, then set `Local__BaseUrl` to that loopback port. Start backend with `dotnet run --project src/Arbitrage.Backend --no-launch-profile`; start desktop in a second terminal with the same runtime and desktop paths. The example in `LocalDevelopment.md` shows these environment assignments. No agent run touched normal user storage. The optional `./scripts/smoke-market-discovery.ps1 -Live` performs only two public sampled GETs and does not store data.

Default pacing is 300 ms between page requests per exchange, with one page request at a time per exchange, 12-second request timeout, 8 MB page limit, three total safe GET attempts, page size 200, and a 15-minute run budget. Separate `Local:PolymarketRequestIntervalMs` and `Local:KalshiRequestIntervalMs`, plus `Local:DiscoveryPageSize` and `Local:DiscoveryRunMinutes`, are validated configuration. Cache browsing works with the backend running offline. Missing fields and stale/partial results stay explicit. Incremental synchronization, orderbooks, matching, strategies, account connectivity, paper fills, and all execution remain deferred; Paper is still the only effective environment.

Linux CI, screenshots, accessibility, DPI, and mouse-driven WPF checks were not run locally. The new XAML compiled and existing Windows resource tests passed, but this is not a visual review. No exchange credentials or trading operations were introduced. No commit, push, merge, or pull request was made.
