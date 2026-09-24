#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PackagePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or ($Relative -split '[/\\]' | Where-Object { $_ -in @('..', '.', '') })) { throw 'Unsafe manifest path.' }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes root.' }
    return $path
}

function Test-DistributionPackage([string]$Root) {
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'distribution-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.RuntimeIdentifier -ne 'win-x64' -or $manifest.PackageMode -ne 'PortableZip' -or $manifest.Product -ne 'ArbitrageTrading') { throw 'Unsupported distribution manifest.' }
    $all = @(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    foreach ($file in $all) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a link/reparse point.' }
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(?i)(^|/)(credentials|secrets|logs|reliability|runtime|backups)(/|$)|\.(db|sqlite|sqlite3)(-.*)?$|\.(pem|pfx|key|dpapi|log)$|(^|/)(connection\.json|managed-local\.json|preferences\.json|report\.json|appsettings\.(Development|Local)\.json|\.env)$') { throw "Runtime/secret file rejected: $relative" }
    }
    $files = @($all | Where-Object { -not $_.PSIsContainer -and $_.Name -ne 'distribution-manifest.json' })
    if ($files.Count -ne @($manifest.Files.PSObject.Properties).Count) { throw 'Package file inventory differs from manifest.' }
    foreach ($entry in $manifest.Files.PSObject.Properties) {
        $path = Assert-PackagePath $Root $entry.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) { throw "Package integrity failure: $($entry.Name)" }
    }
    foreach ($entry in @(@($manifest.DesktopRelativePath,$manifest.DesktopSha256), @($manifest.BackendRelativePath,$manifest.BackendSha256), @($manifest.BackendConfigRelativePath,$manifest.BackendConfigSha256))) {
        if ((Get-FileHash -LiteralPath (Assert-PackagePath $Root $entry[0]) -Algorithm SHA256).Hash -ne $entry[1]) { throw 'Critical hash mismatch.' }
    }
    foreach ($tree in @($Root, (Join-Path $Root 'backend'))) {
        foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $tree $runtimeFile))) { throw 'Self-contained runtime missing.' }
        }
    }
    foreach ($runtimeConfig in @('Arbitrage.Desktop.runtimeconfig.json','backend/Arbitrage.Backend.runtimeconfig.json')) {
        $runtime = (Get-Content -LiteralPath (Join-Path $Root $runtimeConfig) -Raw | ConvertFrom-Json).runtimeOptions
        if ($runtime.PSObject.Properties.Name -contains 'framework' -or $runtime.PSObject.Properties.Name -contains 'frameworks' -or -not ($runtime.PSObject.Properties.Name -contains 'includedFrameworks')) { throw 'Framework-dependent runtime configuration rejected.' }
    }
    $config = Get-Content -LiteralPath (Join-Path $Root $manifest.BackendConfigRelativePath) -Raw | ConvertFrom-Json
    if ($config.Local.DeploymentMode -ne 'Local' -or $config.Local.TradingMode -ne 'Paper' -or $config.Local.BaseUrl -ne 'http://127.0.0.1:5274' -or @($config.Local.PSObject.Properties).Count -ne 3) { throw 'Packaged configuration differs from safe non-secret defaults.' }
    return $manifest
}
