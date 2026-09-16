#Requires -Version 5.1
<#
.SYNOPSIS
Updates the current user's installed Agent Signaler apps from a trusted MSI folder.
.EXAMPLE
.\Update-AgentSignaler.ps1 -SourcePath C:\Releases\AgentSignaler -WhatIf
.EXAMPLE
.\Update-AgentSignaler.ps1 -SourcePath \\server\releases\AgentSignaler -Apps Remote
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $SourcePath,
    [ValidateSet('Dashboard', 'Remote')]
    [string[]] $Apps = @('Dashboard', 'Remote')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not [IO.Path]::IsPathFullyQualified($SourcePath) -or $SourcePath -match '[\x00-\x1F]') {
    throw 'SourcePath must be an absolute local or UNC directory without control characters.'
}
$SourcePath = [IO.Path]::GetFullPath($SourcePath)

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
    $plan = @(
        foreach ($app in ($Apps | Select-Object -Unique)) {
            $installed = Get-InstalledVersion $installer $upgradeCodes[$app]
            if ($null -eq $installed) {
                Write-Host "$app is not installed for the current user; skipping."
                continue
            }
            $source = Join-Path $SourcePath "AgentSignaler.$app.msi"
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Installer not found or inaccessible: $source"
            }
            $source = (Get-Item -LiteralPath $source).FullName
            $package = Read-Package $installer $source $upgradeCodes[$app]
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
                    "Upgrade $($item.Installed) to $($item.Package.Version) from $($item.Source)")) {
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

        $updateRoot = Join-Path $env:LOCALAPPDATA 'AgentSignaler\Updates'
        $runId = [guid]::NewGuid().ToString('N')
        $staging = Join-Path $updateRoot $runId
        [void](New-Item -ItemType Directory -Path $staging -Force)
        $logRoot = Join-Path $updateRoot 'Logs'
        [void](New-Item -ItemType Directory -Path $logRoot -Force)
        Write-Warning 'Use only a trusted release folder. Hash checking detects copy errors, not publisher authenticity.'

        # Stage every approved package before changing either installation.
        foreach ($item in $approved) {
            $item.Staged = Join-Path $staging "AgentSignaler.$($item.App).msi"
            $hash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash
            Copy-Item -LiteralPath $item.Source -Destination $item.Staged
            if ((Get-FileHash -LiteralPath $item.Staged -Algorithm SHA256).Hash -ne $hash) {
                throw "Copy verification failed for $($item.App); no installations have been changed."
            }
            $stagedPackage = Read-Package $installer $item.Staged $upgradeCodes[$item.App]
            if ($stagedPackage.Version -ne $item.Package.Version -or
                $stagedPackage.ProductCode -ne $item.Package.ProductCode) {
                throw "The $($item.App) release changed during staging. Run the script again."
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
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
exit $exitCode
