# Distribution D02.1 verification

## Baseline and gate

Repository `C:\Users\Yovko\source\repos\ArbTrading`; attached `main`; starting HEAD `5b3a217aa8f3d7bfad377771709fa32adcec27f2`, clean working tree, existing origin retained. Read AGENTS.md, D01/D02 reports and canonical distribution/signing/CI scripts. No Git metadata/history/branch/remotes were changed.

The exact [D02 run 36034974594](https://github.com/yovko93/ArbTrading/actions/runs/36034974594) was initially in progress with Linux, Windows tests, focused signing and unsigned publishing successful. A subsequent GitHub API read confirmed completed/success for the exact baseline SHA, with both jobs successful. The mandatory D02 gate passed, including its canonical test-signed smoke.

## Scope and design

Changed publishing and smoke orchestration, added shared workspace/capacity/promotion/recovery helpers and deterministic PowerShell tests, updated Windows CI WorkRoot arguments and documentation. No application C#, financial/execution behavior, signing policy/provider, migration, package layout, trimming/AOT/single-file setting or normal runtime storage changes.

Previous publisher left each approximately 270 MB staging tree behind, while candidate/smoke extraction also consumed output/system TEMP space. These were confirmed contributing distribution costs in the prior disk-full failures; the scripts cannot explain all concurrent changes in C: capacity. New staging/candidate and smoke/private test state use invocation-owned scratch and are removed in finally on normal success/failure. `-KeepWorkDirectory` explicitly retains debug data. Abrupt termination recovery is opt-in, marked-work-only and skips live owner processes. No disk-wide or generic cache cleanup.

Default WorkRoot is repository `artifacts/.work`. Explicit local test work root: `D:\ArbitrageTradingBuildWork`; output remains in repository artifacts on C:. Capacity rules: 1536 MiB work, 256 MiB output, 1792 MiB shared-volume aggregate; unknown capacity fails. Preflight occurs before source verification and publish. Promotion copies to a new temporary output file, flushes, hash-verifies, atomically renames within the output directory without overwriting existing output, verifies final hash and writes checksum. Source bin/obj and source-test TEMP retain their existing locations; no normal application state is moved.

No verification receipt was added: authenticated provenance/replay handling would expand this narrow correction, while arbitrary JSON success flags are inadequate. Default full source verification and strict exact-clean-CI-SHA SkipTests remain. Windows CI uses runner.temp and never KeepWorkDirectory, with primary-only artifact upload unchanged. Linux unchanged.

## Focused results

41 deterministic workspace assertions passed: sufficient space/exact boundary/one byte short, same/separate volume, unknown capacity, unsafe roots, package paths, ownership mismatch, reparse traversal, success and injected publish/signing/manifest/smoke/ZIP failure cleanup, debug retention, checksum/promotion, no durable overwrite, stale dry-run/explicit deletion and active-owner protection. Tests use small synthetic payloads and injected capacity evidence, not real disk exhaustion. An initial test harness variable-shadowing recursion was corrected; its exact owned scratch tree and test process were cleaned, and the suite was rerun successfully.

## Canonical verification progress

The first D02.1 unsigned invocation passed restore and Release build (zero warnings/errors). Domain 55 and Application 277 tests passed. Desktop had 25 passed and 2 failures in existing real-process lifecycle fixture cleanup (`backend.lock` held); only the exact two failed-test backend children were stopped after matching PID, parent and executable identity. C: simultaneously fell from approximately 3 GB to 170 MB free; inspected Arbitrage TEMP data was too small to attribute that decrease to those directories. This failed attempt is not counted as successful verification. Final retry/package results follow below.

All changes remain uncommitted. No commit, push, release or BurnIn-01 is performed by this task.

## Completed unsigned retry

With process-local `DOTNET_PROCESSOR_COUNT=2` to reduce concurrent resource pressure, the canonical publisher ran the unchanged full `verify.ps1` restore/build/test sequence successfully: **865 passed, 0 failed, 0 skipped** (Domain 55, Application 277, Backend 506, Desktop 27), **0 warnings/errors**. No tests or assertions were disabled and no repository/default concurrency setting was changed. The earlier run's backend suite also passed all 506; its failure was confined to the two Desktop cleanup cases recorded above.

`pwsh -NoProfile -File scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -WorkRoot D:\ArbitrageTradingBuildWork -OutputDirectory artifacts/distribution-d021-unsigned` exited 0. Its extracted smoke observed the real WPF window and passed bootstrap, idempotent migration, restart identity, Paper/no-live/no-funds/no-policy/no-arming/no-campaign assertions, package immutability and malformed/tampered/missing package rejection.

- ZIP: `artifacts/distribution-d021-unsigned/ArbitrageTrading-win-x64-g5b3a217a-local-dirty-unsigned.zip`
- SHA256: `52EA95CCB8AA9946A2A79FE87D182F4025559F72D6BB0616B3206C736F6C88CD`
- Staged/extracted payload: 270021890 bytes, 791 files; candidate/final ZIP: 120527121 bytes.
- Smoke workspace at cleanup: 270559507 bytes.
- One-second sampling observed peak scratch 660571343 bytes (~630 MiB), 137 samples; component-based simultaneous upper estimate ~661109000 bytes (~630.5 MiB). Sampling is approximate, not a byte-exact reservation/high-water counter.
- Post-success work root: **0 transient bytes**, no child workspaces. Final output contains only ZIP/checksum. Cross-volume D:→C: flush/hash/rename/checksum promotion succeeded.

EF pending-model check passed with no changes. NuGet transitive audit reported no vulnerable packages across all 13 projects. Focused signing regression passed on real Windows PEs, including both success/failure certificate and private-key cleanup. Final workspace regression count is **42** (including orchestration-level success/failure KeepWorkDirectory).

The original successful D02 unsigned ZIP remains intact: SHA256 `DD64EB13183B9D9052869770B71A7EEBDA75B14984555495181A1FDC0CD9D0F2`. Older durable artifacts were not deleted.

## Requested report mapping

| Items | Result |
| --- | --- |
| A–B | Expected clean baseline/main above; exact D02 CI completed/success. |
| C–E | Unbounded retained stage and system-TEMP smoke costs confirmed; additional C: fluctuation not attributed without evidence. New owned finally cleanup handles success/failure. |
| F–G | Default artifacts/.work; actual alternate scratch D:\ArbitrageTradingBuildWork. |
| H–I | Preflight before verification/publish; work 1536 MiB, output 256 MiB, shared 1792 MiB; unknown capacity refused. |
| J | Cross-volume flush/hash/same-directory no-overwrite rename, final hash/checksum. |
| K–L | Failure cleanup tested; explicit retention tested on success and failure, never enabled in CI. |
| M | No receipt reuse; full local tests retained rather than trust arbitrary local success metadata. |
| N | runner.temp WorkRoot, sequential cleaned modes, primary-only upload; modified workflow not run remotely because changes remain local. |
| O–R | Full counts/build/EF/audit documented above and final signed run below. |
| S–V | Canonical artifacts and hashes recorded in each result section. |
| W–AA | Real test signature/public identity/cleanup/signed-smoke evidence recorded with the signed artifact below. |
| AB–AC | Sampled peak/component sizes and zero remaining transient data recorded per mode. |
| AD–AE | Prior ZIP/checksum retained; original D02 ZIP hash rechecked. No normal user runtime DB or credentials moved/deleted. |
| AF–AH | Signing, financial/execution safety and payload semantics unchanged; no migration added. |
| AI | Changes uncommitted on main; no push, release or BurnIn-01. |

Limitations: preflight cannot reserve space against unrelated writers; tests used the development Windows host, not a clean VM. TestEphemeral is intentionally untrusted and never a production signing identity. No production certificate was acquired/used, signing policy relaxed, or trust-store root installed. Legacy unmarked scratch needs separate exact-path review; only marked abandoned work is eligible for the explicit recovery utility.

## Final TestEphemeral result

`pwsh -NoProfile -File scripts/publish.ps1 -AllowDirty -SigningMode TestEphemeral -WorkRoot D:\ArbitrageTradingBuildWork -OutputDirectory artifacts/distribution-d02-test-signed` exited **0**, with the same process-local DOTNET_PROCESSOR_COUNT=2. Default source verification ran again: restore successful, Release build **0 warnings/errors**, all **865 tests passed, 0 failed/skipped** (55/277/506/27). Thus both final canonical modes include successful full verification, without receipt reuse or local SkipTests.

- ZIP: `artifacts/distribution-d02-test-signed/ArbitrageTrading-win-x64-g5b3a217a-local-dirty-test-signed.zip`
- SHA256: `D2D8D14A20583D103B86B3C81F40FD1AB7F74D9AA4F02E87DE83280DCE39780F`
- External checksum: same filename plus `.sha256`, reread and matched against the final archive.
- Manifest: schema 2, SourceCommit `5b3a217aa8f3d7bfad377771709fa32adcec27f2`, SourceDirty true, SigningRequired true, TestEphemeral/TestSignatureOnly.
- Public signer subject: `CN=ArbitrageTrading D02 TEST ONLY ba6d5bdabb3f4d49bb7534f9b7d9d679`
- Public certificate thumbprint: `45952937A06248F4B244B26130D900757F0F5B4C`
- Desktop and Backend EXE signature states: **Untrusted, ContentValid true, SHA256, Timestamped false**, exactly the test-only policy. All 12 allowlisted signatures were independently reread and verified from the final ZIP after promotion and certificate deletion.
- Desktop post-sign SHA256: `0A34B769E6225183A18E123643D186F1BB9B9CDCC1B7D33CEAE1C216C6E54D32`
- Backend post-sign SHA256: `4F1C50B06C9E66242DC2EE5586A6D3FA92FCB71231D3256DFB44E28F4F7E04E7`
- Signing helper verified certificate **and private key removal** immediately after signing. Final inspection independently confirmed that certificate remained absent. No private signing material was exported, tracked or packaged.

Extracted signed smoke passed with the actual WPF shell observed, no automatic backend launch/migration, explicit isolated bootstrap, idempotent migration, self-contained backend startup/restart identity, Paper mode and no live availability, no first-run funds/risk/armed automation/campaign, package-root write refusal and exact immutability. Signed-byte corruption with rewritten manifest hashes and spoofed signing metadata were rejected by Desktop validation. Signature checks did not trust metadata alone.

Measured staged/extracted payload: **270040764 bytes**, **791 files**; candidate/final ZIP **120544280 bytes**. Smoke workspace at cleanup: **270578381 bytes**. One-second monitoring (331 samples) observed peak scratch **660626226 bytes (~630 MiB)**; summing stage/candidate/final smoke components gives approximately **661164000 bytes (~630.5 MiB)** including markers. This is an approximate transient high-water measurement, not a claim of exact instantaneous OS allocation. Output promotion uses one additional approximately 120.5 MB temporary file after smoke is cleaned, then renames it in place.

After canonical success: **0 scratch bytes, 0 child workspace directories**, no staging/candidate/extraction retained; durable output contains exactly ZIP and checksum. A later small final-signature inspection used the same owned-work helper and also returned the root to empty. Prior final artifacts were retained. Git diff whitespace checks and PowerShell parsing passed. No changes under src/ or .NET tests/, no new migration, and no signing policy changes. D02.1 source changes remain uncommitted on **main**; no push, GitHub Release or BurnIn-01.

Changed files: publisher and extracted smoke; new distribution-workspace.ps1, clean-distribution-work.ps1 and test-distribution-workspace.ps1; Windows workflow; README; architecture, operator and this verification documentation. The recovery helper defaults to report-only and was exercised on synthetic owned fixtures, not arbitrary user directories.
