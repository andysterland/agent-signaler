[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Enable', 'Remove', 'Status')][string] $Action = 'Enable',
    [ValidateSet('Installed', 'Developer', 'Both')][string] $Scope = 'Both',
    [ValidateSet('Release', 'Debug')][string] $Configuration = 'Release',
    [int] $DashboardPort,
    [string] $PlanBase64,
    [string] $InvokingSid,
    [switch] $Elevated
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stateDirectory = Join-Path $root 'artifacts\application-firewall'
$receiptPath = Join-Path $stateDirectory 'last-operation.clixml'
$descriptionPrefix = 'Agent Signaler owned application firewall rule'

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
        elseif ($address -in @('*', 'Any')) { 'Any' }
        else { $address }
    }
}
function AssertPort([int] $Port, [string] $Name) {
    if ($Port -lt 1024 -or $Port -gt 65535) { throw "$Name must be between 1024 and 65535." }
}
function NewExpected([string] $Label, [string] $Program, [int] $Port, [string] $Profile) {
    $programPath = [IO.Path]::GetFullPath($Program)
    $leaf = Split-Path -Leaf $programPath
    if ($leaf -ne 'AgentSignaler.Dashboard.exe') {
        throw "Unsupported firewall executable: $programPath"
    }
    AssertPort $Port 'Receiver port'
    $remoteAddress = if ($Profile -eq 'Private') { 'Any' } elseif ($Profile -eq 'Public') { '127.0.0.1' } else {
        throw "Unsupported firewall profile: $Profile"
    }
    [pscustomobject]@{
        Name = "AgentSignaler.ApplicationFirewall.$((HashText "$programPath|$Profile").Substring(0, 24))"
        DisplayName = "Agent Signaler application ($Profile): $leaf"
        Description = "$descriptionPrefix; program=$programPath; port=$Port; profile=$Profile; remote=$remoteAddress"
        Label = $Label
        Program = $programPath
        Port = $Port
        Profile = $Profile
        RemoteAddress = $remoteAddress
    }
}
function AddProgramRules([Collections.Generic.List[object]] $Items, [string] $Label, [string] $Program, [int] $Port) {
    $Items.Add((NewExpected $Label $Program $Port 'Private'))
    $Items.Add((NewExpected $Label $Program $Port 'Public'))
}
function ReadOwnedRule($Rule) {
    $application = $Rule | Get-NetFirewallApplicationFilter
    $ports = $Rule | Get-NetFirewallPortFilter
    $addresses = $Rule | Get-NetFirewallAddressFilter
    $profile = [string]$Rule.Profile
    $localPorts = @($ports.LocalPort)
    if ($localPorts.Count -ne 1 -or [string]$localPorts[0] -notmatch '^[0-9]{4,5}$') {
        throw "Owned rule has an invalid local-port shape: $($Rule.Name)"
    }
    $port = [int]$localPorts[0]
    $expected = NewExpected 'owned rule' ([string]$application.Program) $port $profile
    if ($Rule.Name -cne $expected.Name -or $Rule.Group -cne $script:group -or
        $Rule.DisplayName -cne $expected.DisplayName -or $Rule.Description -cne $expected.Description -or
        [string]$Rule.Action -ne 'Allow' -or [string]$Rule.Enabled -ne 'True' -or
        [string]$Rule.Direction -ne 'Inbound' -or [string]$Rule.EdgeTraversalPolicy -ne 'Block' -or
        [string]$Rule.PolicyStoreSourceType -ne 'Local' -or [string]$ports.Protocol -ne 'TCP' -or
        -not (SameSet $ports.RemotePort @('Any')) -or
        -not (SameSet (NormalizeAddress $addresses.LocalAddress) @('Any')) -or
        -not (SameSet (NormalizeAddress $addresses.RemoteAddress) @($expected.RemoteAddress))) {
        throw "Rule identity or properties differ; refusing to modify it: $($Rule.Name)"
    }
    $expected
}
function NewOwnedRule($Expected, [Collections.Generic.List[string]] $Created) {
    New-NetFirewallRule -PolicyStore PersistentStore -Name $Expected.Name -DisplayName $Expected.DisplayName `
        -Group $script:group -Description $Expected.Description -Program $Expected.Program -Enabled True `
        -Direction Inbound -Action Allow -Profile $Expected.Profile -Protocol TCP `
        -LocalPort $Expected.Port -LocalAddress Any -RemoteAddress $Expected.RemoteAddress `
        -EdgeTraversalPolicy Block | Out-Null
    $Created.Add($Expected.Name)
    $actual = Get-NetFirewallRule -PolicyStore PersistentStore -Name $Expected.Name
    $verified = ReadOwnedRule $actual
    if ($verified.Port -ne $Expected.Port -or $verified.Program -ine $Expected.Program) {
        throw "Created firewall rule did not match its plan: $($Expected.Name)"
    }
}

if (-not $InvokingSid) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $InvokingSid = $identity.User.Value }
    finally { $identity.Dispose() }
}
if ($InvokingSid -notmatch '^S-1-[0-9-]{3,180}$') { throw 'The invoking user SID is invalid.' }
$group = "AgentSignaler.ApplicationFirewall.v1.$((HashText $InvokingSid).Substring(0, 16))"

