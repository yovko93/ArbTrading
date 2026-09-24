#requires -Version 7.5
param([Parameter(Mandatory)][string]$PackageRoot)
. (Join-Path $PSScriptRoot 'distribution-common.ps1')
$manifest = Test-DistributionPackage ([IO.Path]::GetFullPath($PackageRoot))
foreach ($name in (Get-DistributionSigningFiles $PackageRoot)) {
    $signature = [Arbitrage.Distribution.AuthenticodeVerifier]::Inspect((Join-Path $PackageRoot $name))
    [pscustomobject]@{File=$name;State=$signature.State;Publisher=$signature.Subject;Thumbprint=$signature.Thumbprint;Algorithm=$signature.Algorithm;Timestamped=$signature.Timestamped}
}
