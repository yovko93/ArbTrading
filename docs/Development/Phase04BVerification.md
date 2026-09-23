# Phase 04B verification — explicit paper resolution and settlement

Date: 2026-09-23. Runtime/API definitions: [PaperExecution](../Architecture/PaperExecution.md).

## A–C. Baseline and repository

Root: `C:\Users\Yovko\source\repos\ArbTrading`. Started on `main`, ordinary checkout, clean working tree, HEAD `ad3ed316443f9cbb508e372455403c85449e954c`. Its parent is Phase 04A `d60e9a819392bc4af9e68d9a7cd0aeafd9612a32`. Root AGENTS and the Phase 04A/04A.1 verification notes were read before editing. No branch, remote, Git history or existing migration was replaced. Only the remote name was printed. Existing ignore rules already exclude runtime databases/sidecars, credentials, logs, build and test artifacts.

The user reported both baseline CI jobs green. A read-only public GitHub API lookup confirmed the exact baseline run [35848009548](https://github.com/yovko93/ArbTrading/actions/runs/35848009548) completed successfully. `gh` was unavailable; the public REST lookup required no credentials. No workflow was triggered or changed. New Phase 04B CI remains pending publication by the user.

## D–F. Official sources and supported contract

Reviewed on 2026-09-23, starting with the [Polymarket index](https://docs.polymarket.com/llms.txt) and [Kalshi index](https://docs.kalshi.com/llms.txt):

- [Polymarket Resolution](https://docs.polymarket.com/concepts/resolution): standard winners have unit value and losers zero. Rules govern resolution; the page separately describes a rare unknown/50-50 result. Phase 04B excludes that exceptional case rather than interpreting every ambiguous market as half payout.
- [Polymarket Negative Risk](https://docs.polymarket.com/concepts/negative-risk): multi-outcome conversions and augmented outcomes have additional semantics. Negative-risk and arbitrary multi-outcome settlement are excluded.
- [Polymarket Manage Positions](https://docs.polymarket.com/trading/positions/manage): real redemption uses collateral adapters. Current documentation names pUSD; this implementation neither calls those adapters nor relabels historical paper USD/USDC balances. A ManualScenario credits one unit of the recorded paper currency for a winning share, not a claim about current wallet collateral or exchange redemption.
- [Kalshi Market Settlement](https://docs.kalshi.com/getting_started/market_settlement): ordinary yes/no winners receive a dollar per contract, with zero simple yes/no settlement fees. Scalar/sub-cent settlement has distinct rounding/fee mechanics. This implementation rejects quantities requiring sub-cent Kalshi payout rounding.
- [Kalshi Market Lifecycle](https://docs.kalshi.com/getting_started/market_lifecycle): closed, determined, disputed and finalized have different meanings; result may also be scalar. None of these statuses causes paper resolution. Undocumented void/cancel behavior is not guessed.

Supported subset: explicit ManualScenario, locally identified standard Yes/No binary, exactly one economic winner with decimal payout/share 1 and loser 0. Unsupported shapes, identities, negative risk, multi-outcome, scalar, invalid/ambiguous/void and exceptional payouts fail closed with `SettlementTypeUnsupported` (invalid selected native identity uses `OutcomeInvalid`). No settlement fee, gas, withdrawal, bridge or transfer charge is invented. Entry fees remain the historical recorded costs.

## G–O. Implementation and boundaries

Execution adds typed source/status/rejection/position states and pure checked payout and execution-economics rules. Contracts add settlement, performance and curve DTOs. Infrastructure adds immutable resolution/outcome entities, preview proof construction, atomic confirmation, reconciliation and derived performance. Backend adds authenticated paper routes, bounded tickets/diagnostics and small invalidations. Desktop adds generation/outcome selection, explicit confirmation, position/history filters and a selected venue/currency curve. Details and route limits are in the architecture document.

Migration `20260923105901_PaperSettlement` is additive: new resolution/outcome tables and nullable settlement facts, safe Open/default revisions, retained Committed enum values, native-definition snapshots, unique generation/market and workspace/request indexes, and restrictive foreign keys. Old position quantities, fee-inclusive cost, fills and execution plans are retained. Only isolated test databases were migrated.

A 30-second preview is nonmutating and binds actor/workspace plus generation revision, positions/revisions/quantities/costs, executions/states, previous resolutions, exact payout vector and balances. Confirmation reauthorizes Owner and recomputes the proof inside the SQLite writer transaction. Idempotency persists the entire confirmation fingerprint and returns the original result after restart or expiry; conflicting bodies reject. No client actor or outcome label is authoritative.

Confirmation writes resolution, payout mappings, settlement journal, cash credits, position closure, execution state/economics and audit together. Zero payouts retain the resolution, journal and loss with no fake cash entry. Proceeds stay in the original generation/venue/currency. A closed generation may settle old positions and report FullySettled, without changing active balances.

Captured deterministic complementary BUY proof governs compatibility: once both market facts are available, the selected legs must reproduce the original guaranteed payout. A contradiction rejects before financial mutation. Current relationships, ranking, books, fees and monitoring are not consulted; changing them after entry does not invalidate historical settlement. An execution starts Committed, becomes PartiallySettled when some legs resolve, then Settled. Original economics remain stored; final profit/return stay null while partial.

## P–R. Exact accounting and performance

The Phase 04A quantity-10 fixture retains gross cost 9.10, fees 0.26792 and total entry cost 9.36792. Both compatible scenarios settle exactly to payout 10 and final profit **0.63208**, with return `0.63208 / 9.36792` and payout difference zero.

| Scenario | Kalshi payout / realized P&L | Polymarket payout / realized P&L | Final profit |
| --- | --- | --- | --- |
| A wins | 10 / 5.832 | 0 / -5.19992 | 0.63208 |
| B wins | 0 / -4.168 | 10 / 4.80008 | 0.63208 |

For each position, payout is quantity times payout/share; realized P&L subtracts persisted fee-inclusive cost. After A settles, B's 5.19992 cost remains open and final execution profit is null. Winning/losing cash changes are asserted exactly in each venue bucket; no floating tolerance is used.

Curves derive from journal/settlement facts, allocating realized P&L when each position settles. Basket completion does not recognize earlier realization again. Realized performance means starting capital plus cumulative realized P&L, excluding open market value. Current cash and open cost remain separate. A dedicated curve fixture verifies `100 → 100.50 → 100.30` for +0.50 then -0.20, page carry-forward, and an independent USDC series. No USD/USDC sum or FX model exists.

## S–U. Integrity, concurrency and persistence tests

Migration-backed real SQLite tests cover genuine winning/losing settlement, partial/full execution state, exact cash, immutable conflicts, body idempotency, expiry, changed quantities, unsupported types, missing generations, integrity denial, history/curve paging and wrong-workspace access. Existing schema permits only Owner; the test asserts rejection of an unknown role and denial without ownership. Source spies reject discovery, orderbook, WebSocket, fee and relationship acquisition; all remain at zero.

Barrier races cover identical and conflicting resolutions, execution versus resolution, and reset versus historical settlement. Exactly one conflicting resolution can commit. If execution wins the race, the old resolution preview changes; if settlement wins, new exposure rejects `MarketAlreadyResolved`. Reset safely retains the old generation's settlement. A deliberate SQLite journal trigger failure verifies rollback of resolution, positions, cash and audit.

Reconciliation detects tampered payout, realized P&L, position status, settlement ledger credit, payout mapping, extra mapping, execution state and request fingerprint, marking Corrupt without repair. It validates mappings against historical native definitions and compares typed financial values rather than decimal JSON formatting. An actual backend restart retains resolution, positions, cash, performance and idempotency without replay. A Phase 04A schema fixture upgrades while preserving costs, quantities and Committed state, and can then settle.

## V–Y. Verification results

- Focused backend/view-model settlement suite: 32 passed before the final two rollback/extra-mapping cases were added. The subsequent focused rollback/reconciliation run passed 9/9, including those cases.
- `dotnet restore ArbitrageTrading.sln`: passed, auditing enabled.
- `dotnet build ArbitrageTrading.sln -c Release --no-restore`: passed, zero warnings/errors.
- Standalone full Release suite: 566 passed (Domain 55, Application 204, Backend integration 292, WPF 15). The first `scripts/verify.ps1` run also passed all 566. After adding a desktop duplicate-response test and the final selector-label rendering assertion, the final `scripts/verify.ps1` passed restore, Release build (zero warnings/errors), and **567 tests, zero failures/skips**: Domain 55, Application 204, Backend integration 293, WPF 15. This preserves the 531-test baseline and adds 36 tests.
- EF `has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build`: no changes since the last migration.
- `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive`: passed, no vulnerable packages across all 13 projects; auditing was not disabled.
- `git diff --check`: passed, including the final documentation update.
- Actual WSL Ubuntu attempt of `bash scripts/verify-backend.sh`: stopped at line 5, `dotnet: command not found`. No local Linux/.NET success is claimed.

## Z. UI checks actually performed

XAML builds. Automated view-model tests cover explicit confirmation, selection/outcome/generation/navigation/access invalidation, delayed preview rejection, lost-response request retention, duplicate result display, conflict display and one-attempt authentication failure. Rendering tests cover OpenPositions, ResolutionPreview, WinningScenario, LosingPosition, PartialExecution, FullySettled, SettlementHistory, PositivePnl, NegativePnl and SeparateCurrencies in both Light and Dark at 1440×2000/96 DPI. The fixture uses no exchange account or normal runtime storage. Representative final images were visually inspected; native dark selector/disabled-button chrome and selector labels were corrected and re-rendered. All 20 settlement captures are in the task visualization directory under `phase04b`, outside Git.

No real mouse workflow, physical monitor/DPI matrix or screen-reader/accessibility audit was performed. Controls are focusable and labeled; the line chart has an adjacent exact-value table. This is not a claim of a complete accessibility audit.

## AA–AF. Limits and completion state

Only supported binary paper scenarios are available. Old records without sufficient retained native metadata fail closed; no hidden metadata acquisition repairs them. Curves/history have bounded response pages; generation summaries/reconciliation still scan that generation's retained accounting facts, suitable for the expected low paper volume. Current Kalshi public fee uncertainty remains blocking for new executions but does not block settling historical entry facts.

No automatic real-result ingestion, price/status inference, mark-to-market, exchange account/credential requirement, real order, redemption, withdrawal or transfer API was added. No Phase 04C work. Changes remain uncommitted; no push, merge or PR.

## Changed-file inventory

- README.md
- docs/Architecture/PaperExecution.md
- docs/Architecture/Roadmap.md
- docs/Development/Phase04BVerification.md
- src/Arbitrage.Backend/PaperCoordinator.cs
- src/Arbitrage.Backend/PaperEndpoints.cs
- src/Arbitrage.Backend/Program.cs
- src/Arbitrage.Backend/Realtime.cs
- src/Arbitrage.Backend/SettlementCoordinator.cs
- src/Arbitrage.Backend/SettlementEndpoints.cs
- src/Arbitrage.Contracts/PaperContracts.cs
- src/Arbitrage.Contracts/SettlementContracts.cs
- src/Arbitrage.Desktop/Services/BackendClient.cs
- src/Arbitrage.Desktop/Services/BackendClient.Settlement.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.cs
- src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Settlement.cs
- src/Arbitrage.Desktop/Views/PaperTradingView.xaml
- src/Arbitrage.Desktop/Views/PaperSettlementView.xaml
- src/Arbitrage.Desktop/Views/PaperSettlementView.xaml.cs
- src/Arbitrage.Desktop/Views/PaperPerformanceChart.cs
- src/Arbitrage.Execution/PaperPlan.cs
- src/Arbitrage.Execution/PaperSettlement.cs
- src/Arbitrage.Execution/README.md
- src/Arbitrage.Infrastructure/PaperEntries.cs
- src/Arbitrage.Infrastructure/PaperStore.cs
- src/Arbitrage.Infrastructure/PaperPerformance.cs
- src/Arbitrage.Infrastructure/SettlementEntries.cs
- src/Arbitrage.Infrastructure/SettlementReconciliation.cs
- src/Arbitrage.Infrastructure/SettlementStore.cs
- src/Arbitrage.Infrastructure/Migrations/20260923105901_PaperSettlement.cs
- src/Arbitrage.Infrastructure/Migrations/20260923105901_PaperSettlement.Designer.cs
- src/Arbitrage.Infrastructure/Migrations/TradingDbContextModelSnapshot.cs
- tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj
- tests/Arbitrage.Backend.IntegrationTests/PaperApiTests.cs
- tests/Arbitrage.Backend.IntegrationTests/SettlementTests.cs
- tests/Arbitrage.Backend.IntegrationTests/SettlementPersistenceTests.cs
- tests/Arbitrage.Backend.IntegrationTests/SettlementDesktopTests.cs
- tests/Arbitrage.Desktop.Tests/SettlementWpfTests.cs
