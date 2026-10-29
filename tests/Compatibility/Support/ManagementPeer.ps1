function Invoke-CompatibilityManagementQuery {
    param([hashtable] $Context, [string] $Directory, [switch] $Unauthorized,
        [ValidateRange(0, 32)][int] $AdditionalPeers = 0)
    $identity = Join-Path $Directory 'management.identity'
    $generated = Invoke-CompatibilityTool $Context 'rnid' @('--generate', $identity)
    if ($generated.Code -ne 0) { throw "Identity generation failed: $($generated.Output) $($generated.Error)" }
    $hash = [regex]::Match($generated.Output, '(?:New identity <|Identity hash:\s*)([0-9a-f]{32})').Groups[1].Value
    if (!$hash) { throw "Management identity hash missing: $($generated.Output)" }
    $allowed = if ($Unauthorized) { '00000000000000000000000000000000' } else { $hash }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $server = Join-Path $Directory 'reference'
    $client = Join-Path $Directory 'candidate'
    $null = New-Item -ItemType Directory -Path $server, $client
    @"
[reticulum]
share_instance = no
enable_transport = yes
enable_remote_management = yes
remote_management_allowed = $allowed
[logging]
loglevel = 0
[interfaces]
[[Candidate]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $server 'config') -Encoding utf8
    @"
[reticulum]
share_instance = no
[logging]
loglevel = 0
[interfaces]
[[Reference]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $client 'config') -Encoding utf8
    $probeIdentity = Join-Path $Directory 'probe.identity'
    $noProofIdentity = Join-Path $Directory 'no-proof.identity'
    [IO.File]::WriteAllBytes($probeIdentity, [byte[]](0..63))
    [IO.File]::WriteAllBytes($noProofIdentity, [byte[]](64..127))
    $interfaceObservation = Join-Path $Directory 'admitted-interfaces.json'
    $peer = Start-TestProcess $Context.Python @('-u',
        (Join-Path $PSScriptRoot '..\..\Interop\Parity7NetworkUtilitiesReference.py'),
        '--revision', $Context.Revision, '--config', $server, '--observe-management',
        '--interface-observation', $interfaceObservation,
        '--probe-identity', $probeIdentity, '--no-proof-identity', $noProofIdentity) -Environment $Context.PeerEnvironment
    $candidate = $null
    $connections = [Collections.Generic.List[Net.Sockets.TcpClient]]::new()
    $expectedPeers = @()
    try {
        $transport = Join-Path $server 'storage\transport_identity'
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($peer.Process.HasExited) { throw "Upstream management peer exited: $($peer.Error.GetAwaiter().GetResult())" }
            if (Test-Path -LiteralPath $transport) {
                $socket = [Net.Sockets.TcpClient]::new()
                try { $socket.Connect('127.0.0.1', $port); break }
                catch { Start-Sleep -Milliseconds 50 }
                finally { $socket.Dispose() }
            }
            else { Start-Sleep -Milliseconds 50 }
        } while ([DateTime]::UtcNow -lt $deadline)
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Upstream management peer did not open its interface.' }
        for ($index = 0; $index -lt $AdditionalPeers; $index++) {
            $socket = [Net.Sockets.TcpClient]::new()
            $connections.Add($socket)
            $socket.Connect('127.0.0.1', $port)
            $expectedPeers += "TCPInterface[Client on Candidate/127.0.0.1:$($socket.Client.LocalEndPoint.Port)]"
        }
        if ($AdditionalPeers) {
            $admissionDeadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                if ($peer.Process.HasExited) { throw "Upstream management peer exited before admission: $($peer.Error.GetAwaiter().GetResult())" }
                $peer.Process.StandardInput.WriteLine('interfaces')
                $peer.Process.StandardInput.Flush()
                Start-Sleep -Milliseconds 50
                $names = if (Test-Path -LiteralPath $interfaceObservation) {
                    $stream = [IO.File]::Open($interfaceObservation, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                    $reader = [IO.StreamReader]::new($stream)
                    try { @($reader.ReadToEnd() | ConvertFrom-Json) }
                    finally { $reader.Dispose() }
                } else { @() }
                $missing = @($expectedPeers | Where-Object { $_ -notin $names })
            } while ($missing.Count -and [DateTime]::UtcNow -lt $admissionDeadline)
            if ($missing.Count) { throw "Upstream management peer did not admit owned interfaces: $($missing -join ', ')" }
        }
        $shown = Invoke-CompatibilityTool $Context 'rnid' @('--identity', $transport, '--print-identity')
        if ($shown.Code -ne 0) { throw "Could not read upstream transport identity: $($shown.Output) $($shown.Error)" }
        $transportHash = [regex]::Match($shown.Output, '(?:Loaded Identity <|Identity hash:\s*)([0-9a-f]{32})').Groups[1].Value
        if (!$transportHash) { throw "Transport identity hash missing: $($shown.Output)" }
        $candidate = Start-TestProcess $Context.Executable ($Context.Prefix + @('rnstatus') + $Context.ToolSeparator +
            @('--config', $client, '-R', $transportHash, '-i', $identity, '-j', '-w', '10')) -Environment $Context.Environment
        Start-Sleep -Milliseconds 300
        $peer.Process.StandardInput.WriteLine('announce')
        $peer.Process.StandardInput.Flush()
        $running = $candidate
        $candidate = $null
        $result = Complete-TestProcess $running 40
        $peer.Process.StandardInput.WriteLine('quit')
        $peer.Process.StandardInput.Close()
        if (!$peer.Process.WaitForExit(10000)) { throw 'Upstream management peer did not shut down.' }
        $output = $peer.Output.GetAwaiter().GetResult()
        $output | Set-Content -LiteralPath (Join-Path $Directory 'peer-events.jsonl') -Encoding utf8
        [pscustomobject]@{
            Candidate = $result
            TransportIdentity = $transportHash
            ManagementIdentity = $hash
            ExpectedPeers = $expectedPeers
            Observations = @($output -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
            PeerExitCode = $peer.Process.ExitCode
            PeerError = $peer.Error.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($candidate) { Stop-TestProcess $candidate }
        Stop-TestProcess $peer
        foreach ($connection in $connections) { $connection.Dispose() }
    }
}
