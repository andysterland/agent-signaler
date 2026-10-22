Set-StrictMode -Version Latest

function Initialize-AgentSignalerUpdateNative {
	if ('AgentSignalerUpdateNative' -as [type]) { return }
	Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AgentSignalerUpdateNative {
	[DllImport("kernel32.dll", SetLastError=true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
	[DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
	public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
		uint disposition, uint flags, IntPtr template);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
	[DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
	public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool ProcessIdToSessionId(uint pid, out uint session);
	[DllImport("advapi32.dll", SetLastError=true)]
	public static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
	[DllImport("msi.dll", CharSet=CharSet.Unicode, EntryPoint="MsiGetProductInfoExW")]
	public static extern uint MsiGetProductInfoEx(string product, string sid, uint context,
		string property, StringBuilder value, ref uint count);
}
'@
}

function Show-AgentSignalerUpdateResult($Result, [string] $ResultPath) {
	if ($Result.stage -notin @('Failed', 'RebootRequired')) { return }
	$message = if ($Result.stage -ceq 'RebootRequired') {
		'Agent Signaler was updated and Windows Installer requires a Windows restart. Applications were not restarted. Restart Windows yourself when ready.'
	}
	elseif ($Result.msiState -in @('Starting', 'Running')) {
		'Agent Signaler update has an unresolved Windows Installer outcome. Applications remain stopped. Do not remove the maintenance gate or restart applications until Windows Installer is proven finished.'
	}
	else { 'Agent Signaler update did not complete or an application did not become ready. Review the recovery record before retrying or repairing as the original installing user.' }
	try {
		Add-Type -AssemblyName System.Windows.Forms
		[void][Windows.Forms.MessageBox]::Show($message + "`r`n`r`nRecovery record: " + $ResultPath,
			'Agent Signaler update result', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Information)
	}
	catch { Write-Warning 'The update result dialog could not be displayed. Retain this run''s result.json for recovery.' }
}

function Open-AgentSignalerDirectoryPin([string] $Path) {
	Initialize-AgentSignalerUpdateNative
	$handle = [AgentSignalerUpdateNative]::CreateFile($Path, [uint32]2147483648, 3, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
	if ($handle.IsInvalid) { $handle.Dispose(); throw 'Unable to pin update directory.' }
	return $handle
}

function Open-AgentSignalerParticipantProcessPin($Participant) {
	Initialize-AgentSignalerUpdateNative
	$expected = $Participant.process
	$handle = [AgentSignalerUpdateNative]::OpenProcess(0x101000, $false, $expected.processId)
	if ($handle.IsInvalid) { $handle.Dispose(); throw 'Captured process could not be pinned before readiness.' }
	try {
		[long]$creation = 0; [long]$exit = 0; [long]$kernel = 0; [long]$user = 0
		$path = [Text.StringBuilder]::new(1025)
		[uint32]$length = 1025; [uint32]$session = 0
		if (-not [AgentSignalerUpdateNative]::GetProcessTimes($handle, [ref]$creation, [ref]$exit, [ref]$kernel, [ref]$user) -or
			[DateTime]::FromFileTimeUtc($creation).Ticks -ne $expected.creationTimeUtcTicks -or
			-not [AgentSignalerUpdateNative]::QueryFullProcessImageName($handle, 0, $path, [ref]$length) -or
			-not $path.ToString().Equals($expected.executablePath, [StringComparison]::OrdinalIgnoreCase) -or
			-not [AgentSignalerUpdateNative]::ProcessIdToSessionId($expected.processId, [ref]$session) -or
			$session -ne $expected.sessionId) { throw 'Captured process identity changed before readiness.' }
		$token = $null
		if (-not [AgentSignalerUpdateNative]::OpenProcessToken($handle, 8, [ref]$token)) { throw 'Captured process user could not be verified.' }
		try {
			$identity = [Security.Principal.WindowsIdentity]::new($token.DangerousGetHandle())
			try { if ($identity.User.Value -cne $expected.userSid) { throw 'Captured process user changed before readiness.' } }
			finally { $identity.Dispose() }
		}
		finally { if ($null -ne $token) { $token.Dispose() } }
		return $handle
	}
	catch { $handle.Dispose(); throw }
}

function Get-AgentSignalerParticipantExitCode($Handle) {
	if ([AgentSignalerUpdateNative]::WaitForSingleObject($Handle, 0) -ne 0) { throw 'Captured process shutdown is not proven.' }
	[uint32]$exitCode = 0
	if (-not [AgentSignalerUpdateNative]::GetExitCodeProcess($Handle, [ref]$exitCode)) { throw 'Captured process shutdown intent is unavailable.' }
	return $exitCode
}

function Get-AgentSignalerInstalledProperty([string] $ProductCode, [string] $UserSid, [string] $Name) {
	Initialize-AgentSignalerUpdateNative
	$text = [Text.StringBuilder]::new(4097)
	[uint32]$length = 4097
	if ([AgentSignalerUpdateNative]::MsiGetProductInfoEx($ProductCode, $UserSid, 2, $Name, $text, [ref]$length) -ne 0 -or $length -gt 4096) {
		throw 'Current-user installed product identity could not be verified.'
	}
	return $text.ToString()
}

function Get-AgentSignalerInstalledIdentity($Installer, $Installation) {
	$code = ([guid]$Installation.productCode).ToString('B').ToUpperInvariant()
	$sid = [string]$Installation.userSid
	if ((Get-AgentSignalerInstalledProperty $code $sid 'State') -cne '5') { throw 'Product is not installed for the current user.' }
	$directory = (Get-AgentSignalerInstalledProperty $code $sid 'InstallLocation').TrimEnd('\')
	if (-not $directory.Equals($Installation.installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
		throw 'Registered installation directory changed.'
	}
	$directoryPins = [Collections.Generic.List[IDisposable]]::new()
	try {
		$current = Assert-AgentSignalerLocalPath $directory
		do {
			$directoryPins.Add((Open-AgentSignalerDirectoryPin $current))
			$current = [IO.Path]::GetDirectoryName($current)
		} while ($current)
		Assert-AgentSignalerUpdatePath $directory
		$cached = Get-AgentSignalerInstalledProperty $code $sid 'LocalPackage'
		$package = Read-Package $Installer $cached $Installation.package
		$version = ConvertTo-AgentSignalerVersion (Get-AgentSignalerInstalledProperty $code $sid 'VersionString')
		if ($package.Version -ne $version -or ([guid]$package.ProductCode) -ne [guid]$code -or
			(Get-InstalledVersion $Installer $script:AgentSignalerUpgradeCodes[$Installation.package]) -ne $version) {
			throw 'Installed package inventory is ambiguous or inconsistent.'
		}
		return [pscustomobject]@{ ProductCode = $code; Version = $version; Directory = $directory }
	}
	finally { foreach ($pin in $directoryPins) { $pin.Dispose() } }
}

function Assert-AgentSignalerLocalPath([string] $Path, [string] $Boundary) {
	if ($Path.Length -gt 1024 -or $Path -notmatch '\A[A-Za-z]:\\' -or $Path -match '[/"\x00-\x1f]' -or
		$Path.Substring(2).Contains(':')) { throw 'Unsafe update path.' }
	$full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
	if ($full -cne $Path.TrimEnd('\')) { throw 'Update path must be canonical.' }
	foreach ($part in $full.Substring(3).Split('\')) {
		if ([string]::IsNullOrEmpty($part) -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
			$part -match '\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)') { throw 'Ambiguous update path.' }
	}
	if ($Boundary) {
		$root = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
		if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and
			-not $full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its boundary.' }
	}
	$current = $full
	while ($current) {
		if (Test-Path -LiteralPath $current) {
			if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
				throw 'Update paths must not traverse reparse points.'
			}
		}
		$current = [IO.Path]::GetDirectoryName($current)
	}
	return $full
}

function Get-AgentSignalerSystemInstallerPath {
	if (-not [Environment]::Is64BitProcess -or -not [Environment]::Is64BitOperatingSystem) {
		throw 'App updates require the native x64 Windows Installer.'
	}
	$windows = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
	$windows = Assert-AgentSignalerLocalPath $windows
	$path = Assert-AgentSignalerLocalPath (Join-Path $windows 'System32\msiexec.exe') $windows
	if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The system Windows Installer is unavailable.' }
	return $path
}

function Assert-AgentSignalerUpdatePath([string] $Path) {
	[void](Assert-AgentSignalerLocalPath $Path)
	[void](Get-Item -LiteralPath $Path -Force -ErrorAction Stop)
}

function Open-AgentSignalerReadPin([string] $Path, [long] $Maximum) {
	$stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
	if ($stream.Length -le 0 -or $stream.Length -gt $Maximum) {
		$stream.Dispose()
		throw 'Update file exceeds its bound.'
	}
	return $stream
}

function Read-AgentSignalerPipeBytes([IO.Stream] $Pipe, [int] $Count, [Diagnostics.Stopwatch] $Clock, [int] $BudgetMilliseconds) {
	$buffer = New-Object byte[] $Count
	$offset = 0
	while ($offset -lt $Count) {
		$pending = $Pipe.BeginRead($buffer, $offset, $Count - $offset, $null, $null)
		try {
			$remaining = [Math]::Max(0, $BudgetMilliseconds - [int]$Clock.ElapsedMilliseconds)
			if (-not $pending.AsyncWaitHandle.WaitOne($remaining)) { $Pipe.Dispose(); throw 'Local update acknowledgement timed out.' }
			$read = $Pipe.EndRead($pending)
			if ($read -le 0) { throw 'Local update pipe closed before acknowledgement.' }
			$offset += $read
		}
		finally { $pending.AsyncWaitHandle.Dispose() }
	}
	return ,$buffer
}

function Read-AgentSignalerPipeFrame([IO.Stream] $Pipe, [int] $BudgetMilliseconds = 15000) {
	$clock = [Diagnostics.Stopwatch]::StartNew()
	$header = Read-AgentSignalerPipeBytes $Pipe 4 $clock $BudgetMilliseconds
	$length = [BitConverter]::ToInt32($header, 0)
	if ($length -le 0 -or $length -gt 4096) { throw 'Local update message exceeds its bound.' }
	$bytes = Read-AgentSignalerPipeBytes $Pipe $length $clock $BudgetMilliseconds
	return ConvertFrom-AgentSignalerStrictJson ([Text.UTF8Encoding]::new($false, $true).GetString($bytes)) 4096
}

function Write-AgentSignalerPipeFrame([IO.Stream] $Pipe, $Value) {
	$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 8 -Compress))
	if ($bytes.Length -le 0 -or $bytes.Length -gt 4096) { throw 'Local update message exceeds its bound.' }
	$header = [BitConverter]::GetBytes([int]$bytes.Length)
	$Pipe.Write($header, 0, 4)
	$Pipe.Write($bytes, 0, $bytes.Length)
	$Pipe.Flush()
}

function Open-AgentSignalerAuthenticatedPipe([string] $Name, $Expected, [int] $BudgetMilliseconds = 10000) {
	Initialize-AgentSignalerUpdateNative
	if ($Name -notmatch '\AAgentSignaler\.Update\.[a-zA-Z0-9.\-]{1,180}\z') { throw 'Unsupported local update pipe.' }
	$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $Name, [IO.Pipes.PipeDirection]::InOut,
		[IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
	try {
		$pipe.Connect([Math]::Max(1, $BudgetMilliseconds))
		[uint32]$server = 0
		if (-not [AgentSignalerUpdateNative]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref]$server) -or
			$server -ne $Expected.processId) { throw 'Local update pipe server identity changed.' }
		$process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$server"
		$owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
		if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne $Expected.userSid -or
			$process.SessionId -ne $Expected.sessionId -or
			(Get-Process -Id $server).StartTime.ToUniversalTime().Ticks -ne [long]$Expected.creationTimeUtcTicks -or
			-not $process.ExecutablePath.Equals($Expected.executablePath, [StringComparison]::OrdinalIgnoreCase)) {
			throw 'Local update pipe server ownership changed.'
		}
		return $pipe
	}
	catch { $pipe.Dispose(); throw }
}

function Get-AgentSignalerExactProcesses([string] $InstallationDirectory, [string] $App, [string] $UserSid, [int] $SessionId) {
	$names = if ($App -ceq 'Dashboard') { @('AgentSignaler.Dashboard.exe') }
		else { @('AgentSignaler.Client.exe', 'AgentSignaler.Configurator.exe', 'AgentSignaler.Relay.exe') }
	$filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
	foreach ($process in @(Get-CimInstance -ClassName Win32_Process -Filter $filter)) {
		if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) { throw 'Affected process identity is inaccessible.' }
		$expected = Join-Path $InstallationDirectory $process.Name
		if (-not $process.ExecutablePath.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { continue }
		$owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
		if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne $UserSid -or $process.SessionId -ne $SessionId) {
			throw 'Affected process belongs to an unsupported user or Windows session.'
		}
		[pscustomobject]@{ ProcessId = [int]$process.ProcessId; CreationTimeUtc = (Get-Process -Id $process.ProcessId).StartTime.ToUniversalTime(); ExecutablePath = $expected }
	}
}

function Wait-AgentSignalerQuiescence($Participants, [string] $Directory, [string] $App, [string] $UserSid, [int] $SessionId,
		[int] $BudgetMilliseconds = 60000) {
	$deadline = [Diagnostics.Stopwatch]::StartNew()
	do {
		$running = @(Get-AgentSignalerExactProcesses $Directory $App $UserSid $SessionId)
		foreach ($process in $running) {
			$match = @($Participants | Where-Object { $_.process.processId -eq $process.ProcessId })
			if ($match.Count -ne 1) { throw 'An unapproved participant appeared during servicing.' }
			if ([long]$match[0].process.creationTimeUtcTicks -ne $process.CreationTimeUtc.Ticks) {
				throw 'Participant process identity changed.'
			}
		}
		if ($running.Count -eq 0) { return }
		if ($deadline.ElapsedMilliseconds -ge $BudgetMilliseconds) { throw 'Participants did not quiesce within 60 seconds; no installer ran.' }
		Start-Sleep -Milliseconds 100
	} while ($true)
}

function Assert-AgentSignalerHandoffManifest($Manifest, [string] $ManifestPath, [string] $UserSid, [int] $SessionId) {
	Assert-AgentSignalerFields $Manifest @('schemaVersion', 'runId', 'nonce', 'package', 'installDirectory',
		'productCode', 'installedVersion', 'releaseId', 'tag', 'assets', 'checksumSha256', 'packageSha256',
		'parent', 'participants', 'scripts', 'readinessPipe', 'expiresAt')
	Assert-AgentSignalerInteger $Manifest.schemaVersion 1 1
	Assert-AgentSignalerInteger $Manifest.releaseId
	if ($Manifest.schemaVersion -ne 1 -or $Manifest.runId -cnotmatch '\A[a-f0-9]{32}\z' -or
		$Manifest.nonce -cnotmatch '\A[a-f0-9]{64}\z' -or @('Dashboard', 'Remote') -cnotcontains $Manifest.package -or
		[long]$Manifest.releaseId -le 0 -or $Manifest.tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z' -or
		$Manifest.checksumSha256 -notmatch '\A[a-fA-F0-9]{64}\z' -or $Manifest.packageSha256 -notmatch '\A[a-fA-F0-9]{64}\z' -or
		$Manifest.readinessPipe -cne ('AgentSignaler.Update.Ready.v1.' + $Manifest.nonce)) { throw 'Invalid app handoff identity.' }
	$expires = [DateTimeOffset]::Parse($Manifest.expiresAt)
	if ($expires.Offset -ne [TimeSpan]::Zero -or $expires -le [DateTimeOffset]::UtcNow -or
		$expires -gt [DateTimeOffset]::UtcNow.AddMinutes(30)) { throw 'App handoff expired.' }
	$expected = Assert-AgentSignalerLocalPath $Manifest.installDirectory
	$hash = [Security.Cryptography.SHA256]::Create()
	try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($expected.ToUpperInvariant()))).Replace('-', '').ToLowerInvariant() }
	finally { $hash.Dispose() }
	$storage = Join-Path $env:LOCALAPPDATA ('AgentSignaler\Updates\' + $key)
	$run = Join-Path $storage $Manifest.runId
	if (-not $ManifestPath.Equals((Join-Path $run 'handoff.json'), [StringComparison]::OrdinalIgnoreCase) -or
		-not $PSScriptRoot.Equals($run, [StringComparison]::OrdinalIgnoreCase)) { throw 'Handoff is not the staged dependency set.' }
	Assert-AgentSignalerReleaseAssets @($Manifest.assets)
	foreach ($asset in $Manifest.assets) {
		Assert-AgentSignalerFields $asset @('id', 'name', 'size')
		Assert-AgentSignalerInteger $asset.id
		Assert-AgentSignalerInteger $asset.size
		if ([long]$asset.id -le 0) { throw 'Invalid pinned asset ID.' }
	}
	if (@($Manifest.assets.id | Select-Object -Unique).Count -ne 3) { throw 'Duplicate pinned asset ID.' }
	[void](ConvertTo-AgentSignalerVersion $Manifest.installedVersion)
	if ((ConvertTo-AgentSignalerVersion $Manifest.tag.Substring(1)) -le (ConvertTo-AgentSignalerVersion $Manifest.installedVersion)) {
		throw 'App handoff does not authorize an upgrade.'
	}
	$code = [guid]::Empty
	if (-not [guid]::TryParse($Manifest.productCode, [ref]$code) -or $code -eq [guid]::Empty) { throw 'Invalid installed ProductCode.' }
	if (@($Manifest.participants).Count -lt 1 -or @($Manifest.participants).Count -gt 32 -or
		@($Manifest.participants.process.processId | Select-Object -Unique).Count -ne @($Manifest.participants).Count) {
		throw 'Invalid approved participant set.'
	}
	$all = @($Manifest.parent) + @($Manifest.participants | ForEach-Object { $_.process })
	foreach ($process in $all) {
		Assert-AgentSignalerFields $process @('processId', 'creationTimeUtcTicks', 'userSid', 'sessionId', 'executablePath')
		Assert-AgentSignalerInteger $process.processId 1 ([int]::MaxValue)
		Assert-AgentSignalerInteger $process.creationTimeUtcTicks
		Assert-AgentSignalerInteger $process.sessionId 0 ([int]::MaxValue)
		if ([int]$process.processId -le 0 -or [long]$process.creationTimeUtcTicks -le 0 -or $process.userSid -cne $UserSid -or
			$process.sessionId -ne $SessionId) { throw 'Unsupported participant ownership or Windows session.' }
		[void](Assert-AgentSignalerLocalPath $process.executablePath $expected)
	}
	foreach ($participant in $Manifest.participants) {
		Assert-AgentSignalerFields $participant @('schemaVersion', 'process', 'restart', 'pipeName')
		Assert-AgentSignalerFields $participant.restart @('application', 'contextPath', 'background')
		Assert-AgentSignalerInteger $participant.schemaVersion 1 1
		if ($participant.restart.background -isnot [bool]) { throw 'Restart visibility must be a JSON Boolean.' }
		$application = [string]$participant.restart.application
		$allowed = if ($Manifest.package -ceq 'Dashboard') { @('Dashboard') } else { @('Client', 'Configurator') }
		if ($participant.schemaVersion -ne 1 -or $allowed -cnotcontains $application -or
			-not $participant.process.executablePath.Equals((Join-Path $expected "AgentSignaler.$application.exe"), [StringComparison]::OrdinalIgnoreCase) -or
			($application -ceq 'Configurator' -and $participant.restart.background)) { throw 'Unsafe captured restart intent.' }
		$hash = [Security.Cryptography.SHA256]::Create()
		try {
			$pipeKey = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(
				$expected.ToUpperInvariant() + '|' + $UserSid))).Replace('-', '').Substring(0, 24)
		}
		finally { $hash.Dispose() }
		$expectedPipe = 'AgentSignaler.Update.v1.' + $pipeKey + '.' + $participant.process.processId + '.' + $participant.process.creationTimeUtcTicks
		if ($participant.pipeName -cne $expectedPipe) { throw 'Participant maintenance endpoint identity changed.' }
		[void](Assert-AgentSignalerLocalPath $participant.restart.contextPath)
		if ($application -cne 'Dashboard' -and $participant.restart.contextPath.EndsWith('\')) {
			throw 'Configuration launch paths must identify a file.'
		}
	}
	if (@($Manifest.participants | Where-Object { $_.process.processId -eq $Manifest.parent.processId -and
		$_.process.creationTimeUtcTicks -eq $Manifest.parent.creationTimeUtcTicks }).Count -ne 1) { throw 'Initiator is not an approved participant.' }
	if (@($Manifest.scripts).Count -ne $script:AgentSignalerUpdaterFiles.Count) { throw 'Incomplete staged dependency set.' }
	foreach ($name in $script:AgentSignalerUpdaterFiles) {
		$pin = @($Manifest.scripts | Where-Object { $_.name -ceq $name })
		if ($pin.Count -ne 1) { throw 'Unexpected staged dependency set.' }
		Assert-AgentSignalerFields $pin[0] @('name', 'size', 'sha256')
		Assert-AgentSignalerInteger $pin[0].size 1 1MB
		if ($pin[0].size -le 0 -or $pin[0].size -gt 1MB -or $pin[0].sha256 -notmatch '\A[a-fA-F0-9]{64}\z') { throw 'Invalid staged dependency pin.' }
	}
	return [pscustomobject]@{ RunDirectory = $run; StorageDirectory = $storage }
}

