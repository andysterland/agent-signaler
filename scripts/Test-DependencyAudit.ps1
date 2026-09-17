[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$json = & dotnet list (Join-Path $root 'AgentSignaler.slnx') package --vulnerable --include-transitive --format json
if ($LASTEXITCODE) { throw 'NuGet dependency audit could not complete.' }
$audit = $json -join "`n" | ConvertFrom-Json
if (($audit.PSObject.Properties.Name -contains 'logs' -and @($audit.logs).Count -gt 0) -or
    @($audit.projects).Count -eq 0) {
    throw 'NuGet audit returned diagnostics or no projects; absence of findings is not a completed audit.'
}
foreach ($project in $audit.projects) {
    if ($project.PSObject.Properties.Name -contains 'logs' -and @($project.logs).Count -gt 0) {
        throw 'A project dependency audit did not complete cleanly.'
    }
    if ($project.PSObject.Properties.Name -notcontains 'frameworks') { continue }
    foreach ($framework in $project.frameworks) {
        foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
            if ($framework.PSObject.Properties.Name -contains $kind -and @($framework.$kind).Count -gt 0) {
                throw 'Known vulnerable NuGet dependencies require review before release.'
            }
        }
    }
}
Write-Host 'NuGet audit completed with no reported vulnerable packages, including transitive dependencies.'
