[CmdletBinding()]
param([string] $Version = '1.0.14')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'artifacts\publish\rpchost\AgentSignaler.RpcHost.exe'
$fixture = Join-Path $root 'artifacts\rpchost-publish-tests\firewall-prepared'
$fixedPathLease = $null
$ownsFixture = $false
$binary = Join-Path $fixture 'exe-only'
$data = Join-Path $fixture 'data'
$cache = Join-Path $fixture 'extraction'
$syntheticAzureCli = Join-Path $fixture 'synthetic\az.exe'
$syntheticDevTunnelCli = Join-Path $fixture 'synthetic\devtunnel.exe'
$process = $null
$socket = $null
$binaryAcl = $null
$parallelProcesses = @()
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function AssertSnapshot($Snapshot, [string] $Domain) {
    Assert ($Snapshot.protocolVersion -eq 1 -and $Snapshot.hostInstanceId -ceq $script:hostInstanceId -and
        $Snapshot.domain -ceq $Domain -and $Snapshot.revision -is [string] -and
        $Snapshot.revision -cmatch '^(0|[1-9][0-9]*)$' -and
        $Snapshot.PSObject.Properties.Name -contains 'state') 'Published snapshot envelope differs from the versioned contract.'
}
function AvailablePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function StartHost([string] $ExtractionPath, [string] $DataDirectory = $data, [int] $ControlPort = $rpcPort) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $binary 'AgentSignaler.RpcHost.exe'
    $start.Arguments = "--rpc-port $ControlPort --data-directory `"$DataDirectory`""
    $start.WorkingDirectory = $binary
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['AGENT_SIGNALER_DATA_DIR'] = $DataDirectory
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $ExtractionPath
    $start.EnvironmentVariables['AGENT_SIGNALER_LIVE_TUNNEL_TEST'] = '0'
    $start.EnvironmentVariables['AZURE_CONFIG_DIR'] = (Join-Path $fixture 'azure-disabled')
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    return [Diagnostics.Process]::Start($start)
}
function ConnectHost {
    $line = $process.StandardOutput.ReadLineAsync()
    Assert ($line.Wait(30000)) 'Published EXE missed its 30-second readiness watchdog.'
    if ([string]::IsNullOrWhiteSpace($line.Result)) {
        Assert ($process.WaitForExit(5000)) 'Published EXE closed stdout without completing startup.'
        $diagnostics = $process.StandardError.ReadToEnd()
        $category = [regex]::Match($diagnostics,
            '\A(?:invalidArguments|resourceOwned|invalidDataDirectory|invalidConfiguration|portConflict|rpcBindFailed|fatalRuntimeFailure|uncleanShutdown):').Value.TrimEnd(':')
        if ([string]::IsNullOrEmpty($category)) { $category = 'nativeOrUnexpectedStartupFailure' }
        throw "Published EXE exited without readiness (exit $($process.ExitCode), category $category)."
    }
    $ready = $line.Result | ConvertFrom-Json
    $instance = [guid]::Empty
    Assert ($ready.state -ceq 'transportReady' -and $ready.protocolVersion -eq 1 -and $ready.rpcPort -eq $rpcPort -and
        [guid]::TryParse([string]$ready.hostInstanceId, [ref]$instance) -and $instance -ne [guid]::Empty -and
        $ready.hostInstanceId -ceq $instance.ToString('D')) 'Unexpected readiness contract.'
    $script:hostInstanceId = $ready.hostInstanceId
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$rpcPort/health" -TimeoutSec 10
    Assert ($health.protocolVersion -eq 1) 'Published transport health failed.'
    $script:socket = [Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.SetRequestHeader('Origin', 'http://localhost')
    $cancel = [Threading.CancellationTokenSource]::new(10000)
    try { [void]$socket.ConnectAsync([uri]"ws://127.0.0.1:$rpcPort/rpc", $cancel.Token).GetAwaiter().GetResult() }
    finally { $cancel.Dispose() }
}
function Rpc([string] $Method) {
    $id = [guid]::NewGuid().ToString('N')
    $text = @{ jsonrpc = '2.0'; id = $id; method = $Method } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $cancel = [Threading.CancellationTokenSource]::new(10000)
    try {
        $assets = Get-Content (Join-Path $root 'src\AgentSignaler.RpcHost\obj\project.assets.json') -Raw | ConvertFrom-Json
        Assert (@($assets.libraries.PSObject.Properties.Name | Where-Object {
            $_ -match '(?i)(Microsoft\.WindowsAppSDK|Microsoft\.UI\.Xaml|AgentSignaler\.Dashboard/)'
        }).Count -eq 0) 'RpcHost graph unexpectedly depends on WinUI or Windows App SDK.'
        [void]$socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text,
            $true, $cancel.Token).GetAwaiter().GetResult()
        while ($true) {
            $message = [IO.MemoryStream]::new()
            try {
                do {
                    $buffer = New-Object byte[] 16384
                    $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $cancel.Token).GetAwaiter().GetResult()
                    Assert ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Text) 'Unexpected WebSocket closure.'
                    $message.Write($buffer, 0, $received.Count)
                    Assert ($message.Length -le 4MB) 'Published response exceeded its bound.'
                } while (-not $received.EndOfMessage)
                $json = [Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
                if ($json.PSObject.Properties.Name -contains 'id' -and $json.id -ceq $id) {
                    Assert ($json.jsonrpc -ceq '2.0' -and $json.PSObject.Properties.Name -contains 'result' -and
                        $json.PSObject.Properties.Name -notcontains 'error') "Published RPC command failed: $Method"
                    return $json.result
                }
            }
            finally { $message.Dispose() }
        }
    }
    finally { $cancel.Dispose() }
}
function StopHost {
    $shutdown = Rpc 'system.shutdown'
    Assert ($shutdown.state -ceq 'stopping') 'Published shutdown acceptance differs from the contract.'
    if ($socket.State -in @([Net.WebSockets.WebSocketState]::Open, [Net.WebSockets.WebSocketState]::CloseReceived)) {
        $closeDeadline = [Threading.CancellationTokenSource]::new(5000)
        try {
            [void]$socket.CloseOutputAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '',
                $closeDeadline.Token).GetAwaiter().GetResult()
        }
        finally { $closeDeadline.Dispose() }
    }
    Assert ($process.WaitForExit(30000)) 'Published EXE missed its shutdown deadline.'
    Assert ($process.ExitCode -eq 0) 'Published EXE shutdown was unclean.'
    $extra = $process.StandardOutput.ReadToEnd()
    $errors = $process.StandardError.ReadToEnd()
    Assert ([string]::IsNullOrWhiteSpace($extra) -and [string]::IsNullOrWhiteSpace($errors)) 'Unexpected process output after its single readiness line.'
    $socket.Dispose(); $script:socket = $null
    $process.Dispose(); $script:process = $null
}
try {
    $lockDirectory = Join-Path $root 'artifacts\test-firewall'
    [void](New-Item -ItemType Directory -Path $lockDirectory -Force)
    $fixedPathLease = [IO.FileStream]::new((Join-Path $lockDirectory 'publish-smoke.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None,
        1, [IO.FileOptions]::DeleteOnClose)
    Assert (-not (Test-Path -LiteralPath $fixture)) 'Fixed publish fixture already exists; inspect the previous run before removing it.'
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($source).ProductVersion.Split('+')[0]
    Assert ($productVersion -ceq $Version) 'Published EXE version differs from the MSI/release.'
    [void](New-Item -ItemType Directory -Path $binary, $data, $cache -Force)
    $ownsFixture = $true
    Copy-Item -LiteralPath $source -Destination (Join-Path $binary 'AgentSignaler.RpcHost.exe')
    $binaryAcl = Get-Acl -LiteralPath $binary
    $readOnly = Get-Acl -LiteralPath $binary
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity.User,
            [Security.AccessControl.FileSystemRights]::Write,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
        $readOnly.AddAccessRule($rule)
        Set-Acl -LiteralPath $binary -AclObject $readOnly
    }
    finally { $identity.Dispose() }
    $rpcPort = AvailablePort
    do { $receiverPort = AvailablePort } while ($receiverPort -eq $rpcPort)
    @{ Port = $receiverPort; RpcPort = $rpcPort; ConnectionMode = 0; AutoStartSharing = $false;
       ReceiveDetailedConversations = $false; AzureCliPath = $syntheticAzureCli;
       DevTunnelCliPath = $syntheticDevTunnelCli } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $data 'dashboard-settings.json') -Encoding UTF8
    $machine = [guid]::NewGuid().ToString()
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $process = StartHost $cache
        ConnectHost
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $status = Rpc 'system.getStatus'
            AssertSnapshot $status 'system'
            if ($status.state.lifecycle -ceq 'operational') { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($status.state.lifecycle -ceq 'operational' -and $status.state.initialAttemptCompleted -eq $true) 'Isolated published runtime did not become operational.'
        if ($attempt -eq 0) {
            $report = @{
                protocolVersion = 1; eventId = [guid]::NewGuid().ToString(); machineId = $machine
                machineName = 'PACKAGING-FIXTURE'; client = 'copilot-cli'; clientVersion = $Version; sessionId = 'fixture'
                event = 'sessionStart'; reportedAtUtc = [DateTime]::UtcNow.ToString('o')
            } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "http://127.0.0.1:$receiverPort/api/v1/status" -Method Post `
                -Body $report -ContentType 'application/json' -TimeoutSec 10 | Out-Null
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $machines = Rpc 'machines.list'
            AssertSnapshot $machines 'machines'
            $matched = @($machines.state.items | Where-Object { $_.machineId -ceq $machine })
            if ($matched.Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($matched.Count -eq 1) 'Published EXE did not read the synthetic SQLite machine, including after restart.'
        StopHost
        $database = Join-Path $data 'dashboard.db'
        $stream = [IO.File]::OpenRead($database)
        try {
            $header = New-Object byte[] 16
            Assert ($stream.Read($header, 0, 16) -eq 16 -and
                [Text.Encoding]::ASCII.GetString($header) -ceq "SQLite format 3`0") 'Published native SQLite write was not durable.'
        }
        finally { $stream.Dispose() }
    }
    Assert (@(Get-ChildItem -LiteralPath $binary -Force).Count -eq 1) 'Single-file host required adjacent/generated files.'
    Assert (@(Get-ChildItem -LiteralPath $cache -Recurse -File -Filter 'e_sqlite3.dll').Count -gt 0) 'Native SQLite was not extracted to the isolated cache.'
    $coldCache = Join-Path $fixture 'concurrent-extraction'
    [void](New-Item -ItemType Directory -Path $coldCache)
    $originalPort = $rpcPort
    $usedPorts = @($rpcPort, $receiverPort)
    for ($i = 0; $i -lt 2; $i++) {
        $parallelData = Join-Path $fixture "concurrent-data-$i"
        [void](New-Item -ItemType Directory -Path $parallelData)
        do { $control = AvailablePort } while ($usedPorts -contains $control)
        $usedPorts += $control
        do { $receiver = AvailablePort } while ($usedPorts -contains $receiver)
        $usedPorts += $receiver
        @{ Port = $receiver; RpcPort = $control; ConnectionMode = 0; AutoStartSharing = $false;
           ReceiveDetailedConversations = $false; AzureCliPath = $syntheticAzureCli;
           DevTunnelCliPath = $syntheticDevTunnelCli } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $parallelData 'dashboard-settings.json') -Encoding UTF8
        $parallelProcesses += [pscustomobject]@{ Process = (StartHost $coldCache $parallelData $control); Port = $control }
    }
    foreach ($child in $parallelProcesses) {
        $process = $child.Process; $rpcPort = $child.Port
        ConnectHost
        AssertSnapshot (Rpc 'system.getStatus') 'system'
        StopHost
        $child.Process = $null
    }
    $rpcPort = $originalPort
    Assert (@(Get-ChildItem -LiteralPath $coldCache -Recurse -File -Filter 'e_sqlite3.dll').Count -gt 0) 'Concurrent cold extraction did not produce SQLite.'
    $badCache = Join-Path $fixture 'not-a-directory'
    [IO.File]::WriteAllText($badCache, 'fixture')
    $process = StartHost $badCache
    Assert ($process.WaitForExit(30000) -and $process.ExitCode -ne 0) 'Failed extraction must fail boundedly, not fall back to a user cache.'
    $process.Dispose(); $process = $null
    Write-Host 'RpcHost EXE-only smoke passed: read-only distribution, real RPC/SQLite durability, isolated and concurrent cold extraction, clean shutdown and failed extraction. Clean Windows image without installed runtimes: Not run - deferred release acceptance.'
}
finally {
    try {
        if ($null -ne $socket) { $socket.Dispose() }
        if ($null -ne $process) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; [void]$process.WaitForExit(5000) }
            $process.Dispose()
            foreach ($child in $parallelProcesses) {
                if ([object]::ReferenceEquals($child.Process, $process)) { $child.Process = $null }
            }
        }
        foreach ($child in $parallelProcesses) {
            if ($null -eq $child.Process) { continue }
            try {
                if (-not $child.Process.HasExited) { Stop-Process -Id $child.Process.Id -Force; [void]$child.Process.WaitForExit(5000) }
            }
            finally { $child.Process.Dispose() }
        }
        if ($null -ne $binaryAcl) { Set-Acl -LiteralPath $binary -AclObject $binaryAcl }
        if ($ownsFixture -and (Test-Path -LiteralPath $fixture)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
    }
    finally { if ($null -ne $fixedPathLease) { $fixedPathLease.Dispose() } }
}
