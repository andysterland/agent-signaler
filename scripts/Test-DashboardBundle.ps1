[CmdletBinding()]
param([string] $Version = '1.0.13')
. (Join-Path $PSScriptRoot 'PrerequisiteSecurity.ps1')
$root = Split-Path -Parent $PSScriptRoot
$metadata = Get-PrerequisiteMetadata
$bundle = Join-Path $root 'artifacts\bundle\AgentSignaler.Dashboard.Setup.exe'
$inspection = Join-Path $root 'artifacts\bundle-inspection'
$source = Join-Path $root 'installers\AgentSignaler.Dashboard.Bundle'
$checks = 0
function Assert-Bundle([bool] $Condition, [string] $Message) {
    $script:checks++
    if (-not $Condition) { throw $Message }
}
Assert-X64Executable $bundle
$assets = Get-Content (Join-Path $source 'obj\project.assets.json') -Raw | ConvertFrom-Json
$cache = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$wix = Join-Path $cache 'wixtoolset.sdk\4.0.6\tools\net6.0\wix.dll'
if (Test-Path -LiteralPath $inspection) { Remove-Item -LiteralPath $inspection -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $inspection 'work') -Force | Out-Null
try {
    # Invoke WiX's reader, never Setup.exe (not even /layout or /?).
    & dotnet $wix burn extract $bundle -o $inspection -oba (Join-Path $inspection 'ux') -intermediateFolder (Join-Path $inspection 'work')
    if ($LASTEXITCODE) { throw 'Read-only bundle extraction failed.' }
    [xml]$xml = Get-Content (Join-Path $inspection 'ux\manifest.xml') -Raw
    $manifest = $xml.DocumentElement
    Assert-Bundle ($manifest.Win64 -eq 'yes' -and $manifest.EngineVersion -eq '4.0.6.0') 'Bundle is not WiX 4.0.6 x64.'
    Assert-Bundle ($manifest.Registration.Version -eq $Version -and $manifest.Registration.PerMachine -eq 'no') 'Bundle registration version/scope mismatch.'
    $chain = @($manifest.Chain.MsiPackage)
    Assert-Bundle (($chain.Id -join ',') -ceq 'AzureCli,DevTunnels,Dashboard') 'Wrong chain order or extra installer.'
    Assert-Bundle ($chain[0].Version -eq $metadata.AzureCliVersion -and $chain[0].ProductCode -eq $metadata.AzureCliProductCode -and
        $chain[0].UpgradeCode -eq $metadata.AzureCliUpgradeCode -and $chain[0].PerMachine -eq 'yes') 'Azure MSI pin mismatch.'
    Assert-Bundle ($chain[1].Version -eq $metadata.DevTunnelsMsiVersion -and $chain[1].PerMachine -eq 'no') 'Dev Tunnels MSI version/scope mismatch.'
    Assert-Bundle ($chain[2].Version -eq $Version) 'Dashboard version mismatch.'
    foreach ($package in $chain[0..1]) {
        Assert-Bundle ($package.Permanent -eq 'yes' -and $package.Vital -eq 'yes') 'Shared prerequisite is not permanent/vital.'
        Assert-Bundle ($package.InstallCondition -ceq "Install$($package.Id) = 1") 'Prerequisite selection condition mismatch.'
        $newer = @($package.RelatedPackage | Where-Object { $_.OnlyDetect -eq 'yes' -and $_.MinVersion -eq $package.Version })
        Assert-Bundle ($newer.Count -ge 1) 'Newer prerequisite downgrade protection missing.'
    }
    $license = @($manifest.Variable | Where-Object Id -eq 'ACCEPT_PREREQUISITE_LICENSES')
    Assert-Bundle ($license.Count -eq 1 -and $license[0].Value -eq '0' -and $license[0].Hidden -eq 'yes' -and
        $license[0].Persisted -eq 'no') 'License acceptance must be hidden, opt-in, and current-run only.'
    Assert-Bundle (@($manifest.UX.Payload | Where-Object FilePath -eq 'AgentSignaler.Bootstrapper.dll').Count -eq 1) 'Custom native bootstrapper missing.'
    Assert-Bundle ((Get-FileHash (Join-Path $inspection 'ux\Prerequisites.props')).Hash -eq
        (Get-FileHash (Join-Path $source 'Prerequisites.props')).Hash) 'Embedded prerequisite metadata differs from source.'
    Assert-Bundle ((Get-FileHash (Join-Path $inspection 'ux\AgentSignaler.Bootstrapper.dll')).Hash -eq
        (Get-FileHash (Join-Path $root 'artifacts\bootstrapper\AgentSignaler.Bootstrapper.dll')).Hash) 'Embedded BA is not the tested BA.'
    foreach ($payload in $manifest.Payload) {
        Assert-Bundle ($payload.Packaging -eq 'embedded' -and -not $payload.HasAttribute('DownloadUrl')) 'Runtime download fallback is forbidden: all payloads must be embedded.'
        $path = Join-Path $inspection "WixAttachedContainer\$($payload.FilePath)"
        Assert-Bundle ((Get-FileHash $path -Algorithm SHA512).Hash -ceq $payload.Hash) 'Burn payload digest mismatch.'
        $original = if ($payload.Id -eq 'AzureCli') { Join-Path $root "artifacts\prerequisites\$($payload.FilePath)" } else { Join-Path $root "artifacts\msi\$($payload.FilePath)" }
        Assert-Bundle ((Get-FileHash $path).Hash -ceq (Get-FileHash $original).Hash) 'Extracted payload differs from validated source.'
    }
    Assert-PrerequisitePayload (Join-Path $inspection "WixAttachedContainer\azure-cli-$($metadata.AzureCliVersion)-x64.msi") AzureCli $metadata
    Assert-PrerequisitePayload (Join-Path $root 'artifacts\prerequisites\devtunnel.exe') DevTunnels $metadata
    $cliMsi = Join-Path $inspection 'WixAttachedContainer\AgentSignaler.DevTunnelsPrerequisite.msi'
    & dotnet $wix msi decompile $cliMsi -o (Join-Path $inspection 'cli.wxs') -x (Join-Path $inspection 'cli-files') -intermediateFolder (Join-Path $inspection 'work')
    if ($LASTEXITCODE) { throw 'Read-only CLI MSI extraction failed.' }
    Assert-PrerequisitePayload (Join-Path $inspection 'cli-files\File\DevTunnelExe') DevTunnels $metadata
    $cliIdentity = Get-PrerequisiteMsiMetadata $cliMsi
    Assert-Bundle ($cliIdentity.UpgradeCode -eq '{3F3F58A0-CB9D-441F-9F70-660E080E9315}' -and
        $cliIdentity.Template -like 'x64;*' -and -not $cliIdentity.ContainsKey('ALLUSERS')) 'CLI wrapper stable family, architecture or scope mismatch.'
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    try {
        $database = $installer.OpenDatabase((Join-Path $inspection 'WixAttachedContainer\AgentSignaler.DevTunnelsPrerequisite.msi'), 0)
        function Rows([string] $Query, [int] $Columns = 1) {
            $view = $database.OpenView($Query)
            try {
                [void]$view.Execute()
                while ($null -ne ($record = $view.Fetch())) {
                    try {
                        $row = @()
                        for ($i = 1; $i -le $Columns; $i++) { $row += $record.StringData($i) }
                        ,$row
                    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                }
            } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        $files = @(Rows 'SELECT `FileName`, `FileSize`, `Version` FROM `File`' 3)
        Assert-Bundle ($files.Count -eq 1 -and ($files[0][0] -split '\|')[-1] -eq 'devtunnel.exe' -and $files[0][2] -eq $metadata.DevTunnelsFileVersion) 'Prerequisite MSI must contain only the qualified CLI.'
        $tables = @(Rows 'SELECT `Name` FROM `_Tables`' | ForEach-Object { $_[0] })
        foreach ($table in @('CustomAction', 'Environment', 'ServiceInstall', 'Shortcut')) {
            Assert-Bundle ($tables -notcontains $table) "Prerequisite MSI must not contain $table."
        }
        $dirs = @(Rows 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' 3)
        Assert-Bundle (@($dirs | Where-Object { $_[0] -eq 'INSTALLFOLDER' -and $_[1] -eq 'ProgramsFolder' -and ($_[2] -split '\|')[-1] -eq 'Microsoft Dev Tunnels CLI' }).Count -eq 1) 'CLI prerequisite directory mismatch.'
        Assert-Bundle (@($dirs | Where-Object { $_[0] -eq 'ProgramsFolder' -and $_[1] -eq 'LocalAppDataFolder' }).Count -eq 1) 'CLI prerequisite is not per-user.'
    } finally {
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
    $ba = Get-Content (Join-Path $source 'Bootstrapper.cpp') -Raw
    $detection = Get-Content (Join-Path $source 'PrerequisiteDetection.h') -Raw
    Assert-Bundle ($ba -notmatch 'catch\s*\(\s*\.\.\.\s*\)' -and $detection -notmatch 'catch\s*\(\s*\.\.\.\s*\)') 'Native boundaries and detection must not swallow arbitrary exceptions.'
    Assert-Bundle ($detection.Contains('InstalledProductVersion(ProductInfo(product, INSTALLPROPERTY_VERSIONSTRING))') -and
        $detection.Contains('throw DetectionFailure(HRESULT_FROM_WIN32(ERROR_INVALID_DATA))')) 'Malformed MSI versions must explicitly fail detection, not be ignored.'
    Assert-Bundle ($ba.Contains('setup::StartupResult(Interactive(), accepted,') -and
        $ba -match 'if \(result\) Complete\(result\);\s*else if \(!supportedPlatform\(\)\) Complete\(1633\);\s*else Detect\(\);') 'Quiet/passive license gate must precede Burn detection/planning.'
    Assert-Bundle ($ba.Contains('Uninstall() && command.relationType == BOOTSTRAPPER_RELATION_UPGRADE')) 'Consent exemption must be restricted to Burn-related upgrade uninstall.'
    Assert-Bundle ($ba.Contains('MB_OKCANCEL | MB_ICONWARNING') -and $ba.Contains('BS_AUTOCHECKBOX') -and $ba.Contains('AzureCliLicenseUrl') -and $ba.Contains('DevTunnelsLicenseUrl')) 'Interactive selection, warning, or license UX missing.'
    Assert-Bundle ($ba.Contains('Newer prerequisite retained') -and $ba.Contains('Clear its installation checkbox')) 'Newer unsupported prerequisites need an explicit no-downgrade warning and decline path.'
    Assert-Bundle ($ba -match '(?s)if \(acceptAzure\).*?auto now = detectAzure\(\);.*?if \(cancelled\).*?Fail\(DetectionFailed\)' -and
        $ba -match '(?s)if \(!failure && acceptTunnel\).*?auto now = detectTunnel\(\);.*?if \(cancelled\).*?Fail\(DetectionFailed\)') 'Accepted prerequisite re-detection must check cancellation before recording failure.'
    Assert-Bundle ($ba.Contains('WindowsAppStoreUri') -and @($chain | Where-Object Id -like '*WindowsApp*').Count -eq 0) 'Windows App must be detect/store only.'
    $log = [regex]::Match($ba, 'void Log\([\s\S]*?\n    }').Value
    Assert-Bundle ($log.Length -gt 0 -and $log -notmatch '(?i)token|credential|account|connectionuri|commandline|wzError') 'BA log must contain only product, version and result.'
    Write-Host "$checks bundle checks passed read-only: real signed/pinned payloads, x64 Burn 4.0.6, custom BA, exact chain and metadata. No installer execution or copy."
    Write-Warning 'Release gate: the exact signed Azure CLI 2.90.0 MSI contains az.cmd, not the mandated az.exe. Accepted Azure re-detection will fail (5104) on a stock installation. Validation has not been relaxed.'
} finally {
    if (Test-Path -LiteralPath $inspection) { Remove-Item -LiteralPath $inspection -Recurse -Force }
}
