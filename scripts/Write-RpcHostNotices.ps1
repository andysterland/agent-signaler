[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [string] $DestinationPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$assets = Get-Content (Join-Path $root 'src\AgentSignaler.RpcHost\obj\project.assets.json') -Raw | ConvertFrom-Json
$depsPath = Join-Path $root "src\AgentSignaler.RpcHost\bin\x64\$Configuration\net10.0-windows10.0.22621.0\win-x64\AgentSignaler.RpcHost.deps.json"
$dependencies = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$folders = @($assets.packageFolders.PSObject.Properties.Name)
$packages = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($library in $dependencies.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'package') {
        $restored = $assets.libraries.PSObject.Properties[$library.Name]
        if ($null -eq $restored) { throw 'Published dependency is missing from the exact restore graph.' }
        $packages[$library.Name] = $restored.Value.path
    }
    elseif ($library.Value.type -eq 'runtimepack') {
        if (-not $library.Name.StartsWith('runtimepack.', [StringComparison]::Ordinal)) {
            throw 'Unexpected published runtime-pack identity.'
        }
        $name = $library.Name.Substring('runtimepack.'.Length)
        $packages[$name] = $name.ToLowerInvariant()
    }
}
$text = [Text.StringBuilder]::new()
[void]$text.AppendLine('Agent Signaler RpcHost - bundled dependency notices')
[void]$text.AppendLine([IO.File]::ReadAllText((Join-Path $root 'LICENSE')))
[void]$text.AppendLine('Generated from the actual published dependency graph, not SDK downloadDependencies. Standard MIT and Apache terms are reproduced in the runtime license/notices below; attributions apply to the named packages.')
foreach ($package in $packages.GetEnumerator() | Sort-Object Key) {
    $directory = $null
    foreach ($folder in $folders) {
        $candidate = Join-Path $folder ($package.Value.Replace('/', '\'))
        if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
    }
    if ($null -eq $directory) { throw 'Resolved package notices are missing from the restore cache.' }
    [void]$text.AppendLine("`r`n================ $($package.Key) ================")
    $nuspec = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec')
    if ($nuspec.Count -ne 1) { throw 'Expected one resolved package manifest.' }
    [xml]$manifest = Get-Content -LiteralPath $nuspec[0].FullName -Raw
    $metadata = $manifest.DocumentElement.SelectSingleNode('*[local-name()="metadata"]')
    foreach ($name in @('authors', 'copyright', 'license', 'licenseUrl', 'projectUrl')) {
        $node = $metadata.SelectSingleNode("*[local-name()='$name']")
        if ($null -ne $node) { [void]$text.AppendLine("${name}: $($node.InnerText)") }
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File |
            Where-Object Name -Match '^(?i:license(?:\.txt)?|third-party-notices\.txt)$' | Sort-Object Name) {
        [void]$text.AppendLine("`r`n$($file.Name):")
        [void]$text.AppendLine([IO.File]::ReadAllText($file.FullName))
    }
}
if ($text.ToString() -notmatch 'Apache License' -or $text.ToString() -notmatch 'END OF TERMS AND CONDITIONS' -or
    $text.ToString() -notmatch 'Permission is hereby granted, free of charge') {
    throw 'Bundled Apache/MIT license terms are incomplete; review notices before packaging.'
}
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = Join-Path $root 'artifacts\publish\rpchost\THIRD-PARTY-NOTICES.txt'
}
[IO.File]::WriteAllText($DestinationPath, $text.ToString(),
    [Text.UTF8Encoding]::new($false))
