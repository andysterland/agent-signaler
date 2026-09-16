[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [Parameter(Mandatory = $true)][ValidateSet('Dashboard', 'Remote')][string] $ProductName
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$document = New-Object System.Xml.XmlDocument
$document.PreserveWhitespace = $true
$document.Load($Path)
$namespaces = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
$namespaces.AddNamespace('wix', 'http://wixtoolset.org/schemas/v4/wxs')
$sha = [Security.Cryptography.SHA256]::Create()
$changed = $false
try {
    foreach ($component in $document.SelectNodes('//wix:Component', $namespaces)) {
        # WiX cannot auto-generate GUIDs for file components with HKCU key paths.
        # The product and Heat's stable relative-path identity define a custom UUIDv8.
        $identity = "AgentSignaler.Installer.v1|$ProductName|$($component.GetAttribute('Id'))"
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))
        $hex = ([BitConverter]::ToString($hash)).Replace('-', '')
        $guid = '{0}-{1}-8{2}-8{3}-{4}' -f $hex.Substring(0, 8), $hex.Substring(8, 4),
            $hex.Substring(13, 3), $hex.Substring(17, 3), $hex.Substring(20, 12)
        if ($component.GetAttribute('Guid') -ne $guid) {
            $component.SetAttribute('Guid', $guid)
            $changed = $true
        }
    }
    if ($changed) { $document.Save($Path) }
}
finally { $sha.Dispose() }
