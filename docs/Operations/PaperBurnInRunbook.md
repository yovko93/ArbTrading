# Paper burn-in operator runbook

This procedure is for the local **Paper** system at the Phase 04G baseline. Use the saved configuration and actual public market inputs. Keep a separate, private operator record of the configuration freeze; `report.json` does not embed a Git commit or complete configuration snapshot. Do not put credentials in notes, reports, tickets, or chat. The companion [daily checklist](PaperBurnInDailyChecklist.md) is for repeated observations.

## 1. Purpose and limitations

Collect multi-hour/multi-day evidence for an explicitly started Paper Reliability Campaign. Automatic Paper produces simulated snapshot BUY entries only. There are no exchange orders, real fills, private balances, automatic settlement, SELL/close orders, or claims about live profitability. A campaign may correctly end `InsufficientEvidence` with zero executions. The operator starts and controls the actual campaign; this document is not a request to run one unattended in a development task.

## 2. Preconditions

Use the intended local checkout, an authenticated owner workspace, a supported Windows build for WPF, local `TradingMode=Paper`, and a current migrated backend database. The backend, risk policy, automation profile, paper generation, monitoring, fee state, and market data must be reviewed before arming. The owner must have a plan for stopping, preserving, and investigating anomalous evidence. An existing `Collecting` or `Paused` campaign must be handled before starting another; the backend permits one active campaign per workspace.

## 3. One-time environment setup

Build the existing solution, configure a literal loopback `Local:BaseUrl`, and use the existing per-user local data directories. The header **LOCAL BACKEND → Start** launches a built backend artifact configured by `ARBITRAGE_BACKEND_ARTIFACT` (or placed beside Desktop); it does not build or restore. A built DLL needs `ARBITRAGE_DOTNET_HOST`. An externally launched backend can be observed but is not stoppable through Desktop. The header **Refresh** re-reads backend state; it does not start a process. Closing Desktop or losing its connection does not stop the backend, monitoring, or an armed automation session. See [LocalDevelopment](../Development/LocalDevelopment.md) for artifact and local launch details.

Optional Kalshi authenticated market-data WebSocket access uses **Settings → Select Private Key File → Import / Replace → Refresh status** with its key ID. Keep the key in the application's secure import workflow; Kalshi public REST catalog/data can work without it. Never paste a key into this runbook. Polymarket ordinary TLS/network access may be unavailable in this environment; do not bypass certificate validation, DNS, proxies, or trust roots to manufacture coverage.

Navigation labels are **Market Explorer**, **Opportunities** (the **Continuous monitoring** tab), **Trading** (also shown through **Portfolio**), **Paper Reliability**, **Logs & Diagnostics**, and **Settings**. The relevant owner-authenticated API routes begin `/api/v1`; `{workspaceId}` is the selected workspace. Source route map: `POST /workspaces/{workspaceId}/catalog/sync` for market sync; `POST /workspaces/{workspaceId}/orderbooks/{exchange}/{marketId}/refresh` for REST; matching `/realtime/start` and `/realtime/stop` for subscriptions; `GET /local-runtime/kalshi-credentials` and its `POST /import`/`POST /remove` subroutes for secure market-data credentials; `/workspaces/{workspaceId}/monitoring/{profile,status,start,stop}` for monitoring; `/workspaces/{workspaceId}/paper/{account,admission-policy,automation/status,automation/profile,automation/arm,automation/disarm,automation/emergency-stop,automation/reset-kill-switch,reliability/*}` for paper state/campaign actions; `/workspaces/{workspaceId}/paper/resolutions/{preview,confirm}` for Manual Scenario settlement. **Logs & Diagnostics** shows bounded backend diagnostics (`GET /workspaces/{workspaceId}/diagnostics`) plus local diagnostics. Write actions require authenticated ownership; this route map does not replace the app's confirmation and secure authentication flow.

## 4. Preflight verification

From `C:\Users\Yovko\source\repos\ArbTrading` in PowerShell, run these read/build checks and save the non-secret outputs with the operator record:

```powershell
Set-Location 'C:\Users\Yovko\source\repos\ArbTrading'
git status --short --branch
git rev-parse HEAD
dotnet --info
dotnet restore ArbitrageTrading.sln
dotnet build ArbitrageTrading.sln -c Release --no-restore
dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build
```

The EF command checks **source model versus migrations** using a design-time placeholder, not whether the normal user's database is upgraded. The test suite uses isolated migration-backed databases; its success does not migrate the normal runtime database. If normal startup reports pending migration, **Disarm**, stop the backend, verify it exited, and back up the entire backend directory while stopped. Only then use the application's documented explicit migration command with the same non-secret runtime configuration, from this repository root:

