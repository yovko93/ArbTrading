param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore ArbitrageTrading.sln
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build ArbitrageTrading.sln -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet test ArbitrageTrading.sln -c $Configuration --no-build --no-restore --logger 'trx;LogFilePrefix=verification'
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }
