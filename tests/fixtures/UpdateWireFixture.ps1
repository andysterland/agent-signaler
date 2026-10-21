param([Parameter(Mandatory = $true)][string] $ManifestPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or -not [Environment]::Is64BitProcess) {
	throw 'This non-installable wire fixture requires x64 Windows PowerShell 5.1.'
}
. (Join-Path $PSScriptRoot 'UpdaterPolicy.ps1')
. (Join-Path $PSScriptRoot 'UpdateHandoff.ps1')

function Wait-FixtureSignal([string] $Name) {
	$clock = [Diagnostics.Stopwatch]::StartNew()
	while (-not [IO.File]::Exists((Join-Path $locations.StorageDirectory $Name))) {
		if ($clock.Elapsed.TotalSeconds -ge 30) { throw 'Wire fixture coordination timed out.' }
		Start-Sleep -Milliseconds 25
	}
}

function Assert-FixtureHealth([bool] $Expected) {
	$registrationPath = Join-Path $locations.StorageDirectory ('instance-' + $control.restartProcess.processId + '-' + $control.restartProcess.creationTimeUtcTicks + '.json')
	$registration = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($registrationPath)) 64KB
	$pipe = Open-AgentSignalerAuthenticatedPipe $registration.pipeName $control.actualHost 10000
	try {
		Write-AgentSignalerPipeFrame $pipe ([ordered]@{ schemaVersion = 1; runId = ([guid]$manifest.runId).ToString(); command = 'health' })
		$reply = Read-AgentSignalerPipeFrame $pipe 10000
		Assert-AgentSignalerFields $reply @('schemaVersion', 'runId', 'accepted')
		if ($reply.schemaVersion -ne 1 -or [guid]$reply.runId -ne [guid]$manifest.runId -or
			$reply.accepted -isnot [bool] -or $reply.accepted -ne $Expected) { throw 'Wire fixture health response changed.' }
	}
	finally { $pipe.Dispose() }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid = $identity.User.Value } finally { $identity.Dispose() }
$session = (Get-Process -Id $PID).SessionId
Assert-AgentSignalerProtectedPath $ManifestPath $sid
$manifest = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($ManifestPath)) 64KB
$locations = Assert-AgentSignalerHandoffManifest $manifest $ManifestPath $sid $session
$controlPath = Join-Path $locations.StorageDirectory 'wire-control.json'
Assert-AgentSignalerProtectedPath $controlPath $sid
$control = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($controlPath)) 64KB
$ready = Open-AgentSignalerAuthenticatedPipe $manifest.readinessPipe $control.actualHost 10000
try { Write-AgentSignalerPipeFrame $ready ([ordered]@{ schemaVersion = 1; runId = $manifest.runId; nonce = $manifest.nonce; state = 'ready' }) }
finally { $ready.Dispose() }

Wait-FixtureSignal 'wire-committed.json'
$lease = [IO.File]::Open((Join-Path $locations.StorageDirectory 'maintenance.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
	$gate = Get-AgentSignalerGate $locations.StorageDirectory $manifest -UpdaterOwned
	Write-AgentSignalerRestartGate $locations.StorageDirectory $manifest $gate
	[void](Write-AgentSignalerRestartGrant $manifest.participants[0] $manifest $locations.StorageDirectory $control.restartProcess)
}
finally { $lease.Dispose() }
[Console]::Out.WriteLine('restart-written')

Wait-FixtureSignal 'wire-registered.json'
Assert-FixtureHealth $false
[Console]::Out.WriteLine('health-not-ready')
Wait-FixtureSignal 'wire-ready.json'
Assert-FixtureHealth $true
[Console]::Out.WriteLine('health-ready')
Wait-FixtureSignal 'wire-finish.json'