```powershell
dotnet run --project src/Arbitrage.Backend --no-launch-profile -- --migrate
```

It takes the storage lease, applies pending migrations, and exits; then explicitly start the backend again. Do not run `dotnet ef database update` on normal storage or delete a database to resolve a failure. After a failed migration, preserve both copies for engineering investigation.

Before the official campaign, save a backup while the backend is stopped. Default backend data is `%LOCALAPPDATA%\ArbitrageTrading\backend` (`Local__DataDirectory` can override it), runtime metadata is `%LOCALAPPDATA%\ArbitrageTrading\runtime` (`Local__RuntimeDirectory` can override it), and Desktop preferences have their own directory. Use the header **Stop** only for a backend owned by Desktop; it acknowledges a stop *request*, so wait for the process/status to show stopped. For an external backend, stop its owning process through its normal launch console/service. For a recoverable migration backup, copy the **whole backend directory**, including `arbitrage.db` and any `arbitrage.db-wal`/`arbitrage.db-shm` sidecars, while stopped to a private location outside Git; that copy can contain `credentials/` and must be protected as secret-bearing. For a narrower evidence archive, copy only the stopped SQLite database and required sidecars, `reliability/` reports and selected sanitized logs/config; omit `credentials/` unless credential recovery is specifically needed. Never omit database sidecars from a raw database copy. Record effective `Local__DataDirectory` and `Local__RuntimeDirectory` overrides without bearer material; do not copy or publish runtime connection/credential files as evidence. Restart only by the explicit **Start** action after the copy. Never copy active SQLite files as a casual file backup.

## 5. Paper account preparation

In **Trading**, use **Refresh paper account** and record the generation ID, integrity, and each balance's venue/currency, initial and available cash. If no generation exists, the owner may explicitly use **Initialize / Reset paper generation…** after reviewing the proposed Kalshi USD and Polymarket USDC amounts and reason. These amounts are simulated and must be chosen by the owner; the form's defaults are suggestions only. Reset creates a *new* generation and retains old history. Do not reset merely to gain evidence. Before the campaign, run the existing owner-authenticated `POST /api/v1/workspaces/{workspaceId}/paper/reconcile?generationId={generationId}` through an authorized local API client; there is currently **no WPF reconciliation button**. This explicit diagnostic can mark a corrupt generation; it does not silently repair it. Record result and UTC time, then refresh the account. Proceed only with `Healthy`. `NeedsReconciliation` requires the explicit check; `Corrupt` blocks new entries and requires investigation. Do not use raw bearer tokens in shell history or documents.

The following Windows PowerShell example performs **only that explicit reconciliation** against the existing running local backend. Run it as the local owner after confirming the runtime directory; it validates the connection file's owner, access rules and loopback URL, and never prints the bearer. It uses the current workspace and active generation returned by the backend. If the backend reports no active generation, stop and initialize explicitly in WPF first. Do not paste the resulting in-memory `$burnInHeaders` into a log or document.

