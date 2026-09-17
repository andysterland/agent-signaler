Set-StrictMode -Version Latest
$script:AgentSignalerUpgradeCodes = @{
    Dashboard = '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
    Remote = '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}'
    RpcHost = '{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}'
}

function ConvertTo-AgentSignalerVersion([string] $Text) {
    if ($Text -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'A canonical three-part MSI version is required.'
    }
    $parts = $Text.Split('.')
    if ($parts[0].Length -gt 3 -or $parts[1].Length -gt 3 -or $parts[2].Length -gt 5 -or
        [int]$parts[0] -gt 255 -or [int]$parts[1] -gt 255 -or [int]$parts[2] -gt 65535) {
        throw 'The release version exceeds MSI limits.'
    }
    return [version]$Text
}

function Read-AgentSignalerChecksums([string[]] $Lines) {
    if ($Lines.Count -gt 128) { throw 'Checksum manifest exceeds its entry limit.' }
    $checksums = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($line in $Lines) {
        if ($line -cmatch '^([A-Fa-f0-9]{64})  (AgentSignaler\.(Dashboard|Remote|RpcHost)\.msi|AgentSignaler\.RpcHost\.(exe|NOTICES\.txt))$') {
            if ($checksums.ContainsKey($Matches[2])) { throw 'Duplicate release asset checksum.' }
            $checksums.Add($Matches[2], $Matches[1].ToUpperInvariant())
        }
        elseif (-not [string]::IsNullOrWhiteSpace($line)) { throw 'Unexpected checksum manifest entry.' }
    }
    foreach ($name in @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi', 'AgentSignaler.RpcHost.msi', 'AgentSignaler.RpcHost.exe')) {
        if (-not $checksums.ContainsKey($name)) { throw "Checksum manifest is missing $name." }
    }
    return ,$checksums
}

function Get-AgentSignalerReleaseAsset($Assets, [string] $Name) {
    $matches = @($Assets | Where-Object { $_.name -ceq $Name })
    if ($matches.Count -ne 1) { throw "The release must contain exactly one $Name asset." }
    $maximum = if ($Name -eq 'SHA256SUMS.txt') { 64KB } else { 1GB }
    if ([long]$matches[0].size -le 0 -or [long]$matches[0].size -gt $maximum) {
        throw 'Release asset size is outside the supported bound.'
    }
    return $matches[0]
}

function Assert-AgentSignalerPackageMetadata($Properties, [string] $Template, [int] $SummaryFlags, [string] $App) {
    if ($Properties['UpgradeCode'] -cne $script:AgentSignalerUpgradeCodes[$App] -or
        $Properties.ContainsKey('ALLUSERS') -or $Template -cnotlike 'x64;*' -or
        (($SummaryFlags -band 8) -ne 0) -ne ($App -ne 'RpcHost')) {
        throw 'Not the expected architecture, identity, scope or elevation policy.'
    }
    if ($App -eq 'RpcHost' -and ($Properties['RPCFIREWALLREQUIRED'] -cne '1' -or
        $Properties['MSIRESTARTMANAGERCONTROL'] -cne 'Disable' -or $Properties['MSIDISABLERMRESTART'] -cne '1')) {
        throw 'RpcHost package does not enforce required firewall and no-shutdown servicing.'
    }
    $code = [guid]::Empty
    if (-not [guid]::TryParse($Properties['ProductCode'], [ref]$code) -or $code -eq [guid]::Empty) {
        throw 'Invalid MSI product code.'
    }
    [pscustomobject]@{
        Version = ConvertTo-AgentSignalerVersion $Properties['ProductVersion']
        ProductCode = $Properties['ProductCode']
    }
}

function Assert-AgentSignalerRpcHostInventory($Inventory, [string] $UserSid, [string] $LocalAppData) {
    $expected = [IO.Path]::GetFullPath((Join-Path $LocalAppData 'Programs\AgentSignaler\RpcHost')).TrimEnd('\')
    $actual = [IO.Path]::GetFullPath([string]$Inventory.InstallDirectory).TrimEnd('\')
    if ($Inventory.UpgradeCode -cne $script:AgentSignalerUpgradeCodes.RpcHost -or
        $Inventory.UserSid -cne $UserSid -or
        -not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Inventory.ReceiverPort -notmatch '^[0-9]{4,5}$' -or
        [int]$Inventory.ReceiverPort -lt 1024 -or [int]$Inventory.ReceiverPort -gt 65535) {
        throw 'RpcHost installed-product metadata is missing or inconsistent. Repair as the original installing user.'
    }
    return $actual
}

function Assert-AgentSignalerRpcHostNotInUse([string] $InstallDirectory, $Processes) {
    $expected = Join-Path $InstallDirectory 'AgentSignaler.RpcHost.exe'
    foreach ($process in $Processes) {
        if (-not $process.IdentityAvailable) {
            if ($process.Name -ieq 'AgentSignaler.RpcHost') {
                throw 'Cannot verify a RpcHost process identity. Stop the installed host in its owning Windows session and retry.'
            }
            continue
        }
        if ($process.SameFile -or [string]::Equals([IO.Path]::GetFullPath([string]$process.Path),
                $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'RpcHost is in use. Explicitly stop the exact installed host in every Windows session before servicing; no shutdown was sent.'
        }
    }
}

function Get-AgentSignalerUpdateDecision([version] $Installed, [version] $Available, [version] $ReleaseVersion) {
    if ($Available -ne $ReleaseVersion) { throw 'MSI version does not match the release tag.' }
    if ($null -eq $Installed) { return 'NotInstalled' }
    if ($Installed -ge $Available) { return 'NoUpgrade' }
    return 'Upgrade'
}

function Get-AgentSignalerServicingOutcome([int] $ExitCode) {
    if ($ExitCode -eq 0) { return 'Completed' }
    if ($ExitCode -eq 3010) { return 'RestartRequired' }
    throw "Windows Installer servicing failed (exit $ExitCode). Elevation denial and required firewall failures are not successful updates."
}