function Write-AgentSignalerRunResult([string] $Directory, $Result) {
	$path = Join-Path $Directory 'result.json'
	$pending = Join-Path $Directory ('result-' + [guid]::NewGuid().ToString('N') + '.new')
	$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Result | ConvertTo-Json -Depth 8 -Compress))
	if ($bytes.Length -gt 64KB) { throw 'Update result exceeds its bound.' }
	$file = [IO.File]::Open($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
	try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
	if (Test-Path -LiteralPath $path) { [IO.File]::Replace($pending, $path, [NullString]::Value) }
	else { [IO.File]::Move($pending, $path) }
}

function Open-AgentSignalerUpdateWrite([string] $Path) {
	[void](Assert-AgentSignalerLocalPath $Path)
	return [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
		[IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
}

function Write-AgentSignalerRestartGate([string] $Storage, $Manifest, $Gate) {
	$Gate.expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o')
	foreach ($name in @('gate.json', 'restart.json', ('restart-' + $Manifest.runId + '.json'))) {
		$Gate.phase = if ($name -ceq ('restart-' + $Manifest.runId + '.json')) { 'Servicing' } else { 'Restarting' }
		$path = Join-Path $Storage $name
		$existing = Test-Path -LiteralPath $path
		if ($existing) {
			Assert-AgentSignalerUpdatePath $path
			$prior = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))) 64KB
			Assert-AgentSignalerFields $prior @('schemaVersion', 'runId', 'installDirectory', 'owner', 'expiresAt', 'phase', 'controller')
			if ([guid]$prior.runId -ne [guid]$Manifest.runId -or $prior.owner.processId -ne $PID -or
				$prior.owner.creationTimeUtcTicks -ne $Gate.owner.creationTimeUtcTicks -or
				-not $prior.installDirectory.Equals($Manifest.installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
				throw 'Restart authorization belongs to another operation.'
			}
		}
		$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Gate | ConvertTo-Json -Depth 8 -Compress))
		$pending = Join-Path $Storage ('restart-' + [guid]::NewGuid().ToString('N') + '.new')
		$file = Open-AgentSignalerUpdateWrite $pending
		try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
		if ($existing) { [IO.File]::Replace($pending, $path, [NullString]::Value) }
		else { [IO.File]::Move($pending, $path) }
	}
}

