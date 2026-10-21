Set-StrictMode -Version Latest
$script:AgentSignalerUpdaterPolicyVersion = 1
$script:AgentSignalerUpdaterFiles = @('Update-AgentSignaler.ps1', 'UpdaterPolicy.ps1', 'UpdateHandoff.ps1')
$script:AgentSignalerReleasePublisher = 'Andy Sterland'
$script:AgentSignalerClientUpdateExitCode = 42
$script:AgentSignalerUpgradeCodes = @{
    Dashboard = '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
    Remote = '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}'
}

function Assert-AgentSignalerInteger($Value, [long] $Minimum = 1, [long] $Maximum = [long]::MaxValue) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum -or $Value -gt $Maximum) {
        throw 'Update identity must be a bounded JSON integer.'
    }
}

function ConvertTo-AgentSignalerVersion([string] $Text) {
    if ($Text -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
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
        if ($line -cmatch '\A([A-Fa-f0-9]{64})  (AgentSignaler\.(Dashboard|Remote)\.msi)\z') {
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

function Assert-AgentSignalerSignatureEvidence($Signature) {
    if ([string]$Signature.Status -cne 'Valid' -or $null -eq $Signature.SignerCertificate -or
        $null -eq $Signature.TimeStamperCertificate -or
        $Signature.SignerCertificate.Subject -ceq $Signature.SignerCertificate.Issuer -or
        $Signature.SignerCertificate.GetNameInfo(
            [Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $script:AgentSignalerReleasePublisher) {
        throw 'Updater publisher or timestamp verification failed.'
    }
    $eku = @($Signature.SignerCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($eku.Count -ne 1 -or @($eku[0].EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -ne 1) {
        throw 'Updater signer is not authorized for code signing.'
    }
}

function Assert-AgentSignalerPublisher([string] $Path) {
    # WinVerifyTrust, exposed by Authenticode, validates the trusted chain at the signed timestamp.
    Assert-AgentSignalerSignatureEvidence (Get-AuthenticodeSignature -LiteralPath $Path)
}

function Write-AgentSignalerUpdaterDependencies([string] $Directory) {
    $files = @(
        foreach ($name in $script:AgentSignalerUpdaterFiles) {
            $path = Join-Path $Directory $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Updater dependency is missing.' }
            [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        }
    )
    [ordered]@{ schemaVersion = 1; policyVersion = $script:AgentSignalerUpdaterPolicyVersion; files = $files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'UpdaterDependencies.json') -Encoding UTF8
}

function ConvertFrom-AgentSignalerStrictJson([string] $Json, [int] $MaximumBytes = 262144) {
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt $MaximumBytes) { throw 'Update JSON exceeds its bound.' }
    $stack = [Collections.Generic.Stack[object]]::new()
    # ConvertFrom-Json alone accepts duplicate properties. Inspect every object before conversion.
    $tokens = [regex]::Matches($Json, '"(?:[^"\\]|\\.)*"|[{}\[\]:,]')
    for ($index = 0; $index -lt $tokens.Count; $index++) {
        $text = $tokens[$index].Value
        if ($text -eq '{') {
            $stack.Push([Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase))
            if ($stack.Count -gt 16) { throw 'Update JSON exceeds its depth bound.' }
        }
        elseif ($text -eq '[') {
            $stack.Push($null)
            if ($stack.Count -gt 16) { throw 'Update JSON exceeds its depth bound.' }
        }
        elseif ($text -in @('}', ']')) {
            if ($stack.Count -eq 0) { throw 'Invalid update JSON nesting.' }
            [void]$stack.Pop()
        }
        elseif ($text.StartsWith('"') -and $index + 1 -lt $tokens.Count -and $tokens[$index + 1].Value -eq ':') {
            $name = ConvertFrom-Json ('[' + $text + ']')
            if ($stack.Count -eq 0 -or $null -eq $stack.Peek() -or -not $stack.Peek().Add([string]$name)) {
                throw 'Duplicate or invalid update JSON property.'
            }
        }
    }
    if ($stack.Count -ne 0) { throw 'Invalid update JSON nesting.' }
    $conversion = @{}
    if ((Get-Command ConvertFrom-Json -CommandType Cmdlet).Parameters.ContainsKey('DateKind')) {
        $conversion.DateKind = 'String'
    }
    return ConvertFrom-Json -InputObject $Json @conversion
}

function Assert-AgentSignalerFields($Value, [string[]] $Fields) {
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Fields.Count -or @($actual | Where-Object { $Fields -cnotcontains $_ }).Count -ne 0) {
        throw 'Unsupported update schema fields.'
    }
}

function Assert-AgentSignalerPinnedRelease($Release, $Pinned) {
    Assert-AgentSignalerReleaseAssets @($Release.assets)
    if ([long]$Release.id -ne [long]$Pinned.id -or $Release.tag_name -cne $Pinned.tag_name -or
        $Release.draft -or $Release.prerelease -ne $Pinned.prerelease) {
        throw 'The selected release identity changed; check for updates again.'
    }
    [void](ConvertTo-AgentSignalerVersion $Release.tag_name.Substring(1))
    Assert-AgentSignalerReleaseAssets @($Pinned.assets)
    foreach ($pin in $Pinned.assets) {
        $asset = Get-AgentSignalerReleaseAsset @($Release.assets) $pin.name
        if ([long]$pin.id -le 0 -or [long]$asset.id -ne [long]$pin.id -or [long]$asset.size -ne [long]$pin.size) {
            throw 'The selected release asset was replaced; check for updates again.'
        }
    }
}

function Get-AgentSignalerMsiArguments([string] $PackagePath, [string] $LogPath) {
    foreach ($path in @($PackagePath, $LogPath)) {
        if ($path -notmatch '\A[A-Za-z]:\\' -or $path -match '["\x00-\x1f]' -or $path.Substring(2).Contains(':')) {
            throw 'Unsafe installer path.'
        }
    }
    return "/i `"$PackagePath`" /passive /norestart /L*v `"$LogPath`" REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable"
}

function Get-AgentSignalerDetailedServicingOutcome([int] $ExitCode) {
    switch ($ExitCode) {
        0 { return 'Completed' }
        3010 { return 'RebootRequired' }
        1618 { return 'InstallerBusy' }
        1602 { return 'Cancelled' }
        default { return 'InstallerFailed' }
    }
}
