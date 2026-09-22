param([switch]$Live, [switch]$OrderBooks)
$ErrorActionPreference = 'Stop'
if (-not $Live) { throw 'Pass -Live for an opt-in, at-most-two-page production C# adapter sample per exchange.' }
$previous = [Environment]::GetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE')
$previousBooks = [Environment]::GetEnvironmentVariable('ARBITRAGE_LIVE_ORDERBOOK_SAMPLE')
try {
    [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE', $(if ($OrderBooks) { '0' } else { '1' }))
    [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_ORDERBOOK_SAMPLE', $(if ($OrderBooks) { '1' } else { '0' }))
    dotnet test (Join-Path $PSScriptRoot '..\tests\Arbitrage.Backend.IntegrationTests\Arbitrage.Backend.IntegrationTests.csproj') `
        -c Release --filter 'FullyQualifiedName~LiveMarketAdapterSample' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Adapter sample failed with exit code $LASTEXITCODE." }
}
finally {
    [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_MARKET_SAMPLE', $previous)
    [Environment]::SetEnvironmentVariable('ARBITRAGE_LIVE_ORDERBOOK_SAMPLE', $previousBooks)
}
