function Invoke-CompatibilityProbe {
    param([hashtable] $Context, [string] $Directory, [switch] $WithoutProof)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $peerConfig = Join-Path $Directory 'reference'
    $candidateConfig = Join-Path $Directory 'candidate'
    $null = New-Item -ItemType Directory -Path $peerConfig, $candidateConfig
    @"
[reticulum]
share_instance = no
enable_remote_management = yes
[logging]
loglevel = 0
[interfaces]
[[Candidate]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $peerConfig 'config') -Encoding utf8
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
"@ | Set-Content -LiteralPath (Join-Path $candidateConfig 'config') -Encoding utf8
    $probeIdentity = Join-Path $Directory 'probe.identity'
    $noProofIdentity = Join-Path $Directory 'no-proof.identity'
    [IO.File]::WriteAllBytes($probeIdentity, [byte[]](0..63))
    [IO.File]::WriteAllBytes($noProofIdentity, [byte[]](64..127))
    $info = [Diagnostics.ProcessStartInfo]::new($Context.Python)
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @('-u', (Join-Path $PSScriptRoot '..\..\Interop\Parity7NetworkUtilitiesReference.py'),
            '--revision', $Context.Revision, '--config', $peerConfig, '--probe-identity', $probeIdentity,
            '--no-proof-identity', $noProofIdentity)) { $info.ArgumentList.Add($argument) }
    foreach ($entry in $Context.PeerEnvironment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    $peer = [Diagnostics.Process]::Start($info)
    $errorTask = $peer.StandardError.ReadToEndAsync()
    $candidate = $null
    $outputTask = $null
    try {
        $line = $peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Upstream peer exited without readiness.' }
        $ready = $line | ConvertFrom-Json
        if ($ready.event -ne 'ready') { throw "Upstream peer was not ready: $line" }
        $outputTask = $peer.StandardOutput.ReadToEndAsync()
        $destination = if ($WithoutProof) { $ready.no_proof } else { $ready.probe }
        if ($destination -notmatch '^[0-9a-f]{32}$') { throw 'Upstream peer supplied an invalid destination.' }
        $timeout = if ($WithoutProof) { '1' } else { '8' }
        $candidate = Start-TestProcess $Context.Executable ($Context.Prefix + @('rnprobe') + $Context.ToolSeparator +
            @('--config', $candidateConfig, '--timeout', $timeout, 'app.aspect', $destination)) -Environment $Context.Environment
        Start-Sleep -Milliseconds 300
        $peer.StandardInput.WriteLine('announce')
        $peer.StandardInput.Flush()
        $running = $candidate
        $candidate = $null
        $result = Complete-TestProcess $running 30
        $peer.StandardInput.WriteLine('quit')
        $peer.StandardInput.Close()
        if (!$peer.WaitForExit(10000)) { throw 'Upstream peer did not shut down.' }
        $observations = @($outputTask.GetAwaiter().GetResult() -split '\r?\n' |
            Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        [pscustomobject]@{
            Candidate = $result
            Destination = $destination
            Observations = $observations
            PeerExitCode = $peer.ExitCode
            PeerError = $errorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($candidate) { Stop-TestProcess $candidate }
        if (!$peer.HasExited) { $peer.Kill($true); $peer.WaitForExit() }
        $errorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-stderr.txt') -Encoding utf8
        if ($outputTask) { $outputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-events.jsonl') -Encoding utf8 }
        $peer.Dispose()
    }
}
