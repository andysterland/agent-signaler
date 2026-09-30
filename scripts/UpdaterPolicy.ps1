Set-StrictMode -Version Latest
$script:AgentSignalerUpgradeCodes = @{
    Dashboard = '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
    Remote = '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}'
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
        if ($line -cmatch '^([A-Fa-f0-9]{64})  (AgentSignaler\.(Dashboard|Remote)\.msi)$') {
            if ($checksums.ContainsKey($Matches[2])) { throw 'Duplicate release asset checksum.' }
            $checksums.Add($Matches[2], $Matches[1].ToUpperInvariant())
        }
        elseif (-not [string]::IsNullOrWhiteSpace($line)) { throw 'Unexpected checksum manifest entry.' }
    }
    foreach ($name in @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')) {
        if (-not $checksums.ContainsKey($name)) { throw "Checksum manifest is missing $name." }
    }
    return ,$checksums
}

function Assert-AgentSignalerReleaseAssets($Assets) {
    $expected = @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi', 'SHA256SUMS.txt')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $Assets) {
        if ($expected -cnotcontains [string]$asset.name -or -not $seen.Add([string]$asset.name)) {
            throw 'Unexpected or duplicate release asset.'
        }
    }
    foreach ($name in $expected) {
        Get-AgentSignalerReleaseAsset $Assets $name | Out-Null
    }
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
    if (@('Dashboard', 'Remote') -cnotcontains $App -or
        $Properties['UpgradeCode'] -cne $script:AgentSignalerUpgradeCodes[$App] -or
        $Properties.ContainsKey('ALLUSERS') -or $Template -cnotlike 'x64;*' -or
        ($SummaryFlags -band 8) -eq 0) {
        throw 'Not the expected architecture, identity, scope or elevation policy.'
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

function Get-AgentSignalerUpdateDecision([version] $Installed, [version] $Available, [version] $ReleaseVersion) {
    if ($Available -ne $ReleaseVersion) { throw 'MSI version does not match the release tag.' }
    if ($null -eq $Installed) { return 'NotInstalled' }
    if ($Installed -ge $Available) { return 'NoUpgrade' }
    return 'Upgrade'
}

function Get-AgentSignalerServicingOutcome([int] $ExitCode) {
    if ($ExitCode -eq 0) { return 'Completed' }
    if ($ExitCode -eq 3010) { return 'RestartRequired' }
    throw "Windows Installer servicing failed (exit $ExitCode)."
}
