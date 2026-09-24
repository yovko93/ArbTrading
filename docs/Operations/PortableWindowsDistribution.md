# Portable Windows first run and upgrades

Use a Windows x64 machine. Download the CI snapshot ZIP and its `.sha256` file from the same verified build. Compare `Get-FileHash <zip> -Algorithm SHA256` with the supplied value before extraction. Snapshots may be unsigned or explicitly signed; none is an installer or automatically a release; a checksum is not proof of publisher identity. Windows SmartScreen or antivirus may warn. Follow your organization's trust policy; D01 does not advise disabling protections.

Extract the entire ZIP into a normal local directory (spaces are supported), open its ArbitrageTrading folder and launch Arbitrage.Desktop.exe. No .NET runtime/SDK, Visual Studio, Node.js, global PATH edit, administrator installation or checkout is required. Open Settings to inspect package/source status. Desktop initially remains disconnected; choose the explicit local backend Start control. Start validates the package, initializes or upgrades your private local database, then starts the backend. Offline local startup needs no exchange credentials or public network.

Paper simulation is implemented. First run does not create paper funds, save risk or automation policy, arm automatic Paper, start monitoring/subscriptions or create a reliability campaign. Live order submission remains unavailable. Trading controls retain their existing explicit configuration and arming requirements. Distribution is not live-trading or burn-in approval.

Default state is `%LOCALAPPDATA%\ArbitrageTrading\backend`, `runtime`, and `desktop`. The package directory stays unchanged. Keep backups and any credentials private; never place them alongside distributed executables. Advanced absolute Local__DataDirectory/Local__RuntimeDirectory and ARBITRAGE_DESKTOP_DIRECTORY overrides are supported outside the package. Local__BaseUrl must remain loopback HTTP. Packaged backend overrides must resolve to the manifest-verified executable. No ARBITRAGE_DOTNET_HOST is required. Legacy development DLL launches still require an absolute dotnet host.

To upgrade, stop the managed backend, extract the new ZIP into a new application directory, open its Desktop and explicitly Start. Pending upgrades first create a consistent private SQLite backup under backend/backups/schema; five successful backups are retained. User/workspace identity and existing business data remain. Two side-by-side packages cannot own the same profile simultaneously. A backend started from another package is not automatically considered owned; do not kill it to bypass ownership checks.

If Start reports invalid package, missing backend, architecture mismatch or hash failure, preserve user storage and extract a fresh trusted ZIP. Migration/backup failures prevent normal backend launch; inspect private backend logs and Settings without sharing secrets. Restore permissions or disk space as appropriate before retrying. Never delete a database to clear an upgrade error. A newer schema cannot be automatically downgraded. Rollback requires an operator-selected compatible backup and matching application version with all backends stopped; keep the original database and backup before attempting recovery.

For source checkouts, run `pwsh ./scripts/publish.ps1` from a clean checkout; it runs verification, publish, scan, ZIP creation and isolated smoke. Use `-AllowDirty` for a visibly labeled local package. Output is artifacts/distribution; choose another directory beneath artifacts on repeat runs. Packaging never touches normal user data.

## Manual clean-machine checklist

D01 automation on a development Windows host is not a truly clean-VM test. On a fresh Windows x64 VM with no .NET/SDK/Visual Studio/Node/source checkout:

1. Transfer ZIP/checksum, verify hash, extract to a directory with spaces.
2. Launch Desktop offline. Confirm no backend auto-start and inspect Settings package identity.
3. Explicitly Start. Confirm authenticated Paper session, one persistent local profile/workspace and unchanged package files.
4. Confirm uninitialized paper funds, unsaved risk/automation, no campaign and unavailable live orders.
5. Close/reopen Desktop; backend remains independent. Stop/Start through verified controls; identity persists and automation remains disarmed.
6. With isolated prior-version fixture data, upgrade from a second extracted folder; check private schema backup and preserved records. Confirm older code rejects newer schema.
7. Record OS/build, source SHA, ZIP hash, results and trust prompts. Do not enter exchange credentials or start BurnIn-01 for this checklist.

