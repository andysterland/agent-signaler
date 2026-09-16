$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security') -ErrorAction Stop
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility') -ErrorAction Stop
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management') -ErrorAction Stop

function Get-PrerequisiteMetadata {
    $root = Split-Path -Parent $PSScriptRoot
    [xml]$document = Get-Content -LiteralPath (Join-Path $root 'installers\AgentSignaler.Dashboard.Bundle\Prerequisites.props') -Raw
    $values = @{}
    foreach ($node in $document.Project.PropertyGroup.ChildNodes) {
        if ($node.NodeType -eq 'Element') {
            if ($values.ContainsKey($node.Name)) { throw 'Duplicate prerequisite metadata.' }
            $values[$node.Name] = $node.InnerText
        }
    }
    $qualified = [regex]::Match((Get-Content (Join-Path $root 'src\AgentSignaler.Tunneling\TunnelValidation.cs') -Raw),
        'SupportedCliVersion\s*=\s*"([^"]+)"').Groups[1].Value
    if ($values.AzureCliVersion -ne '2.90.0' -or $values.DevTunnelsVersion -ne $qualified -or
        $values.AzureCliSigner -ne 'Microsoft Corporation' -or $values.DevTunnelsSigner -ne 'Microsoft Corporation' -or
        $values.AzureCliUrl -cne "https://azcliprod.blob.core.windows.net/msi/azure-cli-$($values.AzureCliVersion)-x64.msi" -or
        $values.DevTunnelsUrl -cne "https://tunnelsassetsprod.blob.core.windows.net/cli/$qualified/devtunnel.exe" -or
        $values.WindowsAppMinimumVersion -ne '2.0.804.0') { throw 'Prerequisite metadata violates the qualified contract.' }
    foreach ($name in @('AzureCliSha256', 'DevTunnelsSha256')) {
        if ($values[$name] -cnotmatch '^[A-F0-9]{64}$') { throw "Invalid pin: $name" }
    }
    return $values
}

function Assert-MicrosoftSignature([string] $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)' -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
        throw 'Prerequisite verification failed: valid Microsoft Authenticode signature required.'
    }
}

function Assert-X64Executable([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid executable.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw 'Invalid PE offset.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'An x64 executable is required.' }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-PrerequisiteMsiMetadata([string] $Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null; $view = $null; $summary = $null
    try {
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
        $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        [void]$view.Execute()
        $values = @{}
        while ($null -ne ($record = $view.Fetch())) {
            try { $values[$record.StringData(1)] = $record.StringData(2) }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
        $summary = $database.SummaryInformation(0)
        $values['Template'] = $summary.Property(7)
        return $values
    }
    finally {
        if ($null -ne $view) { [void]$view.Close() }
        foreach ($item in @($summary, $view, $database, $installer)) {
            if ($null -ne $item) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
        }
    }
}

function Assert-PrerequisitePayload([string] $Path, [ValidateSet('AzureCli', 'DevTunnels')] [string] $Product, $Metadata) {
    Assert-MicrosoftSignature $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Metadata["${Product}Sha256"]) {
        throw "$Product verification failed: SHA-256 mismatch."
    }
    if ($Product -eq 'AzureCli') {
        $msi = Get-PrerequisiteMsiMetadata $Path
        if ($msi.ProductVersion -ne $Metadata.AzureCliVersion -or $msi.ProductCode -ne $Metadata.AzureCliProductCode -or
            $msi.UpgradeCode -ne $Metadata.AzureCliUpgradeCode -or $msi.Manufacturer -ne 'Microsoft Corporation' -or
            $msi.ProductName -ne 'Microsoft Azure CLI (64-bit)' -or $msi.Template -notlike 'x64;*' -or $msi.ALLUSERS -ne '1') {
            throw 'Azure CLI verification failed: MSI identity, version, architecture or scope mismatch.'
        }
    } else {
        Assert-X64Executable $Path
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if ($info.ProductVersion -cne $Metadata.DevTunnelsVersion -or $info.FileVersion -ne $Metadata.DevTunnelsFileVersion) {
            throw 'Dev Tunnels verification failed: qualified version mismatch.'
        }
    }
}

function Assert-PrerequisiteDownloadUri([uri] $Uri) {
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or -not $Uri.IsDefaultPort -or
        $Uri.UserInfo -ne '' -or $Uri.Fragment -ne '' -or $Uri.Query -ne '' -or
        $Uri.IdnHost -notin @('aka.ms', 'azcliprod.blob.core.windows.net', 'tunnelsassetsprod.blob.core.windows.net')) {
        throw 'Prerequisite download rejected: only the qualified Microsoft HTTPS hosts are allowed.'
    }
}

function Receive-Prerequisite([uri] $Uri, [string] $Destination) {
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(3)
    $client.MaxResponseContentBufferSize = 512MB
    try {
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            Assert-PrerequisiteDownloadUri $Uri
            $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    if ($null -eq $response.Headers.Location) { throw 'Missing redirect location.' }
                    $Uri = [uri]::new($Uri, $response.Headers.Location)
                    continue
                }
                [void]$response.EnsureSuccessStatusCode()
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                if ($bytes.Length -eq 0) { throw 'Empty prerequisite download.' }
                [IO.File]::WriteAllBytes($Destination, $bytes)
                return $Uri.AbsoluteUri
            }
            finally { $response.Dispose() }
        }
        throw 'Prerequisite redirect limit exceeded.'
    }
    finally { $client.Dispose(); $handler.Dispose() }
}

function Get-VerifiedPrerequisitePayloads {
    $metadata = Get-PrerequisiteMetadata
    $cache = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\prerequisites'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    foreach ($product in @('AzureCli', 'DevTunnels')) {
        $name = if ($product -eq 'AzureCli') { "azure-cli-$($metadata.AzureCliVersion)-x64.msi" } else { 'devtunnel.exe' }
        $path = Join-Path $cache $name
        if (-not (Test-Path -LiteralPath $path)) {
            $partial = "$path.$([guid]::NewGuid().ToString('N')).download"
            try {
                [void](Receive-Prerequisite $metadata["${product}Url"] $partial)
                Assert-PrerequisitePayload $partial $product $metadata
                Move-Item -LiteralPath $partial -Destination $path
            }
            finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
        }
        Assert-PrerequisitePayload $path $product $metadata
        Write-Host "$product $($metadata["${product}Version"]) verified."
    }
}
