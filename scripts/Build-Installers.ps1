[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Version = '1.0.14',
    [switch] $SkipPublish,
    [switch] $NoRestore,
    [switch] $ApplicationMsisOnly,
    [switch] $SkipRpcHostSmoke,
    [string] $DestinationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Version must be a three-part MSI version, for example 1.2.3.'
}
$parts = $Version.Split('.')
if ([long]$parts[0] -gt 255 -or [long]$parts[1] -gt 255 -or [long]$parts[2] -gt 65535) {
    throw 'MSI versions must be no greater than 255.255.65535.'
}

$root = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $root 'artifacts\publish'
$dashboard = Join-Path $publishRoot 'dashboard'
$remote = Join-Path $publishRoot 'remote'
$rpcHost = Join-Path $publishRoot 'rpchost'
$staging = Join-Path $root 'artifacts\installer-staging'
$msiOutput = Join-Path $root 'artifacts\msi'

function Invoke-DotNet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE`: $($Arguments -join ' ')"
    }
}

function Publish-Application {
    param([string] $ProjectName, [string] $Destination)
    $project = Join-Path $root "src\$ProjectName\$ProjectName.csproj"
    if (-not $NoRestore) {
        Invoke-DotNet @('restore', $project, '--runtime', 'win-x64',
            "-p:Configuration=$Configuration", '-p:Platform=x64')
    }
    Invoke-DotNet @('publish', $project, '--no-restore', '--configuration', $Configuration,
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $Destination,
        '-p:Platform=x64', "-p:Version=$Version", '-p:PublishTrimmed=false')
}

function Merge-PublishedApplication {
    param([string] $Source, [string] $Destination)
    $prefix = $Source.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relative = $file.FullName.Substring($prefix.Length)
        $target = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
                throw "Publish collision: $relative differs between remote applications. Align dependencies before packaging."
            }
        }
        else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

if (-not $SkipPublish) {
    foreach ($directory in @($dashboard, $remote, $rpcHost, $staging)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Publish-Application 'AgentSignaler.Dashboard' $dashboard
    Publish-Application 'AgentSignaler.RpcHost' $rpcHost
    # Optional symbols are separate; the distribution requires only the EXE and notices.
    Get-ChildItem -LiteralPath $rpcHost -File -Filter '*.pdb' | Remove-Item -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $rpcHost 'LICENSE')
    & (Join-Path $PSScriptRoot 'Write-RpcHostNotices.ps1') -Configuration $Configuration
    $configuratorStage = Join-Path $staging 'configurator'
    $relayStage = Join-Path $staging 'relay'
    $clientStage = Join-Path $staging 'client'
    Publish-Application 'AgentSignaler.Configurator' $configuratorStage
    Publish-Application 'AgentSignaler.Relay' $relayStage
    Publish-Application 'AgentSignaler.Client' $clientStage
    Merge-PublishedApplication $configuratorStage $remote
    Merge-PublishedApplication $relayStage $remote
    Merge-PublishedApplication $clientStage $remote
    Remove-Item -LiteralPath $staging -Recurse -Force
}

New-Item -ItemType Directory -Path $msiOutput -Force | Out-Null
foreach ($name in @('Dashboard', 'Remote', 'RpcHost')) {
    $project = Join-Path $root "installers\AgentSignaler.$name\AgentSignaler.$name.wixproj"
    if (-not $NoRestore) { Invoke-DotNet @('restore', $project, '-p:Platform=x64') }
    Invoke-DotNet @('build', $project, '--no-restore', '-t:Rebuild', '--configuration', $Configuration,
        '-p:Platform=x64', "-p:ProductVersion=$Version", "-p:OutputPath=$msiOutput")
}
& (Join-Path $PSScriptRoot 'Test-Installers.ps1') -Version $Version
if ($SkipRpcHostSmoke) {
    Write-Warning 'RpcHost listener-bearing publish smoke was deferred. Run scripts\Test-RpcHostPublish.ps1 separately before accepting these artifacts.'
}
else {
    & (Join-Path $PSScriptRoot 'Test-RpcHostPublish.ps1') -Version $Version
}
& (Join-Path $PSScriptRoot 'Test-Updater.ps1')

if ($ApplicationMsisOnly) {
    if (-not [string]::IsNullOrWhiteSpace($DestinationPath)) {
        throw 'DestinationPath cannot be used with ApplicationMsisOnly.'
    }
    Write-Host 'Built and inspected Dashboard, Remote and RpcHost x64 MSIs and the standalone RpcHost EXE locally. Bundle and prerequisite packaging were skipped.'
    return
}

. (Join-Path $PSScriptRoot 'PrerequisiteSecurity.ps1')
Get-VerifiedPrerequisitePayloads
$bundleOutput = Join-Path $root 'artifacts\bundle'
New-Item -ItemType Directory -Path $bundleOutput -Force | Out-Null
foreach ($name in @('DevTunnelsPrerequisite', 'Dashboard.Bundle')) {
    $project = Join-Path $root "installers\AgentSignaler.$name\AgentSignaler.$name.wixproj"
    $output = if ($name -eq 'Dashboard.Bundle') { $bundleOutput } else { $msiOutput }
    if (-not $NoRestore) { Invoke-DotNet @('restore', $project, '-p:Platform=x64') }
    Invoke-DotNet @('build', $project, '--no-restore', '-t:Rebuild', '--configuration', $Configuration,
        '-p:Platform=x64', "-p:ProductVersion=$Version", "-p:OutputPath=$output")
}
& (Join-Path $PSScriptRoot 'Test-DashboardBundle.ps1') -Version $Version
& (Join-Path $PSScriptRoot 'Test-PrerequisiteSecurity.ps1')

if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    Write-Host "Built and inspected all x64 installers locally. No destination was supplied; nothing was copied, installed, or published."
    return
}
if (-not [IO.Path]::IsPathFullyQualified($DestinationPath) -or $DestinationPath -match '[\x00-\x1F]') {
    throw 'DestinationPath must be an absolute local or UNC directory without control characters.'
}

function Get-PackageHash {
    param([string] $Path)
    # Buffer remote reads to avoid a network round trip for each small hashing read.
    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read, 1MB,
        [System.IO.FileOptions]::SequentialScan)
    try {
        $hash = [System.Security.Cryptography.SHA256]::Create()
        try { return [System.BitConverter]::ToString($hash.ComputeHash($stream)) }
        finally { $hash.Dispose() }
    }
    finally { $stream.Dispose() }
}

$releaseDirectory = [IO.Path]::GetFullPath($DestinationPath)
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($name in @('Dashboard', 'Remote', 'RpcHost')) {
    $source = Join-Path $msiOutput "AgentSignaler.$name.msi"
    $destination = Join-Path $releaseDirectory "AgentSignaler.$name.msi"
    Write-Host "Copying $source to $destination..."
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Write-Host "Verifying copied $name MSI..."
    if ((Get-PackageHash $source) -ne (Get-PackageHash $destination)) {
        throw "Copied MSI verification failed: $destination"
    }
    Write-Host "$name MSI copied and verified."
}
$bundle = Join-Path $bundleOutput 'AgentSignaler.Dashboard.Setup.exe'
$bundleDestination = Join-Path $releaseDirectory 'AgentSignaler.Dashboard.Setup.exe'
Copy-Item -LiteralPath $bundle -Destination $bundleDestination -Force
if ((Get-PackageHash $bundle) -ne (Get-PackageHash $bundleDestination)) { throw 'Copied bundle verification failed.' }
$exeDestination = Join-Path $releaseDirectory 'AgentSignaler.RpcHost.exe'
Copy-Item -LiteralPath (Join-Path $rpcHost 'AgentSignaler.RpcHost.exe') -Destination $exeDestination -Force
if ((Get-PackageHash (Join-Path $rpcHost 'AgentSignaler.RpcHost.exe')) -ne (Get-PackageHash $exeDestination)) {
    throw 'Copied RpcHost EXE verification failed.'
}
$notices = Join-Path $rpcHost 'THIRD-PARTY-NOTICES.txt'
$noticesDestination = Join-Path $releaseDirectory 'AgentSignaler.RpcHost.NOTICES.txt'
Copy-Item -LiteralPath $notices -Destination $noticesDestination -Force
if ((Get-PackageHash $notices) -ne (Get-PackageHash $noticesDestination)) { throw 'Copied notices verification failed.' }
Write-Host "Built three per-user x64 MSIs, the RpcHost EXE and Dashboard bundle, validated and copied them to $releaseDirectory. No products or integrations were installed."