function Get-AgentSignalerPinnedDownload($Manifest) {
	$clock = [Diagnostics.Stopwatch]::StartNew()
	$response = Open-GitHubResponse "$apiRoot/releases/$($Manifest.releaseId)" $null 'application/vnd.github+json'
	$release = Read-GitHubJsonResponse $response ([Math]::Max(1, 15000 - [int]$clock.ElapsedMilliseconds))
	$pin = [pscustomobject]@{ id = $Manifest.releaseId; tag_name = $Manifest.tag; prerelease = $release.prerelease; assets = $Manifest.assets }
	Assert-AgentSignalerPinnedRelease $release $pin
	foreach ($asset in $release.assets) {
		$asset.url = "$apiRoot/releases/assets/$($asset.id)"
	}
	return $release
}

function Get-AgentSignalerGate([string] $Storage, $Manifest, [switch] $UpdaterOwned) {
	$path = Join-Path $Storage 'gate.json'
	Assert-AgentSignalerUpdatePath $path
	$gate = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))) 64KB
	Assert-AgentSignalerFields $gate @('schemaVersion', 'runId', 'installDirectory', 'owner', 'expiresAt', 'phase', 'controller')
	foreach ($process in @($gate.owner, $gate.controller)) {
		Assert-AgentSignalerFields $process @('processId', 'creationTimeUtcTicks', 'userSid', 'sessionId', 'executablePath')
		[void](Assert-AgentSignalerLocalPath $process.executablePath)
	}
	if ($gate.schemaVersion -ne 1 -or [guid]$gate.runId -ne [guid]$Manifest.runId -or
		-not $gate.installDirectory.Equals($Manifest.installDirectory, [StringComparison]::OrdinalIgnoreCase) -or
		$gate.controller.processId -ne $Manifest.parent.processId -or
		$gate.controller.creationTimeUtcTicks -ne $Manifest.parent.creationTimeUtcTicks -or
		$gate.controller.userSid -cne $Manifest.parent.userSid -or $gate.controller.sessionId -ne $Manifest.parent.sessionId -or
		-not $gate.controller.executablePath.Equals($Manifest.parent.executablePath, [StringComparison]::OrdinalIgnoreCase) -or
		[DateTimeOffset]::Parse($gate.expiresAt).Offset -ne [TimeSpan]::Zero -or
		[DateTimeOffset]::Parse($gate.expiresAt) -gt [DateTimeOffset]::Parse($Manifest.expiresAt) -or
		$gate.owner.userSid -cne $Manifest.parent.userSid -or $gate.owner.sessionId -ne $Manifest.parent.sessionId) {
		throw 'Maintenance gate ownership changed.'
	}
	if ($UpdaterOwned) {
		$self = Get-Process -Id $PID
		if ($gate.owner.processId -ne $PID -or $gate.owner.creationTimeUtcTicks -ne $self.StartTime.ToUniversalTime().Ticks -or
			-not $gate.owner.executablePath.Equals($self.Path, [StringComparison]::OrdinalIgnoreCase) -or
			$gate.phase -cne 'Servicing') { throw 'Updater has not acquired the maintenance gate.' }
	}
	elseif ($gate.owner.processId -ne $Manifest.parent.processId -or
		$gate.owner.creationTimeUtcTicks -ne $Manifest.parent.creationTimeUtcTicks -or $gate.phase -cne 'Preparing' -or
		[DateTimeOffset]::Parse($gate.expiresAt) -le [DateTimeOffset]::UtcNow) {
		throw 'Parent does not own the preparing maintenance gate.'
	}
	return $gate
}

