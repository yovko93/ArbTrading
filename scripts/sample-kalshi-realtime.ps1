param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$MarketTicker,
    [ValidateRange(5,30)][int]$Seconds = 20
)
$ErrorActionPreference = 'Stop'
# Explicit opt-in against a user-selected running local backend. Never searches for credentials.
# The only bearer here is the existing protected local transport credential, not an exchange secret.
$connectionPath = Join-Path $RuntimeDirectory 'connection.json'
if (-not [IO.Path]::IsPathFullyQualified($RuntimeDirectory)) { throw 'Specify an absolute runtime directory.' }
for ($item = Get-Item -LiteralPath $RuntimeDirectory; $null -ne $item; $item = $item.Parent) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe runtime directory.' }
}
$file = Get-Item -LiteralPath $connectionPath
if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe connection file.' }
$acl = Get-Acl -LiteralPath $connectionPath
$current = [Security.Principal.WindowsIdentity]::GetCurrent().User
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $current) { throw 'Unsafe connection owner.' }
foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference -ne $current) { throw 'Unsafe connection permissions.' }
}
$connection = Get-Content -LiteralPath $connectionPath -Raw | ConvertFrom-Json
$uri = [Uri]$connection.baseUrl
$ip = $null
if ($uri.Scheme -ne 'http' -or -not [Net.IPAddress]::TryParse($uri.Host,[ref]$ip) -or -not [Net.IPAddress]::IsLoopback($ip) -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Invalid local endpoint.' }
$headers = @{ Authorization = 'Bearer ' + $connection.credential }
function Read-Local([string]$Path) { Invoke-RestMethod -Uri ($connection.baseUrl + $Path) -Headers $headers -TimeoutSec 5 -MaximumRedirection 0 }
function Post-Local([string]$Path) { Invoke-RestMethod -Method Post -Uri ($connection.baseUrl + $Path) -Headers $headers -TimeoutSec 5 -MaximumRedirection 0 }
$started = $false
try {
    $status = Read-Local '/api/v1/local-runtime/kalshi-credentials'
    if (-not $status.configured) { Write-Output 'NotConfigured'; return }
    $session = Read-Local '/api/v1/session'
    $prefix = '/api/v1/workspaces/' + $session.defaultWorkspaceId
    if (-not $MarketTicker) {
        $page = Read-Local ($prefix + '/catalog/markets?exchange=Kalshi&status=Open&page=1&pageSize=50&sort=title')
        $market = $page.items | Where-Object { $_.classification -eq 'binary' } | Select-Object -First 1
        if (-not $market) { Write-Output 'NotAttemptedInstrumentUnavailable'; return }
        $MarketTicker = $market.nativeId
    }
    $metadata = Read-Local ($prefix + '/catalog/markets/Kalshi/' + [Uri]::EscapeDataString($MarketTicker))
    if ($metadata.classification -ne 'binary' -or $metadata.status -ne 'Open') { Write-Output 'NotAttemptedInstrumentUnavailable'; return }
    $path = $prefix + '/orderbooks/Kalshi/' + [Uri]::EscapeDataString($MarketTicker)
    $before = Read-Local ($path + '?instrumentId=yes')
    if ($before.realtime -and $before.realtime.state -notin @('Stopped','NotSubscribed','AuthenticationRequired','AuthenticationFailed','Faulted')) { throw 'The selected instrument already has a realtime owner.' }
    $null = Post-Local ($path + '/realtime/start?instrumentId=yes'); $started = $true
    $watch = [Diagnostics.Stopwatch]::StartNew(); $anchor = $null; $last = $null; $gap = $false
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 500
        $last = Read-Local ($path + '?instrumentId=yes')
        if ($last.realtime.resyncReason -eq 'SequenceGap') { $gap = $true }
        if ($last.realtime.anchorAt -and -not $anchor) { $anchor = $last.realtime.sequence - $last.realtime.deltaCount }
        if ($last.realtime.state -in @('AuthenticationRequired','AuthenticationFailed','NetworkRestricted','Faulted')) { break }
    }
    if ($last.isActionable -and $last.snapshot.asks.Count -gt 0) {
        $depthBody = @{ instrumentId='yes'; action='Buy'; quantity=1; diagnosticOnly=$false } | ConvertTo-Json
        $null = Invoke-RestMethod -Method Post -Uri ($connection.baseUrl + $path + '/depth') -Headers $headers -ContentType 'application/json' -Body $depthBody -TimeoutSec 5 -MaximumRedirection 0
    }
    [pscustomobject]@{
        Exchange='Kalshi'; MarketTicker=$MarketTicker; Connected=$last.realtime.connected
        Authenticated=($last.realtime.anchorAt -ne $null); SnapshotReceived=($anchor -ne $null)
        StartSequence=$anchor; EndSequence=$last.realtime.sequence; DeltaCount=$last.realtime.deltaCount
        GapDetected=$gap; NativeBidLevels=$last.snapshot.bids.Count; DerivedAskLevels=$last.snapshot.asks.Count
        BestBid=($last.snapshot.bids | Select-Object -First 1).price
        BestAsk=($last.snapshot.asks | Select-Object -First 1).price
        Actionable=$last.isActionable; State=$last.realtime.state; Elapsed=$watch.Elapsed.TotalSeconds
    } | ConvertTo-Json
} catch { Write-Error 'Bounded realtime sample failed. Check local backend status; no secret details are printed.' }
finally {
    if ($started) { try { $null = Post-Local ($path + '/realtime/stop?instrumentId=yes') } catch { Write-Warning 'Stop could not be confirmed. Stop the selected subscription in Market Explorer.' } }
    $headers.Clear(); $connection = $null
}
