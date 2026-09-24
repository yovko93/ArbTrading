# D01 distribution verification

Baseline: repository `C:\Users\Yovko\source\repos\ArbTrading`, branch `main`, HEAD `486367bb9b5a488f7918afe14bce56ae028324db`; initially clean, existing origin retained. The exact Phase 04H.4 GitHub Actions run [36017540630](https://github.com/yovko93/ArbTrading/actions/runs/36017540630) was observed completed/success before implementation. Changes remain uncommitted; no push, release, installer, auto-update, arm64 target or BurnIn-01 was performed.

Implementation and package contract: [architecture](../Architecture/Distribution.md). First run, private state, upgrade/rollback limitations and clean-VM checklist: [operator guide](../Operations/PortableWindowsDistribution.md).

Focused tests: 17 passed, 0 failed. They cover package inventory/hash corruption, missing backend/config, invalid path/root escape, alternate executable rejection, development mode, failed migration preventing normal launch with matching profile environment, new/current database idempotence, exact singleton ownership, no business bootstrap, SQLite WAL backup consistency, private ACLs, five-copy retention, deterministic backup failure, deterministic SQL migration failure, unknown-future-schema refusal, and existing adaptive execution/ledger preservation across a real earlier-schema upgrade. The upgrade fixture invokes DatabaseInitializer, the same path used by backend --migrate; normal startup refusal for pending migrations remains covered by PersistenceTests.

The initial focused run exposed uncaught InvalidDataException for four malformed paths. Validation now converts that exception into an invalid package result; the repeat passed. An initial full build was blocked by a pre-existing backend process holding output DLLs. The user explicitly authorized stopping PID 24660; subsequent Release build succeeded with zero warnings/errors. No unrelated process was stopped.

EF migration consistency check passed: no model changes since the last migration. No new or historical migrations were edited. Current-source NuGet audit completed successfully, with no vulnerable packages across all 13 projects. Windows protected ACL/process tests and NuGet access were run outside the filesystem sandbox with tool approval.

Full canonical publish and extracted smoke results are recorded below. Automated smoke uses a unique temporary directory outside the checkout with spaces, empty private storage, system32-only child PATH and nonexistent DOTNET_ROOT. Only extracted executables are launched. It validates the Desktop window/no automatic backend launch, migration twice, normal backend/authenticated session/restart identity, Paper-only capability values, no paper funds/policies/arming/campaign, immutable package inventory, and tampered/missing/path-invalid refusal. Test-owned child processes and the exact temporary test directory are cleaned up.

An actually clean physical machine or VM has **not** been tested. Hidden installed runtimes plus explicit self-contained runtime files and runtimeconfig validation demonstrate the automated boundary only. Snapshots are unsigned; SmartScreen/code-signing trust remains a documented limitation. Manifest hashes detect corruption but do not authenticate a publisher. No credentials or runtime user data are copied into the package; staging scan and immutable payload inventory are enforced by the canonical script. Packaging does not change Paper execution, admission, arming, ownership, authorization, kill-switch or campaign semantics.

Additional checks: the real managed process lifecycle test now runs for both development and manifest-backed package layouts. Both focused variants passed: migration creates storage before normal launch, two concurrent Desktop controllers converge on one managed process, authenticated WebSocket state synchronizes, Stop/restart and credential rotation remain intact. This fixture copies build outputs and exercises controller behavior; the separate extracted ZIP smoke proves self-contained deployment.

The stronger smoke test observes the actual hidden WPF shell HWND/class/title, excluding an error-dialog-only process, and passed with `Desktop window observed: True`. It also checks that Desktop alone creates neither backend connection nor database and that standalone packaged backend refuses package-local mutable storage before creating it. Clean-tree-default and local-SkipTests refusal checks passed. PowerShell scripts parse successfully.

## Requested delivery checklist

| Item | Result / implementation |
| --- | --- |
| A: repository | Existing root above, attached main branch; original remote/history retained; changes uncommitted. |
| B: baseline CI | Exact Phase 04H.4 commit run completed successfully; linked above. |
| C–E: architecture/layout/publish | Independent self-contained Desktop root and backend subdirectory; Release win-x64; net10.0-windows/net10.0; trimming, AOT and single-file disabled. |
| F: resolution | Explicit absolute override, packaged backend, legacy adjacent fallback; packaged override must match verified executable. |
| G–H: manifest/integrity | Schema 1, source SHA/dirty/UTC/RID/TFMs/paths/hashes/full inventory. Missing/corrupt/unsafe package blocks managed Start. No capability authority. |
| I–J: storage/config | Per-user LocalAppData backend/runtime/desktop; private overrides outside package. Config contains Local/Paper/loopback only, existing runtime gates remain authoritative. |
| K–L: first run/migration | Desktop launch observes only. Explicit Start validates, runs --migrate with the same profile/environment, waits for exit 0, then starts normally. |
| M–N: backup | SQLite BackupDatabase under backend lease, WAL included, integrity/private ACL verified, UTC/GUID names, latest five completed copies in backend/backups/schema. New/current databases create no backup. |
| O–P: failures/newer schema | Failure prevents normal launch and preserves DB/backups; unknown migration IDs reject with no downgrade/reset. |
| Q–R: compatibility/diagnostics | Development/manual pending schemas still require explicit --migrate. Settings displays mode, manifest, RID, source/build identity, storage and migration result separately from connectivity/capabilities. |
| S–T: publisher/source | Canonical publish.ps1 verifies by default; clean source required unless AllowDirty, which labels manifest and ZIP. SkipTests restricted to exact verified clean CI SHA. |
| U: scan | Runtime DB/sidecar/log/credential/key/config-pattern scan, no reparse points, complete hashed file inventory, safe config assertion, runtimeconfig/runtime presence checks. |
| V–W: smoke | Extracted copy outside checkout, spaces in path, empty isolated storage, child PATH lacks dotnet and DOTNET_ROOT is nonexistent; WPF shell, idempotent migration, authenticated Paper startup and restart verified. |
| X: upgrade | Earlier real migration fixture preserves adaptive executions/ledger and financial history; consistent pre-upgrade copy exists and retains earlier schema. Failure/WAL/retention/future-schema tests also pass. |
| Y–Z: immutability/rejection | Exact inventory/hashes unchanged after runtime smoke. Tampered/missing executable and rooted/traversing manifest paths rejected. |
| AA–AC: artifact identity | Final filename, byte counts, file count and SHA-256 values below. |
| AD: CI | Windows calls canonical publisher after verify.ps1 in the same job, uploads ZIP + checksum; packaging failure fails job. Linux verification unchanged. Modified workflow has not yet run on GitHub because changes are uncommitted. |
| AE–AH: verification | Actual final totals/build/EF/audit below. |
| AI–AJ: limits | No clean physical/VM test; unsigned snapshot may encounter SmartScreen trust prompts. No protection-disabling guidance. |
| AK–AM: scope/safety | No installer, updater, arm64, live execution or loosened safety gates. No embedded credentials/user runtime data; no normal user database was migrated by verification. |
| AN: source state | Uncommitted; no push, release or BurnIn-01. |

## Final verification results (2026-09-24)

`pwsh -NoProfile -File scripts/publish.ps1 -AllowDirty -OutputDirectory artifacts/distribution-final` ran the canonical `scripts/verify.ps1` with restore and Release build before publishing. Restore succeeded; build reported **0 warnings, 0 errors**. All **855 tests passed**, with 0 failed and 0 skipped: Domain 55, Application 277, Backend integration 496, Desktop 27. The final suite includes the missing-manifest case and both real-process lifecycle variants. Existing authentication, authorization, storage, reconnect, Paper admission/arming/kill-switch and reliability tests remain in the full suite.

EF: `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build` succeeded with no pending model changes. NuGet: `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive` succeeded; no vulnerable packages reported across 13 projects using configured sources including nuget.org. `git diff --check`, PowerShell parser checks, new documentation links, and artifact ignore checks passed. No tracked DB/sidecar/PEM/PFX/key/connection/managed-runtime files were found by the filename hygiene scan. Existing .gitignore already covers build outputs, artifacts, user runtime storage and secrets, so it was preserved without unnecessary replacement.

Changed-file scope: canonical publishing/smoke scripts and packaged first-run text; Windows CI artifact step; Desktop manifest validation, migration orchestration, runtime path consistency and Settings diagnostics; backend package-local storage rejection/startup codes; Infrastructure consistent schema backups and initializer sequencing; focused distribution/backup and real-process lifecycle tests; architecture/operations/verification documentation, README and roadmap. Contracts, financial/execution algorithms, historical migrations, repository metadata and Linux CI behavior were not changed.

## Final portable artifact

Canonical publisher exited **0**. Extracted smoke passed with **Desktop window observed: True**, no installed dotnet on child PATH, isolated empty data, idempotent --migrate, authenticated backend restart with preserved identity, Paper-only capabilities, no initialized funds/risk/automation/campaign, package-local data refusal, exact package immutability, and tampered/missing/rooted/traversing-path rejection. No runtime file or credential entered the package.

- Output: `artifacts/distribution-final/ArbitrageTrading-win-x64-g486367bb-local-dirty.zip`
- External checksum: same filename plus `.sha256`
- Compressed ZIP: **120,520,311 bytes** (114.94 MiB).
- Uncompressed payload, including manifest: **270,005,255 bytes** (257.50 MiB), **791 files**.
- Manifest source: `486367bb9b5a488f7918afe14bce56ae028324db`, `SourceDirty: true`.
- Manifest build instant: `2026-09-24T15:58:45.8145039+00:00`.
- Target: `win-x64`, Desktop `net10.0-windows`, Backend `net10.0`.

| File | SHA-256 |
| --- | --- |
| Desktop EXE | `E64257C357B4DCADF69497D97CF3F9CBF2E976DECDBFD980C4CF396E132ADA4C` |
| Backend EXE | `2B505F1D6E243F78A1B9F405CEBCF7542E5899BAC214EB311D0F91E7A23EC9DD` |
| Backend appsettings.json | `30DB909658FD4E540D4DBDAFF4042514D1AA442F7E197ECF38978FF71F8AD684` |
| ZIP | `2E24590BCFA9568E6BC6EEFBEE703F7D9A47325F4ABFB2A01EFBBF32DAF8B0BB` |

The earlier `artifacts/distribution/` package was an intermediate smoke artifact; use `distribution-final` for this delivery. Generated artifacts remain ignored. This deliverable is a locally verified dirty snapshot, not an official release or clean-VM certification. All changes remain uncommitted on main; no commit, push, GitHub Release or BurnIn-01 was performed.