```powershell
$burnInRuntime = if ($env:Local__RuntimeDirectory) { $env:Local__RuntimeDirectory } else { Join-Path $env:LOCALAPPDATA 'ArbitrageTrading\runtime' }
if (-not [IO.Path]::IsPathFullyQualified($burnInRuntime)) { throw 'Runtime directory must be absolute.' }
$burnInConnectionPath = Join-Path $burnInRuntime 'connection.json'
for ($burnInItem = Get-Item -LiteralPath $burnInRuntime; $null -ne $burnInItem; $burnInItem = $burnInItem.Parent) {
    if ($burnInItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe runtime path.' }
}
$burnInFile = Get-Item -LiteralPath $burnInConnectionPath
if ($burnInFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe connection file.' }
$burnInAcl = Get-Acl -LiteralPath $burnInConnectionPath
$burnInOwner = [Security.Principal.WindowsIdentity]::GetCurrent().User
if ($burnInAcl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $burnInOwner) { throw 'Unsafe connection owner.' }
foreach ($burnInRule in $burnInAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($burnInRule.AccessControlType -eq 'Allow' -and $burnInRule.IdentityReference -ne $burnInOwner) { throw 'Unsafe connection permissions.' }
}
$burnInConnection = Get-Content -LiteralPath $burnInConnectionPath -Raw | ConvertFrom-Json
$burnInUri = [Uri]$burnInConnection.baseUrl
$burnInIp = $null
if ($burnInUri.Scheme -ne 'http' -or -not [Net.IPAddress]::TryParse($burnInUri.Host,[ref]$burnInIp) -or -not [Net.IPAddress]::IsLoopback($burnInIp) -or $burnInUri.AbsolutePath -ne '/' -or $burnInUri.UserInfo -or $burnInUri.Query -or $burnInUri.Fragment) { throw 'Invalid local endpoint.' }
$burnInHeaders = @{ Authorization = 'Bearer ' + $burnInConnection.credential }
try {
    $burnInSession = Invoke-RestMethod -Uri ($burnInConnection.baseUrl + '/api/v1/session') -Headers $burnInHeaders -MaximumRedirection 0
    $burnInPrefix = '/api/v1/workspaces/' + $burnInSession.defaultWorkspaceId
    $burnInAccount = Invoke-RestMethod -Uri ($burnInConnection.baseUrl + $burnInPrefix + '/paper/account') -Headers $burnInHeaders -MaximumRedirection 0
    if (-not $burnInAccount.generation) { throw 'No active paper generation; initialize explicitly in WPF first.' }
    $burnInResult = Invoke-RestMethod -Method Post -Uri ($burnInConnection.baseUrl + $burnInPrefix + '/paper/reconcile?generationId=' + $burnInAccount.generation.id) -Headers $burnInHeaders -MaximumRedirection 0
    [pscustomobject]@{ GenerationId=$burnInResult.generationId; Integrity=$burnInResult.integrity; CheckedAtUtc=[DateTimeOffset]::UtcNow }
} finally { $burnInHeaders.Clear(); $burnInConnection = $null }
```

## 6. Risk policy preparation

In **Paper Trading → Paper Risk**, choose **Refresh risk status** and **Load saved policy into form**. Record `PolicyVersion`, `Revision`, `Fingerprint`, and every effective `Limits` field from the saved `GET /api/v1/workspaces/{workspaceId}/paper/admission-policy` response: minimum cash reserve, single-execution debit, total/market/instrument open-cost fractions, maximum open positions/executions/per-relationship executions, maximum requested quantity, minimum fee-adjusted edge, and minimum fee-adjusted profit. Check the assessment and headroom by venue/currency. Use **Save Risk Policy…** only for an intentional, owner-confirmed change before the freeze. An absent or invalid risk policy prevents new paper exposure; do not weaken it to increase counts.

## 7. Automation profile preparation

In **Paper Trading → Automatic Paper Execution**, use **Refresh Auto Paper** and **Load saved Auto Paper settings**. Record saved `PolicyVersion`, `Revision`, `PolicyFingerprint`, `SizingMode`, `FixedQuantity` or adaptive `MinimumQuantity`/`MaximumQuantity`/`QuantityStep`, minimum fee-adjusted edge/profit, per-session/hour/relationship caps, both cooldowns, session debit fraction by bucket, maximum candidates per cycle, `RequireRealtime`, and `AllowPolymarketBestEffort`. Keep the owner's saved Fixed or Adaptive mode. Do not switch modes for the first campaign. **Save Auto Paper profile…** is an explicit policy change and cannot be used while armed; it never arms automatically. The application requires `RequireRealtime=true`; no REST-only override is valid.

## 8. Realtime market-data preparation

Use **Market Explorer → Sync Markets** for public catalog metadata and select instruments. **Refresh Order Book** requests REST data; **Start Realtime** and **Stop Realtime** control a selected instrument's live subscription. These are separate controls: monitoring does not acquire books or subscribe automatically. Check each leg's current source, age, anchor/streaming/connection, and continuity. Automatic Paper accepts only current actionable realtime depth, deterministic eligible relationships, two BUY complementary legs, `Detected` opportunity status, resolved `FeeAdjustedDetected` economics, and the saved edge/profit/risk/session gates. Kalshi legs need authenticated WebSocket data with `Continuous` continuity; REST is diagnostic only for Auto Paper. Polymarket may be `BestEffort` only when the saved profile explicitly allows it; the UI warns and asks for acknowledgment on save and arm. BestEffort is weaker continuity, never promoted to Continuous. If ordinary Polymarket access or Kalshi credentials are unavailable, record that coverage limitation and expect `InsufficientEvidence`; do not bypass network protections or downgrade `RequireRealtime`.

## 9. Monitoring startup

