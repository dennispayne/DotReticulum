BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $analysis = Join-Path $PSScriptRoot '..\..\tools\Invoke-PowerShellAnalysis.ps1'
    $summary = Join-Path $PSScriptRoot '..\..\tools\Get-StaticAnalysisSummary.ps1'

    function New-AnalysisFixture([string] $Directory, [string] $Content) {
        $null = New-Item -ItemType Directory -Path $Directory
        Set-Content -LiteralPath (Join-Path $Directory 'Invoke-Fixture.ps1') -Value $Content
        & git -C $Directory init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Fixture Git initialization failed.' }
        & git -C $Directory add -- Invoke-Fixture.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Fixture Git staging failed.' }
    }

    function New-SarifFixture([string] $Directory) {
        $null = New-Item -ItemType Directory -Force -Path $Directory
        $runs = @(foreach ($language in 'actions', 'csharp', 'python', 'powershell') {
            $tool = if ($language -eq 'powershell') { 'PSScriptAnalyzer' } else { 'CodeQL' }
            $results = @()
            if ($language -eq 'python') {
                $results = @(@{
                    ruleId = 'test/rule'
                    message = @{ text = "message | <unsafe>`nnext line" }
                    locations = @(@{ physicalLocation = @{
                        artifactLocation = @{ uri = 'tests/example.py' }
                        region = @{ startLine = 7 }
                    } })
                })
            }
            @{
                tool = @{ driver = @{ name = $tool; rules = @(@{ id = 'test/rule'; defaultConfiguration = @{ level = 'error' } }) } }
                automationDetails = @{ id = "/language:$language/" }
                results = $results
            }
        })
        @{ version = '2.1.0'; runs = $runs } | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath (Join-Path $Directory 'results.sarif')
    }
}

Describe 'Same-run native artifact selection' -Tag Tooling {
    It 'exports the verified <Variable> executable' -ForEach @(
        @{ Variable = 'DOTRETICULUM_NATIVE_EXE' }
        @{ Variable = 'DOTRETICULUM_BUFFER_CONSUMER' }
    ) {
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $directory
        $binary = Join-Path $directory 'candidate.exe'
        Set-Content -LiteralPath $binary -Value 'Not executed by artifact validation.'
        @{ rid = 'test-rid'; commit = 'verified-commit'; executable = 'candidate.exe' } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'build-manifest.json')
        $environmentFile = Join-Path $directory 'github-env'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot '..\Set-NativeBuild.ps1'),
            '-Directory', $directory, '-Rid', 'test-rid', '-Commit', 'verified-commit', '-EnvironmentVariable', $Variable) `
            -Environment @{ GITHUB_ENV = $environmentFile }
        $result.Code | Should -Be 0 -Because $result.Error
        (Get-Content -LiteralPath $environmentFile -Raw).Trim() | Should -Be "$Variable=$binary"
    }

    It 'rejects a consumer artifact with <Problem> before exporting it' -ForEach @(
        @{ Problem = 'a mismatched source commit'; Commit = 'other-commit'; Binary = $true; Expected = 'Artifact was built from' }
        @{ Problem = 'a missing executable'; Commit = 'verified-commit'; Binary = $false; Expected = 'is missing from the artifact' }
    ) {
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $directory
        if ($Binary) { Set-Content -LiteralPath (Join-Path $directory 'candidate.exe') -Value 'Not executed.' }
        @{ rid = 'test-rid'; commit = $Commit; executable = 'candidate.exe' } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'build-manifest.json')
        $environmentFile = Join-Path $directory 'github-env'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot '..\Set-NativeBuild.ps1'),
            '-Directory', $directory, '-Rid', 'test-rid', '-Commit', 'verified-commit', '-EnvironmentVariable', 'DOTRETICULUM_BUFFER_CONSUMER') `
            -Environment @{ GITHUB_ENV = $environmentFile }
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match $Expected
        Test-Path -LiteralPath $environmentFile | Should -BeFalse
    }
}

