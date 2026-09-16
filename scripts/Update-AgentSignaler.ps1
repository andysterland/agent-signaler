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
    [ValidateSet('Dashboard', 'Remote')]
    [string[]] $Apps = @('Dashboard', 'Remote')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = 'andysterland/agent-signaler'
$apiRoot = "https://api.github.com/repos/$repository"

if (-not [Environment]::Is64BitOperatingSystem -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Agent Signaler requires Windows x64.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
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
        $json = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return $json | ConvertFrom-Json
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
        Version = [version]$release.tag_name.Substring(1)
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
            try { $source.CopyTo($file) }
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
    param($Installer, [string] $Path, [string] $UpgradeCode)

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
        if ($properties['UpgradeCode'] -ne $UpgradeCode -or $properties.ContainsKey('ALLUSERS')) {
            throw "Not the expected per-user Agent Signaler package: $Path"
        }
        $summary = $database.SummaryInformation(0)
        try {
            if ($summary.Property(7) -notlike 'x64;*' -or
                ([int]$summary.Property(15) -band 8) -eq 0) {
                throw "Not a non-elevated x64 package: $Path"
            }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        if ($properties['ProductVersion'] -notmatch '^\d+\.\d+\.\d+$' -or
            [string]::IsNullOrWhiteSpace($properties['ProductCode'])) {
            throw "Invalid MSI product metadata: $Path"
        }
        [pscustomobject]@{
            Version = [version]$properties['ProductVersion']
            ProductCode = $properties['ProductCode']
        }
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

$upgradeCodes = @{
    Dashboard = '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
    Remote = '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}'
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$staging = $null
$exitCode = 0
try {
    $releaseInfo = Get-LatestGitHubRelease
    $release = $releaseInfo.Release
    $assets = @($release.assets)
    $checksumAssets = @($assets | Where-Object { $_.name -ceq 'SHA256SUMS.txt' })
    if ($checksumAssets.Count -ne 1) {
        throw 'The latest GitHub Release must contain exactly one SHA256SUMS.txt asset.'
    }
    $updateRoot = Join-Path $env:LOCALAPPDATA 'AgentSignaler\Updates'
    $runId = [guid]::NewGuid().ToString('N')
    $staging = Join-Path $updateRoot $runId
    [void](New-Item -ItemType Directory -Path $staging -Force -WhatIf:$false)
    $checksumPath = Join-Path $staging 'SHA256SUMS.txt'
    Save-GitHubAsset $checksumAssets[0] $releaseInfo.Token $checksumPath
    $checksums = @{}
    foreach ($line in [IO.File]::ReadAllLines($checksumPath)) {
        if ($line -match '^([A-Fa-f0-9]{64})  (AgentSignaler\.(Dashboard|Remote)\.msi)$') {
            if ($checksums.ContainsKey($matches[2])) { throw "Duplicate checksum for $($matches[2])." }
            $checksums[$matches[2]] = $matches[1].ToUpperInvariant()
        }
    }
    foreach ($name in @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')) {
        if (-not $checksums.ContainsKey($name)) { throw "SHA256SUMS.txt is missing $name." }
    }
    Write-Host "Latest published release: $($release.tag_name) ($($release.html_url))"

    $plan = @(
        foreach ($app in ($Apps | Select-Object -Unique)) {
            $installed = Get-InstalledVersion $installer $upgradeCodes[$app]
            if ($null -eq $installed) {
                Write-Host "$app is not installed for the current user; skipping."
                continue
            }
            $name = "AgentSignaler.$app.msi"
            $matchingAssets = @($assets | Where-Object { $_.name -ceq $name })
            if ($matchingAssets.Count -ne 1) {
                throw "The latest GitHub Release must contain exactly one $name asset."
            }
            $source = Join-Path $staging $name
            Save-GitHubAsset $matchingAssets[0] $releaseInfo.Token $source
            if ((Get-Sha256 $source) -cne $checksums[$name]) {
                throw "GitHub Release checksum verification failed for $name."
            }
            $package = Read-Package $installer $source $upgradeCodes[$app]
            if ($package.Version -ne $releaseInfo.Version) {
                throw "$name version $($package.Version) does not match release $($release.tag_name)."
            }
            if ($package.Version -le $installed) {
                Write-Host "$app installed: $installed; available: $($package.Version). No upgrade needed."
                continue
            }
            [pscustomobject]@{
                App = $app
                Installed = $installed
                Package = $package
                Source = $source
                Staged = $null
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
            $stagedPackage = Read-Package $installer $item.Staged $upgradeCodes[$item.App]
            if ($stagedPackage.Version -ne $item.Package.Version -or
                $stagedPackage.ProductCode -ne $item.Package.ProductCode) {
                throw "The downloaded $($item.App) package changed during validation. Run the script again."
            }
        }

        # Client ownership spans Windows sessions; only its current-user IPC helper may stop it.
        if ($approved.App -contains 'Remote') { Stop-OwnedRemoteClient }

        foreach ($item in $approved) {
            $log = Join-Path $logRoot "$runId-$($item.App).log"
            Write-Host "Updating $($item.App) to $($item.Package.Version). Log: $log"
            $arguments = "/i `"$($item.Staged)`" /passive /norestart /L*v `"$log`" REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable"
            $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
                -ArgumentList $arguments -Wait -PassThru
            if ($process.ExitCode -notin @(0, 3010)) {
                throw "Updating $($item.App) failed with MSI exit code $($process.ExitCode). See $log. Earlier successful updates are not rolled back."
            }
            $actual = Get-InstalledVersion $installer $upgradeCodes[$item.App]
            if ($null -eq $actual -or $actual -ne $item.Package.Version) {
                throw "Post-install version verification failed for $($item.App). See $log."
            }
            Write-Host "$($item.App) updated to $actual."
            if ($process.ExitCode -eq 3010) {
                $exitCode = 3010
                Write-Warning 'Windows Installer reports that a restart is required. This script will not restart Windows.'
            }
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
