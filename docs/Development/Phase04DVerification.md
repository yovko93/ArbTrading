# Phase 04D verification — paper capital and risk admission

Date: 2026-09-23. Architecture and exact API semantics: [Paper execution](../Architecture/PaperExecution.md#phase-04d-capital-and-risk-admission).

## A–C. Repository, baseline and CI

Root: `C:\Users\Yovko\source\repos\ArbTrading`. Work began on `main`, ordinary checkout, with a clean working tree at `86288ab2807b332befbbff51adea400849ce0d4c` (the supplied published Phase 04C.1 baseline). Its supplied Phase 04C parent is `8cc878eda554a847022faf3817d47ea7a9e2f60e`. Root AGENTS and prior paper architecture/verification reports were inspected. The existing origin name was inspected without printing a remote URL or credential. No repository, branch, worktree, remote, history or existing user changes were replaced.

A read-only GitHub API query observed [baseline run 35878392528](https://github.com/yovko93/ArbTrading/actions/runs/35878392528), `Phase 01A verification`, completed with conclusion success. No workflow was triggered. This is baseline CI, not CI for the uncommitted Phase 04D changes.

## D. Changed scope

Execution adds pure admission models and evaluator. Infrastructure adds policy persistence, a coherent accounting read, transaction-time admission and recorded-proof reconciliation. Backend adds owner-confirmed policy APIs, status, bounded diagnostics and preview/confirm mapping. Contracts carry exact decisions and nullable historical proofs. Desktop adds policy editing, explicit confirmation, projected headroom, rejected checks, REST refresh and stale-response/access guards. One additive migration adds the policy table and nullable proof fields. Tests and the README, roadmap, execution README and paper architecture are updated. Previous migrations and verification reports remain unchanged.

New files:

- `src/Arbitrage.Execution/PaperRisk.cs`
- `src/Arbitrage.Infrastructure/PaperRiskStore.cs`
- `src/Arbitrage.Infrastructure/Migrations/20260923154728_PaperRiskPolicy.cs` and its Designer
- `src/Arbitrage.Backend/PaperRiskEndpoints.cs`
- `src/Arbitrage.Backend/PaperRiskDiagnostics.cs`
- `src/Arbitrage.Contracts/PaperRiskContracts.cs`
- `src/Arbitrage.Desktop/Services/BackendClient.Risk.cs`
- `src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Risk.cs`
- `src/Arbitrage.Desktop/Views/PaperRiskView.xaml` and code-behind
- `tests/Arbitrage.Application.Tests/PaperRiskTests.cs`
- `tests/Arbitrage.Backend.IntegrationTests/PaperRiskApiTests.cs`
- `tests/Arbitrage.Backend.IntegrationTests/PaperRiskDesktopTests.cs`
- `tests/Arbitrage.Desktop.Tests/PaperRiskWpfTests.cs`
- this report

Existing files extended: `PaperPlan`, `PaperStore`, `PaperEntries`, EF model snapshot, `PaperCoordinator`, `PaperEndpoints`, Backend `Program`, `PaperContracts`, `BackendClient`, `PaperTradingViewModel` and its Settlement partial, `PaperTradingView`, linked-source integration test project, `PaperTests`, `PaperApiTests`, `PaperConcurrencyTests`, `PaperDesktopTests`, `PaperPersistenceTests`, `PaperWpfTests`, plus the four documentation files above. Existing .gitignore already covers build/test artifacts, local databases/sidecars, logs, credentials and runtime config. Tracked sensitive/runtime filename inspection found no matches.

## E–H. Policy, validation and evaluator

The workspace row has schema version 1, GUID revision, UTC timestamps, authenticated updater, exact decimal limits and canonical SHA-256 fingerprint. Limits persist as scalar owned columns. A successful update always gets a new revision and audit snapshot. Create expects null revision; update must match. There is no startup/default policy and no desktop preference authority. An absent profile blocks only new exposure. Reset/restart retain the profile.

The server rejects invalid fractions, inconsistent concentration hierarchy, invalid integer counts, quantities above the hard 1,000 maximum, edges below the hard 0.001 floor, negative profit thresholds and missing confirmation. No clamping occurs. Profile fingerprint/version/structural corruption produces IntegrityFailure and prevents entry. Owner replacement remains explicit.

`PaperRiskEvaluator` is pure Execution code with immutable inputs/results and checked decimal arithmetic. It receives the exact already eligible requested plan; existing relationship, depth, source, freshness, skew, fee, currency, funds and Paper-mode rules remain authoritative. It does not recalculate fee formulas or walk liquidity. No connector, network, SQL, WPF or mark dependency was introduced in the inner layer.

## I–L. Capital and counts

Each exchange/currency uses its own InitialCash. Reserve uses cash after the exact fee-inclusive plan debit. Single-execution debit uses `PaperPlan.Debits`. Total, native-market and native-instrument/outcome limits use OPEN fee-inclusive cost basis plus proposed fills. No marked equity or unrealized P&L is used, and USD/USDC are never netted.

Distinct materialized open positions count once even when a fill adds to an existing instrument. Committed and PartiallySettled executions count; Settled does not. Captured relationship IDs limit repeated entry. Top relationship diagnostics are bounded to 20. Open-state reads use generation/status indexes and reject unsupported states above 1,000 open positions/executions. Cached real exchange depth is never changed by paper fills.

## M–Q. Preview, proof, atomicity and idempotency

Preview preserves exact fills/economics even on risk rejection, with typed violations and current/proposed/projected/limit/headroom values. Funds display and risk assessment use the same coherent SQLite snapshot. The ticket captures policy revision/version/fingerprint, financial revision and accounting decision snapshot. Client risk fields are never authoritative.

Confirmation revalidates eligibility/funds and recomputes risk under the SAME immediate SQLite writer reservation as the ledger mutation. Changed policy rejects RiskPolicyChanged even if looser; newly failed limits return the specific violation set; otherwise changed financial revision requires a fresh preview (FinancialStateChanged). Every reviewed headroom change is therefore fail-closed. The final existing orderbook commit guard is preserved.

Durable RequestId/body recovery precedes new admission. Identical committed retries remain recoverable after policy changes, preview expiry and backend restart; changed bodies conflict. New execution records bind Approved proof to plan ID, quantity, cost, generation and policy revision. Legacy nullable proofs remain valid historical facts. Reconciliation checks recorded metadata/approval/amounts without applying today's policy to old executions.

Barrier tests demonstrate exactly one successful entry for competing total-cost, reserve, market, open-execution and relationship limits. A separate writer test uses two distinct native instrument baskets: both preview at two positions, but the second would create a third; at most one commits. Concurrent policy writes with the same expected revision produce one success and one conflict. Existing rollback/cancellation, reset and durable duplicate tests remain included. The former two-funded-requests test now expects a fresh preview after the first financial revision change, documenting the intentional 04D behavior.

## R–S. Tightening, independence and migration

Valid tightening is allowed even when current state becomes OverLimit. Saving policy does not touch cash, executions, positions, settlement or realized P&L. New exposure rejects until settlement, reset or policy change restores eligibility. Tests settle both legs while OverLimit and while NotConfigured, assert partially settled executions still count, and confirm valuation is available. Network spies cover these operations and fail on external acquisition.

Migration `20260923154728_PaperRiskPolicy` adds the policy table, nullable proof fields and open-state indexes, replacing only a redundant generation index. The isolated preservation fixture downgrades to the exact published 04C.1 schema, snapshots all application tables and baseline columns, upgrades, and compares every retained row. Existing paper execution, fills, positions, partial resolution, cash ledger, fees, relationships and ownership are preserved; old risk fields remain null. The remaining legacy leg settles and reconciles successfully afterward. Earlier migration fixtures also retain catalog, monitoring and audit data through the complete migration chain. Normal user storage was not migrated.

## T. Exact numerical fixtures

Equality is accepted throughout:

| Check (initial cash 100) | Accepted | Rejected |
| --- | --- | --- |
| Reserve 20% | debit 80 leaves 20 | debit 81 leaves 19 |
| Single debit 10% | 10 | 10.01 |
| Open cost 60%, current 45 | add 15 | add 15.01 |
| Market 20%, current 12 | add 8 | add 8.01 |
| Instrument 15%, current 10 | add 5 | add 5.01 |
| Entry edge floor .003 | .003 | .0029 |
| Entry profit floor 1 | 1 | .99 |

Separate-bucket fixture: Kalshi USD initial/available 100 with debit 10 passes; Polymarket USDC initial 20, available 8, debit 5 leaves 3 against reserve 4 and rejects the whole basket. Count tests distinguish repeated instruments, new instruments, unrelated relationships and exact count equality. Overflow and invalid/missing profiles are typed failures.

## U. API and authentication

`admission-policy` GET/PUT, `admission-status` GET and `admission-diagnostics` GET share the existing authenticated paper membership filter. Mutation also rechecks Owner inside the writer transaction. APIs accept no actor identity. Tests cover 401, cross-workspace 403, unknown generation 404, invalid settings 400, confirmation, revision conflict, configuration/status states and stale previews. The existing membership schema only supports Owner; nonmember mutation is denied and unsupported roles remain database-invalid. The original valuation `/risk` route is unchanged.

## V–Y. Verification

- Focused pure evaluator: 20 passed.
- Initial focused risk API: 14 passed; expanded migration/desktop/concurrency checks subsequently ran in the full suite.
- Standalone full solution run before final coherence/proof-test additions: 665 passed, zero failed/skipped (Domain 55, Application 243, Backend 350, Desktop 17).
- Release solution build: zero warnings and zero errors. Restore passed.
- Final `scripts/verify.ps1` passed restore, Release build (zero warnings/errors) and **668 tests, zero failed/skipped**: Domain 55, Application 243, Backend integration 353, Desktop 17. This includes the final coherent-preview correction and three risk-proof corruption cases. The baseline had 616 tests; 52 tests were added.
- EF `has-pending-model-changes`: no changes since the last migration.
- NuGet vulnerability audit with transitive packages: no vulnerable packages across all 13 projects; auditing remains enabled.
- Actual Ubuntu WSL `bash scripts/verify-backend.sh` failed at line 5: `dotnet: command not found`. No local Linux success is claimed.
- `git diff --check` passed.

## Z. UI checks actually performed

XAML compiled. Automated view-model tests cover inactive suggestions, explicit save confirmation, exact percentage parsing, malformed locale input, one mutation attempt on 401/403/conflict, status, rejected/approved preview confirmation and delayed generation/navigation/access responses. Existing desktop preview/lost-response tests remain included.

WPF renders cover nine states in BOTH Light and Dark: NotConfigured, WithinLimits, OverLimit, cash reserve, market exposure, position count, approved preview, rejected preview and policy edit. Eighteen risk PNGs were produced at 1440×1400 / 96 DPI under `C:\Users\Yovko\.codex\visualizations\2026\09\21\01a0c58f-64af-7992-bd42-49bc8367e42c\Phase04D`. Light approved and Dark rejected fixtures were visually inspected. Fixture assertions check confirmation availability. Manual mouse workflow, other DPI scales and a dedicated accessibility audit were NOT performed; no claim is made for them.

## AA–AE. Limitations and completion state

This policy is deterministic admission for exact user-selected paper requests. It is not a recommendation, optimal sizing model, market-impact simulator or forecast. The existing five-second preview expiry remains. Larger legacy open states fail closed at the documented bound. Current profile comparisons on historical generations are diagnostics, not historical policy reevaluation. The current local schema has Owner only. UI fixtures do not establish a manual end-to-end usability or accessibility result.

No automatic sizing, automatic paper execution, opportunity ranking changes, live order submission, real exchange balance access or Phase 04E work was added. No real exchange accounts or credentials were needed. All work remains uncommitted; no commit, push, merge, pull request, branch switch, stash, reset or history rewrite was performed.
