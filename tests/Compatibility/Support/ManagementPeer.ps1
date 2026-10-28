function Invoke-CompatibilityManagementQuery {
    param([hashtable] $Context, [string] $Directory, [switch] $Unauthorized)
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
    $peer = Start-TestProcess $Context.Python @('-u',
        (Join-Path $PSScriptRoot '..\..\Interop\Parity7NetworkUtilitiesReference.py'),
        '--revision', $Context.Revision, '--config', $server, '--observe-management',
        '--probe-identity', $probeIdentity, '--no-proof-identity', $noProofIdentity) -Environment $Context.PeerEnvironment
    $candidate = $null
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
            Observations = @($output -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
            PeerExitCode = $peer.Process.ExitCode
            PeerError = $peer.Error.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($candidate) { Stop-TestProcess $candidate }
        Stop-TestProcess $peer
    }
}
