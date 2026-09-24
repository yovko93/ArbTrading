#requires -Version 7.5
param()
. (Join-Path $PSScriptRoot 'distribution-workspace.ps1')
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Expect-Rejection([scriptblock]$Operation) {
    $failed = $false
    try { & $Operation | Out-Null } catch { $failed = $true }
    Assert-Check $failed 'Expected rejection did not occur.'
}
$repo = Split-Path $PSScriptRoot -Parent
$fixture = New-DistributionWork (Join-Path $repo 'artifacts/.work-tests')
try {
    # Pure capacity evidence injection: never consumes real disk to test exhaustion.
    Assert-DistributionCapacity @{Volume='W';Free=1536MB} @{Volume='O';Free=256MB}; $checks++
    Assert-DistributionCapacity @{Volume='W';Free=1792MB} @{Volume='W';Free=1792MB}; $checks++
    Expect-Rejection { Assert-DistributionCapacity @{Volume='W';Free=(1536MB-1)} @{Volume='O';Free=256MB} }
    Expect-Rejection { Assert-DistributionCapacity @{Volume='W';Free=1536MB} @{Volume='O';Free=(256MB-1)} }
    Expect-Rejection { Assert-DistributionCapacity @{Volume='W';Free=(1792MB-1)} @{Volume='W';Free=1792MB} }
    Expect-Rejection { Assert-DistributionCapacity $null @{Volume='O';Free=256MB} }
    Expect-Rejection { Assert-DistributionCapacity @{Volume='W'} @{Volume='O';Free=256MB} }
    foreach ($unsafe in @('relative', '\\server\share\scratch', [IO.Path]::GetPathRoot($repo), $repo, (Join-Path $repo 'src/scratch'), $env:SystemRoot)) {
        Expect-Rejection { Resolve-DistributionWorkRoot $unsafe }
    }
    $packageRoot = Join-Path $fixture.Path 'package'
    [void][IO.Directory]::CreateDirectory($packageRoot)
    '{}' | Set-Content -LiteralPath (Join-Path $packageRoot 'distribution-manifest.json')
    Expect-Rejection { Resolve-DistributionWorkRoot (Join-Path $packageRoot 'nested') }
    Expect-Rejection { Remove-OwnedDistributionWork @{Root=$fixture.Root;Path=$repo;Token=$fixture.Token} }
    Expect-Rejection { Remove-OwnedDistributionWork @{Root=$fixture.Root;Path=$fixture.Path;Token='forged'} }
    $workRoot = Join-Path $fixture.Path 'work'
    $output = Join-Path $fixture.Path 'durable'
    [void][IO.Directory]::CreateDirectory($output)
    'old durable artifact' | Set-Content -LiteralPath (Join-Path $output 'previous.zip')
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $output 'previous.zip')).Hash
    # Same lifecycle and final promotion used by the canonical publisher, with small deterministic payloads.
    foreach ($failure in @('none','publish','signing','manifest','smoke','zip')) {
        $pipelineAction = { param($workPath)
            $stage = Join-Path $workPath 'staging/ArbitrageTrading'
            [void][IO.Directory]::CreateDirectory($stage)
            'payload' | Set-Content -LiteralPath (Join-Path $stage 'app.bin')
            foreach ($phase in @('publish','signing','manifest','zip')) { if ($failure -eq $phase) { throw "Injected $phase failure" } }
            $candidate = Join-Path $workPath 'candidate.zip'
            [IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $stage -Parent),$candidate)
            Invoke-DistributionWorkspace $workRoot -Action { param($smoke)
                [IO.Compression.ZipFile]::ExtractToDirectory($candidate,(Join-Path $smoke 'extracted'))
                if ($failure -eq 'smoke') { throw 'Injected smoke failure' }
            }
            Publish-DistributionArchive $candidate (Join-Path $output 'final.zip') | Out-Null
        }
        if ($failure -eq 'none') { Invoke-DistributionWorkspace $workRoot -Action $pipelineAction }
        else { Expect-Rejection { Invoke-DistributionWorkspace $workRoot -Action $pipelineAction } }
        Assert-Check (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -eq 0) "Scratch remains after $failure."
    }
    $zip = Join-Path $output 'final.zip'
    Assert-Check (Test-Path -LiteralPath $zip) 'Final ZIP missing.'
    Assert-Check ((Get-Content -LiteralPath ($zip+'.sha256')).StartsWith((Get-FileHash -LiteralPath $zip).Hash)) 'Final checksum mismatch.'
    Expect-Rejection { Publish-DistributionArchive $zip $zip }
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $output 'previous.zip')).Hash -eq $oldHash) 'Existing durable artifact changed.'
    Assert-Check (@(Get-ChildItem -LiteralPath $output -Force).Count -eq 3) 'Unexpected durable output.'
    # Retention works on success and failure; exact handles remain owned by this test process.
    foreach ($fail in @($false,$true)) {
        $script:retainedPath = $null
        $retainAction = { param($workPath)
            $script:retainedPath = $workPath
            'debug' | Set-Content -LiteralPath (Join-Path $workPath 'debug.txt')
            if ($fail) { throw 'debug failure' }
        }
        try {
            if ($fail) { Expect-Rejection { Invoke-DistributionWorkspace $workRoot -Action $retainAction -KeepWorkDirectory } }
            else { Invoke-DistributionWorkspace $workRoot -Action $retainAction -KeepWorkDirectory }
            Assert-Check (Test-Path -LiteralPath (Join-Path $script:retainedPath 'debug.txt')) 'Debug workspace was not retained.'
        } finally {
            if ($script:retainedPath) {
                $marker = Get-Content -LiteralPath (Join-Path $script:retainedPath '.d02-work-owner.json') -Raw | ConvertFrom-Json
                Remove-OwnedDistributionWork @{Root=$workRoot;Path=$script:retainedPath;Token=$marker.Token}
            }
        }
    }
    # A junction to unrelated data must never be traversed or recursively removed.
    $linkWork = New-DistributionWork $workRoot
    $link = Join-Path $linkWork.Path 'unsafe-link'
    try {
        New-Item -ItemType Junction -Path $link -Target $output | Out-Null
        Expect-Rejection { Resolve-DistributionWorkRoot (Join-Path $link 'nested') }
        Expect-Rejection { Remove-OwnedDistributionWork $linkWork }
        Assert-Check (Test-Path -LiteralPath $zip) 'Cleanup traversed junction.'
    } finally {
        # Remove only the exact junction itself, never its target.
        if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        Remove-OwnedDistributionWork $linkWork
    }
    $stale = New-DistributionWork $workRoot
    Clear-StaleDistributionWork $workRoot -Execute
    Assert-Check (Test-Path -LiteralPath $stale.Path) 'Active workspace was removed.'
    $markerPath = Join-Path $stale.Path '.d02-work-owner.json'
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $marker.ProcessId = [int]::MaxValue # Deterministic absent-process fixture, not a real disk/process failure.
    $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath
    Clear-StaleDistributionWork $workRoot
    Assert-Check (Test-Path -LiteralPath $stale.Path) 'Dry-run removed workspace.'
    Clear-StaleDistributionWork $workRoot -Execute
    Assert-Check (-not (Test-Path -LiteralPath $stale.Path)) 'Explicit stale cleanup failed.'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $output 'previous.zip')).Hash -eq $oldHash) 'Stale cleanup touched durable output.'
    Write-Output "Distribution workspace checks passed: $checks. Capacity boundaries, path/ownership/reparse refusal, success/failure cleanup, retention, durable preservation and promotion."
} finally { Remove-OwnedDistributionWork $fixture }

