Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')

function Assert-LocalSigningInputs {
    param([string] $Version, [string] $Endpoint, [string] $Account,
        [string] $Profile, [string] $ExpectedPublisher)
    if ((ConvertTo-AgentSignalerVersion $Version).ToString() -cne $Version) {
        throw 'A canonical three-part MSI version is required.'
    }
    if ($Endpoint -notmatch '\Ahttps://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.codesigning\.azure\.net/?\z') {
        throw 'Use a public HTTPS *.codesigning.azure.net base endpoint without a port, path, query or credentials.'
    }
    foreach ($name in @($Account, $Profile)) {
        if ($name -notmatch '\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])?\z') {
            throw 'Signing account and profile must be 1-64 ASCII letters, digits or hyphens, with alphanumeric ends.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or $ExpectedPublisher.Length -gt 200 -or
        $ExpectedPublisher -match '[\x00-\x1f\x7f]' -or $ExpectedPublisher -cne $ExpectedPublisher.Trim()) {
        throw 'ExpectedPublisher must be an exact, nonempty certificate publisher without control characters.'
    }
}

function Assert-LocalSigningPath {
    param([string] $Path, [string] $Root)
    if ($Path -notmatch '\A[A-Za-z]:\\' -or $Path.Substring(2) -match '[:\x00-\x1f\x7f"%!&|<>^]') {
        throw 'Signing paths must be absolute local Windows paths without alternate streams or shell metacharacters.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($Root) {
        $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\')
        if (-not $full.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -and
            -not $full.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Signing path escapes its allowed directory.'
        }
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Signing paths must not contain reparse points.'
            }
        }
        $current = Split-Path -Parent $current
    }
    return $full
}

function Get-LocalSigningFiles {
    param([string] $Directory, [string] $Root)
    [void](Assert-LocalSigningPath $Directory $Root)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Missing signing payload directory.' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    $count = 0
    $bytes = 0L
    while ($pending.Count -gt 0) {
        $parent = $pending.Pop()
        Get-ChildItem -LiteralPath $parent -Force | ForEach-Object {
            $count++
            if ($count -gt 10000) { throw 'Signing payload exceeds the 10,000-entry limit.' }
            [void](Assert-LocalSigningPath $_.FullName $Root)
            if ($_.PSIsContainer) { $pending.Push($_.FullName) }
            else {
                $bytes += $_.Length
                if ($_.Length -gt 1GB -or $bytes -gt 8GB) { throw 'Signing payload exceeds its size limit.' }
                $_
            }
        }
    }
}

function Get-LocalSigningTargets {
    param([string[]] $Directories, [string] $Root)
    $targets = @(
        foreach ($directory in $Directories) {
            $files = @(Get-LocalSigningFiles $directory $Root)
            if (@($files | Where-Object { $_.Name -like 'AgentSignaler.RpcHost.*' }).Count) {
                throw 'Retired product in application payload; republish supported applications.'
            }
            if (@($files | Where-Object { $_.Name -like 'AgentSignaler.*' -and $_.Extension -in @('.exe', '.dll') }).Count -eq 0) {
                throw 'Each payload must contain first-party application binaries, not only updater scripts.'
            }
            $selected = @($files | Where-Object {
                ($_.Name -like 'AgentSignaler.*' -and $_.Extension -in @('.exe', '.dll')) -or
                $_.Name -in $script:AgentSignalerUpdaterFiles
            })
            if ($selected.Count -eq 0) { throw 'Each payload must contain first-party application binaries.' }
            $selected.FullName
        }
    )
    if ($targets.Count -gt 256) { throw 'More than 256 first-party signing targets.' }
    return $targets | Sort-Object
}

function Assert-LocalSigningX64 {
    param([string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $stream.Length -gt 128MB -or $reader.ReadUInt16() -ne 0x5a4d) {
            throw 'Signing tool is not a supported PE image.'
        }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 26) { throw 'Invalid signing tool PE header.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'SignTool and Azure.CodeSigning.Dlib.dll must both be x64.'
        }
    }
    finally { $reader.Dispose() }
}

