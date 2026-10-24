param([string] $Application)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    $workspace = Join-Path (Join-Path $PSScriptRoot '..\..\TestResults') ('rnsd-config-process-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $workspace
    function Invoke-Rnsd {
        param([string[]] $Arguments)
        Invoke-TestProcess $executable ($prefix + @('rnsd') + $Arguments)
    }
    function Wait-RnsdLog {
        param([hashtable] $Peer, [string] $Path, [string] $Pattern)
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($Peer.Process.HasExited) { throw "rnsd exited: $($Peer.Error.GetAwaiter().GetResult())" }
            if ((Test-Path -LiteralPath $Path) -and ([IO.File]::ReadAllText($Path) -match $Pattern)) { return }
            if ([DateTime]::UtcNow -ge $deadline) { throw "rnsd did not log '$Pattern' to '$Path'." }
            Start-Sleep -Milliseconds 50
        } while ($true)
    }
}

AfterAll {
    if ($workspace -and (Test-Path -LiteralPath $workspace)) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}

Describe 'Parity 3 daemon configuration and logging processes' -Tag E2E, Runtime {
    It 'reports native and pinned reference versions without starting a daemon' {
        $result = Invoke-Rnsd @('--version')
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match 'DotReticulum'
        $result.Output | Should -Match 'reference RNS 1\.5\.7'
    }

    It 'runs a bounded native interactive management shell and persists identity and runtime state across restart' {
        $config = New-Item -ItemType Directory -Path (Join-Path $workspace 'interactive')
        "[reticulum]`nshare_instance=no`n[logging]`nloglevel=4`n[interfaces]`n" |
            Set-Content -LiteralPath (Join-Path $config.FullName 'config')
        $arguments = $prefix + @('rnsd', '--config', $config.FullName, '--interactive')
        $result = Invoke-TestProcess $executable $arguments -InputText "help`nstatus`ninterfaces`npersist`nquit`n"
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match 'replacement for the upstream Python REPL'
        $result.Output | Should -Match 'Status:'
        $result.Output | Should -Match 'Interfaces:'
        $result.Output | Should -Match 'State persisted\.'
        $identity = Join-Path $config.FullName 'storage\transport_identity'
        $state = Join-Path $config.FullName 'storage\runtime-state-v1.msgpack'
        Test-Path -LiteralPath $identity | Should -BeTrue
        Test-Path -LiteralPath $state | Should -BeTrue
        (Get-Item -LiteralPath $state).Length | Should -BeGreaterThan 0
        $before = (Get-FileHash -LiteralPath $identity).Hash
        $restarted = Invoke-TestProcess $executable $arguments -InputText "status`nexit`n"
        $restarted.Code | Should -Be 0 -Because $restarted.Error
        (Get-FileHash -LiteralPath $identity).Hash | Should -Be $before
        $restarted.Output | Should -Match 'Shutting down'
    }

    It 'gracefully persists on interactive EOF and rejects oversized lines without executing them' {
        $config = New-Item -ItemType Directory -Path (Join-Path $workspace 'interactive-bounds')
        "[reticulum]`nshare_instance=no`n[logging]`nloglevel=4`n[interfaces]`n" |
            Set-Content -LiteralPath (Join-Path $config.FullName 'config')
        $arguments = $prefix + @('rnsd', '--config', $config.FullName, '-i')
        $result = Invoke-TestProcess $executable $arguments -InputText (('x' * 4097) + "`nstatus`n")
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Error | Should -Match 'exceeds 4096 characters; discarded the entire line'
        $result.Output | Should -Match 'Status:'
        $result.Output | Should -Match 'Shutting down'
        Test-Path -LiteralPath (Join-Path $config.FullName 'storage\runtime-state-v1.msgpack') | Should -BeTrue
    }

    It 'does not silently accept ineffective configuration controls' {
        $config = New-Item -ItemType Directory -Path (Join-Path $workspace 'unsupported')
        "[reticulum]`npanic_on_interface_error=yes`n" | Set-Content -LiteralPath (Join-Path $config.FullName 'config')
        $result = Invoke-Rnsd @('--config', $config.FullName)
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'panic_on_interface_error.*unsupported'
        Test-Path -LiteralPath (Join-Path $config.FullName 'storage\transport_identity') | Should -BeFalse
    }

    It 'creates missing directory configuration without silently opting into network discovery' {
        $config = Join-Path $workspace 'created'
        $log = Join-Path $config 'logfile'
        $peer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $config, '--service'))
        try {
            Wait-RnsdLog $peer $log 'rnsd ready'
            Test-Path -LiteralPath (Join-Path $config 'config') | Should -BeTrue
            [IO.File]::ReadAllText($log) | Should -Match 'AutoInterface is disabled by default'
            [IO.File]::ReadAllText((Join-Path $config 'config')) | Should -Match 'enabled = no'
            Test-Path -LiteralPath (Join-Path $config 'storage\transport_identity') | Should -BeTrue
        }
        finally { Stop-TestProcess $peer }
    }

    It 'service mode ignores quiet flags, respects disabled timestamps and preserves the existing config' {
        $config = New-Item -ItemType Directory -Path (Join-Path $workspace 'service')
        $file = Join-Path $config.FullName 'config'
        "[reticulum]`nshare_instance=no`n[logging]`nloglevel=8`nlogtimestamps=no`n[interfaces]`n" | Set-Content -LiteralPath $file
        $before = (Get-FileHash -LiteralPath $file).Hash
        $log = Join-Path $config.FullName 'logfile'
        $peer = Start-TestProcess $executable ($prefix + @('rnsd', "--config=$($config.FullName)", '-s', '-qqqqqqqq'))
        try {
            Wait-RnsdLog $peer $log 'rnsd ready'
            [IO.File]::ReadAllText($log) | Should -Match '\[Info\]\s+rnsd ready'
            [IO.File]::ReadAllText($log) | Should -Not -Match '\[\d{4}-\d{2}-\d{2}'
            (Get-FileHash -LiteralPath $file).Hash | Should -Be $before
            $peer.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $peer }
    }

    It 'persists identity across graceful SIGTERM and restart (Unix only; Windows uses managed cancellation tests)' -Skip:$IsWindows {
        # Windows has no POSIX SIGTERM; in-process cancellation/identity persistence is covered by xUnit.
        $config = New-Item -ItemType Directory -Path (Join-Path $workspace 'signal')
        "[reticulum]`nshare_instance=no`n[logging]`nloglevel=4`n[interfaces]`n" |
            Set-Content -LiteralPath (Join-Path $config.FullName 'config')
        $log = Join-Path $config.FullName 'logfile'
        $identity = Join-Path $config.FullName 'storage\transport_identity'
        $peer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $config.FullName, '--service'))
        try {
            Wait-RnsdLog $peer $log 'rnsd ready'
            $before = (Get-FileHash -LiteralPath $identity).Hash
            & kill -TERM $peer.Process.Id
            $result = Complete-TestProcess $peer
            $peer = $null
            $result.Code | Should -Be 0 -Because $result.Error
            [IO.File]::ReadAllText($log) | Should -Match 'Shutting down'
            Remove-Item -LiteralPath $log
            $peer = Start-TestProcess $executable ($prefix + @('rnsd', '--config', $config.FullName, '--service'))
            Wait-RnsdLog $peer $log 'rnsd ready'
            (Get-FileHash -LiteralPath $identity).Hash | Should -Be $before
            & kill -TERM $peer.Process.Id
            $result = Complete-TestProcess $peer
            $peer = $null
            $result.Code | Should -Be 0 -Because $result.Error
        }
        finally { if ($peer) { Stop-TestProcess $peer } }
    }
}
