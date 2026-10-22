[CmdletBinding(SupportsShouldProcess = $true)]
param(
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
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalSigning.ps1')
Invoke-AgentSignalerLocalSigning -Root (Split-Path -Parent $PSScriptRoot) @PSBoundParameters
