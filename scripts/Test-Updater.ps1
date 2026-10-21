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
$utcJson = ConvertFrom-AgentSignalerStrictJson '{"utc":"2026-10-20T12:00:00.1234567+00:00"}'
Assert ($utcJson.utc -is [string] -and $utcJson.utc -ceq '2026-10-20T12:00:00.1234567+00:00') 'Update JSON must preserve canonical UTC strings on every supported PowerShell host.'
$policyCases = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\update-policy.json')))
Assert ($policyCases.schemaVersion -eq 1 -and $policyCases.repository -ceq 'andysterland/agent-signaler' -and
    $policyCases.authorizedPublisher -ceq $script:AgentSignalerReleasePublisher) 'Shared release policy identity changed.'
foreach ($app in @('Dashboard', 'Remote')) {
    Assert ($policyCases.upgradeCodes.$app -ceq $script:AgentSignalerUpgradeCodes[$app]) 'Shared package upgrade identity changed.'
}
Assert ($policyCases.dependencySet.schemaVersion -eq 1 -and
    $policyCases.dependencySet.policyVersion -eq $script:AgentSignalerUpdaterPolicyVersion -and
    $policyCases.dependencySet.files.Count -eq $script:AgentSignalerUpdaterFiles.Count -and
    @($policyCases.dependencySet.files | Select-Object -Unique).Count -eq $script:AgentSignalerUpdaterFiles.Count) 'Shared updater dependency contract changed.'
foreach ($name in $script:AgentSignalerUpdaterFiles) {
    Assert ($policyCases.dependencySet.files -ccontains $name) 'Shared updater dependency is missing.'
}
foreach ($text in $policyCases.validVersions) {
    Assert ((ConvertTo-AgentSignalerVersion $text).ToString() -ceq $text) 'Shared canonical version was rejected or normalized.'
}
foreach ($text in $policyCases.invalidVersions) { Reject { ConvertTo-AgentSignalerVersion $text } }
foreach ($comparison in $policyCases.versionComparisons) {
    $left = ConvertTo-AgentSignalerVersion $comparison.left
    $right = ConvertTo-AgentSignalerVersion $comparison.right
    Assert ([Math]::Sign($left.CompareTo($right)) -eq $comparison.expected) 'Shared numeric version ordering changed.'
}
Assert-AgentSignalerReleaseAssets $policyCases.validAssets
Assert-AgentSignalerReleaseAssets $policyCases.validBoundaryAssets
foreach ($assetSet in $policyCases.invalidAssetSets) { Reject { Assert-AgentSignalerReleaseAssets $assetSet.assets } }
$handoffPolicyCases = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $root 'tests\fixtures\update-policy-v1.json')))
Assert ($handoffPolicyCases.schemaVersion -eq 1 -and $handoffPolicyCases.policyVersion -eq $script:AgentSignalerUpdaterPolicyVersion -and
    $handoffPolicyCases.clientUpdateExitCode -eq $script:AgentSignalerClientUpdateExitCode -and
    $handoffPolicyCases.publisher -ceq $script:AgentSignalerReleasePublisher -and
    $handoffPolicyCases.codeSigningEku -ceq '1.3.6.1.5.5.7.3.3' -and
    ($handoffPolicyCases.updaterScripts -join ',') -ceq ($script:AgentSignalerUpdaterFiles -join ',')) 'Managed handoff policy fixture differs from PowerShell policy.'
