function Invoke-CompatibilityChannelStreams {
    param([hashtable] $Context, [string] $Directory, [string] $InputText, [string] $OutputText)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $peerConfig = New-CompatibilityTransferConfig (Join-Path $Directory 'reference') $port -Server
    $candidateConfig = New-CompatibilityTransferConfig (Join-Path $Directory 'candidate') $port
    $shellConfig = (New-Item -ItemType Directory -Path (Join-Path $Directory 'shell')).FullName
    $sent = Join-Path $Directory 'reference-send.txt'
    $received = Join-Path $Directory 'reference-received.txt'
    [IO.File]::WriteAllText($sent, $OutputText, [Text.UTF8Encoding]::new($false))
    $info = [Diagnostics.ProcessStartInfo]::new($Context.Python)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($key in 'GITHUB_STEP_SUMMARY', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_PATH', 'GITHUB_STATE') {
        $null = $info.Environment.Remove($key)
    }
    foreach ($argument in @('-u', (Join-Path $PSScriptRoot '..\..\Interop\CompatibilityChannelReference.py'),
            '--revision', $Context.Revision, '--config', $peerConfig, '--send', $sent, '--received', $received)) {
        $info.ArgumentList.Add($argument)
    }
    foreach ($entry in $Context.PeerEnvironment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    $peer = [Diagnostics.Process]::Start($info)
    $errorTask = $peer.StandardError.ReadToEndAsync()
    $outputTask = $null
    $candidate = $null
    try {
        $line = $peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        if (!$line) {
            throw "Channel peer exited before readiness: $($errorTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult())"
        }
        $ready = $line | ConvertFrom-Json
        if ($ready.event -ne 'ready' -or $ready.destination -notmatch '^[0-9a-f]{32}$') {
            throw "Invalid Channel peer readiness: $line"
        }
        $outputTask = $peer.StandardOutput.ReadToEndAsync()
        $candidate = Start-TestProcess $Context.Executable ($Context.Prefix + @('rnsh') + $Context.ToolSeparator +
            @('--rnsconfig', $candidateConfig, '-c', $shellConfig, '-q', '-m',
                '-w', '30', $ready.destination, '--', 'channel-fixture-no-execution')) -Environment $Context.Environment
        $candidate.Process.StandardInput.WriteAsync($InputText).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        $candidate.Process.StandardInput.Close()
        $running = $candidate
        $candidate = $null
        $result = Complete-TestProcess $running 100
        [IO.File]::WriteAllText((Join-Path $Directory 'candidate-received.txt'), $result.Output, [Text.UTF8Encoding]::new($false))
        $result.Error | Set-Content -LiteralPath (Join-Path $Directory 'candidate-stderr.txt') -Encoding utf8
        if (!$peer.WaitForExit(15000)) { throw "Channel peer did not shut down: $($result.Error)" }
        $events = @($outputTask.GetAwaiter().GetResult() -split '\r?\n' |
            Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        [pscustomobject]@{
            Candidate = $result
            ReceivedFile = $received
            CandidateFile = Join-Path $Directory 'candidate-received.txt'
            Events = $events
            PeerExitCode = $peer.ExitCode
            PeerError = $errorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($candidate) { Stop-TestProcess $candidate }
        if (!$peer.HasExited) { $peer.Kill($true); $peer.WaitForExit() }
        $errorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-stderr.txt') -Encoding utf8
        if ($outputTask) {
            $outputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-events.jsonl') -Encoding utf8
        }
        $peer.Dispose()
    }
}
