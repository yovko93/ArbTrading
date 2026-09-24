# Portable distribution architecture (D01)

The Windows x64 ZIP contains separate self-contained Release publishes: net10.0-windows WPF Desktop at the root and net10.0 ASP.NET Backend under `backend/`. Both carry their runtime; publish explicitly disables trimming, NativeAOT and single-file bundling. Desktop still references Contracts only and reads business state through authenticated HTTP. No installer, runtime installation, updater or capability changes are involved.

```
ArbitrageTrading/
  Arbitrage.Desktop.exe
  [Desktop assemblies and self-contained runtime]
  backend/
    Arbitrage.Backend.exe
    appsettings.json
    [Backend assemblies and self-contained runtime]
  distribution-manifest.json
  README-FIRST-RUN.txt
```

Manifest schema 1 records Product, RuntimeIdentifier, TargetFrameworkDesktop/Backend, SourceCommit, SourceDirty, UTC BuildUtc, PackageMode, relative Desktop/Backend/BackendConfig paths and SHA-256 values, plus a Files map covering every payload file except the manifest itself. ExpectedTradingMode is descriptive only. Source identity is embedded at build time; runtime never invokes git. ZIP SHA-256 is external. These checks detect corruption, not malicious replacement of both files and unsigned manifest.

Backend selection is explicit absolute ARBITRAGE_BACKEND_ARTIFACT, packaged backend executable, then legacy adjacent development executable. Presence of the manifest or packaged backend directory selects the package safety boundary. Packaged Start requires a valid manifest and that the selected executable is the manifest's fixed backend path; overrides cannot bypass integrity. Relative/rooted/traversing paths, reparse points, unexpected/missing files, hash mismatches and unsafe config fail closed. Missing manifest remains supported only for development layouts. Invalid package status remains visible in Settings; Start cannot launch it. `Arbitrage.Desktop.exe --validate-package` exits 0 for valid packages, 2 otherwise, and never starts a backend.

Settings shows package mode, manifest state, RID, source/dirty identity, build instant, data/runtime locations and last database startup result. The initial scan is cached for display; explicit Start rescans before migration and before normal launch. These checks are not a code-signing substitute and do not protect against an attacker concurrently rewriting an executable after validation.

Desktop launch and Refresh only observe. Explicit packaged Start takes the existing desktop launch lock, checks ownership and port availability, validates package/storage, executes the packaged backend with `--migrate`, awaits successful exit, revalidates, then starts it normally. Both processes receive identical absolute data/runtime paths, loopback URL and pinned Local/Paper/ManagedLocal settings. No migration process runs concurrently with the normal process started by this operation. The backend's LocalRuntimeLease still excludes other processes using that profile. Failure, cancellation or timeout prevents normal launch. Only a timed-out migration child owned by this operation may be terminated. Normal Start/Stop ownership metadata continues binding instance, profile, workspace, executable, PID and start time. Closing Desktop leaves a running backend intact.

Under the lease, an existing database with pending migrations is copied using SQLite BackupDatabase, including committed WAL state. The backup passes integrity_check and private-file verification before becoming a completed `.db` backup. Failed copies remain `.pending` evidence and are never mistaken for completed backups. Latest five completed backups are retained under `backend/backups/schema`, named with UTC time and GUID. Retention failure blocks migration. There is no backup for new/current schema. Unknown migration IDs fail as UnsupportedNewerSchema before backup or mutation. Manual normal backend launch retains explicit-migration refusal. Migration failure never deletes/resets the database or auto-restores a backup; operators preserve state for recovery.

Mutable state remains under the current OS user's LocalAppData/ArbitrageTrading: backend database/logs/credentials/evidence/backups, runtime connection and ownership metadata, and desktop preferences/logs. Absolute advanced environment overrides remain supported; package-local mutable storage is rejected. Desktop validates its own storage before writing. Backend validates configuration and protected storage independently. Package appsettings contains only Local/Paper/loopback defaults. Environment overrides precede appsettings; existing nonloopback, mode, ownership and authorization restrictions remain. No user profile, bearer credential, exchange key or database is copied into a package.

The canonical PowerShell 7 publisher requires Windows x64, records commit/dirty identity, verifies source by default, publishes into a unique repository artifacts staging directory, scans for runtime/secret files, checks hashes/runtime configs, archives and tests an extracted copy outside the checkout. Default requires clean source. `-AllowDirty` produces an explicitly local-dirty ZIP. `-SkipTests` requires CI plus matching verified SHA and clean source; Windows CI supplies it only after successful verify.ps1 in the same job. Linux verification is unchanged. No build step migrates normal user storage or publishes a release.