A code-version change is a reliability campaign boundary under the existing burn-in runbook. D01 does not complete, cancel or start campaigns during migration; operators must perform the existing explicit campaign workflow before upgrading.


## Signing identity and advanced publishing (D02)

Filename suffixes distinguish `-unsigned`, `-signed` and `-test-signed`, with `-local-dirty` added for local modified source. Settings shows actual signatures and publisher, not just the manifest claim. In Windows, inspect an EXE's Properties → Digital Signatures → Details and signer certificate. Developers can use `pwsh ./scripts/verify-signatures.ps1 -PackageRoot <extracted-ArbitrageTrading-folder>`; the application itself needs no SDK/PowerShell verification step. Continue comparing the ZIP's SHA256 with its external checksum. Embedded public certificates are normal; there should be no standalone private key/PFX in the package.

Unsigned snapshot: Windows download/reputation warnings may occur. Test signature only: not a publicly trusted production publisher; do not install or trust the ephemeral certificate. Production SignedSnapshot: Authenticode signature valid under the configured identity and local Windows policy. This does not mean Microsoft approved, SmartScreen trusted, or warning-free. A publicly trusted code-signing certificate or managed signing service is needed for normal publisher trust; no particular vendor or EV/standard certificate guarantees every reputation outcome. Follow your organization's trust policies and never disable Edge/browser security, SmartScreen or Defender. A ZIP is not signed like an EXE/MSI/MSIX, and a future installer/distribution format may improve UX; D02 does not implement one.

Publishing/signing tools require PowerShell 7.5+ (.NET 9+ for the shared X509 certificate loader); this is a developer-tool requirement only. The self-contained application needs neither PowerShell nor an installed runtime.

Local development:

```powershell
pwsh ./scripts/publish.ps1 -AllowDirty -SigningMode Unsigned -OutputDirectory artifacts/d02-local
pwsh ./scripts/publish.ps1 -AllowDirty -SigningMode TestEphemeral -OutputDirectory artifacts/d02-test
```

Both commands run verification by default and use only isolated smoke storage. Test-only mode creates/removes its own CurrentUser/My certificate and private key without changing trusted roots. Do not distribute its artifact as a production-trusted package. Failed signing never falls back to unsigned.

Production operator prerequisites (no sample secrets): Windows SDK SignTool; a current code-signing certificate/private key already available to the operator in CurrentUser/My; CERTIFICATE_THUMBPRINT; SIGNING_TIMESTAMP_URL (absolute HTTPS RFC3161 endpoint, no credentials); optional EXPECTED_PUBLISHER (exact subject) and SIGNTOOL_PATH (trusted absolute Microsoft-signed SDK tool). Run `pwsh ./scripts/publish.ps1 -SigningMode Authenticode` from a clean checkout. Timestamping is required and may need network access. No PFX/password argument, export or enrollment is supported. The `/sha1` SignTool selector is the certificate thumbprint, not the signing digest; file and timestamp digests are SHA256.

Future production CI additionally needs WINDOWS_SIGNING_ENABLED=true, a protected pre-provisioned Windows runner selected by WINDOWS_SIGNING_RUNNER, encrypted secret WINDOWS_CERTIFICATE_THUMBPRINT, and variables WINDOWS_TIMESTAMP_URL, optional WINDOWS_EXPECTED_PUBLISHER/WINDOWS_SIGNTOOL_PATH. The certificate/private key must be provisioned securely outside this repository/workflow into that runner's CurrentUser/My; hosted windows-latest does not acquire one automatically. Protect main and isolate signing runners. Only a push to main with the explicit switch selects the signing path/runner. PR jobs use windows-latest and unsigned/test-only modes, with no production signing inputs. Missing provisioning fails closed. D02 does not upload test-signed artifacts as the main package, import secret PFX files, grant OIDC/write permissions or publish releases.

Runtime Authenticode inspection is offline. Fresh revocation retrieval is not performed and an uncached issuer chain may prevent a production signature from validating on a particular offline machine. A Windows-valid signature and RFC3161 timestamp remain distinct from browser/SmartScreen reputation.
