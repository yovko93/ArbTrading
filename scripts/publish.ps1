#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Release')][string]$Configuration = 'Release', [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = 'artifacts/distribution', [switch]$AllowDirty, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'distribution-common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'D01 publishing requires Windows x64 and PowerShell 7.' }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Git source identity unavailable.' }
    $dirty = -not [string]::IsNullOrWhiteSpace((git status --porcelain | Out-String))
    if ($LASTEXITCODE) { throw 'Git working-tree inspection failed.' }
    if ($dirty -and -not $AllowDirty) { throw 'Official snapshots require a clean working tree. Use -AllowDirty only for a labeled local verification package.' }
    if ($SkipTests -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:DISTRIBUTION_VERIFIED_SHA -ne $commit -or $dirty)) { throw '-SkipTests is restricted to CI after successful verification of this exact clean commit in the same job.' }
    $output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\') + '\'
    if (-not $output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be beneath this repository artifacts directory.' }
    for ($cursor = [IO.DirectoryInfo]::new($output); $null -ne $cursor; $cursor = $cursor.Parent) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Distribution output may not contain reparse points.' }
    }
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'verify.ps1') -Configuration $Configuration }
    $stage = Join-Path $output ('staging-' + [Guid]::NewGuid().ToString('N'))
    $package = Join-Path $stage 'ArbitrageTrading'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    $identity = '1.0.0+' + $commit + $(if ($dirty) { '.dirty' } else { '' })
    foreach ($app in @('Desktop','Backend')) {
        $destination = if ($app -eq 'Desktop') { $package } else { Join-Path $package 'backend' }
        dotnet publish "src/Arbitrage.$app/Arbitrage.$app.csproj" -c $Configuration -r $Runtime --self-contained true -o $destination -p:PublishTrimmed=false -p:PublishAot=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false "-p:InformationalVersion=$identity" "-p:SourceRevisionId=$commit"
        if ($LASTEXITCODE) { throw "$app publish failed." }
    }
    # Only remove known development-only files within this newly created staging tree.
    Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -eq 'appsettings.Development.json' -or $_.Name -eq 'web.config' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution/README-FIRST-RUN.txt') -Destination $package
    $inventory = [ordered]@{}
    Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object { $inventory[[IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $manifest = [ordered]@{ SchemaVersion=1; Product='ArbitrageTrading'; RuntimeIdentifier=$Runtime; TargetFrameworkDesktop='net10.0-windows'; TargetFrameworkBackend='net10.0'; SourceCommit=$commit; SourceDirty=$dirty; BuildUtc=[DateTimeOffset]::UtcNow.ToString('O'); DesktopRelativePath='Arbitrage.Desktop.exe'; BackendRelativePath='backend/Arbitrage.Backend.exe'; BackendConfigRelativePath='backend/appsettings.json'; DesktopSha256=$inventory['Arbitrage.Desktop.exe']; BackendSha256=$inventory['backend/Arbitrage.Backend.exe']; BackendConfigSha256=$inventory['backend/appsettings.json']; PackageMode='PortableZip'; ExpectedTradingMode='Paper'; Files=$inventory }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'distribution-manifest.json') -Encoding utf8NoBOM
    Test-DistributionPackage $package | Out-Null
    $name = "ArbitrageTrading-win-x64-g$($commit.Substring(0,8))" + $(if ($dirty) { '-local-dirty' } else { '' })
    $zip = Join-Path $output ($name + '.zip')
    if (Test-Path -LiteralPath $zip) { throw 'Output ZIP already exists; choose a fresh repo-local artifacts output directory.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    & (Join-Path $PSScriptRoot 'verify-distribution.ps1') -PackageZip $zip
    if ($LASTEXITCODE) { throw 'Distribution smoke failed.' }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    Write-Output "Verified package: $zip"
    Write-Output "ZIP SHA-256: $hash"
    Write-Output "ZIP bytes: $((Get-Item -LiteralPath $zip).Length); package bytes: $((Get-ChildItem -LiteralPath $package -Recurse -File | Measure-Object Length -Sum).Sum); files: $($inventory.Count + 1)"
} finally { Pop-Location }
