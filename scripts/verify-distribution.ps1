#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageZip)
. (Join-Path $PSScriptRoot 'distribution-common.ps1')
if (-not $IsWindows) { throw 'Distribution smoke requires Windows x64.' }
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempParent ('Arbitrage Trading Portable Test ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$extracted = Join-Path $testRoot 'extracted'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
# Inspect the hidden WPF shell window; Process.MainWindowHandle ignores hidden windows.
if (-not ('PortableSmokeWindows' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PortableSmokeWindows {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int length);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int length);
    public static bool HasShell(int processId) {
        bool found = false;
        EnumWindows((window, state) => {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id != processId) return true;
            var title = new StringBuilder(256); var type = new StringBuilder(256);
            GetWindowText(window, title, 256); GetClassName(window, type, 256);
            if (title.ToString() == "Arbitrage Trading" && type.ToString().StartsWith("HwndWrapper")) found = true;
            return !found;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}
$data = Join-Path $testRoot 'private-data'
$runtime = Join-Path $testRoot 'private-runtime'
$desktopData = Join-Path $testRoot 'private-desktop'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$url = "http://127.0.0.1:$port"

function Start-Isolated([string]$Exe, [string[]]$Arguments = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.WorkingDirectory = Split-Path $Exe -Parent
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(Local__|ARBITRAGE_|ASPNETCORE_|DOTNET_|Kestrel__)' -or $key -in @('urls','http_ports','https_ports')) { [void]$start.Environment.Remove($key) }
    }
    $start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-installed-runtime'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    $start.Environment['Local__DataDirectory'] = $data
    $start.Environment['Local__RuntimeDirectory'] = $runtime
    $start.Environment['ARBITRAGE_DESKTOP_DIRECTORY'] = $desktopData
    $start.Environment['Local__BaseUrl'] = $url
    $start.Environment['Local__DeploymentMode'] = 'Local'
    $start.Environment['Local__TradingMode'] = 'Paper'
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $owned.Add($process)
    # Drain output without exposing runtime diagnostics or credentials.
    $null = $process.StandardOutput.ReadToEndAsync(); $null = $process.StandardError.ReadToEndAsync()
    return $process
}

function Wait-Success([Diagnostics.Process]$Process, [int]$Expected = 0) {
    if (-not $Process.WaitForExit(120000)) { throw 'Test-owned process timed out.' }
    if ($Process.ExitCode -ne $Expected) { throw "Test-owned process exited $($Process.ExitCode); expected $Expected." }
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($PackageZip), $extracted)
    $package = Join-Path $extracted 'ArbitrageTrading'
    Test-DistributionPackage $package | Out-Null
    $desktopExe = Join-Path $package 'Arbitrage.Desktop.exe'
    $backendExe = Join-Path $package 'backend/Arbitrage.Backend.exe'
    Wait-Success (Start-Isolated $desktopExe @('--validate-package'))
    $desktop = Start-Isolated $desktopExe
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not $desktop.HasExited -and -not [PortableSmokeWindows]::HasShell($desktop.Id) -and [DateTime]::UtcNow -lt $windowDeadline) { Start-Sleep -Milliseconds 200 }
    $windowObserved = -not $desktop.HasExited -and [PortableSmokeWindows]::HasShell($desktop.Id)
    if (-not $windowObserved) { throw 'Packaged Desktop did not create its WPF shell window.' }
    if ((Test-Path -LiteralPath (Join-Path $runtime 'connection.json')) -or (Test-Path -LiteralPath (Join-Path $data 'arbitrage.db'))) { throw 'Desktop automatically launched backend or migration.' }
    [void]$desktop.CloseMainWindow()
    if (-not $desktop.WaitForExit(10000)) { $desktop.Kill($true); $desktop.WaitForExit() }
    $outsideData = $data
    $data = Join-Path $package 'forbidden-user-data'
    Wait-Success (Start-Isolated $backendExe @('--migrate')) 1
    if (Test-Path -LiteralPath $data) { throw 'Backend wrote package-local user data.' }
    $data = $outsideData
    foreach ($attempt in 1..2) { Wait-Success (Start-Isolated $backendExe @('--migrate')) }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'arbitrage.db'))) { throw 'Migration did not create isolated SQLite database.' }
    if (Test-Path -LiteralPath (Join-Path $data 'backups/schema')) { throw 'New/current database should not create migration backups.' }
    $firstSession = $null
    foreach ($attempt in 1..2) {
        $backend = Start-Isolated $backendExe
        $deadline = [DateTime]::UtcNow.AddSeconds(45); $ready = $false
        while ([DateTime]::UtcNow -lt $deadline -and -not $backend.HasExited) {
            try { $health = Invoke-RestMethod -Uri ($url + '/health/live') -TimeoutSec 2 -NoProxy; if ($health.status -eq 'Live') { $ready = $true; break } } catch { }
            Start-Sleep -Milliseconds 200
        }
        if (-not $ready) { throw 'Self-contained backend readiness failed.' }
        $connection = Get-Content -LiteralPath (Join-Path $runtime 'connection.json') -Raw | ConvertFrom-Json
        $headers = @{ Authorization = 'Bearer ' + $connection.Credential }
        $session = Invoke-RestMethod -Uri ($url + '/api/v1/session') -Headers $headers -NoProxy
        if ($null -eq $firstSession) { $firstSession = $session }
        elseif ($session.userId -ne $firstSession.userId -or $session.defaultWorkspaceId -ne $firstSession.defaultWorkspaceId) { throw 'Local identity changed after restart.' }
        if (-not $session.capabilities.paperExecutionImplemented -or $session.capabilities.liveOrderSubmissionAvailable -or $session.capabilities.manualLiveExecutionAvailable -or $session.capabilities.automaticLiveExecutionAvailable) { throw 'Packaged safety capabilities differ.' }
        $snapshot = Invoke-RestMethod -Uri ($url + '/api/v1/workspaces/' + $session.defaultWorkspaceId + '/snapshot') -Headers $headers -NoProxy
        if ($snapshot.system.effectiveTradingMode -ne 'Paper') { throw 'Packaged environment is not Paper.' }
        $account = Invoke-RestMethod -Uri ($url + '/api/v1/workspaces/' + $session.defaultWorkspaceId + '/paper/account') -Headers $headers -NoProxy
        if ($null -ne $account.generation) { throw 'First-run bootstrap initialized paper money.' }
        $paperUrl = $url + '/api/v1/workspaces/' + $session.defaultWorkspaceId + '/paper'
        $risk = Invoke-RestMethod -Uri ($paperUrl + '/admission-policy') -Headers $headers -NoProxy
        if ($null -ne $risk -and "$risk" -ne '') { throw 'First run saved a risk policy.' }
        $automation = Invoke-RestMethod -Uri ($paperUrl + '/automation/status') -Headers $headers -NoProxy
        if ($automation.state -ne 'NotConfigured') { throw 'First run configured or armed automation.' }
        $campaign = Invoke-RestMethod -Uri ($paperUrl + '/reliability/current') -Headers $headers -NoProxy
        if ($null -ne $campaign.campaign) { throw 'First run started a reliability campaign.' }
        $backend.Kill($true); $backend.WaitForExit()
        $headers.Clear(); $connection = $null
    }
    # Exact manifest inventory/hash comparison proves no package-root runtime writes.
    Test-DistributionPackage $package | Out-Null
    $originalExe = [IO.File]::ReadAllBytes($backendExe)
    [IO.File]::AppendAllText($backendExe, 'integrity-test')
    Wait-Success (Start-Isolated $desktopExe @('--validate-package')) 2
    [IO.File]::WriteAllBytes($backendExe, $originalExe)
    $hiddenExe = Join-Path $testRoot 'saved-backend.exe'
    Move-Item -LiteralPath $backendExe -Destination $hiddenExe
    Wait-Success (Start-Isolated $desktopExe @('--validate-package')) 2
    Move-Item -LiteralPath $hiddenExe -Destination $backendExe
    $manifestPath = Join-Path $package 'distribution-manifest.json'
    $originalManifest = [IO.File]::ReadAllText($manifestPath)
    foreach ($unsafe in @('../outside.exe', 'C:/outside.exe')) {
        $bad = $originalManifest | ConvertFrom-Json
        $bad.BackendRelativePath = $unsafe
        $bad | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        Wait-Success (Start-Isolated $desktopExe @('--validate-package')) 2
    }
    [IO.File]::WriteAllText($manifestPath, $originalManifest)
    Test-DistributionPackage $package | Out-Null
    Write-Output "Distribution smoke passed outside repository, with spaces and no dotnet in child PATH. Desktop window observed: $windowObserved. Bootstrap, idempotence, restart identity, Paper safety, package immutability, tamper/missing/path rejection passed."
    $global:LASTEXITCODE = 0
} finally {
    foreach ($process in $owned) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() } } finally { $process.Dispose() }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('Arbitrage Trading Portable Test ')) { throw 'Unexpected smoke cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
