param([string] $Application, [string] $Python, [string] $Upstream)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    $adapter = Join-Path $PSScriptRoot '..\Interop\SharedReference.py'
    $key = '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'
    $script:sharedTestPorts = [Collections.Generic.HashSet[int]]::new()
    function New-SharedTestPort {
        do {
            $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $probe.Start()
            try { $port = $probe.LocalEndpoint.Port } finally { $probe.Stop() }
        } while (!$script:sharedTestPorts.Add($port))
        return $port
    }
    function Wait-SharedTestPort {
        param([int] $Port, [hashtable] $Peer)
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($Peer.Process.HasExited) { throw "Daemon exited: $($Peer.Error.GetAwaiter().GetResult())" }
            $socket = [Net.Sockets.TcpClient]::new()
            try { $socket.Connect('127.0.0.1', $Port); return } catch { Start-Sleep -Milliseconds 50 }
            finally { $socket.Dispose() }
        }
        throw "Port $Port was not ready."
    }
    function Invoke-SharedRpc {
        param([int] $Port, [string] $RpcKey = $key)
        Invoke-TestProcess $Python @($adapter, 'client', '--port', '1', '--control-port', "$Port", '--key', $RpcKey) `
            -TimeoutSeconds 15 -Environment @{ PYTHONPATH = $Upstream; DOTRETICULUM_UPSTREAM = $Upstream; PYTHONIOENCODING = 'utf-8' }
    }
    function Write-SharedConfig {
        param([string] $Directory, [int] $SharedPort, [int] $ControlPort, [int] $PhysicalPort,
            [string] $InstanceType = 'tcp', [string] $InstanceName = 'default')
        $null = New-Item -ItemType Directory -Path $Directory
        @"
[reticulum]
share_instance = yes
shared_instance_type = $InstanceType
instance_name = $InstanceName
shared_instance_port = $SharedPort
instance_control_port = $ControlPort
rpc_key = $key
[interfaces]
[[Physical]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $PhysicalPort
"@ | Set-Content -LiteralPath (Join-Path $Directory 'config') -Encoding utf8
    }
}

Describe 'Shared-instance separate CLI processes' -Tag Runtime, E2E {
    It 'elects an abstract Unix owner and exposes the independently configured local-client role' -Skip:(!$IsLinux) {
        $shared = New-SharedTestPort
        $control = New-SharedTestPort
        $physical = New-SharedTestPort
        $instance = 'parity6-' + [guid]::NewGuid().ToString('N')
        $hostDir = Join-Path $TestDrive 'unix-host'
        $clientDir = Join-Path $TestDrive 'unix-client'
        Write-SharedConfig $hostDir $shared $control $physical 'unix' $instance
        Write-SharedConfig $clientDir $shared $control $physical 'unix' $instance
        $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostDir))
        $clientPeer = $null
        try {
            Wait-SharedTestPort $physical $hostPeer
            $clientPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $clientDir))
            Start-Sleep -Milliseconds 700
            $clientPeer.Process.HasExited | Should -BeFalse -Because 'only the abstract-socket owner may open the physical listener'
            $status = Invoke-TestProcess $executable ($prefix + @('rnsd', '--config', $clientDir, '--interactive')) `
                -InputText "interfaces`nexit`n"
            $status.Code | Should -Be 0 -Because $status.Error
            $status.Output | Should -Match 'Local shared instance.*LocalSharedPacketInterface'
            $status.Output | Should -Match 'Interfaces: 1'
            $hostPeer.Process.HasExited | Should -BeFalse
        }
        finally {
            if ($clientPeer) { Stop-TestProcess $clientPeer }
            Stop-TestProcess $hostPeer
        }
    }

    It 'elects a host and avoids opening physical listeners in local-client processes' {
        $shared = New-SharedTestPort
        $control = New-SharedTestPort
        $physical = New-SharedTestPort
        $hostDir = Join-Path $TestDrive 'host'
        $clientDir = Join-Path $TestDrive 'client'
        Write-SharedConfig $hostDir $shared $control $physical
        Write-SharedConfig $clientDir $shared $control $physical
        $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostDir))
        $clientPeer = $null
        try {
            Wait-SharedTestPort $physical $hostPeer
            Wait-SharedTestPort $control $hostPeer
            $clientPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $clientDir))
            Start-Sleep -Milliseconds 700
            $clientPeer.Process.HasExited | Should -BeFalse -Because 'a shared client must not attempt to bind the already-owned physical listener'
            $hostPeer.Process.HasExited | Should -BeFalse
        }
        finally {
            if ($clientPeer) { Stop-TestProcess $clientPeer }
            Stop-TestProcess $hostPeer
        }
    }

    It 'serves actual Python multiprocessing RPC and rejects a bad key without losing service' -Skip:([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
        $shared = New-SharedTestPort
        $control = New-SharedTestPort
        $physical = New-SharedTestPort
        $hostDir = Join-Path $TestDrive 'rpc-host'
        Write-SharedConfig $hostDir $shared $control $physical
        $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostDir))
        try {
            Wait-SharedTestPort $control $hostPeer
            $bad = Invoke-SharedRpc $control ('ff' * 32)
            $bad.Code | Should -Not -Be 0
            $bad.Error | Should -Match 'AuthenticationError'
            $good = Invoke-SharedRpc $control
            $good.Code | Should -Be 0 -Because $good.Error
            ($good.Output | ConvertFrom-Json).event | Should -Be 'response'
            ($good.Output | ConvertFrom-Json).packed | Should -Not -BeNullOrEmpty
        }
        finally { Stop-TestProcess $hostPeer }
    }

    It 'reconnects an existing local client after the independently launched daemon restarts' -Skip:([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
        $shared = New-SharedTestPort
        $control = New-SharedTestPort
        $physical = New-SharedTestPort
        $hostDir = Join-Path $TestDrive 'restart-host'
        $clientDir = Join-Path $TestDrive 'restart-client'
        Write-SharedConfig $hostDir $shared $control $physical
        Write-SharedConfig $clientDir $shared $control $physical
        $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostDir))
        $clientPeer = $null
        try {
            Wait-SharedTestPort $control $hostPeer
            $clientPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $clientDir))
            Start-Sleep -Milliseconds 500
            $before = Invoke-SharedRpc $control
            $before.Code | Should -Be 0 -Because $before.Error
            Stop-TestProcess $hostPeer
            $hostPeer = $null
            Start-Sleep -Milliseconds 300
            $clientPeer.Process.HasExited | Should -BeFalse
            $hostPeer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $hostDir))
            Wait-SharedTestPort $control $hostPeer
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $after = Invoke-SharedRpc $control
                $after.Code | Should -Be 0 -Because $after.Error
                # The adapter independently decodes upstream MessagePack to count accepted local peers.
                $summary = $after.Output | ConvertFrom-Json
                if ($summary.local_clients -ge 1) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            $summary.local_clients | Should -BeGreaterOrEqual 1
            $clientPeer.Process.HasExited | Should -BeFalse
        }
        finally {
            if ($clientPeer) { Stop-TestProcess $clientPeer }
            if ($hostPeer) { Stop-TestProcess $hostPeer }
        }
    }
}
