[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'PrerequisiteSecurity.ps1')
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root 'installers\AgentSignaler.Dashboard.Bundle\Prerequisites.props'
$scratch = Join-Path $root ("artifacts\prerequisite-update-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $metadata = Get-PrerequisiteMetadata
    $azure = Join-Path $scratch 'azure.msi'
    $redirected = Join-Path $scratch 'redirected.exe'
    $dev = Join-Path $scratch 'devtunnel.exe'
    [void](Receive-Prerequisite $metadata.AzureCliUrl $azure)
    $resolved = Receive-Prerequisite 'https://aka.ms/TunnelsCliDownload/win-x64' $redirected
    Assert-MicrosoftSignature $azure
    Assert-MicrosoftSignature $redirected
    Assert-X64Executable $redirected
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($redirected)
    if ($info.ProductVersion -cne $metadata.DevTunnelsVersion) { throw 'The redirect no longer serves the qualified TunnelValidation version; no metadata was changed.' }
    if (([uri]$resolved).IdnHost -cne 'tunnelsassetsprod.blob.core.windows.net') { throw 'Unexpected official CLI distribution host.' }
    # Resolve the redirect's distribution directory to its published immutable version.
    $versioned = [uri]::new([uri]$resolved, "$($info.ProductVersion)/devtunnel.exe").AbsoluteUri
    [void](Receive-Prerequisite $versioned $dev)
    Assert-MicrosoftSignature $dev
    Assert-X64Executable $dev
    if ((Get-FileHash $dev).Hash -ne (Get-FileHash $redirected).Hash) { throw 'Versioned artifact differs from the official redirect.' }
    $msi = Get-PrerequisiteMsiMetadata $azure
    $metadata.AzureCliSha256 = (Get-FileHash $azure).Hash
    $metadata.AzureCliProductCode = $msi.ProductCode
    $metadata.AzureCliUpgradeCode = $msi.UpgradeCode
    $metadata.DevTunnelsSha256 = (Get-FileHash $dev).Hash
    $metadata.DevTunnelsFileVersion = $info.FileVersion
    $metadata.DevTunnelsUrl = $versioned
    Assert-PrerequisitePayload $azure AzureCli $metadata
    Assert-PrerequisitePayload $dev DevTunnels $metadata
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    foreach ($entry in $metadata.GetEnumerator()) { $xml.Project.PropertyGroup.($entry.Key) = $entry.Value }
    $xml.Save($path)
    Write-Host 'Updated exact signature-verified prerequisite metadata. No packages were installed.'
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
