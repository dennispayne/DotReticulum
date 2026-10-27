param([string] $Application, [string] $Python, [string] $Upstream)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    $work = New-Item -ItemType Directory -Path (Join-Path (Get-Location) ('.artifacts\p7-transfer-' + [guid]::NewGuid().ToString('N')))
    $shim = Join-Path $PSScriptRoot '..\Interop\rncp_win_shim.py'

    function New-TransferConfig([string] $Name, [string] $Type, [int] $Port) {
        $directory = New-Item -ItemType Directory -Path (Join-Path $work.FullName $Name)
        $endpoint = if ($Type -eq 'TCPServerInterface') { "listen_ip = 127.0.0.1`nlisten_port = $Port" } else { "target_host = 127.0.0.1`ntarget_port = $Port" }
        "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 3`n[interfaces]`n[[Owned test peer]]`ntype = $Type`nenabled = yes`n$endpoint" |
            Set-Content -LiteralPath (Join-Path $directory.FullName 'config')
        return $directory.FullName
    }
    function Get-TransferPort {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $probe.Start()
        try { return $probe.LocalEndpoint.Port } finally { $probe.Stop() }
    }
    function Get-TransferIdentity([string] $Tool, [string] $Config) {
        $result = Invoke-TestProcess $executable ($prefix + @($Tool, '--config', $Config, '-p'))
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        return @{
            Identity = [regex]::Match($result.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
            Destination = [regex]::Match($result.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        }
    }
    function Wait-TransferFile([string] $Path, [long] $Length) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -ne $Length) {
            if ([DateTime]::UtcNow -gt $deadline) { throw "Owned transfer file did not arrive: $Path" }
            Start-Sleep -Milliseconds 50
        }
    }
    function Get-TransferCommand([string] $Script) {
        return 'pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    }
    function Invoke-TransferBinary([string[]] $Arguments) {
        $info = [Diagnostics.ProcessStartInfo]::new($executable)
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in ($prefix + $Arguments)) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($info)
        $out = [IO.MemoryStream]::new()
        $err = [IO.MemoryStream]::new()
        try {
            $outTask = $process.StandardOutput.BaseStream.CopyToAsync($out)
            $errTask = $process.StandardError.BaseStream.CopyToAsync($err)
            if (!$process.WaitForExit(60000)) { throw 'Binary CLI exceeded its owned 60-second lifetime.' }
            [Threading.Tasks.Task]::WaitAll(@($outTask, $errTask))
            return @{ Code = $process.ExitCode; Output = $out.ToArray(); Error = $err.ToArray() }
        }
        finally {
            if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose(); $out.Dispose(); $err.Dispose()
        }
    }
}

AfterAll { if ($work -and (Test-Path -LiteralPath $work.FullName)) { Remove-Item -LiteralPath $work.FullName -Recurse -Force } }