function Resolve-LocalSigningTool {
    param([string] $ExplicitPath, [string] $Name)
    $candidates = [Collections.Generic.List[string]]::new()
    if ($ExplicitPath) { $candidates.Add((Assert-LocalSigningPath $ExplicitPath)) }
    else {
        if ($Name -eq 'Azure.CodeSigning.Dlib.dll' -and $env:LOCALAPPDATA) {
            $candidates.Add((Join-Path $env:LOCALAPPDATA "Microsoft\MicrosoftArtifactSigningClientTools\$Name"))
        }
        if ($Name -eq 'signtool.exe' -and ${env:ProgramFiles(x86)}) {
            $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
            if (Test-Path -LiteralPath $kits -PathType Container) {
                [void](Assert-LocalSigningPath $kits)
                Get-ChildItem -LiteralPath $kits -Directory |
                    Where-Object { $_.Name -match '\A10\.0\.[0-9]+\.0\z' } |
                    Sort-Object { [version]$_.Name } -Descending |
                    Select-Object -First 32 | ForEach-Object {
                        $candidates.Add((Join-Path $_.FullName "x64\$Name"))
                    }
            }
        }
    }
    foreach ($candidate in $candidates) {
        [void](Assert-LocalSigningPath $candidate)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        if ([IO.Path]::GetFileName($candidate) -ine $Name) { throw 'Unexpected signing tool filename.' }
        Assert-LocalSigningX64 $candidate
        $signature = Get-AuthenticodeSignature -LiteralPath $candidate
        # The official Artifact Signing client currently ships an unsigned native Dlib.
        if ($Name -eq 'Azure.CodeSigning.Dlib.dll' -and $signature.Status -eq 'NotSigned') {
            return $candidate
        }
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.GetNameInfo(
                [Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -notin
                @('Microsoft Corporation', 'Microsoft Windows')) {
            throw 'Signing tools must have valid Microsoft Authenticode signatures.'
        }
        return $candidate
    }
    throw "Cannot find verified x64 $Name. Install official tools separately or supply its explicit path."
}

function Resolve-LocalSigningAzureCli {
    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($name in @('az.exe', 'az.cmd')) {
        foreach ($command in @(Get-Command $name -CommandType Application -ErrorAction SilentlyContinue)) {
            $candidates.Add($command.Source)
        }
    }
    foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($programFiles) {
            $candidates.Add((Join-Path $programFiles 'Microsoft SDKs\Azure\CLI2\wbin\az.cmd'))
        }
    }
    foreach ($candidate in $candidates) {
        [void](Assert-LocalSigningPath $candidate)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Azure CLI is required. Install it and sign in separately; this script never logs in.'
}

function Invoke-LocalSigningNative {
    param([string] $Path, [string[]] $Arguments)
    # Native diagnostics can contain private signing/account details; expose only an exit code.
    $ErrorActionPreference = 'Continue'
    try { & $Path @Arguments *> $null; $code = $LASTEXITCODE }
    catch { throw 'Signing command could not be executed.' }
    if ($code -ne 0) { throw "Signing command failed (exit $code)." }
}

function Invoke-LocalSigningBuild {
    param([string] $Root, [string] $Version, [string] $Configuration, [bool] $NoRestore, [bool] $Publish)
    $arguments = @{ Version = $Version; Configuration = $Configuration; NoRestore = $NoRestore }
    if ($Publish) { $arguments.PublishOnly = $true }
    else { $arguments.SkipPublish = $true; $arguments.ApplicationMsisOnly = $true }
    & (Join-Path $Root 'scripts\Build-Installers.ps1') @arguments
}

function Assert-LocalSignedFile {
    param([string] $Path, [string] $SignTool, [string] $Publisher)
    Invoke-LocalSigningNative $SignTool @('verify', '/pa', '/all', '/tw', $Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.GetNameInfo(
            [Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $Publisher -or
        $null -eq $signature.TimeStamperCertificate) {
        throw 'Signature, expected publisher or required timestamp verification failed.'
    }
}

function Invoke-LocalSignFile {
    param([string] $Path, [string] $SignTool, [string] $Dlib, [string] $Metadata, [string] $Publisher)
    $pending = Join-Path (Split-Path -Parent $Metadata) ('pending-' + [IO.Path]::GetFileName($Path))
    try {
        [void](Assert-LocalSigningPath $Path)
        [void](Assert-LocalSigningPath $pending)
        # Sign an independent copy: build outputs may be hard-linked to other build/cache paths.
        [IO.File]::Copy($Path, $pending, $false)
        # /tr requests RFC3161, not the legacy Authenticode /t timestamp; do not append signatures.
        Invoke-LocalSigningNative $SignTool @('sign', '/fd', 'SHA256',
            '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256',
            '/dlib', $Dlib, '/dmdf', $Metadata, $pending)
        Assert-LocalSignedFile $pending $SignTool $Publisher
        [void](Assert-LocalSigningPath $Path)
        [IO.File]::Replace($pending, $Path, [NullString]::Value)
    }
    finally {
        if (Test-Path -LiteralPath $pending) { [IO.File]::Delete($pending) }
    }
}

function Disconnect-LocalSigningBackupLinks {
    param([string] $Path, [string] $Root)
    $files = if (Test-Path -LiteralPath $Path -PathType Container) {
        @(Get-LocalSigningFiles $Path $Root)
    }
    else { @(Get-Item -LiteralPath $Path -Force) }
    foreach ($file in $files) {
        if (-not $file.PSObject.Properties['LinkType'] -or $file.LinkType -ne 'HardLink') { continue }
        $copy = $file.FullName + '.signing-copy-' + [guid]::NewGuid().ToString('N')
        try {
            [void](Assert-LocalSigningPath $copy $Root)
            [IO.File]::Copy($file.FullName, $copy, $false)
            [IO.File]::Replace($copy, $file.FullName, [NullString]::Value)
        }
        finally { if (Test-Path -LiteralPath $copy) { [IO.File]::Delete($copy) } }
    }
}

function Remove-LocalSigningOutput {
    param([string] $Path, [string] $Root)
    [void](Assert-LocalSigningPath $Path $Root)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Get-LocalSigningFiles $Path $Root | Out-Null
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
}

function ConvertTo-SignedMsiDestination {
    param([string] $Path, [string] $Root)
    # Lexical checks only: WhatIf must not contact a UNC server, even to inspect it.
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[/\x00-\x1f\x7f"<>|?*]' -or
        ($Path -notmatch '\A[A-Za-z]:\\' -and $Path -notmatch '\A\\\\[^\\]+\\[^\\]+\\') -or
        $Path -match '\A\\\\[.?]\\' -or $Path.Substring(2) -match ':') {
        throw 'DestinationPath must be an absolute local or UNC directory without device paths, wildcards or alternate streams.'
    }
    foreach ($part in ($Path -split '\\' | Where-Object { $_ -and $_ -notmatch '\A[A-Za-z]:\z' })) {
        if ($part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)') {
            throw 'DestinationPath contains an ambiguous or reserved Windows path component.'
        }
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) {
        throw 'DestinationPath must not be a drive or share root.'
    }
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($full.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $boundary.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DestinationPath must not overlap the repository, its ancestors, artifacts or signing sources.'
    }
    return $full
}

function Assert-SignedMsiDestination {
    param([string] $Path, [switch] $AllowActiveCopy)
    $current = $Path
    while ($current) {
        $directory = Get-Item -LiteralPath $current -Force
        if (-not $directory.PSIsContainer -or
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The existing destination and its ancestors must be ordinary directories, not reparse points.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    foreach ($name in @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')) {
        $target = [IO.Path]::Combine($Path, $name)
        if (Test-Path -LiteralPath $target) {
            $file = Get-Item -LiteralPath $target -Force
            if ($file.PSIsContainer -or $file.Length -gt 1GB -or
                ($file.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::ReadOnly)) -ne 0) {
                throw 'An MSI destination is a directory, reparse point, read-only file or oversized file.'
            }
        }
    }
    if (-not $AllowActiveCopy -and (Test-Path -LiteralPath ([IO.Path]::Combine($Path, '.AgentSignaler-copy.lock')))) {
        throw 'An active or interrupted MSI copy exists at the destination; inspect .AgentSignaler-copy.lock before retrying.'
    }
}

function Get-SignedMsiHash {
    param([string] $Path)
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
    try {
        $hash = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $hash.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-SignedMsiFileStamp {
    param([string] $Path)
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or $file.Length -gt 1GB -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Copy target is not a bounded ordinary file.'
    }
    [pscustomobject]@{
        Hash = Get-SignedMsiHash $Path; Length = $file.Length
        Modified = $file.LastWriteTimeUtc.Ticks; Created = $file.CreationTimeUtc.Ticks
    }
}

function Assert-SignedMsiFileUnchanged {
    param([string] $Path, $Stamp)
    if ($null -eq $Stamp) { throw 'Copy file ownership could not be established.' }
    $current = Get-SignedMsiFileStamp $Path
    if ($current.Hash -cne $Stamp.Hash -or $current.Length -ne $Stamp.Length -or
        $current.Modified -ne $Stamp.Modified -or $current.Created -ne $Stamp.Created) {
        throw 'A destination file changed concurrently; it will not be overwritten or removed.'
    }
}

function Copy-SignedMsiStream {
    param([IO.Stream] $Source, [IO.Stream] $Destination)
    $Source.CopyTo($Destination, 1MB)
}

function Copy-AgentSignalerSignedMsis {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [string] $Root = (Split-Path -Parent $PSScriptRoot),
        [string] $DestinationPath = '\\tsclient\C\public\AgentSignaler',
        [string] $ExpectedPublisher = 'Andy Sterland',
        [string] $SignToolPath
    )
    $ErrorActionPreference = 'Stop'
    $destination = ConvertTo-SignedMsiDestination $DestinationPath $Root
    $names = @('AgentSignaler.Dashboard.msi', 'AgentSignaler.Remote.msi')
    if (-not $PSCmdlet.ShouldProcess($destination, "Verify and copy $($names -join ', ') only; preserve local signed outputs")) {
        return
    }
    $Root = Assert-LocalSigningPath $Root
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or $ExpectedPublisher.Length -gt 200 -or
        $ExpectedPublisher -match '[\x00-\x1f\x7f]' -or $ExpectedPublisher -cne $ExpectedPublisher.Trim()) {
        throw 'ExpectedPublisher must be an exact, nonempty certificate publisher without control characters.'
    }
    $signTool = Resolve-LocalSigningTool $SignToolPath 'signtool.exe'
    $sourceDirectory = Join-Path $Root 'artifacts\msi'
    $manifest = Assert-LocalSigningPath (Join-Path $sourceDirectory 'SHA256SUMS.txt') $Root
    if ((Test-Path -LiteralPath (Join-Path $Root 'artifacts\signing-staging\active.lock')) -or
        -not (Test-Path -LiteralPath $manifest -PathType Leaf) -or (Get-Item -LiteralPath $manifest).Length -gt 64KB) {
        throw 'Copy requires a completed local signing transaction and its bounded SHA256SUMS.txt.'
    }
    $pins = [Collections.Generic.List[IO.Stream]]::new()
    $entries = @()
    $lock = $null
    $lockPath = [IO.Path]::Combine($destination, '.AgentSignaler-copy.lock')
    $completed = $false
    $recoveryRequired = $false
    $failure = $null
    try {
        $pins.Add([IO.File]::Open($manifest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $checksums = Read-AgentSignalerChecksums @(Get-Content -LiteralPath $manifest)
        foreach ($name in $names) {
            $source = Assert-LocalSigningPath (Join-Path $sourceDirectory $name) $Root
            $pin = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $pins.Add($pin)
            if ($pin.Length -le 0 -or $pin.Length -gt 1GB -or (Get-SignedMsiHash $source) -cne $checksums[$name]) {
                throw 'Local MSI does not match the completed signing checksum manifest.'
            }
            Assert-LocalSignedFile $source $signTool $ExpectedPublisher
            $id = [guid]::NewGuid().ToString('N')
            $entries += [pscustomobject]@{
                Name = $name; Source = $pin; Hash = $checksums[$name]
                Target = [IO.Path]::Combine($destination, $name)
                Pending = [IO.Path]::Combine($destination, ".AgentSignaler-copy-$id-pending-$name")
                Backup = [IO.Path]::Combine($destination, ".AgentSignaler-copy-$id-backup-$name")
                Original = $null; Staged = $null; OwnsPending = $false; Promoting = $false
            }
        }
        Assert-SignedMsiDestination $destination
        foreach ($entry in $entries) {
            if (Test-Path -LiteralPath $entry.Target) {
                # A same-named foreign/unsigned file is not an Agent Signaler package to replace.
                Assert-LocalSignedFile $entry.Target $signTool $ExpectedPublisher
                $entry.Original = Get-SignedMsiFileStamp $entry.Target
            }
        }
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $journal = @{
            Schema = 'AgentSignaler.SignedMsiCopy.v1'
            Files = @($entries | ForEach-Object {
                @{ Name = $_.Name; SHA256 = $_.Hash; Original = $_.Original
                   Pending = [IO.Path]::GetFileName($_.Pending); Backup = [IO.Path]::GetFileName($_.Backup) }
            })
        } | ConvertTo-Json -Depth 5
        $bytes = [Text.Encoding]::UTF8.GetBytes($journal)
        $lock.Write($bytes, 0, $bytes.Length)
        $lock.Flush()
        foreach ($entry in $entries) {
            Assert-SignedMsiDestination $destination -AllowActiveCopy
            $output = [IO.FileStream]::new($entry.Pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
                [IO.FileShare]::None, 1MB, [IO.FileOptions]::SequentialScan)
            $entry.OwnsPending = $true
            try { Copy-SignedMsiStream $entry.Source $output }
            finally {
                $output.Dispose()
                $entry.Staged = Get-SignedMsiFileStamp $entry.Pending
            }
            if ($entry.Staged.Hash -cne $entry.Hash) { throw 'Staged MSI copy hash verification failed.' }
        }
        foreach ($entry in $entries) {
            Assert-SignedMsiDestination $destination -AllowActiveCopy
            Assert-SignedMsiFileUnchanged $entry.Pending $entry.Staged
            if ($null -ne $entry.Original) {
                Assert-SignedMsiFileUnchanged $entry.Target $entry.Original
                [IO.File]::Move($entry.Target, $entry.Backup)
                Assert-SignedMsiFileUnchanged $entry.Backup $entry.Original
            }
            elseif (Test-Path -LiteralPath $entry.Target) { throw 'An MSI destination appeared concurrently.' }
            $entry.Promoting = $true
            [IO.File]::Move($entry.Pending, $entry.Target)
            Assert-SignedMsiFileUnchanged $entry.Target $entry.Staged
        }
        foreach ($entry in $entries) { Assert-SignedMsiFileUnchanged $entry.Target $entry.Staged }
        $completed = $true
    }
    catch { $failure = $_ }
    finally {
        if ($lock) {
            if (-not $completed) {
                for ($i = $entries.Count - 1; $i -ge 0; $i--) {
                    $entry = $entries[$i]
                    try {
                        Assert-SignedMsiDestination $destination -AllowActiveCopy
                        if ($entry.Promoting -and -not (Test-Path -LiteralPath $entry.Pending) -and
                            (Test-Path -LiteralPath $entry.Target)) {
                            Assert-SignedMsiFileUnchanged $entry.Target $entry.Staged
                            [IO.File]::Move($entry.Target, $entry.Pending)
                        }
                        if (Test-Path -LiteralPath $entry.Backup) {
                            Assert-SignedMsiFileUnchanged $entry.Backup $entry.Original
                            [IO.File]::Move($entry.Backup, $entry.Target)
                        }
                    }
                    catch { $recoveryRequired = $true }
                }
            }
            foreach ($entry in $entries) {
                foreach ($file in @(
                    @{ Path = $entry.Pending; Stamp = $entry.Staged; Owned = $entry.OwnsPending },
                    @{ Path = $entry.Backup; Stamp = $entry.Original; Owned = $completed })) {
                    try {
                        if ($file.Owned -and (Test-Path -LiteralPath $file.Path)) {
                            Assert-SignedMsiDestination $destination -AllowActiveCopy
                            Assert-SignedMsiFileUnchanged $file.Path $file.Stamp
                            [IO.File]::Delete($file.Path)
                        }
                    }
                    catch { $recoveryRequired = $true }
                }
            }
            $lock.Dispose()
            if (-not $recoveryRequired) {
                try {
                    Assert-SignedMsiDestination $destination -AllowActiveCopy
                    [IO.File]::Delete($lockPath)
                }
                catch { $recoveryRequired = $true }
            }
        }
        foreach ($pin in $pins) { $pin.Dispose() }
    }
    if ($recoveryRequired) {
        $paths = @($lockPath) + @($entries | ForEach-Object { $_.Target; $_.Backup; $_.Pending })
        throw "MSI copy requires recovery; destination may contain a mixed set. Local signed files were not changed. Inspect these exact paths (do not overwrite concurrent edits): $($paths -join '; ')"
    }
    if ($failure) {
        throw "MSI copy failed; local files were not changed and any destination replacements were rolled back. $($failure.Exception.Message)"
    }
    Write-Host "Copied and hash-verified only $($names -join ', ') to $destination. No installers were executed."
}

function Invoke-AgentSignalerLocalSigning {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version,
        [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
        [switch] $NoRestore,
        [string] $Endpoint = $env:ARTIFACT_SIGNING_ENDPOINT,
        [string] $Account = $env:ARTIFACT_SIGNING_ACCOUNT,
        [string] $Profile = $env:ARTIFACT_SIGNING_PROFILE,
        [string] $ExpectedPublisher = 'Andy Sterland',
        [string] $SignToolPath,
        [string] $DlibPath,
        [string] $DestinationPath = '\\tsclient\C\public\AgentSignaler'
    )
    $ErrorActionPreference = 'Stop'
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
        throw 'Run signing from 64-bit PowerShell on Windows.'
    }
    Assert-LocalSigningInputs $Version $Endpoint $Account $Profile $ExpectedPublisher
    $Root = Assert-LocalSigningPath $Root
    $destination = ConvertTo-SignedMsiDestination $DestinationPath $Root
    $artifacts = Join-Path $Root 'artifacts'
    $directories = @('dashboard', 'remote') | ForEach-Object { Join-Path $artifacts "publish\$_" }
    $msis = @('Dashboard', 'Remote') | ForEach-Object { Join-Path $artifacts "msi\AgentSignaler.$_.msi" }
    $manifest = Join-Path $artifacts 'msi\SHA256SUMS.txt'
    $stagingRoot = Join-Path $artifacts 'signing-staging'
    foreach ($path in @($directories) + @($msis) + @($manifest, $stagingRoot,
            (Join-Path $artifacts 'installer-staging'), (Join-Path $Root 'scripts\Build-Installers.ps1'))) {
        [void](Assert-LocalSigningPath $path $Root)
    }
    foreach ($directory in $directories) {
        if (Test-Path -LiteralPath $directory) { Get-LocalSigningFiles $directory $Root | Out-Null }
    }
    foreach ($path in @($msis) + @($manifest)) {
        if ((Test-Path -LiteralPath $path) -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'Existing MSI and checksum outputs must be regular files.'
        }
    }
    foreach ($path in @($SignToolPath, $DlibPath)) {
        if ($path) { [void](Assert-LocalSigningPath $path) }
    }
    if (-not $PSCmdlet.ShouldProcess("Dashboard/Remote payloads; destination $destination",
            "Publish $Version ($Configuration), sign/verify, write SHA256SUMS.txt, then copy AgentSignaler.Dashboard.msi and AgentSignaler.Remote.msi")) {
        return
    }
    Assert-SignedMsiDestination $destination
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'scripts\Build-Installers.ps1') -PathType Leaf)) {
        throw 'Build-Installers.ps1 is missing.'
    }
    $signTool = Resolve-LocalSigningTool $SignToolPath 'signtool.exe'
    $dlib = Resolve-LocalSigningTool $DlibPath 'Azure.CodeSigning.Dlib.dll'
    $az = Resolve-LocalSigningAzureCli
    $oldPath = $env:PATH
    $lock = $null
    $stage = $null
    $metadata = $null
    $completed = $false
    $buildStarted = $false
    $rollbackFailed = $false
    $cleanupFailed = $false
    $entries = @()
    try {
        $env:PATH = (Split-Path -Parent $az) + ';' + $oldPath
        Invoke-LocalSigningNative $az @('account', 'show', '--output', 'none', '--only-show-errors')
        if ((Test-Path -LiteralPath $stagingRoot) -and
            @(Get-ChildItem -LiteralPath $stagingRoot -Force | Select-Object -First 1).Count) {
            throw 'Unfinished signing transaction exists in artifacts\signing-staging. Recover its backups before retrying.'
        }
        [void](New-Item -ItemType Directory -Path $stagingRoot -Force)
        $lockPath = Join-Path $stagingRoot 'active.lock'
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stage = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory -Path $stage)
        $metadata = Join-Path $stage 'metadata.json'
        @{
            Endpoint = $Endpoint; CodeSigningAccountName = $Account; CertificateProfileName = $Profile
            ExcludeCredentials = @(
                'EnvironmentCredential', 'WorkloadIdentityCredential', 'ManagedIdentityCredential',
                'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
                'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
        } | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding UTF8
        # Move the manifest first and restore it last, only after every original output is restored.
        $index = 0
        foreach ($path in @($manifest) + @($msis) + @($directories)) {
            $entry = [pscustomobject]@{
                Path = $path; Backup = Join-Path $stage ("backup-$index")
                Existed = (Test-Path -LiteralPath $path)
            }
            $entries += $entry
            $index++
        }
        @{
            Schema = 'AgentSignaler.LocalSigningRecovery.v1'; Version = $Version
            Outputs = @($entries | ForEach-Object {
                @{ Path = $_.Path.Substring($Root.Length + 1)
                   Backup = [IO.Path]::GetFileName($_.Backup); Existed = $_.Existed }
            })
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'recovery.json') -Encoding UTF8
        foreach ($entry in $entries) {
            if ($entry.Existed) {
                Move-Item -LiteralPath $entry.Path -Destination $entry.Backup
                Disconnect-LocalSigningBackupLinks $entry.Backup $Root
            }
        }
        $buildStarted = $true
        Invoke-LocalSigningBuild $Root $Version $Configuration $NoRestore.IsPresent $true
        $targets = @(Get-LocalSigningTargets $directories $Root)
        foreach ($target in $targets) { Invoke-LocalSignFile $target $signTool $dlib $metadata $ExpectedPublisher }
        foreach ($directory in $directories) { Write-AgentSignalerUpdaterDependencies $directory }
        Invoke-LocalSigningBuild $Root $Version $Configuration $NoRestore.IsPresent $false
        foreach ($msi in $msis) {
            [void](Assert-LocalSigningPath $msi $Root)
            if (-not (Test-Path -LiteralPath $msi -PathType Leaf) -or
                (Get-Item -LiteralPath $msi).Length -le 0 -or (Get-Item -LiteralPath $msi).Length -gt 1GB) {
                throw 'Missing or oversized application MSI.'
            }
            Invoke-LocalSignFile $msi $signTool $dlib $metadata $ExpectedPublisher
        }
        foreach ($target in $targets + $msis) {
            [void](Assert-LocalSigningPath $target $Root)
            Assert-LocalSignedFile $target $signTool $ExpectedPublisher
        }
        $pendingManifest = Join-Path $stage 'SHA256SUMS.txt'
        $msis | ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
            "$hash  $([IO.Path]::GetFileName($_))"
        } | Set-Content -LiteralPath $pendingManifest -Encoding ascii
        [void](Read-AgentSignalerChecksums @(Get-Content -LiteralPath $pendingManifest))
        [void](Assert-LocalSigningPath $manifest $Root)
        [IO.File]::Move($pendingManifest, $manifest)
        $completed = $true
    }
    finally {
        try {
            if ($metadata -and (Test-Path -LiteralPath $metadata)) {
                try { Remove-LocalSigningOutput $metadata $Root }
                catch { $cleanupFailed = $true }
            }
            if (-not $completed -and $entries.Count) {
                if ($buildStarted) {
                    try { Remove-LocalSigningOutput $manifest $Root }
                    catch { $rollbackFailed = $true }
                }
                for ($i = $entries.Count - 1; $i -ge 0; $i--) {
                    $entry = $entries[$i]
                    if ($entry.Path -eq $manifest -and $rollbackFailed) { continue }
                    try {
                        if (Test-Path -LiteralPath $entry.Backup) {
                            if ($buildStarted) { Remove-LocalSigningOutput $entry.Path $Root }
                            elseif (Test-Path -LiteralPath $entry.Path) { throw 'Rollback destination changed.' }
                            Move-Item -LiteralPath $entry.Backup -Destination $entry.Path
                        }
                        elseif ($buildStarted -and -not $entry.Existed) {
                            Remove-LocalSigningOutput $entry.Path $Root
                        }
                    }
                    catch { $rollbackFailed = $true }
                }
            }
            if ($lock -and -not $rollbackFailed -and -not $cleanupFailed -and $stage) {
                try {
                    foreach ($entry in $entries) {
                        Remove-LocalSigningOutput $entry.Backup $Root
                    }
                    Remove-LocalSigningOutput (Join-Path $stage 'SHA256SUMS.txt') $Root
                    Remove-LocalSigningOutput (Join-Path $stage 'recovery.json') $Root
                    [void](Assert-LocalSigningPath $stage $Root)
                    [IO.Directory]::Delete($stage)
                }
                catch { $cleanupFailed = $true }
            }
        }
        finally {
            try {
                if ($lock) {
                    $lock.Dispose()
                    if (-not $rollbackFailed -and -not $cleanupFailed) {
                        try { Remove-Item -LiteralPath $lockPath -Force }
                        catch { $cleanupFailed = $true }
                    }
                }
            }
            finally { $env:PATH = $oldPath }
        }
        if ($rollbackFailed) {
            throw 'Signing rollback requires manual recovery from artifacts\signing-staging; no new checksum manifest is valid.'
        }
        if ($cleanupFailed) {
            throw 'Signing cleanup requires manual attention in artifacts\signing-staging; do not retry before resolving it.'
        }
    }
    Write-Host "Signed and verified $Version Dashboard/Remote payloads and MSIs; artifacts\msi\SHA256SUMS.txt is ready. Nothing was installed or uploaded to GitHub."
    try {
        Copy-AgentSignalerSignedMsis -Root $Root -DestinationPath $destination -ExpectedPublisher $ExpectedPublisher `
            -SignToolPath $signTool -Confirm:$false
    }
    catch {
        throw "Local signing committed successfully; signed outputs and SHA256SUMS.txt remain available for copy-only retry. $($_.Exception.Message)"
    }
}
