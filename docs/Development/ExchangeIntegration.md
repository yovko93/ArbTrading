# Phase 02A public exchange integration

Verified against official documentation on 2026-09-22:

- [Polymarket Discover Markets](https://docs.polymarket.com/market-data/discover-markets) and [Market Details](https://docs.polymarket.com/market-data/market-details).
- [Kalshi Market Data Quick Start](https://docs.kalshi.com/getting_started/quick_start_market_data) and [Get Markets](https://docs.kalshi.com/api-reference/market/get-markets).

Both adapters use fixed production HTTPS hosts and dedicated unauthenticated clients. No local bearer credential, exchange account credential, proxy rotation, orderbook request, or arbitrary URL fetch is involved. The desktop talks only to its authenticated local backend.

Polymarket uses `GET https://gamma-api.polymarket.com/markets/keyset?closed=false&limit=200`; each `next_cursor` is passed unchanged as `after_cursor`. This covers publicly enumerable markets whose Gamma `closed` filter is false, without selecting a tag/category or excluding negative-risk markets. The raw Gamma envelope is `{ markets: [...], next_cursor: ... }`. Raw `outcomes` and `clobTokenIds` are JSON-encoded arrays; the adapter parses both and correlates by index only when lengths match. This differs from SDK-normalized `outcomes.yes`/`outcomes.no` objects. Missing tags, event references, and token arrays remain missing with warnings. `active` and `acceptingOrders` are retained as native state; normalized labels are conservative and do not claim trading readiness. `startDate`, `endDate`, `closedTime`, and `updatedAt` remain distinct.

Kalshi uses `GET https://external-api.kalshi.com/trade-api/v2/markets?status=<scope>&limit=200`, traversing `unopened`, `open`, `paused`, and `closed` separately. A returned opaque `cursor` is passed unchanged as `cursor`. The public endpoint is unauthenticated. The raw envelope is `{ markets: [...], cursor: ... }`. Native `ticker`, `event_ticker`, `mve_collection_ticker`, `market_type`, `status`, `rules_primary`, `rules_secondary`, and source timestamps are preserved where returned. The listing does not provide series category or tags, so those remain unknown. Kalshi outcome labels Yes/No have no fabricated token IDs. No `mve_filter=exclude` is applied, preserving listed multivariate markets. No incompatible `min_updated_ts` filter is used.

The Kalshi quick start mentions `status=all`, while the endpoint reference lists only `unopened`, `open`, `paused`, `closed`, and `settled` and says an empty status returns any state. The adapter uses the four explicit non-finalized filters instead of relying on the quick-start shorthand or paging through historical settled markets.

For both exchanges, a page's continuation cursor controls traversal even when the page is short. A repeated cursor or several non-progressing pages fails the run. Records are keyed by exchange plus native ID, stored per page, and never deleted due to a partial or failed run. Complete means reaching the end of every selected scope during this run, not an atomic global exchange snapshot. Historical settled backfill and efficient incremental synchronization are deferred.

The backend allows one page request per exchange at a time, paces requests by at least 300 ms by default, bounds each request to 12 seconds and 8 MB, retries safe transient GET failures at most twice, and honors `Retry-After`. `Local:PolymarketRequestIntervalMs` and `Local:KalshiRequestIntervalMs` configure pacing independently from 100–10,000 ms. Default page size is 200 and default run budget is 15 minutes; `Local:DiscoveryPageSize` accepts 1–500 and `Local:DiscoveryRunMinutes` accepts 1–120. Hitting the time budget or a `Retry-After` beyond it yields Partial. A 401/403 public response fails only that exchange run and does not revoke local workspace access. Malformed envelopes fail; malformed records without usable identity are counted. External text and references are displayed as inert text and never fetched automatically. Prices are omitted because listing data is not an executable quote.

Optional sampled live connectivity check, from the repository root:

```powershell
./scripts/smoke-market-discovery.ps1 -Live
```

This makes at most one read-only request per exchange with a page size of five. It neither starts a backend nor writes normal user storage; the C# adapter sample verifies parsed records in isolated temporary SQLite and does **not** verify full traversal. To exercise an installation, launch backend and desktop with the isolated paths described in `LocalDevelopment.md`, then explicitly select **Sync Markets**. The full run can be long and should not be used as an automated smoke test.

The original Phase 02A **shell-only** sample returned five Kalshi entries. Polymarket did not produce a parsed response; a separate status-only shell check reported an untrusted TLS certificate chain (`SEC_E_UNTRUSTED_ROOT`). Certificate verification was not bypassed. Those observations do not prove the C# adapter path. See `Phase02A.1Verification.md` for the newer C# sample.

## Phase 02A.1 contract correction

[Kalshi's lifecycle documentation](https://docs.kalshi.com/getting_started/market_lifecycle) separates request filter names from REST response statuses: `unopened` selects `initialized`, `open` selects `active`, `paused` selects `inactive`, and `closed` selects markets past close that are not finalized. The adapter maps `initialized` → Upcoming, `active` → Open, `inactive` → Paused, `closed` → Closed, `determined` → Determined, `disputed` → Disputed, `amended` → Amended, and `finalized` → Finalized. Future native values stay Unknown with a warning. Polymarket `closed=true` means trading has closed; it is labeled Closed, never inferred to be finalized. Neither exchange's timestamps prove settlement.

The [Kalshi Get Markets contract](https://docs.kalshi.com/api-reference/market/get-markets) requires a string cursor; the empty string ends paging. The [Polymarket keyset example](https://docs.polymarket.com/market-data/discover-markets) carries `next_cursor`; the adapter accepts string, null, or omission only with explicit `has_more=false`. Unsupported cursor types fail the run. Short pages do not imply termination. The four Kalshi buckets and the `closed=false` Polymarket feed are changing upstream data, never an atomic snapshot. Prior Kalshi runs covering only three buckets do not satisfy current full-coverage metadata.

Test JSON is contract-shaped synthetic data based on those official pages, not a live recording. The opt-in script now calls production C# adapters and stores each sampled page in its own temporary SQLite database. It issues at most one GET per exchange with a page size of five and labels the result Sampled. A successful page does not mean Complete coverage. Secure-connection failures stop without ordinary transient retries; the reported classification does not assert a specific certificate-chain cause unless separately observed.