foreach ($app in @('Dashboard', 'Remote')) {
    Assert ($handoffPolicyCases.upgradeCodes.$app -ceq $script:AgentSignalerUpgradeCodes[$app]) 'Managed handoff upgrade identity differs from PowerShell policy.'
}
Assert-AgentSignalerReleaseAssets @($handoffPolicyCases.releaseAssets | ForEach-Object { [pscustomobject]@{ name = $_; size = 1 } })
foreach ($case in $handoffPolicyCases.versions) {
    if ($case.valid) { Assert ((ConvertTo-AgentSignalerVersion $case.text).ToString() -ceq $case.text) 'Managed canonical version differs from PowerShell policy.' }
    else { Reject { ConvertTo-AgentSignalerVersion $case.text } }
}
$signatureCases = @(
    foreach ($case in $policyCases.signatureCases) {
        [pscustomobject]@{ status = $case.status; publisher = $case.publisher; selfIssued = $case.selfIssued;
            hasTimestamp = $case.hasTimestamp; accepted = $case.accepted;
            enhancedKeyUsages = @(if ($case.codeSigningEku) { '1.3.6.1.5.5.7.3.3' }) }
    }
    foreach ($case in $handoffPolicyCases.signatures) {
        [pscustomobject]@{ status = $(if ($case.trustedSignature) { 'Valid' } else { 'NotTrusted' }); publisher = $case.publisher;
            selfIssued = $case.selfSigned; hasTimestamp = $case.timestampValid; accepted = $case.valid;
            enhancedKeyUsages = @($case.enhancedKeyUsages) }
    }
)
foreach ($case in $signatureCases) {
    $certificate = [pscustomobject]@{ Publisher = $case.publisher; Subject = 'CN=Signer';
        Issuer = $(if ($case.selfIssued) { 'CN=Signer' } else { 'CN=Trusted Code Signing CA' }); Extensions = @() }
    if ($case.enhancedKeyUsages.Count -gt 0) {
        $certificate.Extensions = @([pscustomobject]@{ Oid = [pscustomobject]@{ Value = '2.5.29.37' };
            EnhancedKeyUsages = @($case.enhancedKeyUsages | ForEach-Object { [pscustomobject]@{ Value = $_ } }) })
    }
    $certificate | Add-Member ScriptMethod GetNameInfo { param($Type, $Issuer) return $this.Publisher }
    $signature = [pscustomobject]@{ Status = $case.status; SignerCertificate = $certificate;
        TimeStamperCertificate = $(if ($case.hasTimestamp) { [pscustomobject]@{} } else { $null }) }
    if ($case.accepted) { Assert-AgentSignalerSignatureEvidence $signature }
    else { Reject { Assert-AgentSignalerSignatureEvidence $signature } }
}
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
    foreach ($suffix in @("`n", "`r`n")) { Reject { Read-AgentSignalerChecksums @($savedChecksums | ForEach-Object { $_ + $suffix }) } }
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
    foreach ($case in @(@(0, 'Completed'), @(3010, 'RebootRequired'), @(1618, 'InstallerBusy'),
            @(1602, 'Cancelled'), @(1603, 'InstallerFailed'), @(1641, 'InstallerFailed'))) {
        Assert ((Get-AgentSignalerDetailedServicingOutcome $case[0]) -ceq $case[1]) 'Detailed MSI result mismatch.'
    }
    Assert ((Get-AgentSignalerMsiArguments 'C:\owned run\package.msi' 'C:\owned run\install.log') -ceq
        '/i "C:\owned run\package.msi" /passive /norestart /L*v "C:\owned run\install.log" REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable') 'Passive MSI flags changed.'
    foreach ($unsafe in @('C:\a"b.msi', 'C:\a:stream', '\\server\share\a.msi', 'relative.msi')) {
        Reject { Get-AgentSignalerMsiArguments $unsafe 'C:\owned\install.log' }
    }
    Reject { ConvertFrom-AgentSignalerStrictJson '{"schema":1,"schema":2}' }
    Reject { ConvertFrom-AgentSignalerStrictJson '{"schema":1,"\u0073chema":2}' }
    Reject { ConvertFrom-AgentSignalerStrictJson '{"a":{"id":1,"id":2}}' }
    Reject { ConvertFrom-AgentSignalerStrictJson ('[' * 17 + '1' + ']' * 17) }
    Reject { ConvertFrom-AgentSignalerStrictJson '{"x":"oversized"}' 4 }
    Assert ((ConvertFrom-AgentSignalerStrictJson '{"a":[{"id":1},{"id":2}]}').a.Count -eq 2) 'Independent object fields are not duplicates.'
    Reject { Assert-AgentSignalerFields (ConvertFrom-AgentSignalerStrictJson '{"schema":1,"unexpected":2}') @('schema') }
    $certificate = [pscustomobject]@{ Publisher = 'Andy Sterland'; Subject = 'CN=Andy Sterland'; Issuer = 'CN=Trusted Code Signing CA'; Extensions = @(
        [pscustomobject]@{ Oid = [pscustomobject]@{ Value = '2.5.29.37' }; EnhancedKeyUsages = @([pscustomobject]@{ Value = '1.3.6.1.5.5.7.3.3' }) }) }
    $certificate | Add-Member ScriptMethod GetNameInfo { param($Type, $Issuer) return $this.Publisher }
    $evidence = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $certificate; TimeStamperCertificate = [pscustomobject]@{} }
    Assert-AgentSignalerSignatureEvidence $evidence
    foreach ($status in @('NotSigned', 'NotTrusted', 'HashMismatch', 'UnknownError')) {
        $evidence.Status = $status; Reject { Assert-AgentSignalerSignatureEvidence $evidence }
    }
    $evidence.Status = 'Valid'
    $evidence.TimeStamperCertificate = $null; Reject { Assert-AgentSignalerSignatureEvidence $evidence }
    $evidence.TimeStamperCertificate = [pscustomobject]@{}
    $certificate.Publisher = 'Other Publisher'; Reject { Assert-AgentSignalerSignatureEvidence $evidence }
    $certificate.Publisher = 'Andy Sterland'
    $certificate.Issuer = $certificate.Subject; Reject { Assert-AgentSignalerSignatureEvidence $evidence }
    $certificate.Issuer = 'CN=Trusted Code Signing CA'
    $certificate.Extensions = @(); Reject { Assert-AgentSignalerSignatureEvidence $evidence }
    $pinned = [pscustomobject]@{ id = 123; tag_name = 'v1.0.15'; prerelease = $true; assets = @(
        [pscustomobject]@{ id = 101; name = 'AgentSignaler.Dashboard.msi'; size = 100 },
        [pscustomobject]@{ id = 102; name = 'AgentSignaler.Remote.msi'; size = 200 },
        [pscustomobject]@{ id = 103; name = 'SHA256SUMS.txt'; size = 300 }) }
    $remoteRelease = ConvertFrom-AgentSignalerStrictJson ($pinned | ConvertTo-Json -Depth 6)
    $remoteRelease | Add-Member NoteProperty draft $false
    Assert-AgentSignalerPinnedRelease $remoteRelease $pinned
    foreach ($field in @('id', 'tag_name', 'prerelease')) {
        $saved = $remoteRelease.$field
        $remoteRelease.$field = switch ($field) { id { 124 }; tag_name { 'v1.0.16' }; prerelease { $false } }
        Reject { Assert-AgentSignalerPinnedRelease $remoteRelease $pinned }; $remoteRelease.$field = $saved
    }
    foreach ($asset in $remoteRelease.assets) {
        foreach ($field in @('id', 'size')) {
            $saved = $asset.$field; $asset.$field = $saved + 1
            Reject { Assert-AgentSignalerPinnedRelease $remoteRelease $pinned }; $asset.$field = $saved
        }
    }
    $remoteRelease.draft = $true; Reject { Assert-AgentSignalerPinnedRelease $remoteRelease $pinned }
    Assert ((Get-AgentSignalerUpdateDecision $null ([version]'1.0.15') ([version]'1.0.15')) -ceq 'NotInstalled') 'Missing installation must be a no-op.'
    Write-Host 'Updater policy and non-mutating fixture -WhatIf passed for Dashboard/Remote: inventory, exact reduced assets, retired-asset rejection, hashes, size, identity, architecture, scope/version, no-op and no downgrade. No network, inventory access, process starts or servicing.'
}
finally { Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force }
& (Join-Path $PSScriptRoot 'Test-UpdaterHandoff.ps1')
