param([switch]$Live)
$ErrorActionPreference = 'Stop'
if (-not $Live) { throw 'Pass -Live to perform two explicit public, read-only GET requests.' }
$requests = @(
    @{ Exchange = 'Polymarket'; Url = 'https://gamma-api.polymarket.com/markets/keyset?closed=false&limit=5' },
    @{ Exchange = 'Kalshi'; Url = 'https://external-api.kalshi.com/trade-api/v2/markets?status=open&limit=5' }
)
foreach ($request in $requests) {
    $result = [ordered]@{
        ObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Exchange = $request.Exchange
        Endpoint = $request.Url
        Requests = 1
        RecordsReceived = 0
        Classification = 'Unverified'
    }
    try {
        $response = Invoke-WebRequest -Uri $request.Url -Method Get -TimeoutSec 12 -MaximumRedirection 0
        $body = $response.Content | ConvertFrom-Json
        if ($null -eq $body.markets) { $result.Classification = 'InvalidEnvelope' }
        else {
            $result.RecordsReceived = @($body.markets).Count
            $result.Classification = 'SampledPublicReadSucceeded'
        }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -ne $response) {
            $result.Classification = 'Http' + [int]$response.StatusCode
        } else { $result.Classification = 'TransportOrTimeoutFailure' }
    }
    [pscustomobject]$result | ConvertTo-Json -Compress
}
