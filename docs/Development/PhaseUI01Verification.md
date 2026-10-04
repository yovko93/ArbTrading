# Phase UI-01 — Desktop visual system and Trading redesign

## A. Starting repository state and design reference

Repository root: `C:\Users\Yovko\source\repos\ArbTrading`. Ordinary checkout on `main`, starting HEAD `c5ae01e48b64894d47e3edbaa7843557e9d90053`, initially clean. The repository root, branch, working tree, applicable AGENTS.md, existing project files, and existing origin remote were inspected before edits; remote credentials were not printed. No Git configuration, history, or branch was changed.

The supplied attachment contained the UI-01 specification but no visual mockup. The user explicitly approved proceeding from the written dark-first, blue/green design direction. That written direction is the reference for this implementation.

## B. Changed files

Paths in the following groups are relative to the repository root:

- Desktop integration: `src/Arbitrage.Desktop/App.xaml`, `src/Arbitrage.Desktop/Arbitrage.Desktop.csproj`.
- Desktop controls: `src/Arbitrage.Desktop/Controls/StatusBadge.xaml`, `src/Arbitrage.Desktop/Controls/StatusToneConverter.cs`.
- Shared styles: `src/Arbitrage.Desktop/Resources/Styles/Controls.xaml`, `Layout.xaml`, `Typography.xaml`.
- Theme resources: `src/Arbitrage.Desktop/Resources/Themes/Brushes.xaml`, `Colors.Dark.xaml`, `Colors.Light.xaml`; `src/Arbitrage.Desktop/Services/WindowsThemeAdapters.cs` adds the navigation background to the existing High Contrast override.
- Views: `src/Arbitrage.Desktop/Views/MainWindow.xaml`, `DashboardView.xaml`, `PaperTradingView.xaml`, `PaperRiskView.xaml`, `PaperAutomationView.xaml`.
- New branding: `src/Arbitrage.Desktop/Resources/Branding.xaml`, `src/Arbitrage.Desktop/Assets/ArbitrageTrading.ico`, `src/Arbitrage.Desktop/Assets/README.md`, `scripts/generate-app-icon.ps1`.
- Tests: new `tests/Arbitrage.Desktop.Tests/Ui01WpfTests.cs`; `tests/Arbitrage.Desktop.Tests/WpfFixture.cs` loads the branding dictionary.
- This report: `docs/Development/PhaseUI01Verification.md`.

Captures, smoke helpers, local concurrency settings, logs, and TRX evidence remain under already-ignored artifacts/TestResults paths. No package dependency or tracked verification script changed.

## C. Shared visual system

Centralized Light/Dark palettes provide coordinated application, navigation, surface, elevated panel, border, selection, focus, and semantic colors. Reusable rounded cards, inset panels, summary cards, section headings, eyebrow labels, metric text, status chips, banners, and detail expanders establish hierarchy without gradients or expensive shadow effects. Buttons retain semantic colors during interaction, with an independent keyboard-focus outline that avoids layout movement. Disabled controls remain muted.

Success/healthy/connected/running states use green; Local/Paper/information use blue; stale/partial/in-progress states use amber; unavailable/unrecognized states use neutral; faults/denied/corrupt/kill-switch states use red. Armed and over-limit summary states are classified by the existing presentation converter. No state transitions or execution gates changed.

## D. Navigation and header

The sidebar includes the blue/green mark, app name, distinct navigation surface, grouped Workspace/Paper Operations/System captions, and a selected row accent. All 11 original destinations and the two-way selection binding remain intact. Narrow windows retain scrolling access to all destinations.

The header retains the workspace, theme picker, lifecycle/capability chips, exact lifecycle explanation, and grouped Start/Stop/Refresh controls. Enabled Start is green, Stop red, Refresh blue. Original commands, enabled bindings, tooltips, capability tones, automation names, and confirmation paths are preserved.

## E. Trading redesign

The page leads with the simulation notice, paper capital/generation information, and separate Kalshi/USD and Polymarket/USDC balance cards. Missing balances remain Unavailable; currencies are neither combined nor converted.

Compact Risk Policy and Auto Paper cards follow. They display saved backend snapshots: reserve percentage (including fractional precision), maximum open executions, saved sizing mode, session cap, policy/profile revisions, automation state, and an explicit clear/latched/unknown kill-switch chip. Unsaved form edits cannot appear as saved policy/profile values. Long revisions remain available through tooltips when visually shortened.

Initialize/reset controls, full risk editing, all Auto Paper session/profile/sizing controls, safety explanations, and explicit preview/confirmation remain below. The new detail expanders start open and permit collapsing sections without changing the contained commands or availability rules. Long forms and action groups continue wrapping at narrow widths.

## F. Dashboard

Dashboard uses the same visual vocabulary: a coherent snapshot banner, cleaner backend/workspace/trading cards, and exchange rows that can grow with their content. Existing status, capability, timestamp, identity, and guidance bindings remain unchanged.

## G. App icon and startup

The maintainable source is `Resources/Branding.xaml`, a text-free blue/green opposing-arrow DrawingImage on a navy rounded square. `Assets/ArbitrageTrading.ico` contains nine 32-bit PNG frames: 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixels. The Desktop project embeds it as a WPF resource and supplies it as ApplicationIcon for the executable. MainWindow uses the embedded icon; the sidebar uses the vector mark. No extra packaging step is required.