function Assert-AgentSignalerInstalledBinaries($Manifest, [version] $Version, $Pins) {
	$names = if ($Manifest.package -ceq 'Dashboard') { @('Dashboard') } else { @('Client', 'Configurator', 'Relay') }
	foreach ($name in $names) {
		$path = Join-Path $Manifest.installDirectory "AgentSignaler.$name.exe"
		[void](Assert-AgentSignalerLocalPath $path $Manifest.installDirectory)
		if ($null -ne $Pins) { $Pins.Add((Open-AgentSignalerReadPin $path 1GB)) }
		Assert-AgentSignalerPublisher $path
		$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion.Split('+')[0]
		if ($productVersion -cne $Version.ToString(3)) { throw 'Installed executable version is not the verified product version.' }
	}
}

function New-AgentSignalerCapturedStartInfo($Participant, $Manifest) {
	$restart = $Participant.restart
	$path = Join-Path $Manifest.installDirectory "AgentSignaler.$($restart.application).exe"
	$start = [Diagnostics.ProcessStartInfo]::new()
	$start.FileName = $path
	$start.UseShellExecute = $false
	$start.WorkingDirectory = $Manifest.installDirectory
	foreach ($name in @('AGENT_SIGNALER_GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_TOKEN')) { $start.EnvironmentVariables.Remove($name) }
	$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_RUN'] = $Manifest.runId
	$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_NONCE'] = $Manifest.nonce
	$start.EnvironmentVariables['AGENT_SIGNALER_UPDATE_PARTICIPANT'] = [string]$Participant.process.processId
	if ($restart.application -ceq 'Dashboard') {
		$start.EnvironmentVariables['AGENT_SIGNALER_DATA_DIR'] = $restart.contextPath
		$start.Arguments = if ($restart.background) { '--background' } else { '' }
	}
	else {
		$start.Arguments = '--config "' + $restart.contextPath + '"'
		if ($restart.application -ceq 'Client') { $start.Arguments = '--background ' + $start.Arguments }
	}
	return $start
}

