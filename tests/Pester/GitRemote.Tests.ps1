param(
    [string] $Application,
    [string] $Python,
    [string] $Upstream
)

# Opt-in local live-reference acceptance, not evidence of hosted-upstream or
# multi-hop interoperability. The adapter checks actual imports and the pin.
BeforeAll {
    $server = $null
    $root = Join-Path $PSScriptRoot "..\..\TestResults\git-remote-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $root -Force
    $root = (Resolve-Path -LiteralPath $root).Path
    $git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $pythonExecutable = (Resolve-Path -LiteralPath $Python).Path
    $reference = (Resolve-Path -LiteralPath $Upstream).Path
    $pin = 'a29bfb51c468945c96a1ee22cae0114e993e2391'

    function Start-GitRemoteProcess {
        param(
            [string] $Executable,
            [string[]] $Arguments,
            [hashtable] $Environment = @{}
        )
        $info = [System.Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.WorkingDirectory = $root
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
        $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
        $info.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false, $true)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        foreach ($key in $Environment.Keys) {
            if ($null -eq $Environment[$key]) { $null = $info.Environment.Remove($key) }
            else { $info.Environment[$key] = $Environment[$key] }
        }
        [System.Diagnostics.Process]::Start($info)
    }

    function Stop-GitRemoteProcess {
        param([System.Diagnostics.Process] $Process)
        if ($null -eq $Process) { return }
        try {
            if (!$Process.HasExited) {
                $Process.Kill($true)
                if (!$Process.WaitForExit(5000)) { throw "Owned process $($Process.Id) did not terminate." }
            }
        }
        finally { $Process.Dispose() }
    }

    function Invoke-GitRemoteProcess {
        param(
            [string] $Executable,
            [string[]] $Arguments,
            [string] $InputText = '',
            [hashtable] $Environment = @{},
            [int] $TimeoutSeconds = 90
        )
        $process = Start-GitRemoteProcess $Executable $Arguments $Environment
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        try {
            if ($InputText) { $process.StandardInput.Write($InputText) }
            $process.StandardInput.Close()
            if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
                throw "Process $Executable timed out after ${TimeoutSeconds}s."
            }
            @{
                Code = $process.ExitCode
                Output = $output.GetAwaiter().GetResult()
                Error = $errorOutput.GetAwaiter().GetResult()
            }
        }
        finally { Stop-GitRemoteProcess $process }
    }

    $pathDirectory = Join-Path $root 'helper path'
    $null = New-Item -ItemType Directory -Path $pathDirectory
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $helper = Join-Path $pathDirectory "git-remote-rns$suffix"
    $applicationDirectory = Split-Path $cli -Parent
    $applicationBase = [System.IO.Path]::GetFileNameWithoutExtension($cli)
    $managed = [System.IO.Path]::GetExtension($cli) -eq '.dll' -or
        (Test-Path -LiteralPath (Join-Path $applicationDirectory "$applicationBase.runtimeconfig.json"))
    if ($managed) {
        $apphost = Join-Path $applicationDirectory "$applicationBase$suffix"
        Copy-Item -LiteralPath $apphost -Destination $helper
        # Renaming an apphost does not change its embedded managed DLL name.
        foreach ($extension in '.dll', '.deps.json', '.runtimeconfig.json') {
            Copy-Item -LiteralPath (Join-Path $applicationDirectory "$applicationBase$extension") -Destination $pathDirectory
        }
        # NuGet runtime assemblies/native libraries are still resolved by the
        # original deps.json; copy the remaining runtime payload, not a wrapper.
        foreach ($item in Get-ChildItem -LiteralPath $applicationDirectory -File) {
            if ($item.Name -eq "$applicationBase.dll" -or $item.Extension -notin @('.dll', '.so', '.dylib')) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $pathDirectory
        }
        $runtimes = Join-Path $applicationDirectory 'runtimes'
        if (Test-Path -LiteralPath $runtimes) { Copy-Item -LiteralPath $runtimes -Destination $pathDirectory -Recurse }
    }
    else { Copy-Item -LiteralPath $cli -Destination $helper }

    $config = Join-Path $root 'client config'
    $null = New-Item -ItemType Directory -Path $config
    $globalGitConfig = Join-Path $root 'empty git config'
    Set-Content -LiteralPath $globalGitConfig -Value '' -Encoding utf8NoBOM
    $environment = @{
        PATH = "$pathDirectory$([System.IO.Path]::PathSeparator)$env:PATH"
        DOTRETICULUM_RNS_CONFIG = $config
        RNS_CONFIG = $null
        DOTRETICULUM_RNGIT_TIMEOUT = '30'
        DOTRETICULUM_RNGIT_MAX_RESPONSE = '16777216'
        GIT_CONFIG_NOSYSTEM = '1'
        GIT_CONFIG_GLOBAL = $globalGitConfig
        GIT_CONFIG_COUNT = '0'
        GIT_TERMINAL_PROMPT = '0'
        GIT_DIR = $null
        GIT_WORK_TREE = $null
        GIT_INDEX_FILE = $null
        GIT_OBJECT_DIRECTORY = $null
        GIT_ALTERNATE_OBJECT_DIRECTORIES = $null
        GIT_EXEC_PATH = $null
    }

    function Invoke-FixtureGit {
        param([string[]] $Arguments, [int] $TimeoutSeconds = 90)
        Invoke-GitRemoteProcess $git (@('-c', 'safe.bareRepository=all') + $Arguments) -Environment $environment -TimeoutSeconds $TimeoutSeconds
    }

    function Get-FixtureGit {
        param([string[]] $Arguments)
        $result = Invoke-FixtureGit $Arguments
        if ($result.Code -ne 0) { throw "git $($Arguments -join ' ') failed: $($result.Error)$($result.Output)" }
        $result.Output.Trim()
    }

    function Assert-FixtureRepository {
        param([string] $Repository)
        $result = Invoke-FixtureGit @('-C', $Repository, 'fsck', '--full', '--strict')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $result.Error | Should -Not -Match 'missing|broken|error:|fatal:'
    }

    function Get-FixtureRefs {
        param([string] $Repository)
        $text = Get-FixtureGit @('-C', $Repository, 'show-ref')
        (($text -split '\r?\n' | Sort-Object) -join "`n")
    }

    function Invoke-Helper {
        param(
            [string] $InputText,
            [string] $Url = $remoteUrl,
            [hashtable] $Overrides = @{},
            [int] $TimeoutSeconds = 90
        )
        $childEnvironment = $environment.Clone()
        foreach ($key in $Overrides.Keys) { $childEnvironment[$key] = $Overrides[$key] }
        Invoke-GitRemoteProcess $helper @('origin', $Url) -InputText $InputText -Environment $childEnvironment -TimeoutSeconds $TimeoutSeconds
    }

    $fixtureTool = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\Interop\CreateRngitFixture.cs')).Path
    function Invoke-FixtureTool {
        param([string[]] $Arguments)
        $result = Invoke-GitRemoteProcess 'dotnet' (@('run', '--file', $fixtureTool, '--verbosity', 'quiet', '--') + $Arguments) `
            -Environment $environment -TimeoutSeconds 120
        if ($result.Code -ne 0) { throw "C# Git fixture failed: $($result.Error)$($result.Output)" }
        $result.Output | ConvertFrom-Json
    }
    $fixture = Invoke-FixtureTool @($root)
    $source = $fixture.Source
    $repositories = $fixture.Repositories
    $bareSource = $fixture.BareSource
    $initialCommit = $fixture.InitialCommit
    $initialTree = $fixture.InitialTree
    $annotatedTag = $fixture.AnnotatedTag
    $featureCommit = $fixture.FeatureCommit
    $unicodeBranch = $fixture.UnicodeBranch
    $unicodeTag = $fixture.UnicodeTag
    $unicodeBranch | Should -Be "branch-caf`u{00e9}"
    $unicodeTag | Should -Be "v-`u{00e9}"

    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = $portProbe.LocalEndpoint.Port
    $portProbe.Stop()
    @"
[reticulum]
  enable_transport = no
  share_instance = no
[interfaces]
  [[Loopback]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $config 'config') -Encoding utf8NoBOM

    $adapter = Join-Path $PSScriptRoot '..\Interop\live_rngit_node.py'
    $serverEnvironment = $environment.Clone()
    # Preserve the reference checkout's Git normalization settings for its
    # tracked-source verification (Windows checkouts may use core.autocrlf).
    $serverEnvironment['GIT_CONFIG_NOSYSTEM'] = $env:GIT_CONFIG_NOSYSTEM
    $serverEnvironment['GIT_CONFIG_GLOBAL'] = $env:GIT_CONFIG_GLOBAL
    $serverEnvironment['DOTRETICULUM_UPSTREAM'] = $reference
    $serverEnvironment['PYTHONPATH'] = $reference
    $serverEnvironment['PYTHONUNBUFFERED'] = '1'
    $serverEnvironment['PYTHONIOENCODING'] = 'utf-8'
    $serverEnvironment['PYTHONUTF8'] = '1'
    $serverEnvironment['PYTHONDONTWRITEBYTECODE'] = '1'
    $server = Start-GitRemoteProcess $pythonExecutable @(
        '-u', $adapter, '--config', (Join-Path $root 'python server'),
        '--tcp-server-port', "$port", '--repositories', $repositories
    ) $serverEnvironment
    $serverError = $server.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $ready = $null
    while ($null -eq $ready) {
        $lineTask = $server.StandardOutput.ReadLineAsync()
        $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (!$lineTask.Wait($remaining)) { throw 'Python rngit adapter did not become ready within 30 seconds.' }
        $line = $lineTask.GetAwaiter().GetResult()
        if ($null -eq $line) { throw "Python rngit adapter exited before readiness: $($serverError.GetAwaiter().GetResult())" }
        if ($line.StartsWith('@@')) { $ready = $line.Substring(2) | ConvertFrom-Json }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Python rngit readiness deadline exceeded.' }
    }
    $serverOutput = $server.StandardOutput.ReadToEndAsync()
    $ready.event | Should -Be 'ready'
    $ready.pin | Should -Be $pin
    [System.IO.Path]::GetFullPath($ready.source) | Should -Be (Join-Path $reference 'RNS\__init__.py')
    $ready.dest | Should -Match '^[0-9a-f]{32}$'
    $server.HasExited | Should -BeFalse
    $probe = [System.Net.Sockets.TcpClient]::new()
    try { $probe.Connect([System.Net.IPAddress]::Loopback, $port) }
    finally { $probe.Dispose() }
    $remoteUrl = "rns://$($ready.dest)/fixtures/fixture.git"
}

