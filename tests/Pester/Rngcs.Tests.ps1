param(
    [string] $Application,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM
)

BeforeAll {
    $root = Join-Path $PSScriptRoot "..\..\TestResults\rngcs-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $root -Force
    $root = (Resolve-Path -LiteralPath $root).Path
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    $bin = Join-Path $root 'program with spaces'
    $null = New-Item -ItemType Directory -Path $bin
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $helper = Join-Path $bin "rngcs$suffix"
    $applicationDirectory = Split-Path $cli -Parent
    $baseName = [IO.Path]::GetFileNameWithoutExtension($cli)
    $managed = [IO.Path]::GetExtension($cli) -eq '.dll' -or
        (Test-Path -LiteralPath (Join-Path $applicationDirectory "$baseName.runtimeconfig.json"))
    if ($managed) {
        Copy-Item -LiteralPath (Join-Path $applicationDirectory "$baseName$suffix") -Destination $helper
        foreach ($extension in '.dll', '.deps.json', '.runtimeconfig.json') {
            Copy-Item -LiteralPath (Join-Path $applicationDirectory "$baseName$extension") -Destination $bin
        }
        foreach ($item in Get-ChildItem -LiteralPath $applicationDirectory -File) {
            if ($item.Name -eq "$baseName.dll" -or $item.Extension -notin @('.dll', '.so', '.dylib')) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $bin
        }
        $runtimes = Join-Path $applicationDirectory 'runtimes'
        if (Test-Path -LiteralPath $runtimes) { Copy-Item -LiteralPath $runtimes -Destination $bin -Recurse }
    }
    else { Copy-Item -LiteralPath $cli -Destination $helper }

    $globalConfig = Join-Path $root 'empty global git config'
    [IO.File]::WriteAllText($globalConfig, '')
    $scratch = Join-Path $root 'owned scratch'
    $null = New-Item -ItemType Directory -Path $scratch
    $environment = @{
        GIT_CONFIG_NOSYSTEM = '1'
        GIT_CONFIG_GLOBAL = $globalConfig
        GIT_CONFIG_COUNT = '0'
        GIT_TERMINAL_PROMPT = '0'
        GIT_DIR = $null
        GIT_WORK_TREE = $null
        GIT_INDEX_FILE = $null
        GIT_OBJECT_DIRECTORY = $null
        GIT_ALTERNATE_OBJECT_DIRECTORIES = $null
        GIT_EXEC_PATH = $null
        GIT_AUTHOR_NAME = $null
        GIT_AUTHOR_EMAIL = $null
        GIT_COMMITTER_NAME = $null
        GIT_COMMITTER_EMAIL = $null
        TEMP = $scratch
        TMP = $scratch
        TMPDIR = $scratch
        PATH = "$bin$([IO.Path]::PathSeparator)$env:PATH"
        DOTRETICULUM_UPSTREAM = $Upstream
        PYTHONPATH = $Upstream
    }

    function Invoke-RngcsProcess {
        param(
            [string] $Executable,
            [string[]] $Arguments,
            [byte[]] $InputBytes = @(),
            [int] $TimeoutSeconds = 30,
            [hashtable] $EnvironmentOverrides = @{}
        )
        $info = [Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.WorkingDirectory = $root
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
        $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        foreach ($key in $environment.Keys) {
            if ($null -eq $environment[$key]) { $null = $info.Environment.Remove($key) }
            else { $info.Environment[$key] = $environment[$key] }
        }
        foreach ($key in $EnvironmentOverrides.Keys) {
            if ($null -eq $EnvironmentOverrides[$key]) { $null = $info.Environment.Remove($key) }
            else { $info.Environment[$key] = $EnvironmentOverrides[$key] }
        }
        $process = [Diagnostics.Process]::Start($info)
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        try {
            if ($InputBytes.Length) { $process.StandardInput.BaseStream.Write($InputBytes) }
            $process.StandardInput.Close()
            if (!$process.WaitForExit($TimeoutSeconds * 1000)) { throw "Owned process $Executable timed out." }
            @{
                Code = $process.ExitCode
                Output = $output.GetAwaiter().GetResult()
                Error = $errorOutput.GetAwaiter().GetResult()
            }
        }
        finally {
            if (!$process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
            $process.Dispose()
        }
    }

    function Invoke-RngcsCli {
        param([string[]] $Arguments, [byte[]] $InputBytes = @())
        Invoke-RngcsProcess $executable ($prefix + $Arguments) $InputBytes
    }

    function Invoke-FixtureGit {
        param([string[]] $Arguments)
        Invoke-RngcsProcess $git (@('-C', $repo) + $Arguments)
    }

    function Get-FixtureGit {
        param([string[]] $Arguments)
        $result = Invoke-FixtureGit $Arguments
        if ($result.Code -ne 0) { throw "git $($Arguments -join ' ') failed: $($result.Error)$($result.Output)" }
        $result.Output
    }

    function Get-CommitSignature {
        param([string] $Revision = 'HEAD')
        $raw = Get-FixtureGit @('cat-file', 'commit', $Revision)
        $signature = [Text.StringBuilder]::new()
        $payload = [Text.StringBuilder]::new()
        $inSignature = $false
        $lines = $raw -split "`n"
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            if ($line.StartsWith('gpgsig ')) {
                $inSignature = $true
                $null = $signature.Append($line.Substring(7)).Append("`n")
                continue
            }
            if ($inSignature -and $line.StartsWith(' ')) {
                $null = $signature.Append($line.Substring(1)).Append("`n")
                continue
            }
            $inSignature = $false
            $null = $payload.Append($line)
            if ($index -lt $lines.Count - 1) { $null = $payload.Append("`n") }
        }
        @{ Signature = $signature.ToString(); Message = [Text.Encoding]::UTF8.GetBytes($payload.ToString()) }
    }

    $key = Join-Path $root 'disposable private identity'
    $created = Invoke-RngcsCli @('rnid', '--generate', $key)
    if ($created.Code -ne 0) { throw "Identity generation failed: $($created.Error)" }
    $identity = [regex]::Match($created.Output, 'Identity hash: ([0-9a-f]{32})').Groups[1].Value
    if (!$identity) { throw "Could not determine generated identity: $($created.Output)" }
    $repo = Join-Path $root 'owned git repository'
    $init = Invoke-RngcsProcess $git @('init', '--initial-branch=main', '--object-format=sha1', $repo)
    if ($init.Code -ne 0) { throw "git init failed: $($init.Error)" }
    $allowed = Join-Path $root 'empty allowed signers'
    [IO.File]::WriteAllText($allowed, '')
    foreach ($setting in @(
        @('user.name', 'Disposable signing fixture'),
        @('user.email', $identity),
        @('user.signingkey', $key),
        @('gpg.format', 'ssh'),
        @('gpg.ssh.program', $helper),
        @('gpg.ssh.allowedSignersFile', $allowed),
        @('commit.gpgsign', 'true'),
        @('tag.gpgsign', 'true')
    )) {
        $null = Get-FixtureGit @('config', '--local', $setting[0], $setting[1])
    }
    $null = Get-FixtureGit @('commit', '--allow-empty', '-m', 'signed fixture')
    $validCommit = (Get-FixtureGit @('rev-parse', 'HEAD')).Trim()
    $null = Get-FixtureGit @('tag', '-s', 'fixture-v1', '-m', 'signed tag fixture')
}

AfterAll {
    if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Describe 'rngcs actual Git signing and verification' -Tag E2E {
    It 'signs and verifies a real commit using the installed executable alias and only repository-local config' {
        $result = Invoke-FixtureGit @('verify-commit', $validCommit)
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        ($result.Output + $result.Error) | Should -Match "Good `"git`" signature.*<$identity>"
        $raw = Get-FixtureGit @('cat-file', 'commit', $validCommit)
        $raw | Should -Match 'gpgsig -----BEGIN SSH SIGNATURE-----'
        # Upstream ignores allowed-signers policy; this empty file must not be
        # described as trust authorization, even when Git reports verification.
        [IO.File]::ReadAllText($allowed) | Should -Be ''
        (Get-FixtureGit @('config', '--local', '--get', 'gpg.ssh.program')).Trim() | Should -Be $helper
    }

    It 'signs and verifies a real annotated tag using tagger identity' {
        $result = Invoke-FixtureGit @('verify-tag', 'fixture-v1')
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        ($result.Output + $result.Error) | Should -Match "Good `"git`" signature.*<$identity>"
        (Get-FixtureGit @('cat-file', 'tag', 'fixture-v1')) | Should -Match '-----BEGIN SSH SIGNATURE-----'
    }

    It 'rejects a cryptographically valid commit signed with a different author email' {
        $null = Get-FixtureGit @('-c', 'user.email=wrong@example.invalid', 'commit', '--allow-empty', '-m', 'wrong author fixture')
        $result = Invoke-FixtureGit @('verify-commit', 'HEAD')
        $result.Code | Should -Be 1
        ($result.Output + $result.Error) | Should -Match 'Commit not signed by author <wrong@example.invalid>'
    }

    It 'rejects a cryptographically valid annotated tag signed with a different tagger email' {
        $null = Get-FixtureGit @('-c', 'user.email=wrong@example.invalid', 'tag', '-s', 'wrong-tagger', '-m', 'wrong tagger fixture', $validCommit)
        $result = Invoke-FixtureGit @('verify-tag', 'wrong-tagger')
        $result.Code | Should -Be 1
        ($result.Output + $result.Error) | Should -Match 'Commit not signed by author <wrong@example.invalid>'
    }

    It 'keeps principal discovery and structural checking separate from crypto and trust checks' {
        $commit = Get-CommitSignature $validCommit
        $sig = Join-Path $root 'commit.sig'
        [IO.File]::WriteAllText($sig, $commit.Signature)
        $find = Invoke-RngcsProcess $helper @('-Y', 'find-principals', '-s', $sig, '-f', $allowed)
        $find.Code | Should -Be 0 -Because $find.Error
        $find.Output.Trim() | Should -Be $identity
        $wrong = Invoke-RngcsProcess $helper @('-Y', 'verify', '-s', $sig, '-I', ('0' * 32)) $commit.Message
        $wrong.Code | Should -Be 1
        $wrong.Error.Trim() | Should -Be 'Principal mismatch'
        $changed = [byte[]]::new($commit.Message.Length + 1)
        [Array]::Copy($commit.Message, $changed, $commit.Message.Length)
        $changed[-1] = 1
        $check = Invoke-RngcsProcess $helper @('-Y', 'check-novalidate', '-s', $sig) $changed
        $check.Code | Should -Be 0 -Because $check.Error
        $verify = Invoke-RngcsProcess $helper @('-Y', 'verify', '-s', $sig) $changed
        $verify.Code | Should -Be 1
        $verify.Error.Trim() | Should -Be 'Invalid signature'
    }

    It 'preserves binary standard input and file signing behavior across separate processes' {
        $payload = [byte[]]@(0, 255, 128, 13, 10, 1, 2, 3)
        $stdin = Invoke-RngcsProcess $helper @('-Y', 'sign', '-f', $key, '-n', 'ignored', '-O', 'ignored', '-O', 'also-ignored') $payload
        $stdin.Code | Should -Be 0 -Because $stdin.Error
        $file = Join-Path $root 'binary signed input'
        [IO.File]::WriteAllBytes($file, $payload)
        $fileResult = Invoke-RngcsProcess $helper @('-Y', 'sign', '-f', $key, $file) ([byte[]]@(9, 9))
        $fileResult.Code | Should -Be 0 -Because $fileResult.Error
        $fileResult.Output | Should -Be ''
        [IO.File]::ReadAllText("$file.sig") | Should -Be $stdin.Output
    }

    It 'validates the actual dotnet Git commit signature with the pinned Python reference and reverse direction' {
        if (!$Python -or !$Upstream) {
            Set-ItResult -Skipped -Because 'Requires explicit Python/Upstream paths for the actual pinned reference, not a Python package from PATH.'
            return
        }
        $pythonExecutable = (Resolve-Path -LiteralPath $Python).Path
        $adapter = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\RngcsReference.py')).Path
        $commit = Get-CommitSignature $validCommit
        $sig = Join-Path $root 'reference crosscheck.sig'
        [IO.File]::WriteAllText($sig, $commit.Signature)
        # The Windows reference checkout has CRLF worktree files. Explicitly
        # restore checkout normalization for its clean-tree check, without
        # re-enabling system/global Git configuration or modifying the source.
        $referenceGit = @{
            GIT_CONFIG_COUNT = '1'
            GIT_CONFIG_KEY_0 = 'core.autocrlf'
            GIT_CONFIG_VALUE_0 = 'true'
        }
        $verified = Invoke-RngcsProcess $pythonExecutable @($adapter, '-Y', 'verify', '-s', $sig, '-I', $identity) $commit.Message -EnvironmentOverrides $referenceGit
        $verified.Code | Should -Be 0 -Because ($verified.Output + $verified.Error)
        $verified.Output | Should -Match "Good `"git`" signature.*<$identity>"
        $signed = Invoke-RngcsProcess $pythonExecutable @($adapter, '-Y', 'sign', '-f', $key) $commit.Message -EnvironmentOverrides $referenceGit
        $signed.Code | Should -Be 0 -Because $signed.Error
        $signed.Output.Replace("`r`n", "`n") | Should -Be $commit.Signature
        [IO.File]::WriteAllText($sig, $signed.Output)
        $reverse = Invoke-RngcsProcess $helper @('-Y', 'verify', '-s', $sig, '-I', $identity) $commit.Message
        $reverse.Code | Should -Be 0 -Because $reverse.Error
        $reverse.Output | Should -Be $verified.Output
    }
}
