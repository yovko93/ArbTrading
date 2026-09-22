# Phase 02B.2 verification

Date: **2026-09-22**. This record covers realtime market data only; live exchange compatibility is distinguished from offline parser/transport verification.

## A–B. Repository and baseline

Repository root: `C:\Users\Yovko\source\repos\ArbTrading`. Ordinary checkout, branch **main**, starting HEAD **aa0a90b1d6c1a9b2d3f72c200953c13b2f6199af**, clean at start, matching the published Phase 02B.1 baseline. Existing remote name `origin` was inspected without printing credential-bearing URLs. Applicable AGENTS.md, README, Phase02B.1Verification, exchange integration, canonical/cache/REST/depth, auth/access, SignalR, storage and tests were reviewed before editing.

The existing solution, history, REST adapters, canonical books, cache and gross-depth engine were extended. No new repository, nested solution, branch switch, reset, clean, stash, migration, commit, push or PR. No unrelated user files or normal user credential/runtime storage were accessed. The user explicitly authorized secure optional Kalshi credential support in this phase, superseding the Phase01A prohibition on collecting exchange credentials.

Changed-file summary:

| Area | Changes |
|---|---|
| Domain/Application | Typed realtime metadata and credential port; extend the existing cache with source priority, generation binding and eligibility. Extract shared Kalshi binary normalization from REST. |
| Connectors | Production single-writer parsers, exact decimal snapshot/delta reconciliation, fixed-host WebSocket transport, bounded retries/keepalive/close, narrow RSA-PSS handshake signer. |
| Infrastructure | Optional Windows CurrentUser DPAPI credential store with private ACLs, atomic writes, safe import validation, fingerprint/version and removal. |
| Backend/Contracts | Hosted exchange owners, catalog/workspace admission, start/stop routes, local-owner credential routes, safe realtime DTOs and coalesced invalidations. |
| Desktop | Start/Stop controls, source/continuity/anchor/sequence/age/reason display, gross-depth form, guarded/coalesced cache reads, Settings credential controls. |
| Verification/docs | Replay, transport, cache, API/security, desktop and WPF fixtures; explicit bounded sample script; README/integration notes; ignore signing files/blobs. |

## C. Official contracts

