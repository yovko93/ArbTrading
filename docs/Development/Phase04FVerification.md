# Phase 04F verification — deterministic adaptive paper sizing

Verification date: 2026-09-24. Local paper simulation and isolated fixtures only.

## A–D. Repository and baseline

- **A:** Root `C:\Users\Yovko\source\repos\ArbTrading`; ordinary checkout on `main`, not detached. Starting HEAD `7ef069d6820d10c93f17bb2714924fcfd73de91a` (`Add explicitly armed automatic paper execution`), parent `431292f4f665bb1394a49fc6131de2d83570ef1e`. Initial working tree clean. Root AGENTS.md applied; `origin` retained without printing its URL or credentials.
- **B:** Before implementation, the exact published Phase 04E baseline's GitHub Actions run [35911588488](https://github.com/yovko93/ArbTrading/actions/runs/35911588488) was **completed / success**. This is baseline evidence, not CI for these uncommitted changes.
- **C:** Existing solution placement, branch, history, prior migrations, fixed automation, explicit manual quantities, settlement, valuation and risk gates are preserved. No reset, clean, stash, branch switch or metadata/remote change. Existing ignore rules cover build/test output, SQLite files/sidecars, logs and credentials; no change was necessary. A tracked-file name check found no database/private-key/secret files.
- **D:** Added `Execution/PaperSizing.cs`, backend `PaperCoordinator.Sizing.cs`, four focused sizing test files plus WPF fixtures. Extended automation policy/store/coordinator/endpoints, selected-plan commit/proof reconciliation, Contracts, desktop client/VM/views, compatibility and desktop tests. Updated README, Execution README, paper architecture, roadmap and this report. No dependencies or migration files changed.

## E–R. Design and compatibility

| Item | Result |
|---|---|
| E — Modes | FixedQuantity still evaluates its exact saved quantity once. Adaptive is a separate typed `LargestAdmissibleGridQuantity` policy. Suggestions do not configure or arm anything. |
| F — Versions | Adaptive profile/proof version 2; existing and newly saved fixed profiles retain version 1. Nullable new grid fields are omitted from canonical settings serialization. Old fingerprints are not rewritten. |
| G — Grid | Decimal `Min + N × Step <= Max`, ordered descending. Positive Min/Step, Min ≤ Max ≤ risk quantity cap ≤ 1000. Unaligned Max is not inserted. Step is a paper grid control, not an exchange lot-size claim. |
| H — Bounds | At most 256 candidates; 257 rejects without truncation. Tiny-step, invalid, fractional and overflow-boundary fixtures cover bounded construction. At most 512 exact adaptive evaluations per worker cycle. |
| I — Search | Explicit descending enumeration stops at first fully admissible Q. No binary search, scoring or monotonicity assumption. No below-Min, above-Max or non-grid fallback. |
| J — L2 | Each Q runs the existing requested-quantity paired walk and PaperPlanner against captured canonical books. No proportional profit/fee/VWAP shortcut. |
| K — Fees | Each Q uses actual native-price-group fee composition from one captured resolved schedule/account-profile snapshot. Input validation binds effective schedule fingerprints and profile revision before/after search. No fee acquisition. |
| L — Risk | Existing PaperRiskEvaluator evaluates each plan against one immutable generation/risk read model, including all cash reserve, debit, exposure, count, quantity and economics rules. |
| M — Session budget | Each Q independently checks per-venue/currency gross entry debit, existing session/hour/relationship counts and cooldowns. Used session debit is captured once and checked again in the writer. No P&L or cross-currency budget credit. |
| N — Coherence | Books captured together; fees captured once; risk and session history captured in one deferred SQLite read transaction. No per-Q database transaction. Before/after validation rejects book/fee/relationship changes with no restart. Quantity-independent source, policy, invalid-state and lifecycle failures stop early. |
| O — Financial race | Selected Q enters the existing writer path with the reviewed financial revision. Current funds/risk, profile, persistent kill and runtime/monitor/book gates remain authoritative. A race rejects without a smaller retry or search inside the writer. |
| P — Identity | Market trigger SHA binds opportunity/relationship, books/quality, fee proof, automation revision/fingerprint, mode and grid. It excludes balances, exposure, session consumption and P&L. Separate sizing SHA binds selected Q/grid/count/reasons, exact plan economics/fills, financial/risk/profile revisions and session debits. |
| Q — RequestId | Adaptive deterministic GUID binds session, key, trigger, selected Q, mode and sizing decision SHA. Fixed v1 derivation remains byte-compatible. Durable financial duplicate and market-input suppression remain separate. Deferred keys are not stamped as attempted. |
| R — Reconciliation | Historical v1 fixed and v2 adaptive proofs use their recorded policy/algorithm. Adaptive proof validation checks grid membership, candidate/rejection counts, selected plan digest, revisions and decision SHA; independent Phase 04D risk reconciliation remains. Current profile changes do not reinterpret prior entries. |

## S–Y. Behavioral evidence