function Write-AgentSignalerRestartGrant($Participant, $Manifest, [string] $Storage, $ProcessIdentity) {
	Assert-AgentSignalerFields $ProcessIdentity @('processId', 'creationTimeUtcTicks', 'userSid', 'sessionId', 'executablePath')
	Assert-AgentSignalerInteger $ProcessIdentity.processId 1 ([int]::MaxValue)
	Assert-AgentSignalerInteger $ProcessIdentity.creationTimeUtcTicks
	$expectedPath = Join-Path $Manifest.installDirectory ('AgentSignaler.' + $Participant.restart.application + '.exe')
	if ($ProcessIdentity.userSid -cne $Manifest.parent.userSid -or $ProcessIdentity.sessionId -ne $Manifest.parent.sessionId -or
		-not $ProcessIdentity.executablePath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
		throw 'Restart grant does not identify the exact captured application.'
	}
	$path = Join-Path $Storage ('restart-grant-' + $ProcessIdentity.processId + '-' + $ProcessIdentity.creationTimeUtcTicks + '.json')
	$pending = Join-Path $Storage ('restart-grant-' + [guid]::NewGuid().ToString('N') + '.new')
	$grant = [ordered]@{ schemaVersion = 1; runId = $Manifest.runId; originalProcessId = $Participant.process.processId;
		process = $ProcessIdentity; restart = $Participant.restart }
	$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($grant | ConvertTo-Json -Depth 8 -Compress))
	if ($bytes.Length -gt 64KB) { throw 'Restart grant exceeds its bound.' }
	$file = Open-AgentSignalerUpdateWrite $pending
	try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
	try { [IO.File]::Move($pending, $path) }
	finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force } }
	return $path
}

