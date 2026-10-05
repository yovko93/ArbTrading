# UI-03.2 verification — Trading dashboard composition

A. Checkout: `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on `main`. Started clean at `8fe2cc0b258e1f93671dd23753237d6a5a75877e` (Fix branding, scrollbars, and sidebar spacing). Origin inspected with credentials suppressed. No branch, remote or Git metadata changes.

B. Exact-head baseline CI: **Build and portable distribution**, [run 37322305428](https://github.com/yovko93/ArbTrading/actions/runs/37322305428), completed **success** before implementation.

C. Seven changed files: Desktop `Views/PaperTradingView.xaml` and `.xaml.cs`; tests `Ui032WpfTests.cs`, `PaperWpfTests.cs`, `PaperPageSplitWpfTests.cs`, `Ui03WpfTests.cs`; this report. The three existing tests now expand the Trading editors before their unchanged detailed-control assertions.

D. Previous composition: duplicate large Paper Trading title, full-width Paper capital card, large active-generation inset and venue balances, then risk/automation summaries and exposed forms. This pushed important saved-state summaries below the first viewport.

E. New composition: compact visible simulation line, then Paper Account / Risk Policy / Auto Paper in a 35/32/33 star Grid. Trading alone has a 1400-DIP content maximum. Actual content widths of at least 900 DIPs use three columns; 660–899 use two plus a full-width Auto Paper card; smaller widths stack all three. The secondary row uses two columns from 660 DIPs. Cards share 12-DIP padding/gaps, title treatment and border resources; risk/profile rows have a 32-DIP minimum rhythm. The duplicate title and old capital hierarchy are removed.

F. Paper Account shows actual generation integrity, current ID with full tooltip, account state and two separate venue/currency mini-cards. Kalshi/USD has a blue accent; Polymarket/USDC has green. AvailableCash, ReservedCash, TotalCash and InitialCash bind directly to current balance DTOs. These are labeled cash, not positions or equity. No currency conversion, aggregation or new accounting calculation. Missing data shows Unavailable / No active generation.

G. Risk Policy binds only to `RiskStatus.Policy`: revision and seven saved limits (cash reserve, single execution debit, total open cost, market exposure, instrument exposure, open positions, open executions). Percent formatting preserves decimal precision. Unsaved form edits do not affect this summary.

H. Auto Paper shows current state, saved profile revision, sizing mode, fixed quantity or adaptive minimum/maximum/step, fee-adjusted edge/profit thresholds, session/hour caps, realtime requirement, BestEffort permission and kill-switch state. Disarmed uses the existing neutral tone. All values come from saved profile/status, not editable inputs.

I. Current Paper Opportunity uses the existing OpportunityKey and PreviewText. Its long key is trimmed with a full tooltip; the compact preview has a full tooltip and points to the complete existing entry section. No selection shows an explicit empty state and directs the user to Opportunities. The summary is hidden when account generation data is unavailable, even if a selection key remains in existing VM state. It does not evaluate opportunities, activate another VM or retain additional private data.

J. Paper Equity deliberately says **Historical equity series is not available**. It directs users to the existing separate balances and Portfolio valuations. There is no line chart, history, axes, combined equity or additional data acquisition.

K. Intentionally omitted illustrative showcase features: fake $10,000 balances, market names, recent-opportunity rows, profits, equity history/chart points, recent logs, new search, notifications/avatar/account controls and inert View All/Profile/Policy buttons. No new activity feed was added; the existing functional expander headers expose details.

L. Four collapsed detailed sections follow the dashboard: Paper Generation; Risk Policy Configuration; Auto Paper Configuration & Session Controls; Explicit Paper Entry. Existing inputs, binding paths, commands, preview grid and confirmation flows remain intact. Trading collapses its own embedded editor instances; standalone editor defaults are unchanged.

M. Armed sessions reveal Disarm and EMERGENCY STOP above the summary cards and outside all collapsed editors. Both bind to the original commands. Tests verify both visible and enabled while Busy and fully within the minimum window's content viewport. No CanExecute, confirmation, kill-switch or execution semantics changed.

N. Dark connected fixture: three aligned cards show Healthy account, saved WithinLimits risk, Disarmed automation, distinct 920 USD / 1900 USDC available cash. Both secondary cards are visible in the wide first viewport. Compared directly with the authoritative showcase: the same three-card hierarchy, blue/green venue insets and compact saved key/value rows replace the old long-form hierarchy. Shared application chrome is preserved; no fictional showcase functions are copied.

O. Dark unavailable fixture: account/risk/profile values become Unavailable while all three cards and secondary panels remain structurally present. Prior workspace snapshot is cleared in the test shell before disconnection. A running fixture backend presentation can coexist with disconnected/unavailable workspace data; it is not a claim about a real backend.

P. Light connected and unavailable renders inspected: matching hierarchy and spacing, legible labels/statuses and distinct venue accents. All fixtures are test-only, use rejecting HTTP handlers and make zero backend requests. Visible generation/policy/profile IDs are deterministic. Ten final renders are under `artifacts/UI032-renders/UI032-*.png`.

Q. At requested 830×590 window size, cards stack and vertical scrolling remains available. Both themes tested, including expanded editor buttons and the explicit-entry footer. All action buttons fit horizontally; no horizontal page overflow. Wide fixture windows request 1680×1000 and are constrained by the host work area (rendered client content 1537×843); minimum client content is 817×553. Intermediate 1100-wide windows explicitly verify the two-plus-one layout. These are WPF fixture renders, not real account screenshots.

R. Focused suite: **18 passed, 0 failed/skipped**. Includes the two new Dark/Light Trading cases plus existing UI-01/UI-03, page split, paper state, risk, automation and sizing WPF tests. New coverage checks saved-vs-edit isolation, direct binding, currencies, all unavailable cards, no fake chart, original command resolution, collapsed defaults, adaptive saved rows, selection invalidation, three/two/one-column behavior, safety controls and zero binding errors. An initial run found three old tests assuming expanded editors; they now open the same controls before asserting them. Initial new-test compilation also caught an xUnit collection-count analyzer diagnostic, corrected to Assert.Single. After the full suite identified a missing uninitialized-account explanation, the compact card restored its existing AccountText binding when generation is absent. The original semantic test was preserved; final targeted UI-03.2 plus semantic checks passed **4/4**, regenerating the final renders.

S. Final full solution verification: **972 passed, 0 failed/skipped** — Domain 55, Application 277, Backend integration 520, Desktop 120. The canonical distribution run invokes unchanged `scripts/verify.ps1`: solution restore, Release build with `--no-restore`, and unfiltered tests with `--no-build --no-restore`. The first full run finished with 971 passed and one failed (the restored uninitialized-account message); its gate stopped before any package publish. The final run used the corrected source. No tests were skipped or weakened.

T. Full Release solution build succeeded with **0 warnings and 0 errors**. `git diff --check` passed. The six changed source/test files have a SHA-256 guard against changes during verification. Original direct command bindings and every explicit-preview DataGrid column were independently compared with HEAD and preserved.

U. `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` exited 0: **no changes since the last migration**. No schema files were changed.

V. `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` exited 0. **All thirteen projects reported no vulnerable packages** against the current sources.

W. **One completed unsigned portable build and smoke passed**, via `scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot <external-root> -KeepWorkDirectory -OutputDirectory artifacts/UI032-distribution`, exit 0. Extracted ZIP launched from a path with spaces and no dotnet in the child PATH; Desktop window observed. Bootstrap, migration idempotence, restart identity, Paper safety, package immutability and tamper/missing/path rejection passed. UI-03.1 branding/icon sources are unchanged; all nine native icon frames in the Release EXE, staged published EXE and final ZIP EXE match the original ICO payloads exactly. The final six source/test hashes remained unchanged throughout verification and packaging.

- ZIP: `artifacts/UI032-distribution/ArbitrageTrading-win-x64-g8fe2cc0b-local-dirty-unsigned.zip`
- SHA-256: `C1EA131158E22E13FF91E471649A7B383B5D9EEA17AF6E999DF913BBF47AE6E7`, independently matches the sidecar.
- ZIP bytes: 120,643,763; payload bytes: 270,220,034; 791 package files (790 inventory entries plus the manifest).
- Manifest: source `8fe2cc0b258e1f93671dd23753237d6a5a75877e`, SourceDirty true, win-x64, PortableZip, Paper, UnsignedSnapshot.
- External scratch: `C:\Users\Yovko\AppData\Local\Temp\ArbitrageTrading-UI032-distribution-65ffe27fd4ba4b4687a65ab62ae734e8`. After the publishing process exited, the existing ownership-checking cleanup helper removed the two completed workspaces; their verified empty parent was removed nonrecursively. ScratchExists = false; about 3.72 GiB free afterward.
- Smoke and WPF fixtures used isolated disposable runtime/data paths. No real account, credential, backend data or user session was modified.

Local evidence: `artifacts/UI032-focused.log`, `UI032-final-focused.log`, `UI032-initial-verification.log`, `UI032-full-distribution.log`, `UI032-ef.log`, `UI032-nuget.log`, `UI032-package-inspection.log`, `UI032-package-metadata.json`, `UI032-source-hashes.json`; final renders are listed above.

X–AB. No fake operational data is introduced. USD and USDC remain separate. Changes are Desktop presentation, tests and documentation only: no backend, business, financial, authentication, lifecycle, database or migration changes. No migration added. Changes remain uncommitted; no push.
