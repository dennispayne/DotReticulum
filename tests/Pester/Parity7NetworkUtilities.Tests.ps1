param([string] $Application, [string] $Python, [string] $Upstream)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    $script:utilityWork = Join-Path (Get-Location) ('.artifacts\p7-network-utilities-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $script:utilityWork
    $script:utilityPorts = [Collections.Generic.HashSet[int]]::new()
    $script:utilityRpcKey = '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'
    $script:utilityReferenceAdapter = Join-Path $PSScriptRoot '..\Interop\Parity7NetworkUtilitiesReference.py'

    function Invoke-NetworkUtility([string] $Tool, [string[]] $Arguments) {
        Invoke-TestProcess $executable ($prefix + @($Tool) + $Arguments)
    }

    function New-UtilityPort {
        do {
            $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $probe.Start()
            try { $port = $probe.LocalEndpoint.Port } finally { $probe.Stop() }
        } while (!$script:utilityPorts.Add($port))
        return $port
    }

    function Write-UtilitySharedConfig([string] $Directory, [int] $SharedPort, [int] $ControlPort, [int] $PhysicalPort) {
        $null = New-Item -ItemType Directory -Path $Directory -Force
        @"
[reticulum]
share_instance = yes
shared_instance_type = tcp
shared_instance_port = $SharedPort
instance_control_port = $ControlPort
rpc_key = $script:utilityRpcKey
[interfaces]
[[Physical]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $PhysicalPort
"@ | Set-Content -LiteralPath (Join-Path $Directory 'config') -Encoding utf8
    }

    function Wait-UtilityPort([int] $Port, [hashtable] $Peer) {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($Peer.Process.HasExited) {
                throw "Daemon exited before opening port $Port.`n$($Peer.Error.GetAwaiter().GetResult())"
            }
            $socket = [Net.Sockets.TcpClient]::new()
            try { $socket.Connect('127.0.0.1', $Port); return }
            catch { Start-Sleep -Milliseconds 50 }
            finally { $socket.Dispose() }
        }
        throw "Daemon did not open port $Port."
    }

    function New-UtilityIdentity([string] $Path) {
        $result = Invoke-NetworkUtility 'rnid' @('--generate', $Path)
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        $match = [regex]::Match($result.Output, '(?:New identity <|Identity hash:\s*)([0-9a-f]{32})')
        $hash = $match.Groups[1].Value
        $hash | Should -Match '^[0-9a-f]{32}$'
        return $hash
    }

    function Get-UtilityDestination([string] $IdentityPath) {
        $result = Invoke-NetworkUtility 'rnid' @('--identity', $IdentityPath, '--hash', 'app.aspect')
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        $hash = [regex]::Match($result.Output, 'app\.aspect destination for this Identity is <([0-9a-f]{32})>').Groups[1].Value
        $hash | Should -Match '^[0-9a-f]{32}$'
        return $hash
    }

    function Start-UtilityReferenceNode([string] $Config, [string] $ProbeIdentity, [string] $NoProofIdentity) {
        $info = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Python).Path)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.WorkingDirectory = (Resolve-Path -LiteralPath $Upstream).Path
        foreach ($argument in @('-u', $script:utilityReferenceAdapter, '--config', $Config,
                '--probe-identity', $ProbeIdentity, '--no-proof-identity', $NoProofIdentity)) {
            $info.ArgumentList.Add($argument)
        }
        $info.Environment['DOTRETICULUM_UPSTREAM'] = $Upstream
        $info.Environment['PYTHONPATH'] = $Upstream
        $info.Environment['PYTHONIOENCODING'] = 'utf-8'
        $process = [Diagnostics.Process]::Start($info)
        $errorTask = $process.StandardError.ReadToEndAsync()
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while ([DateTime]::UtcNow -lt $deadline) {
                if ($process.HasExited) { throw "Pinned reference node exited: $($errorTask.GetAwaiter().GetResult())" }
                $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(2)).GetAwaiter().GetResult()
                if (!$line) { continue }
                $event = $line | ConvertFrom-Json -AsHashtable
                if ($event.event -eq 'ready') {
                    if ($event.revision -ne '1.5.7' -or !$event.module.StartsWith($Upstream, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Reference node import/version was not pinned: $line"
                    }
                    return @{ Process = $process; Error = $errorTask; Ready = $event }
                }
            }
            throw 'Pinned reference node did not report readiness within 30 seconds.'
        }
        catch {
            if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
            throw
        }
    }

    function Send-UtilityReferenceCommand([hashtable] $Peer, [string] $Command) {
        $Peer.Process.StandardInput.WriteLine($Command)
        $Peer.Process.StandardInput.Flush()
        Start-Sleep -Milliseconds 250
    }

    function Stop-UtilityReferenceNode([hashtable] $Peer) {
        if (!$Peer.Process.HasExited) {
            $Peer.Process.StandardInput.WriteLine('quit')
            $Peer.Process.StandardInput.Close()
            if (!$Peer.Process.WaitForExit(10000)) {
                $Peer.Process.Kill($true)
                $Peer.Process.WaitForExit()
            }
        }
        $output = $Peer.Process.StandardOutput.ReadToEnd()
        $error = $Peer.Error.GetAwaiter().GetResult()
        $Peer.Process.Dispose()
        return @{ Output = $output; Error = $error }
    }
}