Describe 'Static analysis reporting' -Tag Tooling {
    It 'reports findings separately from successful jobs and uses rule-default severity' {
        $directory = Join-Path $TestDrive 'reports'
        New-SarifFixture $directory
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $summary,
            '-ReportsDirectory', $directory, '-CodeQLStatus', 'success', '-PowerShellStatus', 'success')
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\| CodeQL \| Python \| success \| 1 \| 0 \| 0 \| 0 \|'
        $result.Output | Should -Match '\| PSScriptAnalyzer \| PowerShell \| success \| 0 \| 0 \| 0 \| 0 \|'
        $result.Output | Should -Match 'message &#124; &lt;unsafe&gt; next line'
    }

    It 'marks absent or failed analyses unavailable rather than passing with zero findings' {
        $directory = Join-Path $TestDrive 'empty'
        $null = New-Item -ItemType Directory -Path $directory
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $summary,
            '-ReportsDirectory', $directory, '-CodeQLStatus', 'failure', '-PowerShellStatus', 'failure')
        $result.Code | Should -Be 0 -Because $result.Error
        [regex]::Matches($result.Output, 'failure; incomplete analysis \| Unavailable').Count | Should -Be 4
        New-SarifFixture $directory
        $file = Join-Path $directory 'results.sarif'
        $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
        $document.runs[1].invocations = @(@{ executionSuccessful = $false })
        $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $file
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $summary,
            '-ReportsDirectory', $directory, '-CodeQLStatus', 'failure', '-PowerShellStatus', 'success')
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\| CodeQL \| C# \| failure; incomplete analysis \| Unavailable'
        $result.Output | Should -Match '\| CodeQL \| Python \| failure \| 1 \| 0 \| 0 \| 0 \|'
    }

    It 'rejects duplicate or malformed SARIF instead of emitting a partial success table' {
        $directory = Join-Path $TestDrive 'invalid'
        New-SarifFixture $directory
        Copy-Item -LiteralPath (Join-Path $directory 'results.sarif') -Destination (Join-Path $directory 'duplicate.sarif')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $summary, '-ReportsDirectory', $directory)
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'Duplicate analysis category'
        $result.Output | Should -Not -Match '### Static analysis'
        Set-Content -LiteralPath (Join-Path $directory 'duplicate.sarif') -Value '{bad json'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $summary, '-ReportsDirectory', $directory)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Not -Match '### Static analysis'
    }

    It 'writes clean and failing analyzer reports from tracked files without executing scripts' {
        if (!(Get-Module -ListAvailable PSScriptAnalyzer | Where-Object Version -EQ '1.24.0')) {
            Set-ItResult -Skipped -Because 'Install PSScriptAnalyzer 1.24.0 to exercise the actual analyzer.'
            return
        }
        $clean = Join-Path $TestDrive 'clean'
        New-AnalysisFixture $clean 'throw "Static analysis must not execute this script."'
        Set-Content -LiteralPath (Join-Path $clean 'untracked.ps1') -Value 'Invoke-Expression "untracked"'
        $report = Join-Path $TestDrive 'clean.sarif'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $analysis, '-Repository', $clean, '-Report', $report)
        $result.Code | Should -Be 0 -Because $result.Error
        $document = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $document.runs[0].automationDetails.id | Should -Be '/language:powershell/'
        $document.runs[0].results.Count | Should -Be 0

        $bad = Join-Path $TestDrive 'bad'
        New-AnalysisFixture $bad 'Invoke-Expression "not executed"'
        $report = Join-Path $TestDrive 'bad.sarif'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $analysis, '-Repository', $bad, '-Report', $report)
        $result.Code | Should -Be 1
        $document = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $finding = $document.runs[0].results | Where-Object ruleId -EQ PSAvoidUsingInvokeExpression
        $finding.level | Should -Be 'warning'
        $finding.locations[0].physicalLocation.artifactLocation.uri | Should -Be 'Invoke-Fixture.ps1'
        $finding.locations[0].physicalLocation.region.startLine | Should -Be 1

        Set-Content -LiteralPath (Join-Path $bad 'Invoke-Fixture.ps1') -Value 'function Invoke-Fixture {'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $analysis, '-Repository', $bad, '-Report', $report)
        $result.Code | Should -Be 1
        $document = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        @($document.runs[0].results | Where-Object level -EQ error).Count | Should -BeGreaterThan 0
    }
}
