[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Enable', 'Remove', 'Status')][string] $Action = 'Enable',
    [ValidateSet('Release', 'Debug')][string] $Configuration = 'Release',
    [ValidateSet('Public', 'Private', 'Domain')][string] $Profile,
    [string] $OwnedBlockRulesCsv,
    [switch] $Elevated
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stateDirectory = Join-Path $root 'artifacts\test-firewall'
$receiptPath = Join-Path $stateDirectory 'last-operation.clixml'
$published = Join-Path $root 'artifacts\publish\rpchost\AgentSignaler.RpcHost.exe'
function HashText([string] $Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
}
function SameSet($Left, $Right) {
    return ((@($Left | Sort-Object -Unique) -join '|') -ieq (@($Right | Sort-Object -Unique) -join '|'))
}
function NormalizeAddress([string[]] $Addresses) {
    foreach ($address in $Addresses) {
        if ($address -in @('127.0.0.1/32', '127.0.0.1/255.255.255.255')) { '127.0.0.1' }
        else { $address }
    }
}
if (-not $Profile) {
    $profiles = @(Get-NetConnectionProfile | ForEach-Object {
        if ([string]$_.NetworkCategory -eq 'DomainAuthenticated') { 'Domain' } else { [string]$_.NetworkCategory }
    } | Sort-Object -Unique)
    if ($profiles.Count -ne 1) { throw 'Specify -Profile explicitly when there is not exactly one active network category.' }
    $Profile = $profiles[0]
}
$Profile = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Profile.ToLowerInvariant())
$group = 'AgentSignaler.TestFirewall.' + (HashText $root).Substring(0, 16)
$description = "Developer tests only; TCP from IPv4 loopback only; checkout=$root"
$windows = 'net10.0-windows10.0.22621.0'
$relativePrograms = @(
    "tests\AgentSignaler.Service.Tests\bin\x64\$Configuration\net10.0\testhost.exe",
    "tests\AgentSignaler.Integration.Tests\bin\x64\$Configuration\$windows\testhost.exe",
    "tests\AgentSignaler.Dashboard.Core.Tests\bin\x64\$Configuration\$windows\testhost.exe",
    "tests\AgentSignaler.RpcHost.Tests\bin\x64\$Configuration\$windows\testhost.exe",
    "src\AgentSignaler.RpcHost\bin\x64\$Configuration\$windows\win-x64\AgentSignaler.RpcHost.exe",
    'artifacts\publish\rpchost\AgentSignaler.RpcHost.exe',
    '.rpc-test-work\published-exe\exe-only\AgentSignaler.RpcHost.exe',
    'artifacts\rpchost-publish-tests\firewall-prepared\exe-only\AgentSignaler.RpcHost.exe'
)
$expected = @($relativePrograms | ForEach-Object {
    $program = Join-Path $root $_
    [pscustomobject]@{ Name = "$group.$((HashText $program).Substring(0,16)).$Profile"; Program = $program }
})
if ($Action -eq 'Enable') {
    foreach ($item in $expected | Select-Object -First 6) {
        if (-not (Test-Path -LiteralPath $item.Program -PathType Leaf)) {
            throw "Build/publish $Configuration x64 before setup. Missing executable: $($item.Program)"
        }
    }
}
function AssertOwnedRule($Rule, $Expected) {
    $application = $Rule | Get-NetFirewallApplicationFilter
    $ports = $Rule | Get-NetFirewallPortFilter
    $addresses = $Rule | Get-NetFirewallAddressFilter
    if ($Rule.Group -cne $group -or $Rule.Description -ine $description -or
        $application.Program -ine $Expected.Program -or [string]$Rule.Action -ne 'Allow' -or
        [string]$Rule.Enabled -ne 'True' -or [string]$Rule.Direction -ne 'Inbound' -or
        [string]$Rule.Profile -ne $Profile -or [string]$Rule.EdgeTraversalPolicy -ne 'Block' -or
        [string]$ports.Protocol -ne 'TCP' -or -not (SameSet $ports.LocalPort @('Any')) -or
        -not (SameSet $ports.RemotePort @('Any')) -or -not (SameSet $addresses.LocalAddress @('Any')) -or
        -not (SameSet (NormalizeAddress $addresses.RemoteAddress) @('127.0.0.1')) -or
        [string]$Rule.PolicyStoreSourceType -ne 'Local') {
        throw "Rule identity/properties differ; refusing to modify it: $($Rule.Name)"
    }
}
$rules = @(Get-NetFirewallRule -PolicyStore PersistentStore)
$present = @{}
foreach ($item in $expected) {
    $match = @($rules | Where-Object Name -CEQ $item.Name)
    if ($match.Count -gt 1) { throw "Ambiguous rule identity: $($item.Name)" }
    if ($match.Count -eq 1) { AssertOwnedRule $match[0] $item; $present[$item.Name] = $match[0] }
}
$blocks = @()
if ($OwnedBlockRulesCsv) {
    $OwnedBlockRulesCsv = (Resolve-Path -LiteralPath $OwnedBlockRulesCsv).ProviderPath
    $rootPattern = [regex]::Escape($root + '\')
    $temporaryPattern = $rootPattern + '(?:\.rpc-test-work|artifacts\\rpchost-publish-tests)\\[0-9a-f]{32}\\exe-only\\AgentSignaler\.RpcHost\.exe$'
    foreach ($entry in Import-Csv -LiteralPath $OwnedBlockRulesCsv) {
        if ($entry.Action -ne 'Block' -or $entry.Profile -ne 'Public' -or $entry.Protocol -notin @('6','17') -or
            ($entry.Program -notmatch ('^' + $temporaryPattern) -and
             $entry.Program -inotmatch ('^' + [regex]::Escape((Join-Path $root "src\AgentSignaler.RpcHost\bin\x64\$Configuration\$windows\win-x64\AgentSignaler.RpcHost.exe")) + '$') -and
             $entry.Program -inotmatch ('^' + [regex]::Escape((Join-Path $root "tests\AgentSignaler.RpcHost.Tests\bin\x64\$Configuration\$windows\testhost.exe")) + '$'))) {
            throw 'Cleanup manifest contains a path or rule outside the verified test-only scope.'
        }
        $match = @($rules | Where-Object Name -CEQ $entry.Name)
        if ($match.Count -eq 0) { Write-Host "Previously recorded test block already absent: $($entry.Name)"; continue }
        if ($match.Count -ne 1) { throw 'Ambiguous cleanup rule identity.' }
        $rule = $match[0]
        $application = $rule | Get-NetFirewallApplicationFilter
        $ports = $rule | Get-NetFirewallPortFilter
        $addresses = $rule | Get-NetFirewallAddressFilter
        $protocol = if ($entry.Protocol -eq '6') { 'TCP' } else { 'UDP' }
        if ($application.Program -ine $entry.Program -or [string]$rule.Action -ne 'Block' -or
            [string]$rule.Profile -ne 'Public' -or [string]$rule.Enabled -ne 'True' -or
            [string]$rule.Direction -ne 'Inbound' -or [string]$rule.EdgeTraversalPolicy -ne 'Block' -or
            [string]$rule.PolicyStoreSourceType -ne 'Local' -or [string]$ports.Protocol -ne $protocol -or
            -not (SameSet $ports.LocalPort @('Any')) -or -not (SameSet $ports.RemotePort @('Any')) -or
            -not (SameSet $addresses.LocalAddress @('Any')) -or -not (SameSet $addresses.RemoteAddress @('Any'))) {
            throw "Recorded test block has changed; refusing removal: $($entry.Name)"
        }
        $blocks += $rule
    }
}
Write-Host "Checkout: $root; profile: $Profile; existing owned allow rules: $($present.Count)/$($expected.Count); verified old test blocks: $($blocks.Count)"
if ($Action -eq 'Status') { return }
$needsChange = if ($Action -eq 'Enable') { $present.Count -ne $expected.Count -or $blocks.Count -gt 0 } else { $present.Count -gt 0 }
if ($needsChange -and -not $PSCmdlet.ShouldProcess("$root ($Profile, TCP from 127.0.0.1 only)", "$Action exact test firewall rules")) { return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if ($needsChange -and -not $administrator) {
    if ($Elevated) { throw 'The elevated helper does not have administrator privileges.' }
    $arguments = @('-NoProfile', '-NonInteractive', '-File', "`"$PSCommandPath`"",
        '-Action', $Action, '-Configuration', $Configuration, '-Profile', $Profile, '-Elevated')
    if ($OwnedBlockRulesCsv) { $arguments += @('-OwnedBlockRulesCsv', "`"$OwnedBlockRulesCsv`"") }
    Write-Host 'Requesting one UAC approval for firewall setup only. No tests run elevated.'
    $helper = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Verb RunAs -ArgumentList $arguments -PassThru
    try {
        if (-not $helper.WaitForExit(120000)) { throw "Firewall helper exceeded 120 seconds (PID $($helper.Id)); inspect it before retrying." }
        if ($helper.ExitCode -ne 0) { throw "Firewall helper failed (exit $($helper.ExitCode)); inspect the error and partial changes in $receiptPath" }
    }
    finally { $helper.Dispose() }
    $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore)
    foreach ($item in $expected) {
        $match = @($rules | Where-Object Name -CEQ $item.Name)
        if ($Action -eq 'Enable') {
            if ($match.Count -ne 1) { throw "Expected test rule was not installed: $($item.Name)" }
            AssertOwnedRule $match[0] $item
        } elseif ($match.Count -ne 0) { throw "Owned test rule was not removed: $($item.Name)" }
    }
    foreach ($block in $blocks) {
        if ($Action -eq 'Enable' -and @($rules | Where-Object Name -CEQ $block.Name).Count -ne 0) {
            throw "Verified test block remains: $($block.Name)"
        }
    }
} elseif ($needsChange) {
    [void](New-Item -ItemType Directory -Path $stateDirectory -Force)
    $created = @()
    $removed = @()
    $complete = $false
    try {
        foreach ($item in $expected) {
            if ($Action -eq 'Enable' -and -not $present.ContainsKey($item.Name)) {
                New-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name `
                    -DisplayName "Agent Signaler developer tests ($Profile): $($item.Program)" `
                    -Group $group -Description $description -Program $item.Program -Enabled True `
                    -Direction Inbound -Action Allow -Profile $Profile -Protocol TCP `
                    -LocalAddress Any -RemoteAddress '127.0.0.1' -EdgeTraversalPolicy Block | Out-Null
                $created += $item.Name
                $rule = Get-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name
                AssertOwnedRule $rule $item
            } elseif ($Action -eq 'Remove' -and $present.ContainsKey($item.Name)) {
                $present[$item.Name] | Remove-NetFirewallRule -Confirm:$false
                $removed += $item.Name
            }
        }
        if ($Action -eq 'Enable') {
            foreach ($block in $blocks) {
                $block | Remove-NetFirewallRule -Confirm:$false
                $removed += $block.Name
            }
        }
        $complete = $true
    }
    finally {
        [pscustomobject]@{
            Complete = $complete; Action = $Action; Profile = $Profile; Checkout = $root
            Created = $created; Removed = $removed; RecordedAtUtc = [DateTime]::UtcNow
            Error = if (-not $complete -and $Error.Count -gt 0) { $Error[0].Exception.Message } else { $null }
        } | Export-Clixml -LiteralPath $receiptPath
        if (-not $complete) { Write-Warning "Firewall operation incomplete; exact partial changes are recorded in $receiptPath" }
    }
}
if (-not $Elevated -and $Action -eq 'Enable') {
    $env:AGENT_SIGNALER_RPC_TEST_EXE = $published
    $env:AGENT_SIGNALER_RPC_HOST_EXE = $published
    Write-Host 'Test access configured. Published-EXE variables set in this terminal. Tests always use fixed EXE fixture paths. Unchanged rules need no elevation.'
} elseif ($Action -eq 'Remove') {
    Write-Host 'Owned test allow rules removed; unrelated rules preserved.'
}
