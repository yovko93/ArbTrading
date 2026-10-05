# UI-03.1 verification — branding, scrollbars, sidebar density

A. Starting checkout: `C:\Users\Yovko\source\repos\ArbTrading`, ordinary checkout on `main`, clean at `b2540fbbf399a1887f54759c08bb405a1f46b6d5` (Polish desktop UI, notices, and product information). Origin inspected with credentials suppressed; no Git configuration/history changes.

B. Exact-head baseline CI: **Build and portable distribution** [run 37313553290](https://github.com/yovko93/ArbTrading/actions/runs/37313553290) completed **success** before edits.

C. Changed files: `scripts/generate-app-icon.ps1`; Desktop `Assets/ArbitrageTrading.ico`, `Assets/README.md`, `Resources/Branding.xaml`, `Resources/Styles/Controls.xaml`, `Resources/Themes/Brushes.xaml`, `Resources/Themes/Colors.Dark.xaml`, `Resources/Themes/Colors.Light.xaml`, `Services/WindowsThemeAdapters.cs`, `Views/MainWindow.xaml`; `tests/Arbitrage.Desktop.Tests/Ui031WpfTests.cs`; this report (12 files).

D–F. Removed both opposing horizontal-arrow paths and the tile from the in-app symbol. Canonical `BrandMarkParts` contains the blue rising A/peak, complementary green right leg, and three aligned ascending green bars. Sidebar `AppMark` and navy rounded-square `AppIconArtwork` reference the same drawing collection. Sidebar lockup uses two uppercase, nonwrapping, semibold 13-DIP lines, a 38-DIP mark and 10-DIP gap. Local workstation remains below; no extra logo card.

G–H. The existing STA WPF generator now renders `AppIconArtwork`. Regeneration succeeded with nine 32-bit PNG ICO frames: 16/20/24/32/40/48/64/128/256. Independently inspected native-size 16/32/48/128/256 output: blue A, green right leg and rising bars remain distinguishable; no tiny text, white square, or opaque outer corners. Large frames retain a navy gradient tile. ApplicationIcon and MainWindow resource URI remain unchanged and resolve.

I–J. Shared implicit ScrollBar template uses PART_Track, its orientation/range/value/viewport bindings, transparent PageUp/Down or PageLeft/Right repeat-button regions and rounded thumbs. Track remains functional; focused tests exercise page commands and drag deltas. Width/height is 10 DIPs, with inherited native minimum sizes overridden; thumb inset gives an 8-DIP visible width. Dark track/slate thumb blend with the navy UI; Light uses a light neutral track and visible cool-gray thumb. Hover/drag colors come from semantic resources. Windows High Contrast receives OS scrollbar colors through the existing palette adapter. No page-local templates or layout changes.

K. Sidebar density before → after: item MinHeight 40→34; Padding 14,10→12,6; Margin 0,3→0,1; inner row minimum 24→20; icon gap 12→10. Group-caption margins 14,14,0,8→12,8,0,4 (first caption 14,2,0,8→12,2,0,4). Outer top margin 16→12; branding margins 8,4,8,16→0,2,0,10; Local workstation top gap 14→8. All group captions and destinations retained; bottom LOCAL MODE card unchanged.

L–M. Visually inspected both themes at normal 1240×790 and minimum 830×590. Normal sidebar fits all eleven destinations; computed vertical scrollbar is collapsed. Minimum sidebar overflows, shows the compact themed scrollbar, and scrolls through Settings; LOCAL MODE remains anchored. Long Settings content also scrolls to its bottom. Dashboard card layout, header lifecycle controls and page typography remain unchanged. Ten WPF renders: `artifacts/UI031-renders`; icon contact: `artifacts/UI031-icon-frames.png`.

N. Final focused tests: **6 passed, 0 failed/skipped** (five UI-03.1 cases plus the existing icon/metadata case). Initial checks exposed native 17.2-DIP scrollbar minimum; overriding that inherited value fixed it. The first full suite then caught alpha 4 at the 16-pixel tile corner; increasing tile corner radius from 48 to 52 restored alpha 0 in every frame without changing the symbol or weakening the existing test. A rebuild attempted before that test process exited hit a transient file lock; rebuilding afterward succeeded. Tests cover canonical geometry, ICO frames/transparency, lockup, compact styles, all destinations/captions, normal/minimum overflow, track page commands/drag behavior and both palettes. Fixtures use isolated WPF profiles and rejecting HTTP handlers; zero backend/network requests.

O. Final full suite: **970 passed, 0 failed/skipped** — Domain 55, Application 277, Backend integration 520, Desktop 118. Canonical publish invoked the unchanged `scripts/verify.ps1`: solution restore, Release build with `--no-restore`, then unfiltered solution tests with `--no-build --no-restore`. The earlier full run had the single resolved corner-transparency failure (969 passed, one failed) and correctly stopped before publishing. No tests were weakened or skipped.

P. Final Release build succeeded with **0 warnings and 0 errors**. Desktop and Backend self-contained publish succeeded. `git diff --check` passed; eleven changed source/test file hashes remained unchanged through final verification and packaging (documentation excluded from that guard).

Q. `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` exited 0: no changes since the last migration.

R. `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` exited 0; all thirteen projects reported no vulnerable packages against the current sources.

S. One completed unsigned portable distribution: `scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot <external-root> -KeepWorkDirectory -OutputDirectory artifacts/UI031-distribution`, exit 0. Extracted ZIP smoke passed with spaces in its path and no dotnet in child PATH; Desktop window observed. Bootstrap, migration idempotence, restart identity, Paper safety, package immutability and tamper/missing/path rejection passed. All nine native icon frame payloads in the Release EXE, staged published EXE and final ZIP's EXE exactly match the corrected source ICO. No package or signing policy changes.

- ZIP: `artifacts/UI031-distribution/ArbitrageTrading-win-x64-gb2540fbb-local-dirty-unsigned.zip`
- SHA-256: `BDA4990A0DF06FCF66524B321E20AD0087B45E476904CBC66CBED3CDB312CC48` — independently matches sidecar.
- ZIP bytes: 120,641,848; payload bytes: 270,210,818; 791 files.
- Manifest: source `b2540fbbf399a1887f54759c08bb405a1f46b6d5`, SourceDirty true, win-x64, PortableZip, Paper, UnsignedSnapshot.
- External root: `C:\Users\Yovko\AppData\Local\Temp\ArbitrageTrading-UI031-distribution-423a57ad78e04724b662ef786d9af15e`. After publish exited, the existing ownership-checking helper removed both completed d02 workspaces; their verified empty parent was removed nonrecursively. ScratchExists = false. Capacity checked before publishing; about 4.15 GiB remained after cleanup.
- Smoke and fixtures used private disposable runtime/data paths. The user's real backend data and running-session ownership were untouched.

Local evidence: `artifacts/UI031-final-focused.log`, `UI031-full-distribution.log`, `UI031-initial-verification.log`, `UI031-ef.log`, `UI031-nuget.log`, `UI031-package-metadata.json`, `UI031-source-hashes.json`; final visual renders and icon contact listed above.

T–V. Changes are limited to Desktop visual resources, icon generation, focused tests and documentation. No backend, business, trading, lifecycle, authentication, credential, database or schema behavior changed. No migration added. Changes remain uncommitted; no push.