| Item | Fixture result |
|---|---|
| S — Full depth | A: 10 @ .40 + 20 @ .43; B: 5 @ .50 + 20 @ .52; grid 5..25 step 5 selects **25**. Gross cost **23.35**, fees **.675035**, cost **24.025035**, expected payout **25**, expected profit **.974965**. Exact automatic economics equal the existing manual evaluation at 25. |
| T — Risk limit | Exact relevant cost-basis cap .063 × initial 100 allows a manual quantity of 12 and rejects 13. Adaptive grid 5..25 step 5 selects **10**, after four evaluations. Session-debit cap at the same fraction independently selects 10. |
| U — Depth limit | Book supports 13; grid 5..20 step 5 selects **10**, after rejecting 20 and 15 for insufficient depth. |
| V — Economics | Edge floor .049: 20 fails, **15** passes using exact depth/fees. Profit floor .5: quantity 5 fails while **25** passes. All-fail and minimum-5/only-4-affordable fixtures create no execution. |
| W — Non-monotonic | Primitive fixture admits 15 and 5 but rejects 20 and 10. Visits exactly 20, 15 and stops at **15**. |
| X — Cycle budget | Three monitored opportunities with 256-point all-rejected grids: first cycle considers two and performs exactly **512** evaluations; one key remains queued. Next cycle evaluates the remaining 256 points. No dropped key, permanent suppression or execution. Existing FeeAdjusted ranking order is retained. |
| Y — No acquisition | Paper API fixtures install rejecting spies for orderbook fetch, discovery, fee/metadata acquisition and websocket creation. Teardown asserts zero calls. Sizing preview makes no audit/financial changes, produces no manual ticket and cannot arm. Cached book version remains unchanged after execution. |

Additional safety evidence: deterministic adaptive kill barriers cover both SQLite writer orderings. The financial race selects 20, commits a manual 10, verifies that another 20 now fails while 10 would pass, then rejects the original automatic attempt without fallback. A separate diagnostic keeps the same market stamp but records a different financial revision, decision SHA and RequestId. Controlled book/fee changes at post-search validation discard the selected result and proof. Six corruption cases cover quantity, grid, count, financial revision, risk revision and fingerprint.

Real SQLite restart fixtures cover fixed/adaptive profiles with/without a kill latch. They retain history/settings but restore no session, sizing state or counters and perform no startup execution. A legacy-shape v1 JSON fixture removes newly introduced optional fields, applies the unchanged schema, verifies stored profile/execution data remains unchanged, saves a later adaptive profile, and reconciles the original fixed entry as Healthy. Prior migration-preservation and manual/settlement tests remain in the full suite.

## Z–AD. Actual verification

- **Z:** Full `scripts/verify.ps1` passed: `dotnet restore ArbitrageTrading.sln`, Release build with **0 warnings / 0 errors**, and `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore` with **774 passed, 0 failed, 0 skipped**. Breakdown: Domain 55, Application 272, Backend integration 428, Desktop 19 (47 added cases over the 727-test baseline). Focused pure sizing: 11 passed; focused adaptive API/concurrency/consistency/restart: 27 passed; desktop automation logic: 16 passed; focused WPF fixture test: 1 passed. `git diff --check` passed. All tests used isolated storage and offline market fixtures.
- **AA:** `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` passed: no model changes since the last migration. Existing versioned JSON columns support the new optional fields; no additive migration is needed. No previous migration was edited. Only isolated test storage was migrated.
- **AB:** `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive --format json` exited 0 with no vulnerable packages across 13 projects. The first sandboxed request was network-blocked; the permitted retry succeeded. Auditing stays enabled.
- **AC:** Ubuntu WSL is present; no `dotnet` on PATH or executable at `$HOME/.dotnet/dotnet`. Linux backend verification was **not run**; no Linux success is claimed. No SDK was installed.
- **AD:** XAML compiles; automated VM tests cover inactive suggestions, explicit save/arm, invalid/256-point grids, diagnostics, denial, single-attempt POST and stale access/navigation/key/mode/invalidation responses. WPF renders **18 PNG fixtures**, 9 scenarios × Light/Dark at 1440×1400 and 96 DPI: fixed profile, adaptive edit, selected, risk-limited, depth-limited, no admissible, armed, execution history and kill latch. Field visibility and layout bounds are asserted. Representative images were visually inspected; the new selector's Dark contrast was corrected and all fixtures regenerated. Files are under the task visualization directory `phase04f`. Manual mouse operation, additional DPI scales, screen-reader/complete accessibility testing and High Contrast were **not performed**.

## AE–AL. Limits and scope

- **AE:** Local cached snapshot simulation only. No guarantee of real fills or latency. BestEffort acknowledgment remains required for Polymarket. Whole-grid budget reservation can conservatively defer a search that might have succeeded early. A disarmed diagnostic uses a prospective empty session; an armed diagnostic uses current session history. Inputs may change after preview. Fixed/manual quantities do not resize. No Linux SDK was available; manual UI/DPI/accessibility checks remain unperformed.
- **AF:** No cross-opportunity allocator or portfolio optimizer added.
- **AG:** No AI, probability forecast, Kelly or utility sizing added.
- **AH:** No transaction-race auto-rescaling added.
- **AI:** No automatic settlement, SELL, close or exit path added.
- **AJ:** No exchange account or real credential required for implementation/tests.
- **AK:** No live order, signing, private-balance or private-position API added.
- **AL:** Changes remain uncommitted on `main`. No commit, push, merge or PR; no next phase started.

Suggested commit message: `Add deterministic adaptive paper sizing`
