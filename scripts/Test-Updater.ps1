[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected 'Expected invalid fixture to fail closed.'
}
$root = Split-Path -Parent $PSScriptRoot
$fixtureDirectory = Join-Path $root ('artifacts\updater-policy-tests\' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixtureDirectory -Force)
$path = Join-Path $fixtureDirectory 'fixture.json'
$sid = 'S-1-5-21-111-222-333-1001'
$payload = 'synthetic-msi-not-an-installable-package'
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '') }
finally { $sha.Dispose() }
$names = @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')
$fixture = @{
    Schema = 'AgentSignaler.UpdaterFixture.v1'; Version = '1.0.15'; UserSid = $sid
    Checksums = @($names | ForEach-Object { "$hash  $_" })
    Inventory = @(
        foreach ($app in @('Dashboard', 'Remote')) {
            @{
                App = $app; UpgradeCode = $script:AgentSignalerUpgradeCodes[$app]
                AssignmentType = '0'; UserSid = $sid; Version = '1.0.14'
            }
        }
    )
    Assets = @(
        foreach ($app in @('Dashboard', 'Remote')) {
            @{
                name = "AgentSignaler.$app.msi"; size = $payload.Length; Payload = $payload
                Template = 'x64;1033'; SummaryFlags = 10
                Properties = @{
                    UpgradeCode = $script:AgentSignalerUpgradeCodes[$app]
                    ProductCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
                    ProductVersion = '1.0.15'
                }
            }
        }
        @{ name = 'SHA256SUMS.txt'; size = 200 }
    )
}
function RunFixture {
    param([string[]] $Apps = @('Dashboard', 'Remote'))
    $fixture | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding UTF8
    $before = (Get-FileHash -LiteralPath $path).Hash
    $files = @(Get-ChildItem -LiteralPath $fixtureDirectory -Recurse -File).Count
    try {
        $result = & (Join-Path $PSScriptRoot 'Update-AgentSignaler.ps1') -FixturePath $path -Apps $Apps -WhatIf
    }
    finally {
        Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $before -and
            @(Get-ChildItem -LiteralPath $fixtureDirectory -Recurse -File).Count -eq $files) 'Fixture WhatIf mutated inputs.'
    }
    Assert ($result.ServicingCalls -eq 0 -and $result.ProcessStarts -eq 0 -and $result.FirewallChanges -eq 0) 'Fixture WhatIf attempted servicing.'
    return $result
}
try {
    $result = RunFixture
    Assert ($result.Plans.Count -eq 2 -and $result.Downloads -eq 2 -and
        $result.Plans[0].App -ceq 'Dashboard' -and $result.Plans[1].App -ceq 'Remote') 'Both supported packages must be planned.'
    foreach ($app in @('Dashboard', 'Remote')) {
        $result = RunFixture -Apps @($app, $app)
        Assert ($result.Plans.Count -eq 1 -and $result.Plans[0].App -ceq $app -and
            $result.Downloads -eq 1) 'Selection must update only the requested app, once.'
    }
    foreach ($version in @('1.0.15', '1.0.16')) {
        foreach ($inventory in $fixture.Inventory) { $inventory.Version = $version }
        Assert ((RunFixture).Plans.Count -eq 0) 'Equal/newer installed versions must not be downgraded.'
    }
    foreach ($inventory in $fixture.Inventory) { $inventory.Version = '1.0.14' }
    $fixture.Inventory[0].Version = '1.0.15'
    Assert ((RunFixture).Plans[0].App -ceq 'Remote') 'An up-to-date app must not suppress another upgrade.'
    $fixture.Inventory[0].Version = '1.0.14'
    $savedInventory = $fixture.Inventory
    $fixture.Inventory = @()
    $result = RunFixture
    Assert ($result.Plans.Count -eq 0 -and $result.Downloads -eq 0) 'Do not download or install uninstalled packages.'
    $fixture.Inventory = $savedInventory
    foreach ($inventory in $fixture.Inventory) {
        foreach ($field in @('UpgradeCode', 'UserSid', 'AssignmentType', 'Version')) {
            $saved = $inventory[$field]; $inventory[$field] = 'invalid'
            Reject { RunFixture }; $inventory[$field] = $saved
        }
        $fixture.Inventory += $inventory; Reject { RunFixture }; $fixture.Inventory = $savedInventory
    }
    $savedAssets = $fixture.Assets
    foreach ($asset in @($fixture.Assets | Where-Object { $_.name -like '*.msi' })) {
        foreach ($field in @('ProductVersion', 'ProductCode', 'UpgradeCode')) {
            $saved = $asset.Properties[$field]; $asset.Properties[$field] = 'invalid'
            Reject { RunFixture }; $asset.Properties[$field] = $saved
        }
        $asset.Properties.ProductVersion = '1.0.16'; Reject { RunFixture }; $asset.Properties.ProductVersion = '1.0.15'
        $asset.Properties.ALLUSERS = '1'; Reject { RunFixture }; $asset.Properties.Remove('ALLUSERS')
        $asset.Template = 'Intel;1033'; Reject { RunFixture }; $asset.Template = 'x64;1033'
        $asset.SummaryFlags = 2; Reject { RunFixture }; $asset.SummaryFlags = 10
        $asset.Payload += 'corruption'; Reject { RunFixture }; $asset.Payload = $payload
        $asset.Payload = 'X' + $payload.Substring(1); Reject { RunFixture }; $asset.Payload = $payload
    }
    foreach ($asset in $savedAssets) {
        $savedName = $asset.name
        $asset.name = $savedName.ToLowerInvariant(); Reject { RunFixture }; $asset.name = $savedName
        $fixture.Assets += $asset; Reject { RunFixture }; $fixture.Assets = $savedAssets
        $fixture.Assets += @{ name = $savedName.ToLowerInvariant(); size = 1 }
        Reject { RunFixture }; $fixture.Assets = $savedAssets
        $fixture.Assets = @($savedAssets | Where-Object { $_.name -cne $savedName })
        Reject { RunFixture -Apps Remote }; $fixture.Assets = $savedAssets
        foreach ($size in @(0, -1, (1GB + 1))) {
            $saved = $asset.size; $asset.size = $size
            Reject { RunFixture }; $asset.size = $saved
        }
    }
    $fixture.Assets[-1].size = 64KB + 1; Reject { RunFixture }; $fixture.Assets[-1].size = 200
    $savedChecksums = $fixture.Checksums
    foreach ($line in $savedChecksums) {
        $fixture.Checksums += $line; Reject { RunFixture }; $fixture.Checksums = $savedChecksums
        $fixture.Checksums = @($savedChecksums | Where-Object { $_ -cne $line })
        Reject { RunFixture -Apps Remote }; $fixture.Checksums = $savedChecksums
        $fixture.Checksums += $line.ToLowerInvariant(); Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    }
    foreach ($name in @('AgentSignaler.RpcHost.msi', 'AgentSignaler.RpcHost.exe',
            'AgentSignaler.RpcHost.NOTICES.txt', 'AgentSignaler.Unexpected.msi')) {
        $fixture.Checksums += "$hash  $name"
        Reject { RunFixture -Apps Remote }; $fixture.Checksums = $savedChecksums
        $fixture.Assets += @{ name = $name; size = 1 }
        Reject { RunFixture -Apps Remote }; $fixture.Assets = $savedAssets
    }
    $fixture.Checksums += 'invalid'; Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    $fixture.Checksums += @('') * 127; Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    Assert ((Read-AgentSignalerChecksums @($savedChecksums + @('', ' '))).Count -eq 2) 'Blank manifest lines remain valid.'
    foreach ($version in @('1.0', '01.0.15', '256.0.1', '1.256.1', '1.0.65536', '1.0.15-beta')) {
        Reject { ConvertTo-AgentSignalerVersion $version }
    }
    Reject { & (Join-Path $PSScriptRoot 'Update-AgentSignaler.ps1') -FixturePath $path }
    Reject { & (Join-Path $PSScriptRoot 'Update-AgentSignaler.ps1') -FixturePath $path -Apps RpcHost -WhatIf }
    $pin = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Reject { [IO.File]::WriteAllText($path, 'replacement') } }
    finally { $pin.Dispose() }
    Assert ((Get-AgentSignalerServicingOutcome 0) -ceq 'Completed') 'MSI success outcome mismatch.'
    Assert ((Get-AgentSignalerServicingOutcome 3010) -ceq 'RestartRequired') 'Restart-required must propagate without restarting.'
    foreach ($code in @(5, 1602, 1603, 1618, 1638)) { Reject { Get-AgentSignalerServicingOutcome $code } }
    Assert ((Get-AgentSignalerUpdateDecision $null ([version]'1.0.15') ([version]'1.0.15')) -ceq 'NotInstalled') 'Missing installation must be a no-op.'
    Write-Host 'Updater policy and non-mutating fixture -WhatIf passed for Dashboard/Remote: inventory, exact reduced assets, retired-asset rejection, hashes, size, identity, architecture, scope/version, no-op and no downgrade. No network, inventory access, process starts or servicing.'
}
finally { Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force }
