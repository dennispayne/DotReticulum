param(
    [string] $Application,
    [string] $Python,
    [string] $Upstream
)

# Opt-in local live-reference acceptance, not evidence of hosted-upstream or
# multi-hop interoperability. The adapter checks actual imports and the pin.
BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $server = $null
    $script:restartServer = $null
    $root = Join-Path $PSScriptRoot "..\..\TestResults\git-remote-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $root -Force
    $root = (Resolve-Path -LiteralPath $root).Path
    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
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
    $applicationDirectory = Split-Path $cli -Parent
    $applicationBase = [System.IO.Path]::GetFileNameWithoutExtension($cli)
    $managed = [System.IO.Path]::GetExtension($cli) -eq '.dll' -or
        (Test-Path -LiteralPath (Join-Path $applicationDirectory "$applicationBase.runtimeconfig.json"))
    $helper = Install-TestGitHelper -Application $cli -Directory $pathDirectory

    $config = Join-Path $root 'client config'
    $null = New-Item -ItemType Directory -Path $config
    $rngitConfig = Join-Path $root 'rngit client'
    $null = New-Item -ItemType Directory -Path $rngitConfig
    $scratch = Join-Path $root 'scratch'
    $null = New-Item -ItemType Directory -Path $scratch
    $globalGitConfig = Join-Path $root 'empty git config'
    Set-Content -LiteralPath $globalGitConfig -Value '' -Encoding utf8NoBOM
    $environment = @{
        PATH = "$pathDirectory$([System.IO.Path]::PathSeparator)$env:PATH"
        DOTRETICULUM_RNS_CONFIG = $config
        RNGIT_CONFIG = $rngitConfig
        TEMP = $scratch
        TMP = $scratch
        TMPDIR = $scratch
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
        $result = Invoke-GitRemoteProcess 'dotnet' (@('run', '--disable-build-servers', '--file', $fixtureTool, '--verbosity', 'quiet', '--') + $Arguments) `
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
    $pushBare = $fixture.PushBare
    $pushClone = Join-Path $root 'push clone'
    $pushFixture = Invoke-FixtureTool @($root, 'push', $pushClone)
    $unicodeBranch | Should -Be "branch-caf`u{00e9}"
    $unicodeTag | Should -Be "v-`u{00e9}"
    $identityPath = Join-Path $rngitConfig 'client_identity'
    $identityExecutable = if ([System.IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $identityArguments = @(if ($identityExecutable -eq 'dotnet') { $cli }) +
        @('rnid', '--generate', $identityPath)
    $identityResult = Invoke-GitRemoteProcess $identityExecutable $identityArguments -Environment $environment
    if ($identityResult.Code -ne 0) { throw "Fixture identity generation failed: $($identityResult.Error)" }
    $writer = [regex]::Match($identityResult.Output, 'Identity hash: ([0-9a-f]{32})').Groups[1].Value
    $writer | Should -Match '^[0-9a-f]{32}$'

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
        '--tcp-server-port', "$port", '--repositories', $repositories, '--writer', $writer
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
    $pushUrl = "rns://$($ready.dest)/fixtures/push.git"
    @"
[aliases]
  fixture = $($ready.dest)
"@ | Set-Content -LiteralPath (Join-Path $rngitConfig 'client_config') -Encoding utf8NoBOM
}

AfterAll {
    try {
        Stop-GitRemoteProcess $server
        Stop-GitRemoteProcess $script:restartServer
    }
    finally {
        if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
    # Only this suite's unique repository-local directory and processes are owned.
}

Describe 'Git-first remote helper against the pinned Python rngit server' -Tag E2E, GitRemote, Live, 'Contract:UTIL-RNGIT-002', 'Reference' {
    It 'also supports the explicit Applications git-remote-rns subcommand' {
        $executable = if ([System.IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
        $arguments = @(if ($executable -eq 'dotnet') { $cli }) + @('git-remote-rns', 'origin', $remoteUrl)
        $result = Invoke-GitRemoteProcess $executable $arguments -InputText "capabilities`n`n" -Environment $environment
        $result.Code | Should -Be 0 -Because $result.Error
        $protocol = $result.Output.Replace("`r`n", "`n")
        $protocol | Should -Match "`n`n$"
        (($protocol.TrimEnd("`n") -split "`n" | Sort-Object) -join "`n") | Should -Be "fetch`noption`npush"
    }

    It 'keeps protocol stdout exact and negotiates fetch, push and option capabilities' {
        $result = Invoke-Helper "capabilities`noption progress true`noption progress false`noption depth 1`n`n"
        $result.Code | Should -Be 0 -Because $result.Error
        $blocks = $result.Output.Replace("`r`n", "`n") -split "`n`n", 2
        $blocks.Count | Should -Be 2
        (($blocks[0] -split "`n" | Sort-Object) -join "`n") | Should -Be "fetch`noption`npush"
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

    It 'resolves configured aliases from the isolated RNGIT_CONFIG directory' {
        $result = Invoke-FixtureGit @('ls-remote', 'rns://fixture/fixtures/fixture.git')
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '(?m)^[0-9a-f]{40}\s+refs/heads/main\r?$'
        Test-Path -LiteralPath $identityPath | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $config 'storage\rngit\client_identity') | Should -BeFalse
    }

    It 'advertises capabilities offline without initializing network or identity state' {
        $isolated = Join-Path $root 'offline rngit'
        $result = Invoke-Helper "capabilities`n`n" -Url 'rns://fixture/fixtures/fixture.git' -Overrides @{
            RNGIT_CONFIG = $isolated
            DOTRETICULUM_RNS_CONFIG = (Join-Path $root 'offline network')
        }
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output.Replace("`r`n", "`n") | Should -Be "fetch`npush`noption`n`n"
        Test-Path -LiteralPath (Join-Path $isolated 'client_identity') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $root 'offline network') | Should -BeFalse
    }

    It 'pushes absent branch and lightweight, annotated and Unicode tag objects through ordinary Git' {
        $localBefore = Get-FixtureRefs $pushClone
        foreach ($object in @($pushFixture.FirstCommit, $pushFixture.AnnotatedTag)) {
            (Invoke-FixtureGit @('-C', $pushBare, 'cat-file', '-e', $object)).Code | Should -Not -Be 0
        }
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--porcelain', $pushUrl,
            "$($pushFixture.FirstCommit):refs/heads/pushed",
            'refs/tags/push-light', 'refs/tags/push-annotated', "refs/tags/push-`u{00e9}")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $result.Error | Should -Match 'Sent [1-9][0-9]* bundle bytes'
        foreach ($ref in @('refs/heads/pushed', 'refs/tags/push-light', "refs/tags/push-`u{00e9}")) {
            Get-FixtureGit @('-C', $pushBare, 'rev-parse', $ref) | Should -Be $pushFixture.FirstCommit
        }
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/tags/push-annotated') | Should -Be $pushFixture.AnnotatedTag
        Get-FixtureGit @('-C', $pushBare, 'cat-file', '-t', 'refs/tags/push-annotated') | Should -Be 'tag'
        Get-FixtureRefs $pushClone | Should -Be $localBefore
        @(Get-ChildItem -LiteralPath (Join-Path $pushClone '.git') -Directory -Filter 'rngit-push-*').Count | Should -Be 0
        Assert-FixtureRepository $pushBare
    }

    It 'initializes an unborn SHA1 remote through ordinary Git push and subsequently clones and fetches it' {
        $emptyBare = $fixture.EmptyBare
        $emptyUrl = "rns://$($ready.dest)/fixtures/empty.git"
        Get-FixtureGit @('-C', $emptyBare, 'rev-parse', '--show-object-format') | Should -Be 'sha1'
        Get-FixtureGit @('-C', $emptyBare, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        (Invoke-FixtureGit @('-C', $emptyBare, 'show-ref')).Code | Should -Be 1
        (Invoke-FixtureGit @('-C', $emptyBare, 'cat-file', '-e', $pushFixture.FirstCommit)).Code | Should -Not -Be 0
        $localBefore = Get-FixtureRefs $pushClone
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--porcelain', $emptyUrl,
            "$($pushFixture.FirstCommit):refs/heads/main")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $result.Error | Should -Match 'Sent [1-9][0-9]* bundle bytes'
        Get-FixtureGit @('-C', $emptyBare, 'rev-parse', 'refs/heads/main') | Should -Be $pushFixture.FirstCommit
        Get-FixtureGit @('-C', $emptyBare, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        Get-FixtureRefs $pushClone | Should -Be $localBefore
        Assert-FixtureRepository $emptyBare
        $target = Join-Path $root 'initial-push clone'
        $result = Invoke-FixtureGit @('clone', $emptyUrl, $target)
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $target, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        Get-FixtureGit @('-C', $target, 'rev-parse', 'HEAD') | Should -Be $pushFixture.FirstCommit
        [System.IO.File]::ReadAllText((Join-Path $target 'push.txt')) |
            Should -Be "new independently generated push object`n"
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $emptyUrl,
            "$($pushFixture.SecondCommit):refs/heads/main")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $result = Invoke-FixtureGit @('-C', $target, 'fetch', 'origin')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $target, 'rev-parse', 'refs/remotes/origin/main') | Should -Be $pushFixture.SecondCommit
        Get-FixtureGit @('-C', $target, 'rev-parse', 'HEAD') | Should -Be $pushFixture.FirstCommit
        Assert-FixtureRepository $target
        Assert-FixtureRepository $emptyBare
        @(Get-ChildItem -LiteralPath (Join-Path $pushClone '.git') -Directory -Filter 'rngit-push-*').Count | Should -Be 0
    }

    It 'updates branches with new objects and rejects a non-fast-forward until explicitly forced' {
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $pushUrl,
            "$($pushFixture.SecondCommit):refs/heads/pushed")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/pushed') | Should -Be $pushFixture.SecondCommit
        $before = Get-FixtureRefs $pushBare
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $pushUrl,
            "$($pushFixture.DivergentCommit):refs/heads/pushed")
        $result.Code | Should -Not -Be 0
        Get-FixtureRefs $pushBare | Should -Be $before
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--force', $pushUrl,
            "$($pushFixture.DivergentCommit):refs/heads/pushed")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/pushed') | Should -Be $pushFixture.DivergentCommit
        Assert-FixtureRepository $pushBare
    }

    It 'creates existing-object refs and preserves the upstream changed-SHA force rule even for fast-forward' {
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $pushUrl,
            "$initialCommit`:refs/heads/existing", "$initialCommit`:refs/tags/existing")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        $before = Get-FixtureRefs $pushBare
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $pushUrl,
            "$featureCommit`:refs/heads/existing")
        $result.Code | Should -Not -Be 0
        Get-FixtureRefs $pushBare | Should -Be $before
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--force', $pushUrl,
            "$featureCommit`:refs/heads/existing", "$featureCommit`:refs/tags/existing")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        foreach ($ref in @('refs/heads/existing', 'refs/tags/existing')) {
            Get-FixtureGit @('-C', $pushBare, 'rev-parse', $ref) | Should -Be $featureCommit
        }
    }

    It 'reports individual helper statuses and completes a successful ref after a rejected ref in one batch' {
        $result = Invoke-Helper "list for-push`npush $featureCommit`:refs/tags/locked`npush $initialCommit`:refs/heads/partial-success`n`n`n" `
            -Url $pushUrl -Overrides @{ GIT_DIR = (Join-Path $pushClone '.git') }
        $result.Code | Should -Be 0 -Because $result.Error
        $protocol = ($result.Output.Replace("`r`n", "`n") -split "`n`n", 2)[1]
        $protocol | Should -Match '(?m)^error refs/tags/locked .+$'
        $protocol | Should -Match '(?m)^ok refs/heads/partial-success$'
        $protocol | Should -Match "`n`n$"
        @($protocol.TrimEnd("`n") -split "`n").Count | Should -Be 2
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/tags/locked') | Should -Be $initialCommit
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/partial-success') | Should -Be $initialCommit
    }

    It 'returns a nonzero actual Git exit while reporting rejected and successful refs individually' {
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--porcelain', $pushUrl,
            "$featureCommit`:refs/tags/locked", "$initialCommit`:refs/heads/git-partial-success")
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match '(?m)^!\s+.*refs/tags/locked'
        $result.Output | Should -Match '(?m)^\*\s+.*refs/heads/git-partial-success'
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/tags/locked') | Should -Be $initialCommit
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/git-partial-success') | Should -Be $initialCommit
    }

    It 'reports a missing local source per ref and still applies the following valid source' {
        $result = Invoke-Helper "list for-push`npush refs/heads/nonexistent-local-source:refs/heads/missing-source`npush $initialCommit`:refs/heads/after-missing-source`n`n`n" `
            -Url $pushUrl -Overrides @{ GIT_DIR = (Join-Path $pushClone '.git') }
        $result.Code | Should -Be 0 -Because $result.Error
        $protocol = ($result.Output.Replace("`r`n", "`n") -split "`n`n", 2)[1]
        $protocol | Should -Match '(?m)^error refs/heads/missing-source .+$'
        $protocol | Should -Match '(?m)^ok refs/heads/after-missing-source$'
        @($protocol.TrimEnd("`n") -split "`n").Count | Should -Be 2
        (Invoke-FixtureGit @('-C', $pushBare, 'show-ref', '--verify', 'refs/heads/missing-source')).Code |
            Should -Not -Be 0
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/after-missing-source') | Should -Be $initialCommit
        Assert-FixtureRepository $pushBare
    }

    It 'rejects a one-byte push bundle limit per ref before remote mutation and removes local staging' {
        $before = Get-FixtureRefs $pushBare
        $sourceBefore = Get-FixtureRefs $bareSource
        $localBefore = Get-FixtureRefs $pushClone
        $result = Invoke-Helper "list for-push`npush $($pushFixture.SecondCommit):refs/heads/capped-bundle`n`n`n" `
            -Url $remoteUrl -Overrides @{
                GIT_DIR = (Join-Path $pushClone '.git')
                DOTRETICULUM_RNGIT_MAX_PUSH = '1'
            }
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '(?m)^error refs/heads/capped-bundle .*byte limit'
        Get-FixtureRefs $pushBare | Should -Be $before
        Get-FixtureRefs $bareSource | Should -Be $sourceBefore
        (Invoke-FixtureGit @('-C', $bareSource, 'show-ref', '--verify', 'refs/heads/capped-bundle')).Code | Should -Not -Be 0
        Get-FixtureRefs $pushClone | Should -Be $localBefore
        @(Get-ChildItem -LiteralPath (Join-Path $pushClone '.git') -Directory -Filter 'rngit-push-*').Count | Should -Be 0
    }

    It 'fetches a fresh working repository with an explicitly configured one-ref batch' {
        $batchConfig = Join-Path $root 'batch-one config'
        $null = New-Item -ItemType Directory -Path $batchConfig
        "[client]`nref_batch_size = 1`n" |
            Set-Content -LiteralPath (Join-Path $batchConfig 'client_config') -Encoding utf8NoBOM
        $childEnvironment = $environment.Clone()
        $childEnvironment['RNGIT_CONFIG'] = $batchConfig
        $target = Join-Path $root 'batch-one clone'
        $result = Invoke-GitRemoteProcess $git @('clone', $remoteUrl, $target) -Environment $childEnvironment
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureRefs $target | Should -Match 'refs/remotes/origin/feature'
        Get-FixtureGit @('-C', $target, 'rev-parse', 'refs/tags/v1') | Should -Be $annotatedTag
        Assert-FixtureRepository $target
    }

    It 'writes and rotates only the isolated client diagnostic log without polluting protocol stdout' {
        $logPath = Join-Path $rngitConfig 'client_log'
        $stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Create)
        try { $stream.SetLength(5 * 1024 * 1024 + 1) }
        finally { $stream.Dispose() }
        $result = Invoke-Helper "list for-push`npush $featureCommit`:refs/tags/locked`n`n`n" `
            -Url $pushUrl -Overrides @{ GIT_DIR = (Join-Path $pushClone '.git') }
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '(?m)^error refs/tags/locked '
        $result.Output | Should -Not -Match '(?m)^\[|git-remote-rns:|Resolving|Windows'
        Test-Path -LiteralPath "$logPath.1" | Should -BeTrue
        (Get-Item -LiteralPath "$logPath.1").Length | Should -BeGreaterThan (5 * 1024 * 1024)
        [System.IO.File]::ReadAllText($logPath) | Should -Match 'Push refs/tags/locked'
    }

    It 'rejects invalid or unbounded client configuration before advertising any refs' -ForEach @(
        @{ Contents = "[client]`nref_batch_size = 0`n"; Name = 'zero' },
        @{ Contents = "[client]`nref_batch_size = -1`n"; Name = 'negative' },
        @{ Contents = "[client]`nref_batch_size = not-an-integer`n"; Name = 'text' },
        @{ Contents = "[client]`nref_batch_size = 2147483648`n"; Name = 'overflow' },
        @{ Contents = "[aliases]`nfixture = not-a-hash`n"; Name = 'bad-alias' },
        @{ Contents = "[client]`nref_batch_size = 1`nref_batch_size = 2`n"; Name = 'duplicate' },
        @{ Contents = "[client]`nunknown = " + ('x' * 8193); Name = 'oversized-line' }
    ) {
        $invalidConfig = Join-Path $root "invalid config $Name"
        $null = New-Item -ItemType Directory -Path $invalidConfig
        $Contents | Set-Content -LiteralPath (Join-Path $invalidConfig 'client_config') -Encoding utf8NoBOM
        $result = Invoke-Helper "list`n`n" -Overrides @{ RNGIT_CONFIG = $invalidConfig }
        $result.Code | Should -Not -Be 0
        $result.Output | Should -BeNullOrEmpty
        $result.Error | Should -Match '(?i)client_config'
        Test-Path -LiteralPath (Join-Path $invalidConfig 'client_identity') | Should -BeFalse
    }

    It 'rejects unsupported dry-run and atomic requests without changing any remote refs' -ForEach @(
        @{ Option = '--dry-run'; Ref = 'dry-run' },
        @{ Option = '--atomic'; Ref = 'atomic' }
    ) {
        $before = Get-FixtureRefs $pushBare
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $Option, $pushUrl,
            "$initialCommit`:refs/heads/$Ref")
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        Get-FixtureRefs $pushBare | Should -Be $before
    }

    It 'rejects duplicate destinations and mixed transfer batches before any mutation' -ForEach @(
        @{ Batch = "push INITIAL:refs/heads/duplicate`npush INITIAL:refs/heads/duplicate`n`n`n" },
        @{ Batch = "push INITIAL:refs/heads/mixed`nfetch INITIAL refs/heads/main`n`n`n" }
    ) {
        $before = Get-FixtureRefs $pushBare
        $commands = "list for-push`n" + $Batch.Replace('INITIAL', $initialCommit)
        $result = Invoke-Helper $commands -Url $pushUrl -Overrides @{ GIT_DIR = (Join-Path $pushClone '.git') }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -Not -Match '(?m)^(ok|error) refs/'
        Get-FixtureRefs $pushBare | Should -Be $before
    }

    It 'rejects EOF before the push batch terminator without applying a queued ref' {
        $before = Get-FixtureRefs $pushBare
        $result = Invoke-Helper "list for-push`npush $initialCommit`:refs/heads/incomplete`n" `
            -Url $pushUrl -Overrides @{ GIT_DIR = (Join-Path $pushClone '.git') }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match '(?i)EOF|terminator'
        $result.Output | Should -Not -Match '(?m)^(ok|error) refs/'
        Get-FixtureRefs $pushBare | Should -Be $before
    }

    It 'times out a queued unterminated push without mutation, status fabrication or staging artifacts' {
        $before = Get-FixtureRefs $pushBare
        $childEnvironment = $environment.Clone()
        $childEnvironment['DOTRETICULUM_RNGIT_TIMEOUT'] = '3'
        $childEnvironment['GIT_DIR'] = Join-Path $pushClone '.git'
        $process = Start-GitRemoteProcess $helper @('origin', $pushUrl) $childEnvironment
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        try {
            $process.StandardInput.Write("list for-push`npush $initialCommit`:refs/heads/pending-timeout`n")
            $process.StandardInput.Flush()
            $process.WaitForExit(15000) | Should -BeTrue
            $process.ExitCode | Should -Be 1
            $output.GetAwaiter().GetResult() | Should -Not -Match '(?m)^(ok|error) refs/'
            $errorOutput.GetAwaiter().GetResult() | Should -Match '(?i)timeout|timed out'
        }
        finally { Stop-GitRemoteProcess $process }
        Get-FixtureRefs $pushBare | Should -Be $before
        @(Get-ChildItem -LiteralPath (Join-Path $pushClone '.git') -Directory -Filter 'rngit-push-*').Count | Should -Be 0
    }

    It 'deletes branches and tags through ordinary Git without deleting their objects' {
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', '--porcelain', $pushUrl,
            ':refs/heads/existing', ':refs/tags/existing')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        foreach ($ref in @('refs/heads/existing', 'refs/tags/existing')) {
            (Invoke-FixtureGit @('-C', $pushBare, 'show-ref', '--verify', $ref)).Code | Should -Not -Be 0
        }
        (Invoke-FixtureGit @('-C', $pushBare, 'cat-file', '-e', $featureCommit)).Code | Should -Be 0
        Assert-FixtureRepository $pushBare
    }

    It 'isolates simultaneous writers to distinct refs and leaves no locks or bundle artifacts' {
        $processes = @()
        try {
            foreach ($name in @('concurrent-one', 'concurrent-two')) {
                $process = Start-GitRemoteProcess $git @('-c', 'safe.bareRepository=all', '-C', $pushClone,
                    'push', $pushUrl, "$initialCommit`:refs/heads/$name") $environment
                $processes += @{
                    Process = $process
                    Output = $process.StandardOutput.ReadToEndAsync()
                    Error = $process.StandardError.ReadToEndAsync()
                }
                $process.StandardInput.Close()
            }
            foreach ($item in $processes) {
                $item.Process.WaitForExit(90000) | Should -BeTrue
                $item.Process.ExitCode | Should -Be 0 -Because (
                    $item.Error.GetAwaiter().GetResult() + $item.Output.GetAwaiter().GetResult())
            }
        }
        finally { foreach ($item in $processes) { Stop-GitRemoteProcess $item.Process } }
        foreach ($name in @('concurrent-one', 'concurrent-two')) {
            Get-FixtureGit @('-C', $pushBare, 'rev-parse', "refs/heads/$name") | Should -Be $initialCommit
        }
        @(Get-ChildItem -LiteralPath $pushBare -Recurse -Filter '*.lock').Count | Should -Be 0
        @(Get-ChildItem -LiteralPath $scratch -Recurse -Filter '*.bundle').Count | Should -Be 0
        Assert-FixtureRepository $pushBare
    }

    It 'rejects hidden reads and writes without revealing refs or mutating the repository' {
        $hiddenUrl = "rns://$($ready.dest)/hidden/push.git"
        $before = Get-FixtureRefs $pushBare
        $result = Invoke-FixtureGit @('ls-remote', $hiddenUrl)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -BeNullOrEmpty
        $result.Error | Should -Not -BeNullOrEmpty
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $hiddenUrl,
            "$initialCommit`:refs/heads/hidden-write")
        $result.Code | Should -Not -Be 0
        Get-FixtureRefs $pushBare | Should -Be $before
    }

    It 'rejects actual SHA256 remote and local repositories explicitly without modifying refs' {
        Get-FixtureGit @('-C', $fixture.Sha256Bare, 'rev-parse', '--show-object-format') | Should -Be 'sha256'
        $before = Get-FixtureRefs $fixture.Sha256Bare
        $result = Invoke-FixtureGit @('ls-remote', "rns://$($ready.dest)/fixtures/sha256.git")
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match '(?i)sha.?256|sha.?1|object.format'
        $result.Output | Should -BeNullOrEmpty
        $sha256Commit = Get-FixtureGit @('-C', $fixture.Sha256Source, 'rev-parse', 'HEAD')
        $result = Invoke-FixtureGit @('-C', $fixture.Sha256Source, 'push', $pushUrl,
            "$sha256Commit`:refs/heads/sha256-rejected")
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match '(?i)sha.?256|sha.?1|object.format'
        Get-FixtureRefs $fixture.Sha256Bare | Should -Be $before
        (Invoke-FixtureGit @('-C', $pushBare, 'show-ref', '--verify', 'refs/heads/sha256-rejected')).Code | Should -Not -Be 0
    }

    It 'rejects unsupported or malformed helper commands with stderr and a nonzero exit' -ForEach @(
        @{ Command = "push missing-colon`n`n" },
        @{ Command = "push refs/heads/main:refs/heads/../invalid`n`n" },
        @{ Command = "push :`n`n" },
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

    It 'denies writes to a readable readonly group without changing server refs' {
        $target = Join-Path $root 'push rejection'
        $null = Get-FixtureGit @('clone', $source, $target)
        $before = Get-FixtureRefs $bareSource
        $readonlyUrl = "rns://$($ready.dest)/readonly/fixture.git"
        $read = Invoke-FixtureGit @('ls-remote', $readonlyUrl)
        $read.Code | Should -Be 0 -Because $read.Error
        $result = Invoke-FixtureGit @('-C', $target, 'push', $readonlyUrl, "$initialCommit`:refs/heads/rejected")
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

    It 'persists server and client identities and pushed refs across an actual server restart' {
        $clientIdentityHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
        $before = Get-FixtureRefs $pushBare
        $server.StandardInput.WriteLine('quit')
        $server.StandardInput.Flush()
        $server.WaitForExit(15000) | Should -BeTrue
        $server.ExitCode | Should -Be 0 -Because $serverError.GetAwaiter().GetResult()
        $script:restartServer = Start-GitRemoteProcess $pythonExecutable @(
            '-u', $adapter, '--config', (Join-Path $root 'python server'),
            '--tcp-server-port', "$port", '--repositories', $repositories, '--writer', $writer
        ) $serverEnvironment
        $restartError = $script:restartServer.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $restartReady = $null
        while ($null -eq $restartReady) {
            $lineTask = $script:restartServer.StandardOutput.ReadLineAsync()
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (!$lineTask.Wait($remaining)) { throw 'Restarted Python server readiness timed out.' }
            $line = $lineTask.GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Restarted Python server exited: $($restartError.GetAwaiter().GetResult())" }
            if ($line.StartsWith('@@')) { $restartReady = $line.Substring(2) | ConvertFrom-Json }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Restarted Python server readiness deadline exceeded.' }
        }
        $restartOutput = $script:restartServer.StandardOutput.ReadToEndAsync()
        $restartReady.dest | Should -Be $ready.dest
        $restartReady.pin | Should -Be $pin
        $script:restartServer.HasExited | Should -BeFalse
        Get-FixtureRefs $pushBare | Should -Be $before
        $target = Join-Path $root 'post-restart clone'
        $result = Invoke-FixtureGit @('clone', $pushUrl, $target)
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $target, 'rev-parse', 'refs/remotes/origin/pushed') | Should -Be $pushFixture.DivergentCommit
        $result = Invoke-FixtureGit @('-C', $target, 'push', $pushUrl, "$initialCommit`:refs/heads/post-restart")
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        Get-FixtureGit @('-C', $pushBare, 'rev-parse', 'refs/heads/post-restart') | Should -Be $initialCommit
        (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash | Should -Be $clientIdentityHash
        Assert-FixtureRepository $target
        Assert-FixtureRepository $pushBare
    }

    It 'deletes the advertised main branch without rewriting the server symbolic HEAD' {
        Get-FixtureGit @('-C', $pushBare, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        $result = Invoke-FixtureGit @('-C', $pushClone, 'push', $pushUrl, ':refs/heads/main')
        $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
        (Invoke-FixtureGit @('-C', $pushBare, 'show-ref', '--verify', 'refs/heads/main')).Code | Should -Not -Be 0
        Get-FixtureGit @('-C', $pushBare, 'symbolic-ref', 'HEAD') | Should -Be 'refs/heads/main'
        @(Get-ChildItem -LiteralPath (Join-Path $pushClone '.git') -Directory -Filter 'rngit-push-*').Count | Should -Be 0
    }
}