In **Opportunities → Continuous monitoring**, inspect the saved profile (`RelationshipLimit`, trust inclusion, edge/quantity/skew/near-edge bounds, sort, fee/gross alert thresholds, retention and CSV choices). **Start Monitoring** explicitly; **Refresh display** must show `Running`. `Save monitoring profile` changes the saved settings, so perform it before freeze. Record the exact coverage fields: `ApprovedRelationshipsAvailable`, `RelationshipsMonitored`, `RelationshipsSkippedByBound`, `CoveragePartial`, `PlansBuilt`, `PlansWithBooksAvailable`, `PlansWithActionableBooks`, `PlansWithResolvedFees`, `FeeAdjustedOpportunities`, `GrossOnlyOpportunities`, `NearEdgeCandidates`, and `BlockedCandidates`. Do not claim all markets are monitored if `CoveragePartial=true`. The Auto Paper worker only consumes current `FeeAdjusted` rankings; `GrossOnly` is diagnostic. Confirm actual fee profile and selected result's fee state in **Settings** and **Opportunities → Explicit evaluation → Evaluate cached fees / Refresh Fee Data for selected result**. The Kalshi official public-fee source conflict may leave gross opportunities but no resolved fee-adjusted candidate. Do not change fee assumptions simply to create executions; record fee profile value, result `ProfileRevision` where exposed, schedule state/fingerprints, and the limitation. `GET /api/v1/workspaces/{workspaceId}/fees/profile` itself returns only profile name, not a revision.

## 10. Reliability campaign start

Create a private **configuration-freeze record** before start: Git SHA from preflight; backend application `Version` from authenticated `GET /api/v1/system/status` (there is no built-in campaign Git identity); UTC timestamp; active generation ID and starting balances by exchange/currency; risk version/revision/fingerprint/all limits; automation version/revision/fingerprint/all fields above; kill-switch `IsLatched`, `Revision`, reason/time if set; monitoring profile/scope/thresholds and coverage; fee profile, available fee revision/state; realtime source/continuity/age and unavailable venues. Do not add secrets. The `Notes` field is bounded to 1,000 characters: use a short non-secret summary such as `commit=<sha>; risk=<revision>; auto=<revision>; purpose=first paper burn-in`; retain the full record separately.

First campaign: **Paper Reliability Campaign → Campaign name** `BurnIn-01`, enter notes, then **Start Reliability Campaign…** and confirm. Record `CampaignId`, `PolicyVersion`, `PolicyFingerprint`, `StartedAt`, `Revision`, and state `Collecting`. Start the campaign **before** arming: the observer is active from campaign start and records monitoring runtime, arm/session, candidate offers, and executions after that point. Starting a campaign does not arm Auto Paper. `Evaluate Now…` after setup provides a first persisted baseline; zero early counts are expected.

## 11. Auto Paper arm procedure

Use this exact order: (1) backend running and authenticated; (2) generation reconciled `Healthy`; (3) saved risk policy valid; (4) saved automation profile valid; (5) kill switch clear and reason reviewed; (6) current eligible realtime books and resolved fees assessed; (7) monitoring `Running`; (8) campaign `Collecting`; (9) coverage and fee-adjusted lane inspected; (10) **Paper Trading → Arm Auto Paper…**, review the profile/risk/generation and BestEffort warning, and explicitly confirm. The server binds expected profile, risk, generation and kill revisions, Paper mode and monitoring state. A success returns `Armed` with a session ID; a failed/uncertain response is **not** retried blindly—use **Refresh Auto Paper** first. Arm is always explicit, including after restart/reset. WPF closure does not disarm.

## 12. Normal observation procedure

After roughly 5 minutes, one hour, and daily, inspect **Opportunities → Continuous monitoring → Refresh display**, **Trading → Refresh Auto Paper / Refresh paper account / Refresh risk status**, **Paper Reliability → Refresh / Evaluate Now…**, and **Logs & Diagnostics**. Note `Running`, `Armed` or deliberate `Disarmed`, kill state, `Collecting`, generation integrity, coverage, current book age/quality, candidate funnel, sizing attempts, committed/rejected executions, duplicate suppression, unexpected worker faults, evidence gaps, and invariant results. A local 30-second campaign checkpoint and approximately five-minute evaluation are normal; **Evaluate Now…** is useful after setup, first automatic paper execution, significant restart, daily review, and before completion—not every second. No constant manual watching is required. Use summarized diagnostics/campaign events and automation counters, not verbose per-book logging. Never log API keys, private keys, local bearer tokens, or full orderbooks.

## 13. What NOT to change during a campaign

