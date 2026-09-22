param([switch]$Live)
$ErrorActionPreference = 'Stop'
if (-not $Live) { throw 'Pass -Live for an opt-in, at-most-two-page production C# adapter sample per exchange.' }
$previous = [Environment]::GetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE')
try {
    [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE', '1')
    dotnet test (Join-Path $PSScriptRoot '..\tests\Arbitrage.Backend.IntegrationTests\Arbitrage.Backend.IntegrationTests.csproj') `
        -c Release --filter 'FullyQualifiedName~LiveMarketAdapterSample' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Adapter sample failed with exit code $LASTEXITCODE." }
}
finally { [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE', $previous) }
