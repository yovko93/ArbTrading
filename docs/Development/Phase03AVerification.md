# Phase 03A — market relationships

Verification date: 2026-09-22. This record distinguishes deterministic fixtures from live semantic validation.

## A. Repository baseline

Root: `C:\Users\Yovko\source\repos\ArbTrading`. Selected checkout: `main`, starting HEAD `1202435e5dfb802ec7120727b3e27dd7e0f6d4f9`, clean working tree. Existing remote name `origin`; credential-bearing URLs were not printed. The existing solution, repository history, unrelated files and earlier migrations were preserved. No branch switch, reset, clean, stash, commit, push, merge, new repository or PR.

## B. Official contracts reviewed

Reviewed 2026-09-22, starting from both official documentation indexes:

- <https://docs.polymarket.com/llms.txt>
- <https://docs.polymarket.com/market-data/market-details>
- <https://docs.polymarket.com/concepts/markets-events>
- <https://docs.polymarket.com/concepts/resolution>
- <https://docs.polymarket.com/concepts/negative-risk>
- <https://docs.kalshi.com/llms.txt>
- <https://docs.kalshi.com/api-reference/market/get-market>
- <https://docs.kalshi.com/api-reference/market/get-markets>
- <https://docs.kalshi.com/api-reference/events/get-event>
- <https://docs.kalshi.com/api-reference/market/get-series>
- <https://docs.kalshi.com/api-reference/events/get-multivariate-events>
- <https://docs.kalshi.com/getting_started/market_settlement>
- <https://docs.kalshi.com/getting_started/market_lifecycle>

Market IDs, event/series groups and token IDs have different meanings. Resolution rules govern settlement; market close is not a proposition deadline. Polymarket augmented negative risk can change placeholder/Other definitions. Kalshi event exclusivity metadata and series settlement sources do not establish cross-exchange rule equivalence. Existing catalog `SourceReference` values are market page/API references, not resolution authorities.

## C. Changed files

- Domain: `MarketRelationships.cs`, audit extension in `Identity.cs`.
- Application: `RelationshipCandidates.cs`, `RelationshipHints.cs`, `RelationshipMetadata.cs`.
- Connectors: `RelationshipMetadataSource.cs`.
- Infrastructure: `RelationshipStore.cs`, `TradingDbContext.cs`, catalog metadata cache columns, new `MarketRelationships` and `IndependentRelationshipSetFacts` migrations and model snapshot.
- Backend: `RelationshipEndpoints.cs`, `RelationshipJobs.cs`, registrations in `Program.cs`.
- Contracts: `RelationshipContracts.cs`.
- Desktop: `RelationshipsViewModel.cs`, `RelationshipsView.xaml` and code-behind, shell/template/DI wiring, typed HTTP methods.
- Tests: relationship Domain, candidate, metadata, persistence, API, job and desktop tests; linked desktop VM; WPF fixtures. The older migration test now inserts audit data using the old schema's columns, retaining its original preservation assertions.
- Documentation: this record, README, ExchangeIntegration.

Existing ignore rules already cover build outputs, TestResults, SQLite databases/sidecars, logs, local configuration, keys and protected credentials; no replacement ignore file was needed.

## D–F. Descriptor, relationship types and trust states

`CanonicalMarketDescriptor` preserves raw title/subtitle, rules/description, category/tags, native grouping/status/structure, explicit outcomes and distinct market/event/resolution/retrieval/update timestamps. `MarketSemantics` represents subject, predicate, geography, event/edition/stage, authority, settlement qualifiers, polarity, exact decimal threshold/operator/unit and observation window with timezone and inclusive boundaries. Each parsed fact carries source and reference. Null means unknown, not equality or non-applicability. Threshold non-applicability requires its own evidenced fact. No automatic entity aliases are installed.

Types: Unknown, Candidate, EquivalentSameOutcome, EquivalentOppositeOutcome, MutuallyExclusive, Exhaustive, MutuallyExclusiveAndExhaustive, Subset, Superset, Overlapping, Related, Rejected. States are separate: Proposed, NeedsReview, VerifiedDeterministic, VerifiedManual, Rejected, Stale. Both creation/update and validation/review times are stored. No confidence score grants approval.

## G–H. Candidate blocking and validation

Explicit jobs use a bounded local catalog window, Unicode/case-normalized token postings, deterministic exchange-balanced ordering, capped postings and per-source retrieval. No global pairwise validation. Negation, numbers, comparators, years, names and stage words remain available. Title-derived year, predicate, geography and threshold hints are labeled Title provenance; they improve review visibility and cannot satisfy the authority gate.

Defaults: 500 source markets, 20 candidates per source, 2,000 comparisons, 15 seconds. Server maxima: 5,000 / 100 / 10,000 / 60 seconds. Token processing is capped at 64 per title. Hitting source/posting/per-source/comparison/runtime bounds reports Partial. Default scope uses recent open/upcoming/paused catalog statuses, including Polymarket combined statuses. Selected-market and selected-event/group API scopes are supported; Desktop exposes recent/open and optional selected market. Comparisons and short persistence transactions are cancellable. A single backend-owned job is admitted at a time; duplicate admission conflicts. Jobs survive request disconnection, record terminal/partial status, and interrupted runs are marked on restart. They do not resume automatically.

The validator compares all material dimensions, rule text, authority, temporal boundaries, threshold/operator/unit, native outcome identities and evidenced outcome meanings. Exact compatible facts produce typed positive evidence. Missing or advisory facts produce prominent blockers; material contradictions reject equivalence. Arbitrary differing rule prose is conservatively rejected by policy 1, not semantically paraphrased. Title equality cannot establish identity, rules, binary complements or exhaustive partitions. Market close never supplies observation-window evidence.

Examples that must not become equivalent: election vs nomination; 2028 vs 2032; US vs California; >50 vs >=50; before vs on/before; USD vs EUR; preliminary vs final; semifinal vs final; people with similar names; identical titles with contradictory rules. Tests assert typed blockers/evidence rather than scores.

## I–J. Outcomes and sets

Mappings use native IDs (Polymarket tokens; Kalshi yes/no within its market), with an explicit relationship per mapping. Inverse contract polarity can map source YES to target NO by outcome meaning; labels are never proof. Reordered outcomes are canonicalized by ID, duplicate IDs block approval, similar labels stay separate. A verified binary complement requires a proven two-member partition and opposite semantic polarities.

Exclusivity and exhaustiveness are independently Unknown/False/True with provenance. The Domain validator handles both, either, neither, and unknown set properties. Manual set facts describe precisely the union of native outcomes selected in the mappings, never every member of a catalog event. These facts persist separately and appear in the strategy read contract. Exclusive/exhaustive/overlap relationship types require consistent explicit facts. No negative-risk/group flag automatically supplies a proof. Pair records currently span two native markets; arbitrary multi-market group expansion is not implemented.

## K–L. Fingerprints and policy

Policy version is explicitly 1. SHA-256 fingerprints use deterministic record serialization, ordinal ordering of outcomes/tags and UTC top-level instants. Retrieval/update-only timestamps are excluded. Rules, title, semantic fields, source references, outcome membership and other preserved contract metadata participate. Conservative false-positive staleness is possible for cosmetic raw-text/metadata changes; approval never survives a potentially material change silently.

Policy mismatch, source disappearance or fingerprint mismatch sets Stale. Details, list filters and `IRelationshipProvider` check current metadata instead of trusting a stored approved flag. List checking batches source reads before trust filtering. Provider reads occur within a SQLite transaction. Catalog partial runs do not delete review history; there is no catalog-to-relationship cascade. Explicit revalidation uses current metadata and removes previous manual approval; the audit retains its decision reason and fingerprints. Generation never overwrites an existing human decision.

## M. Manual review and public enrichment

All routes require existing local authentication and explicit workspace membership. Mutations recheck owner membership inside their write transaction. Actor is resolved from the authenticated context, separate from workspace ownership. Manual verify/reject requires confirmation, bounded reason and expected current fingerprints; verification additionally validates native mapping identities. Approval is always VerifiedManual, never VerifiedDeterministic. Evidence/blockers remain visible even when overridden manually. Audit records include action, actor, workspace, relationship/market identities, policy, fingerprints, reason and set facts, without rule documents or credentials. Desktop mutation requests never retry after 401.

