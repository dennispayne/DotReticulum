param([string] $Application)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    function Invoke-Parity4Cli {
        param([string[]] $Arguments)
        Invoke-TestProcess $executable ($prefix + $Arguments) -TimeoutSeconds 60
    }
}

Describe 'Parity 4 authenticated interface processes' -Tag E2E, Transport {
    It 'rejects wrong IFAC credentials then preserves service through malformed and disconnected peers' -Tag 'Contract:IFACE-IFAC-001' {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $port = $probe.LocalEndpoint.Port
        $probe.Stop()
        $server = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'ifac-server')
        $client = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'ifac-client')
        $received = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'ifac-received')
        @"
[reticulum]
  share_instance = no
[interfaces]
  [[Authenticated]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = $port
    networkname = parity4-process
    passphrase = owned-test-network
    ifac_size = 128
    bitrate = 64000
"@ | Set-Content -LiteralPath (Join-Path $server.FullName 'config')
        $clientTemplate = @"
[reticulum]
  share_instance = no
[interfaces]
  [[Authenticated]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = $port
    network_name = parity4-process
    pass_phrase = {0}
    ifac_size = 128
    bitrate = 64000
"@
        $clientFile = Join-Path $client.FullName 'config'
        ($clientTemplate -f 'wrong-test-network') | Set-Content -LiteralPath $clientFile
        $serverInfo = Invoke-Parity4Cli @('rncp', '--config', $server.FullName, '-p')
        $serverInfo.Code | Should -Be 0 -Because $serverInfo.Error
        $destination = [regex]::Match($serverInfo.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $destination | Should -Not -BeNullOrEmpty
        $clientInfo = Invoke-Parity4Cli @('rncp', '--config', $client.FullName, '-p')
        $clientInfo.Code | Should -Be 0 -Because $clientInfo.Error
        $identity = [regex]::Match($clientInfo.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $identity | Should -Not -BeNullOrEmpty
        $listener = Start-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName,
            '-l', '-a', $identity, '-s', $received.FullName, '-v'))
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                if ($listener.Process.HasExited) { throw $listener.Error.GetAwaiter().GetResult() }
                $noise = [Net.Sockets.TcpClient]::new()
                try {
                    $noise.Connect([Net.IPAddress]::Loopback, $port)
                    $noise.GetStream().Write([byte[]]@(0x7e, 0x08, 0x00, 0x01, 0x7e))
                    $ready = $true
                }
                catch [Net.Sockets.SocketException] { $ready = $false; Start-Sleep -Milliseconds 50 }
                finally { $noise.Dispose() }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Authenticated listener did not become reachable.' }
            } until ($ready)
            $file = Join-Path $TestDrive 'ifac-transfer.bin'
            [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes('authenticated bounded interface flow ' * 1001))
            $wrong = Invoke-Parity4Cli @('rncp', '--config', $client.FullName, '-w', '2', $file, $destination)
            $wrong.Code | Should -Not -Be 0 -Because 'Wrong network credentials must not discover or establish an authenticated link.'
            Test-Path -LiteralPath (Join-Path $received.FullName 'ifac-transfer.bin') | Should -BeFalse
            ($clientTemplate -f 'owned-test-network') | Set-Content -LiteralPath $clientFile
            $sent = Invoke-Parity4Cli @('rncp', '--config', $client.FullName, '-w', '30', $file, $destination)
            $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            $saved = Join-Path $received.FullName 'ifac-transfer.bin'
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath $saved)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Authenticated resource was not persisted.' }
                Start-Sleep -Milliseconds 25
            }
            (Get-FileHash -LiteralPath $saved).Hash | Should -Be (Get-FileHash -LiteralPath $file).Hash
            $listener.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $listener }
    }
}
