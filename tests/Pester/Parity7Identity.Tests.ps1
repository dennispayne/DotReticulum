param(
    [string] $Application,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM
)

BeforeAll {
    $root = Join-Path $PSScriptRoot "..\..\TestResults\rnid-process-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $root
    $root = (Resolve-Path -LiteralPath $root).Path
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $command = if ([IO.Path]::GetExtension($cli) -eq '.dll') { (Get-Command dotnet -CommandType Application).Source } else { $cli }
    $prefix = @(if ([IO.Path]::GetExtension($cli) -eq '.dll') { $cli })
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $testKey = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
    $generator = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\GenerateParity7RnidVectors.py')).Path

    function Invoke-RnidProcess {
        param([string[]] $Arguments, [byte[]] $InputBytes = @(), [int] $TimeoutSeconds = 30,
            [string] $Executable = $command, [switch] $Reference)
        $info = [Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WorkingDirectory = $root
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
        foreach ($arg in @($(if (-not $Reference) { $prefix }), $(if (-not $Reference) { 'rnid' }), $Arguments)) {
            foreach ($value in $arg) { if ($null -ne $value) { $info.ArgumentList.Add($value) } }
        }
        if ($Reference) {
            $info.Environment['DOTRETICULUM_UPSTREAM'] = $Upstream
            $info.Environment['PYTHONPATH'] = $Upstream
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $bytes = [IO.MemoryStream]::new()
        try {
            $null = $process.Start()
            $copy = $process.StandardOutput.BaseStream.CopyToAsync($bytes)
            $errors = $process.StandardError.ReadToEndAsync()
            $write = $process.StandardInput.BaseStream.WriteAsync($InputBytes, 0, $InputBytes.Length)
            if (-not $write.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) { throw 'rnid stdin timed out' }
            $process.StandardInput.Close()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw 'rnid process timed out' }
            if (-not $copy.Wait([TimeSpan]::FromSeconds(3))) { throw 'rnid stdout did not close' }
            if (-not $errors.Wait([TimeSpan]::FromSeconds(3))) { throw 'rnid stderr did not close' }
            return @{
                Code = $process.ExitCode
                Bytes = $bytes.ToArray()
                Output = [Text.Encoding]::UTF8.GetString($bytes.ToArray())
                Error = $errors.Result
            }
        }
        finally {
            if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(3000) }
            $process.Dispose()
            $bytes.Dispose()
        }
    }
}