The explicit enrichment action reads at most the two selected markets. Per market: Polymarket public Gamma detail (one request); Kalshi market, event and series (at most three). Fixed HTTPS hosts, existing public transport behavior, redirects disabled, one-million-byte response bound, 20-second per-market budget, no account headers, no document URL fetching and one concurrent enrichment admission. Enrichment caches raw selected semantic metadata against the catalog base fingerprint. A catalog semantic change invalidates that supplement. Its current outcomes replace older catalog outcomes for review. Enrichment never happens on navigation, catalog sync, generation or startup.

Routes below `/api/v1/workspaces/{workspaceId}/relationships`: paged/filterable GET `/`, GET `/{id}`, POST `/generate`, GET `/jobs/{id}`, POST `/jobs/{id}/cancel`, POST `/{id}/revalidate`, `/enrich`, `/verify`, `/reject`.

## N. Migration/data preservation

New migration-backed tables persist relationships, outcome mappings, evidence and jobs; audit details and bounded public metadata cache columns are additive. Pair order is canonicalized by exchange/native ID; the workspace/pair composite index prevents duplicates. Additional indexes cover target market, type, verification state and job timestamps. Migration fixtures start at the published Phase 02 catalog schema and preserve users, workspace/membership/profile, catalog market, discovery run and audit record. Existing stores require explicit migration; tests never substitute EnsureCreated.

## O–Q. Actual verification

Commands executed on Windows with SDK 10.0.401 and NuGet auditing enabled:

| Check | Actual result |
|---|---|
| Focused relationship tests | Passed across Domain, Application, API/persistence/job/desktop and WPF fixtures |
| `dotnet restore ArbitrageTrading.sln` | Passed |
| `dotnet build ArbitrageTrading.sln -c Release --no-restore` | Passed, 0 warnings / 0 errors |
| `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore` | 350 passed, 0 failed, 0 skipped: Domain 40, Application 136, Backend integration 164, Desktop 10 |
| `scripts/verify.ps1` | Passed (restore, Release build, all 350 tests, TRX output) |
| `git diff --check` | Passed using the repository's normal Windows line-ending settings |
| `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` | No vulnerable packages reported for any project by the configured NuGet sources |

WPF XAML compiled. Automated selection/access/confirmation/one-attempt tests passed. Light and Dark fixtures were rendered and inspected at 1180×1000, 96 DPI, including a stale candidate, prominent predicate blocker, long wrapped rules, mapping editors and review controls. Inspection prompted wider title columns and local themed selector/button templates. Captures are in ignored `TestResults/Phase03A/relationships-*.png`; they are synthetic fixtures, not a live exchange session. Confirmation wording and cancel/accept behavior were tested through the confirmation delegate, not a real mouse-driven dialog.

Real mouse testing, multiple-monitor/DPI testing and an assistive-technology accessibility audit were **not performed**. Automation labels and keyboard-capable WPF controls are present, but those are not a substitute for that audit. Linux runtime execution was not performed in this Windows session.

## R–U. Boundaries and remaining limitations

Policy 1 provides strict validation of fully established semantic descriptors and conservative catalog review. It intentionally has no general natural-language rules parser and does not automatically construct complete authoritative semantics from live exchange listings. Consequently no live cross-exchange equivalence is claimed from these fixtures. Public enrichment assists review but does not prove arbitrary prose or fetch linked terms documents. Candidate windows/postings are bounded, incomplete discovery is explicit, and a missing candidate is not evidence of incompatibility.

The provider exposes approved mappings, complements/set relationships and their distinct manual/deterministic trust. Proposed, NeedsReview, Stale and Rejected are excluded; manual results require `includeManual=true`. This is semantic eligibility, not permission to trade or a decision about future execution policy. No arbitrary alias resolver, AI proposer, automatic group expansion or strategy engine was added.

No Kalshi/Polymarket account, exchange credential, wallet, signing key, live WebSocket or normal user credential store was needed or used. All default tests are offline fixtures and runtime databases are isolated under temporary storage outside Git. Public documentation lookup and NuGet auditing are separate network checks. No arbitrage cost/edge/ROI/profit calculations, order creation/execution, simulated fills, relationship-triggered subscriptions or external AI calls were introduced. Phase 03B was not started. Changes remain uncommitted.