function Wait-AgentSignalerCapturedParticipantReady($Process, $ProcessIdentity, $Participant, $Manifest, [string] $Storage,
		[ValidateRange(1, 30000)][int] $BudgetMilliseconds = 30000) {
	$registrationPath = Join-Path $Storage ("instance-" + $ProcessIdentity.processId + '-' + $ProcessIdentity.creationTimeUtcTicks + '.json')
	$clock = [Diagnostics.Stopwatch]::StartNew()
	do {
		if ($Process.HasExited) { throw 'Restarted application exited before readiness.' }
		if (Test-Path -LiteralPath $registrationPath -PathType Leaf) {
			Assert-AgentSignalerUpdatePath $registrationPath
			$registration = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($registrationPath, [Text.UTF8Encoding]::new($false, $true))) 64KB
			Assert-AgentSignalerFields $registration @('schemaVersion', 'process', 'restart', 'pipeName')
			Assert-AgentSignalerFields $registration.process @('processId', 'creationTimeUtcTicks', 'userSid', 'sessionId', 'executablePath')
			Assert-AgentSignalerFields $registration.restart @('application', 'contextPath', 'background')
			Assert-AgentSignalerInteger $registration.schemaVersion 1 1
			Assert-AgentSignalerInteger $registration.process.processId 1 ([int]::MaxValue)
			Assert-AgentSignalerInteger $registration.process.creationTimeUtcTicks
			Assert-AgentSignalerInteger $registration.process.sessionId 0 ([int]::MaxValue)
			if ($registration.process.processId -ne $ProcessIdentity.processId -or
				$registration.process.creationTimeUtcTicks -ne $ProcessIdentity.creationTimeUtcTicks -or
				$registration.process.userSid -cne $ProcessIdentity.userSid -or $registration.process.sessionId -ne $ProcessIdentity.sessionId -or
				-not $registration.process.executablePath.Equals($ProcessIdentity.executablePath, [StringComparison]::OrdinalIgnoreCase) -or
				$registration.restart.contextPath -cne $Participant.restart.contextPath -or
				$registration.restart.application -cne $Participant.restart.application -or
				$registration.restart.background -isnot [bool] -or $registration.restart.background -ne $Participant.restart.background) {
				throw 'Restarted application identity changed.'
			}
			$remaining = $BudgetMilliseconds - [int]$clock.ElapsedMilliseconds
			if ($remaining -le 0) { break }
			$pipe = Open-AgentSignalerAuthenticatedPipe $registration.pipeName $registration.process $remaining
			try {
				Write-AgentSignalerPipeFrame $pipe ([ordered]@{ schemaVersion = 1; runId = ([guid]$Manifest.runId).ToString(); command = 'health' })
				$reply = Read-AgentSignalerPipeFrame $pipe ([Math]::Max(1, $BudgetMilliseconds - [int]$clock.ElapsedMilliseconds))
				Assert-AgentSignalerFields $reply @('schemaVersion', 'runId', 'accepted')
				Assert-AgentSignalerInteger $reply.schemaVersion 1 1
				if ([guid]$reply.runId -ne [guid]$Manifest.runId -or $reply.accepted -isnot [bool]) {
					throw 'Restarted application returned an invalid health acknowledgement.'
				}
			}
			finally { $pipe.Dispose() }
			if ($reply.accepted) { return [pscustomobject]@{ application = $Participant.restart.application; processId = $ProcessIdentity.processId; state = 'Ready' } }
		}
		$remaining = $BudgetMilliseconds - [int]$clock.ElapsedMilliseconds
		if ($remaining -le 0) { break }
		Start-Sleep -Milliseconds ([Math]::Min(100, $remaining))
	} while ($clock.ElapsedMilliseconds -lt $BudgetMilliseconds)
	throw 'Restarted application did not become locally ready within 30 seconds.'
}

function Start-AgentSignalerCapturedParticipant($Participant, $Manifest, [string] $Storage) {
	$restart = $Participant.restart
	$start = New-AgentSignalerCapturedStartInfo $Participant $Manifest
	$process = [Diagnostics.Process]::Start($start)
	if ($null -eq $process) { throw 'Captured application could not be restarted.' }
	$grantPath = $null
	try {
		$processId = $process.Id
		$ticks = $process.StartTime.ToUniversalTime().Ticks
		$processIdentity = [pscustomobject]@{ processId = $processId; creationTimeUtcTicks = $ticks;
			userSid = $Manifest.parent.userSid; sessionId = $process.SessionId; executablePath = $process.MainModule.FileName }
		$grantPath = Write-AgentSignalerRestartGrant $Participant $Manifest $Storage $processIdentity
		return Wait-AgentSignalerCapturedParticipantReady $process $processIdentity $Participant $Manifest $Storage
	}
	finally {
		try {
			if ($null -ne $grantPath -and (Test-Path -LiteralPath $grantPath)) {
				Assert-AgentSignalerUpdatePath $grantPath
				$grant = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($grantPath, [Text.UTF8Encoding]::new($false, $true))) 64KB
				if ($grant.runId -cne $Manifest.runId -or $grant.process.processId -ne $processId -or
					$grant.process.creationTimeUtcTicks -ne $ticks) { throw 'Restart grant ownership changed.' }
				Remove-Item -LiteralPath $grantPath -Force
			}
		}
		finally { $process.Dispose() }
	}
}

