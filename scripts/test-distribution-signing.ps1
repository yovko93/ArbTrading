#requires -Version 7.5
param()
. (Join-Path $PSScriptRoot 'distribution-common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([IO.Path]::GetTempPath()) ('Arbitrage D02 Signing Test ' + [Guid]::NewGuid().ToString('N'))
function Expect-Failure([scriptblock]$Operation) {
    $failed = $false
    try { & $Operation | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected signing rejection did not occur.' }
}
try {
    Initialize-SignatureVerifier
    try { Find-DistributionSignTool $root | Out-Null; Write-Output 'Microsoft SDK SignTool discovery: available and verified.' } catch { Write-Output 'Microsoft SDK SignTool discovery: unavailable or unverifiable; test provider is independent of SDK.' }
    foreach ($name in [Arbitrage.Distribution.AuthenticodeVerifier]::SigningFiles) {
        $project = if ($name.StartsWith('backend/')) { 'Backend' } else { 'Desktop' }
        $framework = if ($project -eq 'Desktop') { 'net10.0-windows' } else { 'net10.0' }
        $source = Join-Path $repo ('src/Arbitrage.' + $project + '/bin/Release/' + $framework + '/' + [IO.Path]::GetFileName($name))
        $destination = Join-Path $root $name
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    $desktop = Join-Path $root 'Arbitrage.Desktop.exe'
    $backend = Join-Path $root 'backend/Arbitrage.Backend.exe'
    $original = [IO.File]::ReadAllBytes($backend)
    $unsigned = Invoke-DistributionSigning $root Unsigned
    if ($unsigned.Status -ne 'UnsignedSnapshot') { throw 'Unsigned labeling failed.' }
    $spoof = @{ SchemaVersion=2; SigningRequired=$true; Signing=@{Mode='Authenticode';Status='Valid';RequireTimestamp=$true;Files=$unsigned.Files;CertificateThumbprint=('A'*40);PublisherSubject='CN=Claimed'} }
    Expect-Failure { Test-DistributionSignatures $root $spoof }
    $before = (Get-FileHash -LiteralPath $backend).Hash
    $signed = Invoke-DistributionSigning $root TestEphemeral
    $after = (Get-FileHash -LiteralPath $backend).Hash
    if ($before -eq $after) { throw 'Pre/post signing hashes are equal.' }
    $manifest = @{SchemaVersion=2;SigningRequired=$true;Signing=$signed;BackendSha256=$after}
    Test-DistributionSignatures $root $manifest
    if ($manifest.BackendSha256 -ne (Get-FileHash -LiteralPath $backend).Hash) { throw 'Final signed hash was not recorded.' }
    foreach ($path in @($desktop,$backend)) {
        $e = Assert-DistributionSignature $path TestEphemeral $signed.CertificateThumbprint $signed.PublisherSubject $false
        if ($e.State -ne 'Untrusted' -or -not $e.ContentValid -or $e.Timestamped) { throw 'Test trust semantics failed.' }
    }
    Expect-Failure { Assert-DistributionSignature $backend TestEphemeral ('B'*40) $signed.PublisherSubject $false }
    Expect-Failure { Assert-DistributionSignature $backend TestEphemeral $signed.CertificateThumbprint 'CN=Wrong' $false }
    Expect-Failure { Assert-DistributionSignature $backend TestEphemeral $signed.CertificateThumbprint $signed.PublisherSubject $true }
    Expect-Failure { Assert-DistributionSignature $backend Authenticode $signed.CertificateThumbprint $signed.PublisherSubject $false }
    foreach ($url in @('', 'http://example.test/timestamp','https://user:secret@example.test/timestamp','relative')) { Expect-Failure { Assert-TimestampUrl $url } }
    $savedThumbprint = $env:CERTIFICATE_THUMBPRINT
    try { $env:CERTIFICATE_THUMBPRINT = $null; Expect-Failure { Get-ProductionSigningConfiguration $root } } finally { $env:CERTIFICATE_THUMBPRINT = $savedThumbprint }
    Assert-TimestampUrl 'https://example.test/timestamp' # Syntax only; never contacted.
    $bytes = [IO.File]::ReadAllBytes($backend); $bytes[1024] = $bytes[1024] -bxor 1; [IO.File]::WriteAllBytes($backend,$bytes)
    if ([Arbitrage.Distribution.AuthenticodeVerifier]::Inspect($backend).ContentValid) { throw 'Tampered signed PE still passed native content validation.' }
    Expect-Failure { Test-DistributionSignatures $root $manifest }
    [IO.File]::WriteAllBytes($backend,$original)
    Expect-Failure { Test-DistributionSignatures $root $manifest }
    if (Test-Path -LiteralPath ('Cert:/CurrentUser/My/' + $signed.CertificateThumbprint)) { throw 'Test certificate remained installed.' }
    # Force signing failure after certificate creation. The signing helper must still delete its exact certificate/key.
    $certificatesBefore = @(Get-ChildItem Cert:/CurrentUser/My | Where-Object Subject -Like 'CN=ArbitrageTrading D02 TEST ONLY *' | ForEach-Object Thumbprint)
    $locked = [IO.File]::Open($desktop,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Expect-Failure { Invoke-DistributionSigning $root TestEphemeral } } finally { $locked.Dispose() }
    $certificatesAfter = @(Get-ChildItem Cert:/CurrentUser/My | Where-Object Subject -Like 'CN=ArbitrageTrading D02 TEST ONLY *' | ForEach-Object Thumbprint)
    if (@(Compare-Object (@('baseline')+$certificatesBefore) (@('baseline')+$certificatesAfter)).Count) { throw 'Failed-signing certificate cleanup mismatch.' }
    $tracked = @(git -C $repo ls-files '*.pfx' '*.p12' '*.pvk' '*.key' '*.pem')
    if ($tracked.Count) { throw 'Signing-key-looking files are tracked.' }
    Write-Output 'Focused signing tests passed: unsigned, structured/untrusted test signatures, changed/post-sign hashes, wrong identity, missing timestamp, spoof, missing signature, native tamper rejection, successful/failed cleanup, no tracked private keys.'
    [pscustomobject]$signed | Select-Object Mode,PublisherSubject,CertificateThumbprint,Timestamped | ConvertTo-Json
} finally {
    $absolute = [IO.Path]::GetFullPath($root)
    if (-not $absolute.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($absolute)).StartsWith('Arbitrage D02 Signing Test ')) { throw 'Invalid signing test cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

