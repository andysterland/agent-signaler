[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $root 'AgentSignaler.slnx'
[xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
# Solution restore skips disabled installer projects, but the audit includes them.
foreach ($project in $solution.SelectNodes('//Project[Build[@Project="false"]]')) {
    & dotnet restore (Join-Path $root $project.GetAttribute('Path')) -p:Platform=x64
    if ($LASTEXITCODE) { throw 'An excluded solution project could not be restored for dependency audit.' }
}
$json = & dotnet list $solutionPath package --vulnerable --include-transitive --format json
if ($LASTEXITCODE) { throw "NuGet dependency audit could not complete (exit code $LASTEXITCODE)." }
$audit = $json -join "`n" | ConvertFrom-Json
if (($audit.PSObject.Properties.Name -contains 'logs' -and @($audit.logs).Count -gt 0) -or
    ($audit.PSObject.Properties.Name -contains 'problems' -and @($audit.problems).Count -gt 0) -or
    @($audit.projects).Count -eq 0) {
    throw 'NuGet audit returned diagnostics or no projects; absence of findings is not a completed audit.'
}
foreach ($project in $audit.projects) {
    if (($project.PSObject.Properties.Name -contains 'logs' -and @($project.logs).Count -gt 0) -or
        ($project.PSObject.Properties.Name -contains 'problems' -and @($project.problems).Count -gt 0)) {
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
