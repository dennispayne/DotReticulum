param([string] $Application, [string] $Python, [string] $Upstream)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    function Invoke-HostingCli {
        param([string[]] $Arguments, [string] $WorkingDirectory = $root.FullName, [hashtable] $Environment = @{})
        Invoke-TestProcess $executable ($prefix + @('rngit') + $Arguments) -TimeoutSeconds 90 -WorkingDirectory $WorkingDirectory -Environment $Environment
    }
    $root = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'hosting')
    $groups = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'repositories')
    $server = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'server')
    $client = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'client')
    $nodeConfig = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'node')
    $repo = Join-Path $groups.FullName 'sample.git'
    $result = Invoke-TestProcess 'git' @('init', '--bare', '--initial-branch=main', $repo)
    if ($result.Code -ne 0) { throw $result.Error }
    $seed = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'seed')
    foreach ($arguments in @(
        @('init', '--initial-branch=main', $seed.FullName),
        @('-C', $seed.FullName, 'config', 'core.autocrlf', 'false')
    )) {
        $result = Invoke-TestProcess 'git' $arguments
        if ($result.Code -ne 0) { throw $result.Error }
    }
    [IO.File]::WriteAllText((Join-Path $seed.FullName 'owned.txt'), "Owned native hosting fixture.`n", [Text.UTF8Encoding]::new($false))
    foreach ($arguments in @(
        @('-C', $seed.FullName, 'add', '.'),
        @('-C', $seed.FullName, '-c', 'user.name=Owned fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'owned'),
        @('-C', $seed.FullName, 'tag', 'v1'),
        @('-C', $seed.FullName, 'push', $repo, 'main', 'v1')
    )) {
        $result = Invoke-TestProcess 'git' $arguments
        if ($result.Code -ne 0) { throw $result.Error }
    }
    @"
[rngit]
node_name = Owned Pester repository host
announce_interval = 1
mirror_interval = 0
record_stats = yes
[pages]
serve_nomadnet = yes
unicode_icons = yes
[repositories]
fixtures = $($groups.FullName)
[access]
fixtures = read:all, write:all, create:all, release:all, interact:all, propose:all, admin:all, stats:all
"@ | Set-Content -LiteralPath (Join-Path $server.FullName 'config') -Encoding utf8
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = $portProbe.LocalEndpoint.Port
    $portProbe.Stop()
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
"@ | Set-Content -LiteralPath (Join-Path $nodeConfig.FullName 'config')
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
    $info = Invoke-HostingCli @('node', '--config', $server.FullName, '-p')
    if ($info.Code -ne 0) { throw $info.Error }
    $destination = [regex]::Match($info.Output, 'Repositories Destination\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
    if (!$destination) { throw "Missing repository destination: $($info.Output)" }
    $remote = "rns://$destination/fixtures/sample.git"
    $common = @('--config', (Join-Path $root.FullName 'client-git'), '--rnsconfig', $client.FullName, '--timeout', '60')
    $helperDirectory = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'helpers')
    $helper = Install-TestGitHelper $cli $helperDirectory.FullName
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    foreach ($name in 'rngit', 'rngcs') {
        Copy-Item -LiteralPath $helper -Destination (Join-Path $helperDirectory.FullName "$name$suffix")
    }
    $globalGitConfig = Join-Path $root.FullName 'git-global'
    [IO.File]::WriteAllText($globalGitConfig, '')
    $gitEnvironment = @{
        PATH = $helperDirectory.FullName + [IO.Path]::PathSeparator + $env:PATH
        DOTRETICULUM_RNS_CONFIG = $client.FullName
        RNGIT_CONFIG = Join-Path $root.FullName 'client-git'
        DOTRETICULUM_RNGIT_TIMEOUT = '60'
        GIT_CONFIG_GLOBAL = $globalGitConfig
        GIT_CONFIG_NOSYSTEM = '1'
        GIT_TERMINAL_PROMPT = '0'
    }
    $identityHash = (Get-FileHash -LiteralPath (Join-Path $server.FullName 'repositories_identity')).Hash
    function Start-OwnedHost {
        $process = Start-TestProcess $executable ($prefix + @('rngit', 'node', '--config', $server.FullName, '--rnsconfig', $nodeConfig.FullName))
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($process.Process.HasExited) { throw "Host exited: $($process.Error.GetAwaiter().GetResult())" }
            $probe = [Net.Sockets.TcpClient]::new()
            try { $probe.Connect([Net.IPAddress]::Loopback, $port); $ready = $true }
            catch [Net.Sockets.SocketException] { $ready = $false; Start-Sleep -Milliseconds 50 }
            finally { $probe.Dispose() }
            if ([DateTime]::UtcNow -ge $deadline) { Stop-TestProcess $process; throw 'Host did not become reachable.' }
        } until ($ready)
        return $process
    }
    $script:hostProcess = Start-OwnedHost
}