$expected = [Collections.Generic.List[object]]::new()
$missing = [Collections.Generic.List[string]]::new()
$approvedPlan = $null
if ($PlanBase64) {
    $planBytes = [Convert]::FromBase64String($PlanBase64)
    if ($planBytes.Length -gt 32768) { throw 'The elevated firewall plan exceeds 32 KiB.' }
    $approvedPlan = [Text.Encoding]::UTF8.GetString($planBytes) | ConvertFrom-Json
    $planExpected = @($approvedPlan.Expected)
    if ($planExpected.Count -gt 4 -or @($approvedPlan.Owned).Count -gt 4 -or
        @($approvedPlan.Remove).Count -gt 4 -or @($approvedPlan.Create).Count -gt 4) {
        throw 'The elevated firewall plan contains too many rules.'
    }
    foreach ($item in $planExpected) {
        $expected.Add((NewExpected ([string]$item.Label) ([string]$item.Program) ([int]$item.Port) ([string]$item.Profile)))
    }
} elseif ($Action -eq 'Enable') {
    AssertPort $DashboardPort 'DashboardPort'
    $programs = [Collections.Generic.List[object]]::new()
    if ($Scope -in @('Installed', 'Both')) {
        $programs.Add([pscustomobject]@{
            Label = 'installed Dashboard'
            Program = Join-Path $env:LOCALAPPDATA 'Programs\AgentSignaler\Dashboard\AgentSignaler.Dashboard.exe'
            Port = $DashboardPort
        })
    }
    if ($Scope -in @('Developer', 'Both')) {
        $windows = 'net10.0-windows10.0.22621.0'
        $programs.Add([pscustomobject]@{
            Label = "developer Dashboard $Configuration"
            Program = Join-Path $root "src\AgentSignaler.Dashboard\bin\x64\$Configuration\$windows\win-x64\AgentSignaler.Dashboard.exe"
            Port = $DashboardPort
        })
    }
    foreach ($program in $programs) {
        if (Test-Path -LiteralPath $program.Program -PathType Leaf) {
            AddProgramRules $expected $program.Label $program.Program $program.Port
        } else {
            $missing.Add("$($program.Label): $($program.Program)")
        }
    }
    foreach ($entry in $missing) { Write-Warning "Executable is not present; no rule can be enabled for $entry" }
    if ($expected.Count -eq 0) { throw 'No selected executable exists. Install or build Dashboard before enabling firewall access.' }
}

$rules = @(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object Group -CEQ $group)
$owned = @{}
foreach ($rule in $rules) {
    $snapshot = ReadOwnedRule $rule
    if ($owned.ContainsKey($snapshot.Name)) { throw "Ambiguous owned rule identity: $($snapshot.Name)" }
    $owned[$snapshot.Name] = $snapshot
}
$expectedByName = @{}
foreach ($item in $expected) {
    if ($expectedByName.ContainsKey($item.Name)) { throw "Duplicate firewall plan identity: $($item.Name)" }
    $expectedByName[$item.Name] = $item
}
$stale = @($owned.Values | Where-Object {
    -not $expectedByName.ContainsKey($_.Name) -or
    $expectedByName[$_.Name].Port -ne $_.Port -or
    $expectedByName[$_.Name].Program -ine $_.Program
})
$staleNames = @($stale | Select-Object -ExpandProperty Name)
$missingRules = @($expected | Where-Object {
    -not $owned.ContainsKey($_.Name) -or $staleNames -contains $_.Name
})
Write-Host "Scope: $Scope; expected rules: $($expected.Count); owned rules: $($owned.Count); stale rules: $($stale.Count); missing executables: $($missing.Count)"
foreach ($item in $owned.Values | Sort-Object Program, Profile) {
    $state = if ($staleNames -contains $item.Name) { 'stale' } else { 'present' }
    Write-Host "$state | $($item.Profile) | TCP $($item.Port) | remote $($item.RemoteAddress) | $($item.Program)"
}
foreach ($item in $missingRules | Sort-Object Program, Profile) {
    Write-Host "absent | $($item.Profile) | TCP $($item.Port) | remote $($item.RemoteAddress) | $($item.Program)"
}
if ($Action -eq 'Status') { return }

