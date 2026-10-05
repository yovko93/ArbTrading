# UI-03 verification — final Desktop product polish

A. **Starting repository:** `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on `main`; clean at `5156c5422dae813bf831a6c59a047c95dd6e9e22` (`Extend UI design system across operational pages`). Existing origin inspected with credentials suppressed. No branch, history, remotes, or Git metadata changed.

B. **Baseline CI:** exact-head **Build and portable distribution** [run 37240090485](https://github.com/yovko93/ArbTrading/actions/runs/37240090485) completed **success** before implementation.

C. **Changed files (34 total: 25 Desktop, eight test files, one report):** Desktop project metadata; shared semantic converter, typography, notice resources; shell and Settings; bounded read-notice presentation in Paper Trading/Risk/Auto Paper, Monitoring, Relationships, Opportunities and Reliability; busy labels on existing exposed states; read-only cancellation styling; focused tests and shared fixture visibility; this report. Full inventory is recorded below after verification.

D. **Spacing:** retained shared workflow cards/fields/metrics and existing page geometry. Unified neutral notice spacing/padding and hid recovered empty notices. Most significant minimum-window correction: footer feedback stays on one line, with the full message in its tooltip, instead of consuming multiple lines of page height. Shell content has an explicit theme background.

E. **Typography:** shell title/eyebrows and market detail heading use existing hierarchy. Added reusable card-heading, wrapping identifier, and busy-text styles. Settings uses shared card headings; build/source identifiers use Consolas. Existing financial density remains; no font-size reduction.

F. **Semantic audit:** Running/Available/Approved are green; Unknown amber; Denied/kill switch red; unfamiliar states neutral. Added corresponding shared badge mappings and synchronized converter mappings. Read-only catalog sync, candidate-job, evaluation and fee-refresh Cancel actions are secondary. Reset kill switch is amber because it removes an entry safety latch; EMERGENCY STOP remains text-labelled red. Existing Stop/Disarm/Reject/remove/reset-generation actions and confirmations remain unchanged. Contextual future live-capability labels retain neutral presentation; this UI does not advertise current live trading.

G. **Stale notices:** reproduced Risk's unavailable notice surviving a successful WithinLimits/Approved read. Corrected Risk and Auto Paper initial/invalidation/read failures; Monitoring and Relationships access/read feedback; opportunity result-read failures; Reliability access/read failures; account-read/access feedback in Trading/Portfolio. `ReadNotice` remembers only obsolete read feedback, clears it on a guarded successful read, and leaves later form/command feedback intact. Operation failures forget read markers so an identical generic message cannot be mistaken for an old read failure. Reliability telemetry failures remain visible while reported; historical report-read failure is not cleared when no report was recovered. Removed the initial "Stopped by default" monitoring assertion and initial paper-account initialization instruction from durable notices. Analytics, catalog, credential and fee reads already replace their availability notice on success; valuation already replaces its notice with current snapshot warnings. No business state, request, actor, cancellation, confirmation, availability guard or polling schedule changed.

H. **Tables and controls:** all original data/command contracts remain checked by UI-02A/UI-02B tests. Retained horizontal scroll and column inventories, row selection/contrast, expander defaults, and tab selection. Removed the older Auto Paper page-local ComboBox template in favor of ThemedComboBox; SelectedValue/Tag, automation name and popup retained. Dense Trading tables retain their existing intentional density. Wide financial tables keep horizontal scrolling.

I. **Empty states:** existing dedicated no-data/unavailable cards retained across operational pages. Recovered Risk/Auto notices and Trading notice banners collapse when empty. Read failure stays visible until the appropriate authoritative read succeeds. Busy text uses exposed Loading/Busy state only; no progress percentages or new asynchronous behavior.

J. **Window branding:** existing Arbitrage Trading title, sidebar mark and title/taskbar ICO retained.

K. **Icon:** original asset retained; nine square frames 16, 20, 24, 32, 40, 48, 64, 128, 256. WPF decode and transparency checks passed. Direct 16/32/48/256 inspection in dark/light contexts shows recognizable blue/green opposing arrows, transparent corners, and no embedded white square.

L. **Executable/published icon:** Release apphost, self-contained staged apphost, and apphost extracted directly from the final ZIP each contain all nine icon resource sizes. Windows PE resource inspection confirmed every frame payload has the same SHA-256 as the original ICO frame. The existing project ApplicationIcon and WPF pack-resource wiring are unchanged.

M. **Product information:** Settings/Application now contains a small mark and product card: actual Desktop assembly version, exact informational build identity, base source commit when present, actual process RID/architecture. Existing validated distribution/runtime/signing summary immediately follows it. No credentials, token, key path or invented publisher is added.

N. **Identity:** assembly version is explicitly labelled separately from informational version/source commit. Development assembly source identity is the SDK-generated baseline SHA; portable builds preserve the existing publisher's SHA/dirty identity for both Desktop and Backend. No release/versioning process or backend contract changed. Product = Arbitrage Trading; Description = Arbitrage Trading Desktop. Company attribute generation disabled rather than inventing a company.

O. **Splash:** intentionally omitted. Fixture startup and extracted-package smoke use the existing responsive shell; no meaningful blank-startup evidence justified new splash or startup logic.

P–Q. **Dark and Light:** all eleven actual navigation destination views inspected with fixture data: Dashboard, Market Explorer, Market Matching, Opportunities, Strategies, Trading, Portfolio, Analytics, Paper Reliability, Logs & Diagnostics, Settings. Monitoring tab, About card, busy/empty/error/evidence states inspected separately. Captures are ignored local evidence under `artifacts/UI03-renders`; no pixel-equality golden test.

R. **Minimum:** unchanged 830×590 window; each destination rendered through the real MainWindow/navigation/templates in both themes. Checked usable page height, action horizontal bounds, disabled outer scroll extents, sidebar selection/scroll, selector popup resources and top/bottom page scrolling. Footer correction restores more than 200 DIPs of page area in the fixture. Forms and tables retain scrolling; no minimum-size increase.

S. **DPI:** RenderTargetBitmap samples at 96/120/144 DPI (100/125/150%) inspected. These are synthetic render-density checks, not a change of Windows monitor DPI or proof of per-monitor transition behavior. No DPI-specific layout hacks introduced.

T. **Accessibility:** navigation automation name and two-way selection preserved; existing form names and command contracts retained, EMERGENCY STOP stays text labelled, recovered/busy feedback uses polite live-region presentation. Shared keyboard outlines, popup navigation, disabled text and selected table foreground/background are covered by existing fixture tests. Status text always accompanies color. This is a bounded visual/structural pass, not a claim of a full assistive-technology certification.

U. **Focused:** 64 selected Desktop tests passed, zero failures/skips (UI-01/02 contracts, themes, semantic/disabled states, Risk/Auto and new UI-03 checks). After the final Monitoring-tab capture correction, the six UI-03 cases passed again. There are six new cases, with loops over themes/destinations/sizes rather than hundreds of brittle pixel tests. Ninety UI-03 PNGs include the shell, minimum top/bottom, busy, Monitoring, About and scaled renders. Final TRX: `UI03_focused_net10.0_20261005015549.trx` and `UI03_final_sweep_net10.0_20261005015718.trx`.

V. **Full tests:** 965 passed, zero failed/skipped: Domain 55, Application 277, Backend integration 520, Desktop 113. The unmodified `scripts/verify.ps1` ran solution restore, Release build and unfiltered no-build/no-restore solution tests as part of the canonical publish pipeline. One completed full test execution. The initial solution build stopped before tests/publishing because the integration project links Desktop source explicitly and lacked the two new helper links; the test project was corrected and verification retried.

W. **Warnings/errors:** final solution build zero warnings/errors; publish completed without warnings/errors. The initial build had eight missing-helper compile errors; all were resolved by two Compile links in the integration test project. No production/backend dependency changes were needed. `git diff --check` passed. Source/test hashes were unchanged through completed verification and packaging.

X. **EF:** `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` exited 0: no changes since the last migration.

Y. **NuGet:** `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` exited 0; all 13 projects reported no vulnerable packages against the current NuGet sources.

Z. **Unsigned package:** one canonical `./scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot <external-root> -KeepWorkDirectory -OutputDirectory artifacts/UI03-distribution` completed, exit 0. The first failed build produced no package. Extracted self-contained launch, path containing spaces, child PATH without dotnet, observed Desktop window, bootstrap, migration idempotence, restart identity, Paper safety, package immutability and tamper/missing/path rejection all passed. Product, icon, version and manifest evidence were checked in staging and/or the final ZIP.

- ZIP: `artifacts/UI03-distribution/ArbitrageTrading-win-x64-g5156c542-local-dirty-unsigned.zip`
- SHA-256: `6161E4E603ECF631BB067B402C600491385AC5982A9F1F6C7FD4B072AC760B38` — independently matches sidecar.
- ZIP bytes: 120,581,860; payload bytes: 270,126,338; 791 package files.
- Manifest: schema 2, product ArbitrageTrading, PortableZip, win-x64, Paper, Unsigned, SourceDirty true; no manifest code/semantic changes.
- Published Desktop and Backend informational versions exactly match: `1.0.0+5156c5422dae813bf831a6c59a047c95dd6e9e22.dirty.5156c5422dae813bf831a6c59a047c95dd6e9e22`. This retains the existing SDK/publisher identity format, including its appended source revision.
- Instantiated ProductInformation directly from the published Desktop assembly without starting the app/backend: Name, assembly version 1.0.0.0, exact build identity, `5156c5422dae813bf831a6c59a047c95dd6e9e22 (dirty local build)` source display and `win-x64 · X64` runtime all matched package evidence. The normal Settings bindings were independently rendered/tested.
- ProductName = Arbitrage Trading; AssemblyDescription/Windows Comments = Arbitrage Trading Desktop; CompanyName blank. Existing technical apphost/assembly filename remains Arbitrage.Desktop.
- C: free space checked before heavy work: approximately 5.9 GiB. External WorkRoot was `C:\Users\Yovko\AppData\Local\Temp\ArbitrageTrading-UI03-distribution-9453114115964d909eec1327d20fc437`. Both finished ownership-marked d02 workspaces were removed using the existing ownership-checking helper, then their verified empty unique parent was removed. ScratchExists = false; C: free after cleanup approximately 5.89 GiB. No checkout scratch or real runtime cleanup.

AA. **Focused signing:** `scripts/test-distribution-signing.ps1` exited 0. Unsigned policy, structured untrusted TestEphemeral signatures, changed/post-sign hashes, wrong identity, missing timestamp, spoof/missing signature, native tamper rejection and successful/failed cleanup passed. Ephemeral certificate/private-key cleanup verified; no test certificate trusted. Microsoft SDK SignTool was unavailable or unverifiable; the independent test provider was used. No full signed-package build or production-signature claim.

AB. **Runtime isolation:** fixture visual tests use rejecting handlers or in-memory read-only HTTP responses and isolated WPF profile paths. No real runtime start/stop, sync, generation, monitoring, paper reset/execution/settlement, risk/profile edits, arming, campaigns, credentials or workspace rename. Canonical distribution smoke uses private disposable runtime directories; user's real backend directory is untouched.

AC. **Scope:** production changes are Desktop presentation, assembly metadata and existing read-notice feedback only. No Backend, Domain, Application, Infrastructure, Contracts, execution/lifecycle ownership behavior or publishing manifest semantics changed.

AD. **Migrations:** no migration or database schema files changed.

AE. **Git:** changes remain uncommitted; no commit, push, merge or pull request.

Changed-file inventory:

```text
docs/Development/PhaseUI03Verification.md
src/Arbitrage.Desktop/ViewModels/ProductInformation.cs
src/Arbitrage.Desktop/ViewModels/ReadNotice.cs
tests/Arbitrage.Desktop.Tests/Ui03WpfTests.cs
src/Arbitrage.Desktop/Arbitrage.Desktop.csproj
src/Arbitrage.Desktop/Controls/StatusToneConverter.cs
src/Arbitrage.Desktop/Resources/Styles/Controls.xaml
src/Arbitrage.Desktop/Resources/Styles/MarketWorkflow.xaml
src/Arbitrage.Desktop/Resources/Styles/Typography.xaml
src/Arbitrage.Desktop/ViewModels/MonitoringViewModel.cs
src/Arbitrage.Desktop/ViewModels/OpportunitiesViewModel.cs
src/Arbitrage.Desktop/ViewModels/PaperReliabilityViewModel.cs
src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Automation.cs
src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.Risk.cs
src/Arbitrage.Desktop/ViewModels/PaperTradingViewModel.cs
src/Arbitrage.Desktop/ViewModels/RelationshipsViewModel.cs
src/Arbitrage.Desktop/ViewModels/ShellViewModel.cs
src/Arbitrage.Desktop/Views/MainWindow.xaml
src/Arbitrage.Desktop/Views/MarketExplorerView.xaml
src/Arbitrage.Desktop/Views/OpportunitiesView.xaml
src/Arbitrage.Desktop/Views/PaperAnalyticsView.xaml
src/Arbitrage.Desktop/Views/PaperAutomationView.xaml
src/Arbitrage.Desktop/Views/PaperReliabilityView.xaml
src/Arbitrage.Desktop/Views/PaperRiskView.xaml
src/Arbitrage.Desktop/Views/PaperTradingView.xaml
src/Arbitrage.Desktop/Views/RelationshipsView.xaml
src/Arbitrage.Desktop/Views/SettingsView.xaml
tests/Arbitrage.Backend.IntegrationTests/Arbitrage.Backend.IntegrationTests.csproj
tests/Arbitrage.Desktop.Tests/SemanticPresentationTests.cs
tests/Arbitrage.Desktop.Tests/Ui01WpfTests.cs
tests/Arbitrage.Desktop.Tests/Ui02BPortfolioWpfTests.cs
tests/Arbitrage.Desktop.Tests/Ui02BReliabilityWpfTests.cs
tests/Arbitrage.Desktop.Tests/Ui02BSettingsWpfTests.cs
tests/Arbitrage.Desktop.Tests/Ui02WpfTests.cs
```
