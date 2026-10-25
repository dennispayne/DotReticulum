param([string] $Application)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
}

Describe 'Parity 6 native interface processes' -Tag E2E, Interfaces {
    It 'rejects Java topology without public-network permission before launching Java' {
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $runner = (Resolve-Path (Join-Path $PSScriptRoot '..\Interop\invoke-i2p-topology.ps1')).Path
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner,
            '-JavaExecutable', $pwsh, '-JavaRouterDirectory', $TestDrive) -Environment @{
                DOTRETICULUM_PYTHON = $null; DOTRETICULUM_UPSTREAM = $null
            }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'Java router requires explicit -AllowPublicNetwork'
    }

    It 'rejects incomplete Java distributions before launching Java' -ForEach @(
        @{ Missing = 'hosts.txt' }
        @{ Missing = 'lib\i2p.jar' }
        @{ Missing = 'lib\router.jar' }
        @{ Missing = 'lib\sam.jar' }
        @{ Missing = 'lib\mstreaming.jar' }
        @{ Missing = 'lib\streaming.jar' }
    ) {
        $distribution = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path (Join-Path $distribution 'lib')
        foreach ($asset in @('hosts.txt', 'lib\i2p.jar', 'lib\router.jar', 'lib\sam.jar', 'lib\mstreaming.jar', 'lib\streaming.jar')) {
            if ($asset -ne $Missing) {
                Set-Content -LiteralPath (Join-Path $distribution $asset) -Value 'validation fixture only'
            }
        }
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $runner = (Resolve-Path (Join-Path $PSScriptRoot '..\Interop\invoke-i2p-topology.ps1')).Path
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner,
            '-JavaExecutable', $pwsh, '-JavaRouterDirectory', $distribution,
            '-AllowPublicNetwork') -Environment @{
                DOTRETICULUM_PYTHON = $null; DOTRETICULUM_UPSTREAM = $null
            }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'not a complete extracted distribution: missing'
    }

    It 'rejects mixing the Java and i2pd router parameter sets' {
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $runner = (Resolve-Path (Join-Path $PSScriptRoot '..\Interop\invoke-i2p-topology.ps1')).Path
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner,
            '-RouterBinary', $pwsh, '-JavaExecutable', $pwsh,
            '-JavaRouterDirectory', $TestDrive, '-AllowPublicNetwork')
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'Parameter set cannot be resolved'
    }

    It 'rejects public RouterInfo seeding without explicit network permission' {
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $runner = (Resolve-Path (Join-Path $PSScriptRoot '..\Interop\invoke-i2p-topology.ps1')).Path
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner,
            '-RouterBinary', $pwsh, '-SeedRouterInfoDirectory', $TestDrive) -Environment @{
                DOTRETICULUM_PYTHON = $null; DOTRETICULUM_UPSTREAM = $null
            }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'requires explicit -AllowPublicNetwork'
    }

    It 'rejects a seed directory without public RouterInfos before starting a router' {
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $runner = (Resolve-Path (Join-Path $PSScriptRoot '..\Interop\invoke-i2p-topology.ps1')).Path
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner,
            '-RouterBinary', $pwsh, '-AllowPublicNetwork', '-CertificatesDirectory', $TestDrive,
            '-SeedRouterInfoDirectory', $TestDrive) -Environment @{
                DOTRETICULUM_PYTHON = $null; DOTRETICULUM_UPSTREAM = $null
            }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'must contain 1\.\.10000'
    }

    It 'transfers a file over independently configured UDP receive and forwarding ports' {
        $first = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $second = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        try {
            $serverPort = $first.Client.LocalEndPoint.Port
            $clientPort = $second.Client.LocalEndPoint.Port
        }
        finally { $first.Dispose(); $second.Dispose() }
        $server = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'udp-server')
        $client = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'udp-client')
        $received = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'udp-received')
        $template = @'
[reticulum]
share_instance=no
[interfaces]
[[UDP]]
type=UDPInterface
enabled=yes
listen_ip=127.0.0.1
listen_port={0}
forward_ip=127.0.0.1
forward_port={1}
network_name=parity6-owned-udp
pass_phrase=parity6-owned-test-key
'@
        ($template -f $serverPort, $clientPort) | Set-Content -LiteralPath (Join-Path $server.FullName 'config')
        ($template -f $clientPort, $serverPort) | Set-Content -LiteralPath (Join-Path $client.FullName 'config')
        $info = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName, '-p'))
        $info.Code | Should -Be 0 -Because $info.Error
        $destination = [regex]::Match($info.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $destination | Should -Not -BeNullOrEmpty
        $listener = Start-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName,
            '-l', '-n', '-s', $received.FullName))
        try {
            $file = Join-Path $TestDrive 'udp-transfer.bin'
            [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes('bounded UDP process transfer ' * 1001))
            $sent = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $client.FullName,
                '-w', '30', $file, $destination)) -TimeoutSeconds 45
            $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            $saved = Join-Path $received.FullName 'udp-transfer.bin'
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath $saved)) {
                if ($listener.Process.HasExited) { throw ($listener.Output.GetAwaiter().GetResult() + $listener.Error.GetAwaiter().GetResult()) }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'UDP receiver did not persist the transferred file.' }
                Start-Sleep -Milliseconds 25
            }
            (Get-FileHash -LiteralPath $saved).Hash | Should -Be (Get-FileHash -LiteralPath $file).Hash
            $listener.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $listener }
    }

    It 'starts the configured Pipe child and joins it during graceful daemon shutdown' {
        $config = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'pipe-daemon')
        $pidFile = Join-Path $config.FullName 'owned-child.pid'
        $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $fixture = (Resolve-Path (Join-Path $PSScriptRoot '..\DotReticulum.Interfaces.Tests\Parity6PipeChild.ps1')).Path
        @"
[reticulum]
share_instance=no
[interfaces]
[[Owned Pipe]]
type=PipeInterface
enabled=yes
command='"$pwsh" -NoProfile -File "$fixture" -PidFile "$pidFile"'
respawn_delay=0.25
"@ | Set-Content -LiteralPath (Join-Path $config.FullName 'config')
        $daemon = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $config.FullName, '--interactive'))
        $child = $null
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            $childId = 0
            while (!(Test-Path -LiteralPath $pidFile) -or
                ![int]::TryParse([IO.File]::ReadAllText($pidFile), [ref] $childId)) {
                if ($daemon.Process.HasExited) { throw ($daemon.Output.GetAwaiter().GetResult() + $daemon.Error.GetAwaiter().GetResult()) }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'The configured Pipe child did not start.' }
                Start-Sleep -Milliseconds 25
            }
            $child = [Diagnostics.Process]::GetProcessById($childId)
            $child.StartTime | Should -BeGreaterOrEqual $daemon.Process.StartTime
            $daemon.Process.StandardInput.WriteLine('interfaces')
            $daemon.Process.StandardInput.WriteLine('exit')
            $daemon.Process.StandardInput.Close()
            $daemon.Process.WaitForExit(15000) | Should -BeTrue
            $daemon.Process.ExitCode | Should -Be 0 -Because $daemon.Error.GetAwaiter().GetResult()
            $daemon.Output.GetAwaiter().GetResult() | Should -Match 'Owned Pipe'
            $child.WaitForExit(5000) | Should -BeTrue -Because 'Normal daemon shutdown must dispose its actual child, not leave it running.'
            Test-Path -LiteralPath (Join-Path $config.FullName 'storage\runtime-state-v1.msgpack') | Should -BeTrue
        }
        finally {
            if ($null -ne $child) { $child.Dispose() }
            Stop-TestProcess $daemon
        }
    }
}
