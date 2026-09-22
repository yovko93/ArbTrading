# Phase 02B.1 verification

Date: **2026-09-22**. Repository: `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on **main**. Starting HEAD: `ba24ca462d4944cfb941ba2fef4d258fbaa3c94a`; working tree was clean. Existing `origin` was inspected without printing URLs/credentials. AGENTS.md, README, earlier 02A verification records, integration notes, catalog/access/realtime code and tests were reviewed. No branch, Git metadata, remotes, history, migration, user storage, or unrelated files were replaced. Changes remain **uncommitted and unpushed**.

## Delivered implementation

| Area | Main files and behavior |
| --- | --- |
| Domain | `OrderBooks.cs`: immutable instrument/level/snapshot models, liquidity origins, canonical normalizer, validity/freshness eligibility, pure gross-depth calculation. |
| Application | `OrderBooks.cs`: source interface/request, bounded thread-safe cache, last-refresh failure metadata and testable freshness. |
| Connectors | `OrderBookSources.cs`: dedicated secure public Polymarket/Kalshi adapters, bounded JSON/fixed-point parsing and GET retries. `PublicMarketSources.cs`: optional sample-only MVE exclusion; normal catalog coverage remains unchanged. |
| Backend | `OrderBookService.cs`, `OrderBookEndpoints.cs`: catalog-backed resolution, serialized refresh admission, authenticated workspace cache/refresh/depth APIs. Program/LocalOptions register clients/cache and validate capacity/freshness. Catalog status advertises ManualRestSnapshots. |
| Contracts | `OrderBookContracts.cs`: instrument/level/snapshot/state/failure and gross-depth DTOs; Desktop continues to reference Contracts only. |
| Desktop | `OrderBookPanelViewModel.cs`, MarketExplorer view/model, BackendClient: manual refresh, selected outcome, separate bounded bid/ask tables, decimal BBO/spread, timestamps/age, native/derived labels, guarded cancellation and local display clock. |
| Verification | Engine/adapter fixtures, real migrated SQLite API tests, desktop race tests, light/dark WPF render test, extended opt-in sample script, README/integration notes and this report. |

Routes are `GET /api/v1/workspaces/{workspaceId}/orderbooks/{exchange}/{marketId}`, `POST .../refresh` (optional catalog-validated `instrumentId` query), and `POST .../depth`. Reads and previews never refresh implicitly. One explicit instrument is fetched at a time; concurrent refresh admission returns 409 instead of building an unbounded queue. Every route checks workspace authorization. No new database schema or durable book history exists.

## Contract verification

Exact checked official URLs, endpoint envelopes, depth semantics, price representations, rate-limit handling, and documentation discrepancies are recorded in [ExchangeIntegration.md](ExchangeIntegration.md#phase-02b1-rest-orderbooks). Sources were checked on **2026-09-22**, beginning with both official llms.txt indexes. The Polymarket Prices and Order Books page supplies explicit raw REST examples for `/book?token_id=...`; SDK examples were not substituted. The CLOB OpenAPI link was attempted but was unavailable to the research reader and failed certificate validation from the local network. This limitation is explicit, and no certificate bypass was attempted.

Kalshi's orderbook guide says the route is public; its generated reference lists signed authentication headers. The bounded real read returned HTTP 200 unauthenticated, confirming the chosen public behavior on this date. Fixed-point fields are `orderbook_fp.yes_dollars`/`no_dollars`, pairs of price/quantity strings. `depth=0` requests all returned levels. Array-order prose differs across pages; sorting never relies on wire order.

Polymarket preserves native bids/asks per catalog outcome token, checks returned asset identity, and keeps Gamma native market identity separate from returned condition identity. Missing, invalid or ambiguous mappings are NotAddressable. No Yes/No or index-zero assumption exists. Kalshi retains own-side and opposite-side native bids; only confirmed binary catalog metadata allows exact `1 - opposite bid` asks marked DerivedComplement. Unknown/scalar/Multivariate metadata is Unsupported with native data retained. Loss of binary metadata suppresses previously cached complements without making a new external request.

Prices are decimal payout units [0,1]; quantities are decimal and strictly positive after removing zero levels. Financial strings never pass through double. Malformed, negative, out-of-domain, duplicate, contradictory-origin, crossed and locked books cannot become actionable. Duplicate price levels are rejected rather than summed because L2 entries may already contain aggregated totals. Empty sides are valid and BBO/VWAP remain null when unavailable. Per-market order tick/minimum-size compliance is not promised by this read-only gross-depth engine.

## Numerical and state checks

- Buy 25 against 10@0.40, 20@0.42, 50@0.45: executable 25, gross **10.30**, VWAP **0.412**, worst **0.42**, two levels, zero unfilled.
- Sell 25 against 8@0.61, 10@0.60: executable **18**, unfilled **7**, gross **10.88**, VWAP **10.88 / 18** using decimal, worst **0.60**, partial depth.
- Native NO bid **0.37 × 12.50** becomes YES ask **0.63 × 12.50**; native YES **0.2001 × 1.25** becomes NO ask **0.7999 × 1.25**. Assertions use exact decimal equality.
- Default stale/invalid/unsupported/failed-refresh depth is non-actionable. Explicit diagnostic mode can walk stale valid depth, never invalid/unsupported levels. Empty required side returns NoLiquidity and null prices. Zero/negative requested quantity is rejected.

Default cache capacity is 128 full instrument identities, configurable 1–1024. Accepted immutable snapshots replace atomically; concurrent cache writes cannot replace a newer retrieval with an older one. Failure metadata does not erase prior valid depth. Default freshness is **5 seconds**, configurable 1–60 using TimeProvider. A last refresh failure independently makes prior cached depth non-actionable. Stale data is retained. Restart begins with an empty book cache and persistent catalog metadata; no book is presented as Live or sequence-safe.

Fixtures cover wire shape, unordered/empty/duplicate levels, numeric errors/ranges/scales, token mismatch, source time/hash, fractional/sub-cent complements, unsupported structures, declared and streamed payload bounds, secure transport failure, bounded GET retry/Retry-After, credential rejection, cache concurrency/eviction/restart emptiness, authorization, unknown market/instrument, no implicit external reads, stale API preview, metadata downgrade and nonbinary Polymarket outcome mapping. Desktop tests cover selection supersession, access invalidation, disposal, cancellation, Loading settlement, derived labels, decimal values, and no public fetch from selection/navigation/header refresh behavior. Existing lifecycle, authentication, Save/realtime, catalog and theme tests remain intact.

## Build, tests and audit

Actual Windows verification on .NET SDK 10.0.401 / runtime 10.0.12:

- `dotnet restore ArbitrageTrading.sln`: succeeded; NuGet auditing remained enabled.
- `dotnet build ArbitrageTrading.sln -c Release --no-restore`: succeeded, **0 warnings, 0 errors**.
- Full Release test command and repository `scripts/verify.ps1`: passed. Final suite: **241 passed, 0 failed, 0 skipped** (Domain 8; Application 95; Backend Integration 129; Desktop 9).
- `git diff --check`: passed.
- Separate `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive`: no vulnerable packages reported for any project using the configured sources. This is separate from compile/test success.

Protected runtime tests used authorized isolated temporary directories outside Git; desktop preferences and test databases were isolated. No normal user application directories were used. The opt-in live gates are disabled during the default suite; their passing no-op test cases are not connectivity evidence. No unrelated process was terminated.

## Actual live sample

The first bounded discovery of 20 open Kalshi records contained no supported binary classification and made **zero** orderbook calls. The sample was then narrowed explicitly to five standard open markets using documented `mve_filter=exclude`, preserving normal catalog synchronization scope. The successful final sample used one discovery GET and one orderbook GET, each with one attempt:

| Field | Observed value |
| --- | --- |
| Exchange | Kalshi |
| Native market | `KXLOLGAME-26SEP250700VKSACPD-VKSA` |
| Instrument | `yes` |
| HTTP response/status | received / 200 |
| Parsing / normalization | succeeded / succeeded |
| Bid / ask levels | 1 / 4 |
| Derived levels | 4 |
| Best bid / ask | 0.0100 / 0.9500 |
| Retrieved UTC | 2026-09-22T11:18:02.079537Z |
| Classification | SampledOrderBookVerified |

A one-contract diagnostic walked available depth with IsActionable=false because diagnostic mode was explicit. No order was submitted. This sample is not evidence of all-market correctness, full discovery traversal, ongoing freshness or an execution guarantee.

Polymarket: **NotAttemptedBecauseCatalogInstrumentUnavailable**. The isolated sample had no safely acquired current catalog token; the user's Gamma network restriction is already documented. No stale token was hardcoded and no request was manufactured through a relay, proxy, DNS override or altered trust policy. CLOB normalization is fixture-tested, but Polymarket live compatibility remains unverified.

## UI checks and limitations

Actual WPF STA rendering at 1180×850 verified both Light and Dark palettes, native/derived text, decimal level display and the explicit refresh button. Offscreen fixture PNGs were generated under ignored `TestResults/OrderBookUI` and visually inspected. The orderbook metadata layout was compacted to expose both level tables within the detail pane; additional depth and existing metadata remain scrollable. These are isolated fixture renders, not screenshots of a live exchange session. Manual mouse/keyboard interaction, DPI/accessibility review and Linux CI were not performed.

The optional depth-entry desktop form is deferred; gross preview is available through the authenticated API. Full source depth is bounded by response bytes/level caps. Existing catalog Multivariate classification cannot prove binary structure, so those books remain unsupported for complement execution. No fee/minimum-order/tick-grid/execution eligibility, market matching or strategy inference is claimed.

No exchange credentials, order submission, WebSocket feed, paper execution, automatic polling/scanning or arbitrage detection was added. Future 02B.2 must add sequence-aware realtime reconciliation and credential lifecycle separately. No commit, push, merge, pull request or Phase 02B.2 work was performed.