AfterAll {
    try { Stop-GitRemoteProcess $server }
    finally {
        if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
    # Only this suite's unique repository-local directory and processes are owned.
}

Describe 'Git-first remote helper against the pinned Python rngit server' -Tag E2E, GitRemote, Live {
    It 'also supports the explicit Applications git-remote-rns subcommand' {
        $executable = if ([System.IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
        $arguments = @(if ($executable -eq 'dotnet') { $cli }) + @('git-remote-rns', 'origin', $remoteUrl)
        $result = Invoke-GitRemoteProcess $executable $arguments -InputText "capabilities`n`n" -Environment $environment
        $result.Code | Should -Be 0 -Because $result.Error
        $protocol = $result.Output.Replace("`r`n", "`n")
        $protocol | Should -Match "`n`n$"
        (($protocol.TrimEnd("`n") -split "`n" | Sort-Object) -join "`n") | Should -Be "fetch`noption"
    }

    It 'keeps protocol stdout exact and negotiates only fetch and option capabilities' {
        $result = Invoke-Helper "capabilities`noption progress true`noption progress false`noption depth 1`n`n"
        $result.Code | Should -Be 0 -Because $result.Error
        $blocks = $result.Output.Replace("`r`n", "`n") -split "`n`n", 2
        $blocks.Count | Should -Be 2
        (($blocks[0] -split "`n" | Sort-Object) -join "`n") | Should -Be "fetch`noption"
        $blocks[1] | Should -Be "ok`nok`nunsupported`n"
    }

    It 'uses RNS_CONFIG as a fallback and advertises real refs and symbolic HEAD without stdout diagnostics' {
        $result = Invoke-Helper "list`n`n" -Overrides @{ DOTRETICULUM_RNS_CONFIG = $null; RNS_CONFIG = $config }
        $result.Code | Should -Be 0 -Because $result.Error
        $lines = $result.Output.Replace("`r`n", "`n")
        $lines | Should -Match '(?m)^@refs/heads/main HEAD$'
        $advertised = @($lines.TrimEnd("`n") -split "`n" | Where-Object { $_ -notmatch '^@' } | Sort-Object)
        $expected = @(
            "$initialCommit refs/heads/main",
            "$initialCommit refs/heads/$unicodeBranch",
            "$featureCommit refs/heads/feature",
            "$featureCommit refs/tags/feature-tip",
            "$initialCommit refs/tags/$unicodeTag",
            "$annotatedTag refs/tags/v1"
        )
        # A peeled annotated tag is optional protocol metadata, not a stored ref.
        $advertised = @($advertised | Where-Object { $_ -notmatch '\^\{\}$' })
        ($advertised -join "`n") | Should -Be (($expected | Sort-Object) -join "`n")
        $lines | Should -Match "`n`n$"
    }

    It 'clones a working tree with Git-owned HEAD, remote tracking, branches and both tag forms' {
        $target = Join-Path $root 'working clone'
        $result = Invoke-FixtureGit @('clone', $remoteUrl, $target)
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $target, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        Get-FixtureGit @('-C', $target, 'symbolic-ref', 'refs/remotes/origin/HEAD') | Should -Be 'refs/remotes/origin/main'
        Get-FixtureGit @('-C', $target, 'remote', 'get-url', 'origin') | Should -Be $remoteUrl
        Get-FixtureGit @('-C', $target, 'rev-parse', 'HEAD^{tree}') | Should -Be $initialTree
        [System.IO.File]::ReadAllText((Join-Path $target 'readme.txt')) | Should -Be "independent main fixture`n"
        Get-FixtureRefs $target | Should -Be ((@(
            "$initialCommit refs/heads/main",
            "$initialCommit refs/remotes/origin/HEAD",
            "$initialCommit refs/remotes/origin/main",
            "$initialCommit refs/remotes/origin/$unicodeBranch",
            "$featureCommit refs/remotes/origin/feature",
            "$featureCommit refs/tags/feature-tip",
            "$initialCommit refs/tags/$unicodeTag",
            "$annotatedTag refs/tags/v1"
        ) | Sort-Object) -join "`n")
        Assert-FixtureRepository $target
    }

    It 'clones bare repositories and honors a nondefault feature branch selected by Git' {
        $bare = Join-Path $root 'bare clone.git'
        $result = Invoke-FixtureGit @('clone', '--bare', $remoteUrl, $bare)
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $bare, 'rev-parse', '--is-bare-repository') | Should -Be 'true'
        Get-FixtureGit @('-C', $bare, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        Get-FixtureRefs $bare | Should -Be (Get-FixtureRefs $bareSource)
        Assert-FixtureRepository $bare
        $feature = Join-Path $root 'feature clone'
        $result = Invoke-FixtureGit @('clone', '-b', 'feature', $remoteUrl, $feature)
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $feature, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/feature'
        Get-FixtureGit @('-C', $feature, 'rev-parse', 'HEAD') | Should -Be $featureCommit
        [System.IO.File]::ReadAllText((Join-Path $feature 'feature.txt')) | Should -Be "nondefault branch fixture`n"
        Get-FixtureRefs $feature | Should -Be ((@(
            "$featureCommit refs/heads/feature",
            "$initialCommit refs/remotes/origin/HEAD",
            "$initialCommit refs/remotes/origin/main",
            "$initialCommit refs/remotes/origin/$unicodeBranch",
            "$featureCommit refs/remotes/origin/feature",
            "$featureCommit refs/tags/feature-tip",
            "$initialCommit refs/tags/$unicodeTag",
            "$annotatedTag refs/tags/v1"
        ) | Sort-Object) -join "`n")
        Assert-FixtureRepository $feature
    }

    It 'fetches previously absent commit and tree objects, preserves a diverged local branch, and tolerates no-change fetch' {
        $target = Join-Path $root 'incremental clone'
        $null = Get-FixtureGit @('clone', $remoteUrl, $target)
        $bare = Join-Path $root 'incremental bare.git'
        $null = Get-FixtureGit @('clone', '--bare', $remoteUrl, $bare)
        (Invoke-FixtureGit @("--git-dir=$bare", 'config', '--get-all', 'remote.origin.fetch')).Code | Should -Be 1
        $incremental = Invoke-FixtureTool @($root, 'incremental', $target)
        $localCommit = $incremental.LocalCommit
        $nextCommit = $incremental.NextCommit
        $nextTree = $incremental.NextTree
        foreach ($missing in $nextCommit, $nextTree) {
            (Invoke-FixtureGit @('-C', $target, 'cat-file', '-e', $missing)).Code | Should -Not -Be 0
            (Invoke-FixtureGit @("--git-dir=$bare", 'cat-file', '-e', $missing)).Code | Should -Not -Be 0
        }
        $result = Invoke-FixtureGit @('-C', $target, 'fetch', '--verbose', 'origin')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $bundle = [regex]::Match($result.Error, 'Received ([0-9]+) bundle bytes')
        $bundle.Success | Should -BeTrue -Because 'missing-object fetch must report actual received bundle bytes'
        [long]$bundle.Groups[1].Value | Should -BeGreaterThan 0
        Get-FixtureGit @('-C', $target, 'rev-parse', 'refs/remotes/origin/main') | Should -Be $nextCommit
        Get-FixtureGit @('-C', $target, 'rev-parse', "$nextCommit^{tree}") | Should -Be $nextTree
        Get-FixtureGit @('-C', $target, 'rev-parse', 'HEAD') | Should -Be $localCommit
        Get-FixtureGit @('-C', $target, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        [System.IO.File]::ReadAllText((Join-Path $target 'local.txt')) | Should -Be "local branch must survive fetch`n"
        Test-Path -LiteralPath (Join-Path $target 'next.txt') | Should -BeFalse
        Get-FixtureRefs $target | Should -Be ((@(
            "$localCommit refs/heads/main",
            "$nextCommit refs/remotes/origin/HEAD",
            "$nextCommit refs/remotes/origin/main",
            "$nextCommit refs/remotes/origin/$unicodeBranch",
            "$featureCommit refs/remotes/origin/feature",
            "$featureCommit refs/tags/feature-tip",
            "$initialCommit refs/tags/$unicodeTag",
            "$annotatedTag refs/tags/v1",
            "$nextCommit refs/tags/v2"
        ) | Sort-Object) -join "`n")
        Assert-FixtureRepository $target
        $before = Get-FixtureRefs $target
        $result = Invoke-FixtureGit @('-C', $target, 'fetch', 'origin')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureRefs $target | Should -Be $before
        Assert-FixtureRepository $target
        $result = Invoke-FixtureGit @("--git-dir=$bare", 'fetch', 'origin')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $bundle = [regex]::Match($result.Error, 'Received ([0-9]+) bundle bytes')
        $bundle.Success | Should -BeTrue -Because 'bare missing-object fetch must report received bundle bytes'
        [long]$bundle.Groups[1].Value | Should -BeGreaterThan 0
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', 'FETCH_HEAD') | Should -Be $nextCommit
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', 'FETCH_HEAD^{tree}') | Should -Be $nextTree
        Get-FixtureGit @("--git-dir=$bare", 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', 'refs/heads/main') | Should -Be $initialCommit
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', 'refs/heads/feature') | Should -Be $featureCommit
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', "refs/heads/$unicodeBranch") | Should -Be $initialCommit
        Get-FixtureGit @("--git-dir=$bare", 'rev-parse', "refs/tags/$unicodeTag") | Should -Be $initialCommit
        (Invoke-FixtureGit @("--git-dir=$bare", 'config', '--get-all', 'remote.origin.fetch')).Code | Should -Be 1
        Assert-FixtureRepository $bare
    }

    It 'rejects unsupported or malformed helper commands with stderr and a nonzero exit' -ForEach @(
        @{ Command = "push refs/heads/main:refs/heads/main`n`n" },
        @{ Command = "connect git-upload-pack`n`n" },
        @{ Command = "not-a-command`n`n" },
        @{ Command = "fetch not-a-sha refs/heads/main`n`n" },
        @{ Command = "fetch`n`n" }
    ) {
        $result = Invoke-Helper $Command
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -BeNullOrEmpty
    }

    It 'fails ordinary Git push without changing server refs' {
        $target = Join-Path $root 'push rejection'
        $null = Get-FixtureGit @('clone', $source, $target)
        $before = Get-FixtureRefs $bareSource
        $result = Invoke-FixtureGit @('-C', $target, 'push', $remoteUrl, "$initialCommit`:refs/heads/rejected")
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        Get-FixtureRefs $bareSource | Should -Be $before
    }

    It 'rejects invalid URLs through ordinary Git without creating a usable clone' -ForEach @(
        @{ Suffix = '/fixtures'; Name = 'missing repository' },
        @{ Suffix = '/fixtures/fixture.git/extra'; Name = 'extra segment' }
    ) {
        $target = Join-Path $root $Name
        $result = Invoke-FixtureGit @('clone', "rns://$($ready.dest)$Suffix", $target)
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $target '.git\HEAD') | Should -BeFalse
    }

    It 'rejects a malformed destination and an access-denied repository with Git failure diagnostics' {
        foreach ($url in @('rns://not-a-hash/fixtures/fixture.git', "rns://$($ready.dest)/hidden/fixture.git")) {
            $result = Invoke-FixtureGit @('ls-remote', $url)
            $result.Code | Should -Not -Be 0
            $result.Error | Should -Not -BeNullOrEmpty
            $result.Output | Should -BeNullOrEmpty
        }
    }

    It 'bounds an unreachable-destination failure using the configured helper timeout' {
        $childEnvironment = $environment.Clone()
        $childEnvironment['DOTRETICULUM_RNGIT_TIMEOUT'] = '2'
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-GitRemoteProcess $git @('ls-remote', 'rns://00000000000000000000000000000000/fixtures/fixture.git') `
            -Environment $childEnvironment -TimeoutSeconds 20
        $watch.Stop()
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -BeNullOrEmpty
        $watch.Elapsed.TotalSeconds | Should -BeLessThan 20
    }

    It 'refuses an oversized response using a process-scoped response limit' {
        $result = Invoke-Helper "list`n`n" -Overrides @{ DOTRETICULUM_RNGIT_MAX_RESPONSE = '1' }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -BeNullOrEmpty
        # A fresh process retains the normal limit, proving the override did not leak.
        $normal = Invoke-Helper "list`n`n"
        $normal.Code | Should -Be 0 -Because $normal.Error
        $normal.Output | Should -Match '(?m)^@refs/heads/main HEAD\r?$'
    }

    It 'times out an idle helper even while its stdin pipe remains open' {
        $childEnvironment = $environment.Clone()
        $childEnvironment['DOTRETICULUM_RNGIT_TIMEOUT'] = '2'
        $process = Start-GitRemoteProcess $helper @('origin', $remoteUrl) $childEnvironment
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        try {
            # Do not write to or close stdin: this exercises an outstanding read,
            # not clean EOF or a network timeout after a list command.
            if (!$process.WaitForExit(10000)) { throw 'Idle helper did not exit within 10 seconds.' }
            $process.ExitCode | Should -Be 1
            $output.GetAwaiter().GetResult() | Should -BeNullOrEmpty
            $errorOutput.GetAwaiter().GetResult() | Should -Match '(?i)timed out|timeout'
        }
        finally { Stop-GitRemoteProcess $process }
    }
}
