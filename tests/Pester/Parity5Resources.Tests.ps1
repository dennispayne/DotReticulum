param([string] $Application)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
}

Describe 'Parity 5 config-owned streamed resources' -Tag E2E, Transport {
    It 'spools multipart files in each receiver configuration and scavenges only its expired owned cache' {
        $working = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'working')
        $source = Join-Path $TestDrive 'multipart.bin'
        $output = [IO.File]::Create($source)
        try {
            $chunk = [byte[]]::new(1024 * 1024)
            for ($part = 0; $part -lt 70; $part++) { $output.Write($chunk) }
            $output.Write([byte[]]::new(37))
        }
        finally { $output.Dispose() }
        $hash = (Get-FileHash -LiteralPath $source).Hash
        $unrelatedDirectory = New-Item -ItemType Directory -Path (Join-Path $working.FullName '.reticulum\resources')
        $unrelated = Join-Path $unrelatedDirectory.FullName ('resource-' + [guid]::NewGuid().ToString('N') + '.bin')
        [IO.File]::WriteAllText($unrelated, 'unowned cwd sentinel')
        [IO.File]::SetLastWriteTimeUtc($unrelated, [DateTime]::UtcNow.AddDays(-2))
        foreach ($role in 'first', 'second') {
            $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $probe.Start()
            $port = $probe.LocalEndpoint.Port
            $probe.Stop()
            $server = New-Item -ItemType Directory -Path (Join-Path $TestDrive "$role-server")
            $client = New-Item -ItemType Directory -Path (Join-Path $TestDrive "$role-client")
            $received = New-Item -ItemType Directory -Path (Join-Path $TestDrive "$role-received")
            @"
[reticulum]
share_instance = no
[interfaces]
[[Owned]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $server.FullName 'config')
            @"
[reticulum]
share_instance = no
[interfaces]
[[Peer]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $client.FullName 'config')
            $spool = New-Item -ItemType Directory -Path (Join-Path $server.FullName 'storage\resources')
            $expired = Join-Path $spool.FullName ('resource-' + [guid]::NewGuid().ToString('N') + '.bin')
            $fresh = Join-Path $spool.FullName ('input-' + [guid]::NewGuid().ToString('N') + '.bin')
            $kept = Join-Path $spool.FullName 'unrelated.txt'
            [IO.File]::WriteAllText($expired, 'expired owned transfer')
            [IO.File]::WriteAllText($fresh, 'fresh owned transfer')
            [IO.File]::WriteAllText($kept, 'unrelated')
            [IO.File]::SetLastWriteTimeUtc($expired, [DateTime]::UtcNow.AddDays(-2))
            [IO.File]::SetLastWriteTimeUtc($kept, [DateTime]::UtcNow.AddDays(-2))
            $printed = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName, '-p')) -WorkingDirectory $working.FullName
            $printed.Code | Should -Be 0 -Because ($printed.Output + $printed.Error)
            $destination = [regex]::Match($printed.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
            $destination | Should -Not -BeNullOrEmpty
            $listener = Start-TestProcess $executable ($prefix + @('rncp', '--config', $server.FullName,
                '-l', '-n', '-s', $received.FullName, '-b', '1', '-v')) -WorkingDirectory $working.FullName
            $watcher = [IO.FileSystemWatcher]::new($spool.FullName, 'resource-*.bin')
            $sender = $null
            try {
                $deadline = [DateTime]::UtcNow.AddSeconds(20)
                while ((Test-Path -LiteralPath $expired) -or
                    !(Test-Path -LiteralPath (Join-Path $spool.FullName '.dotreticulum-resource-storage'))) {
                    if ($listener.Process.HasExited) { throw $listener.Error.GetAwaiter().GetResult() }
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Receiver did not initialize/scavenge its owned resource cache.' }
                    Start-Sleep -Milliseconds 25
                }
                Test-Path -LiteralPath $fresh | Should -BeTrue
                Test-Path -LiteralPath $kept | Should -BeTrue
                $watcher.EnableRaisingEvents = $true
                $sender = Start-TestProcess $executable ($prefix + @('rncp', '--config', $client.FullName,
                    '-w', '30', $source, $destination)) -WorkingDirectory $working.FullName
                $created = $watcher.WaitForChanged([IO.WatcherChangeTypes]::Created, 60000)
                $created.TimedOut | Should -BeFalse -Because 'The 70-MiB receive must spill above the default 64-MiB threshold.'
                $created.Name | Should -Match '^resource-[0-9a-f]{32}\.bin$'
                $runningSender = $sender
                $sender = $null
                $sent = Complete-TestProcess $runningSender -TimeoutSeconds 90
                $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
                $saved = Join-Path $received.FullName 'multipart.bin'
                $deadline = [DateTime]::UtcNow.AddSeconds(20)
                while (!(Test-Path -LiteralPath $saved) -or (Get-Item -LiteralPath $saved).Length -ne 73400357 -or
                    @(Get-ChildItem -LiteralPath $spool.FullName -Filter 'resource-*.bin').Count -ne 0) {
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Streamed multipart output was not fully persisted.' }
                    Start-Sleep -Milliseconds 25
                }
                (Get-FileHash -LiteralPath $saved).Hash | Should -Be $hash
                @(Get-ChildItem -LiteralPath $spool.FullName -Filter 'resource-*.bin').Count | Should -Be 0
                [IO.File]::ReadAllText($unrelated) | Should -Be 'unowned cwd sentinel'
                $listener.Process.HasExited | Should -BeFalse
            }
            finally {
                if ($sender) { Stop-TestProcess $sender }
                $watcher.Dispose()
                Stop-TestProcess $listener
            }
        }
    }
}