AfterAll {
    if ($script:hostProcess) { Stop-TestProcess $script:hostProcess }
    if ($root -and (Test-Path -LiteralPath $root.FullName)) { Remove-Item -LiteralPath $root.FullName -Recurse -Force }
}

Describe 'Native repository hosting CLI' -Tag E2E {
    It 'matches actual pinned CLI exit codes for <Name>' -ForEach @(
        @{ Name = 'version'; Arguments = @('--version') },
        @{ Name = 'missing creation URL'; Arguments = @('create') },
        @{ Name = 'missing permission URL'; Arguments = @('perms') },
        @{ Name = 'missing fork source and target'; Arguments = @('fork') },
        @{ Name = 'missing mirror source and target'; Arguments = @('mirror') },
        @{ Name = 'missing sync URL'; Arguments = @('sync') },
        @{ Name = 'missing work operation'; Arguments = @('work') },
        @{ Name = 'missing release operation'; Arguments = @('release') },
        @{ Name = 'unknown work operation'; Arguments = @('work', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'unknown') },
        @{ Name = 'unknown release operation'; Arguments = @('release', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'unknown') },
        @{ Name = 'missing document ID'; Arguments = @('work', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'view') },
        @{ Name = 'missing document title'; Arguments = @('work', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'create') },
        @{ Name = 'missing release target'; Arguments = @('release', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'view') },
        @{ Name = 'missing release fetch target'; Arguments = @('release', 'rns://00000000000000000000000000000000/fixtures/sample.git', 'fetch') },
        @{ Name = 'node-inapplicable identity flag'; Arguments = @('node', '--identity', 'owned-unused') },
        @{ Name = 'creation-inapplicable scope flag'; Arguments = @('create', '--scope', 'active', 'rns://00000000000000000000000000000000/fixtures/sample.git') }
    ) -Skip:([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
        $isolated = New-Item -ItemType Directory -Path (Join-Path $root.FullName ([guid]::NewGuid().ToString('N')))
        $rns = New-Item -ItemType Directory -Path (Join-Path $isolated.FullName 'rns')
        "[reticulum]`nshare_instance = no`nenable_transport = no`n[interfaces]`n" |
            Set-Content -LiteralPath (Join-Path $rns.FullName 'config')
        $nativeArguments = @($Arguments) + @('--config', (Join-Path $isolated.FullName 'native'), '--rnsconfig', $rns.FullName)
        $sourceArguments = @($Arguments) + @('--config', (Join-Path $isolated.FullName 'reference'), '--rnsconfig', $rns.FullName)
        $source = Invoke-TestProcess $Python (@((Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\RngitCliReference.py')) + $sourceArguments) `
            -Environment @{ PYTHONPATH = $Upstream; DOTRETICULUM_UPSTREAM = $Upstream } -WorkingDirectory $isolated.FullName -TimeoutSeconds 20
        $actual = Invoke-HostingCli $nativeArguments
        $actual.Code | Should -Be $source.Code -Because "source='$($source.Output) $($source.Error)', native='$($actual.Output) $($actual.Error)'"
        if ($Name -eq 'version') {
            $actual.Output.Trim() | Should -Be $source.Output.Trim()
        }
        else {
            $actual.Code | Should -Not -Be 0
            $actual.Error | Should -Not -BeNullOrEmpty
        }
    }

    It 'discovers actual utility filenames and preserves ordinary Git clone, Unicode ref push and fetch' -Tag 'Contract:UTIL-RNGIT-004' {
        $alias = Invoke-TestProcess (Join-Path $helperDirectory.FullName "rngit$suffix") @('--version')
        $alias.Code | Should -Be 0 -Because $alias.Error
        $alias.Output | Should -Match 'rngit 1.5.7'
        $signing = Invoke-TestProcess (Join-Path $helperDirectory.FullName "rngcs$suffix") @('-h')
        $signing.Code | Should -Be 0 -Because $signing.Error
        $clone = Join-Path $root.FullName 'clone'
        $result = Invoke-TestProcess 'git' @('clone', $remote, $clone) -Environment $gitEnvironment -TimeoutSeconds 90
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        [IO.File]::ReadAllText((Join-Path $clone 'owned.txt')) | Should -Match 'Owned native hosting'
        $head = Invoke-TestProcess 'git' @('-C', $clone, 'symbolic-ref', 'HEAD')
        $head.Output.Trim() | Should -Be 'refs/heads/main'
        $pushed = Invoke-TestProcess 'git' @('-C', $clone, 'push', 'origin', 'HEAD:refs/heads/parity-é') -Environment $gitEnvironment -TimeoutSeconds 90
        $pushed.Code | Should -Be 0 -Because ($pushed.Output + $pushed.Error)
        $fetched = Invoke-TestProcess 'git' @('-C', $clone, 'fetch', 'origin') -Environment $gitEnvironment -TimeoutSeconds 90
        $fetched.Code | Should -Be 0 -Because ($fetched.Output + $fetched.Error)
        $tracking = Invoke-TestProcess 'git' @('-C', $clone, 'show-ref', '--verify', 'refs/remotes/origin/parity-é')
        $tracking.Code | Should -Be 0 -Because $tracking.Error
        [IO.File]::WriteAllText((Join-Path $clone 'owned.txt'), "New objects received by the native host.`n", [Text.UTF8Encoding]::new($false))
        foreach ($arguments in @(
            @('-C', $clone, 'add', '.'),
            @('-C', $clone, '-c', 'user.name=Owned native writer', '-c', 'user.email=native@example.invalid', 'commit', '-m', 'native bundled push')
        )) {
            $committed = Invoke-TestProcess 'git' $arguments -Environment $gitEnvironment
            $committed.Code | Should -Be 0 -Because $committed.Error
        }
        $bundlePush = Invoke-TestProcess 'git' @('-C', $clone, 'push', 'origin', 'HEAD:refs/heads/new-objects') -Environment $gitEnvironment -TimeoutSeconds 90
        $bundlePush.Code | Should -Be 0 -Because ($bundlePush.Output + $bundlePush.Error)
        $local = Invoke-TestProcess 'git' @('-C', $clone, 'rev-parse', 'HEAD')
        $remoteRef = Invoke-TestProcess 'git' @("--git-dir=$repo", 'rev-parse', 'refs/heads/new-objects')
        $local.Output.Trim() | Should -Be $remoteRef.Output.Trim()
        $main = Invoke-TestProcess 'git' @("--git-dir=$repo", 'rev-parse', 'refs/heads/main')
        $main.Output.Trim() | Should -Not -Be $local.Output.Trim()
    }

    It 'creates repositories and refuses duplicate creation without replacing persisted identity' -Tag 'Contract:UTIL-RNGIT-004' {
        $target = "rns://$destination/fixtures/created.git"
        $created = Invoke-HostingCli (@('create', $target) + $common)
        $created.Code | Should -Be 0 -Because ($created.Output + $created.Error)
        $created.Output | Should -Match 'Repository fixtures/created.git created'
        Test-Path -LiteralPath (Join-Path $groups.FullName 'created.git.allowed') | Should -BeTrue
        $duplicate = Invoke-HostingCli (@('create', $target) + $common)
        $duplicate.Code | Should -Be 1
        $duplicate.Error | Should -Match 'already exists'
        (Get-FileHash -LiteralPath (Join-Path $server.FullName 'repositories_identity')).Hash | Should -Be $identityHash
    }

    It 'creates, views, comments, completes and activates signed work documents through separate processes' -Tag 'Contract:UTIL-RNGIT-005' {
        $content = Join-Path $root.FullName 'document.md'
        'Owned persistent work document.' | Set-Content -LiteralPath $content -Encoding utf8
        $created = Invoke-HostingCli (@('work', $remote, 'create', '--title', 'Owned document', '--content', $content) + $common)
        $created.Code | Should -Be 0 -Because ($created.Output + $created.Error)
        $id = [regex]::Match($created.Output, '#([0-9]+)').Groups[1].Value
        $id | Should -Not -BeNullOrEmpty
        $script:documentId = $id
        $viewed = Invoke-HostingCli (@('work', $remote, 'view', '--id', $id) + $common)
        $viewed.Code | Should -Be 0 -Because $viewed.Error
        $viewed.Output | Should -Match 'Owned persistent work document'
        $viewed.Output | Should -Match 'Signature\s*:\s*Valid'
        $comment = Invoke-HostingCli (@('work', $remote, 'update', '--id', $id, '--content', $content) + $common)
        $comment.Code | Should -Be 0 -Because $comment.Error
        $complete = Invoke-HostingCli (@('work', $remote, 'complete', '--id', $id) + $common)
        $complete.Code | Should -Be 0 -Because $complete.Error
        $completed = Invoke-HostingCli (@('work', $remote, 'view', '--id', $id, '--scope', 'completed') + $common)
        $completed.Code | Should -Be 0 -Because $completed.Error
        $completed.Output | Should -Match 'Status\s*:\s*Completed'
        $activated = Invoke-HostingCli (@('work', $remote, 'activate', '--id', $id) + $common)
        $activated.Code | Should -Be 0 -Because $activated.Error
    }

    It 'edits repository permissions through authorized content and diagnoses hidden state' -Tag 'Contract:UTIL-RNGIT-006' {
        $content = Join-Path $root.FullName 'permissions.allowed'
        "read:all`nwrite:none`nadmin:all" | Set-Content -LiteralPath $content -Encoding utf8
        $edited = Invoke-HostingCli (@('perms', "rns://$destination/fixtures/created.git", '--content', $content) + $common)
        $edited.Code | Should -Be 0 -Because ($edited.Output + $edited.Error)
        [IO.File]::ReadAllText((Join-Path $groups.FullName 'created.git.allowed')) | Should -Match 'write:none'
        $hidden = Invoke-HostingCli (@('work', "rns://$destination/unknown/secret.git", 'list') + $common)
        $hidden.Code | Should -Be 1
        $hidden.Error | Should -Match 'Not found'
    }

    It 'proposes, edits, administers and deletes scoped work without resetting persisted IDs' -Tag 'Contract:UTIL-RNGIT-005' {
        $content = Join-Path $root.FullName 'proposal.md'
        [IO.File]::WriteAllText($content, 'Owned proposed content')
        $proposed = Invoke-HostingCli (@('work', $remote, 'propose', '--title', 'Owned proposal', '--content', $content) + $common)
        $proposed.Code | Should -Be 0 -Because $proposed.Error
        $id = [regex]::Match($proposed.Output, '#([0-9]+)').Groups[1].Value
        $id | Should -Not -BeNullOrEmpty
        [IO.File]::WriteAllText($content, 'Edited proposed content')
        $edited = Invoke-HostingCli (@('work', $remote, 'edit', '--scope', 'proposed', '--id', $id, '--content', $content) + $common)
        $edited.Code | Should -Be 0 -Because $edited.Error
        $viewed = Invoke-HostingCli (@('work', $remote, 'view', '--scope', 'proposed', '--id', $id) + $common)
        $viewed.Code | Should -Be 0 -Because $viewed.Error
        $viewed.Output | Should -Match 'Edited proposed content'
        $viewed.Output | Should -Match 'Owned proposal'
        $viewed.Output | Should -Match 'Signature\s*:\s*Valid'
        $allowed = Join-Path $root.FullName 'document.allowed'
        [IO.File]::WriteAllText($allowed, "read:all`ninteract:all`nadmin:all`n")
        $permissions = Invoke-HostingCli (@('work', $remote, 'perms', '--scope', 'proposed', '--id', $id, '--content', $allowed) + $common)
        $permissions.Code | Should -Be 0 -Because ($permissions.Output + $permissions.Error)
        $deleted = Invoke-HostingCli (@('work', $remote, 'delete', '--scope', 'proposed', '--id', $id, '--yes') + $common)
        $deleted.Code | Should -Be 0 -Because $deleted.Error
        $missing = Invoke-HostingCli (@('work', $remote, 'view', '--scope', 'proposed', '--id', $id) + $common)
        $missing.Code | Should -Be 1
        $missing.Error | Should -Match 'not found'
    }

    It 'creates and lists signed releases, sets latest, fetches and verifies offline, diagnoses tampering and deletes' -Tag 'Contract:UTIL-RNGIT-007' {
        $artifacts = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'release-artifacts')
        [IO.File]::WriteAllText((Join-Path $artifacts.FullName 'payload.txt'), 'Owned release payload')
        $notes = Join-Path $root.FullName 'notes.md'
        [IO.File]::WriteAllText($notes, 'Owned release notes')
        $created = Invoke-HostingCli (@('release', $remote, 'create', "v1:$($artifacts.FullName)", '--name', 'fixture', '--content', $notes) + $common) -WorkingDirectory $seed.FullName
        $created.Code | Should -Be 0 -Because ($created.Output + $created.Error)
        Test-Path -LiteralPath (Join-Path $artifacts.FullName 'manifest.rsm') | Should -BeTrue
        $listed = Invoke-HostingCli (@('release', $remote, 'list') + $common)
        $listed.Code | Should -Be 0 -Because $listed.Error
        $listed.Output | Should -Match 'Tag\s+Status\s+Created\s+Objs'
        $listed.Output | Should -Match 'v1\s+published'
        $viewed = Invoke-HostingCli (@('release', $remote, 'view', 'v1') + $common)
        $viewed.Code | Should -Be 0 -Because $viewed.Error
        $viewed.Output | Should -Match 'Release\s*:\s*v1'
        $viewed.Output | Should -Match 'Owned release notes'
        $cancelled = Invoke-HostingCli (@('release', $remote, 'latest', 'v1') + $common)
        $cancelled.Code | Should -Be 0 -Because $cancelled.Error
        $cancelled.Output | Should -Match 'Update cancelled'
        $latest = Invoke-HostingCli (@('release', $remote, 'latest', 'v1', '--yes') + $common)
        $latest.Code | Should -Be 0 -Because $latest.Error
        $downloaded = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'release-download')
        $signer = Join-Path $root.FullName 'client-git\client_identity'
        $fetched = Invoke-HostingCli (@('release', $remote, 'fetch', 'latest:all', '--signer', $signer) + $common) -WorkingDirectory $downloaded.FullName
        $fetched.Code | Should -Be 0 -Because ($fetched.Output + $fetched.Error)
        $fetched.Output | Should -Match 'explicitly required identity'
        [IO.File]::ReadAllText((Join-Path $downloaded.FullName 'payload.txt')) | Should -Be 'Owned release payload'
        $manifest = Join-Path $downloaded.FullName 'fixture_v1.rsm'
        Test-Path -LiteralPath $manifest | Should -BeTrue
        $verified = Invoke-HostingCli @('release', $manifest, 'verify', '--signer', $signer)
        $verified.Code | Should -Be 0 -Because $verified.Error
        [IO.File]::WriteAllText((Join-Path $downloaded.FullName 'payload.txt'), 'Tampered owned artifact')
        $tampered = Invoke-HostingCli @('release', $manifest, 'verify', '--signer', $signer)
        $tampered.Code | Should -Be 1
        ($tampered.Output + $tampered.Error) | Should -Match 'Invalid|mismatch'
        $deleted = Invoke-HostingCli (@('release', $remote, 'delete', 'v1', '--yes') + $common)
        $deleted.Code | Should -Be 0 -Because $deleted.Error
        $missing = Invoke-HostingCli (@('release', $remote, 'view', 'v1') + $common)
        $missing.Code | Should -Be 1
        $missing.Error | Should -Match 'not found|does not exist'
    }

    It 'runs a real editor only with explicit CLI authorization and removes owned temporary files' -Tag 'Contract:UTIL-RNGIT-005' {
        $editor = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $scriptPath = Join-Path $root.FullName 'authorized-editor.ps1'
        [IO.File]::WriteAllText($scriptPath, @'
param([string] $DocumentPath)
[IO.File]::WriteAllText($DocumentPath, 'Edited by the explicitly authorized CLI process')
'@)
        $temporary = New-Item -ItemType Directory -Path (Join-Path $root.FullName 'editor-temporary')
        $environment = @{
            EDITOR = "`"$editor`" -NoProfile -NonInteractive -File `"$scriptPath`""
            TEMP = $temporary.FullName
            TMP = $temporary.FullName
            POWERSHELL_TELEMETRY_OPTOUT = '1'
        }
        $denied = Invoke-HostingCli (@('work', $remote, 'edit', '--id', $script:documentId) + $common) -Environment $environment
        $denied.Code | Should -Be 1
        $denied.Error | Should -Match 'requires explicit --allow-exec'
        @(Get-ChildItem -LiteralPath $temporary.FullName -Force).Count | Should -Be 0
        $edited = Invoke-HostingCli (@('work', $remote, 'edit', '--id', $script:documentId, '--allow-exec') + $common) -Environment $environment
        $edited.Code | Should -Be 0 -Because ($edited.Output + $edited.Error)
        $viewed = Invoke-HostingCli (@('work', $remote, 'view', '--id', $script:documentId) + $common)
        $viewed.Code | Should -Be 0 -Because $viewed.Error
        $viewed.Output | Should -Match 'Edited by the explicitly authorized CLI process'
        $viewed.Output | Should -Match 'Signature\s*:\s*Valid'
        @(Get-ChildItem -LiteralPath $temporary.FullName -Force).Count | Should -Be 0
    }

    It 'restarts with the same identity and serves persisted work state' -Tag 'Contract:UTIL-RNGIT-004' {
        Stop-TestProcess $script:hostProcess
        $script:hostProcess = Start-OwnedHost
        (Get-FileHash -LiteralPath (Join-Path $server.FullName 'repositories_identity')).Hash | Should -Be $identityHash
        $listed = Invoke-HostingCli (@('work', $remote, 'list', '--scope', 'all') + $common)
        $listed.Code | Should -Be 0 -Because ($listed.Output + $listed.Error)
        $listed.Output | Should -Match 'Owned document'
    }
}