Describe 'Parity 7 transfer and execution process contracts' -Tag E2E, Utilities {
    It 'reports parser/version/output and execution authorization failures without starting a listener' -Tag 'Contract:UTIL-RNX-001' {
        foreach ($tool in 'rnsd', 'rncp', 'rnx') {
            $version = Invoke-TestProcess $executable ($prefix + @($tool, '--version'))
            $version.Code | Should -Be 0
            $version.Output | Should -Match 'reference RNS 1\.5\.7'
            $invalid = Invoke-TestProcess $executable ($prefix + @($tool, '--not-a-parity7-option'))
            $invalid.Code | Should -Be 2
        }
        $missing = Invoke-TestProcess $executable ($prefix + @('rncp', '-l', '-s', (Join-Path $work.FullName 'absent')))
        $missing.Code | Should -Be 3
        $missing.Error | Should -Match 'Output directory not found'
        $policy = Invoke-TestProcess $executable ($prefix + @('rnx', '-l', '-n'))
        $policy.Code | Should -Be 2
        $policy.Error | Should -Match 'allow-execution'
    }

    It 'returns writable-output exit 4 for an existing directory without write access' -Tag 'Contract:UTIL-RNCP-002' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'denied-save')
        $acl = $null
        try {
            if ($IsWindows) {
                $acl = Get-Acl -LiteralPath $directory.FullName
                $denied = Get-Acl -LiteralPath $directory.FullName
                $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
                $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,
                    [Security.AccessControl.FileSystemRights]::CreateFiles, [Security.AccessControl.AccessControlType]::Deny)
                $denied.AddAccessRule($rule)
                Set-Acl -LiteralPath $directory.FullName -AclObject $denied
            }
            else {
                [IO.File]::SetUnixFileMode($directory.FullName, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserExecute)
            }
            $result = Invoke-TestProcess $executable ($prefix + @('rncp', '-l', '-s', $directory.FullName))
            $result.Code | Should -Be 4 -Because ($result.Output + $result.Error)
            $result.Error | Should -Match 'Output directory not writable'
        }
        finally {
            if ($IsWindows -and $acl) { Set-Acl -LiteralPath $directory.FullName -AclObject $acl }
            elseif (!$IsWindows) { [IO.File]::SetUnixFileMode($directory.FullName, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
        }
    }

    It 'sends fetches renames overwrites and refuses jailed files using separate authorized CLI processes' -Tag 'Contract:UTIL-RNCP-002' {
        $port = Get-TransferPort
        $server = New-TransferConfig 'rncp-server' TCPServerInterface $port
        $client = New-TransferConfig 'rncp-client' TCPClientInterface $port
        $serverId = Get-TransferIdentity rncp $server
        $clientId = Get-TransferIdentity rncp $client
        $saved = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'rncp-save')
        $jail = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'rncp-jail')
        $fetched = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'rncp-fetched')
        $source = Join-Path $work.FullName 'upload.bin'
        $bytes = [byte[]]::new(2097189)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        [IO.File]::WriteAllBytes($source, $bytes)
        [IO.File]::WriteAllBytes((Join-Path $jail.FullName 'fetch.bin'), $bytes)
        $listener = Start-TestProcess $executable ($prefix + @('rncp', '--config', $server, '-l', '-a', $clientId.Identity,
            '-s', $saved.FullName, '-F', '-j', $jail.FullName, '-b', '1'))
        try {
            foreach ($iteration in 0, 1) {
                $sent = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $client, '-w', '20', $source, $serverId.Destination))
                $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            }
            Wait-TransferFile (Join-Path $saved.FullName 'upload.bin.1') $bytes.Length
            (Get-FileHash -LiteralPath (Join-Path $saved.FullName 'upload.bin.1')).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
            $fetch = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $client, '-f', 'fetch.bin', $serverId.Destination,
                '-s', $fetched.FullName, '-w', '20', '-P'))
            $fetch.Code | Should -Be 0 -Because ($fetch.Output + $fetch.Error)
            $fetch.Output | Should -Match 'Physical layer rate:'
            (Get-FileHash -LiteralPath (Join-Path $fetched.FullName 'fetch.bin')).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
            $overwrite = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $client, '-f', 'fetch.bin', $serverId.Destination,
                '-s', $fetched.FullName, '-w', '20', '-O'))
            $overwrite.Code | Should -Be 0
            Test-Path -LiteralPath (Join-Path $fetched.FullName 'fetch.bin.1') | Should -BeFalse
            $outsidePath = [IO.Path]::Combine('..', 'upload.bin')
            $outside = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $client, '-f', $outsidePath,
                $serverId.Destination, '-s', $fetched.FullName, '-w', '20'))
            $outside.Code | Should -Be 0
            $outside.Output | Should -Match 'not allowed'
            $listener.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $listener }
    }

    It 'preserves binary streams output slice limits failed spawn and remote versus protocol exit codes' -Tag 'Contract:UTIL-RNX-001' {
        $port = Get-TransferPort
        $server = New-TransferConfig 'rnx-server' TCPServerInterface $port
        $client = New-TransferConfig 'rnx-client' TCPClientInterface $port
        $serverId = Get-TransferIdentity rnx $server
        $clientId = Get-TransferIdentity rnx $client
        $listener = Start-TestProcess $executable ($prefix + @('rnx', '--config', $server, '-l', '--allow-execution', '-a', $clientId.Identity))
        try {
            $command = Get-TransferCommand '[Console]::OpenStandardOutput().Write([byte[]](0,255,65,66),0,4);[Console]::OpenStandardError().Write([byte[]](254,0,69),0,3);exit 7'
            $binary = Invoke-TransferBinary @('rnx', '--config', $client, '-w', '15', '-m', $serverId.Destination, $command)
            $binary.Code | Should -Be 7
            [Convert]::ToHexString($binary.Output) | Should -Be '00FF4142'
            [Convert]::ToHexString($binary.Error) | Should -Be 'FE0045'
            $negative = Invoke-TransferBinary @('rnx', '--config', $client, '-w', '15', '--stdout', '-1', '--stderr', '0',
                $serverId.Destination, $command)
            $negative.Code | Should -Be 0
            [Convert]::ToHexString($negative.Output) | Should -Be '00FF41'
            $negative.Error.Length | Should -Be 0
            $stdinCommand = Get-TransferCommand '[Console]::Write([Console]::In.ReadToEnd())'
            $stdinResult = Invoke-TransferBinary @('rnx', '--config', $client, '-w', '15', '--stdin=-v',
                $serverId.Destination, $stdinCommand)
            $stdinResult.Code | Should -Be 0
            [Text.Encoding]::UTF8.GetString($stdinResult.Output) | Should -Be '-v'
            $missing = Invoke-TestProcess $executable ($prefix + @('rnx', '--config', $client, '-w', '15',
                $serverId.Destination, 'dotreticulum-no-such-parity7-command'))
            $missing.Code | Should -Be 248
            $missing.Error | Should -Match 'could not execute'
            $exit245 = Get-TransferCommand 'exit 245'
            $notMirrored = Invoke-TestProcess $executable ($prefix + @('rnx', '--config', $client, '-w', '15', $serverId.Destination, $exit245))
            $notMirrored.Code | Should -Be 0
            $mirrored = Invoke-TestProcess $executable ($prefix + @('rnx', '--config', $client, '-w', '15', '-m', $serverId.Destination, $exit245))
            $mirrored.Code | Should -Be 245
            $interactive = Invoke-TestProcess $executable ($prefix + @('rnx', '--config', $client, '-w', '15', '-x', '-m',
                $serverId.Destination, (Get-TransferCommand 'exit 3'))) -InputText "dotnet --version`nEXIT`n"
            $interactive.Code | Should -Be 0 -Because ($interactive.Output + $interactive.Error)
            $interactive.Output | Should -Match '3> '
            $interactive.Output | Should -Match '\d+\.\d+\.\d+'
            $timed = Invoke-TestProcess $executable ($prefix + @('rnx', '--config', $client, '-w', '2', '-d', $serverId.Destination,
                (Get-TransferCommand "[Console]::Write('before timeout');Start-Sleep -Seconds 30")))
            $timed.Code | Should -Be 0 -Because ($timed.Output + $timed.Error)
            $timed.Output | Should -Match 'before timeout'
            $listener.Process.HasExited | Should -BeFalse
        }
        finally { Stop-TestProcess $listener }
    }

    It 'exchanges files with verified pinned Python utilities in both CLI listener roles' -Tag 'Contract:UTIL-RNCP-001', 'Reference' -Skip:(!$Python -or !$Upstream) {
        $port = Get-TransferPort
        $nativeConfig = New-TransferConfig 'mixed-native' TCPServerInterface $port
        $pythonConfig = New-TransferConfig 'mixed-python' TCPClientInterface $port
        $nativeId = Get-TransferIdentity rncp $nativeConfig
        $environment = @{ PYTHONPATH = $Upstream; DOTRETICULUM_UPSTREAM = $Upstream; PYTHONIOENCODING = 'utf-8' }
        $printed = Invoke-TestProcess $Python @('-u', $shim, 'rncp', '--config', $pythonConfig, '-p') -Environment $environment
        $printed.Code | Should -Be 0 -Because ($printed.Output + $printed.Error)
        $pythonIdentity = [regex]::Match($printed.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $pythonDestination = [regex]::Match($printed.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $nativeSave = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'mixed-native-save')
        $pythonSave = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'mixed-python-save')
        $source = Join-Path $work.FullName 'mixed.bin'
        $bytes = [byte[]]::new(3145741)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        [IO.File]::WriteAllBytes($source, $bytes)
        $native = Start-TestProcess $executable ($prefix + @('rncp', '--config', $nativeConfig, '-l', '-a', $pythonIdentity,
            '-s', $nativeSave.FullName, '-F', '-j', $work.FullName, '-b', '1'))
        try {
            $sent = Invoke-TestProcess $Python @('-u', $shim, 'rncp', '--config', $pythonConfig, $source,
                $nativeId.Destination, '-w', '30') -Environment $environment
            $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            Wait-TransferFile (Join-Path $nativeSave.FullName 'mixed.bin') $bytes.Length
            (Get-FileHash -LiteralPath (Join-Path $nativeSave.FullName 'mixed.bin')).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
            $fetch = Invoke-TestProcess $Python @('-u', $shim, 'rncp', '--config', $pythonConfig, '-f', 'mixed.bin',
                $nativeId.Destination, '-w', '30') -Environment $environment -WorkingDirectory $pythonSave.FullName
            $fetch.Code | Should -Be 0 -Because ($fetch.Output + $fetch.Error)
            Wait-TransferFile (Join-Path $pythonSave.FullName 'mixed.bin') $bytes.Length
        }
        finally { Stop-TestProcess $native }
        $pythonPeer = Start-TestProcess $Python @('-u', $shim, 'rncp', '--config', $pythonConfig, '-l', '-a', $nativeId.Identity, '-F', '-b', '1') `
            -Environment $environment -WorkingDirectory $pythonSave.FullName
        try {
            $sent = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $nativeConfig, $source, $pythonDestination, '-w', '30'))
            $sent.Code | Should -Be 0 -Because ($sent.Output + $sent.Error)
            Wait-TransferFile (Join-Path $pythonSave.FullName 'mixed.bin.1') $bytes.Length
            $fetchDirectory = New-Item -ItemType Directory -Path (Join-Path $work.FullName 'mixed-native-fetch')
            $fetch = Invoke-TestProcess $executable ($prefix + @('rncp', '--config', $nativeConfig, '-f', 'mixed.bin',
                $pythonDestination, '-s', $fetchDirectory.FullName, '-w', '30'))
            $fetch.Code | Should -Be 0 -Because ($fetch.Output + $fetch.Error)
            Wait-TransferFile (Join-Path $fetchDirectory.FullName 'mixed.bin') $bytes.Length
            (Get-FileHash -LiteralPath (Join-Path $fetchDirectory.FullName 'mixed.bin')).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
        }
        finally { Stop-TestProcess $pythonPeer }
    }

    It 'executes explicitly authorized commands in both verified Python and native CLI listener roles' -Tag 'Contract:UTIL-RNX-001', 'Reference' -Skip:(!$Python -or !$Upstream) {
        $port = Get-TransferPort
        $nativeConfig = New-TransferConfig 'mixed-rnx-native' TCPServerInterface $port
        $pythonConfig = New-TransferConfig 'mixed-rnx-python' TCPClientInterface $port
        $nativeId = Get-TransferIdentity rnx $nativeConfig
        $environment = @{ PYTHONPATH = $Upstream; DOTRETICULUM_UPSTREAM = $Upstream; PYTHONIOENCODING = 'utf-8' }
        $printed = Invoke-TestProcess $Python @('-u', $shim, 'rnx', '--config', $pythonConfig, '-p') -Environment $environment
        $printed.Code | Should -Be 0 -Because ($printed.Output + $printed.Error)
        $pythonIdentity = [regex]::Match($printed.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $pythonDestination = [regex]::Match($printed.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
        $native = Start-TestProcess $executable ($prefix + @('rnx', '--config', $nativeConfig, '-l', '--allow-execution', '-a', $pythonIdentity))
        try {
            $executed = Invoke-TestProcess $Python @('-u', $shim, 'rnx', '--config', $pythonConfig, '-w', '30', '-m',
                $nativeId.Destination, 'dotnet --version') -Environment $environment
            $executed.Code | Should -Be 0 -Because ($executed.Output + $executed.Error)
            $executed.Output | Should -Match '\d+\.\d+\.\d+'
        }
        finally { Stop-TestProcess $native }
        $pythonPeer = Start-TestProcess $Python @('-u', $shim, 'rnx', '--config', $pythonConfig, '-l', '-a', $nativeId.Identity) -Environment $environment
        try {
            $binaryCommand = Get-TransferCommand '[Console]::OpenStandardOutput().Write([byte[]](0,255,65,66),0,4);[Console]::OpenStandardError().Write([byte[]](254,0,69),0,3);exit 7'
            $executed = Invoke-TransferBinary @('rnx', '--config', $nativeConfig, '-w', '30', '-m', $pythonDestination, $binaryCommand)
            $executed.Code | Should -Be 7
            [Convert]::ToHexString($executed.Output) | Should -Be '00FF4142'
            [Convert]::ToHexString($executed.Error) | Should -Be 'FE0045'
        }
        finally { Stop-TestProcess $pythonPeer }
    }
}
