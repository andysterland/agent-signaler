[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = $PSScriptRoot
. (Join-Path $source 'UpdaterPolicy.ps1')
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action) {
	$rejected = $false
	try { & $Action | Out-Null } catch { $rejected = $true }
	Assert $rejected 'Unsafe handoff fixture was accepted.'
}
$root = Split-Path -Parent $source
$fixture = Join-Path $root ('artifacts\updater-handoff-tests\' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture -Force)
$priorLocalAppData = $env:LOCALAPPDATA
$priorSystemRoot = $env:SystemRoot
try {
	$env:LOCALAPPDATA = $fixture
	$installationDirectory = Join-Path $fixture 'Programs\AgentSignaler\Remote'
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($installationDirectory.ToUpperInvariant()))).Replace('-', '').ToLowerInvariant() }
	finally { $sha.Dispose() }
	$storage = Join-Path $fixture ('AgentSignaler\Updates\' + $key)
	$runId = [guid]::NewGuid().ToString('N')
	$run = Join-Path $storage $runId
	[void](New-Item -ItemType Directory -Path $run -Force)
	foreach ($name in $script:AgentSignalerUpdaterFiles) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $run $name) }
	. (Join-Path $run 'UpdateHandoff.ps1')
	$systemInstaller = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\msiexec.exe'
	$untrustedSystemRoot = Join-Path $fixture 'untrusted-windows'
	$env:SystemRoot = $untrustedSystemRoot
	try { Assert ((Get-AgentSignalerSystemInstallerPath) -ceq $systemInstaller) 'Installer resolution trusted the inherited SystemRoot.' }
	finally { $env:SystemRoot = $priorSystemRoot }
	foreach ($reparsePath in @($systemInstaller, (Split-Path -Parent $systemInstaller), (Split-Path -Parent (Split-Path -Parent $systemInstaller)))) {
		function Get-Item {
			param($LiteralPath, [switch]$Force)
			if ($LiteralPath -ceq $reparsePath) { return [pscustomobject]@{ Attributes = [IO.FileAttributes]::ReparsePoint } }
			return Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force
		}
		try { Reject { Get-AgentSignalerSystemInstallerPath } }
		finally { Remove-Item -LiteralPath Function:\Get-Item }
	}
	$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
	$self = Get-Process -Id $PID
	$parent = [ordered]@{ processId = $PID; creationTimeUtcTicks = $self.StartTime.ToUniversalTime().Ticks;
		userSid = $sid; sessionId = $self.SessionId; executablePath = (Join-Path $installationDirectory 'AgentSignaler.Client.exe') }
	$nativeIdentity = [pscustomobject]@{ processId = $PID; creationTimeUtcTicks = $parent.creationTimeUtcTicks;
		userSid = $sid; sessionId = $self.SessionId; executablePath = $self.Path }
	$nativeParticipant = [pscustomobject]@{ process = $nativeIdentity }
	$nativePin = Open-AgentSignalerParticipantProcessPin $nativeParticipant
	try { Reject { Get-AgentSignalerParticipantExitCode $nativePin } } finally { $nativePin.Dispose() }
	$nativeIdentity.creationTimeUtcTicks++; Reject { Open-AgentSignalerParticipantProcessPin $nativeParticipant }; $nativeIdentity.creationTimeUtcTicks--
	$nativeIdentity.userSid = 'S-1-5-18'; Reject { Open-AgentSignalerParticipantProcessPin $nativeParticipant }; $nativeIdentity.userSid = $sid
	$nativeIdentity.executablePath = $parent.executablePath; Reject { Open-AgentSignalerParticipantProcessPin $nativeParticipant }
	foreach ($fixtureExitCode in @(0, $script:AgentSignalerClientUpdateExitCode)) {
		$signal = Join-Path $fixture ('exit-' + $fixtureExitCode + '.signal')
		$hostPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\WindowsPowerShell\v1.0\powershell.exe'
		$startInfo = [Diagnostics.ProcessStartInfo]::new($hostPath)
		$startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
		$startInfo.EnvironmentVariables['PSModulePath'] = Join-Path (Split-Path -Parent $hostPath) 'Modules'
		$escapedSignal = $signal.Replace("'", "''")
		$startInfo.Arguments = "-NoProfile -NonInteractive -Command `"while (-not [IO.File]::Exists('$escapedSignal')) { Start-Sleep -Milliseconds 10 }; exit $fixtureExitCode`""
		$child = [Diagnostics.Process]::Start($startInfo)
		$processPin = $null
		try {
			$childParticipant = [pscustomobject]@{ process = [pscustomobject]@{ processId = $child.Id;
				creationTimeUtcTicks = $child.StartTime.ToUniversalTime().Ticks; userSid = $sid;
				sessionId = $child.SessionId; executablePath = $hostPath } }
			$processPin = Open-AgentSignalerParticipantProcessPin $childParticipant
			Reject { Get-AgentSignalerParticipantExitCode $processPin }
			[IO.File]::WriteAllText($signal, 'fixture')
			Assert ($child.WaitForExit(10000)) 'Controlled shutdown-intent fixture did not exit.'
			Assert ((Get-AgentSignalerParticipantExitCode $processPin) -eq $fixtureExitCode) 'Held native process handle lost the exact exit intent.'
		}
		finally {
			if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force; $child.WaitForExit() }
			if ($null -ne $processPin) { $processPin.Dispose() }
			$child.Dispose()
		}
	}
	$participant = [ordered]@{ schemaVersion = 1; process = $parent;
		restart = [ordered]@{ application = 'Client'; contextPath = (Join-Path $fixture 'config\remote.json'); background = $true };
		pipeName = 'AgentSignaler.Update.v1.synthetic' }
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $pipeKey = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($installationDirectory.ToUpperInvariant() + '|' + $sid))).Replace('-', '').Substring(0, 24) }
	finally { $sha.Dispose() }
	$participant.pipeName = 'AgentSignaler.Update.v1.' + $pipeKey + '.' + $PID + '.' + $parent.creationTimeUtcTicks
	$payload = 'synthetic-non-installable-msi'
	function HashText([string] $Text) {
		$hash = [Security.Cryptography.SHA256]::Create()
		try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '') }
		finally { $hash.Dispose() }
	}
	$packageHash = HashText $payload
	$checksumText = "$packageHash  AgentSignaler.Dashboard.msi`n$packageHash  AgentSignaler.Remote.msi`n"
	$manifest = [ordered]@{ schemaVersion = 1; runId = $runId; nonce = ('a' * 64); package = 'Remote';
		installDirectory = $installationDirectory; productCode = '{10000000-0000-0000-0000-000000000001}'; installedVersion = '1.0.14';
		releaseId = 123; tag = 'v1.0.15'; assets = @(
			[ordered]@{ id = 101; name = 'AgentSignaler.Dashboard.msi'; size = $payload.Length },
			[ordered]@{ id = 102; name = 'AgentSignaler.Remote.msi'; size = $payload.Length },
			[ordered]@{ id = 103; name = 'SHA256SUMS.txt'; size = $checksumText.Length });
		checksumSha256 = (HashText $checksumText); packageSha256 = $packageHash; parent = $parent;
		participants = @($participant); scripts = @(
			foreach ($name in $script:AgentSignalerUpdaterFiles) {
				$path = Join-Path $run $name
				[ordered]@{ name = $name; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path).Hash }
			}); readinessPipe = ('AgentSignaler.Update.Ready.v1.' + ('a' * 64)); expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(20).ToString('o') }
	$path = Join-Path $run 'handoff.json'
	$security = [Security.AccessControl.DirectorySecurity]::new()
	$security.SetOwner([Security.Principal.SecurityIdentifier]::new($sid))
	$security.SetAccessRuleProtection($true, $false)
	$security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
		[Security.Principal.SecurityIdentifier]::new($sid), [Security.AccessControl.FileSystemRights]::FullControl,
		[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None,
		[Security.AccessControl.AccessControlType]::Allow))
	Set-Acl -LiteralPath $storage -AclObject $security
	Assert-AgentSignalerProtectedPath $storage $sid
	$aclPath = Join-Path $storage 'acl-test.json'
	[IO.File]::WriteAllText($aclPath, '{}')
	Assert-AgentSignalerProtectedPath $aclPath $sid
	$foreignRule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),
		[Security.AccessControl.FileSystemRights]::WriteData, [Security.AccessControl.AccessControlType]::Allow)
	$acl = Get-Acl -LiteralPath $aclPath
	$acl.AddAccessRule($foreignRule); Set-Acl -LiteralPath $aclPath -AclObject $acl
	Reject { Assert-AgentSignalerProtectedPath $aclPath $sid }
	$acl.RemoveAccessRuleSpecific($foreignRule); Set-Acl -LiteralPath $aclPath -AclObject $acl
	$pinTarget = Join-Path $storage 'pin-test'
	[void](New-Item -ItemType Directory -Path $pinTarget)
	$directoryPin = Open-AgentSignalerDirectoryPin $pinTarget
	try { Reject { [IO.Directory]::Move($pinTarget, ($pinTarget + '-replacement')) } }
	finally { $directoryPin.Dispose() }
	$inventoryProbe = {
		param([string] $ProbeDirectory)
		function Get-AgentSignalerInstalledProperty {
			param($ProductCode, $UserSid, $Name)
			switch ($Name) {
				'State' { return '5' }
				'InstallLocation' { return $ProbeDirectory }
				'LocalPackage' { return (Join-Path $ProbeDirectory 'fixture.msi') }
				'VersionString' { return '1.0.14' }
				default { throw 'Unexpected inventory fixture property.' }
			}
		}
		function Read-Package {
			param($Installer, $Path, $App)
			Reject { [IO.Directory]::Move($ProbeDirectory, ($ProbeDirectory + '-replacement')) }
			return [pscustomobject]@{ Version = [version]'1.0.14'; ProductCode = '{10000000-0000-0000-0000-000000000001}' }
		}
		function Get-InstalledVersion { param($Installer, $UpgradeCode) return [version]'1.0.14' }
		Get-AgentSignalerInstalledIdentity $null ([pscustomobject]@{ productCode = '{10000000-0000-0000-0000-000000000001}';
			userSid = $sid; installDirectory = $ProbeDirectory; package = 'Remote' })
	}
	$verifiedInventory = & $inventoryProbe $pinTarget
	Assert ($verifiedInventory.Directory -ceq $pinTarget -and $verifiedInventory.Version -eq [version]'1.0.14') 'Pinned inventory lost the registered custom installation path.'
	$installAcl = Get-Acl -LiteralPath $pinTarget
	$installAcl.AddAccessRule($foreignRule); Set-Acl -LiteralPath $pinTarget -AclObject $installAcl
	Reject { & $inventoryProbe $pinTarget }
	$installAcl.RemoveAccessRuleSpecific($foreignRule); Set-Acl -LiteralPath $pinTarget -AclObject $installAcl
	[IO.Directory]::Move($pinTarget, ($pinTarget + '-replacement'))
	[IO.Directory]::Move(($pinTarget + '-replacement'), $pinTarget)
	foreach ($unsafe in @('relative', '\\server\share\file', 'C:\a:stream', 'C:\a\..\b', 'C:\trailing.\file', 'C:\nul\file', 'C:/wrong')) {
		Reject { Assert-AgentSignalerLocalPath $unsafe }
	}
	function ParsedManifest { return ConvertFrom-AgentSignalerStrictJson ($manifest | ConvertTo-Json -Depth 12) 64KB }
	[void](Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId)
	$start = New-AgentSignalerCapturedStartInfo $participant (ParsedManifest)
	Assert (-not $start.UseShellExecute -and $start.FileName -ceq $parent.executablePath -and
		$start.WorkingDirectory -ceq $installationDirectory -and
		$start.Arguments -ceq ('--background --config "' + $participant.restart.contextPath + '"') -and
		$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_RUN'] -ceq $runId -and
		$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_NONCE'] -ceq $manifest.nonce -and
		$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_PARTICIPANT'] -ceq [string]$PID) 'Captured launch context or nonce-bound authorization changed.'
	$childIdentity = [pscustomobject]@{ processId = 999996; creationTimeUtcTicks = $parent.creationTimeUtcTicks;
		userSid = $sid; sessionId = $self.SessionId; executablePath = $parent.executablePath }
	$grantPath = Write-AgentSignalerRestartGrant $participant (ParsedManifest) $storage $childIdentity
	$grantAcl = Get-Acl -LiteralPath $grantPath
	Assert ($grantAcl.AreAccessRulesProtected -and $grantAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ceq $sid -and
		@($grantAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IsInherited -or $_.IdentityReference.Value -cne $sid }).Count -eq 0) 'Root restart grant ACL does not match managed storage policy.'
	$grant = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($grantPath))
	Assert ($grant.schemaVersion -eq 1 -and $grant.runId -ceq $runId -and $grant.originalProcessId -eq $PID -and
		$grant.process.processId -eq 999996 -and $grant.process.creationTimeUtcTicks -eq $childIdentity.creationTimeUtcTicks -and
		$grant.restart.contextPath -ceq $participant.restart.contextPath -and $grant.restart.application -ceq 'Client') 'Exact child restart grant changed.'
	Reject { Write-AgentSignalerRestartGrant $participant (ParsedManifest) $storage $childIdentity }
	Remove-Item -LiteralPath $grantPath -Force
	$childIdentity.executablePath = Join-Path $installationDirectory 'AgentSignaler.Configurator.exe'
	Reject { Write-AgentSignalerRestartGrant $participant (ParsedManifest) $storage $childIdentity }
	$childIdentity.executablePath = $parent.executablePath; $childIdentity.userSid = 'S-1-5-18'
	Reject { Write-AgentSignalerRestartGrant $participant (ParsedManifest) $storage $childIdentity }
	$gatePath = Join-Path $storage 'gate.json'
	$gate = ConvertFrom-AgentSignalerStrictJson (([ordered]@{ schemaVersion = 1; runId = $runId;
		installDirectory = $installationDirectory; owner = $parent; controller = $parent;
		expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(10).ToString('o'); phase = 'Preparing' }) | ConvertTo-Json -Depth 8)
	$gate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $gatePath -Encoding UTF8
	[void](Get-AgentSignalerGate $storage (ParsedManifest))
	$gate.owner.executablePath = $self.Path; $gate.phase = 'Servicing'
	$gate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $gatePath -Encoding UTF8
	[void](Get-AgentSignalerGate $storage (ParsedManifest) -UpdaterOwned)
	$gate.owner.creationTimeUtcTicks++
	$gate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $gatePath -Encoding UTF8
	Reject { Get-AgentSignalerGate $storage (ParsedManifest) -UpdaterOwned }
	$gate.owner.creationTimeUtcTicks--
	Remove-Item -LiteralPath $gatePath -Force
	$gateBytes = [Text.UTF8Encoding]::new($false).GetBytes(($gate | ConvertTo-Json -Depth 8 -Compress))
	$gateFile = Open-AgentSignalerProtectedWrite $gatePath $sid
	try { $gateFile.Write($gateBytes, 0, $gateBytes.Length); $gateFile.Flush($true) } finally { $gateFile.Dispose() }
	foreach ($attempt in @(1, 2)) {
		Write-AgentSignalerRestartGate $storage (ParsedManifest) $gate
		foreach ($name in @('gate.json', 'restart.json', ('restart-' + $runId + '.json'))) {
			$documentPath = Join-Path $storage $name
			$documentAcl = Get-Acl -LiteralPath $documentPath
			Assert ($documentAcl.AreAccessRulesProtected -and $documentAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ceq $sid -and
				@($documentAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IsInherited -or $_.IdentityReference.Value -cne $sid }).Count -eq 0) 'Root restart document ACL does not match managed storage policy.'
			$document = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($documentPath))
			$expectedPhase = if ($name -ceq ('restart-' + $runId + '.json')) { 'Servicing' } else { 'Restarting' }
			Assert ($document.phase -ceq $expectedPhase -and [guid]$document.runId -eq [guid]$runId -and
				$document.owner.creationTimeUtcTicks -eq $self.StartTime.ToUniversalTime().Ticks) 'Real restart writer lost its gate phase or updater identity.'
		}
		Assert ([IO.File]::ReadAllText($gatePath) -ceq [IO.File]::ReadAllText((Join-Path $storage 'restart.json'))) 'Authoritative and restart gates differ.'
	}
	foreach ($field in @('schemaVersion', 'runId', 'nonce', 'package', 'installDirectory', 'productCode', 'installedVersion',
			'releaseId', 'tag', 'checksumSha256', 'packageSha256', 'readinessPipe', 'expiresAt')) {
		$saved = $manifest[$field]; $manifest[$field] = 'invalid'
		Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest[$field] = $saved
	}
	$manifest['unexpected'] = 'x'; Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest.Remove('unexpected')
	$savedExpiry = $manifest.expiresAt
	foreach ($invalidExpiry in @([DateTimeOffset]::UtcNow.AddSeconds(-1), [DateTimeOffset]::UtcNow.AddMinutes(31))) {
		$manifest.expiresAt = $invalidExpiry.ToString('o')
		Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }
	}
	$manifest.expiresAt = $savedExpiry
	foreach ($suffix in @("`n", "`r`n")) {
		foreach ($field in @('runId', 'nonce', 'tag', 'checksumSha256', 'packageSha256', 'installedVersion')) {
			$saved = $manifest[$field]; $manifest[$field] = $saved + $suffix
			Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest[$field] = $saved
		}
		$saved = $manifest.scripts[0].sha256; $manifest.scripts[0].sha256 = $saved + $suffix
		Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest.scripts[0].sha256 = $saved
		try { Open-AgentSignalerAuthenticatedPipe ($participant.pipeName + $suffix) $parent 1; throw 'A noncanonical pipe name was accepted.' }
		catch { Assert ($_.Exception.Message -ceq 'Unsupported local update pipe.') 'A noncanonical pipe name reached connection instead of validation.' }
	}
	$participant.restart['command'] = 'cmd.exe'; Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $participant.restart.Remove('command')
	$participant.restart.application = 'Dashboard'; Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $participant.restart.application = 'Client'
	$parent.sessionId++; Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $parent.sessionId--
	$manifest.participants = @($participant, $participant); Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest.participants = @($participant)
	$saved = $manifest.scripts; $manifest.scripts = @($saved[0], $saved[1]); Reject { Assert-AgentSignalerHandoffManifest (ParsedManifest) $path $sid $self.SessionId }; $manifest.scripts = $saved
	$memory = [IO.MemoryStream]::new()
	try {
		Write-AgentSignalerPipeFrame $memory ([ordered]@{ schemaVersion = 1; runId = $runId; accepted = $true })
		$memory.Position = 0
		$reply = Read-AgentSignalerPipeFrame $memory
		Assert ($reply.accepted -and $reply.runId -ceq $runId) 'Pipe framing lost the acknowledgement identity.'
	}
	finally { $memory.Dispose() }
	$healthIdentity = [pscustomobject]@{ processId = 999995; creationTimeUtcTicks = $parent.creationTimeUtcTicks;
		userSid = $sid; sessionId = $self.SessionId; executablePath = $parent.executablePath }
	$healthRegistration = [ordered]@{ schemaVersion = 1; process = $healthIdentity; restart = $participant.restart;
		pipeName = 'AgentSignaler.Update.v1.synthetic.health' }
	$healthRegistrationPath = Join-Path $storage ('instance-' + $healthIdentity.processId + '-' + $healthIdentity.creationTimeUtcTicks + '.json')
	$healthRegistrationJson = $healthRegistration | ConvertTo-Json -Depth 8 -Compress
	$healthFile = Open-AgentSignalerProtectedWrite $healthRegistrationPath $sid
	try { $bytes = [Text.Encoding]::UTF8.GetBytes($healthRegistrationJson); $healthFile.Write($bytes, 0, $bytes.Length) }
	finally { $healthFile.Dispose() }
	$healthProbe = {
		param([string] $ProbeMode, [int] $ProbeBudget = 3000)
		$script:healthReplies = 0
		$script:healthStreams = [Collections.Generic.List[IO.MemoryStream]]::new()
		function Open-AgentSignalerAuthenticatedPipe {
			param($Name, $Expected, $BudgetMilliseconds)
			$stream = [IO.MemoryStream]::new(); $script:healthStreams.Add($stream)
			return $stream
		}
		function Read-AgentSignalerPipeFrame {
			param($Pipe, $BudgetMilliseconds)
			$script:healthReplies++
			$reply = [pscustomobject]@{ schemaVersion = 1; runId = ([guid]$runId).ToString();
				accepted = ($ProbeMode -cne 'never-ready' -and $script:healthReplies -ge 3) }
			switch ($ProbeMode) {
				'malformed-bool' { $reply.accepted = 'false' }
				'malformed-schema' { $reply.schemaVersion = '1' }
				'wrong-run' { $reply.runId = [guid]::NewGuid().ToString() }
			}
			return $reply
		}
		Wait-AgentSignalerCapturedParticipantReady ([pscustomobject]@{ HasExited = $false }) $healthIdentity $participant (ParsedManifest) $storage $ProbeBudget
	}
	$healthy = & $healthProbe 'delayed'
	Assert ($healthy.state -ceq 'Ready' -and $healthy.processId -eq $healthIdentity.processId -and $script:healthReplies -eq 3 -and
		@($script:healthStreams | Where-Object CanRead).Count -eq 0) 'Valid not-ready health replies did not retry to readiness or close each connection.'
	Reject { & $healthProbe 'never-ready' 300 }
	Assert ($script:healthReplies -gt 0 -and @($script:healthStreams | Where-Object CanRead).Count -eq 0) 'Unready health did not reach its deadline and close its connections.'
	foreach ($mode in @('malformed-bool', 'malformed-schema', 'wrong-run')) {
		Reject { & $healthProbe $mode }
		Assert ($script:healthReplies -eq 1 -and @($script:healthStreams | Where-Object CanRead).Count -eq 0) 'Invalid health acknowledgement was retried or leaked its connection.'
	}
	foreach ($field in @('processId', 'creationTimeUtcTicks', 'userSid', 'sessionId', 'executablePath')) {
		$changed = ConvertFrom-AgentSignalerStrictJson $healthRegistrationJson
		$changed.process.$field = switch ($field) {
			'processId' { 999994 }
			'creationTimeUtcTicks' { $healthIdentity.creationTimeUtcTicks + 1 }
			'userSid' { 'S-1-5-18' }
			'sessionId' { $healthIdentity.sessionId + 1 }
			'executablePath' { Join-Path $installationDirectory 'AgentSignaler.Configurator.exe' }
		}
		[IO.File]::WriteAllText($healthRegistrationPath, ($changed | ConvertTo-Json -Depth 8 -Compress))
		Reject { & $healthProbe 'delayed' }
		Assert ($script:healthReplies -eq 0 -and $script:healthStreams.Count -eq 0) 'Mismatched registration reached the health connection.'
	}
	Remove-Item -LiteralPath $healthRegistrationPath -Force
	$systemInstallerResolver = ${function:Get-AgentSignalerSystemInstallerPath}
	function Get-AgentSignalerSystemInstallerPath {
		$env:SystemRoot = $untrustedSystemRoot
		try { return & $systemInstallerResolver }
		finally { $env:SystemRoot = $priorSystemRoot }
	}
	function Assert-AgentSignalerProtectedPath { param($Path, $UserSid) }
	function Open-AgentSignalerDirectoryPin { param($Path) return [IO.MemoryStream]::new() }
	function Assert-AgentSignalerPublisher {
		param($Path)
		if ($Path.EndsWith('.msi')) { Reject { [IO.File]::WriteAllText($Path, 'synthetic replacement during trust validation') } }
		if ($script:mode -eq 'signature' -and $Path.EndsWith('.msi')) { throw 'Synthetic untrusted publisher.' }
	}
	function Get-Sha256 { param($Path) return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
	function Get-AgentSignalerGate {
		param($Storage, $Manifest, [switch]$UpdaterOwned)
		return [pscustomobject]@{ schemaVersion = 1; runId = $Manifest.runId; installDirectory = $Manifest.installDirectory;
			owner = $Manifest.parent; controller = $Manifest.parent; phase = 'Servicing'; expiresAt = $Manifest.expiresAt }
	}
	function Get-AgentSignalerInstalledIdentity {
		param($Installer, $Installation)
		$script:inventoryReads++
		if ($script:mode -eq 'inventory-race' -and $script:inventoryReads -gt 1) { throw 'Synthetic concurrent inventory change.' }
		if ($script:mode -eq 'broken' -and $script:installerCalls -gt 0) { throw 'Synthetic damaged prior installation.' }
		$updated = $script:installerCalls -gt 0 -and $script:exitCode -eq 0
		return [pscustomobject]@{ ProductCode = $Installation.productCode; Version = [version]$(if ($updated) { '1.0.15' } else { '1.0.14' }) }
	}
	function Open-AgentSignalerParticipantProcessPin {
		param($Participant)
		$script:processPinsCaptured++
		if ($script:mode -ceq 'capture-failure') { throw 'Synthetic inaccessible captured Client.' }
		return [IO.MemoryStream]::new()
	}
	function Get-AgentSignalerParticipantExitCode {
		param($Handle)
		Assert ($Handle.CanRead -and $script:readinessSignals -eq 1) 'Client process pin was not retained from before readiness through restart.'
		if ($script:mode -ceq 'unknown-intent') { throw 'Synthetic unavailable shutdown intent.' }
		if ($script:mode -in @('ordinary-exit', 'ordinary-exit-recovery')) { return 0 }
		if ($script:mode -ceq 'error-exit') { return 17 }
		return $script:AgentSignalerClientUpdateExitCode
	}
	function Open-AgentSignalerAuthenticatedPipe {
		param($Name, $Expected)
		$expectedPins = if ($script:mode -ceq 'client-stopped') { 0 } else { 1 }
		Assert ($script:processPinsCaptured -eq $expectedPins) 'Readiness preceded capture of the exact Client process.'
		$script:readinessSignals++
		return [IO.MemoryStream]::new()
	}
	function Wait-AgentSignalerQuiescence {
		param($Participants, $Directory, $App, $UserSid, $SessionId, $BudgetMilliseconds)
		if ($script:mode -eq 'quiescence') { throw 'Synthetic participant timeout.' }
	}
	function Get-AgentSignalerExactProcesses {
		param($Directory, $App, $UserSid, $SessionId)
		if ($script:mode -eq 'quiescence') { return [pscustomobject]@{ ProcessId = $PID; CreationTimeUtc = $self.StartTime.ToUniversalTime() } }
		if ($script:mode -eq 'process-race') { return [pscustomobject]@{ ProcessId = 999999; CreationTimeUtc = [DateTime]::UtcNow } }
	}
	function Get-AgentSignalerPinnedDownload {
		param($Manifest)
		if ($script:mode -in @('download', 'ordinary-exit-recovery')) { throw 'Synthetic download failure.' }
		if ($script:mode -eq 'expired') { $Manifest.expiresAt = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o') }
		return [pscustomobject]@{ assets = @($Manifest.assets) }
	}
	function Save-GitHubAsset {
		param($Asset, $Token, $Destination)
		Assert ([string]::IsNullOrEmpty($Token)) 'App handoff consulted credentials.'
		$text = if ($Asset.name -ceq 'SHA256SUMS.txt') { $checksumText } else { $payload }
		if ($script:mode -eq 'hash' -and $Asset.name.EndsWith('.msi')) { $text = 'corrupt' }
		[IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
	}
	function Read-Package {
		param($Installer, $Path, $App)
		$script:packageReads++
		return [pscustomobject]@{ Version = [version]'1.0.15'; ProductCode = '{10000000-0000-0000-0000-000000000002}' }
	}
	function Assert-AgentSignalerInstalledBinaries { param($Manifest, $Version) $script:installedBinaryChecks++ }
	function Start-Process {
		param($FilePath, $ArgumentList, [switch]$PassThru)
		Assert ($FilePath -ceq $systemInstaller -and -not $FilePath.StartsWith($untrustedSystemRoot, [StringComparison]::OrdinalIgnoreCase)) 'Fake installer escaped trusted system executable allowlist.'
		Assert ($ArgumentList -ceq (Get-AgentSignalerMsiArguments (Join-Path $run 'AgentSignaler.Remote.msi') (Join-Path $run 'install.log'))) 'Installer passive/reboot/shutdown flags changed.'
		Assert ($script:installedBinaryChecks -eq 0) 'Installed executables were pinned before MSI replacement.'
		$script:activePackagePath = Join-Path $run 'AgentSignaler.Remote.msi'
		Reject { [IO.File]::WriteAllText($script:activePackagePath, 'synthetic replacement at installer launch') }
		$script:installerCalls++
		$process = [pscustomobject]@{ ExitCode = $script:exitCode; Id = 999998; StartTime = [DateTime]::Now }
		$process | Add-Member ScriptMethod WaitForExit {
			Reject { [IO.File]::WriteAllText($script:activePackagePath, 'synthetic replacement while installer runs') }
			if ($script:mode -eq 'interrupted') { throw 'Synthetic interruption while MSI is running.' }
		}
		$process | Add-Member ScriptMethod Dispose { }
		return $process
	}
	function Start-AgentSignalerCapturedParticipant {
		param($Participant, $Manifest, $Storage)
		Assert (@($Manifest.participants | Where-Object { $_.process.processId -eq $Participant.process.processId -and
			$_.process.creationTimeUtcTicks -eq $Participant.process.creationTimeUtcTicks -and
			$_.restart.application -ceq $Participant.restart.application }).Count -eq 1) 'Restart escaped the captured participant manifest.'
		if ($script:mode -ceq 'client-stopped') { Assert ($Participant.restart.application -ceq 'Configurator') 'A stopped Client absent from the manifest was restarted.' }
		$operationGate = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $Storage 'gate.json')))
		$marker = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $Storage 'restart.json')))
		$health = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $Storage ('restart-' + $Manifest.runId + '.json'))))
		Assert ($operationGate.phase -ceq 'Restarting' -and $marker.phase -ceq 'Restarting' -and $health.phase -ceq 'Servicing' -and
			[guid]$marker.runId -eq [guid]$Manifest.runId -and $marker.owner.processId -eq $PID) 'Restart/health authorization gate contract changed.'
		$script:restartCalls++
		if ($script:mode -eq 'health') { throw 'Synthetic local health timeout.' }
		return [pscustomobject]@{ application = $Participant.restart.application; processId = 999997; state = 'Ready' }
	}
	function Show-AgentSignalerUpdateResult {
		param($Result, $ResultPath)
		$script:resultShows++
		Assert ($ResultPath -ceq (Join-Path $run 'result.json')) 'Result dialog escaped its own recovery record.'
	}
	$clientExecutablePath = $parent.executablePath
	foreach ($case in @(
			@('success', 0, 1, 1, 'Completed', 'Completed'),
			@('client-stopped', 0, 1, 1, 'Completed', 'Completed'),
			@('ordinary-exit', 0, 1, 0, 'Completed', 'Completed'),
			@('ordinary-exit-recovery', 0, 0, 0, 'Failed', 'Failed'),
			@('unknown-intent', 0, 1, 0, 'Failed', 'Completed'),
			@('error-exit', 0, 1, 0, 'Failed', 'Completed'),
			@('capture-failure', 0, 0, 0, 'Failed', 'Failed'),
			@('reboot', 3010, 1, 0, 'RebootRequired', 'RebootRequired'),
			@('busy', 1618, 1, 0, 'Failed', 'InstallerBusy'),
			@('cancel', 1602, 1, 0, 'Failed', 'Cancelled'),
			@('broken', 1603, 1, 0, 'Failed', 'InstallerFailed'),
			@('download', 0, 0, 1, 'Failed', 'Failed'),
			@('signature', 0, 0, 1, 'Failed', 'Failed'),
			@('hash', 0, 0, 1, 'Failed', 'Failed'),
			@('expired', 0, 0, 1, 'Failed', 'Failed'),
			@('quiescence', 0, 0, 0, 'Failed', 'Failed'),
			@('inventory-race', 0, 0, 0, 'Failed', 'Failed'),
			@('process-race', 0, 0, 1, 'Failed', 'Failed'),
			@('health', 0, 1, 1, 'Failed', 'Completed'),
			@('interrupted', 0, 1, 0, 'Failed', 'Failed'))) {
		$script:mode = $case[0]; $script:exitCode = $case[1]
		$manifest.expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(20).ToString('o')
		$parent.executablePath = $clientExecutablePath
		$participant.restart.application = 'Client'; $participant.restart.background = $true
		if ($script:mode -ceq 'client-stopped') {
			$parent.executablePath = Join-Path $installationDirectory 'AgentSignaler.Configurator.exe'
			$participant.restart.application = 'Configurator'; $participant.restart.background = $false
		}
		$script:installerCalls = 0; $script:restartCalls = 0; $script:inventoryReads = 0; $script:packageReads = 0; $script:resultShows = 0; $script:installedBinaryChecks = 0
		$script:processPinsCaptured = 0; $script:readinessSignals = 0
		foreach ($name in @('SHA256SUMS.txt', 'AgentSignaler.Remote.msi', 'result.json')) {
			$file = Join-Path $run $name
			if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
		}
		$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
		[IO.File]::WriteAllText((Join-Path $storage 'maintenance.lock'), 'fixture')
		Get-AgentSignalerGate $storage (ParsedManifest) -UpdaterOwned | ConvertTo-Json -Depth 8 |
			Set-Content -LiteralPath (Join-Path $storage 'gate.json') -Encoding UTF8
		Get-AgentSignalerGate $storage (ParsedManifest) -UpdaterOwned | ConvertTo-Json -Depth 8 |
			Set-Content -LiteralPath (Join-Path $storage ('restart-' + $runId + '.json')) -Encoding UTF8
		Invoke-AgentSignalerAppHandoff $path
		$result = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText((Join-Path $run 'result.json'))) 64KB
		Assert ($script:installerCalls -eq $case[2] -and $script:restartCalls -eq $case[3]) "Wrong installer/restart counts for $($case[0])."
		Assert ($result.stage -ceq $case[4] -and $result.outcome -ceq $case[5]) "Wrong durable result for $($case[0])."
		if ($script:mode -ceq 'client-stopped') {
			Assert (@($result.restarts).Count -eq 1 -and $result.restarts[0].application -ceq 'Configurator' -and
				$result.restarts[0].state -ceq 'Ready') 'Update did not preserve the stopped Client restart intent.'
		}
		Assert ($script:resultShows -eq 1) 'Explicit update did not surface its result.'
		Assert ((Test-Path -LiteralPath (Join-Path $storage 'gate.json')) -eq ($case[0] -in @('interrupted', 'capture-failure'))) "Untransferred or unresolved servicing ownership must retain its maintenance gate: $($case[0])."
		if ($script:installerCalls -gt 0) { Assert ($result.installerProcessId -eq 999998 -and $result.installerCreationTimeUtcTicks -gt 0) 'MSI interruption recovery identity was not persisted.' }
	}
	Write-Host 'Updater handoff tests passed: strict manifest, frames, passive flags, pins/trust, inventory/startup races, 0/3010/1618/1602/failure, recovery, health and gate retention. Exact native handles distinguish ordinary Client Exit from update shutdown; absent Client never starts. No network, installer or application launch; controlled PowerShell children only.'
}
finally {
	$env:LOCALAPPDATA = $priorLocalAppData
	$env:SystemRoot = $priorSystemRoot
	Remove-Item -LiteralPath $fixture -Recurse -Force
}