Treat a material change to risk limits, automation mode/grid/thresholds/quality, fee assumptions/profile, monitoring scope/thresholds, eligible relationship policy, paper generation, or application binary/commit as a campaign boundary. Existing code may disarm on risk/automation/generation changes and monitoring stop; fee and monitoring settings can change observed opportunity flow without a guaranteed automatic campaign boundary. The campaign does **not** automatically freeze all configuration. Procedurally **Disarm**, pause or complete, evaluate/export, preserve the old campaign, change configuration, and start a new campaign with a new freeze. Do not weaken safety/fee/relationship/realtime gates or production evidence minimums to reach a desired count.

## 14. Expected healthy states

Backend connected in Paper mode, paper generation `Healthy`, valid saved risk/automation profiles, kill switch clear, monitoring `Running`, campaign `Collecting`, and—only when intentionally active—Auto Paper `Armed`. Current candidate legs are realtime Kalshi `Continuous` or acknowledged Polymarket `BestEffort`; books remain actionable and fee-adjusted economics resolved. `EvidenceGapDetected=false` and no violated/unknown mandatory invariants are the desired evidence state. A candidate rate or execution count of zero is not by itself a fault.

## 15. Common insufficient-evidence states

Short observed runtime, no genuine candidates, fewer than 25 distinct trigger inputs, fewer than 20 sizing attempts or five automatic entries, missing Kalshi WebSocket credentials, unavailable Polymarket network access, stale/REST-only books, missing fee schedules, Kalshi fee-source conflict, `GrossOnly` rankings, or partial monitoring coverage can all prevent meeting minimums. `EvidenceGapDetected`, retention/scan bounds, unknown historical proof, or unavailable reconciliation also block a pass. Do not treat `GrossOnly` as fee-adjusted, sum USD and USDC, or fabricate test events in production. Manual paper settlement is separate, explicit **Paper Trading → Preview Resolution → Confirm Paper Resolution**; the production policy currently requires zero fully settled executions, so settlement must not be performed solely to boost a criterion.

## 16. Fault / emergency procedure

| Signal | Immediate action | Evidence and next step |
| --- | --- | --- |
| `InvariantViolation` | **EMERGENCY STOP** Auto Paper. | Preserve database, logs, report, campaign events; do not reset evidence or generation before engineering investigation. |
| Generation `Corrupt` or negative/mismatched ledger | **EMERGENCY STOP** or **Disarm**; stop new entries. | Preserve state; run explicit reconciliation only as part of investigation. No silent repair. |
| `UnexpectedWorkerFaults > 0` or Auto Paper `Faulted` | **Disarm** if not already stopped. | **Evaluate Now…**, export, collect sanitized diagnostics; investigate worker cause. |
| Kill switch latched | Leave latched. | Record reason, `LatchedAt`, revision, diagnostics and ordering; reset only after cause is understood. |
| Monitoring stopped | Check that Auto Paper is disarmed; use **Disarm** if uncertain. | Inspect source/status; restart and re-arm only explicitly after review. |
| `EvidenceGapDetected` / telemetry persistence failures | Stop interpreting report as a pass. | Inspect storage/restart/retention, evaluate/export; retain gapped report. |

If UI status is stale or unreachable, do not assume an arm or stop command succeeded. Restore local authenticated visibility, inspect state, and prefer the existing emergency control when a safety concern is active.

## 17. Kill-switch procedure

**Paper Trading → EMERGENCY STOP** latches the persistent kill switch and stops Auto Paper. Record reason, time and revision; campaign events record the latch and durable writer order, which matters when execution and latch share a UTC timestamp. Do not erase earlier valid transactions. Inspect **Refresh Auto Paper**, paper execution history, campaign events, and diagnostics. **Reset kill switch…** requires an explicit confirmed reason and matching revision after investigation. It leaves Auto Paper `Disarmed`; **Arm Auto Paper…** is a separate future decision. Do not reset merely to resume evidence collection.

## 18. Backend restart procedure

First **Disarm** and record current status; use **Evaluate Now…** and export an interim snapshot. For a Desktop-managed backend use header **Stop**, confirm, then wait for actual stopped/process-exit status. For an external backend, stop it through the process owner; Desktop cannot stop it. After safe backup/migration if needed, use header **Start** for a managed artifact or the normal external launch command; then header **Refresh**. A collecting campaign resumes collection with a new backend interval; clean shutdown flushes observed time. An abrupt loss may mark an evidence gap and lose uncheckpointed in-memory counts. Backend downtime is excluded. Monitoring restarts `Stopped` and Auto Paper remains `Disarmed`; inspect generation, risk, kill, fees/books, restart monitoring explicitly, evaluate the campaign, and arm only after renewed confirmation. Do not claim continuous venue coverage across restart.

