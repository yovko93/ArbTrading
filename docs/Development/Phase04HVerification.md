# Phase 04H verification — operational paper burn-in readiness

Phase 04H prepares an operator procedure. It does **not** report a 24-hour production burn-in, live fills, or a CriteriaMet result. Starting baseline: `e5f48587ab97a99795936b837aeadb74c24b9e1a` on `main`, clean ordinary checkout at `C:\Users\Yovko\source\repos\ArbTrading`. The existing `origin` was not altered and its URL was not printed.

## Baseline and scope (A–D)

| Item | Result |
| --- | --- |
| A — starting state | Git root `C:\Users\Yovko\source\repos\ArbTrading`; `main`, HEAD above, no initial uncommitted files. Root `AGENTS.md` applied. |
| B — exact-HEAD CI | [Run 35981990311](https://github.com/yovko93/ArbTrading/actions/runs/35981990311) for the exact SHA above: overall **Success**, Windows **success**, Linux backend **success**. This validates the published 04G baseline, not these uncommitted 04H changes. |
| C — scope | Operator documentation plus one integration **test-only** smoke rehearsal. No production code, migration, dependency, gate, threshold or runtime configuration change. |
| D — changed files | `docs/Operations/PaperBurnInRunbook.md`, `docs/Operations/PaperBurnInDailyChecklist.md`, this report, `README.md`, `docs/Architecture/Roadmap.md`, and `tests/Arbitrage.Backend.IntegrationTests/PaperReliabilityTests.cs`. |

## Operator findings (E–Q)

| Item | Source-grounded finding |
| --- | --- |
| E — prerequisites | Owner-authenticated local Paper backend, migrated private SQLite, `Healthy` active generation, valid saved risk and automation profiles, clear reviewed kill state, current fee/source inputs, monitoring `Running`, campaign `Collecting`, and explicit arm. `POST /paper/reconcile?generationId=` is the existing explicit financial integrity check; there is no WPF button. |
| F — order | Backend → paper reconciliation/health → risk → automation profile → kill review → realtime books/fees → monitoring → campaign → coverage review → `Arm Auto Paper…`. Campaign start precedes arm so the observer can capture its session and activity. |
| G — freeze | Operator records commit and available application version, UTC time, generation and venue/currency balances, risk version/revision/fingerprint/all limits, automation version/revision/fingerprint/mode/grid/all caps/quality, kill state/revision/reason, monitoring profile/coverage, fee profile/revision where exposed/state, and realtime quality. Campaign notes hold only a short non-secret reference; full freeze is a separate private record. No report Git identity exists. |
| H — source quality | `RequireRealtime=true` is required by profile validation. Eligible deterministic two-BUY complementary strategies need current actionable realtime books, Kalshi `Continuous`, Polymarket `BestEffort` only with saved explicit `AllowPolymarketBestEffort`, `Detected` and `FeeAdjustedDetected`, plus risk/session/edge/profit checks. REST is diagnostic for Auto Paper. |
| I — fees | The fee-adjusted lane requires known resolved fees. Kalshi public official-fee conflict can yield gross-only diagnostics without automatic entries. `GET /fees/profile` exposes the profile name; opportunity fee results expose `ProfileRevision` and schedule fingerprints. Do not alter assumptions to induce entries. |
| J — first campaign | Retain saved Fixed or Adaptive settings, paper balances and risk. Name `BurnIn-01`, attach a short non-secret freeze reference, start explicitly, evaluate baseline, then explicitly arm only after verifying live state and coverage. No automatic campaign management. |
| K — daily | Review state/coverage/quality/fees, generation, kill, session, funnel, sizing, execution/rejections, gaps/faults/invariants, balances by venue/currency; use `Evaluate Now…` daily and at meaningful transitions, not per tick. |
| L — emergency | `EMERGENCY STOP` latches kill; preserve local DB, logs, report/events and ordering. Investigate before `Reset kill switch…`; reset stays disarmed. Worker fault, Corrupt or invariant violation requires investigation, not gate changes. |
| M — restart | Explicit Disarm and evaluation/export, stop and verify process exit, back up while stopped, restart, refresh. Clean shutdown checkpoints; abrupt loss can mark gap. Campaign resumes collection but monitoring is Stopped and Auto Paper Disarmed; re-start/re-arm explicitly after review. |
| N — gaps | Persistence failure, uncertain shutdown, retention/scan bounds or clock/revision ambiguity can mark `EvidenceGapDetected`; a gap blocks CriteriaMet and should be archived/investigated rather than erased. |
| O — completion/export | Explicit Disarm before `Complete…`; completion freezes final evaluation but does not disarm. `Export Report` writes private `reliability/<workspace N>/<campaign N>/report.json` under effective backend data directory, atomically; archive with separate commit/config freeze. |
| P — new campaign | Material economics/safety/eligibility/monitoring/fee changes, paper generation reset/integrity repair, software commit/binary change, or unusable gap: preserve old campaign/report, change, then start a new one. Code does not enforce a full automatic configuration freeze. |
| Q — investigate | Invariant violation, Corrupt/unknown financial integrity, fault/latch, unexplained disarm, gap/persistence/export failure, unexpected duplicate suppression or report instability. |

## Isolated rehearsals and boundedness (R–V)

| Item | Evidence |
| --- | --- |
| R — smoke | New `Operational_smoke_campaign_observes_explicit_arm_execution_disarm_and_frozen_export` uses migrated isolated SQLite, fake current books and local clock. It checks monitoring Running, campaign Collecting, explicit arm, candidate input and one automatic paper execution, invariant-satisfied evaluation, explicit disarm, completion, report export, and stable report/export after later clock and collector activity. It is **not** production evidence. |
| S — restart | Existing `Restart_excludes_one_hour_downtime_and_never_rearms` covers persisted campaign recovery: 2h before + 1h downtime + 2h after = 4h backend observed, only 2h armed, Auto Paper Disarmed after restart. No startup arm/execution path exists. |
| T — kill | `Kill_latches_before_return_reset_stays_disarmed_and_manual_execution_remains_explicit` tests latch, denied arm, reset remaining disarmed and explicit future re-arm. `Execution_before_kill_latch_is_valid_even_with_equal_utc_timestamps` tests writer-ordered campaign event/invariant. `Tampered_durable_facts_produce_sticky_violation_without_repair(kill)` tests an invalid historical order. |
| U — freeze/gap | `Explicit_lifecycle_runtime_pause_frozen_report_and_financial_isolation` checks report immutability and byte-stable re-export. Existing automatic fixture checks post-completion financial activity does not alter a report. `Storage_failure_is_isolated_and_recovery_records_gap` and `Uncheckpointed_gap_is_visible_and_unknown_policy_cannot_pass` cover InsufficientEvidence without financial repair. |
| V — persistence/load | `PaperReliabilityCoordinator` checkpoints each 30 seconds and schedules evaluations about every 5 minutes: a continuously collecting 24h window has about 2,880 checkpoint **updates** and 288 evaluation attempts. It does not insert a row per book tick or sizing candidate. Per campaign, event and interval limits are 10,000 each, at most 100 latest evaluation rows retained; interval rows are per lifecycle/backend interval, not checkpoint. Scan bounds mark gaps rather than claim complete evidence. `Event_retention_is_bounded_and_marks_gap` tests the 10,000-event bound. This is a source-based row/cadence estimate, not a measured file-size benchmark: SQLite pages/WAL, reports, financial ledger, monitoring alerts, audit and logs have separate growth/retention behavior. Do not infer a whole-database 24h size cap. |

## Verification and boundaries (W–AB)

| Item | Result |
| --- | --- |
| W — local verification | Focused smoke: **1 passed, 0 failed**. EF model check: no pending changes. Current-source NuGet audit: no vulnerable packages in all 13 projects. `git diff --check`, new-document whitespace, relative links, exact 26-heading order and PowerShell reconciliation example parse passed. Full `scripts/verify.ps1` result is recorded below. |
| X — environment | No normal user database was opened/migrated or backed up. No actual multi-day market campaign was run. Polymarket ordinary TLS access and Kalshi credential availability remain operator-environment conditions; no bypass or credential import was attempted. |
| Y — thresholds | `PaperReliabilityPolicy` standard v1 minimums (24h backend, 24h monitoring, 4h armed/healthy, 100 candidate inputs, 25 distinct, 20 sizing attempts, 5 automatic paper executions, 0 fully settled minimum, 0 unexpected worker faults maximum) and all gates are unchanged. |
| Z — synthetic activity | The only new fixture is in `tests/`; no production synthetic market/candidate/execution path or quick-pass policy was added. |
| AA — exchange boundary | No new live/private exchange client, account-balance query, signing, order submission, SELL/close, or automatic settlement capability was added. |
| AB — repository | Changes remain uncommitted in the current checkout; no commit, push, merge, branch switch, remote change or PR. |

### Final command results

`scripts/verify.ps1` **passed outside the filesystem sandbox**: restore and Release build succeeded; 812 tests passed, 0 failed, 0 skipped (Domain 55, Application 277, Backend integration 460, Desktop 20). The new isolated smoke test is included in the 460. `dotnet --info` reported SDK 10.0.401 on Windows. The first sandboxed attempt built with 0 warnings/errors, then had 12 backend and 4 desktop fixture failures because Windows private ACL/protected temp-directory operations were denied in that sandbox (Domain 55, Application 277, Backend 448/460, Desktop 16/20). Those were environmental access errors in isolated temp/process/DPAPI fixtures. The network-enabled NuGet audit succeeded with no vulnerable packages after the first sandboxed audit could not reach nuget.org. `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` passed; `git diff --check`, link resolution, heading order and PowerShell snippet parse passed.