AfterAll {
    if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Describe 'Parity 7 rnid real processes' {
    It 'preserves legacy create/show output and refuses destructive overwrite' {
        $identity = Join-Path $root 'legacy identity.rid'
        $generated = Invoke-RnidProcess -Arguments @('--generate', $identity)
        $generated.Code | Should -Be 0
        $generated.Output | Should -Match 'Identity hash: [0-9a-f]{32}'
        $original = [IO.File]::ReadAllBytes($identity)
        $shown = Invoke-RnidProcess -Arguments @('--show', $identity)
        $shown.Code | Should -Be 0
        $shown.Output | Should -BeExactly $generated.Output
        $refused = Invoke-RnidProcess -Arguments @('--generate', $identity)
        $refused.Code | Should -Be 1
        $refused.Output | Should -BeNullOrEmpty
        $refused.Error | Should -Not -BeNullOrEmpty
        [Convert]::ToHexString([IO.File]::ReadAllBytes($identity)) | Should -BeExactly ([Convert]::ToHexString($original))
    }

    It 'imports, exports, persists and reloads independent reference keys' {
        $path = Join-Path $root 'imported.rid'
        $imported = Invoke-RnidProcess -Arguments @('-M', $testKey, '-X', '-w', $path)
        $imported.Code | Should -Be 0
        [Convert]::ToHexString([IO.File]::ReadAllBytes($path)).ToLowerInvariant() | Should -BeExactly $testKey
        $shown = Invoke-RnidProcess -Arguments @('-i', $path, '-p', '-B')
        $shown.Code | Should -Be 0
        $shown.Output | Should -Match ([regex]::Escape($fixture.base32))
        $shown.Output | Should -Match 'Private Key   : Hidden'
        $shown.Output | Should -Not -Match $testKey
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-X', '-w', $path)).Code | Should -Be 11
    }

    It 'creates byte-identical detached signatures, validates files recursively and rejects tampering' {
        $first = Join-Path $root 'first'
        $second = Join-Path $root 'second'
        [IO.File]::WriteAllBytes($first, [Convert]::FromHexString($fixture.message))
        [IO.File]::WriteAllText($second, 'second message')
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-s', $first, $second)).Code | Should -Be 0
        [Convert]::ToHexString([IO.File]::ReadAllBytes("$first.rsg")).ToLowerInvariant() | Should -BeExactly $fixture.rsg
        (Invoke-RnidProcess -Arguments @('-V', $first, $second)).Code | Should -Be 0
        [IO.File]::AppendAllText($second, ' tampered')
        (Invoke-RnidProcess -Arguments @('-V', $first, $second)).Code | Should -Be 10
    }

    It 'verifies legacy signatures only with an explicit key' {
        $path = Join-Path $root 'legacy-data'
        [IO.File]::WriteAllBytes($path, [Convert]::FromHexString($fixture.message))
        [IO.File]::WriteAllBytes("$path.rsg", [Convert]::FromHexString($fixture.raw))
        (Invoke-RnidProcess -Arguments @('-V', $path)).Code | Should -Be 2
        (Invoke-RnidProcess -Arguments @('-m', $fixture.base64, '-V', $path)).Code | Should -Be 0
    }

    It 'handles stdin, binary stdout, wrapped base256 RSM and authenticated metadata' {
        $signed = Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-I', '-O') -InputBytes ([Text.Encoding]::UTF8.GetBytes('stdin λ'))
        $signed.Code | Should -Be 0
        $verified = Invoke-RnidProcess -Arguments @('-V', '-', '-I') -InputBytes $signed.Bytes
        $verified.Code | Should -Be 0
        $verified.Output | Should -Match 'stdin λ'
        $wrapped = Join-Path $root 'wrapped.rsm'
        [IO.File]::WriteAllText($wrapped, $fixture.wrapped.base256, [Text.UTF8Encoding]::new($false))
        (Invoke-RnidProcess -Arguments @('-V', $wrapped)).Code | Should -Be 0
        $metadata = Join-Path $root 'metadata.rsm'
        [IO.File]::WriteAllBytes($metadata, [Convert]::FromHexString($fixture.rsm))
        $verified = Invoke-RnidProcess -Arguments @('-V', $metadata, '--meta')
        $verified.Code | Should -Be 0
        $verified.Output | Should -Match 's  title=Example'
        (Invoke-RnidProcess -Arguments @('-i', ('a' * 32), '-N', '-V', $metadata)).Code | Should -Be 10
    }

    It 'decrypts the independent reference ciphertext and rejects wrong identities and overwrite' {
        $encrypted = Join-Path $root 'reference.rfe'
        $plain = Join-Path $root 'reference.plain'
        [IO.File]::WriteAllBytes($encrypted, [Convert]::FromHexString($fixture.encrypted))
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-d', $encrypted, '-w', $plain)).Code | Should -Be 0
        [Convert]::ToHexString([IO.File]::ReadAllBytes($plain)).ToLowerInvariant() | Should -BeExactly $fixture.message
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-d', $encrypted, '-w', $plain)).Code | Should -Be 11
        $wrongKey = [Convert]::ToHexString([byte[]](63..0)).ToLowerInvariant()
        (Invoke-RnidProcess -Arguments @('-M', $wrongKey, '-d', $encrypted, '-w', $plain, '-f')).Code | Should -Be 12
    }

    It 'uses current metadata specs, file messages and output extension rules' {
        $message = Join-Path $root 'file-message.txt'
        $metadata = Join-Path $root 'file-metadata'
        $output = Join-Path $root 'file-message'
        [IO.File]::WriteAllText($message, 'file message')
        [IO.File]::WriteAllText($metadata, "count=3`nflag=yes`n")
        [IO.File]::WriteAllText("$metadata.spec", "count=`"integer(1, 5)`"`nflag=boolean()`n")
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-r', $message, '-E', $metadata, '-w', $output)).Code | Should -Be 0
        $verified = Invoke-RnidProcess -Arguments @('-V', "$output.rsm", '--meta')
        $verified.Code | Should -Be 0
        $verified.Output | Should -Match 'i  count=3'
        [IO.File]::WriteAllText($metadata, "count=9`nflag=yes`n")
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-r', $message, '-E', $metadata, '-w', "${output}-invalid")).Code | Should -Not -Be 0
        Test-Path -LiteralPath "${output}-invalid.rsm" | Should -BeFalse
    }

    It 'requires explicit editor authorization and bounds authorized success, failure and timeout' {
        $editor = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $script = Join-Path $root 'owned-editor.ps1'
        [IO.File]::WriteAllText($script, 'param($Path); [IO.File]::WriteAllText($Path, "editor message λ")')
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-b', '--editor', $editor)).Code | Should -Be 252
        $result = Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-b', '--allow-editor', '--editor', $editor,
            '--editor-args', "-NoProfile -File '$script'", '--editor-timeout', '10')
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        $result.Output | Should -Match 'Start of rsg data'
        [IO.File]::WriteAllText($script, 'exit 7')
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-b', '--allow-editor', '--editor', $editor,
            '--editor-args', "-NoProfile -File '$script'")).Code | Should -Be 252
        [IO.File]::WriteAllText($script, 'Start-Sleep -Seconds 120')
        $watch = [Diagnostics.Stopwatch]::StartNew()
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-S', '-b', '--allow-editor', '--editor', $editor,
            '--editor-args', "-NoProfile -File '$script'", '--editor-timeout', '0.5')).Code | Should -Be 252
        $watch.Elapsed.TotalSeconds | Should -BeLessThan 10
        @(Get-ChildItem -LiteralPath $root -Directory -Filter '.rnid-editor-*').Count | Should -Be 0
    }

    It 'rejects invalid encoding, mutually exclusive arguments and nonfinite network timeout' {
        (Invoke-RnidProcess -Arguments @('-m', 'invalid', '-p')).Code | Should -Be 8
        (Invoke-RnidProcess -Arguments @('-b', '-B', '-p')).Code | Should -Be 1
        (Invoke-RnidProcess -Arguments @('-t', 'NaN', '-p')).Code | Should -Be 2
        (Invoke-RnidProcess -Arguments @('-i', $fixture.hash, '-N', '-H', 'rns.id')).Output | Should -Match $fixture.destination
    }

    It 'interoperates with pinned Python file encryption in both directions across the exact 16 MiB boundary' {
        if (-not $Python -or -not $Upstream) { throw 'This contract requires explicit pinned Python and upstream paths; it is not silently skipped.' }
        $source = Join-Path $root 'large-source'
        $bytes = [byte[]]::new(16 * 1024 * 1024 + 257)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        [IO.File]::WriteAllBytes($source, $bytes)
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-e', $source)).Code | Should -Be 0
        $decoded = Join-Path $root 'python-decoded'
        $reference = Invoke-RnidProcess -Executable $Python -Reference -Arguments @($generator, '--decrypt', "$source.rfe", '--write', $decoded)
        $reference.Code | Should -Be 0 -Because $reference.Error
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($decoded))) |
            Should -BeExactly ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))
        $encoded = Join-Path $root 'python-encoded.rfe'
        $reference = Invoke-RnidProcess -Executable $Python -Reference -Arguments @($generator, '--encrypt', $source, '--write', $encoded)
        $reference.Code | Should -Be 0 -Because $reference.Error
        $decoded = Join-Path $root 'dotnet-decoded'
        (Invoke-RnidProcess -Arguments @('-M', $testKey, '-d', $encoded, '-w', $decoded)).Code | Should -Be 0
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($decoded))) |
            Should -BeExactly ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))
    }

    It 'requests a real Python identity, retains it across restart and sends an authenticated announce back' {
        if (-not $Python -or -not $Upstream) { throw 'Pinned Python and upstream are required for actual network acceptance.' }
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $port = $probe.LocalEndpoint.Port
        $probe.Stop()
        $adapter = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\GenerateParity7RnidNetwork.py')).Path
        $pythonConfig = Join-Path $root 'python-network'
        $config = Join-Path $root 'native-network'
        $null = New-Item -ItemType Directory -Path $config
        [IO.File]::WriteAllText((Join-Path $config 'config'), @"
[reticulum]
share_instance = no
[interfaces]
  [[Owned Python]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = $port
"@)
        $info = [Diagnostics.ProcessStartInfo]::new($Python)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Environment['DOTRETICULUM_UPSTREAM'] = $Upstream
        $info.Environment['PYTHONPATH'] = $Upstream
        $info.Environment['PYTHONIOENCODING'] = 'utf-8'
        foreach ($arg in @($adapter, '--config', $pythonConfig, '--port', "$port")) { $info.ArgumentList.Add($arg) }
        $peer = [Diagnostics.Process]::new()
        $peer.StartInfo = $info
        function Read-RnidPeerEvent([string] $Expected) {
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            while ([DateTime]::UtcNow -lt $deadline) {
                $read = $peer.StandardOutput.ReadLineAsync()
                if (-not $read.Wait([TimeSpan]::FromSeconds(20))) { throw "Python event $Expected timed out" }
                if ($null -eq $read.Result) { throw "Python exited before $Expected" }
                if ($read.Result.StartsWith('@@')) {
                    $value = $read.Result.Substring(2) | ConvertFrom-Json
                    if ($value.event -eq $Expected) { return $value }
                }
            }
            throw "Python event $Expected timed out"
        }
        try {
            $null = $peer.Start()
            $errors = $peer.StandardError.ReadToEndAsync()
            $ready = Read-RnidPeerEvent 'ready'
            $result = Invoke-RnidProcess -Arguments @('--config', $config, '-i', $ready.identity, '-R', '-t', '10', '-p')
            $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
            $result.Output | Should -Match $ready.identity
            $result.Output | Should -Match 'Received Identity'
            $result = Invoke-RnidProcess -Arguments @('--config', $config, '-M', $testKey, '-a')
            $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
            $announced = Read-RnidPeerEvent 'announce'
            $announced.identity | Should -BeExactly $fixture.hash
            $announced.destination | Should -BeExactly $fixture.destination
            $peer.StandardInput.WriteLine('quit')
            $peer.StandardInput.Flush()
            $peer.WaitForExit(5000) | Should -BeTrue
            $peer.ExitCode | Should -Be 0 -Because $errors.Result
            $result = Invoke-RnidProcess -Arguments @('--config', $config, '-i', $ready.identity, '-p')
            $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
            $result.Output | Should -Match 'Recalled Identity'
            $result.Output | Should -Match $ready.identity
            $result = Invoke-RnidProcess -Arguments @('--config', $config, '-i', ('b' * 32), '-R', '-t', '0.2', '-p')
            $result.Code | Should -Be 2
            $result.Output | Should -Match 'Identity request timed out'
        }
        finally {
            if ($peer.Id -and -not $peer.HasExited) { $peer.Kill($true); $null = $peer.WaitForExit(3000) }
            $peer.Dispose()
        }
    }
}
