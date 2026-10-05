# UI-03.2.1 verification — venue marks and deterministic responsive tests

A. Checkout: `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on `main`. Started clean at `81812f42c5aa36ac61a4fb36dcd49c43d6a4011d` (Match Trading dashboard to showcase layout). Remote identity inspected with credentials suppressed. No Git metadata, branch or remote changes.

B. The specified exact-head [GitHub Actions run 37336634794](https://github.com/yovko93/ArbTrading/actions/runs/37336634794) completed failure: Windows failed its Build WPF and backend; run all tests step; linux-backend succeeded. The supplied failure reports both themes of `Ui032WpfTests.Trading_dashboard_uses_saved_data_and_preserves_editors_at_all_widths` expecting three columns but receiving two. Public job metadata was verified through GitHub REST; unauthenticated log download returned 403, so the exact assertion text comes from the supplied specification. This task does not commit/push or rerun remote CI; the previous run remains red.

C. The original test assumed a requested MainWindow width of 1680 guaranteed sufficient realized content width. Production correctly uses actual SummaryGrid width, after shell/sidebar/padding/scrollbar allocation. A temporary local diagnostic reproduced the discrepancy:

| Original harness | MainWindow.ActualWidth | PaperTradingView.ActualWidth | SummaryGrid.ActualWidth | Columns |
|---|---:|---:|---:|---:|
| Requested 1680, unconstrained locally | 1680 | 1399.2 | 1389.2 | 3 |
| Requested 1680, MaxWidth 1100 | 1100 | 819.2 | 809.2 | 2 |

The local machine allowed the wide window and passed; a constrained runner could legitimately leave the grid between 660 and 899 DIPs. The precise CI window dimensions were not logged by the previous test; its two-column result implies that breakpoint range. The temporary diagnostic source is preserved only as an ignored artifact, outside the test project.

D. The existing test now sizes PaperTradingView explicitly at 1260×760, 780×760 and 560×340 logical DIPs. A Canvas inside a small offscreen window maintains real visual materialization and IsVisible checks while arranging the page at its own explicit size. The host window's realized size is never used for responsive assertions. Measure/Arrange/UpdateLayout and the existing dispatcher idle boundary deliver SizeChanged to the unchanged production Grid; a second layout pass completes its row/column update. No timed sleeps, retry-until-pass or CI detection. Assertions verify the requested page size, actual grid width range, 3/2/1 columns and the two-plus-one span in both themes. Saved-state, currency, commands, armed safety, expanded-editor fit and unavailable-state assertions remain.

E. Production `PaperTradingView.xaml.cs` is unchanged: width >=900 → three columns; >=660 → two; otherwise one. No breakpoint was lowered or forced.

F. `KalshiMark` is a restrained local K vector on a rounded dark-green tile, with a bright-green glyph. It is explicitly documented as a local identity, not an exact official logo. Palette reference: [Kalshi's official brand kit](https://kalshi.com/brandkit). No official wordmark was cropped and no currency flag is used.

G. `PolymarketMark` converts the official `icon-blue.svg` linked from [Polymarket's brand page](https://polymarket.com/brand) via its [logo pack](https://polymarket-upload.s3.us-east-2.amazonaws.com/polymarket-logos.zip). The 512-square blue background, white geometric mark, path commands/coordinates, nonzero fill rule and colors are retained. A token comparison against the source SVG verified the geometry. The downloaded pack was removed; only the required WPF vector is part of application source. No generic coin or runtime image download.

H. Shared resources: new `src/Arbitrage.Desktop/Resources/ExchangeBranding.xaml`, merged by `App.xaml` and the shared WPF test fixture. Provenance: `src/Arbitrage.Desktop/Assets/ExchangeBranding.md`. Only the two Trading venue header rows changed: reusable Image, 28×28 DIPs, 8-DIP label gap, vertically centered, accessible names Kalshi / Polymarket. Auto/star header columns let labels wrap if space is tight. All Available/Reserved/Total/Initial values, currency converters and automation IDs are untouched. Other Trading panels, sidebar, scrollbars, application branding/icon and global color system are unchanged.

I. Dark connected-wide render inspected: the green K and official blue/white Polymarket mark are distinct and legible in their compact header rows. Existing card hierarchy, fields and venue labels remain intact.

J. Light connected-wide render inspected: both marks remain clear against the light inset panels, with no opaque white square behind either icon.

K. Dark and Light unavailable-wide renders inspected: both venue marks and textual currency labels remain visible when all balances say Unavailable. Wide page renders are exactly 1260×760 logical pixels, independent of screen size; ten renders under `artifacts/UI0321-renders/UI032-*.png` also cover narrow, armed and expanded-editor states. They show test-only data and are not real account screenshots. No fake funds or operational data.

L. Focused suite: **10 passed, 0 failed/skipped** — updated UI-03.2 tests, UI-03.1 visual tests, page split and semantic presentation. Checks include distinct reusable DrawingImages, correct image names/sizes/visibility, USD/USDC labels, unchanged balance binding paths/converter parameters, real production responsive layout and original commands. During harness development, a disconnected visual tree correctly failed IsVisible assertions; the small Canvas host preserves those assertions without depending on host-window size.

M. Exact responsive method repeated **three times in Release**, both themes each time: **6 passed, 0 failed/skipped**. Every run observed grid widths 1250 / 770 / 550 and columns 3 / 2 / 1. Earlier stabilized-harness checks also passed both themes. No theme/test was skipped or weakened.

N. Full solution verification: **972 passed, 0 failed/skipped** — Domain 55, Application 277, Backend integration 520, Desktop 120. The single unsigned distribution pipeline invoked unchanged `scripts/verify.ps1`: solution restore, Release build with `--no-restore`, and unfiltered solution tests with `--no-build --no-restore`. No full-suite retry was needed.

O. Full Release solution build passed with **0 warnings and 0 errors**. `git diff --check` passed. All six changed Desktop/test/provenance file hashes remained unchanged through verification and packaging; the editable verification report is excluded from that guard.

P. `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` exited 0: **no changes since the last migration**.

Q. `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` exited 0: **all thirteen projects reported no vulnerable packages** against the current sources.

R. **One unsigned portable publish and extracted-ZIP smoke passed**, exit 0. Canonical command: `scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot <external-root> -KeepWorkDirectory -OutputDirectory artifacts/UI0321-distribution`. The smoke launched with spaces in the extraction path and no dotnet in the child PATH; Desktop window observed. Bootstrap, migration idempotence, restart identity, Paper safety, package immutability and tamper/missing/path rejection passed. All nine UI-03.1 application-icon frame payloads in the Release EXE, staged EXE and final ZIP EXE match the unchanged source ICO exactly.

- ZIP: `artifacts/UI0321-distribution/ArbitrageTrading-win-x64-g81812f42-local-dirty-unsigned.zip`
- SHA-256: `C06211407207F3ED9FA09E0AF04DF3BD5F9F9F3D61B18668E6CCC83554FECAB8`, independently matches its sidecar.
- ZIP bytes: 120,644,443; payload bytes: 270,222,082; 791 package files (790 inventory entries plus manifest).
- Manifest: source `81812f42c5aa36ac61a4fb36dcd49c43d6a4011d`, SourceDirty true, win-x64, PortableZip, Paper, UnsignedSnapshot.
- External scratch: `C:\Users\Yovko\AppData\Local\Temp\ArbitrageTrading-UI0321-distribution-612581bcabb14db795ecc0e7cfc4a7d4`. After publish exited, the ownership-checking helper removed both completed workspaces; their verified empty parent was removed nonrecursively. ScratchExists = false. About 2.57 GiB free afterward.
- WPF fixtures and smoke used isolated disposable runtime/data directories. No real credentials, account data or user backend session were modified.

S. USD and USDC remain separate. Their existing venue/currency converter parameters, balance fields and automation IDs are unchanged.

T. No backend, business, financial, risk, automation, execution, monitoring, lifecycle, authentication, credential or storage changes.

U. No schema changes or migration.

V. Changes remain uncommitted and unpushed on `main` at the starting HEAD. No remote CI run was created.

Changed files (seven): Desktop `App.xaml`, `Views/PaperTradingView.xaml`, `Resources/ExchangeBranding.xaml`, `Assets/ExchangeBranding.md`; tests `Ui032WpfTests.cs`, `WpfFixture.cs`; this report.

Local evidence: `artifacts/UI0321-baseline-ci.json`, `UI0321-baseline-probe.log`, `UI0321-baseline-probe.cs`, `UI0321-harness-focused.log`, `UI0321-focused.log`, `UI0321-responsive-repeat-{1,2,3}.log`, `UI0321-mark-provenance.log`, `UI0321-full-distribution.log`, `UI0321-ef.log`, `UI0321-nuget.log`, `UI0321-package-inspection.log`, `UI0321-package-metadata.json`, `UI0321-source-hashes.json` and the renders above.
