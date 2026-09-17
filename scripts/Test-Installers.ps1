[CmdletBinding()]
param([string] $Version = '1.0.19')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$installer = New-Object -ComObject WindowsInstaller.Installer
$seenComponentGuids = @{}

function Read-MsiTable {
    param($Database, [string] $Query, [string[]] $Columns)
    try { $view = $Database.OpenView($Query) }
    catch { throw "MSI query failed ($Query): $($_.Exception.Message)" }
    try {
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $row = @{}
                for ($i = 0; $i -lt $Columns.Count; $i++) {
                    $row[$Columns[$i]] = $record.StringData($i + 1)
                }
                [pscustomobject]$row
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    }
    finally {
        [void]$view.Close()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Assert-Msi {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ApplicationPayloadPath {
    param([string] $RelativePath)
    $segments = $RelativePath -split '[\\/]'
    Assert-Msi (-not [IO.Path]::IsPathRooted($RelativePath) -and
        @($segments | Where-Object { $_ -in @('', '.', '..') -or $_.Contains(':') }).Count -eq 0) 'Payload must remain in its application installation directory.'
    $leaf = $segments[-1]
    Assert-Msi ($leaf -notmatch '(?i)\.(jsonl|ndjson|log|db|sqlite|sqlite3|bak|pfx|p12|pem|key)(-wal|-shm)?$' -and
        $leaf -notmatch '(?i)\.Tests\.(dll|pdb|deps\.json|runtimeconfig\.json)$' -and
        $leaf -notmatch '(?i)^(remote|dashboard-settings|session-state|configurator-settings)\.json$' -and
        $leaf -notmatch '(?i)(transcript|conversation|cursor|spool|journal|credential|pairing).*\.(json|txt|xml|bin|dat)$' -and
        @($segments | Where-Object { $_ -match '^(?i:fixtures?|transcripts?|conversations?|spool|backups?|recovery|\.copilot|CopilotCli)$' }).Count -eq 0) 'Application payload contains prohibited runtime data, transcript inputs, test fixtures, or credential material.'
}

try {
    foreach ($name in @('Dashboard', 'Remote')) {
        $msi = Join-Path $root "artifacts\msi\AgentSignaler.$name.msi"
        $publish = Join-Path $root ('artifacts\publish\' + $name.ToLowerInvariant())
        $database = $installer.OpenDatabase($msi, 0)
        try {
            $properties = @{}
            Read-MsiTable $database 'SELECT `Property`, `Value` FROM `Property`' @('Property', 'Value') |
                ForEach-Object { $properties[$_.Property] = $_.Value }
            Assert-Msi ($properties['ProductVersion'] -eq $Version) "$name version mismatch."
            if ($name -eq 'Dashboard') {
                Assert-Msi ($properties['PREREQUISITE_WARNING'] -like '*does not install*') 'Direct MSI prerequisite warning missing.'
                $searches = @(Read-MsiTable $database 'SELECT `Property` FROM `AppSearch`' @('Property'))
                foreach ($property in @('AZURE_CLI_MSI_VERSION', 'AZURE_CLI_EXE', 'DEVTUNNELS_WINGET_EXE', 'DEVTUNNELS_PREREQUISITE_EXE', 'WINDOWS_APP_PROTOCOL')) {
                    Assert-Msi ($searches.Property -contains $property) "Missing direct MSI detection property: $property"
                }
                $warnings = @(Read-MsiTable $database 'SELECT `Description` FROM `ActionText` WHERE `Action` = ''AppSearch''' @('Description'))
                Assert-Msi ($warnings.Count -eq 1 -and $warnings[0].Description -like 'Warning:*') 'AppSearch log warning missing.'
                $allActions = @(Read-MsiTable $database 'SELECT `Action` FROM `CustomAction`' @('Action'))
                Assert-Msi (@($allActions | Where-Object { $_.Action -notin @('SetIntegrationTransaction', 'RollbackDashboardIntegration', 'RemoveDashboardIntegration') }).Count -eq 0) 'Unexpected prerequisite/nested installer custom action.'
            }
            Assert-Msi (-not $properties.ContainsKey('ALLUSERS')) "$name is not strictly per-user."
            $tables = @(Read-MsiTable $database 'SELECT `Name` FROM `_Tables`' @('Name'))
            Assert-Msi (@($tables | Where-Object { $_.Name -in @('RemoveRegistry', 'Environment', 'ServiceInstall') }).Count -eq 0) 'Application package must not add host-profile cleanup, environment changes, or another reporting service.'
            $expectedUpgrade = if ($name -eq 'Dashboard') {
                '{D67CE744-C442-473A-B751-CA70D3CBCA4D}'
            } else { '{BFA03A34-37C9-4149-9789-9A68EC2A9C3A}' }
            Assert-Msi ($properties['UpgradeCode'] -eq $expectedUpgrade) "$name UpgradeCode changed."

            $summary = $database.SummaryInformation(0)
            try {
                Assert-Msi ($summary.Property(7) -like 'x64;*') "$name is not an x64 package."
                Assert-Msi (([int]$summary.Property(15) -band 8) -ne 0) "$name unexpectedly requires elevated installation privileges."
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }

            $directories = @{}
            Read-MsiTable $database 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' @('Id', 'Parent', 'Name') |
                ForEach-Object { $directories[$_.Id] = $_ }
            Assert-Msi (($directories['INSTALLFOLDER'].Name -split '\|')[-1] -eq $name) "$name install directory changed."
            Assert-Msi ($directories['ApplicationProgramsFolder'].Parent -eq 'ProgramMenuFolder' -and
                ($directories['ApplicationProgramsFolder'].Name -split '\|')[-1] -eq 'Agent Signaler') 'Shortcut cleanup must remain in the owned Agent Signaler Start Menu directory.'
            Assert-Msi ($directories['AgentSignalerFolder'].Parent -eq 'ProgramsFolder' -and
                $directories['ProgramsFolder'].Parent -eq 'LocalAppDataFolder') "$name is not under LocalAppData\Programs."

            $components = @{}
            $componentRows = @(Read-MsiTable $database 'SELECT `Component`, `Directory_`, `Attributes`, `ComponentId`, `KeyPath` FROM `Component`' @('Id', 'Directory', 'Attributes', 'Guid', 'KeyPath'))
            $registryRows = @{}
            Read-MsiTable $database 'SELECT `Registry`, `Root`, `Key` FROM `Registry`' @('Id', 'Root', 'Key') |
                ForEach-Object {
                    Assert-Msi ($_.Root -eq '1' -and
                        ($_.Key -eq "Software\AgentSignaler\Installer\$name" -or
                         $_.Key.StartsWith("Software\AgentSignaler\Installer\$name\", [StringComparison]::Ordinal))) 'Installer registry ownership must not extend to host transcript/profile settings.'
                    $registryRows[$_.Id] = $_.Root
                }
            foreach ($component in $componentRows) {
                $components[$component.Id] = $component.Directory
                Assert-Msi (([int]$component.Attributes -band 4) -ne 0 -and
                    $registryRows[$component.KeyPath] -eq '1') "$name component needs an HKCU key path: $($component.Id)"
                Assert-Msi ([guid]$component.Guid -ne [guid]::Empty) "$name component GUID is missing."
                Assert-Msi (-not $seenComponentGuids.ContainsKey($component.Guid)) "$name shares a component GUID with another package/component."
                $seenComponentGuids[$component.Guid] = $name
            }
            Assert-Msi (@($componentRows.Guid | Select-Object -Unique).Count -eq $componentRows.Count) "$name has duplicate component GUIDs."
            $removedDirectories = @{}
            Read-MsiTable $database 'SELECT `DirProperty`, `FileName`, `InstallMode` FROM `RemoveFile`' @('Directory', 'Name', 'Mode') |
                ForEach-Object {
                    Assert-Msi ($_.Name -eq '' -and $_.Mode -eq '2') 'Installer cleanup may remove only empty owned directories, never transcript files or wildcard host history.'
                    $directory = $_.Directory
                    $visited = @{}
                    if ($directory -notin @('ApplicationProgramsFolder', 'AgentSignalerFolder', 'ProgramsFolder')) {
                        while ($directory -ne 'INSTALLFOLDER') {
                            Assert-Msi ($directories.ContainsKey($directory) -and -not $visited.ContainsKey($directory)) 'Installer cleanup targets an unowned directory.'
                            $visited[$directory] = $true
                            $directory = $directories[$directory].Parent
                        }
                    }
                    $removedDirectories[$_.Directory] = $true
                }
            foreach ($directory in $components.Values | Select-Object -Unique) {
                Assert-Msi ($removedDirectories.ContainsKey($directory)) "$name directory lacks empty-folder cleanup: $directory"
            }
            $files = @(Read-MsiTable $database 'SELECT `FileName`, `FileSize`, `Component_` FROM `File`' @('Name', 'Size', 'Component'))
            $publishedFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
            $application = if ($name -eq 'Dashboard') { 'Dashboard' } else { 'Configurator' }
            $requiredFiles = @("AgentSignaler.$application.exe", "AgentSignaler.$application.pri",
                'App.xbf', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll')
            if ($name -eq 'Remote') {
                $requiredFiles += @('AgentSignaler.Relay.exe', 'AgentSignaler.Client.exe',
                    'AgentSignaler.Client.dll', 'AgentSignaler.Client.runtimeconfig.json')
            }
            foreach ($required in $requiredFiles) {
                $requiredPath = Join-Path $publish $required
                Assert-Msi ((Test-Path -LiteralPath $requiredPath -PathType Leaf) -and
                    (Get-Item -LiteralPath $requiredPath).Length -gt 0) "$name required self-contained/XAML asset missing: $required"
            }
            if ($name -eq 'Remote') {
                Assert-Msi (Test-Path -LiteralPath (Join-Path $publish 'MainWindow.xbf') -PathType Leaf) 'Configurator MainWindow.xbf is missing.'
                $relayFiles = @($files | Where-Object { ($_.Name -split '\|')[-1] -eq 'AgentSignaler.Relay.exe' })
                Assert-Msi ($relayFiles.Count -eq 1 -and
                    $components[$relayFiles[0].Component] -eq 'INSTALLFOLDER') 'Remote MSI must bundle AgentSignaler.Relay.exe beside the configurator.'
                $mainComponents = @(Read-MsiTable $database 'SELECT `Component_` FROM `FeatureComponents` WHERE `Feature_` = ''Main''' @('Component'))
                Assert-Msi ($mainComponents.Component -contains $relayFiles[0].Component) 'Remote relay must be included in the Main installation feature.'
                $clientFiles = @($files | Where-Object { ($_.Name -split '\|')[-1] -eq 'AgentSignaler.Client.exe' })
                Assert-Msi ($clientFiles.Count -eq 1 -and
                    $components[$clientFiles[0].Component] -eq 'INSTALLFOLDER' -and
                    $mainComponents.Component -contains $clientFiles[0].Component) 'Remote Client must be installed beside Configurator and Relay in the Main feature.'
                $binaryVersions = @('Configurator', 'Relay', 'Client') | ForEach-Object {
                    [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish "AgentSignaler.$_.exe")).ProductVersion
                }
                Assert-Msi (@($binaryVersions | Select-Object -Unique).Count -eq 1) 'All three remote binaries must be published as one versioned unit.'
            }
            Assert-Msi ($files.Count -eq $publishedFiles.Count) "$name MSI does not contain the complete publish tree."
            foreach ($file in $files) {
                $relative = ($file.Name -split '\|')[-1]
                $directory = $components[$file.Component]
                $visited = @{}
                while ($directory -ne 'INSTALLFOLDER') {
                    Assert-Msi ($directories.ContainsKey($directory) -and -not $visited.ContainsKey($directory)) "$name invalid payload directory tree."
                    $visited[$directory] = $true
                    $entry = $directories[$directory]
                    $segment = (($entry.Name -split ':')[0] -split '\|')[-1]
                    if ($segment -ne '.') { $relative = Join-Path $segment $relative }
                    $directory = $entry.Parent
                }
                Assert-ApplicationPayloadPath $relative
                $path = Join-Path $publish $relative
                Assert-Msi (Test-Path -LiteralPath $path -PathType Leaf) "$name missing published source: $relative"
                Assert-Msi ((Get-Item -LiteralPath $path).Length -eq [long]$file.Size) "$name payload size differs: $relative"
            }

            $shortcuts = @(Read-MsiTable $database 'SELECT `Target` FROM `Shortcut`' @('Target'))
            $executable = if ($name -eq 'Dashboard') { 'Dashboard' } else { 'Configurator' }
            $expectedShortcuts = @("[INSTALLFOLDER]AgentSignaler.$executable.exe")
            if ($name -eq 'Remote') { $expectedShortcuts += '[INSTALLFOLDER]AgentSignaler.Client.exe' }
            Assert-Msi ($shortcuts.Count -eq $expectedShortcuts.Count -and
                @($shortcuts | Where-Object { $_.Target -notin $expectedShortcuts }).Count -eq 0 -and
                @($shortcuts.Target | Select-Object -Unique).Count -eq $expectedShortcuts.Count) "$name shortcut target mismatch."
            $media = @(Read-MsiTable $database 'SELECT `Cabinet` FROM `Media`' @('Cabinet'))
            Assert-Msi ($media.Count -gt 0 -and @($media | Where-Object { $_.Cabinet -notlike '#*' }).Count -eq 0) "$name payload is not fully embedded."
            $sequence = @{}
            Read-MsiTable $database 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`' @('Action', 'Condition', 'Sequence') |
                ForEach-Object { $sequence[$_.Action] = $_ }
            Assert-Msi ([int]$sequence['RemoveExistingProducts'].Sequence -gt [int]$sequence['InstallInitialize'].Sequence -and
                [int]$sequence['RemoveExistingProducts'].Sequence -lt [int]$sequence['InstallFiles'].Sequence) "$name major upgrade is not transactional."
            $actions = @()
            if ($sequence.ContainsKey('RemoveRemoteIntegration') -or $sequence.ContainsKey('RemoveDashboardIntegration')) {
                $actions = @(Read-MsiTable $database 'SELECT `Action`, `Type`, `Target` FROM `CustomAction`' @('Action', 'Type', 'Target'))
            }
            if ($name -eq 'Remote') {
                Assert-Msi (@($actions | Where-Object { $_.Action -notin @(
                    'SetIntegrationTransaction', 'RollbackRemoteIntegration', 'RemoveRemoteIntegration',
                    'StopRemoteClientForUpdate', 'RollbackLegacyHeartbeatMigration', 'MigrateLegacyHeartbeat',
                    'CommitLegacyHeartbeatMigration') }).Count -eq 0) 'Unexpected remote custom action; remote servicing must not configure hooks/startup, launch UI, or require cloud access.'
                $stop = @($actions | Where-Object { $_.Action -eq 'StopRemoteClientForUpdate' })
                Assert-Msi ($stop.Count -eq 1 -and
                    $stop[0].Target -eq '"[INSTALLFOLDER]AgentSignaler.Relay.exe" --stop-client-for-update' -and
                    ([int]$stop[0].Type -band 2048) -eq 0 -and
                    ([int]$stop[0].Type -band 1024) -eq 0 -and
                    ([int]$stop[0].Type -band 192) -eq 0) 'Remote upgrade must synchronously stop only the owned current-user client through the still-installed matching helper, checking its exit code.'
                Assert-Msi (($sequence['StopRemoteClientForUpdate'].Condition -replace '\s', '') -eq 'REMOTECLIENTEXISTSANDNOTREMOVE="ALL"' -and
                    [int]$sequence['StopRemoteClientForUpdate'].Sequence -gt [int]$sequence['InstallInitialize'].Sequence -and
                    [int]$sequence['StopRemoteClientForUpdate'].Sequence -lt [int]$sequence['RemoveExistingProducts'].Sequence) 'Remote client shutdown must execute immediately before old files are removed, not be deferred until script execution.'
                $migrationCommands = @{
                    RollbackLegacyHeartbeatMigration = '--rollback-legacy-heartbeat'
                    MigrateLegacyHeartbeat = '--migrate-legacy-heartbeat'
                    CommitLegacyHeartbeatMigration = '--commit-legacy-heartbeat'
                }
                foreach ($id in $migrationCommands.Keys) {
                    $migration = @($actions | Where-Object { $_.Action -eq $id })
                    $target = '"[INSTALLFOLDER]AgentSignaler.Relay.exe" ' + $migrationCommands[$id] +
                        ' --transaction-id "[IntegrationTransaction]"'
                    Assert-Msi ($migration.Count -eq 1 -and $migration[0].Target -eq $target -and
                        ([int]$migration[0].Type -band 2048) -eq 0 -and
                        ([int]$migration[0].Type -band 1024) -ne 0 -and
                        (([int]$migration[0].Type -band 256) -ne 0) -eq $id.StartsWith('Rollback') -and
                        (([int]$migration[0].Type -band 512) -ne 0) -eq $id.StartsWith('Commit')) 'Legacy task migration requires current-user transactional helpers.'
                    Assert-Msi (($sequence[$id].Condition -replace '\s', '') -eq 'NOTREMOVE="ALL"' -and
                        [int]$sequence[$id].Sequence -gt [int]$sequence['InstallFiles'].Sequence -and
                        [int]$sequence[$id].Sequence -lt [int]$sequence['InstallFinalize'].Sequence) 'Task-only migration must use the newly installed helper, including on legacy upgrades and repair.'
                }
                Assert-Msi ([int]$sequence['RollbackLegacyHeartbeatMigration'].Sequence -lt [int]$sequence['MigrateLegacyHeartbeat'].Sequence -and
                    [int]$sequence['MigrateLegacyHeartbeat'].Sequence -lt [int]$sequence['CommitLegacyHeartbeatMigration'].Sequence) 'Legacy task migration rollback/commit ordering is invalid.'
            }
            foreach ($action in $actions | Where-Object { $_.Action -match '^(Remove|Rollback)(Remote|Dashboard)Integration$' }) {
                Assert-Msi (($sequence[$action.Action].Condition -replace '\s', '') -eq 'REMOVE="ALL"ANDNOTUPGRADINGPRODUCTCODE') "$name cleanup must skip upgrades and repair."
                Assert-Msi (([int]$action.Type -band 2048) -eq 0) "$name cleanup must run as the installing user."
                Assert-Msi (([int]$action.Type -band 1024) -ne 0) "$name cleanup must execute inside the installer transaction."
                Assert-Msi ((([int]$action.Type -band 256) -ne 0) -eq $action.Action.StartsWith('Rollback')) "$name rollback action has the wrong execution mode."
                Assert-Msi ($action.Target.Contains('--transaction-id "[IntegrationTransaction]"')) "$name cleanup must share its captured transaction identifier."
                Assert-Msi ([int]$sequence[$action.Action].Sequence -lt [int]$sequence['RemoveFiles'].Sequence) "$name cleanup must precede file removal."
            }
            Assert-Msi ($sequence.ContainsKey("Remove${name}Integration") -and $sequence.ContainsKey("Rollback${name}Integration")) "$name cleanup actions missing."
            Assert-Msi ([int]$sequence["Rollback${name}Integration"].Sequence -lt [int]$sequence["Remove${name}Integration"].Sequence) "$name rollback must be scheduled before cleanup."
            $transaction = @($actions | Where-Object { $_.Action -eq 'SetIntegrationTransaction' })
            Assert-Msi ($transaction.Count -eq 1 -and $transaction[0].Target -eq '[ProductCode]|[Date]|[Time]' -and
                [int]$sequence['SetIntegrationTransaction'].Sequence -lt [int]$sequence['InstallInitialize'].Sequence) "$name must capture its transaction identifier once before initialization."
            Write-Host "$name MSI verified read-only: $($files.Count) payload files, x64/per-user, shortcut, embedded cabinets, upgrade and cleanup tables."
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }

& (Join-Path $PSScriptRoot 'Test-RpcHostInstaller.ps1') -Version $Version
