#Requires -Version 5.1
<#
.SYNOPSIS
Updates the current user's installed Agent Signaler apps from the latest GitHub Release.
.EXAMPLE
.\Update-AgentSignaler.ps1 -WhatIf
.EXAMPLE
.\Update-AgentSignaler.ps1 -Apps Remote
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Dashboard', 'Remote', 'RpcHost')]
    [string[]] $Apps = @('Dashboard', 'Remote', 'RpcHost'),
    [Parameter(DontShow = $true)]
    [string] $FixturePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = 'andysterland/agent-signaler'
$apiRoot = "https://api.github.com/repos/$repository"
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')

if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
    if (-not $WhatIfPreference) { throw 'Synthetic inventory is allowed only with -WhatIf; it cannot service applications.' }
    & (Join-Path $PSScriptRoot 'Test-UpdaterFixture.ps1') -FixturePath $FixturePath -Apps $Apps
    return
}

if (-not [Environment]::Is64BitOperatingSystem -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Agent Signaler requires Windows x64.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $installingUserSid = $identity.User.Value
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($identity.IsSystem -or $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from a non-elevated PowerShell as the Windows user who installed the apps.'
    }
}
finally { $identity.Dispose() }

Add-Type -AssemblyName System.Net.Http
$httpHandler = New-Object Net.Http.HttpClientHandler
$httpHandler.AllowAutoRedirect = $false
$httpClient = New-Object Net.Http.HttpClient($httpHandler)
$httpClient.Timeout = [TimeSpan]::FromMinutes(5)

function Get-GitHubCredentialToken {
    if (-not [string]::IsNullOrWhiteSpace($env:AGENT_SIGNALER_GITHUB_TOKEN)) {
        return $env:AGENT_SIGNALER_GITHUB_TOKEN
    }
    if ($null -eq (Get-Command git.exe -ErrorAction SilentlyContinue)) { return $null }
    $request = "protocol=https`nhost=github.com`nusername=andysterland`n`n"
    $lines = @($request | git credential fill 2>$null)
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        return $null
    }
    foreach ($line in $lines) {
        if ($line.StartsWith('password=', [StringComparison]::Ordinal)) {
            return $line.Substring('password='.Length)
        }
    }
    return $null
}

function Test-AllowedGitHubHost([uri] $Uri) {
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or -not $Uri.IsDefaultPort -or
        $Uri.UserInfo -ne '' -or $Uri.Fragment -ne '') {
        return $false
    }
    return $Uri.IdnHost -in @('api.github.com', 'github.com') -or
        $Uri.IdnHost.EndsWith('.githubusercontent.com', [StringComparison]::OrdinalIgnoreCase)
}

function Open-GitHubResponse([uri] $Uri, [string] $Token, [string] $Accept) {
    for ($redirect = 0; $redirect -le 5; $redirect++) {
        if (-not (Test-AllowedGitHubHost $Uri)) {
            throw "GitHub download redirected to an untrusted address: $Uri"
        }
        $request = New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Get, $Uri)
        try {
            [void]$request.Headers.UserAgent.ParseAdd('AgentSignaler-Updater/1.0')
            [void]$request.Headers.Accept.ParseAdd($Accept)
            if ($Uri.IdnHost -eq 'api.github.com' -and -not [string]::IsNullOrWhiteSpace($Token)) {
                $request.Headers.Authorization =
                    New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Token)
            }
            $response = $httpClient.SendAsync(
                $request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        }
        finally { $request.Dispose() }
        if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
            try {
                if ($null -eq $response.Headers.Location) { throw 'GitHub returned a redirect without a location.' }
                $Uri = New-Object uri($Uri, $response.Headers.Location)
            }
            finally { $response.Dispose() }
            continue
        }
        return $response
    }
    throw 'GitHub download exceeded the redirect limit.'
}

