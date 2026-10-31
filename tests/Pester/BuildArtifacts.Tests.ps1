BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $resolver = Join-Path $PSScriptRoot '..\Resolve-TestBuild.ps1'
    $requestWriter = Join-Path $PSScriptRoot '..\New-TestRequest.ps1'
    $builtTests = Join-Path $PSScriptRoot '..\Invoke-BuiltTests.ps1'

    function gh([string] $Command, [string] $Endpoint) {
        throw "Unexpected unmocked GitHub request: $Command $Endpoint"
    }
}

Describe 'Cross-workflow Build selection' -Tag Tooling {
    BeforeEach {
        $script:savedOutput = $env:GITHUB_OUTPUT
        $script:savedSummary = $env:GITHUB_STEP_SUMMARY
        $script:savedEvent = $env:GITHUB_EVENT_NAME
        $script:savedRef = $env:GITHUB_REF
        $env:GITHUB_OUTPUT = Join-Path $TestDrive ("job-output-$([guid]::NewGuid().ToString('N'))")
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
        $script:producer = @{
            id = 84; run_attempt = 2; path = '.github/workflows/test-request.yml'
            repository = @{ full_name = 'example/project' }; head_repository = @{ full_name = 'example/project' }
            event = 'workflow_dispatch'; head_branch = 'main'; head_sha = 'c' * 40
            status = 'completed'; conclusion = 'success'
        }
        $script:requestData = @{ build_run_id = '42'; cadence = 'manual'; full_integration = $true; iterations = 2 }
        $script:requestFile = Join-Path $TestDrive 'test-request.json'
        $script:eventFile = Join-Path $TestDrive 'request-event.json'
        @{ workflow_run = @{ id = 84 } } | ConvertTo-Json | Set-Content -LiteralPath $script:eventFile
        $fixture = @{ Run = $script:run; Artifacts = $script:artifacts; Producer = $script:producer
            RequestArtifacts = @(@{ name = 'test-request-2'; expired = $false })
            Parents = @(@{ sha = 'c' * 40 }, @{ sha = 'a' * 40 }) }
        $script:fixture = $fixture
        Mock gh ({
            param($Command, $Endpoint)
            $global:LASTEXITCODE = 0
            if ($Command -ne 'api') { throw 'Only API requests are expected.' }
            if ($Endpoint -like '*/runs/84/artifacts?*') { return (@{ artifacts = $fixture.RequestArtifacts } | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/runs/84') { return ($fixture.Producer | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/git/commits/*') { return (@{ parents = $fixture.Parents } | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/artifacts?*') { return (@{ artifacts = $fixture.Artifacts } | ConvertTo-Json -Depth 5) }
            if ($Endpoint -like '*/workflows/build.yml/runs?*') { return (@{ workflow_runs = @($fixture.Run) } | ConvertTo-Json -Depth 5) }
            return ($fixture.Run | ConvertTo-Json -Depth 5)
        }.GetNewClosure())
    }

    AfterEach {
        $env:GITHUB_OUTPUT = $script:savedOutput
        $env:GITHUB_STEP_SUMMARY = $script:savedSummary
        $env:GITHUB_EVENT_NAME = $script:savedEvent
        $env:GITHUB_REF = $script:savedRef
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

    It 'rejects a foreign producing repository even when the Build itself succeeded' {
        $script:run.repository.full_name = 'untrusted/fork'
        { & $resolver -RunId 42 -Repository example/project -EventPath '' } | Should -Throw '*running in this repository*'
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

    It 'preserves <Kind> feedback only through read-only workflow_run execution' -ForEach @(
        @{ Kind = 'fork PR'; Event = 'pull_request'; HeadRepository = 'untrusted/fork' }
        @{ Kind = 'feature-branch Build'; Event = 'workflow_dispatch'; HeadRepository = 'example/project' }
    ) {
        $script:run.event = $Event
        $script:run.head_branch = 'feature'
        $script:run.head_repository.full_name = $HeadRepository
        $eventFile = Join-Path $TestDrive 'feedback-event.json'
        @{ workflow_run = @{ id = 42 } } | ConvertTo-Json | Set-Content -LiteralPath $eventFile
        & $resolver -Repository example/project -EventPath $eventFile
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'attested=false'
        { & $resolver -RunId 42 -Repository example/project -EventPath '' } | Should -Throw '*main Build*'
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

    It 'validates the request producer and current-attempt inventory before downloading' {
        & $resolver -Repository example/project -EventPath $script:eventFile -RequestProducerOnly
        Get-Content -LiteralPath $env:GITHUB_OUTPUT | Should -Contain 'request-artifact=test-request-2'
        Should -Invoke gh -Times 0 -Exactly -ParameterFilter { $Endpoint -like '*/runs/42*' }
        $script:fixture.RequestArtifacts[0].expired = $true
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestProducerOnly } |
            Should -Throw '*one unexpired request artifact*'
    }

    It 'rejects <Problem> as a request producer before exposing its artifact' -ForEach @(
        @{ Problem = 'nonmain controls'; Field = 'head_branch'; Value = 'feature' }
        @{ Problem = 'a different workflow'; Field = 'path'; Value = '.github/workflows/build.yml' }
        @{ Problem = 'a PR event'; Field = 'event'; Value = 'pull_request' }
        @{ Problem = 'a failed request'; Field = 'conclusion'; Value = 'failure' }
        @{ Problem = 'unfinished controls'; Field = 'status'; Value = 'in_progress' }
        @{ Problem = 'a mismatched trigger'; Field = 'id'; Value = 85 }
    ) {
        $script:producer[$Field] = $Value
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestProducerOnly } |
            Should -Throw '*successful main Test request*'
        Test-Path -LiteralPath $env:GITHUB_OUTPUT | Should -BeFalse
    }

    It 'rejects a foreign request <_> even if its branch is called main' -ForEach @('repository', 'head_repository') {
        $script:producer[$_].full_name = 'untrusted/fork'
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestProducerOnly } |
            Should -Throw '*successful main Test request*'
    }

    It 'resolves exactly the requested main Build and preserves <Cadence> Integration' -ForEach @(
        @{ Cadence = 'manual'; Event = 'workflow_dispatch'; Iterations = 2; Constrained = 'true' }
        @{ Cadence = 'weekly-live'; Event = 'schedule'; Iterations = 1; Constrained = 'false' }
        @{ Cadence = 'weekly-mesh'; Event = 'schedule'; Iterations = 3; Constrained = 'true' }
    ) {
        $script:producer.event = $Event
        $script:requestData.cadence = $Cadence
        $script:requestData.iterations = $Iterations
        $script:requestData | ConvertTo-Json | Set-Content -LiteralPath $script:requestFile
        & $resolver -Repository example/project -EventPath $script:eventFile -RequestPath $script:requestFile
        $output = Get-Content -LiteralPath $env:GITHUB_OUTPUT
        $output | Should -Contain 'run-id=42'
        $output | Should -Contain 'attested=true'
        $output | Should -Contain 'full-integration=true'
        $output | Should -Contain "iterations=$Iterations"
        $output | Should -Contain "constrained-streams=$Constrained"
        if ($Cadence -eq 'weekly-mesh') {
            ($output | Where-Object { $_ -like 'integration-filter=*' }) |
                Should -Match 'Parity8CompatibilityLiveTests.*LiveMixedTransportTests.*Parity4NativeTransportTests.*PublishedPublicStreamsSustain'
        } else { $output | Should -Contain 'integration-filter=Category=Live' }
        Should -Invoke gh -Times 0 -Exactly -ParameterFilter { $Endpoint -like '*/workflows/build.yml/runs?*' }
        { & $resolver -RunId 43 -Repository example/project -EventPath $script:eventFile -RequestPath $script:requestFile } |
            Should -Throw '*differs from the Test request*'
    }

    It 'does not let a trusted request promote <Kind> Build feedback to manual acceptance' -ForEach @(
        @{ Kind = 'a fork PR'; Event = 'pull_request'; HeadRepository = 'untrusted/fork' }
        @{ Kind = 'a feature Build'; Event = 'workflow_dispatch'; HeadRepository = 'example/project' }
    ) {
        $script:run.event = $Event
        $script:run.head_branch = 'feature'
        $script:run.head_repository.full_name = $HeadRepository
        $script:requestData | ConvertTo-Json | Set-Content -LiteralPath $script:requestFile
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestPath $script:requestFile } |
            Should -Throw '*main Build*'
    }

    It 'rejects malformed request <Problem> without exporting Build or cadence' -ForEach @(
        @{ Problem = 'unknown cadence'; Field = 'cadence'; Value = 'ordinary' }
        @{ Problem = 'schedule on manual producer'; Field = 'cadence'; Value = 'weekly-mesh' }
        @{ Problem = 'fractional repetitions'; Field = 'iterations'; Value = 1.5 }
        @{ Problem = 'string repetitions'; Field = 'iterations'; Value = '3' }
        @{ Problem = 'zero repetitions'; Field = 'iterations'; Value = 0 }
        @{ Problem = 'excessive repetitions'; Field = 'iterations'; Value = 11 }
        @{ Problem = 'nonboolean full flag'; Field = 'full_integration'; Value = 'true' }
        @{ Problem = 'nonstring Build ID'; Field = 'build_run_id'; Value = 42 }
        @{ Problem = 'blank Build ID'; Field = 'build_run_id'; Value = '' }
        @{ Problem = 'extra fields'; Field = 'unexpected'; Value = 'data' }
    ) {
        $script:requestData[$Field] = $Value
        $script:requestData | ConvertTo-Json | Set-Content -LiteralPath $script:requestFile
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestPath $script:requestFile } | Should -Throw
        Test-Path -LiteralPath $env:GITHUB_OUTPUT | Should -BeFalse
        Should -Invoke gh -Times 0 -Exactly -ParameterFilter { $Endpoint -like '*/runs/42*' }
    }

    It 'rejects inconsistent scheduled <Cadence> requests' -ForEach @(
        @{ Cadence = 'weekly-live'; Full = $true; Iterations = 3 }
        @{ Cadence = 'weekly-mesh'; Full = $true; Iterations = 1 }
        @{ Cadence = 'weekly-live'; Full = $false; Iterations = 1 }
        @{ Cadence = 'manual'; Full = $true; Iterations = 1 }
    ) {
        $script:producer.event = 'schedule'
        $script:requestData.cadence = $Cadence
        $script:requestData.full_integration = $Full
        $script:requestData.iterations = $Iterations
        $script:requestData | ConvertTo-Json | Set-Content -LiteralPath $script:requestFile
        { & $resolver -Repository example/project -EventPath $script:eventFile -RequestPath $script:requestFile } |
            Should -Throw '*cadence does not match*'
    }

    It 'writes a pinned non-executing <Cadence> request from main controls' -ForEach @(
        @{ Cadence = 'weekly-live'; Schedule = '0 6 * * 2'; Iterations = 1; Full = $true }
        @{ Cadence = 'weekly-mesh'; Schedule = '0 6 * * 6'; Iterations = 3; Full = $true }
        @{ Cadence = 'manual'; Schedule = ''; Iterations = 10; Full = $true }
        @{ Cadence = 'manual'; Schedule = ''; Iterations = 1; Full = $false }
    ) {
        $env:GITHUB_REF = 'refs/heads/main'
        $env:GITHUB_EVENT_NAME = if ($Schedule) { 'schedule' } else { 'workflow_dispatch' }
        @{ schedule = $Schedule; inputs = @{ full_integration = $Full.ToString().ToLowerInvariant(); iterations = "$Iterations" } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:eventFile
        & $requestWriter -Repository example/project -EventPath $script:eventFile -OutputPath $script:requestFile
        $request = Get-Content -LiteralPath $script:requestFile -Raw | ConvertFrom-Json
        $request.build_run_id | Should -BeExactly '42'
        $request.cadence | Should -BeExactly $Cadence
        $request.full_integration | Should -Be $Full
        $request.iterations | Should -Be $Iterations
    }

    It 'refuses to write requests from nonmain manual controls' {
        $env:GITHUB_REF = 'refs/heads/feature'
        $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
        { & $requestWriter -Repository example/project -EventPath $script:eventFile -OutputPath $script:requestFile } |
            Should -Throw '*main workflow controls*'
        Should -Invoke gh -Times 0 -Exactly
    }

    It 'rejects malformed manual repetition <_> before selecting a Build' -ForEach @('1.5', '0', '11') {
        $env:GITHUB_REF = 'refs/heads/main'
        $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
        @{ inputs = @{ full_integration = 'true'; iterations = $_ } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:eventFile
        { & $requestWriter -Repository example/project -EventPath $script:eventFile -OutputPath $script:requestFile } |
            Should -Throw '*1-10 integer repetitions*'
        Should -Invoke gh -Times 0 -Exactly
    }

    It 'rejects unknown schedules instead of silently choosing the Tuesday cadence' {
        $env:GITHUB_REF = 'refs/heads/main'
        $env:GITHUB_EVENT_NAME = 'schedule'
        @{ schedule = '0 6 * * 3' } | ConvertTo-Json | Set-Content -LiteralPath $script:eventFile
        { & $requestWriter -Repository example/project -EventPath $script:eventFile -OutputPath $script:requestFile } |
            Should -Throw '*Unknown Test request schedule*'
        Should -Invoke gh -Times 0 -Exactly
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
