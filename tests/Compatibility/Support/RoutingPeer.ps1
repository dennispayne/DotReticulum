function New-CompatibilityRoutingPair {
    param([hashtable] $Context, [string] $Directory)
    $pair = New-CompatibilityTransferPair $Context $Directory
    $candidate = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    $router = $null
    try {
        $router = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $candidatePort = $candidate.Client.LocalEndPoint.Port
        $routerPort = $router.Client.LocalEndPoint.Port
    }
    finally {
        if ($router) { $router.Dispose() }
        $candidate.Dispose()
    }
    $pair.ReferenceConfig = New-CompatibilityTransferConfig $pair.ReferenceConfig $pair.Port
    @"
[reticulum]
share_instance = no
[logging]
loglevel = 6
[interfaces]
[[UDP to fixed upstream router]]
type = UDPInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $candidatePort
forward_ip = 127.0.0.1
forward_port = $routerPort
"@ | Set-Content -LiteralPath (Join-Path $pair.CandidateConfig 'config') -Encoding utf8
    $pair.RouterConfig = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'router')).FullName
    @"
[reticulum]
share_instance = no
enable_transport = yes
[logging]
loglevel = 6
[interfaces]
[[UDP candidate]]
type = UDPInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $routerPort
forward_ip = 127.0.0.1
forward_port = $candidatePort
[[TCP reference]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $($pair.Port)
"@ | Set-Content -LiteralPath (Join-Path $pair.RouterConfig 'config') -Encoding utf8
    $pair
}

function Start-CompatibilityRoutingTransport {
    param([hashtable] $Context, [hashtable] $Pair)
    $peer = Start-TestProcess $Context.Python @('-u',
        (Join-Path $PSScriptRoot '..\..\Interop\reference_cli.py'),
        '--revision', $Context.Revision, '--tool', 'rnsd', '--', '--config', $Pair.RouterConfig) `
        -Environment $Context.PeerEnvironment
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($peer.Process.HasExited) { throw "Upstream routing daemon exited: $($peer.Error.GetAwaiter().GetResult())" }
            $socket = [Net.Sockets.TcpClient]::new()
            try {
                $socket.Connect('127.0.0.1', $Pair.Port)
                return $peer
            }
            catch [Net.Sockets.SocketException] {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Upstream routing daemon did not open its TCP interface.' }
                Start-Sleep -Milliseconds 50
            }
            finally { $socket.Dispose() }
        } while ($true)
    }
    catch {
        Stop-CompatibilityTransferListener $peer $Pair.RouterConfig
        throw
    }
}
