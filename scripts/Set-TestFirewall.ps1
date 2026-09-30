[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Enable', 'Remove', 'Status')][string] $Action = 'Enable',
    [ValidateSet('Release', 'Debug')][string] $Configuration = 'Release',
    [ValidateSet('Public', 'Private', 'Domain')][string[]] $Profile = @('Private', 'Public'),
    [string] $ProfilePlanBase64,
    [string] $AllowPlanBase64,
    [switch] $Elevated
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stateDirectory = Join-Path $root 'artifacts\test-firewall'
$receiptPath = Join-Path $stateDirectory 'last-operation.clixml'
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
$profiles = if ($ProfilePlanBase64) {
    $profileBytes = [Convert]::FromBase64String($ProfilePlanBase64)
    if ($profileBytes.Length -gt 1024) { throw 'The elevated profile plan exceeds 1 KiB.' }
    @([Text.Encoding]::UTF8.GetString($profileBytes) | ConvertFrom-Json)
} else {
    @($Profile)
}
$profiles = @($profiles | ForEach-Object {
    if ($_ -notin @('Public', 'Private', 'Domain')) { throw "Unsupported firewall profile: $_" }
    [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_.ToLowerInvariant())
} | Sort-Object -Unique)
$group = 'AgentSignaler.TestFirewall.' + (HashText $root).Substring(0, 16)
$description = "Developer tests only; TCP from IPv4 loopback only; checkout=$root"
$windows = 'net10.0-windows10.0.22621.0'
$relativePrograms = @(
    "tests\AgentSignaler.Service.Tests\bin\x64\$Configuration\net10.0\testhost.exe",
    "tests\AgentSignaler.Integration.Tests\bin\x64\$Configuration\$windows\testhost.exe",
    "tests\AgentSignaler.Dashboard.Core.Tests\bin\x64\$Configuration\$windows\testhost.exe"
)
$expected = @($profiles | ForEach-Object {
    $currentProfile = $_
    $relativePrograms | ForEach-Object {
        $program = Join-Path $root $_
        [pscustomobject]@{
            Name = "$group.$((HashText $program).Substring(0,16)).$currentProfile"
            Program = $program
            Profile = $currentProfile
        }
    }
})
if ($Action -eq 'Enable') {
    foreach ($relativeProgram in $relativePrograms) {
        $program = Join-Path $root $relativeProgram
        if (-not (Test-Path -LiteralPath $program -PathType Leaf)) {
            throw "Build $Configuration x64 before setup. Missing executable: $program"
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
        [string]$Rule.Profile -ne $Expected.Profile -or [string]$Rule.EdgeTraversalPolicy -ne 'Block' -or
        [string]$ports.Protocol -ne 'TCP' -or -not (SameSet $ports.LocalPort @('Any')) -or
        -not (SameSet $ports.RemotePort @('Any')) -or -not (SameSet $addresses.LocalAddress @('Any')) -or
        -not (SameSet (NormalizeAddress $addresses.RemoteAddress) @('127.0.0.1')) -or
        [string]$Rule.PolicyStoreSourceType -ne 'Local') {
        throw "Rule identity/properties differ; refusing to modify it: $($Rule.Name)"
    }
}
function NewOwnedRule($Expected, [Collections.Generic.List[string]] $Created) {
    New-NetFirewallRule -PolicyStore PersistentStore -Name $Expected.Name `
        -DisplayName "Agent Signaler developer tests ($($Expected.Profile)): $($Expected.Program)" `
        -Group $group -Description $description -Program $Expected.Program -Enabled True `
        -Direction Inbound -Action Allow -Profile $Expected.Profile -Protocol TCP `
        -LocalAddress Any -RemoteAddress '127.0.0.1' -EdgeTraversalPolicy Block | Out-Null
    $Created.Add($Expected.Name)
    $rule = Get-NetFirewallRule -PolicyStore PersistentStore -Name $Expected.Name
    AssertOwnedRule $rule $Expected
}
$rules = @(Get-NetFirewallRule -PolicyStore PersistentStore)
$present = @{}
foreach ($item in $expected) {
    $match = @($rules | Where-Object Name -CEQ $item.Name)
    if ($match.Count -gt 1) { throw "Ambiguous rule identity: $($item.Name)" }
    if ($match.Count -eq 1) { AssertOwnedRule $match[0] $item; $present[$item.Name] = $match[0] }
}
Write-Host "Checkout: $root; profiles: $($profiles -join ', '); existing owned allow rules: $($present.Count)/$($expected.Count)"
if ($Action -eq 'Status') { return }
$approvedAllowPlan = $null
if ($AllowPlanBase64) {
    $allowBytes = [Convert]::FromBase64String($AllowPlanBase64)
    if ($allowBytes.Length -gt 32768) { throw 'The elevated allow plan exceeds 32 KiB.' }
    $approvedAllowPlan = [Text.Encoding]::UTF8.GetString($allowBytes) | ConvertFrom-Json
    if (@($approvedAllowPlan.Owned).Count -gt $expected.Count -or
        @($approvedAllowPlan.Create).Count -gt $expected.Count -or
        @($approvedAllowPlan.Remove).Count -gt $expected.Count) {
        throw 'The elevated allow plan contains too many rules.'
    }
    $approvedOwned = @($approvedAllowPlan.Owned)
    if ($approvedOwned.Count -ne $present.Count) { throw 'Owned test firewall state changed while awaiting elevation.' }
    foreach ($item in $approvedOwned) {
        if (-not $present.ContainsKey([string]$item.Name)) {
            throw "Owned test firewall state changed while awaiting elevation: $($item.Name)"
        }
    }
    $createItems = @($approvedAllowPlan.Create | ForEach-Object {
        $match = @($expected | Where-Object Name -CEQ ([string]$_.Name))
        if ($match.Count -ne 1 -or $present.ContainsKey($match[0].Name)) {
            throw "Approved test-rule creation changed while awaiting elevation: $($_.Name)"
        }
        $match[0]
    })
    $removeItems = @($approvedAllowPlan.Remove | ForEach-Object {
        $match = @($expected | Where-Object Name -CEQ ([string]$_.Name))
        if ($match.Count -ne 1 -or -not $present.ContainsKey($match[0].Name)) {
            throw "Approved test-rule removal changed while awaiting elevation: $($_.Name)"
        }
        $match[0]
    })
} else {
    $createItems = @(if ($Action -eq 'Enable') { $expected | Where-Object { -not $present.ContainsKey($_.Name) } })
    $removeItems = @(if ($Action -eq 'Remove') { $expected | Where-Object { $present.ContainsKey($_.Name) } })
}
$needsChange = $createItems.Count -gt 0 -or $removeItems.Count -gt 0
if ($needsChange -and -not $PSCmdlet.ShouldProcess("$root ($($profiles -join ', '), TCP from 127.0.0.1 only)", "$Action exact test firewall rules")) { return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if ($needsChange -and -not $administrator) {
    if ($Elevated) { throw 'The elevated helper does not have administrator privileges.' }
    $profileJson = $profiles | ConvertTo-Json -Compress
    $profilePlan = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($profileJson))
    $allowJson = [ordered]@{
        Owned = @($expected | Where-Object { $present.ContainsKey($_.Name) } | Select-Object Name)
        Create = @($createItems | Select-Object Name)
        Remove = @($removeItems | Select-Object Name)
    } | ConvertTo-Json -Compress
    $allowPlan = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($allowJson))
    $arguments = @('-NoProfile', '-NonInteractive', '-File', "`"$PSCommandPath`"",
        '-Action', $Action, '-Configuration', $Configuration, '-ProfilePlanBase64', $profilePlan,
        '-AllowPlanBase64', $allowPlan, '-Elevated')
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
} elseif ($needsChange) {
    [void](New-Item -ItemType Directory -Path $stateDirectory -Force)
    $created = [Collections.Generic.List[string]]::new()
    $removedAllows = [Collections.Generic.List[object]]::new()
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    $complete = $false
    try {
        foreach ($item in $removeItems) {
            $present[$item.Name] | Remove-NetFirewallRule -Confirm:$false
            $removedAllows.Add($item)
        }
        foreach ($item in $createItems) { NewOwnedRule $item $created }
        $complete = $true
    }
    catch {
        foreach ($name in $created) {
            try {
                Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule -Confirm:$false
            } catch { $rollbackErrors.Add("remove-created:$name") }
        }
        foreach ($item in $removedAllows) {
            try {
                if (-not (Get-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name -ErrorAction SilentlyContinue)) {
                    $ignored = [Collections.Generic.List[string]]::new()
                    NewOwnedRule $item $ignored
                }
            } catch { $rollbackErrors.Add("restore-allow:$($item.Name)") }
        }
        throw
    }
    finally {
        [pscustomobject]@{
            Complete = $complete; Action = $Action; Profiles = $profiles; Checkout = $root
            Created = @($created)
            Removed = @($removedAllows | Select-Object -ExpandProperty Name)
            RollbackErrors = @($rollbackErrors)
            RecordedAtUtc = [DateTime]::UtcNow
            Error = if (-not $complete -and $Error.Count -gt 0) { $Error[0].Exception.Message } else { $null }
        } | Export-Clixml -LiteralPath $receiptPath
        if (-not $complete) { Write-Warning "Firewall operation incomplete; exact partial changes are recorded in $receiptPath" }
    }
}
if (-not $Elevated -and $Action -eq 'Enable') {
    Write-Host 'Test access configured. Unchanged rules need no elevation.'
} elseif ($Action -eq 'Remove') {
    Write-Host 'Owned test allow rules removed; unrelated rules preserved.'
}