Verified **2026-09-22**, before implementing authentication or parsers. Full URL/field inventory is in [ExchangeIntegration](ExchangeIntegration.md#phase-02b2--explicit-realtime-market-data).

Primary specifications: [Polymarket AsyncAPI](https://docs.polymarket.com/asyncapi.json), [Polymarket realtime](https://docs.polymarket.com/market-data/realtime-data), [Polymarket changelog](https://docs.polymarket.com/changelog/predictions), [Kalshi AsyncAPI YAML](https://docs.kalshi.com/asyncapi.yaml), [Kalshi orderbooks](https://docs.kalshi.com/websockets/orderbook-updates), [Kalshi WebSocket quick start](https://docs.kalshi.com/getting_started/quick_start_websockets), [Kalshi API keys](https://docs.kalshi.com/getting_started/api_keys).

Production endpoints are `wss://ws-subscriptions-clob.polymarket.com/ws/market` and `wss://external-api-ws.kalshi.com/trade-api/ws/v2`. Current Kalshi demo endpoint is `wss://external-api-ws.demo.kalshi.co/trade-api/ws/v2`, documented but not exposed as a selectable production option. Differences from older examples: dedicated Kalshi WS hostname, WS `_dollars_fp` snapshot fields distinct from REST, and sequenced subscription control frames. Neither reviewed specification gives a usable numeric market cap; 16 is an application safety bound, not an asserted upstream entitlement.

## D–F. Reconciliation and continuity

Polymarket requests an initial full book. Absolute BUY/SELL level updates replace quantities; zero removes. Multiple changes validate atomically, asset/anchored condition identity is checked, full books replace, and new ticks constrain later delta prices. Reconnect/uncertainty discards incremental state and requires a new anchor. Old generations are ignored. **BestEffort only**: the public protocol has no strict sequence suitable for proving lossless continuity.

Kalshi binds the acknowledged SID, tracks sequence across all selected markets and sequenced controls, then requires exact next sequence. Snapshot native YES/NO bids anchor; signed decimal deltas add/remove levels. Duplicate/backward/skipped/wrong SID/ticker/negative-result frames invalidate immediately and cannot mutate the retained visible book. No REST-to-WS synchronization boundary is invented. Reconnect requires a new full snapshot. Native bid sides and complement-derived asks use the same normalizer as REST. Exact seq100 (.40×10 YES, .55×8 NO), 101 (+5), 102 (-15), 104 (missing103) is replay-tested, including derived NO asks .60×15 and zero removal.

## G–I. Authentication and secret handling

Signing input is decimal Unix milliseconds + `GET` + `/trade-api/ws/v2`, encoded UTF-8. Headers are KALSHI-ACCESS-KEY/TIMESTAMP/SIGNATURE; signature is RSA-PSS SHA256/MGF1-SHA256, digest-length salt, standard base64. Each handshake has a fresh disposable RSA instance and zeroed private-byte lease. PSS is randomized; generated-key tests verify the exact signed input/algorithm rather than comparing randomized signature bytes. No official fixed signature test vector was found.

The Windows implementation uses CurrentUser DPAPI, owner-only directory/file ACLs, no link/reparse ancestry, bounded absolute-file import, private RSA PEM validation/signing self-test, flushed temporary output and atomic replacement. Storage is outside Git at `Local:DataDirectory/credentials/kalshi.dpapi`, separate from SQLite. Original key files are not altered/deleted. Replace/remove require confirmation and expected version, then stop Kalshi subscriptions. Only the authenticated local profile owner may administer secrets; workspace membership is insufficient. Linux returns UnsupportedPlatform for this store; fake providers keep default non-WPF tests offline. Server enrollment/secret-manager support remains future work.

Evidence:

- Generated-key Windows integration test decrypts through the production store, verifies the public key, checks restrictive ACLs, confirms ciphertext lacks PEM/PKCS#8 plaintext, checks replacement/version conflicts and removal, and verifies original import-file preservation.
- Generated private material is checked against API responses and isolated backend SQLite/log files. API responses expose masked ID, public fingerprint, timestamps, capability, authentication result and version only.
- Unsafe file permissions, relative paths, malformed/public/undersized keys and unauthorized administration are rejected.
- Source/DTO inspection confirms SignalR contains identity/version/status only; no key, signature, signing input or protected blob. Private PEM never enters a desktop field, preferences, appsettings or command-line argument. Signing code has only the fixed WS path, with no generic trading-signature operation.
- Repository path checks find no tracked `.pem`, `.key`, `.dpapi`, connection metadata or local secret config. New ignore rules extend existing exclusions. No unrelated credential stores were searched.

These checks concern generated test material and the implemented data paths; no real user key was loaded to establish them.

## J–L. Cache, lifecycle, freshness

One bounded cache retains REST diagnostics and the current realtime view per instrument. Start immediately invalidates actionability, including before the scheduler starts the connection. REST refresh during realtime cannot overwrite its book, generation or continuity. Stop retains the old book non-actionably; a subsequent explicit REST refresh selects REST. Active entries are pinned against ordinary REST eviction. Cache eligibility independently checks connected state and the generation of the actual published book.

One connection owner per exchange, one Kalshi multiplexed SID. Desired-set changes reconnect that exchange and invalidate all its current anchors; other instruments resume after new snapshots. Protocol faults conservatively invalidate the connection scope. Other exchange owners are isolated. Authentication failures/missing credentials stop retries until explicit recovery; replacement/removal stops existing Kalshi sessions. Up to five attempts with exponential jitter, normal certificate validation, bounded frame/receive/connect/close sizes/times. No DNS/IP/proxy/TLS bypass. The real loopback WebSocket regression verifies cancellation leaves time to send unsubscribe before closing; shutdown awaits owned tasks.

REST freshness stays five seconds; realtime defaults ten seconds, configurable 1–60 via TimeProvider. Pong/control events never refresh book age. Anchor, generation/SID/session, sequence, source time, received/control/changed timestamps, tick and version are explicit. Kalshi requires Continuous; Polymarket permits labeled BestEffort. Empty/partial depth is handled by the existing calculator. Realtime diagnostic requests cannot bypass an unresolved gap or stale/noncurrent anchor.

The shared scheduler emits at most four small invalidations per instrument per second through a bounded nonblocking queue. Desktop cached reads are at most one per second; navigation stops page work without stopping subscriptions. Session/workspace/backend/access/selection guards reject stale responses. Start/Stop and credential mutations have no 401 replay. Header Refresh, navigation, catalog sync and REST refresh do not start external sockets.

## M–N. Actual verification

Final `scripts/verify.ps1` completed successfully after the final code changes:

| Check | Actual result |
|---|---|
| `dotnet restore ArbitrageTrading.sln` | Passed; auditing enabled |
| `dotnet build ArbitrageTrading.sln -c Release --no-restore` | Passed, 0 warnings, 0 errors |
| `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore` | **296 passed, 0 failed, 0 skipped** |
| Domain / Application / Backend integration / WPF | 8 / 129 / 150 / 9 passed |
| `scripts/verify.ps1` | Passed (restore, Release build and full tests) |
| `git diff --check` | Passed |
| PowerShell sample parser | Passed |

The original 241-test baseline is preserved; added regressions cover this phase. Earlier focused runs also passed. TRX files are under each test project's ignored `TestResults` directory. Windows-only and Linux-only branches of platform-specific tests execute only on the appropriate platform; the aggregate Windows count does not imply Linux runtime verification.

Separate `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive`: **no vulnerable packages in all 13 projects** from the configured sources. NuGet auditing remained enabled, including the new ProtectedData package.

Default tests used generated credentials, fake external transports and isolated temporary SQLite/storage outside Git; the production transport test used an ephemeral loopback server. No default test needs exchange internet or real credentials. Portable `net10.0` projects compile; **Linux execution was not performed on this Windows host**. Existing Linux CI workflow remains intact; restore still needs dependencies available via cache or NuGet.

## O–P. Actual live outcomes

- **Kalshi: NotConfigured.** No user credential was supplied or imported. No authenticated live exchange connection was attempted. The production no-credential API path was verified in an isolated backend. The explicit sample script is supplied for a separately configured backend; its PowerShell syntax was checked, but no configured live sample was run.
- **Polymarket: NotAttemptedInstrumentUnavailable.** No safe current catalog-backed token was supplied. Existing network restrictions were not probed or bypassed. Production-parser replay tests do not establish live network compatibility.

The sample is bounded to one explicitly selected/reused catalog market and 5–30 seconds, checks optional credential status first, refuses the selected instrument if already streaming, runs cached gross depth when available, and stops in finally. No exchange order operation is present.

## Q. UI verification

- XAML compiled in Release; automated Windows desktop tests ran.
- Light/dark fixture rendering and visual inspection covered explicit Start/Stop, REST, Continuous, BestEffort, resynchronizing, stale, authentication-required, derived asks and non-actionable depth messaging. PNGs are local ignored artifacts under `TestResults/Phase02B.2-UI`.
- The new depth selector uses the same explicit system-color item template as the existing outcome selector for readable dark-theme text. Existing catalog filter/disabled-control contrast is outside this panel change and has not been accessibility-certified.
- **Real mouse interaction: not performed. DPI matrix: not performed** (fixture rendering at 96 DPI only). **Formal accessibility/screen-reader/keyboard audit: not performed.** Basic names are assigned to new inputs; that is not an accessibility certification.

## R–T. Limitations and stopping point

Polymarket is best-effort rather than sequence-perfect. Live exchange verification remains unavailable without deliberately configured Kalshi credentials/a safe Polymarket token. Windows-only local DPAPI storage has no implemented Linux/server secret-provider production alternative. Shared exchange connections resynchronize their other selected instruments when the desired set changes or protocol scope is uncertain. Old views are non-actionable throughout. Last authentication metadata is process-local; the sample does not promise a delta on an inactive market during its short window. No all-market scanning or durable book history.

No exchange order placement/amend/cancel, private portfolio/positions/balances/fills, wallet/order signing, paper fills, arbitrage strategy, market matching, fee/profit or automatic scanner was added. Kalshi credentials serve only WebSocket market-data handshake authentication. **Changes remain uncommitted and unpushed on main. Phase 03 was not started.**
