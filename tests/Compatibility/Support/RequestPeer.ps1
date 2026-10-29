function Invoke-CompatibilityLargeRequest {
    param([hashtable] $Context, [string] $Directory, [string] $InputText)
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start()
    try { $port = $probe.LocalEndpoint.Port } finally { $probe.Stop() }
    $server = New-CompatibilityTransferConfig (Join-Path $Directory 'reference') $port -Server
    $client = New-CompatibilityTransferConfig (Join-Path $Directory 'candidate') $port
    $candidateEndpoint = @{
        Executable = $Context.Executable
        Prefix = $Context.Prefix + @('rnx') + $Context.ToolSeparator
        Environment = $Context.Environment
    }
    $referenceEndpoint = @{
        Executable = $Context.Python
        Prefix = @((Join-Path $PSScriptRoot '..\..\Interop\reference_cli.py'),
            '--revision', $Context.Revision, '--tool', 'rnx', '--')
        Environment = $Context.PeerEnvironment
    }
    $candidateId = Get-CompatibilityTransferIdentity $candidateEndpoint $client $Directory 'candidate-identity'
    $referenceId = Get-CompatibilityTransferIdentity $referenceEndpoint $server $Directory 'reference-identity'
    $persisted = Join-Path $Directory 'received.bin'
    $quoted = $persisted.Replace("'", "''")
    $script = "`$memory=[IO.MemoryStream]::new();[Console]::OpenStandardInput().CopyTo(`$memory);" +
        "`$bytes=`$memory.ToArray();[IO.File]::WriteAllBytes('$quoted',`$bytes);" +
        "[Console]::OpenStandardOutput().Write(`$bytes,0,`$bytes.Length);`$memory.Dispose()"
    $command = 'pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand ' +
        [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $peer = Start-TestProcess $Context.Python @('-u',
        (Join-Path $PSScriptRoot '..\..\Interop\CompatibilityRequestReference.py'),
        '--revision', $Context.Revision, '--config', $server, '--allowed', $candidateId.Identity) `
        -Environment $Context.PeerEnvironment
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ($true) {
            if ($peer.Process.HasExited) { throw "Upstream request peer exited: $($peer.Error.GetAwaiter().GetResult())" }
            $socket = [Net.Sockets.TcpClient]::new()
            try { $socket.Connect('127.0.0.1', $port); break }
            catch [Net.Sockets.SocketException] {
                if ([DateTime]::UtcNow -ge $deadline) { throw "Upstream request peer did not open its interface: $($_.Exception.Message)" }
                Start-Sleep -Milliseconds 50
            }
            finally { $socket.Dispose() }
        }
        $candidate = Invoke-TestProcess $candidateEndpoint.Executable ($candidateEndpoint.Prefix +
            @('--config', $client, '-q', '-q', '-q', '-w', '30', '-m',
                '--stdin', $InputText, $referenceId.Destination, $command)) `
            -Environment $candidateEndpoint.Environment -TimeoutSeconds 90
        [IO.File]::WriteAllText((Join-Path $Directory 'candidate-stdout.txt'), $candidate.Output)
        $candidate.Error | Set-Content -LiteralPath (Join-Path $Directory 'candidate-stderr.txt') -Encoding utf8
    }
    finally {
        Stop-TestProcess $peer
        $peer.Output.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-stdout.txt') -Encoding utf8
        $peer.Error.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-stderr.txt') -Encoding utf8
    }
    $observations = @($peer.Output.GetAwaiter().GetResult() -split '\r?\n' |
        Where-Object { $_.StartsWith('@@') } | ForEach-Object { $_.Substring(2) | ConvertFrom-Json })
    @{
        Candidate = $candidate
        Identity = $candidateId.Identity
        PersistedFile = $persisted
        Observations = $observations
        PeerError = $peer.Error.GetAwaiter().GetResult()
    }
}