function Invoke-AgentSignalerAppHandoff([string] $ManifestPath) {
	$pins = [Collections.Generic.List[IDisposable]]::new()
	$binaryPins = [Collections.Generic.List[IDisposable]]::new()
	$participantPins = @{}
	$installer = $null
	$lease = $null
	$owned = $false
	$locations = $null
	$result = $null
	$restartAdmissionReady = $false
	$restartGateWritten = $false
	try {
		$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
		try { $sid = $identity.User.Value } finally { $identity.Dispose() }
		$session = (Get-Process -Id $PID).SessionId
		Assert-AgentSignalerUpdatePath $ManifestPath
		$pins.Add((Open-AgentSignalerReadPin $ManifestPath 64KB))
		$manifest = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($ManifestPath, [Text.UTF8Encoding]::new($false, $true))) 64KB
		$locations = Assert-AgentSignalerHandoffManifest $manifest $ManifestPath $sid $session
		$directory = $locations.RunDirectory
		do {
			Assert-AgentSignalerUpdatePath $directory
			$pins.Add((Open-AgentSignalerDirectoryPin $directory))
			if ($directory.Equals($env:LOCALAPPDATA.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { break }
			$directory = [IO.Path]::GetDirectoryName($directory)
		} while ($directory)
		foreach ($pin in $manifest.scripts) {
			$path = Join-Path $locations.RunDirectory $pin.name
			Assert-AgentSignalerUpdatePath $path
			$handle = Open-AgentSignalerReadPin $path 1MB
			$pins.Add($handle)
			if ($handle.Length -ne $pin.size -or (Get-Sha256 $path) -cne $pin.sha256.ToUpperInvariant()) { throw 'Staged updater dependency changed.' }
			Assert-AgentSignalerPublisher $path
		}
		[void](Get-AgentSignalerGate $locations.StorageDirectory $manifest)
		$installer = New-Object -ComObject WindowsInstaller.Installer
		$installation = [pscustomobject]@{ productCode = $manifest.productCode; userSid = $sid; installDirectory = $manifest.installDirectory; package = $manifest.package }
		$prior = Get-AgentSignalerInstalledIdentity $installer $installation
		if ($prior.Version -ne (ConvertTo-AgentSignalerVersion $manifest.installedVersion)) { throw 'Installed version changed before handoff.' }
		$result = [ordered]@{ schemaVersion = 1; runId = $manifest.runId; stage = 'WaitingForExit'; outcome = 'Pending';
			msiState = 'NotStarted'; installerExitCode = $null; installerProcessId = $null; installerCreationTimeUtcTicks = $null;
			targetVersion = $manifest.tag.Substring(1); diagnosticCode = $null; restarts = @() }
		Write-AgentSignalerRunResult $locations.RunDirectory $result
		foreach ($participant in $manifest.participants) {
			if ($participant.restart.application -cne 'Client') { continue }
			$handle = Open-AgentSignalerParticipantProcessPin $participant
			$pins.Add($handle)
			$participantPins[([string]$participant.process.processId + '-' + $participant.process.creationTimeUtcTicks)] = $handle
		}
		$pipe = Open-AgentSignalerAuthenticatedPipe $manifest.readinessPipe $manifest.parent
		try { Write-AgentSignalerPipeFrame $pipe ([ordered]@{ schemaVersion = 1; runId = $manifest.runId; nonce = $manifest.nonce; state = 'ready' }) }
		finally { $pipe.Dispose() }
		$lockPath = Join-Path $locations.StorageDirectory 'maintenance.lock'
		Assert-AgentSignalerUpdatePath $lockPath
		$clock = [Diagnostics.Stopwatch]::StartNew()
		do {
			try { $lease = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
			catch [IO.IOException] { Start-Sleep -Milliseconds 50 }
		} while ($null -eq $lease -and $clock.Elapsed.TotalSeconds -lt 15)
		if ($null -eq $lease) { throw 'Maintenance ownership was not transferred.' }
		[void](Get-AgentSignalerGate $locations.StorageDirectory $manifest -UpdaterOwned)
		$owned = $true
		Wait-AgentSignalerQuiescence $manifest.participants $manifest.installDirectory $manifest.package $sid $session ([Math]::Max(0, 60000 - [int]$clock.ElapsedMilliseconds))
		$result.stage = 'Downloading'; Write-AgentSignalerRunResult $locations.RunDirectory $result
		$release = Get-AgentSignalerPinnedDownload $manifest
		$checksumAsset = Get-AgentSignalerReleaseAsset @($release.assets) 'SHA256SUMS.txt'
		$checksumPath = Join-Path $locations.RunDirectory 'SHA256SUMS.txt'
		Save-GitHubAsset $checksumAsset $null $checksumPath
		$pins.Add((Open-AgentSignalerReadPin $checksumPath 64KB))
		if ((Get-Sha256 $checksumPath) -cne $manifest.checksumSha256.ToUpperInvariant()) { throw 'Pinned checksum manifest changed.' }
		$checksums = Read-AgentSignalerChecksums ([IO.File]::ReadAllLines($checksumPath))
		$packageName = 'AgentSignaler.' + $manifest.package + '.msi'
		if ($checksums[$packageName] -cne $manifest.packageSha256.ToUpperInvariant()) { throw 'Pinned package checksum changed.' }
		$asset = Get-AgentSignalerReleaseAsset @($release.assets) $packageName
		$packagePath = Join-Path $locations.RunDirectory $packageName
		Save-GitHubAsset $asset $null $packagePath
		$pins.Add((Open-AgentSignalerReadPin $packagePath 1GB))
		$result.stage = 'Validating'; Write-AgentSignalerRunResult $locations.RunDirectory $result
		if ((Get-Sha256 $packagePath) -cne $checksums[$packageName]) { throw 'Pinned package hash mismatch.' }
		Assert-AgentSignalerPublisher $packagePath
		$package = Read-Package $installer $packagePath $manifest.package
		if ((Get-AgentSignalerUpdateDecision $prior.Version $package.Version (ConvertTo-AgentSignalerVersion $manifest.tag.Substring(1))) -cne 'Upgrade') {
			throw 'Pinned release is not an upgrade.'
		}
		$current = Get-AgentSignalerInstalledIdentity $installer $installation
		if ($current.Version -ne $prior.Version -or $current.ProductCode -cne $prior.ProductCode) { throw 'Installed inventory changed before MSI.' }
		if (@(Get-AgentSignalerExactProcesses $manifest.installDirectory $manifest.package $sid $session).Count -ne 0) { throw 'An affected application started before MSI.' }
		[void](Get-AgentSignalerGate $locations.StorageDirectory $manifest -UpdaterOwned)
		# Revalidate trust and identity while all downloaded files remain read-only pinned.
		Assert-AgentSignalerPublisher $packagePath
		$verified = Read-Package $installer $packagePath $manifest.package
		if ((Get-Sha256 $packagePath) -cne $checksums[$packageName] -or $verified.ProductCode -cne $package.ProductCode -or
			$verified.Version -ne $package.Version) { throw 'Pinned package changed before MSI.' }
		if ([DateTimeOffset]::UtcNow -ge [DateTimeOffset]::Parse($manifest.expiresAt)) {
			throw 'Explicit servicing authorization expired before MSI.'
		}
		$installerPath = Get-AgentSignalerSystemInstallerPath
		$result.stage = 'Installing'; $result.msiState = 'Starting'; Write-AgentSignalerRunResult $locations.RunDirectory $result
		$arguments = Get-AgentSignalerMsiArguments $packagePath (Join-Path $locations.RunDirectory 'install.log')
		$process = Start-Process -FilePath $installerPath -ArgumentList $arguments -PassThru
		try {
			$result.installerProcessId = $process.Id
			$result.installerCreationTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
			$result.msiState = 'Running'; Write-AgentSignalerRunResult $locations.RunDirectory $result
			Write-Host 'Windows Installer is servicing the selected package. Do not close this window; Windows will not be restarted.'
			$process.WaitForExit()
			$result.installerExitCode = $process.ExitCode
			$result.msiState = 'Completed'
			$result.outcome = Get-AgentSignalerDetailedServicingOutcome $process.ExitCode
			Write-AgentSignalerRunResult $locations.RunDirectory $result
		}
		finally { $process.Dispose() }
		if ($result.outcome -ceq 'RebootRequired') {
			$result.stage = 'RebootRequired'
			Write-Warning 'Update requires a Windows restart. Applications will remain stopped; restart Windows yourself when ready.'
		}
		elseif ($result.outcome -ceq 'Completed') {
			$result.stage = 'Verifying'; Write-AgentSignalerRunResult $locations.RunDirectory $result
			$installation.productCode = $package.ProductCode
			$actual = Get-AgentSignalerInstalledIdentity $installer $installation
			if ($actual.Version -ne $package.Version) { throw 'Installed version does not match the selected release.' }
			Assert-AgentSignalerInstalledBinaries $manifest $package.Version $binaryPins
			$result.stage = 'Restarting'
		}
		else { throw 'Windows Installer did not complete the update.' }
	}
	catch {
		if ($null -eq $result) { throw 'App update validation failed before handoff; no installer was authorized.' }
		$result.stage = 'Failed'
		$result.diagnosticCode = if ($result.msiState -in @('Starting', 'Running')) { 'update_installer_unknown' } else { 'update_servicing_failed' }
		if ($result.outcome -ceq 'Pending') { $result.outcome = 'Failed' }
		Write-Warning 'The update did not complete. See this run''s result.json. No forced shutdown, downgrade or reboot will be performed.'
		# A failed MSI cannot prove configuration compatibility merely from old binary versions.
		if ($owned -and $result.msiState -ceq 'NotStarted') {
			try {
				$installation.productCode = $manifest.productCode
				$actual = Get-AgentSignalerInstalledIdentity $installer $installation
				if ($actual.Version -eq (ConvertTo-AgentSignalerVersion $manifest.installedVersion)) {
					Assert-AgentSignalerInstalledBinaries $manifest $actual.Version $binaryPins
					$result.diagnosticCode = 'update_prior_installation_intact'
				}
				else { throw 'The prior installed version is no longer intact.' }
			}
			catch { $result.diagnosticCode = 'update_recovery_required' }
		}
		elseif ($result.msiState -ceq 'Completed') { $result.diagnosticCode = 'update_recovery_required' }
	}
	finally {
		try {
			if ($owned -and $null -ne $result -and $result.msiState -notin @('Starting', 'Running')) {
				$gate = Get-AgentSignalerGate $locations.StorageDirectory $manifest -UpdaterOwned
				if ($result.stage -ceq 'Restarting' -or $result.diagnosticCode -ceq 'update_prior_installation_intact') {
					Write-AgentSignalerRestartGate $locations.StorageDirectory $manifest $gate
					$restartGateWritten = $true
					$restartAdmissionReady = $true
				}
			}
			if ($null -ne $lease) { $lease.Dispose(); $lease = $null }
			if ($restartAdmissionReady -and ($result.stage -ceq 'Restarting' -or $result.diagnosticCode -ceq 'update_prior_installation_intact')) {
				foreach ($participant in $manifest.participants) {
					$stillRunning = @(Get-AgentSignalerExactProcesses $manifest.installDirectory $manifest.package $sid $session |
						Where-Object { $_.ProcessId -eq $participant.process.processId -and
							$_.CreationTimeUtc.Ticks -eq $participant.process.creationTimeUtcTicks })
					if ($stillRunning.Count -eq 1) {
						$result.restarts += [pscustomobject]@{ application = $participant.restart.application; processId = $participant.process.processId; state = 'StillRunning' }
						continue
					}
					try {
						if ($participant.restart.application -ceq 'Client') {
							$key = [string]$participant.process.processId + '-' + $participant.process.creationTimeUtcTicks
							if (-not $participantPins.ContainsKey($key)) { throw 'Client shutdown intent was not captured.' }
							$exitCode = Get-AgentSignalerParticipantExitCode $participantPins[$key]
							if ($exitCode -eq 0) {
								Write-Host 'Client exited normally and will remain stopped; no automatic Client restart was requested.'
								continue
							}
							if ($exitCode -ne $script:AgentSignalerClientUpdateExitCode) { throw 'Client did not confirm update-owned shutdown.' }
						}
						$result.restarts += Start-AgentSignalerCapturedParticipant $participant $manifest $locations.StorageDirectory
					}
					catch {
						$result.restarts += [pscustomobject]@{ application = $participant.restart.application; processId = 0; state = 'Failed' }
						$result.stage = 'Failed'; $result.diagnosticCode = 'update_restart_not_ready'
					}
				}
				if ($result.stage -ceq 'Restarting') { $result.stage = 'Completed' }
			}
			if ($null -ne $result) {
				Write-AgentSignalerRunResult $locations.RunDirectory $result
				Write-Host "Update result: $($result.stage). Recovery record: $(Join-Path $locations.RunDirectory 'result.json')"
				Show-AgentSignalerUpdateResult $result (Join-Path $locations.RunDirectory 'result.json')
			}
		}
		finally {
			foreach ($pin in $binaryPins) { $pin.Dispose() }
			if ($restartGateWritten -or ($owned -and $null -ne $result -and $result.msiState -notin @('Starting', 'Running'))) {
				foreach ($name in @(('restart-' + $manifest.runId + '.json'), 'restart.json', 'gate.json')) {
					$path = Join-Path $locations.StorageDirectory $name
					if (-not (Test-Path -LiteralPath $path)) { continue }
					Assert-AgentSignalerUpdatePath $path
					$gate = ConvertFrom-AgentSignalerStrictJson ([IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))) 64KB
					if ([guid]$gate.runId -eq [guid]$manifest.runId -and $gate.owner.processId -eq $PID -and
						$gate.owner.creationTimeUtcTicks -eq (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks) {
						Remove-Item -LiteralPath $path -Force
					}
				}
			}
			if ($null -ne $lease) { $lease.Dispose() }
			if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
			foreach ($pin in $pins) { $pin.Dispose() }
			if ($owned -and $null -ne $result -and $result.msiState -notin @('Starting', 'Running')) {
				foreach ($entry in @(
						@('SHA256SUMS.txt', $manifest.checksumSha256),
						@('AgentSignaler.' + $manifest.package + '.msi', $manifest.packageSha256))) {
					$path = Join-Path $locations.RunDirectory $entry[0]
					if (Test-Path -LiteralPath $path -PathType Leaf) {
						Assert-AgentSignalerUpdatePath $path
						if ((Get-Sha256 $path) -ceq $entry[1].ToUpperInvariant()) { Remove-Item -LiteralPath $path -Force }
					}
				}
				$logPath = Join-Path $locations.RunDirectory 'install.log'
				if (Test-Path -LiteralPath $logPath -PathType Leaf) {
					Assert-AgentSignalerUpdatePath $logPath
					$log = [IO.File]::Open($logPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
					try { if ($log.Length -gt 4MB) { $log.SetLength(4MB); $log.Flush($true) } } finally { $log.Dispose() }
				}
			}
		}
	}
}
