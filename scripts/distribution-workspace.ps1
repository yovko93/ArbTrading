#requires -Version 7.5
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Get-Variable DistributionOwnedWork -Scope Script -ErrorAction SilentlyContinue)) {
    $script:DistributionOwnedWork = @{}
}

function Assert-DistributionNoLinks([string]$Path, [switch]$Tree) {
    for ($cursor = [IO.DirectoryInfo]::new($Path); $null -ne $cursor; $cursor = $cursor.Parent) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Distribution workspace traverses a reparse point.' }
    }
    if ($Tree -and (Test-Path -LiteralPath $Path)) {
        $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Path)
        while ($pending.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Distribution workspace contains a reparse point; cleanup refused.' }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
    }
}

function Resolve-DistributionWorkRoot([string]$Root) {
    $repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $repo 'artifacts/.work' }
    if (-not [IO.Path]::IsPathFullyQualified($Root) -or $Root -notmatch '^[A-Za-z]:[\\/]' -or $Root.Substring(2).Contains(':')) { throw 'WorkRoot must be an absolute local drive directory; UNC/device paths are unsupported.' }
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\') -or $full -eq $repo -or
        ($full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $full.StartsWith($repo + '\artifacts\', [StringComparison]::OrdinalIgnoreCase))) { throw 'WorkRoot cannot be a drive root, repository root or source directory.' }
    foreach ($system in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($system -and ($full -eq $system -or $full.StartsWith($system.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'WorkRoot cannot be a system directory.' }
    }
    Assert-DistributionNoLinks $full
    for ($cursor = [IO.DirectoryInfo]::new($full); $null -ne $cursor; $cursor = $cursor.Parent) {
        if (Test-Path -LiteralPath (Join-Path $cursor.FullName 'distribution-manifest.json')) { throw 'WorkRoot cannot be inside an active distribution package.' }
    }
    if ((Test-Path -LiteralPath (Join-Path $full 'ArbitrageTrading')) -or (Test-Path -LiteralPath (Join-Path $full 'Arbitrage.Desktop.exe'))) { throw 'WorkRoot contains active package contents.' }
    return $full
}

function Get-DistributionCapacity([string]$Path) {
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path))
    if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Distribution volume capacity unavailable or volume is not a fixed local disk.' }
    return @{ Volume=$drive.Name.ToUpperInvariant(); Free=[long]$drive.AvailableFreeSpace }
}

function Assert-DistributionCapacity($Work, $Output) {
    # 270MB stage + 120MB archive + 270MB extraction + test copies/data + safety margin.
    $scratchRequired = 1536MB; $outputRequired = 256MB
    foreach ($capacity in @($Work,$Output)) {
        if ($null -eq $capacity -or -not $capacity.ContainsKey('Volume') -or -not $capacity.Volume -or -not $capacity.ContainsKey('Free') -or $null -eq $capacity.Free -or $capacity.Free -lt 0) { throw 'Distribution volume capacity unavailable; publishing refused.' }
    }
    $checks = if ($Work.Volume -eq $Output.Volume) {
        @(@{ Required=($scratchRequired+$outputRequired); Free=[Math]::Min($Work.Free,$Output.Free); Kind='workspace/output' })
    } else { @(@{ Required=$scratchRequired; Free=$Work.Free; Kind='workspace' }, @{ Required=$outputRequired; Free=$Output.Free; Kind='output' }) }
    foreach ($check in $checks) {
        if ($check.Free -lt $check.Required) { throw ('Distribution {0} has insufficient free space. Required: {1:N0} MiB; Available: {2:N0} MiB. Use -WorkRoot on a volume with sufficient space; ensure output capacity too.' -f $check.Kind,($check.Required/1MB),($check.Free/1MB)) }
    }
}

function New-DistributionWork([string]$Root) {
    $rootPath = Resolve-DistributionWorkRoot $Root
    [void][IO.Directory]::CreateDirectory($rootPath)
    $path = Join-Path $rootPath ('d02-work-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    $token = [Guid]::NewGuid().ToString('N')
    $script:DistributionOwnedWork[$path] = $token
    try {
        @{ Schema=1; Name=[IO.Path]::GetFileName($path); Token=$token; ProcessId=$PID; CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $path '.d02-work-owner.json')
    } catch { Remove-OwnedDistributionWork @{Root=$rootPath;Path=$path;Token=$token}; throw }
    return @{ Root=$rootPath; Path=$path; Token=$token }
}

function Remove-OwnedDistributionWork($Work, [switch]$KeepWorkDirectory) {
    $rootPath = Resolve-DistributionWorkRoot $Work.Root
    $path = [IO.Path]::GetFullPath($Work.Path)
    if (-not [IO.Path]::IsPathFullyQualified($Work.Path) -or [IO.Path]::GetDirectoryName($path) -ne $rootPath -or
        [IO.Path]::GetFileName($path) -notmatch '^d02-work-[0-9a-f]{32}$' -or
        -not $script:DistributionOwnedWork.ContainsKey($path) -or $script:DistributionOwnedWork[$path] -ne $Work.Token) { throw 'Cleanup refused: workspace is not owned by this invocation.' }
    Assert-DistributionNoLinks $path -Tree
    if ($KeepWorkDirectory) { Write-Host "Retained distribution workspace: $path"; return }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    $script:DistributionOwnedWork.Remove($path)
}

function Invoke-DistributionWorkspace([string]$Root, [scriptblock]$Action, [switch]$KeepWorkDirectory) {
    $work = New-DistributionWork $Root
    try { & $Action $work.Path }
    finally { Remove-OwnedDistributionWork $work -KeepWorkDirectory:$KeepWorkDirectory }
}

function Clear-StaleDistributionWork([string]$Root, [switch]$Execute) {
    $rootPath = Resolve-DistributionWorkRoot $Root
    if (-not (Test-Path -LiteralPath $rootPath)) { return }
    foreach ($directory in Get-ChildItem -LiteralPath $rootPath -Directory -Force) {
        if ($directory.Name -notmatch '^d02-work-[0-9a-f]{32}$') { continue }
        Assert-DistributionNoLinks $directory.FullName -Tree
        $markerPath = Join-Path $directory.FullName '.d02-work-owner.json'
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'Unmarked workspace refused; legacy staging is never automatically deleted.' }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.Schema -ne 1 -or $marker.Name -ne $directory.Name -or $marker.Token -notmatch '^[0-9a-f]{32}$' -or $marker.ProcessId -le 0) { throw 'Invalid workspace ownership marker.' }
        if (Get-Process -Id $marker.ProcessId -ErrorAction SilentlyContinue) { Write-Host "Skipping workspace whose owner process still exists: $($directory.FullName)"; continue }
        Write-Host "Stale distribution workspace: $($directory.FullName)"
        if ($Execute) {
            # Only this explicit recovery command adopts marked work left by an exited process.
            $script:DistributionOwnedWork[$directory.FullName] = $marker.Token
            Remove-OwnedDistributionWork @{Root=$rootPath;Path=$directory.FullName;Token=$marker.Token}
        }
    }
}

function Publish-DistributionArchive([string]$Candidate, [string]$Final) {
    $expected = (Get-FileHash -LiteralPath $Candidate -Algorithm SHA256).Hash
    $directory = Split-Path $Final -Parent
    Assert-DistributionNoLinks $directory
    [void][IO.Directory]::CreateDirectory($directory)
    if ((Test-Path -LiteralPath $Final) -or (Test-Path -LiteralPath ($Final + '.sha256'))) { throw 'Final artifact already exists; refusing to overwrite durable output.' }
    $temporary = Join-Path $directory ('.d02-promote-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $source = [IO.File]::OpenRead($Candidate)
        try {
            $destination = [IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $source.CopyTo($destination); $destination.Flush($true) } finally { $destination.Dispose() }
        } finally { $source.Dispose() }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expected) { throw 'Promotion copy hash differs.' }
        [IO.File]::Move($temporary,$Final) # Same-directory atomic rename, no overwrite, including cross-volume scratch.
        $actual = (Get-FileHash -LiteralPath $Final -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw 'Promoted ZIP hash differs.' }
        "$actual  $([IO.Path]::GetFileName($Final))" | Set-Content -LiteralPath ($Final + '.sha256') -Encoding ascii
        return $actual
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