AfterAll {
    if (Test-Path -LiteralPath $utilityWork) {
        Remove-Item -LiteralPath $utilityWork -Recurse -Force
    }
}

Describe 'Parity 7 rnstatus, rnpath and rnprobe processes' -Tag E2E, NetworkUtilities {
    It 'exposes independently dispatchable help and version commands' -Tag 'Contract:UTIL-NET-001' {
        foreach ($tool in 'rnstatus', 'rnpath', 'rnprobe') {
            $help = Invoke-NetworkUtility $tool @('--help')
            $help.Code | Should -Be 0 -Because $help.Error
            $help.Output | Should -Match ([regex]::Escape($tool))
            $version = Invoke-NetworkUtility $tool @('--version')
            $version.Code | Should -Be 0 -Because $version.Error
            $version.Output | Should -Match ([regex]::Escape($tool))
        }
    }

    It 'rejects unsupported options without opening a Reticulum configuration' -Tag 'Contract:UTIL-NET-001' {
        $result = Invoke-NetworkUtility 'rnstatus' @('--not-an-option')
        $result.Code | Should -Be 2
        $result.Error | Should -Match 'Unknown option'
    }

    It 'rejects invalid monitor and path-filter values before runtime startup' -Tag 'Contract:UTIL-NET-001' {
        $status = Invoke-NetworkUtility 'rnstatus' @('-m', '-I', 'NaN')
        $status.Code | Should -Be 1
        $status.Error | Should -Match 'monitor interval'
        $path = Invoke-NetworkUtility 'rnpath' @('-t', '-m', '-1')
        $path.Code | Should -Be 1
        $path.Error | Should -Match 'Maximum hops'
    }

    It 'rejects oversized or negative probe payload input before loading configuration' -Tag 'Contract:UTIL-NET-001' {
        $probe = Invoke-NetworkUtility 'rnprobe' @('--size', '-1', 'app.aspect', ('0' * 32))
        $probe.Code | Should -Be 1
        $probe.Error | Should -Match 'nonnegative'
    }

    It 'queries local daemon management and sends a real probe path request from separate CLI processes' -Tag 'Contract:UTIL-RNSTATUS-001', 'Contract:UTIL-RNPATH-001' {
        $shared = New-UtilityPort
        $control = New-UtilityPort
        $physical = New-UtilityPort
        $hostConfig = Join-Path $utilityWork 'host'
        $clientConfig = Join-Path $utilityWork 'client'
        Write-UtilitySharedConfig $hostConfig $shared $control $physical
        Write-UtilitySharedConfig $clientConfig $shared $control $physical
        $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostConfig))
        try {
            Wait-UtilityPort $physical $hostPeer
            Wait-UtilityPort $control $hostPeer

            $status = Invoke-NetworkUtility 'rnstatus' @('--config', $clientConfig, '--json')
            $status.Code | Should -Be 0 -Because ($status.Output + $status.Error)
            $statusData = $status.Output | ConvertFrom-Json -AsHashtable
            $statusData.ContainsKey('interfaces') | Should -BeTrue
            $statusData.ContainsKey('transport_id') | Should -BeTrue

            $paths = Invoke-NetworkUtility 'rnpath' @('--config', $clientConfig, '--table', '--json')
            $paths.Code | Should -Be 0 -Because ($paths.Output + $paths.Error)
            ($paths.Output | ConvertFrom-Json -AsHashtable).Count | Should -Be 0

            $unknown = '00000000000000000000000000000000'
            $drop = Invoke-NetworkUtility 'rnpath' @('--config', $clientConfig, '--drop', $unknown)
            $drop.Code | Should -Be 1
            $drop.Output | Should -Match 'Unable to drop path'

            $blocked = Invoke-NetworkUtility 'rnpath' @('--config', $clientConfig, '--blackhole', $unknown, '--reason', 'p7-process-test')
            $blocked.Code | Should -Be 0 -Because ($blocked.Output + $blocked.Error)
            $blocked.Output | Should -Match 'Blackholed identity'
            $unblocked = Invoke-NetworkUtility 'rnpath' @('--config', $clientConfig, '--unblackhole', $unknown)
            $unblocked.Code | Should -Be 0 -Because ($unblocked.Output + $unblocked.Error)
            $unblocked.Output | Should -Match 'Lifted blackhole'

            $probe = Invoke-NetworkUtility 'rnprobe' @('--config', $clientConfig, '--timeout', '0.1',
                'app.aspect', $unknown)
            $probe.Code | Should -Be 1
            $probe.Output | Should -Match 'Path to .* requested'
            $probe.Output | Should -Match 'Path request timed out'
            $hostPeer.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $hostPeer }
    }

    It 'performs authenticated remote status and path requests over separate native node processes' -Tag 'Contract:UTIL-RNSTATUS-001', 'Contract:UTIL-RNPATH-001' {
        $port = New-UtilityPort
        $identityFile = Join-Path $utilityWork 'remote-management.identity'
        $identity = New-UtilityIdentity $identityFile

        $serverConfig = Join-Path $utilityWork 'remote-server'
        $clientConfig = Join-Path $utilityWork 'remote-client'
        $null = New-Item -ItemType Directory -Path $serverConfig
        $null = New-Item -ItemType Directory -Path $clientConfig
        @"
[reticulum]
share_instance = yes
shared_instance_type = tcp
shared_instance_port = $(New-UtilityPort)
instance_control_port = $(New-UtilityPort)
rpc_key = $utilityRpcKey
enable_transport = yes
enable_remote_management = yes
remote_management_allowed = $identity
[logging]
loglevel = 0
[interfaces]
[[Remote TCP]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $serverConfig 'config') -Encoding utf8
        @"
[reticulum]
share_instance = no
[logging]
loglevel = 0
[interfaces]
[[To remote]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $clientConfig 'config') -Encoding utf8

$server = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $serverConfig))
try {
    Wait-UtilityPort $port $server
    $transportIdentity = Join-Path $serverConfig 'storage\transport_identity'
    $serverIdentity = Invoke-NetworkUtility 'rnid' @('--show', $transportIdentity)
    $serverIdentity.Code | Should -Be 0 -Because ($serverIdentity.Output + $serverIdentity.Error)
    $transportId = [regex]::Match($serverIdentity.Output, 'Identity hash:\s*([0-9a-f]{32})').Groups[1].Value
    $transportId | Should -Match '^[0-9a-f]{32}$'

    $serverStatus = Invoke-NetworkUtility 'rnstatus' @('--config', $serverConfig, '-z', '-j')
    $serverStatus.Code | Should -Be 0 -Because ($serverStatus.Output + $serverStatus.Error)
    $statusTransportId = ($serverStatus.Output | ConvertFrom-Json -AsHashtable).transport_id
    $statusTransportId | Should -Be $transportId

    $status = Invoke-NetworkUtility 'rnstatus' @('--config', $clientConfig, '-R', $transportId,
        '-i', $identityFile, '-j', '-w', '15')
            $status.Code | Should -Be 0 -Because ($status.Output + $status.Error)
            ($status.Output | ConvertFrom-Json -AsHashtable).ContainsKey('interfaces') | Should -BeTrue

            $paths = Invoke-NetworkUtility 'rnpath' @('--config', $clientConfig, '-R', $transportId,
                '-i', $identityFile, '-t', '-j', '-W', '15')
            $paths.Code | Should -Be 0 -Because ($paths.Output + $paths.Error)
            ($paths.Output | ConvertFrom-Json -AsHashtable).Count | Should -Be 0
            $server.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $server }
    }

    It 'queries pinned Python remote management and probes announced reply and no-proof destinations' -Tag 'Contract:UTIL-RNSTATUS-001', 'Contract:UTIL-RNPROBE-001', 'Reference' -Skip:([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
        $managementIdentity = Join-Path $utilityWork 'python-management.identity'
        $probeIdentity = Join-Path $utilityWork 'python-probe.identity'
        $noProofIdentity = Join-Path $utilityWork 'python-no-proof.identity'
        $managementHash = New-UtilityIdentity $managementIdentity
        $null = New-UtilityIdentity $probeIdentity
        $null = New-UtilityIdentity $noProofIdentity
        $probeHash = Get-UtilityDestination $probeIdentity
        $noProofHash = Get-UtilityDestination $noProofIdentity
        $port = New-UtilityPort
        $pythonConfig = Join-Path $utilityWork 'python-node'
        $nativeConfig = Join-Path $utilityWork 'native-client'
        $null = New-Item -ItemType Directory -Path $pythonConfig
        $null = New-Item -ItemType Directory -Path $nativeConfig
        @"
[reticulum]
share_instance = no
enable_transport = yes
enable_remote_management = yes
remote_management_allowed = $managementHash
[logging]
loglevel = 0
[interfaces]
[[Native]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $pythonConfig 'config') -Encoding utf8
        @"
[reticulum]
share_instance = no
[logging]
loglevel = 0
[interfaces]
[[To Python]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $nativeConfig 'config') -Encoding utf8

        $pythonNode = Start-UtilityReferenceNode $pythonConfig $probeIdentity $noProofIdentity
        try {
            Wait-UtilityPort $port @{ Process = $pythonNode.Process; Error = $pythonNode.Error }
            $pythonNode.Ready.transport_identity | Should -Match '^[0-9a-f]{32}$'
            $pythonNode.Ready.remote_management | Should -Match '^[0-9a-f]{32}$'
            $pythonNode.Ready.probe | Should -Be $probeHash
            $pythonNode.Ready.no_proof | Should -Be $noProofHash

            $remoteStatus = Start-TestProcess $executable ($prefix + @('rnstatus', '--config', $nativeConfig,
                '-R', $pythonNode.Ready.transport_identity, '-i', $managementIdentity, '-j', '-w', '15'))
            Start-Sleep -Milliseconds 300
            Send-UtilityReferenceCommand $pythonNode 'announce'
            $status = Complete-TestProcess $remoteStatus 30
            $status.Code | Should -Be 0 -Because ($status.Output + $status.Error)
            ($status.Output | ConvertFrom-Json -AsHashtable).ContainsKey('interfaces') | Should -BeTrue

            $remotePaths = Start-TestProcess $executable ($prefix + @('rnpath', '--config', $nativeConfig,
                '-R', $pythonNode.Ready.transport_identity, '-i', $managementIdentity, '-t', '-j', '-W', '15'))
            Start-Sleep -Milliseconds 300
            Send-UtilityReferenceCommand $pythonNode 'announce'
            $paths = Complete-TestProcess $remotePaths 30
            $paths.Code | Should -Be 0 -Because ($paths.Output + $paths.Error)

            $probePeer = Start-TestProcess $executable ($prefix + @('rnprobe', '--config', $nativeConfig,
                '--timeout', '8', 'app.aspect', $probeHash))
            Start-Sleep -Milliseconds 300
            Send-UtilityReferenceCommand $pythonNode 'announce'
            $probe = Complete-TestProcess $probePeer 30
            $probe.Code | Should -Be 0 -Because ($probe.Output + $probe.Error)
            $probe.Output | Should -Match 'Valid reply from'
            $probe.Output | Should -Match 'packet loss 0%'

            $lossPeer = Start-TestProcess $executable ($prefix + @('rnprobe', '--config', $nativeConfig,
                '--timeout', '1', 'app.aspect', $noProofHash))
            Start-Sleep -Milliseconds 300
            Send-UtilityReferenceCommand $pythonNode 'announce'
            $loss = Complete-TestProcess $lossPeer 30
            $loss.Code | Should -Be 2 -Because ($loss.Output + $loss.Error)
            $loss.Output | Should -Match 'Probe timed out'
            $loss.Output | Should -Match 'packet loss 100%'
        }
        finally {
            $referenceOutput = Stop-UtilityReferenceNode $pythonNode
        }
        $referenceOutput.Output | Should -Match '"event": "probe_received"'
        $referenceOutput.Output | Should -Match ('"destination": "' + [regex]::Escape($probeHash) + '"')
        $referenceOutput.Output | Should -Match ('"destination": "' + [regex]::Escape($noProofHash) + '"')
        $referenceOutput.Output | Should -Match '"payload_bytes": 16'
        $referenceOutput.Error | Should -Be ''
    }
}