if ($approvedPlan) {
    $approvedOwned = @($approvedPlan.Owned | ForEach-Object {
        NewExpected ([string]$_.Label) ([string]$_.Program) ([int]$_.Port) ([string]$_.Profile)
    })
    if ($approvedOwned.Count -ne $owned.Count) { throw 'Owned firewall state changed while awaiting elevation.' }
    foreach ($item in $approvedOwned) {
        if (-not $owned.ContainsKey($item.Name) -or $owned[$item.Name].Port -ne $item.Port -or
            $owned[$item.Name].Program -ine $item.Program) {
            throw "Owned firewall state changed while awaiting elevation: $($item.Name)"
        }
    }
    $remove = @($approvedPlan.Remove | ForEach-Object {
        NewExpected ([string]$_.Label) ([string]$_.Program) ([int]$_.Port) ([string]$_.Profile)
    })
    $create = @($approvedPlan.Create | ForEach-Object {
        NewExpected ([string]$_.Label) ([string]$_.Program) ([int]$_.Port) ([string]$_.Profile)
    })
    foreach ($item in $remove) {
        if (-not $owned.ContainsKey($item.Name) -or $owned[$item.Name].Port -ne $item.Port) {
            throw "Approved removal target changed while awaiting elevation: $($item.Name)"
        }
    }
    foreach ($item in $create) {
        if ($owned.ContainsKey($item.Name)) {
            throw "Approved creation target appeared while awaiting elevation: $($item.Name)"
        }
    }
} else {
    $remove = @(if ($Action -eq 'Enable') { $stale } else { $owned.Values })
    $create = @(if ($Action -eq 'Enable') { $missingRules })
}
if ($remove.Count -eq 0 -and $create.Count -eq 0) {
    Write-Host "No firewall changes are required for action $Action."
    return
}
$target = "$($remove.Count) removal(s), $($create.Count) creation(s); Public access remains IPv4 loopback-only"
if (-not $PSCmdlet.ShouldProcess($target, "$Action Agent Signaler application firewall rules")) { return }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if (-not $administrator) {
    if ($Elevated) { throw 'The elevated helper does not have administrator privileges.' }
    $planJson = [ordered]@{
        Expected = @($expected | Select-Object Label, Program, Port, Profile)
        Owned = @($owned.Values | Select-Object Label, Program, Port, Profile)
        Remove = @($remove | Select-Object Label, Program, Port, Profile)
        Create = @($create | Select-Object Label, Program, Port, Profile)
    } | ConvertTo-Json -Compress
    $encodedPlan = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($planJson))
    $arguments = @('-NoProfile', '-NonInteractive', '-File', "`"$PSCommandPath`"", '-Action', $Action,
        '-Scope', $Scope, '-Configuration', $Configuration, '-PlanBase64', $encodedPlan,
        '-InvokingSid', $InvokingSid, '-Elevated')
    Write-Host 'Requesting one UAC approval for the exact precomputed firewall plan.'
    $helper = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Verb RunAs -ArgumentList $arguments -PassThru
    try {
        if (-not $helper.WaitForExit(120000)) {
            throw "Firewall helper exceeded 120 seconds (PID $($helper.Id)); inspect it before retrying."
        }
        if ($helper.ExitCode -ne 0) {
            throw "Firewall helper failed (exit $($helper.ExitCode)); inspect $receiptPath for partial changes."
        }
    }
    finally { $helper.Dispose() }
} else {
    [void](New-Item -ItemType Directory -Path $stateDirectory -Force)
    $created = [Collections.Generic.List[string]]::new()
    $removed = [Collections.Generic.List[object]]::new()
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    $complete = $false
    try {
        foreach ($item in $remove) {
            Get-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name | Remove-NetFirewallRule -Confirm:$false
            $removed.Add($item)
        }
        foreach ($item in $create) { NewOwnedRule $item $created }
        $complete = $true
    }
    catch {
        foreach ($name in $created) {
            try {
                Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule -Confirm:$false
            } catch { $rollbackErrors.Add("remove-created:$name") }
        }
        foreach ($item in $removed) {
            try {
                if (-not (Get-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name -ErrorAction SilentlyContinue)) {
                    $ignored = [Collections.Generic.List[string]]::new()
                    NewOwnedRule $item $ignored
                }
            } catch { $rollbackErrors.Add("restore-removed:$($item.Name)") }
        }
        throw
    }
    finally {
        [pscustomobject]@{
            Complete = $complete
            Action = $Action
            Scope = $Scope
            InvokingSid = $InvokingSid
            Expected = @($expected | Select-Object Name, Program, Port, Profile, RemoteAddress)
            Created = @($created)
            Removed = @($removed | Select-Object -ExpandProperty Name)
            RollbackErrors = @($rollbackErrors)
            RecordedAtUtc = [DateTime]::UtcNow
            Error = if (-not $complete -and $Error.Count -gt 0) { $Error[0].Exception.Message } else { $null }
        } | Export-Clixml -LiteralPath $receiptPath
    }
}

if (-not $Elevated) {
    $remaining = @(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object Group -CEQ $group)
    if ($Action -eq 'Enable') {
        if ($remaining.Count -ne $expected.Count) { throw 'The enabled firewall rule count differs from the approved plan.' }
        foreach ($rule in $remaining) {
            $actual = ReadOwnedRule $rule
            if (-not $expectedByName.ContainsKey($actual.Name) -or $expectedByName[$actual.Name].Port -ne $actual.Port) {
                throw "The enabled firewall state differs from the approved plan: $($actual.Name)"
            }
        }
    } elseif ($remaining.Count -ne 0) {
        throw 'Owned application firewall rules remain after removal.'
    }
    Write-Host "Application firewall action $Action completed. Public-profile rules allow IPv4 loopback only."
}
