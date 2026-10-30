BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $resolver = Join-Path $PSScriptRoot '..\Resolve-TestBuild.ps1'
    $builtTests = Join-Path $PSScriptRoot '..\Invoke-BuiltTests.ps1'

    function gh([string] $Command, [string] $Endpoint) {
        throw "Unexpected unmocked GitHub request: $Command $Endpoint"
    }
}

Describe 'Cross-workflow Build selection' -Tag Tooling {
    BeforeEach {
        $script:savedOutput = $env:GITHUB_OUTPUT
        $script:savedSummary = $env:GITHUB_STEP_SUMMARY
        $env:GITHUB_OUTPUT = Join-Path $TestDrive 'job-output'
        $env:GITHUB_STEP_SUMMARY = $null
        $script:run = @{
            id = 42; run_attempt = 1; path = '.github/workflows/build.yml'
            repository = @{ full_name = 'example/project' }; head_repository = @{ full_name = 'example/project' }
            event = 'push'; head_branch = 'main'; head_sha = 'a' * 40
            status = 'completed'; conclusion = 'success'; html_url = 'https://github.com/example/project/actions/runs/42'
        }
        $script:artifacts = @(
            foreach ($rid in 'linux-x64', 'osx-arm64', 'win-x64') {
                foreach ($kind in 'native', 'managed') { @{ name = "$kind-$rid"; expired = $false } }
            }
        )
        $fixture = @{ Run = $script:run; Artifacts = $script:artifacts
            Parents = @(@{ sha = 'c' * 40 }, @{ sha = 'a' * 40 }) }
        $script:fixture = $fixture
        Mock gh ({
            param($Command, $Endpoint)
            $global:LASTEXITCODE = 0
            if ($Command -ne 'api') { throw 'Only API requests are expected.' }
            if ($Endpoint -like '*/git/commits/*') { return (@{ parents = $fixture.Parents } | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/artifacts?*') { return (@{ artifacts = $fixture.Artifacts } | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/workflows/build.yml/runs?*') { return (@{ workflow_runs = @($fixture.Run) } | ConvertTo-Json -Depth 5) }
            return ($fixture.Run | ConvertTo-Json -Depth 5)
        }.GetNewClosure())
    }

    AfterEach {
        $env:GITHUB_OUTPUT = $script:savedOutput
        $env:GITHUB_STEP_SUMMARY = $script:savedSummary
    }

    It 'selects and records one complete successful main Build without rebuilding' {
        & $resolver -Repository example/project -EventPath ''
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'run-id=42'
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain "commit=$('a' * 40)"
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'attested=true'
        Should -Invoke gh -Times 1 -Exactly -ParameterFilter { $Endpoint -eq 'repos/example/project/actions/runs/42' }
    }

    It 'rejects <Problem> before exporting a producer' -ForEach @(
        @{ Problem = 'a foreign workflow'; Field = 'path'; Value = '.github/workflows/release.yml' }
        @{ Problem = 'a failed Build'; Field = 'conclusion'; Value = 'failure' }
        @{ Problem = 'an unfinished Build'; Field = 'status'; Value = 'in_progress' }
        @{ Problem = 'a non-main manual Build'; Field = 'head_branch'; Value = 'feature' }
        @{ Problem = 'an invalid source commit'; Field = 'head_sha'; Value = 'invalid' }
    ) {
        $script:run[$Field] = $Value
        { & $resolver -RunId 42 -Repository example/project -EventPath '' } | Should -Throw
    }

    It 'rejects fork-produced artifacts even when the Build itself succeeded' {
        $script:run.head_repository.full_name = 'untrusted/fork'
        { & $resolver -RunId 42 -Repository example/project -EventPath '' } | Should -Throw '*fork-produced*'
    }

    It 'fails missing, expired or duplicate artifact inventory instead of selecting another run' -ForEach @('missing', 'expired', 'duplicate') {
        switch ($_) {
            missing { $script:fixture.Artifacts = $script:fixture.Artifacts[1..5] }
            expired { $script:fixture.Artifacts[0].expired = $true }
            duplicate { $script:fixture.Artifacts += $script:fixture.Artifacts[0] }
        }
        { & $resolver -RunId 42 -Repository example/project -EventPath '' } | Should -Throw '*exactly one unexpired*'
    }

    It 'pins the triggering run and rejects a conflicting requested run' {
        $eventFile = Join-Path $TestDrive 'event.json'
        @{ workflow_run = @{ id = 42 } } | ConvertTo-Json | Set-Content -LiteralPath $eventFile
        { & $resolver -RunId 43 -Repository example/project -EventPath $eventFile } | Should -Throw '*triggering run*'
        & $resolver -Repository example/project -EventPath $eventFile
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'run-id=42'
    }

    It 'allows same-repository PR feedback without treating it as attested main provenance' {
        $script:run.event = 'pull_request'
        $script:run.head_branch = 'feature'
        $eventFile = Join-Path $TestDrive 'pr-event.json'
        @{ workflow_run = @{ id = 42 } } | ConvertTo-Json | Set-Content -LiteralPath $eventFile
        & $resolver -Repository example/project -EventPath $eventFile
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'attested=false'
    }

    It 'preserves PR merge testing but rejects an unrelated or misreported main checkout' {
        $manifest = Join-Path $TestDrive 'native-manifest.json'
        @{ rid = 'linux-x64'; commit = 'b' * 40 } | ConvertTo-Json | Set-Content -LiteralPath $manifest
        { & $resolver -RunId 42 -Repository example/project -EventPath '' -NativeManifest $manifest } |
            Should -Throw '*Main Build manifest*'
        $script:run.event = 'pull_request'
        $eventFile = Join-Path $TestDrive 'merge-event.json'
        @{ workflow_run = @{ id = 42 } } | ConvertTo-Json | Set-Content -LiteralPath $eventFile
        & $resolver -Repository example/project -EventPath $eventFile -NativeManifest $manifest
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain "commit=$('b' * 40)"
        $script:fixture.Parents[1].sha = 'd' * 40
        { & $resolver -Repository example/project -EventPath $eventFile -NativeManifest $manifest } |
            Should -Throw '*not a merge*'
    }
}

Describe 'Portable managed Build validation' -Tag Tooling {
    It 'rejects a mismatched <Field> before extracting or starting tests' -ForEach @(
        @{ Field = 'commit'; Value = 'b' * 40; Error = 'commit/runtime mismatch' }
        @{ Field = 'rid'; Value = 'linux-x64'; Error = 'commit/runtime mismatch' }
        @{ Field = 'sha256'; Value = '0' * 64; Error = 'digest mismatch' }
    ) {
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $directory
        $archive = Join-Path $directory 'managed.tar'
        Set-Content -LiteralPath $archive -Value 'Not a valid archive and must not be extracted.'
        $manifest = @{
            commit = 'a' * 40; rid = 'win-x64'
            sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $manifest[$Field] = $Value
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'managed-manifest.json')
        $results = Join-Path $directory 'results'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $builtTests, '-Directory', $directory,
            '-Rid', 'win-x64', '-Commit', ('a' * 40), '-Filter', 'Category=Unit', '-ResultsDirectory', $results)
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match $Error
        Test-Path -LiteralPath $results | Should -BeFalse
    }
}
