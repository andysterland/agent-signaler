[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalSigning.ps1')

function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected 'Expected signing fixture to fail closed.'
}
function Write-FixtureFile([string] $Path, [string] $Text) {
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force)
    [IO.File]::WriteAllText($Path, $Text)
}
function Get-FixtureSnapshot([string] $Directory) {
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName |
        ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n")
}

$root = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $root ('artifacts\local-signing-tests\' + [guid]::NewGuid().ToString('N'))
$script:trace = [Collections.Generic.List[string]]::new()
$script:mode = ''
$script:fixture = ''
$script:destination = ''
$script:destinationChecks = 0
$script:copyCalls = 0
$script:copyInjected = $false
$script:binaryTargets = @(
    'publish\dashboard\AgentSignaler.Dashboard.exe',
    'publish\dashboard\AgentSignaler.Contracts.dll',
    'publish\remote\AgentSignaler.Client.exe',
    'publish\remote\nested\AgentSignaler.Remote.dll')
$script:msiTargets = @('msi\AgentSignaler.Dashboard.msi', 'msi\AgentSignaler.Remote.msi')
$options = @{
    Version = '1.2.3'; Configuration = 'Debug'; NoRestore = $true
    Endpoint = 'https://test.codesigning.azure.net/'; Account = 'synthetic-account'; Profile = 'synthetic-profile'
}
$resolveTool = ${function:Resolve-LocalSigningTool}
$resolveAz = ${function:Resolve-LocalSigningAzureCli}
$native = ${function:Invoke-LocalSigningNative}
$build = ${function:Invoke-LocalSigningBuild}
$removeOutput = ${function:Remove-LocalSigningOutput}
$destinationCheck = ${function:Assert-SignedMsiDestination}
$copyStream = ${function:Copy-SignedMsiStream}
$unchanged = ${function:Assert-SignedMsiFileUnchanged}

function Assert-SignedMsiDestination {
    param([string] $Path, [switch] $AllowActiveCopy)
    $script:destinationChecks++
    Assert (-not $Path.StartsWith('\\')) 'A synthetic test attempted to access a real UNC destination.'
    & $destinationCheck $Path -AllowActiveCopy:$AllowActiveCopy
}
function Copy-SignedMsiStream {
    param([IO.Stream] $Source, [IO.Stream] $Destination)
    $script:copyCalls++
    Assert ($Destination.Name.StartsWith($script:destination + '\')) 'Destination override was not forwarded.'
    Assert (Test-Path -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt')) 'Copy began before final checksums existed.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:fixture 'artifacts\signing-staging\active.lock'))) 'Copy began before local signing committed.'
    $script:trace.Add("copy:$([IO.Path]::GetFileName($Source.Name))")
    if ($script:mode -eq 'copy-stream-fails' -and $script:copyCalls -eq 2) {
        $Destination.WriteByte(42)
        throw 'Synthetic interrupted remote copy.'
    }
    if ($script:mode -eq 'copy-hash-mismatch' -and $script:copyCalls -eq 2) {
        $Destination.WriteByte(42)
        return
    }
    & $copyStream $Source $Destination
}
function Assert-SignedMsiFileUnchanged {
    param([string] $Path, $Stamp)
    & $unchanged $Path $Stamp
    $remote = Join-Path $script:destination 'AgentSignaler.Remote.msi'
    if (-not $script:copyInjected -and $script:mode -in @('copy-mid-commit', 'copy-concurrent-edit') -and $Path -eq $remote -and
        $Stamp.Hash -ceq (Get-SignedMsiHash (Join-Path $script:fixture 'artifacts\msi\AgentSignaler.Remote.msi'))) {
        $script:copyInjected = $true
        if ($script:mode -eq 'copy-concurrent-edit') {
            [IO.File]::WriteAllText((Join-Path $script:destination 'AgentSignaler.Dashboard.msi'), 'concurrent foreign edit')
        }
        throw 'Synthetic failure after both promotions.'
    }
}

function Resolve-LocalSigningTool {
    param([string] $ExplicitPath, [string] $Name)
    $script:trace.Add("resolve:$Name")
    if ($script:mode -eq 'missing-tool') { throw 'Synthetic missing tool.' }
    return Join-Path $script:fixture $Name
}
function Resolve-LocalSigningAzureCli { return Join-Path $script:fixture 'az.cmd' }
function Invoke-LocalSigningBuild {
    param([string] $Root, [string] $Version, [string] $Configuration, [bool] $NoRestore, [bool] $Publish)
    Assert ($Version -ceq '1.2.3' -and $Configuration -ceq 'Debug' -and $NoRestore) 'Build options were not forwarded.'
    $output = Join-Path $Root 'artifacts'
    if ($Publish) {
        $script:trace.Add('publish')
        if ($script:mode -eq 'backup-hardlinks') {
            Get-ChildItem -LiteralPath (Join-Path $Root 'cache-links') -File |
                ForEach-Object { [IO.File]::WriteAllText($_.FullName, 'cache rebuild writes') }
        }
        foreach ($name in $script:binaryTargets) {
            $file = Join-Path $output $name
            Write-FixtureFile $file 'unsigned binary'
            if ($script:mode -eq 'hardlinks') {
                $link = Join-Path $Root ('unsigned-links\' + [IO.Path]::GetFileName($file))
                [void](New-Item -ItemType Directory -Path (Split-Path -Parent $link) -Force)
                [void](New-Item -ItemType HardLink -Path $link -Target $file)
            }
        }
        foreach ($name in @('publish\dashboard\Microsoft.Runtime.dll', 'publish\remote\Vendor.exe',
                'publish\dashboard\AgentSignaler.Dashboard.deps.json', 'publish\remote\AgentSignaler.NotExecutable.txt')) {
            Write-FixtureFile (Join-Path $output $name) 'do not sign'
        }
        foreach ($directory in @('dashboard', 'remote')) {
            foreach ($name in $script:AgentSignalerUpdaterFiles) {
                Write-FixtureFile (Join-Path $output "publish\$directory\$name") 'unsigned script'
            }
        }
        if ($script:mode -eq 'publish-fails') { throw 'Synthetic publish failure.' }
    }
    else {
        $script:trace.Add('package')
        foreach ($name in $script:binaryTargets) {
            Assert ((Get-Content -LiteralPath (Join-Path $output $name) -Raw) -match 'signed$') 'Packaging began before every binary was signed.'
        }
        foreach ($directory in @('dashboard', 'remote')) {
            $payload = Join-Path $output "publish\$directory"
            $descriptor = Get-Content -LiteralPath (Join-Path $payload 'UpdaterDependencies.json') -Raw | ConvertFrom-Json
            foreach ($name in $script:AgentSignalerUpdaterFiles) {
                Assert ((Get-Content -LiteralPath (Join-Path $payload $name) -Raw) -match 'signed$') 'Packaging began before updater signing.'
                $entry = @($descriptor.files | Where-Object name -CEQ $name)
                Assert ($entry.Count -eq 1 -and $entry[0].sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $payload $name)).Hash) 'Signed updater dependency hash was not regenerated.'
            }
        }
        foreach ($name in $script:msiTargets) {
            $file = Join-Path $output $name
            Write-FixtureFile $file 'unsigned msi'
            if ($script:mode -eq 'hardlinks') {
                [void](New-Item -ItemType HardLink -Path (Join-Path $Root ('unsigned-links\' + [IO.Path]::GetFileName($file))) -Target $file)
            }
        }
        if ($script:mode -eq 'package-fails') { throw 'Synthetic package failure.' }
    }
}
function Invoke-LocalSigningNative {
    param([string] $Path, [string[]] $Arguments)
    if ($Arguments[0] -eq 'account') {
        Assert (($Arguments -join ' ') -ceq 'account show --output none --only-show-errors') 'Azure preflight must not acquire/output tokens or log in.'
        Assert ($env:PATH.StartsWith((Split-Path -Parent $Path) + ';')) 'Azure CLI directory was not made available to AzureCliCredential.'
        $script:trace.Add('auth')
        if ($script:mode -eq 'auth-fails') { throw 'Synthetic credential failure.' }
        return
    }
    $target = $Arguments[-1]
    if ($Arguments[0] -eq 'verify' -and $target.StartsWith($script:destination + '\')) {
        Assert ([IO.Path]::GetFileName($target) -in @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')) 'Unexpected destination signature target.'
        $script:trace.Add('verify-destination:' + [IO.Path]::GetFileName($target))
        return
    }
    $relative = $target.Substring((Join-Path $script:fixture 'artifacts').Length + 1)
    if ($relative.StartsWith('signing-staging\')) {
        Assert ([IO.Path]::GetFileName($target).StartsWith('pending-')) 'Unexpected staged signing path.'
        $name = [IO.Path]::GetFileName($target).Substring('pending-'.Length)
        if ($name -in $script:AgentSignalerUpdaterFiles) { $relative = 'updater\' + $name }
        else {
        $matches = @($script:binaryTargets + $script:msiTargets | Where-Object { [IO.Path]::GetFileName($_) -ceq $name })
        Assert ($matches.Count -eq 1) 'Staged target is not one of the selected binaries/MSIs.'
        $relative = $matches[0]
        }
    }
    Assert ((($script:binaryTargets + $script:msiTargets) -contains $relative) -or
        ([IO.Path]::GetFileName($target) -replace '^pending-', '') -in $script:AgentSignalerUpdaterFiles) 'Signing escaped the exact first-party/MSI fixture allowlist.'
    $script:trace.Add("$($Arguments[0]):$relative")
    if ($Arguments[0] -eq 'sign') {
        Assert ($Arguments.Count -eq 12 -and $Arguments[1] -ceq '/fd' -and $Arguments[2] -ceq 'SHA256' -and
            $Arguments[3] -ceq '/tr' -and $Arguments[4] -ceq 'http://timestamp.acs.microsoft.com' -and
            $Arguments[5] -ceq '/td' -and $Arguments[6] -ceq 'SHA256' -and
            $Arguments[7] -ceq '/dlib' -and $Arguments[9] -ceq '/dmdf') 'Signing must request SHA256 and an RFC3161 timestamp.'
        $metadata = Get-Content -LiteralPath $Arguments[10] -Raw | ConvertFrom-Json
        Assert ($metadata.Endpoint -ceq $options.Endpoint -and $metadata.CodeSigningAccountName -ceq $options.Account -and
            $metadata.CertificateProfileName -ceq $options.Profile) 'Signing metadata inputs changed.'
        $excluded = @('EnvironmentCredential', 'WorkloadIdentityCredential', 'ManagedIdentityCredential',
            'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
            'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
        Assert (($metadata.ExcludeCredentials -join ',') -ceq ($excluded -join ',')) 'Only AzureCliCredential may be enabled.'
        [IO.File]::AppendAllText($target, ' signed')
        if ($script:mode -eq 'binary-sign-fails' -and $relative -eq $script:binaryTargets[0]) { throw 'Synthetic partial signing failure.' }
        if ($script:mode -eq 'msi-sign-fails' -and $relative -eq $script:msiTargets[1]) { throw 'Synthetic partial MSI signing failure.' }
        if ($script:mode -eq 'rollback-fails') { throw 'Synthetic failure followed by blocked rollback.' }
        if ($script:mode -eq 'backup-hardlinks') { throw 'Synthetic failure after cache rebuild.' }
        if ($script:mode -eq 'cancelled') { throw [OperationCanceledException]::new('Synthetic cancellation.') }
    }
    else {
        Assert (($Arguments[0..3] -join ' ') -ceq 'verify /pa /all /tw') 'Verification must require all signatures and timestamps.'
        if ($script:mode -eq 'verify-fails') { throw 'Synthetic verification failure.' }
    }
}
function Get-AuthenticodeSignature {
    param([string] $LiteralPath)
    $certificate = [pscustomobject]@{ Publisher = 'Andy Sterland' }
    if ($script:mode -eq 'wrong-publisher' -or
        ($script:mode -eq 'msi-wrong-publisher' -and $LiteralPath.EndsWith('.msi'))) {
        $certificate.Publisher = 'Untrusted Publisher'
    }
    if ($script:mode -eq 'tool-certificate') { $certificate.Publisher = 'Microsoft Corporation' }
    $certificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($Type, $Issuer); return $this.Publisher }
    [pscustomobject]@{
        Status = if ($script:mode -eq 'invalid-signature') { 'HashMismatch' }
            elseif ($script:mode -eq 'foreign-destination' -and $LiteralPath.StartsWith($script:destination + '\')) { 'NotSigned' }
            elseif ($script:mode -eq 'unsigned-tools') { 'NotSigned' } else { 'Valid' }
        SignerCertificate = if ($script:mode -eq 'unsigned-tools') { $null } else { $certificate }
        TimeStamperCertificate = if ($script:mode -eq 'missing-timestamp') { $null } else { $certificate }
    }
}
function Remove-LocalSigningOutput {
    param([string] $Path, [string] $Root)
    if ($script:mode -eq 'rollback-fails' -and $Path.EndsWith('\publish\dashboard')) {
        throw 'Synthetic locked payload prevents rollback.'
    }
    & $removeOutput $Path $Root
}
function New-SigningFixture([string] $Name, [bool] $Previous = $true) {
    $script:mode = $Name
    $script:trace.Clear()
    $script:copyCalls = 0
    $script:copyInjected = $false
    $script:destinationChecks = 0
    $script:fixture = Join-Path $fixtureRoot ($Name + '-' + [guid]::NewGuid().ToString('N'))
    $script:destination = $script:fixture + '-destination'
    $options.DestinationPath = $script:destination
    Write-FixtureFile (Join-Path $script:destination 'unrelated.txt') 'preserve unrelated destination file'
    Write-FixtureFile (Join-Path $script:fixture 'scripts\Build-Installers.ps1') "throw 'Never run real builds in synthetic tests.'"
    Write-FixtureFile (Join-Path $script:fixture 'artifacts\msi\AgentSignaler.RpcHost.msi') 'preserve retired MSI'
    Write-FixtureFile (Join-Path $script:fixture 'artifacts\msi\AgentSignaler.DevTunnelsPrerequisite.msi') 'preserve prerequisite'
    Write-FixtureFile (Join-Path $script:fixture 'artifacts\bundle\AgentSignaler.Dashboard.Setup.exe') 'preserve bundle'
    if ($Previous) {
        foreach ($name in $script:binaryTargets + $script:msiTargets) {
            Write-FixtureFile (Join-Path $script:fixture "artifacts\$name") 'previous unsigned payload'
        }
        $script:msiTargets | ForEach-Object {
            $path = Join-Path $script:fixture "artifacts\$_"
            "$((Get-FileHash -LiteralPath $path).Hash)  $([IO.Path]::GetFileName($path))"
        } | Set-Content -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt') -Encoding ascii
    }
}

try {
    New-SigningFixture 'success'
    $originalPath = $env:PATH
    Invoke-AgentSignalerLocalSigning -Root $script:fixture @options
    Assert ($env:PATH -ceq $originalPath) 'Process PATH was not restored.'
    Assert ($script:trace.IndexOf('auth') -lt $script:trace.IndexOf('publish')) 'Auth must precede destructive publishing.'
    $packageIndex = $script:trace.IndexOf('package')
    foreach ($name in $script:binaryTargets) {
        Assert ($script:trace.IndexOf("sign:$name") -gt $script:trace.IndexOf('publish') -and
            $script:trace.IndexOf("verify:$name") -lt $packageIndex) 'Binary signing/verification must precede packaging.'
    }
    foreach ($name in $script:msiTargets) {
        Assert ($script:trace.IndexOf("sign:$name") -gt $packageIndex) 'MSIs must be signed after packaging.'
    }
    Assert (@($script:trace | Where-Object { $_ -like 'sign:*' }).Count -eq 12) 'Wrong signing target count.'
    $checksums = Read-AgentSignalerChecksums @(Get-Content -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt'))
    foreach ($name in $script:msiTargets) {
        Assert ($checksums[[IO.Path]::GetFileName($name)] -ceq
            (Get-FileHash -LiteralPath (Join-Path $script:fixture "artifacts\$name")).Hash) 'Manifest does not hash final signed MSIs.'
    }
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $script:fixture 'artifacts\signing-staging') -Force).Count -eq 0) 'Successful signing left metadata/backups.'
    Assert ((Get-Content -LiteralPath (Join-Path $script:fixture 'artifacts\publish\remote\Vendor.exe')) -ceq 'do not sign') 'Vendor binaries were altered.'
    Assert ((Get-Content -LiteralPath (Join-Path $script:fixture 'artifacts\msi\AgentSignaler.RpcHost.msi')) -ceq 'preserve retired MSI') 'Unselected MSI was altered.'
    Assert ((Get-Content -LiteralPath (Join-Path $script:fixture 'artifacts\bundle\AgentSignaler.Dashboard.Setup.exe')) -ceq 'preserve bundle') 'Bundle was altered.'
    Assert (@(Get-ChildItem -LiteralPath $script:destination -File).Count -eq 3) 'Copy delivered anything other than the two selected MSIs.'
    foreach ($name in $script:msiTargets) {
        $leaf = [IO.Path]::GetFileName($name)
        Assert ((Get-SignedMsiHash (Join-Path $script:destination $leaf)) -ceq $checksums[$leaf]) 'Delivered MSI differs from final signed manifest.'
        Assert ($script:trace.IndexOf("copy:$leaf") -gt $script:trace.LastIndexOf("sign:$name") -and
            $script:trace.IndexOf("copy:$leaf") -gt $script:trace.LastIndexOf("verify:$name")) 'Copy preceded final signature verification.'
    }

    New-SigningFixture 'hardlinks'
    Invoke-AgentSignalerLocalSigning -Root $script:fixture @options
    $linkedFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:fixture 'unsigned-links') -File)
    Assert ($linkedFiles.Count -eq 6) 'Hard-link fixture did not cover every selected binary/MSI.'
    foreach ($file in $linkedFiles) {
        Assert ((Get-Content -LiteralPath $file.FullName -Raw) -in @('unsigned binary', 'unsigned msi')) 'Signing modified another hard-link name.'
    }
    New-SigningFixture 'backup-hardlinks'
    $cacheLinks = Join-Path $script:fixture 'cache-links'
    [void](New-Item -ItemType Directory -Path $cacheLinks)
    foreach ($name in $script:binaryTargets + $script:msiTargets) {
        [void](New-Item -ItemType HardLink -Path (Join-Path $cacheLinks ([IO.Path]::GetFileName($name))) -Target (Join-Path $script:fixture "artifacts\$name"))
    }
    Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
    foreach ($name in $script:binaryTargets + $script:msiTargets) {
        Assert ((Get-Content -LiteralPath (Join-Path $script:fixture "artifacts\$name") -Raw) -ceq 'previous unsigned payload') 'Rebuild changed a hard-linked rollback backup.'
    }

    foreach ($failure in @('missing-tool', 'auth-fails', 'publish-fails', 'package-fails', 'binary-sign-fails',
            'msi-sign-fails', 'verify-fails', 'wrong-publisher', 'invalid-signature', 'missing-timestamp',
            'msi-wrong-publisher', 'cancelled')) {
        New-SigningFixture $failure
        $before = Get-FixtureSnapshot $script:fixture
        Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
        Assert ((Get-FixtureSnapshot $script:fixture) -ceq $before) "Failure $failure did not restore original payloads and checksums."
        Assert ($env:PATH -ceq $originalPath) 'Failure did not restore PATH.'
        if ($failure -notin @('msi-sign-fails', 'package-fails', 'msi-wrong-publisher')) {
            Assert (-not $script:trace.Contains('package')) 'Unverified payload reached packaging.'
        }
    }
    New-SigningFixture 'binary-sign-fails' $false
    Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt'))) 'Failed fresh run left a success manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:fixture 'artifacts\publish\dashboard'))) 'Failed fresh run left partially signed output.'

    New-SigningFixture 'rollback-fails'
    Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt'))) 'Failed rollback restored a misleading manifest.'
    $recovery = @(Get-ChildItem -LiteralPath (Join-Path $script:fixture 'artifacts\signing-staging') -Recurse -File)
    Assert (@($recovery | Where-Object Name -eq 'metadata.json').Count -eq 0) 'Failed rollback retained private signing metadata.'
    Assert (@($recovery | Where-Object Name -eq 'recovery.json').Count -eq 1 -and
        @($recovery | Where-Object Name -eq 'backup-0').Count -eq 1) 'Failed rollback discarded recovery records or original checksums.'
    $script:mode = 'retry'; $script:trace.Clear()
    Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
    Assert (-not $script:trace.Contains('publish')) 'Unfinished transaction did not block a new build.'
    Assert ($env:PATH -ceq $originalPath) 'Rollback failure did not restore PATH.'

    foreach ($mode in @('copy-success', 'copy-stream-fails', 'copy-hash-mismatch', 'copy-mid-commit', 'copy-concurrent-edit', 'foreign-destination')) {
        New-SigningFixture $mode
        foreach ($name in $script:msiTargets) {
            Write-FixtureFile (Join-Path $script:destination ([IO.Path]::GetFileName($name))) 'prior destination signed MSI'
        }
        $localBefore = Get-FixtureSnapshot $script:fixture
        $destinationBefore = Get-FixtureSnapshot $script:destination
        $copyArguments = @{ Root = $script:fixture; DestinationPath = $script:destination }
        if ($mode -eq 'copy-success') {
            Copy-AgentSignalerSignedMsis @copyArguments
            foreach ($name in $script:msiTargets) {
                Assert ((Get-SignedMsiHash (Join-Path $script:destination ([IO.Path]::GetFileName($name)))) -ceq
                    (Get-SignedMsiHash (Join-Path $script:fixture "artifacts\$name"))) 'Existing target was not replaced with the verified copy.'
            }
            Assert (@(Get-ChildItem -LiteralPath $script:destination -Force).Count -eq 3) 'Successful copy retained staging/backup/journal files.'
        }
        elseif ($mode -eq 'copy-concurrent-edit') {
            $message = ''
            try { Copy-AgentSignalerSignedMsis @copyArguments } catch { $message = $_.Exception.Message }
            Assert ($message.Contains('mixed set') -and $message.Contains('.AgentSignaler-copy.lock') -and
                $message.Contains('-backup-AgentSignaler.Dashboard.msi')) 'Rollback conflict did not report recovery paths.'
            Assert ((Get-Content -LiteralPath (Join-Path $script:destination 'AgentSignaler.Dashboard.msi') -Raw) -ceq 'concurrent foreign edit') 'Concurrent foreign edit was overwritten/deleted.'
            $saved = @(Get-ChildItem -LiteralPath $script:destination -Filter '*-backup-AgentSignaler.Dashboard.msi' -File)
            Assert ($saved.Count -eq 1 -and (Get-Content -LiteralPath $saved[0].FullName -Raw) -ceq 'prior destination signed MSI') 'Recovery discarded the destination original.'
            Reject { Copy-AgentSignalerSignedMsis @copyArguments }
        }
        else {
            Reject { Copy-AgentSignalerSignedMsis @copyArguments }
            Assert ((Get-FixtureSnapshot $script:destination) -ceq $destinationBefore) "$mode did not preserve/restore destination originals."
        }
        Assert ((Get-FixtureSnapshot $script:fixture) -ceq $localBefore) 'Copy-only helper changed local signed outputs.'
        Assert ((Get-Content -LiteralPath (Join-Path $script:destination 'unrelated.txt') -Raw) -ceq 'preserve unrelated destination file') 'Copy touched an unrelated destination file.'
        Assert (-not $script:trace.Contains('auth') -and -not $script:trace.Contains('publish') -and -not $script:trace.Contains('package') -and
            @($script:trace | Where-Object { $_ -like 'sign:*' }).Count -eq 0) 'Copy-only helper authenticated, signed or built.'
    }
    foreach ($mode in @('copy-source-mismatch', 'copy-extra-checksum', 'wrong-publisher', 'missing-timestamp')) {
        New-SigningFixture $mode
        if ($mode -eq 'copy-source-mismatch') {
            [IO.File]::AppendAllText((Join-Path $script:fixture 'artifacts\msi\AgentSignaler.Remote.msi'), 'tampered')
        }
        if ($mode -eq 'copy-extra-checksum') {
            Add-Content -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt') -Value (('A' * 64) + '  AgentSignaler.RpcHost.msi')
        }
        $localBefore = Get-FixtureSnapshot $script:fixture
        $destinationBefore = Get-FixtureSnapshot $script:destination
        Reject { Copy-AgentSignalerSignedMsis -Root $script:fixture -DestinationPath $script:destination }
        Assert ($script:copyCalls -eq 0 -and (Get-FixtureSnapshot $script:fixture) -ceq $localBefore -and
            (Get-FixtureSnapshot $script:destination) -ceq $destinationBefore) 'Unverified sources changed local or destination files.'
    }
    New-SigningFixture 'copy-stream-fails'
    $message = ''
    try { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options } catch { $message = $_.Exception.Message }
    Assert ($message.Contains('Local signing committed successfully')) 'Copy failure did not distinguish successful local signing.'
    $checksums = Read-AgentSignalerChecksums @(Get-Content -LiteralPath (Join-Path $script:fixture 'artifacts\msi\SHA256SUMS.txt'))
    foreach ($name in $script:msiTargets) {
        $path = Join-Path $script:fixture "artifacts\$name"
        Assert ((Get-Content -LiteralPath $path -Raw) -ceq 'unsigned msi signed' -and
            (Get-SignedMsiHash $path) -ceq $checksums[[IO.Path]::GetFileName($name)]) 'Copy failure rolled back valid local signing.'
    }
    Assert (@(Get-ChildItem -LiteralPath $script:destination -Force).Count -eq 1) 'Failed delivery left a mixed new destination set.'

    New-SigningFixture 'whatif'
    $before = Get-FixtureSnapshot $script:fixture
    $pathsBefore = @(Get-ChildItem -LiteralPath $script:fixture -Recurse -Force).Count
    Invoke-AgentSignalerLocalSigning -Root $script:fixture @options -WhatIf
    $previewOptions = $options.Clone(); $previewOptions.Remove('DestinationPath')
    Invoke-AgentSignalerLocalSigning -Root $script:fixture @previewOptions -WhatIf
    Copy-AgentSignalerSignedMsis -Root $script:fixture -WhatIf
    Assert ($script:destinationChecks -eq 0) 'WhatIf inspected a local/remote destination.'
    Assert ($script:trace.Count -eq 0 -and (Get-FixtureSnapshot $script:fixture) -ceq $before -and
        @(Get-ChildItem -LiteralPath $script:fixture -Recurse -Force).Count -eq $pathsBefore) 'WhatIf invoked tools or mutated the fixture.'
    foreach ($version in @('1.2', '01.2.3', '256.0.1', '1.256.1', '1.0.65536', '1.2.3-beta',
            '1.2.999999999999999999999', "1.2.3`n")) {
        $invalid = $options.Clone(); $invalid.Version = $version
        Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @invalid -WhatIf }
    }
    foreach ($field in @('Endpoint', 'Account', 'Profile', 'ExpectedPublisher', 'SignToolPath', 'DlibPath', 'DestinationPath')) {
        $invalid = $options.Clone(); $invalid[$field] = "unsafe`nvalue"
        Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @invalid -WhatIf }
    }
    foreach ($endpoint in @('http://test.codesigning.azure.net', 'https://example.com',
            'https://test.codesigning.azure.net.evil.test', 'https://secret@test.codesigning.azure.net',
            'https://test.codesigning.azure.net/?secret=x', 'https://test.codesigning.azure.net/#secret',
            'https://test.codesigning.azure.net/path', 'https://test.codesigning.azure.net:443')) {
        $invalid = $options.Clone(); $invalid.Endpoint = $endpoint
        Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @invalid -WhatIf }
    }
    Assert ($script:trace.Count -eq 0) 'Invalid inputs reached tools or authentication.'
    foreach ($invalidDestination in @('Q:\', '\\server\share', '\\server\share\', '\\?\Q:\folder', 'relative',
            $script:fixture, (Join-Path $script:fixture 'artifacts\msi'), $fixtureRoot,
            (Join-Path $script:destination 'bad:stream'), ($script:destination + '.'),
            (Join-Path $script:destination 'NUL'), (Join-Path $script:destination '..\escape'))) {
        Reject { Copy-AgentSignalerSignedMsis -Root $script:fixture -DestinationPath $invalidDestination -WhatIf }
    }
    foreach ($scriptText in @(
        (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Sign-Installers.ps1') -Raw),
        ${function:Invoke-AgentSignalerLocalSigning}.ToString(),
        ${function:Copy-AgentSignalerSignedMsis}.ToString())) {
        $ast = [scriptblock]::Create($scriptText).Ast
        $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'DestinationPath' })[0]
        Assert ($parameter.DefaultValue.Value -ceq '\\tsclient\C\public\AgentSignaler') 'Default destination changed.'
    }
    Assert-LocalSigningInputs '255.255.65535' $options.Endpoint $options.Account $options.Profile 'Andy Sterland'
    foreach ($name in @('Sign-Installers.ps1', 'LocalSigning.ps1', 'UpdaterPolicy.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $script:fixture "scripts\$name")
    }
    $before = Get-FixtureSnapshot $script:fixture
    & (Join-Path $script:fixture 'scripts\Sign-Installers.ps1') @options -WhatIf
    Assert ((Get-FixtureSnapshot $script:fixture) -ceq $before) 'Entry script WhatIf mutated output.'
    Reject { Assert-LocalSigningPath (Join-Path $script:fixture '..\escaped.exe') $script:fixture }
    Reject { Assert-LocalSigningPath ((Join-Path $script:fixture 'payload.dll') + ':stream') $script:fixture }
    Reject { Assert-LocalSigningPath '\\example.invalid\share\az.cmd' }

    $outside = Join-Path $fixtureRoot 'outside'
    [void](New-Item -ItemType Directory -Path $outside)
    $junction = Join-Path $script:fixture 'artifacts\publish\dashboard\escape'
    [void](New-Item -ItemType Junction -Path $junction -Target $outside)
    try { Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options -WhatIf } }
    finally { [IO.Directory]::Delete($junction) }
    Assert ($script:trace.Count -eq 0) 'Reparse point reached tools or build.'
    $destinationLink = Join-Path $fixtureRoot 'destination-junction'
    [void](New-Item -ItemType Junction -Path $destinationLink -Target $script:destination)
    try { Reject { & $destinationCheck $destinationLink } }
    finally { [IO.Directory]::Delete($destinationLink) }
    $collision = Join-Path $script:destination 'AgentSignaler.Dashboard.msi'
    [void](New-Item -ItemType Directory -Path $collision)
    try {
        Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @options }
        Assert (-not $script:trace.Contains('auth')) 'Destination collision was not detected before signing authentication/build.'
    }
    finally { [IO.Directory]::Delete($collision) }
    $missingDestination = Join-Path $fixtureRoot 'missing-destination'
    $invalid = $options.Clone(); $invalid.DestinationPath = $missingDestination
    Reject { Invoke-AgentSignalerLocalSigning -Root $script:fixture @invalid }
    Assert (-not (Test-Path -LiteralPath $missingDestination) -and -not $script:trace.Contains('auth')) 'Missing destination was created or detected after authentication/build.'

    $bounded = Join-Path $fixtureRoot 'bounded'
    foreach ($i in 1..257) { Write-FixtureFile (Join-Path $bounded "AgentSignaler.$i.dll") 'synthetic' }
    Reject { Get-LocalSigningTargets @($bounded) $fixtureRoot }
    Write-FixtureFile (Join-Path $bounded 'AgentSignaler.RpcHost.dll') 'retired'
    Reject { Get-LocalSigningTargets @($bounded) $fixtureRoot }

    New-SigningFixture 'tool-certificate'
    $pe = [byte[]]::new(128)
    $pe[0] = 0x4d; $pe[1] = 0x5a; $pe[0x3c] = 64
    $pe[64] = 0x50; $pe[65] = 0x45; $pe[68] = 0x64; $pe[69] = 0x86
    $toolPath = Join-Path $script:fixture 'signtool.exe'
    [IO.File]::WriteAllBytes($toolPath, $pe)
    Assert ((& $resolveTool $toolPath 'signtool.exe') -ceq $toolPath) 'Explicit verified x64 tool was not selected.'
    $pe[68] = 0x4c; $pe[69] = 0x01
    [IO.File]::WriteAllBytes($toolPath, $pe)
    Reject { & $resolveTool $toolPath 'signtool.exe' }
    $pe[68] = 0x64; $pe[69] = 0x86
    [IO.File]::WriteAllBytes($toolPath, $pe)
    $script:mode = 'wrong-publisher'
    Reject { & $resolveTool $toolPath 'signtool.exe' }
    $originalLocalAppData = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = Join-Path $script:fixture 'local-app-data'
        $dlibPath = Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll'
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $dlibPath) -Force)
        [IO.File]::WriteAllBytes($dlibPath, $pe)
        $script:mode = 'tool-certificate'
        Assert ((& $resolveTool '' 'Azure.CodeSigning.Dlib.dll') -ceq $dlibPath) 'Official current-user flat Dlib installation was not discovered.'
        $script:mode = 'unsigned-tools'
        Assert ((& $resolveTool '' 'Azure.CodeSigning.Dlib.dll') -ceq $dlibPath) 'Official unsigned Dlib must remain usable.'
        Reject { & $resolveTool $toolPath 'signtool.exe' }
    }
    finally { $env:LOCALAPPDATA = $originalLocalAppData }

    $fakeCli = Join-Path $script:fixture 'az.cmd'
    [IO.File]::WriteAllText($fakeCli, "@echo off`r`nexit /b 17`r`n")
    Reject { & $native $fakeCli @('account', 'show', '--output', 'none') }
    try {
        $env:PATH = $script:fixture
        Assert ((& $resolveAz) -ceq $fakeCli) 'Application-only Azure CLI resolution must support az.cmd.'
    }
    finally { $env:PATH = $originalPath }
    $originalProgramFiles = $env:ProgramFiles
    $originalProgramFilesX86 = ${env:ProgramFiles(x86)}
    try {
        $env:ProgramFiles = Join-Path $script:fixture 'program-files'
        ${env:ProgramFiles(x86)} = Join-Path $script:fixture 'program-files-x86'
        $env:PATH = Join-Path $script:fixture 'empty-path'
        $installedCli = Join-Path $env:ProgramFiles 'Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
        Write-FixtureFile $installedCli "@echo off`r`nexit /b 17`r`n"
        Assert ((& $resolveAz) -ceq $installedCli) 'Standard Azure CLI installation must be discovered before the session PATH is refreshed.'
    }
    finally {
        $env:ProgramFiles = $originalProgramFiles
        ${env:ProgramFiles(x86)} = $originalProgramFilesX86
        $env:PATH = $originalPath
    }
    Write-FixtureFile (Join-Path $script:fixture 'scripts\Build-Installers.ps1') @'
