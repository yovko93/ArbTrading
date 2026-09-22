# Phase 02A.2 verification

Date: 2026-09-22. Existing repository root: `C:\Users\Yovko\source\repos\ArbTrading`. Starting `main` HEAD was `4a3c0f3fac03a434284c7319bbe9eb8c3bbdb22d`, and the working tree was clean. The `origin` remote was present; its URL was not printed. No branch, Git metadata, remote, migration, identity, settings, or normal runtime data was changed. Work remains uncommitted and unpushed.

## Contract and reproduced findings

The [official Polymarket keyset reference](https://docs.polymarket.com/api-reference/markets/list-markets-keyset-pagination) and [May 14, 2026 changelog](https://docs.polymarket.com/changelog/predictions) specify a maximum `limit` of 100. The reference also says `next_cursor` is omitted on the last page. Focused tests reproduced the previous adapter's outbound `limit=200`/`500` and rejection of nonempty and empty terminal pages without a cursor before the fix.

The requested `Local:DiscoveryPageSize` remains 1–500, default 200; zero or negative values remain invalid. The Polymarket adapter uses `min(requested, 100)` in the actual outbound URI on first and continuation requests. Kalshi still uses the requested value; its separate contract is unchanged. Paging follows each nonempty opaque cursor with no arbitrary record cutoff. A production-parser test traversed 1,205 unique markets through 13 legal-size pages. Tests cover requested 5, 100, 200, and 500, plus invalid zero and Kalshi 200.

Polymarket now treats an omitted `next_cursor` as terminal after validating the object envelope and `markets` array. Present nonempty string cursors continue even on short pages. Present null, empty string, number, boolean, object, and array cursor values fail, as the keyset reference documents a string when present and omission at termination. Kalshi's required string field and empty terminal string remain separate. A backend/SQLite test persisted a valid first page and a nonempty terminal second page, then reported Complete for its declared fixture scope. Existing production-parser regressions keep a malformed or cyclic second page from advancing full-coverage metadata. These are synthetic contract tests, not live recordings.

## Changed files

- `src/Arbitrage.Connectors/PublicMarketSources.cs`: caps Polymarket's effective limit, accepts documented terminal omission, retains strict present-cursor types, and provides the common public HTTP handler policy.
- `src/Arbitrage.Backend/Program.cs`: registers both dedicated clients through that same handler factory.
- `tests/Arbitrage.Application.Tests/PublicMarketSourceTests.cs`: outbound URI limits, 1,205-record traversal, cursor terminals, invalid configuration, and transport-policy regressions.
- `tests/Arbitrage.Backend.IntegrationTests/MarketCatalogApiTests.cs`: production parser, coordinator, and SQLite terminal-page completion regression.
- `tests/Arbitrage.Backend.IntegrationTests/LiveMarketAdapterSample.cs` and `scripts/smoke-market-discovery.ps1`: at-most-two-GET opt-in sample, one attempt per page, shared `AllowAutoRedirect=false`/`UseProxy=false` policy, isolated SQLite checks, per-exchange stage reporting, and nonzero exit when either requested check fails.
- `docs/Development/ExchangeIntegration.md` and this file: corrected current contract and verification evidence. Prior verification records remain intact.

## Checks and sampled live result

Focused tests passed first. `dotnet restore ArbitrageTrading.sln` succeeded with NuGet auditing enabled and no audit warning in its output; a separate vulnerability assessment was not performed. `dotnet build ArbitrageTrading.sln -c Release --no-restore` succeeded with 0 warnings and 0 errors. Both `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore` and the required `scripts/verify.ps1` passed 171 tests, 0 failed, 0 skipped (Domain 8, Application 42, Backend Integration 113, Desktop 8), using the tests' isolated system-temp directories outside the Git checkout. `git diff --check` passed after the final documentation edit.

The explicit `./scripts/smoke-market-discovery.ps1 -Live` run on .NET 10.0.12 / Windows 10.0.19045 reported:

| Exchange | UTC start | Requests / budget | Effective size | HTTP response | Parsed | Isolated SQLite | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Kalshi (`external-api.kalshi.com`) | 2026-09-22 09:35:24.992 | 2 / 2 | 5 | HTTP 200 | 2 pages, 10 records | 10 stored and verified | `SampledRecordsVerified` |
| Polymarket (`gamma-api.polymarket.com`) | 2026-09-22 09:35:28.858 | 1 / 2 | 5 | None | No page parsed | No records stored | `SecureConnectionFailure`, .NET `SecureConnectionError` |

The script exited nonzero because Polymarket failed; the per-exchange output still reported Kalshi's success. No `SocketErrorCode` or native error was available for Polymarket. A secure-connection classification does not establish a particular certificate-chain cause or prove how far the handshake progressed. The previous shell-only `SEC_E_UNTRUSTED_ROOT` observation remains separate. No proxy, DNS, certificate store, or TLS validation setting was changed, and this unchanged environment was not retried. The sampled Kalshi pages do **not** establish full discovery coverage. A parsed empty page would be reported as `SampledEmptyParsed`, not as verified records. For a manual check under an existing approved network configuration, run from the repository root:

```powershell
./scripts/smoke-market-discovery.ps1 -Live
```

Linux CI was not triggered locally. Windows XAML and automated desktop tests passed; manual WPF appearance, DPI, accessibility, and mouse-driven checks remain outstanding. No orderbook or execution capability was added.