`scripts/generate-app-icon.ps1` regenerates the ICO using Windows STA WPF rendering; the asset README documents the command. Regeneration was deterministic in this environment: SHA-256 `9F94567876F14838AE4FB9945C04470F69153662FD8A5E052D9DDF6C9E60E30C` (12,008 bytes). The multi-size preview was inspected, and the WPF test decodes all nine frames and verifies window/sidebar resources.

The actual Release executable started successfully using a fresh ignored profile and an unused loopback port. Its WPF shell was observed, and native small (40×40) and large (64×64) icons loaded at the current display DPI. No child backend, database, or connection file was created. Both test-owned processes exited; no user runtime was touched.

Closing the shell with WM_CLOSE exposed an existing shutdown defect: App.OnExit explicitly disposes MainViewModel and subsequently disposes its DI provider, invoking the non-idempotent MainViewModel.Dispose again. The resulting ObjectDisposedException exits with code -532462766. Both implicated source files are unchanged from the starting HEAD. Startup and icon checks passed; graceful shutdown did not. This lifecycle fix is outside the requested UI-only scope and remains unresolved. The sandboxed smoke initially could not query process children; a permitted retry completed those checks and recorded the shutdown failure.

Local evidence: `artifacts/UI01-startup-4f501c3c6c7247d98a3dc89ff43031a2/result.json` and `stderr.log`, plus `artifacts/UI01-icon-preview.png`.

## H–I. Theme and layout inspection

**H. Dark:** manually inspected generated WPF captures for populated Trading, normal/minimum-width shell, Dashboard, and semantic lifecycle states. Separate balances, saved summaries, controls, preview, selected navigation, and focus/disabled styling remain readable.

**I. Light:** inspected populated narrow Trading, normal shell, Dashboard, and Faulted lifecycle rendering. Cards, text, input fields, chips, and red fault banner remain coherent. Captures cover 1020/560-pixel Trading widths and 1240×790/830×590 shell sizes; tall full-page captures expose the lower editors and preview.

System continues resolving the existing Windows Light/Dark preference. Existing theme tests cover resource switching, identical palette keys, High Contrast, disabled semantics, and at least 4.5:1 semantic text contrast. Full verification includes the existing System-following, explicit-theme, and preference-persistence tests. The user's Windows theme/preferences were not changed.

## J. Focused verification

Desktop Release build passed with 0 warnings and 0 errors. Initial focused WPF/status run: **63 passed, 0 failed, 0 skipped**. It covers rendering/composition, saved summary versus unsaved draft state, unavailable summaries, kill-switch/fault colors, all 11 navigation destinations, icon resources, command bindings, and existing real Start/Refresh/Stop lifecycle tests for development, portable fixture, and delayed readiness. The lifecycle tests use isolated owned backends.

After retaining fractional reserve percentages, the five UI-01 cases were rerun: **5 passed, 0 failed, 0 skipped**, including a saved 35.25% reserve while the draft still contains 99. No additional unique cases were added by this rerun.

Evidence: `artifacts/UI01-ui/`, `tests/Arbitrage.Desktop.Tests/TestResults/UI01-focused.trx`, and `UI01-final-summary.trx` in the same TestResults directory.

## K–L. Full tests and builds

`scripts/verify.ps1 -Configuration Release` completed successfully: **929 passed, 0 failed, 0 skipped**.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Domain | 55 | 0 | 0 |
| Application | 277 | 0 | 0 |
| Backend integration | 520 | 0 | 0 |
| Desktop | 77 | 0 | 0 |
| Total | 929 | 0 | 0 |

Final TRX files in each respective test project's TestResults directory:

- Domain: `verification_net10.0_20261004220942.trx`.
- Application: `verification_net10.0_20261004220953.trx`.
- Backend integration: `verification_net10.0_20261004221605.trx`.
- Desktop: `verification_net10.0_20261004222102.trx`.

The complete command log is `artifacts/UI01-verification.log`; full-suite visual captures are in `artifacts/UI01-full-ui/`. The green automated suite does not cover the actual App.OnExit double-disposal identified by the separate startup smoke in section G.

Focused Desktop and full solution Release builds both succeeded with **0 warnings and 0 errors**. `git diff --check` passes.

The canonical `scripts/verify.ps1 -Configuration Release` performs solution restore, Release build with `--no-restore`, and unfiltered tests with `--no-build --no-restore`. It is run once for this phase through an ignored local wrapper limiting test project concurrency to one and xUnit workers to two. This does not filter cases or change tracked scripts. The existing private-storage/ACL integration tests require execution outside the restricted sandbox.

## M. EF

`dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` passed: no changes to the model since the last migration.

## N. NuGet

`dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` passed: no vulnerable packages reported across all 13 projects using the configured sources. The audit ran with network permission.

## O–Q. Scope and final state

**O.** Production changes are presentation/assets only. No business view models, backend, API, contracts, settings format, database, paper capital/risk/automation rules, monitoring, reliability, lifecycle ownership, command availability, or confirmations changed. Desktop continues using its existing Contracts/HTTP boundary. The only production C# edits classify two display tones and add a High Contrast navigation color. A separate read-only review found no actionable binding or availability regressions.

**P.** No migration was added or changed.

**Q.** All **23** changed/new source, asset, test, and report files remain uncommitted on `main` at the starting HEAD. No commit, push, merge, pull request, branch switch, or history rewrite was performed.

Suggested commit message: `Redesign desktop visuals and Trading summaries`
