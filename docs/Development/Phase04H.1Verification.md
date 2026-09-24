# Phase 04H.1 — Trading and Portfolio split

Verification date: 2026-09-24. Desktop-only correction; no burn-in campaign was started.

| Item | Result |
| --- | --- |
| A — repository baseline | Root `C:\Users\Yovko\source\repos\ArbTrading`, existing checkout on `main`, starting HEAD `81ac26a472b0120d75fe1d71f0356c62e6600adc`. Working tree initially clean. Root AGENTS.md applied. Existing `origin` inspected without displaying credentials or changing configuration. |
| B — baseline CI | Exact-HEAD [GitHub Actions run 35986897732](https://github.com/yovko93/ArbTrading/actions/runs/35986897732) was completed/success before editing: Windows and Linux backend both successful. This is baseline evidence, not CI for these uncommitted changes. |
| C — root cause | Shell mapped both destinations to the same PaperTradingViewModel, whose sole DataTemplate rendered PaperTradingView. Changing the title did not change the page. |
| D — architecture | Distinct PaperTradingPageViewModel and PaperPortfolioViewModel navigation objects share the existing PaperTradingViewModel as an internal authenticated DTO/command coordinator. Separate WPF views bind to that coordinator. No second financial store; REST remains authoritative. |
| E — changed files | See inventory below. Production changes are confined to Desktop; remaining changes are tests and documentation. |
| F — Trading | Risk policy, automatic paper controls/sizing, generation initialization/reset, compact available funding, selected opportunity, requested quantity, snapshot preview/fills and explicit confirmation. No holdings, settlement, valuation or retained history browser. |
| G — Portfolio | Full venue/currency balances, generation context/history, open/settled position filtering, manual scenario resolution, settlement history, realized performance/curve, executable valuation, unrealized P&L/coverage/book value, execution history and selected details. No entry, risk editor, generation mutation or automation controls. |
| H — shell mapping | Trading → PaperTradingPageViewModel → PaperTradingView; Portfolio → PaperPortfolioViewModel → PaperPortfolioView. Paper Reliability retains PaperReliabilityViewModel → PaperReliabilityView. Obsolete direct coordinator template removed; legacy fallback now accurately describes unavailable paper services. |
| I — activation/polling | One shared active-page discriminator and versioned two-second loop. Trading reads account/risk/automation; Portfolio reads account/history/settlement/performance/valuation. Switching invalidates the old loop and its results; leaving both stops page polling. Repeated activation is idempotent. Navigation calls no backend mutation or disarm. |
| J — opportunity routing | Both Opportunities and Continuous Monitoring PaperRequested select the opportunity and navigate specifically to Trading. Shell test covers both callbacks. |
| K — preview lifecycle | Leaving Trading increments preview revision and clears the preview/pending confirmation. Returning cannot restore an actionable old preview. Backend five-second expiry remains unchanged. |
| L — access and late responses | Shared workspace/access/backend-instance invalidation clears private data for both views. Context, poll version and operation revisions reject late account/history/settlement/curve/risk/preview/valuation results. Settlement sequential reads stop issuing follow-on requests after page/context changes. Initialization presentation also checks navigation version. Deterministic delayed-response tests cover preview, history and valuation invalidation; no arbitrary sleeps. |
| M — WPF content | Composition assertions require risk/automation/entry controls only in Trading, and balance/holdings/history/settlement/valuation views only in Portfolio. Existing execution-history fixture now renders Portfolio. Shell identity, title, active-page ownership and Reliability separation are asserted through observable navigation. |
| N — rendering | Populated Trading and Portfolio rendered in Light and Dark at 1440×3200. All four split-page images inspected: distinct headings, funding versus full balances, entry controls versus accounting sections, readable theme contrast. Cards, collapsible Portfolio sections and scrolling retained. Captures are local ignored artifacts under `artifacts/phase04h1/split-*.png`. |
| O — tests | Full `scripts/verify.ps1`: exit 0, **816 passed**, 0 failed, 0 skipped (Domain 55, Application 277, Backend Integration 463, Desktop WPF 21). Focused shell/paper/risk/valuation tests: 33 passed; focused WPF: 2 passed. Full backend tests took 5m20s; WPF 2m53s. |
| P — build | `scripts/verify.ps1` performs solution restore, Release build and full solution test with `--no-build --no-restore`. Restore succeeded; Release build: 0 warnings, 0 errors. Windows verification uses normal permissions for ACL/process fixtures. |
| Q — EF consistency | `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build`: exit 0; no model changes since last migration. |
| R — NuGet audit | `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive`: exit 0; no vulnerable packages in any project given current sources (including NuGet.org). |
| S — documentation | Burn-in navigation now assigns entry/risk/automation and generation mutations to Trading; balances/holdings/history/settlement/valuation to Portfolio. README's three shared-page references corrected. Roadmap did not imply shared pages and was left unchanged. |
| T — financial boundaries | No backend endpoint, execution/risk/automation/sizing/settlement/valuation/reliability engine, ledger, authentication policy or financial semantics changed. Desktop references remain unchanged. No live order or burn-in was initiated. |
| U — schema | No migration or model change. |
| V — UI limits | Existing dense forms and wide financial grids still require scrolling on smaller windows. Portfolio sections can be collapsed. Financial DTOs and commands remain in a shared coordinator; the separate page objects control navigation/lifecycle and distinct views control exposed presentation. Captures use deterministic simulated data, not a live burn-in. |
| W — Git state | Changes remain uncommitted on the existing main checkout. No branch switch, reset, clean, stash, commit, push, merge or PR. |

Changed-file inventory:

- `src/Arbitrage.Desktop/ViewModels/PaperPageViewModels.cs` (new distinct page identities).
- `src/Arbitrage.Desktop/ViewModels/ShellViewModel.cs` (mapping and activation).
- `src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.cs`, `.Risk.cs`, `.Settlement.cs` (page-specific reads and navigation guards).
- `src/Arbitrage.Desktop/Views/MainWindow.xaml`, `PaperTradingView.xaml`, `TradingView.xaml` (templates, entry layout, fallback).
- `src/Arbitrage.Desktop/Views/PaperPortfolioView.xaml` and `.xaml.cs` (new accounting page).
- `tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj`, `DesktopShellTests.cs`, `PaperDesktopTests.cs` (source linking and lifecycle/navigation tests).
- `tests/Arbitrage.Desktop.Tests/PaperWpfTests.cs`, `PaperPageSplitWpfTests.cs` (ownership and populated render fixtures).
- `README.md`, `docs/Operations/PaperBurnInRunbook.md`, this report.

Local verification logs and captures are ignored build artifacts, not runtime financial evidence. No repository hygiene changes were needed. `git diff --check` passed.