param($Version, $Configuration, [switch]$NoRestore, [switch]$PublishOnly, [switch]$SkipPublish, [switch]$ApplicationMsisOnly)
[pscustomobject]@{ Version=$Version; Configuration=$Configuration; NoRestore=$NoRestore.IsPresent
    PublishOnly=$PublishOnly.IsPresent; SkipPublish=$SkipPublish.IsPresent; ApplicationMsisOnly=$ApplicationMsisOnly.IsPresent }
'@
    $published = & $build $script:fixture '1.2.3' 'Debug' $true $true
    $packaged = & $build $script:fixture '1.2.3' 'Debug' $true $false
    Assert ($published.PublishOnly -and -not $published.SkipPublish -and -not $published.ApplicationMsisOnly -and
        $packaged.SkipPublish -and $packaged.ApplicationMsisOnly -and -not $packaged.PublishOnly -and
        $published.Version -ceq '1.2.3' -and $packaged.Configuration -ceq 'Debug' -and
        $published.NoRestore -and $packaged.NoRestore) 'Existing builder flags were not forwarded exactly.'
    $scriptsOnly = Join-Path $fixtureRoot 'scripts-only'
    foreach ($name in $script:AgentSignalerUpdaterFiles) { Write-FixtureFile (Join-Path $scriptsOnly $name) 'synthetic script' }
    Reject { Get-LocalSigningTargets @($scriptsOnly) $fixtureRoot }
    Write-Host 'Local signing/copy fixtures passed: ordering, exact targets, credentials, hard links, rollback/recovery, hashes, WhatIf without remote access, destination defaults/overrides, copy failures/concurrent edits, paths, versions, publishers, timestamps and tools. No real build, cloud signing, login, UNC copy or installer ran.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
