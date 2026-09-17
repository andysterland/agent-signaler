[CmdletBinding()]
param(
    [string] $Version = '1.0.19',
    [string] $MsiPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')
$root = Split-Path -Parent $PSScriptRoot
$path = if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    Join-Path $root 'artifacts\msi\AgentSignaler.RpcHost.msi'
} else { [IO.Path]::GetFullPath($MsiPath) }
$publish = Join-Path $root 'artifacts\publish\rpchost'
$installer = New-Object -ComObject WindowsInstaller.Installer
$inspection = Join-Path $root ('artifacts\rpchost-msi-inspection\' + [guid]::NewGuid().ToString('N'))
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
try {
    $database = $installer.OpenDatabase($path, 0)
    try {
        function Rows([string] $Query, [string[]] $Columns) {
            $view = $database.OpenView($Query)
            try {
                [void]$view.Execute()
                while ($null -ne ($record = $view.Fetch())) {
                    try {
                        $row = @{}
                        for ($i = 0; $i -lt $Columns.Count; $i++) { $row[$Columns[$i]] = $record.StringData($i + 1) }
                        [pscustomobject]$row
                    }
                    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                }
            }
            finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        $properties = @{}
        Rows 'SELECT `Property`, `Value` FROM `Property`' @('Name', 'Value') | ForEach-Object { $properties[$_.Name] = $_.Value }
        $summary = $database.SummaryInformation(0)
        try { $package = Assert-AgentSignalerPackageMetadata $properties $summary.Property(7) ([int]$summary.Property(15)) 'RpcHost' }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        Assert ($package.Version -eq (ConvertTo-AgentSignalerVersion $Version)) 'RpcHost MSI version mismatch.'
        Assert ($properties['SecureCustomProperties'].Split(';') -contains 'RECEIVERPORT') 'Receiver port must cross the elevation boundary explicitly.'
        Assert ($properties['SecureCustomProperties'].Split(';') -contains 'RPCPORTMAINTENANCE') 'Explicit receiver-port maintenance intent must survive UI-to-execute transfer.'
        foreach ($private in @('RPCUSER', 'RPCDIRECTORY', 'RPCDATA')) {
            Assert ($properties['SecureCustomProperties'].Split(';') -contains $private -and
                $properties['MsiHiddenProperties'].Split(';') -contains $private) 'Captured user paths must be secure and hidden.'
        }
        $tables = @(Rows 'SELECT `Name` FROM `_Tables`' @('Name'))
        foreach ($forbidden in @('ServiceInstall', 'ServiceControl', 'Environment', 'RemoveRegistry', 'Shortcut')) {
            Assert ($tables.Name -notcontains $forbidden) 'RpcHost must not install services, startup, shortcuts or host cleanup.'
        }
        $directories = @{}
        Rows 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' @('Name', 'Parent', 'Value') |
            ForEach-Object { $directories[$_.Name] = $_ }
        Assert ($directories.INSTALLFOLDER.Value -eq 'RpcHost' -and
            $directories.INSTALLFOLDER.Parent -eq 'AgentSignalerFolder' -and
            $directories.AgentSignalerFolder.Parent -eq 'ProgramsFolder' -and
            $directories.ProgramsFolder.Parent -eq 'LocalAppDataFolder') 'RpcHost install path changed.'
        $registry = @(Rows 'SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`' @('Root', 'Key', 'Name', 'Value', 'Component'))
        Assert ($registry.Count -eq 4) 'Unexpected RpcHost user metadata.'
        foreach ($entry in $registry) {
            Assert ($entry.Root -eq '1' -and $entry.Key -ceq 'Software\AgentSignaler\Installer\RpcHost' -and $entry.Component -ceq 'RpcHostPayload' -and
                $entry.Name -in @('UpgradeCode', 'InstallDirectory', 'ReceiverPort', 'UserSid')) 'Registry metadata escaped installer ownership.'
        }
        $features = @(Rows 'SELECT `Feature_`, `Component_` FROM `FeatureComponents`' @('Feature', 'Component'))
        Assert ($features.Count -eq 1 -and $features[0].Feature -ceq 'Main' -and
            $features[0].Component -ceq 'RpcHostPayload') 'Registry-only maintenance may reinstall only the owned Main feature/component.'
        $components = @(Rows 'SELECT `ComponentId`, `Attributes`, `Directory_` FROM `Component`' @('Guid', 'Attributes', 'Directory'))
        Assert ($components.Count -eq 1 -and $components[0].Guid -ceq '{501D1AF2-8A68-43C6-AFDC-AF83FDE49671}' -and
            ([int]$components[0].Attributes -band 4) -ne 0 -and
            ([int]$components[0].Attributes -band 256) -ne 0 -and $components[0].Directory -ceq 'INSTALLFOLDER') 'RpcHost component identity, HKCU key path or x64 attributes changed.'
        $files = @(Rows 'SELECT `File`, `FileName`, `FileSize` FROM `File`' @('Id', 'Name', 'Size'))
        Assert ($files.Count -eq 3) 'RpcHost MSI may contain only the single EXE and notices.'
        foreach ($file in $files) {
            $leaf = ($file.Name -split '\|')[-1]
            Assert ($leaf -cin @('AgentSignaler.RpcHost.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) 'Unexpected RpcHost payload (WinUI/runtime/data files are forbidden).'
            Assert ((Get-Item -LiteralPath (Join-Path $publish $leaf)).Length -eq [long]$file.Size) 'RpcHost payload size mismatch.'
        }
        Assert (@(Get-ChildItem -LiteralPath $publish -Recurse -File).Count -eq 3) 'Publish must not require adjacent runtime or WinUI files.'
        $cleanup = @(Rows 'SELECT `FileName`, `DirProperty`, `InstallMode` FROM `RemoveFile`' @('Name', 'Directory', 'Mode'))
        Assert ($cleanup.Count -eq 3 -and @($cleanup.Directory | Select-Object -Unique).Count -eq 3) 'Empty owned-directory cleanup is incomplete.'
        foreach ($entry in $cleanup) {
            Assert ($entry.Name -eq '' -and $entry.Directory -cin @('INSTALLFOLDER', 'AgentSignalerFolder', 'ProgramsFolder') -and
                $entry.Mode -eq '2') 'Cleanup may remove only empty installer directories, never files, unrelated applications or shared data.'
        }
        $actions = @(Rows 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' @('Name', 'Type', 'Source', 'Target'))
        $allowed = @('CaptureRpcHostUser', 'PrepareRpcHostServicing', 'RollbackRpcHostFirewall', 'ApplyRpcHostFirewall', 'CommitRpcHostFirewall', 'VerifyRpcHostFilesIdle', 'SetARPINSTALLLOCATION')
        Assert ($actions.Count -eq $allowed.Count -and @($actions | Where-Object Name -NotIn $allowed).Count -eq 0) 'Unexpected RpcHost custom action.'
        foreach ($action in $actions | Where-Object Name -ne 'SetARPINSTALLLOCATION') {
            Assert (([int]$action.Type -band 63) -eq 1 -and $action.Source -ceq 'RpcHostInstallerActions') 'Custom actions must load the embedded native DLL, never the per-user EXE.'
            $elevated = $action.Name -in @('RollbackRpcHostFirewall', 'ApplyRpcHostFirewall', 'CommitRpcHostFirewall')
            $deferred = $elevated -or $action.Name -eq 'VerifyRpcHostFilesIdle'
            Assert ((([int]$action.Type -band 2048) -ne 0) -eq $elevated -and
                (([int]$action.Type -band 1024) -ne 0) -eq $deferred -and
                ([int]$action.Type -band 192) -eq 0) 'Firewall actions must be elevated, transactional and checked.'
            Assert ((([int]$action.Type -band 256) -ne 0) -eq ($action.Name -eq 'RollbackRpcHostFirewall') -and
                (([int]$action.Type -band 512) -ne 0) -eq ($action.Name -eq 'CommitRpcHostFirewall')) 'Rollback/commit execution mode mismatch.'
        }
        $sequence = @{}
        Rows 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`' @('Name', 'Condition', 'Number') |
            ForEach-Object { $sequence[$_.Name] = $_ }
        Assert ([int]$sequence.CaptureRpcHostUser.Number -lt [int]$sequence.CostInitialize.Number -and
            [int]$sequence.PrepareRpcHostServicing.Number -lt [int]$sequence.InstallInitialize.Number -and
            [int]$sequence.RollbackRpcHostFirewall.Number -gt [int]$sequence.InstallInitialize.Number -and
            [int]$sequence.RollbackRpcHostFirewall.Number -lt [int]$sequence.ApplyRpcHostFirewall.Number -and
            [int]$sequence.ApplyRpcHostFirewall.Number -lt [int]$sequence.RemoveFiles.Number -and
            [int]$sequence.CommitRpcHostFirewall.Number -gt [int]$sequence.ApplyRpcHostFirewall.Number -and
            [int]$sequence.VerifyRpcHostFilesIdle.Number -gt [int]$sequence.InstallFiles.Number -and
            [int]$sequence.VerifyRpcHostFilesIdle.Number -lt [int]$sequence.InstallExecute.Number -and
            [int]$sequence.RemoveExistingProducts.Number -gt [int]$sequence.InstallExecute.Number -and
            [int]$sequence.RemoveExistingProducts.Number -lt [int]$sequence.InstallFinalize.Number) 'RpcHost transaction ordering is unsafe.'
        foreach ($action in @('ApplyRpcHostFirewall', 'RollbackRpcHostFirewall', 'CommitRpcHostFirewall', 'VerifyRpcHostFilesIdle')) {
            Assert (($sequence[$action].Condition -replace '\s','') -ceq 'NOTUPGRADINGPRODUCTCODE') 'Mandatory receiver rule must apply in both connection modes; old-product upgrade removal skips duplicate cleanup.'
        }
        $ui = @(Rows 'SELECT `Action`, `Sequence` FROM `InstallUISequence`' @('Name', 'Number'))
        Assert ([int]($ui | Where-Object Name -eq 'CaptureRpcHostUser').Number -lt
            [int]($ui | Where-Object Name -eq 'CostInitialize').Number) 'Intended identity must be captured before elevation in UI sequence.'
        $media = @(Rows 'SELECT `Cabinet` FROM `Media`' @('Cabinet'))
        Assert ($media.Count -gt 0 -and @($media | Where-Object Cabinet -NotLike '#*').Count -eq 0) 'Cabinets must be embedded.'
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $assets = Get-Content (Join-Path $root 'installers\AgentSignaler.RpcHost\obj\project.assets.json') -Raw | ConvertFrom-Json
    $cache = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $wix = Join-Path $cache 'wixtoolset.sdk\4.0.6\tools\net6.0\wix.dll'
    [void](New-Item -ItemType Directory -Path $inspection -Force)
    & dotnet $wix msi decompile $path -o (Join-Path $inspection 'package.wxs') -x (Join-Path $inspection 'payload') -intermediateFolder (Join-Path $inspection 'work')
    if ($LASTEXITCODE) { throw 'Read-only RpcHost MSI extraction failed.' }
    foreach ($pair in @(
        @('File\RpcHostExecutable', (Join-Path $publish 'AgentSignaler.RpcHost.exe')),
        @('File\RpcHostLicense', (Join-Path $publish 'LICENSE')),
        @('File\RpcHostNotices', (Join-Path $publish 'THIRD-PARTY-NOTICES.txt')),
        @('Binary\RpcHostInstallerActions', (Join-Path $root 'artifacts\rpchost-installer\AgentSignaler.RpcHost.InstallerActions.dll')))) {
        Assert ((Get-FileHash -LiteralPath (Join-Path $inspection ('payload\' + $pair[0]))).Hash -ceq
            (Get-FileHash -LiteralPath $pair[1]).Hash) 'Extracted payload differs from the independently tested source.'
    }
    Write-Host 'RpcHost MSI identity/elevation/ownership/sequencing/full-payload table gates passed. Real UAC/firewall servicing: Not run - deferred release acceptance.'
    Write-Host 'RpcHost MSI ICE validation is disabled by explicit user decision; it was not run and is not claimed as passed.'
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    if (Test-Path -LiteralPath $inspection) { Remove-Item -LiteralPath $inspection -Recurse -Force }
}
