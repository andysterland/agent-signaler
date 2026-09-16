[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'PrerequisiteSecurity.ps1')
$metadata = Get-PrerequisiteMetadata
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $root ("artifacts\prerequisite-tests-" + [guid]::NewGuid().ToString('N'))
$checks = 0
function Reject([scriptblock] $Action) {
    $script:checks++
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected prerequisite security rejection did not occur.' }
}
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    foreach ($uri in @('http://aka.ms/TunnelsCliDownload/win-x64', 'https://aka.ms.evil.example/file',
        'https://attacker.blob.core.windows.net/file', 'https://evil.example/file',
        'https://user@azcliprod.blob.core.windows.net/file', 'https://azcliprod.blob.core.windows.net:444/file',
        'https://tunnelsassetsprod.blob.core.windows.net/cli/file#fragment',
        'file:///C:/payload.exe', 'https://aka.ms/file?next=http://evil.example')) {
        Reject { Assert-PrerequisiteDownloadUri $uri }
    }
    foreach ($uri in @($metadata.AzureCliUrl, $metadata.DevTunnelsUrl, 'https://aka.ms/TunnelsCliDownload/win-x64')) {
        Assert-PrerequisiteDownloadUri $uri; $checks++
    }
    $unsigned = Join-Path $root 'artifacts\bootstrapper\BootstrapperTests.exe'
    Reject { Assert-MicrosoftSignature $unsigned }
    $dev = Join-Path $root 'artifacts\prerequisites\devtunnel.exe'
    foreach ($field in @('DevTunnelsSha256', 'DevTunnelsFileVersion', 'DevTunnelsVersion')) {
        $wrong = $metadata.Clone()
        $wrong[$field] = 'invalid'
        Reject { Assert-PrerequisitePayload $dev DevTunnels $wrong }
    }
    $azure = Join-Path $root "artifacts\prerequisites\azure-cli-$($metadata.AzureCliVersion)-x64.msi"
    foreach ($field in @('AzureCliSha256', 'AzureCliProductCode', 'AzureCliVersion', 'AzureCliUpgradeCode')) {
        $wrong = $metadata.Clone(); $wrong[$field] = 'invalid'
        Reject { Assert-PrerequisitePayload $azure AzureCli $wrong }
    }
    $bytes = [IO.File]::ReadAllBytes($unsigned)
    $offset = [BitConverter]::ToInt32($bytes, 0x3c)
    $bytes[$offset + 4] = 0x4c; $bytes[$offset + 5] = 0x01
    $x86 = Join-Path $scratch 'x86.exe'; [IO.File]::WriteAllBytes($x86, $bytes)
    Reject { Assert-X64Executable $x86 }
    $text = Join-Path $scratch 'not-an-executable.exe'
    [IO.File]::WriteAllText($text, 'not an executable')
    Reject { Assert-X64Executable $text }
    Write-Host "$checks prerequisite security tests passed: unsafe origins, signer, exact hash, version, MSI identity, and architecture failures."
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