## 19. Daily evidence review

Follow the [daily checklist](PaperBurnInDailyChecklist.md). Record UTC time, commit/config freeze continuity, campaign state, latest `EvaluatedAt`, gaps/invariants/faults, monitoring coverage, source quality, fee state, sessions, candidate/sizing/execution/rejection/duplicate counts, per-venue/currency balances, and any operational event. **Evaluate Now…** and optionally export an interim report. Do not complete the campaign each day. Keep logs at summary level; report and SQLite contain sensitive local operational history and belong in private storage.

## 20. Campaign evaluation

**Paper Reliability Campaign → Evaluate Now…** persists a snapshot with criteria, invariants, per-generation/venue/currency economics, references and `EvidenceFingerprint`. It performs local read-only financial reconciliation; it does not fetch exchange data, mutate financial rows, arm/disarm, or settle. `InsufficientEvidence` is legitimate. A proven violation has precedence; unknown/gap prevents `CriteriaMet`. The standard v1 minimums are backend 24h, monitoring 24h, armed 4h, healthy 4h, candidate inputs 100, distinct inputs 25, sizing attempts 20, automatic paper executions 5, fully settled minimum 0, and unexpected worker faults maximum 0, with mandatory invariants satisfied. These are not adjustable operator targets.

## 21. Campaign completion

When the observation window is over, **Disarm** first (completion itself does **not** disarm), review latest balances and integrity, **Evaluate Now…**, then **Complete…** with confirmation. `Completed` freezes the final evaluation/report; later paper activity does not alter it. Use **Cancel…** only when abandoning a campaign while retaining its history. Do not delete the old campaign to restart evidence. If final evaluation fails, inspect the returned state: code can resume collecting with a gap rather than fabricate a completed report.

## 22. Report export and archive

Select the campaign, use **Read selected report** if needed, then **Export Report**. It writes a private, atomically replaced `report.json` at `<effective Local__DataDirectory>\reliability\<workspaceId N>\<campaignId N>\report.json`; Desktop displays the actual returned path. Export requires a persisted evaluation and is capped at 10 MB. Archive the report together with the separate private freeze record, `CampaignId`, `EvidenceFingerprint`, `PolicyVersion`, `PolicyFingerprint`, UTC evaluation time and intended Git SHA. The report itself has no Git SHA/config freeze fields; its campaign `Notes` can hold a short operator-entered non-secret reference, but notes alone are not a complete freeze. Re-export of a frozen completed evaluation is byte stable. Keep reports and backups outside Git with restricted access; do not copy local bearer or credential files into the archive.

## 23. Interpretation of CriteriaMet

`CriteriaMet` means the configured **paper evidence** minimums and known invariants were met for that report snapshot. It does not prove exchange fill quality, order latency, live atomicity, transferable real-capital return, or legal/operational permission to trade live. It unlocks no live capability, and no live-order transport exists here.

## 24. Conditions that require restarting the campaign

Start a new named campaign and freeze after a material risk-policy change, automation sizing mode/grid/quality or threshold change, fee-assumption/profile change, monitoring scope/threshold change, eligibility/trust policy change, software binary/commit change, generation reset or integrity repair, or a campaign whose gap makes the intended one-version evidence unusable. **Disarm**, pause/complete/cancel as appropriate, evaluate and export the old record first. Retain both campaigns. A simple Desktop navigation or reconnect does not itself require a new campaign; assess any resulting observation gap or state change.

## 25. Conditions that require engineering investigation

Investigate `InvariantViolation`, `Corrupt`, unexplained `NeedsReconciliation`, nonzero unexpected worker faults, unexplained automatic disarm/latch, telemetry persistence failures or `EvidenceGapDetected`, report/export failure, non-deterministic completed report bytes, unexplained duplicate suppression, negative balances, unexpected migration/lease failure, or sustained lack of expected coverage despite verified ordinary access. Preserve data and sanitized diagnostic context. Do not weaken production gates to make an investigation disappear.

## 26. Explicit prohibition on treating burn-in as live-trading approval

This runbook authorizes **paper observation only**. Neither a completed campaign nor `CriteriaMet` is approval to add live-order submission, signing, private account reads, automatic settlement, SELL/close execution, or real-capital deployment.
