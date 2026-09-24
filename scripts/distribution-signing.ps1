#requires -Version 7.5
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Initialize-SignatureVerifier {
    if (-not ('Arbitrage.Distribution.AuthenticodeVerifier' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot '../src/Shared/AuthenticodeVerifier.cs')
    }
}
function Get-DistributionSigningFiles([string]$Root) {
    # Explicit project allowlist: never re-sign Microsoft/runtime/third-party DLLs.
    Initialize-SignatureVerifier
    $names = [Arbitrage.Distribution.AuthenticodeVerifier]::SigningFiles
    foreach ($name in $names) {
        $path = Assert-PackagePath $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'A required project signing target is missing.' }
        $cursor = Get-Item -LiteralPath $path
        while ($null -ne $cursor) {
            if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Signing target traverses a reparse point.' }
            $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
        }
        $name
    }
}
function Find-DistributionSignTool([string]$PackageRoot) {
    $candidate = $env:SIGNTOOL_PATH
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $candidate = Get-ChildItem -LiteralPath $kit -Directory -ErrorAction SilentlyContinue |
            Where-Object Name -Match '^10\.0\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($candidate) -or -not [IO.Path]::IsPathFullyQualified($candidate) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or [IO.Path]::GetFileName($candidate) -ne 'signtool.exe') { throw 'Microsoft Windows SDK SignTool is unavailable; supply a trusted absolute SIGNTOOL_PATH.' }
    $resolved = [IO.Path]::GetFullPath($candidate)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($PackageRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'SignTool cannot come from distribution staging.' }
    Initialize-SignatureVerifier
    $signature = [Arbitrage.Distribution.AuthenticodeVerifier]::Inspect($resolved)
    if ($signature.State -ne 'Valid' -or $signature.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') { throw 'SignTool must have a valid Microsoft signature.' }
    return $resolved
}
function Assert-TimestampUrl([string]$Value) {
    $uri = $null
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 2048 -or -not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -ne '' -or $uri.Fragment -ne '') { throw 'Production signing requires an absolute HTTPS RFC3161 timestamp URL without credentials or fragment.' }
}
function Get-ProductionSigningConfiguration([string]$Root) {
    if ($env:CERTIFICATE_THUMBPRINT -notmatch '^[0-9a-fA-F]{40}$') { throw 'Production signing requires a certificate thumbprint in CurrentUser/My.' }
    Assert-TimestampUrl $env:SIGNING_TIMESTAMP_URL
    $tool = Find-DistributionSignTool $Root
    $certificate = Get-Item -LiteralPath ('Cert:/CurrentUser/My/' + $env:CERTIFICATE_THUMBPRINT) -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le [DateTime]::Now -or $certificate.NotBefore -gt [DateTime]::Now -or
        @($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) { throw 'Configured code-signing certificate/private key is unavailable or not current.' }
    if ($env:EXPECTED_PUBLISHER -and $certificate.Subject -ne $env:EXPECTED_PUBLISHER) { throw 'Configured publisher does not match certificate identity.' }
    return @{ Tool=$tool; Thumbprint=$certificate.Thumbprint; Subject=$certificate.Subject; Timestamp=$env:SIGNING_TIMESTAMP_URL }
}
function Assert-DistributionSignature([string]$Path, [string]$Mode, [string]$Thumbprint, [string]$Subject, [bool]$RequireTimestamp) {
    Initialize-SignatureVerifier
    $evidence = [Arbitrage.Distribution.AuthenticodeVerifier]::Inspect($Path)
    if (-not [Arbitrage.Distribution.AuthenticodeVerifier]::MeetsPolicy($evidence, $Mode, $Thumbprint, $Subject, $RequireTimestamp)) { throw 'Signature verification failed (content, trust, signer, SHA256 or timestamp policy).' }
    return $evidence
}
function Invoke-DistributionSigning([string]$Root, [ValidateSet('Unsigned','Authenticode','TestEphemeral')][string]$Mode) {
    $targets = @(Get-DistributionSigningFiles $Root)
    $certificate = $null; $keyPath = $null; $thumbprint = $null
    try {
        if ($Mode -eq 'Unsigned') {
            foreach ($relative in $targets) { Assert-DistributionSignature (Join-Path $Root $relative) 'Unsigned' '' '' $false | Out-Null }
            return [ordered]@{Mode='Unsigned';Status='UnsignedSnapshot';PublisherSubject=$null;CertificateThumbprint=$null;SignatureAlgorithm=$null;Timestamped=$false;RequireTimestamp=$false;Files=$targets}
        }
        if ($Mode -eq 'Authenticode') { $configuration = Get-ProductionSigningConfiguration $Root; $thumbprint=$configuration.Thumbprint; $subject=$configuration.Subject }
        else {
            $subject = 'CN=ArbitrageTrading D02 TEST ONLY ' + [Guid]::NewGuid().ToString('N')
            $certificate = New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:/CurrentUser/My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter ([DateTime]::Now.AddHours(8))
            $thumbprint = $certificate.Thumbprint
            $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            try { $keyPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Microsoft/Crypto/Keys/' + $key.Key.UniqueName) } finally { $key.Dispose() }
            if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { throw 'Temporary key location could not be verified for cleanup.' }
        }
        foreach ($relative in $targets) {
            $path = Join-Path $Root $relative
            $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            if ($Mode -eq 'Authenticode') {
                # Certificate-store selection avoids passwords/PFX/private-key paths in process arguments.
                $null = & $configuration.Tool sign /s My /sha1 $thumbprint /fd SHA256 /tr $configuration.Timestamp /td SHA256 $path 2>&1
                if ($LASTEXITCODE) { throw 'Production SignTool signing failed; no unsigned fallback.' }
                $null = & $configuration.Tool verify /pa /all $path 2>&1
                if ($LASTEXITCODE) { throw 'Production SignTool verification failed.' }
            } else {
                $null = Set-AuthenticodeSignature -LiteralPath $path -Certificate $certificate -HashAlgorithm SHA256
            }
            Assert-DistributionSignature $path $Mode $thumbprint $subject ($Mode -eq 'Authenticode') | Out-Null
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $before) { throw 'Signing did not change final file bytes.' }
        }
        return [ordered]@{Mode=$Mode;Status=$(if($Mode -eq 'Authenticode'){'SignedSnapshot'}else{'TestSignatureOnly'});PublisherSubject=$subject;CertificateThumbprint=$thumbprint;SignatureAlgorithm='SHA256';Timestamped=($Mode -eq 'Authenticode');RequireTimestamp=($Mode -eq 'Authenticode');Files=$targets}
    } catch { throw 'Distribution signing failed. Check explicit mode, tool, certificate identity and timestamp policy. No fallback package was produced.' }
    finally {
        if ($null -ne $certificate) {
            $certificate.Dispose()
            Remove-Item -LiteralPath ('Cert:/CurrentUser/My/' + $thumbprint) -DeleteKey -Force
            if ((Test-Path -LiteralPath ('Cert:/CurrentUser/My/' + $thumbprint)) -or ($keyPath -and (Test-Path -LiteralPath $keyPath))) { throw 'Ephemeral signing identity cleanup failed.' }
            Write-Information 'Ephemeral certificate and private key cleanup verified.' -InformationAction Continue
        }
    }
}
function Test-DistributionSignatures([string]$Root, $Manifest) {
    if ($Manifest.SchemaVersion -eq 1) { return }
    if ($Manifest.SchemaVersion -ne 2 -or $null -eq $Manifest.Signing) { throw 'Missing schema-2 signing policy.' }
    $s = $Manifest.Signing
    if ($s.Mode -notin @('Unsigned','Authenticode','TestEphemeral') -or $Manifest.SigningRequired -ne ($s.Mode -ne 'Unsigned') -or $s.RequireTimestamp -ne ($s.Mode -eq 'Authenticode')) { throw 'Inconsistent signing policy.' }
    $targets = @(Get-DistributionSigningFiles $Root)
    if (@(Compare-Object $targets @($s.Files)).Count) { throw 'Signing target inventory mismatch.' }
    foreach ($relative in $targets) { Assert-DistributionSignature (Join-Path $Root $relative) $s.Mode $s.CertificateThumbprint $s.PublisherSubject $s.RequireTimestamp | Out-Null }
}