function Read-GitHubJsonResponse($Response) {
    try {
        if (-not $Response.IsSuccessStatusCode) {
            throw "GitHub request failed with HTTP $([int]$Response.StatusCode) $($Response.ReasonPhrase)."
        }
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
        try {
            $stream = $Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $memory = [IO.MemoryStream]::new()
            try {
                $buffer = New-Object byte[] 16384
                while (($read = $stream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    if ($memory.Length + $read -gt 4MB) { throw 'GitHub release metadata exceeds its bound.' }
                    $memory.Write($buffer, 0, $read)
                }
                $json = [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
                return $json | ConvertFrom-Json
            }
            finally { $memory.Dispose(); $stream.Dispose() }
        }
        finally { $deadline.Dispose() }
    }
    finally { $Response.Dispose() }
}

function Get-LatestGitHubRelease {
    $uri = "$apiRoot/releases?per_page=20"
    $token = $null
    $response = Open-GitHubResponse $uri $token 'application/vnd.github+json'
    if ([int]$response.StatusCode -in @(401, 403, 404)) {
        $response.Dispose()
        $token = Get-GitHubCredentialToken
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw 'The GitHub repository is not publicly accessible. Sign in with Git Credential Manager or set AGENT_SIGNALER_GITHUB_TOKEN for this process.'
        }
        $response = Open-GitHubResponse $uri $token 'application/vnd.github+json'
    }
    $releases = @(Read-GitHubJsonResponse $response)
    $release = $releases | Where-Object { -not $_.draft } |
        Sort-Object { [DateTimeOffset]$_.published_at } -Descending | Select-Object -First 1
    if ($null -eq $release) { throw 'No published Agent Signaler GitHub Release is available.' }
    if ($release.tag_name -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "The latest release tag is not a supported three-part version: $($release.tag_name)"
    }
    [pscustomobject]@{
        Release = $release
        Token = $token
        Version = ConvertTo-AgentSignalerVersion $release.tag_name.Substring(1)
    }
}

function Save-GitHubAsset($Asset, [string] $Token, [string] $Destination) {
    $response = Open-GitHubResponse $Asset.url $Token 'application/octet-stream'
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "Downloading $($Asset.name) failed with HTTP $([int]$response.StatusCode) $($response.ReasonPhrase)."
        }
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $file = New-Object IO.FileStream(
                $Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
                try {
                    $buffer = New-Object byte[] 65536
                    while (($read = $source.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                        if ($file.Length + $read -gt [long]$Asset.size) { throw 'Download exceeded its declared size.' }
                        $file.Write($buffer, 0, $read)
                    }
                }
                finally { $deadline.Dispose() }
            }
            finally { $file.Dispose() }
        }
        finally { $source.Dispose() }
    }
    finally { $response.Dispose() }
    if ((Get-Item -LiteralPath $Destination).Length -ne [long]$Asset.size) {
        throw "Downloaded size mismatch for $($Asset.name)."
    }
}

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-Package {
    param($Installer, [string] $Path, [string] $App)

    $database = $Installer.OpenDatabase($Path, 0)
    try {
        $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        try {
            [void]$view.Execute()
            $properties = @{}
            while ($null -ne ($record = $view.Fetch())) {
                try { $properties[$record.StringData(1)] = $record.StringData(2) }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        }
        finally {
            [void]$view.Close()
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
        $summary = $database.SummaryInformation(0)
        try {
            return Assert-AgentSignalerPackageMetadata $properties $summary.Property(7) ([int]$summary.Property(15)) $App
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
}

function Get-InstalledVersion {
    param($Installer, [string] $UpgradeCode)

    $products = $Installer.RelatedProducts($UpgradeCode)
    try {
        $versions = @(
            foreach ($code in $products) {
                # RelatedProducts can include machine-wide products; these MSIs are per-user.
                if ($Installer.ProductState($code) -eq 5 -and
                    $Installer.ProductInfo($code, 'AssignmentType') -eq '0') {
                    [version]$Installer.ProductInfo($code, 'VersionString')
                }
            }
        )
        if ($versions.Count -gt 1) {
            throw "Multiple installed products have UpgradeCode $UpgradeCode. Repair the installation before updating."
        }
        if ($versions.Count -eq 1) { $versions[0] }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($products) }
}

function Stop-OwnedRemoteClient {
    $directory = Join-Path $env:LOCALAPPDATA 'Programs\AgentSignaler\Remote'
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'AgentSignaler.Client.exe') -PathType Leaf)) {
        return # Legacy releases have no persistent client or servicing command.
    }
    $relay = Join-Path $directory 'AgentSignaler.Relay.exe'
    if (-not (Test-Path -LiteralPath $relay -PathType Leaf)) {
        throw 'The installed remote Client has no matching Relay helper. Repair the installation before updating.'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $relay
    $start.Arguments = '--stop-client-for-update'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(8000)) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop }
            throw 'Remote shutdown helper exceeded its deadline. Exit the owned Client and retry; no MSI has run.'
        }
        if ($process.ExitCode -ne 0) {
            throw "Unable to safely stop the current user's remote Client (helper exit $($process.ExitCode)). Exit it in its owning Windows session and retry."
        }
    }
    finally { $process.Dispose() }
}

