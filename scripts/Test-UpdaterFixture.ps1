[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $FixturePath,
    [string[]] $Apps = @('Dashboard', 'Remote', 'RpcHost')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')
if ((Get-Item -LiteralPath $FixturePath).Length -gt 1MB) { throw 'Updater fixture exceeds its bound.' }
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
if ($fixture.Schema -cne 'AgentSignaler.UpdaterFixture.v1') { throw 'Not a synthetic updater fixture.' }
$version = ConvertTo-AgentSignalerVersion $fixture.Version
$checksums = Read-AgentSignalerChecksums $fixture.Checksums
$downloads = 0
$plans = @(
    foreach ($app in ($Apps | Select-Object -Unique)) {
        $installed = @($fixture.Inventory | Where-Object { $_.App -ceq $app })
        if ($installed.Count -gt 1) { throw 'Multiple installed products share an upgrade identity.' }
        if ($installed.Count -eq 0) { continue }
        $inventory = $installed[0]
        if ($inventory.UpgradeCode -cne $script:AgentSignalerUpgradeCodes[$app] -or
            $inventory.AssignmentType -cne '0' -or $inventory.UserSid -cne $fixture.UserSid) {
            throw 'Unexpected installed-product identity or scope.'
        }
        $asset = Get-AgentSignalerReleaseAsset $fixture.Assets "AgentSignaler.$app.msi"
        $downloads++
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]$asset.Payload)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
        finally { $sha.Dispose() }
        if ($bytes.Length -ne [long]$asset.size -or $hash -cne $checksums[$asset.name]) {
            throw 'Synthetic downloaded asset size or checksum mismatch.'
        }
        $properties = @{}
        foreach ($property in $asset.Properties.PSObject.Properties) { $properties[$property.Name] = [string]$property.Value }
        $package = Assert-AgentSignalerPackageMetadata $properties $asset.Template $asset.SummaryFlags $app
        $decision = Get-AgentSignalerUpdateDecision (ConvertTo-AgentSignalerVersion $inventory.Version) $package.Version $version
        if ($decision -ne 'Upgrade') { continue }
        $port = $null
        if ($app -eq 'RpcHost') {
            $directory = Assert-AgentSignalerRpcHostInventory $inventory $fixture.UserSid $fixture.LocalAppData
            Assert-AgentSignalerRpcHostNotInUse $directory $fixture.Processes
            $port = [int]$inventory.ReceiverPort
        }
        [pscustomobject]@{ App = $app; Version = $package.Version.ToString(); ReceiverPort = $port }
    }
)
[pscustomobject]@{ Plans = $plans; Downloads = $downloads; ServicingCalls = 0; ProcessStarts = 0; FirewallChanges = 0 }
