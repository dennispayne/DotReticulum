param([string] $Application)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([System.IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    function Invoke-Cli {
        param([string[]] $Arguments)
        Invoke-TestProcess $executable ($prefix + $Arguments)
    }
}

Describe 'DotReticulum CLI end-to-end' -Tag E2E {
    It 'advertises the complete native Git utility surface in root help' -Tag 'Contract:UTIL-CLI-001' {
        $result = Invoke-Cli @('--help')
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match 'rngit.*Repository node'
        $result.Output | Should -Match 'git-remote-rns.*read/write'
        $result.Output | Should -Match 'rngcs.*signing and verification'
        $result.Output | Should -Not -Match 'Read-only bare Git clone'
    }

    It 'advertises and dispatches the upstream utility commands' -Tag 'Contract:UTIL-CLI-001' {
        $rootHelp = Invoke-Cli @('--help')
        $rootHelp.Code | Should -Be 0 -Because $rootHelp.Error
        foreach ($tool in 'rnid', 'rnstatus', 'rnpath', 'rnprobe', 'rnsh', 'rnodeconf') {
            $rootHelp.Output | Should -Match "\b$tool\b"
            $help = Invoke-Cli @($tool, '--help')
            $help.Code | Should -Be 0 -Because $help.Error
            $help.Output | Should -Match "\b$tool\b"
        }
    }

    It 'persists identity generation, preserves it on overwrite refusal and reloads the same public identity' -Tag 'Contract:UTIL-RNID-001' {
        $path = Join-Path $TestDrive 'private identity'
        $created = Invoke-Cli @('rnid', '--generate', $path)
        $created.Code | Should -Be 0 -Because $created.Error
        $created.Output | Should -Match 'Identity hash: [0-9a-f]{32}'
        $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $refused = Invoke-Cli @('rnid', '--generate', $path)
        $refused.Code | Should -Be 1
        $refused.Error | Should -Not -BeNullOrEmpty
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash | Should -Be $before
        $loaded = Invoke-Cli @('rnid', '--show', $path)
        $loaded.Code | Should -Be 0 -Because $loaded.Error
        $loaded.Output | Should -Be $created.Output
    }

    It 'dispatches a renamed utility executable without a repeated subcommand' -Tag 'Contract:UTIL-CLI-001' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'identity-alias')
        $helper = Install-TestGitHelper $cli $directory.FullName
        $alias = Join-Path $directory.FullName $(if ($IsWindows) { 'rnid.exe' } else { 'rnid' })
        Move-Item -LiteralPath $helper -Destination $alias
        $identity = Join-Path $TestDrive 'alias-identity'
        $created = Invoke-TestProcess $alias @('--generate', $identity)
        $created.Code | Should -Be 0 -Because $created.Error
        $loaded = Invoke-Cli @('rnid', '--show', $identity)
        $loaded.Code | Should -Be 0 -Because $loaded.Error
        $loaded.Output | Should -Be $created.Output
    }

    It 'reports invalid rngit limits without creating a target repository' -Tag 'Contract:UTIL-RNGIT-003' {
        $target = Join-Path $TestDrive 'invalid.git'
        $result = Invoke-Cli @('rngit', 'clone', 'rns://0123456789abcdef0123456789abcdef/group/repo', $target, '--timeout', '0')
        $result.Code | Should -Be 2
        $result.Error | Should -Match 'must be positive'
        Test-Path -LiteralPath $target | Should -BeFalse
    }

    It 'sends and fetches a multi-megabyte file between separately launched authenticated rncp processes' -Tag 'Contract:UTIL-RNCP-002', 'Contract:FUNC-CORE-RESOURCES' {
        $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $portProbe.Start()
        $port = $portProbe.LocalEndpoint.Port
        $portProbe.Stop()
        $server = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'server')
        $client = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'client')
        $received = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'received')
        $fetched = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'fetched')
        @"
[reticulum]
  share_instance = no
  enable_transport = no
[interfaces]
  [[Loopback]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $server.FullName 'config')
        @"
[reticulum]
  share_instance = no
  enable_transport = no
[interfaces]
  [[Loopback]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $client.FullName 'config')
        $clientInfo = Invoke-Cli @('rncp', '--config', $client.FullName, '-p')
        $clientInfo.Code | Should -Be 0 -Because $clientInfo.Error
        $clientInfo.Output | Should -Match 'Identity\s*:\s*<([0-9a-f]{32})>'
        $identity = [regex]::Match($clientInfo.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $serverInfo = Invoke-Cli @('rncp', '--config', $server.FullName, '-p')
        $serverInfo.Code | Should -Be 0 -Because $serverInfo.Error
        $serverInfo.Output | Should -Match 'Listening on\s*:\s*<([0-9a-f]{32})>'
        $destination = [regex]::Match($serverInfo.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $listener = Start-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName,
            '-l', '-a', $identity, '-s', $received.FullName, '-F', '-j', $received.FullName, '-b', '1', '-v'))
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                if ($listener.Process.HasExited) { throw "Listener exited: $($listener.Error.GetAwaiter().GetResult())" }
                $probe = [System.Net.Sockets.TcpClient]::new()
                try { $probe.Connect([System.Net.IPAddress]::Loopback, $port); $ready = $true }
                catch [System.Net.Sockets.SocketException] { $ready = $false; Start-Sleep -Milliseconds 50 }
                finally { $probe.Dispose() }
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Listener did not become reachable.' }
            } until ($ready)
            $path = Join-Path $TestDrive 'round trip.bin'
            [System.IO.File]::WriteAllText($path, ('reticulum powershell e2e ' * 100001))
            (Get-Item -LiteralPath $path).Length | Should -BeGreaterThan 2000000
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            $sent = Invoke-Cli @('rncp', '--config', $client.FullName, '-w', '30', $path, $destination)
            $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            $saved = Join-Path $received.FullName 'round trip.bin'
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath $saved)) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Listener did not save the transferred file.' }
                Start-Sleep -Milliseconds 50
            }
            (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash | Should -Be $hash
            $result = Invoke-Cli @('rncp', '--config', $client.FullName, '-w', '30', '-f', 'round trip.bin',
                $destination, '-s', $fetched.FullName)
            $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
            (Get-FileHash -LiteralPath (Join-Path $fetched.FullName 'round trip.bin') -Algorithm SHA256).Hash | Should -Be $hash
        }
        finally { Stop-TestProcess $listener }
    }
}