function Assert-RpcHostProcessInventory([string] $Directory) {
    if ($null -eq ('AgentSignalerUpdater.FileIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace AgentSignalerUpdater {
    public static class FileIdentity {
        [StructLayout(LayoutKind.Sequential)]
        private struct Info {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
        public static bool Same(string a, string b) {
            using (var first = CreateFile(a, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            using (var second = CreateFile(b, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                Info x, y;
                if (first.IsInvalid || second.IsInvalid ||
                    !GetFileInformationByHandle(first, out x) || !GetFileInformationByHandle(second, out y))
                    return false;
                return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
            }
        }
    }
}
'@
    }
    $exe = Join-Path $Directory 'AgentSignaler.RpcHost.exe'
    $processes = @(
        foreach ($process in Get-Process) {
            try {
                $path = $null
                try { $path = $process.Path }
                catch [System.ComponentModel.Win32Exception] { }
                catch [System.InvalidOperationException] { }
                [pscustomobject]@{
                    Name = $process.ProcessName
                    Path = $path
                    IdentityAvailable = -not [string]::IsNullOrWhiteSpace($path)
                    SameFile = -not [string]::IsNullOrWhiteSpace($path) -and [AgentSignalerUpdater.FileIdentity]::Same($path, $exe)
                }
            }
            finally { $process.Dispose() }
        }
    )
    Assert-AgentSignalerRpcHostNotInUse $Directory $processes
}

$upgradeCodes = @{
    Dashboard = '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
    Remote = '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}'
    RpcHost = '{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}'
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$staging = $null
$exitCode = 0
try {
    $releaseInfo = Get-LatestGitHubRelease
    $release = $releaseInfo.Release
    $assets = @($release.assets)
    $checksumAsset = Get-AgentSignalerReleaseAsset $assets 'SHA256SUMS.txt'
    $updateRoot = Join-Path $env:LOCALAPPDATA 'AgentSignaler\Updates'
    $runId = [guid]::NewGuid().ToString('N')
    $staging = Join-Path $updateRoot $runId
    [void](New-Item -ItemType Directory -Path $staging -Force -WhatIf:$false)
    $checksumPath = Join-Path $staging 'SHA256SUMS.txt'
    Save-GitHubAsset $checksumAsset $releaseInfo.Token $checksumPath
    $checksums = Read-AgentSignalerChecksums ([IO.File]::ReadAllLines($checksumPath))
    Write-Host "Latest published release: $($release.tag_name) ($($release.html_url))"

    $plan = @(
        foreach ($app in ($Apps | Select-Object -Unique)) {
            $installed = Get-InstalledVersion $installer $upgradeCodes[$app]
            if ($null -eq $installed) {
                Write-Host "$app is not installed for the current user; skipping."
                continue
            }
            $name = "AgentSignaler.$app.msi"
            $asset = Get-AgentSignalerReleaseAsset $assets $name
            $source = Join-Path $staging $name
            Save-GitHubAsset $asset $releaseInfo.Token $source
            if ((Get-Sha256 $source) -cne $checksums[$name]) {
                throw "GitHub Release checksum verification failed for $name."
            }
            $package = Read-Package $installer $source $app
            $decision = Get-AgentSignalerUpdateDecision $installed $package.Version $releaseInfo.Version
            if ($decision -eq 'NoUpgrade') {
                Write-Host "$app installed: $installed; available: $($package.Version). No upgrade needed."
                continue
            }
            $receiverPort = $null
            if ($app -eq 'RpcHost') {
                $metadata = Get-ItemProperty -LiteralPath 'HKCU:\Software\AgentSignaler\Installer\RpcHost'
                $directory = Assert-AgentSignalerRpcHostInventory $metadata $installingUserSid $env:LOCALAPPDATA
                Assert-RpcHostProcessInventory $directory
                $receiverPort = [int]$metadata.ReceiverPort
            }
            [pscustomobject]@{
                App = $app
                Installed = $installed
                Package = $package
                Source = $source
                Staged = $null
                ReceiverPort = $receiverPort
            }
        }
    )
    $approved = @(
        foreach ($item in $plan) {
            if ($PSCmdlet.ShouldProcess("$($item.App) for $env:USERNAME",
                    "Upgrade $($item.Installed) to $($item.Package.Version) from $($release.html_url)")) {
                $item
            }
        }
    )
    if ($approved.Count -gt 0) {
        $processNames = @(
            if ($approved.App -contains 'Dashboard') { 'AgentSignaler.Dashboard' }
            if ($approved.App -contains 'Remote') { 'AgentSignaler.Configurator'; 'AgentSignaler.Relay' }
        )
        $sessionId = (Get-Process -Id $PID).SessionId
        $running = @(Get-Process | Where-Object {
            $_.SessionId -eq $sessionId -and $_.ProcessName -in $processNames
        })
        if ($running.Count -gt 0) {
            throw "Exit the apps and pause active Copilot CLI work, then retry. Running: $($running.ProcessName -join ', '). Closing Dashboard to the tray is not Exit."
        }

        $logRoot = Join-Path $updateRoot 'Logs'
        [void](New-Item -ItemType Directory -Path $logRoot -Force)
        Write-Warning 'GitHub Release checksums were verified, but these development MSIs are unsigned and do not provide publisher authenticity.'

        # Revalidate every downloaded package before changing either installation.
        foreach ($item in $approved) {
            $item.Staged = $item.Source
            if ((Get-Sha256 $item.Staged) -cne $checksums["AgentSignaler.$($item.App).msi"]) {
                throw 'The staged package checksum changed. No further servicing will run.'
            }
            $stagedPackage = Read-Package $installer $item.Staged $item.App
            if ($stagedPackage.Version -ne $item.Package.Version -or
                $stagedPackage.ProductCode -ne $item.Package.ProductCode) {
                throw "The downloaded $($item.App) package changed during validation. Run the script again."
            }
        }

        # Client ownership spans Windows sessions; only its current-user IPC helper may stop it.
        if ($approved.App -contains 'Remote') { Stop-OwnedRemoteClient }

        foreach ($item in $approved) {
            # Keep the verified download pinned against replacement through the
            # Windows Installer elevation handoff and the complete servicing run.
            $pin = [IO.File]::Open($item.Staged, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ((Get-Sha256 $item.Staged) -cne $checksums["AgentSignaler.$($item.App).msi"]) {
                    throw 'The staged package changed before servicing. No MSI was invoked.'
                }
                $currentPackage = Read-Package $installer $item.Staged $item.App
                if ($currentPackage.Version -ne $item.Package.Version -or $currentPackage.ProductCode -cne $item.Package.ProductCode) {
                    throw 'The pinned package identity changed before servicing.'
                }
                $log = Join-Path $logRoot "$runId-$($item.App).log"
                Write-Host "Updating $($item.App) to $($item.Package.Version). Log: $log"
                $arguments = "/i `"$($item.Staged)`" /passive /norestart /L*v `"$log`" REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable"
                if ($item.App -eq 'RpcHost') {
                    Assert-RpcHostProcessInventory (Join-Path $env:LOCALAPPDATA 'Programs\AgentSignaler\RpcHost')
                    $arguments += " RECEIVERPORT=$($item.ReceiverPort) MSIDISABLERMRESTART=1"
                    Write-Warning 'RpcHost requires Windows Installer elevation for its mandatory Private receiver rule. Denial or firewall failure fails servicing; no updater firewall helper is used.'
                }
                $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
                    -ArgumentList $arguments -Wait -PassThru
                try {
                    try { $outcome = Get-AgentSignalerServicingOutcome $process.ExitCode }
                    catch { throw "Updating $($item.App) failed with MSI exit code $($process.ExitCode). See $log. Earlier successful updates are not rolled back." }
                    $actual = Get-InstalledVersion $installer $upgradeCodes[$item.App]
                    if ($null -eq $actual -or $actual -ne $item.Package.Version) {
                        throw "Post-install version verification failed for $($item.App). See $log."
                    }
                    Write-Host "$($item.App) updated to $actual."
                    if ($outcome -eq 'RestartRequired') {
                        $exitCode = 3010
                        Write-Warning 'Windows Installer reports that a restart is required. This script will not restart Windows.'
                    }
                }
                finally { $process.Dispose() }
            }
            finally { $pin.Dispose() }
        }
        Write-Host 'Updates completed. Reopen the apps when ready. Start Agent Signaler Client explicitly to resume remote reporting; upgrade does not override Exit.'
    }
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    $httpClient.Dispose()
    $httpHandler.Dispose()
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -WhatIf:$false
    }
}
exit $exitCode
