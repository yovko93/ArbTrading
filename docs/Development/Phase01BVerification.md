# Phase 01B verification record

Implementation date: 2026-09-22. Existing checkout: `C:\Users\Yovko\source\repos\ArbTrading`, branch `main`, initially clean. The Phase 01A record remains in [Verification.md](Verification.md). No branch, remote, or Git history was changed; no commit, push, or pull request was created.

## Delivered

The Windows desktop now has a sidebar with ten stable destinations, header and status area, actual Dashboard/Settings/Diagnostics state, a Trading capability page, and reusable unavailable pages for deferred modules. The backend remains an independent authenticated process. Manual, Automatic, live order submission, exchange integration, and paper fills remain unavailable. No operational market data is invented.

Dark, Light, and System are the only saved appearance choices. A separate versioned desktop preference file holds the requested choice outside the repository and outside backend storage. System reads the Windows applications theme and falls back to Light if detection fails; Windows High Contrast temporarily overrides the palette. The desktop shows save failures without claiming persistence. A single long-lived backend snapshot preserves unsaved workspace input and labels retained data stale after a failed Refresh. Desktop diagnostics contain only bounded, sanitized events from the current run; backend log streaming remains Phase 01C work.

## Automated verification

- `dotnet restore ArbitrageTrading.sln -p:NuGetAudit=false`: passed. A first restore without this flag hit `NU1900` because this environment could not contact NuGet's vulnerability service. Cached package restore succeeded with audit disabled for this run; no package versions or repository-wide audit settings changed.
- `dotnet build ArbitrageTrading.sln -c Release --no-restore`: passed, **0 warnings, 0 errors**.
- `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore`: passed with Windows ACL permission for the existing backend lease tests. **80 passed, 0 failed, 0 skipped**: Domain 8, Application 5, Backend.IntegrationTests 64, Desktop.Tests 3.
- `git diff --check`: passed.

The new portable tests cover theme selection and System fallback/override, preference round-trip and malformed files, save failure, rapid changes, selector synchronization, ten destinations and retained workspace edits, stale data after failed Refresh, and bounded diagnostics. Windows STA/Dispatcher tests load palette dictionaries and navigation views, exercise representative WPF controls, and prove an existing visual changes color when switching themes repeatedly without accumulating dictionaries.

The first full sandboxed test run had one failure in an unchanged backend lease test: Windows denied the test process an ACL update on its unique temporary directory. The full run passed with the permissions required by that test. The temporary diagnostic change used to find the cause was reverted; no backend behavior was changed.

## Desktop launch and visual limit

The built WPF desktop executable was launched once for a disconnected-startup smoke check with unique temporary `Local__DataDirectory`, `Local__RuntimeDirectory`, `ARBITRAGE_RUNTIME_DIRECTORY`, and `ARBITRAGE_DESKTOP_DIRECTORY` paths and an unused loopback port. The process remained alive after startup, wrote one log file under that isolated desktop root, and recorded no startup-failure marker. Only that process was stopped and only its validated temporary root was removed. This checks startup liveness, **not** appearance or user interaction.

Native screenshot/mouse inspection could not be completed in this environment; the Windows screenshot helper previously failed with `SetIsBorderRequired: No such interface supported (0x80004002)`. The STA tests verify resource application and view loading, but no screenshot-based visual review, DPI measurement, or mouse-driven end-to-end workflow is claimed.

Manual visual check on an isolated profile:

1. Start Desktop with the isolated directories before Backend; confirm the disconnected state. Start Backend with the same paths and an unused loopback port, then Refresh to confirm recovery.
2. Visit all ten destinations, especially Dashboard, Settings, and Logs & Diagnostics; check disabled/unavailable explanations and no fabricated trading data.
3. Edit a workspace name without saving, navigate away, switch Dark/Light/System, then return and confirm the edit and selected page are preserved. Save, Refresh, and confirm the backend result.
4. Resize the window near its minimum, use keyboard Tab/arrow navigation, and inspect at common Windows display scaling settings. Check focus visibility, text contrast, scrolling, popups, and control states in Dark and Light.
5. Close and reopen Desktop to verify the saved theme. Windows application-theme and High Contrast reactions can be checked manually without the agent changing machine appearance settings.

Phase 01C still needs authorized workspace-scoped SignalR, backend log streaming, and comprehensive reconnect behavior. Exchange connectivity, market ingestion, strategies, orders, and paper simulation remain later work.
