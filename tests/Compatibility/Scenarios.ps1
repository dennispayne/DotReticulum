function Assert-Claim([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Invoke-ClaimScenario {
    param(
        [string] $Scenario, [string] $Executable, [string[]] $Prefix, [string[]] $ToolSeparator, [hashtable] $Environment,
        [string] $Directory, [string] $Python, [hashtable] $PeerEnvironment, [string] $Revision
    )

    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
    $inputPath = Join-Path $Directory $(if ($Scenario -eq 'identity-decryption') { 'input.rfe' } else { 'input' })
    $outputPath = Join-Path $Directory 'output'

    if ($Scenario -in 'delivery-proof', 'missing-delivery-proof') {
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
        $info = [Diagnostics.ProcessStartInfo]::new($Python)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in @('-u', (Join-Path $PSScriptRoot '..\Interop\Parity7NetworkUtilitiesReference.py'),
                '--revision', $Revision, '--config', $peerConfig, '--probe-identity', $probeIdentity,
                '--no-proof-identity', $noProofIdentity)) {
            $info.ArgumentList.Add($argument)
        }
        foreach ($entry in $PeerEnvironment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
        $peer = [Diagnostics.Process]::Start($info)
        $errorTask = $peer.StandardError.ReadToEndAsync()
        $candidate = $null
        $outputTask = $null
        try {
            $line = $peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            Assert-Claim ($null -ne $line) 'Upstream peer exited without readiness.'
            $ready = $line | ConvertFrom-Json
            Assert-Claim ($ready.event -eq 'ready') "Upstream peer was not ready: $line"
            $outputTask = $peer.StandardOutput.ReadToEndAsync()
            $destination = if ($Scenario -eq 'delivery-proof') { $ready.probe } else { $ready.no_proof }
            Assert-Claim ($destination -match '^[0-9a-f]{32}$') 'Upstream peer supplied an invalid destination.'
            $timeout = if ($Scenario -eq 'delivery-proof') { '8' } else { '1' }
            $candidate = Start-TestProcess $Executable ($Prefix + @('rnprobe') + $ToolSeparator + @('--config', $candidateConfig,
                '--timeout', $timeout, 'app.aspect', $destination)) -Environment $Environment
            Start-Sleep -Milliseconds 300
            $peer.StandardInput.WriteLine('announce')
            $peer.StandardInput.Flush()
            $running = $candidate
            $candidate = $null
            $result = Complete-TestProcess $running 30
            if ($Scenario -eq 'delivery-proof') {
                Assert-Claim ($result.Code -eq 0 -and $result.Output -match 'Valid reply from' -and
                    $result.Output -match 'packet loss 0(?:\.0+)?%') "Proof not confirmed: exit $($result.Code); $($result.Output) $($result.Error)"
            }
            else {
                Assert-Claim ($result.Code -eq 2 -and $result.Output -match 'Probe timed out' -and
                    $result.Output -match 'packet loss 100(?:\.0+)?%') "Missing proof was not reported: exit $($result.Code); $($result.Output) $($result.Error)"
            }
            $peer.StandardInput.WriteLine('quit')
            $peer.StandardInput.Close()
            Assert-Claim ($peer.WaitForExit(10000)) 'Upstream peer did not shut down.'
            $observations = @($outputTask.GetAwaiter().GetResult() -split '\r?\n' |
                Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
            Assert-Claim (@($observations | Where-Object {
                $_.event -eq 'probe_received' -and $_.destination -eq $destination -and $_.payload_bytes -eq 16
            }).Count -gt 0) 'Upstream peer did not observe the expected 16-byte probe.'
            Assert-Claim ($peer.ExitCode -eq 0 -and $errorTask.GetAwaiter().GetResult() -eq '') 'Upstream peer failed.'
        }
        finally {
            if ($candidate -and !$candidate.Process.HasExited) { Stop-TestProcess $candidate }
            if (!$peer.HasExited) { $peer.Kill($true); $peer.WaitForExit() }
            $errorText = $errorTask.GetAwaiter().GetResult()
            $errorText | Set-Content -LiteralPath (Join-Path $Directory 'peer-stderr.txt') -Encoding utf8
            if ($outputTask) { $outputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'peer-events.jsonl') -Encoding utf8 }
            $peer.Dispose()
        }
        return
    }

    $arguments = switch ($Scenario) {
        'identity-import' { @('-M', $key, '-X', '-w', $outputPath) }
        'detached-signature' {
            [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
            @('-M', $key, '-s', $inputPath)
        }
        'identity-decryption' {
            [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.encrypted))
            @('-M', $key, '-d', $inputPath, '-w', $outputPath)
        }
        'destination-hash' { @('-M', $key, '-H', 'rns.id') }
        default { throw "Unknown shared claim scenario: $Scenario" }
    }
    $result = Invoke-TestProcess $Executable ($Prefix + @('rnid') + $ToolSeparator + $arguments) -Environment $Environment
    Assert-Claim ($result.Code -eq 0) "CLI exited $($result.Code): $($result.Error) $($result.Output)"
    $matches = switch ($Scenario) {
        'identity-import' { (Test-Path -LiteralPath $outputPath) -and [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() -eq $key }
        'detached-signature' { (Test-Path -LiteralPath "$inputPath.rsg") -and [Convert]::ToHexString([IO.File]::ReadAllBytes("$inputPath.rsg")).ToLowerInvariant() -eq $fixture.rsg }
        'identity-decryption' { (Test-Path -LiteralPath $outputPath) -and [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() -eq $fixture.message }
        'destination-hash' { $result.Output.Contains($fixture.destination, [StringComparison]::OrdinalIgnoreCase) }
    }
    Assert-Claim $matches "Output does not meet the shared $Scenario expectation."
}
