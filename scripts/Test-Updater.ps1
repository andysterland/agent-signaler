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
$local = 'C:\SyntheticUser\AppData\Local'
$directory = Join-Path $local 'Programs\AgentSignaler\RpcHost'
$payload = 'synthetic-msi-not-an-installable-package'
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '') }
finally { $sha.Dispose() }
$names = @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi', 'AgentSignaler.RpcHost.msi', 'AgentSignaler.RpcHost.exe')
$fixture = @{
    Schema = 'AgentSignaler.UpdaterFixture.v1'; Version = '1.0.15'; UserSid = $sid; LocalAppData = $local
    Checksums = @($names | ForEach-Object { "$hash  $_" })
    Inventory = @(@{
        App = 'RpcHost'; UpgradeCode = $script:AgentSignalerUpgradeCodes.RpcHost
        AssignmentType = '0'; UserSid = $sid; Version = '1.0.14'
        InstallDirectory = $directory; ReceiverPort = 51824
    })
    Processes = @()
    Assets = @(@{
        name = 'AgentSignaler.RpcHost.msi'; size = $payload.Length; Payload = $payload
        Template = 'x64;1033'; SummaryFlags = 2
        Properties = @{
            UpgradeCode = $script:AgentSignalerUpgradeCodes.RpcHost
            ProductCode = '{7F5FAB2A-376B-416C-A24F-6AF1A494E3B9}'
            ProductVersion = '1.0.15'; RPCFIREWALLREQUIRED = '1'
            MSIRESTARTMANAGERCONTROL = 'Disable'; MSIDISABLERMRESTART = '1'
        }
    })
}
function RunFixture {
    $fixture | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding UTF8
    $before = (Get-FileHash -LiteralPath $path).Hash
    $files = @(Get-ChildItem -LiteralPath $fixtureDirectory -Recurse -File).Count
    $result = & (Join-Path $PSScriptRoot 'Update-AgentSignaler.ps1') -FixturePath $path -WhatIf
    Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $before -and
        @(Get-ChildItem -LiteralPath $fixtureDirectory -Recurse -File).Count -eq $files) 'Fixture WhatIf mutated inputs.'
    Assert ($result.ServicingCalls -eq 0 -and $result.ProcessStarts -eq 0 -and $result.FirewallChanges -eq 0) 'Fixture WhatIf attempted servicing.'
    return $result
}
try {
    $result = RunFixture
    Assert ($result.Plans.Count -eq 1 -and $result.Plans[0].ReceiverPort -eq 51824) 'Upgrade must preserve the MSI port.'
    $fixture.Inventory[0].ReceiverPort = 51822
    Assert ((RunFixture).Plans[0].ReceiverPort -eq 51822) 'A committed maintenance mirror must survive the next upgrade even when settings still use the old port.'
    $fixture.Inventory[0].ReceiverPort = 51824
    Assert ((RunFixture).Plans[0].ReceiverPort -eq 51824) 'Rolled-back maintenance must retain the prior port on the next upgrade.'
    foreach ($version in @('1.0.15', '1.0.16')) {
        $fixture.Inventory[0].Version = $version
        Assert ((RunFixture).Plans.Count -eq 0) 'Equal/newer installed versions must not be downgraded.'
    }
    $fixture.Inventory[0].Version = '1.0.14'
    $savedInventory = $fixture.Inventory
    $fixture.Inventory = @()
    $result = RunFixture
    Assert ($result.Plans.Count -eq 0 -and $result.Downloads -eq 0) 'Do not discover standalone EXEs or download uninstalled packages.'
    $fixture.Inventory = $savedInventory
    $fixture.Processes = @(@{ Name = 'AgentSignaler.RpcHost'; Path = 'C:\Standalone\AgentSignaler.RpcHost.exe'; IdentityAvailable = $true; SameFile = $false })
    Assert ((RunFixture).Plans.Count -eq 1) 'Standalone unrelated process must not block MSI servicing.'
    $fixture.Processes[0].Path = Join-Path $directory 'AgentSignaler.RpcHost.exe'
    Reject { RunFixture }
    $fixture.Processes[0].Path = 'C:\Alias\AgentSignaler.RpcHost.exe'; $fixture.Processes[0].SameFile = $true
    Reject { RunFixture }
    $fixture.Processes[0].SameFile = $false; $fixture.Processes[0].IdentityAvailable = $false
    Reject { RunFixture }
    $fixture.Processes = @()
    foreach ($field in @('UpgradeCode', 'UserSid', 'InstallDirectory', 'ReceiverPort')) {
        $saved = $fixture.Inventory[0][$field]; $fixture.Inventory[0][$field] = 'invalid'
        Reject { RunFixture }; $fixture.Inventory[0][$field] = $saved
    }
    $fixture.Inventory += $fixture.Inventory[0]; Reject { RunFixture }; $fixture.Inventory = $savedInventory
    $asset = $fixture.Assets[0]
    foreach ($field in @('ProductVersion', 'ProductCode', 'UpgradeCode', 'RPCFIREWALLREQUIRED', 'MSIRESTARTMANAGERCONTROL', 'MSIDISABLERMRESTART')) {
        $saved = $asset.Properties[$field]; $asset.Properties[$field] = 'invalid'
        Reject { RunFixture }; $asset.Properties[$field] = $saved
    }
    $asset.Properties.ALLUSERS = '1'; Reject { RunFixture }; $asset.Properties.Remove('ALLUSERS')
    $asset.Template = 'Intel;1033'; Reject { RunFixture }; $asset.Template = 'x64;1033'
    $asset.SummaryFlags = 10; Reject { RunFixture }; $asset.SummaryFlags = 2
    $asset.Payload += 'corruption'; Reject { RunFixture }; $asset.Payload = $payload
    $asset.name = 'agentsignaler.RpcHost.msi'; Reject { RunFixture }; $asset.name = 'AgentSignaler.RpcHost.msi'
    $fixture.Assets += $asset; Reject { RunFixture }; $fixture.Assets = @($asset)
    $savedChecksums = $fixture.Checksums
    $fixture.Checksums += $fixture.Checksums[0]; Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    $fixture.Checksums = @($savedChecksums | Select-Object -First 3); Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    $fixture.Checksums += "$hash  agentsignaler.RpcHost.msi"; Reject { RunFixture }; $fixture.Checksums = $savedChecksums
    foreach ($version in @('1.0', '01.0.15', '256.0.1', '1.256.1', '1.0.65536', '1.0.15-beta')) {
        Reject { ConvertTo-AgentSignalerVersion $version }
    }
    Reject { & (Join-Path $PSScriptRoot 'Update-AgentSignaler.ps1') -FixturePath $path }
    $pin = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Reject { [IO.File]::WriteAllText($path, 'replacement') } }
    finally { $pin.Dispose() }
    Assert ((Get-AgentSignalerServicingOutcome 0) -ceq 'Completed') 'MSI success outcome mismatch.'
    Assert ((Get-AgentSignalerServicingOutcome 3010) -ceq 'RestartRequired') 'Restart-required must propagate without restarting.'
    foreach ($code in @(5, 1602, 1603, 1618, 1638)) { Reject { Get-AgentSignalerServicingOutcome $code } }
    Write-Host 'Updater policy and non-mutating fixture -WhatIf passed: inventory, exact assets, hashes, scope/version, in-use aliases, no-op, preserved port. No network, inventory access, process starts, elevation or servicing.'
}
finally { Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force }
