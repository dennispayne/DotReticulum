BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $coverageScript = Join-Path $PSScriptRoot '..\Get-CoverageSummary.ps1'
    $runner = Join-Path $PSScriptRoot '..\Invoke-Tests.ps1'
    $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
}

Describe 'PowerShell coverage reporting' -Tag Tooling {
    It 'merges complementary line and branch hits once and excludes test assemblies' {
        $projects = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\..\src') -Directory |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.csproj' }
        $source = [System.Security.SecurityElement]::Escape((Join-Path $TestDrive 'Example.cs'))
        $reports = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'reports')
        foreach ($index in 0, 1) {
            $modules = foreach ($project in $projects) {
                $name = $project.BaseName
                if ($name -ne 'DotReticulum.Core') { "<Module><ModuleName>$name</ModuleName></Module>"; continue }
                @"
<Module><ModuleName>$name</ModuleName><Files><File uid="1" fullPath="$source"/></Files>
<Classes><Class><Methods><Method><Name>Example.Run()</Name><FileRef uid="1"/>
<SequencePoints><SequencePoint sl="10" vc="$index"/><SequencePoint sl="11" vc="$(1 - $index)"/></SequencePoints>
<BranchPoints><BranchPoint sl="10" offset="0" path="0" offsetend="2" vc="$index"/>
<BranchPoint sl="10" offset="0" path="1" offsetend="4" vc="$(1 - $index)"/></BranchPoints>
</Method></Methods></Class></Classes></Module>
"@
            }
            $directory = New-Item -ItemType Directory -Path (Join-Path $reports.FullName "report-$index")
            "<CoverageSession><Modules>$($modules -join '')<Module><ModuleName>DotReticulum.Core.Tests</ModuleName></Module></Modules></CoverageSession>" |
                Set-Content -LiteralPath (Join-Path $directory.FullName 'coverage.opencover.xml')
        }
        $json = Join-Path $TestDrive 'merged.json'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $reports.FullName, '-JsonPath', $json)
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\*\*Overall\*\* \| 2/2 \(100\.00%\) \| 2/2 \(100\.00%\)'
        $result.Output | Should -Not -Match 'Core.Tests'
        $merged = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
        $merged.ReportCount | Should -Be 2
        $overall = $merged.Rows | Where-Object Name -EQ Overall
        $overall.Lines.Hit | Should -Be 2
        $overall.Branches.Total | Should -Be 2

        $platforms = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'platforms')
        $linux = New-Item -ItemType Directory -Path (Join-Path $platforms.FullName 'test-results-linux-x64-1')
        foreach ($suite in 'unit', 'integration') {
            $index = if ($suite -eq 'unit') { 0 } else { 1 }
            $export = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript,
                (Join-Path $reports.FullName "report-$index"), '-JsonPath', (Join-Path $linux.FullName "coverage-$suite.json"))
            $export.Code | Should -Be 0 -Because $export.Error
            $export.Output | Should -Match '\*\*Overall\*\* \| 1/2 \(50\.00%\) \| 1/2 \(50\.00%\)'
        }
        $summary = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'), '-ReportsDirectory', $platforms.FullName)
        $summary.Code | Should -Be 0 -Because $summary.Error
        [regex]::Matches($summary.Output, '\| Overall \| 1/2 \(50\.00%\) / 1/2 \(50\.00%\) \| Unavailable \| Unavailable \|').Count | Should -Be 2
        $summary.Output | Should -Not -Match '100\.00%'
    }

    It 'rejects missing production modules without printing an overall percentage' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'incomplete')
        '<CoverageSession><Modules><Module><ModuleName>DotReticulum.Core</ModuleName></Module></Modules></CoverageSession>' |
            Set-Content -LiteralPath (Join-Path $directory.FullName 'coverage.opencover.xml')
        $json = Join-Path $TestDrive 'invalid.json'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $directory.FullName, '-JsonPath', $json)
        $result.Code | Should -Be 2
        $result.Error | Should -Match 'missing modules'
        $result.Output | Should -Not -Match '\*\*Overall\*\*'
        Test-Path -LiteralPath $json | Should -BeFalse
    }

    It 'reports malformed coverage as a failure instead of a partial summary' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'malformed')
        '<CoverageSession>' | Set-Content -LiteralPath (Join-Path $directory.FullName 'coverage.opencover.xml')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $directory.FullName)
        $result.Code | Should -Be 2
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -Not -Match '\*\*Overall\*\*'
    }

    It 'rejects invalid platform coverage rather than displaying a fabricated percentage' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'bad-platform-coverage')
        $platform = New-Item -ItemType Directory -Path (Join-Path $directory.FullName 'test-results-linux-x64-1')
        @{
            ReportCount = 1
            Rows = @(@{ Name = 'Overall'; Lines = @{ Hit = 2; Total = 1 }; Branches = @{ Hit = 0; Total = 0 } })
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $platform.FullName 'coverage-unit.json')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'), '-ReportsDirectory', $directory.FullName)
        $result.Code | Should -Not -Be 0
        $result.Error | Should -Match 'Invalid coverage hit/total counts'
        $result.Output | Should -Not -Match '200\.00%'
    }
}

Describe 'PowerShell test runner' -Tag Tooling {
    It 'keeps standalone script verbs approved and comment-based help discoverable' {
        $root = Join-Path $PSScriptRoot '..\..'
        $directories = 'tests', 'tests\Compatibility', 'tests\Interop', 'tools', 'assets\branding'
        $approvedVerbs = (Get-Verb).Verb
        $scripts = @($directories | ForEach-Object {
            Get-ChildItem -LiteralPath (Join-Path $root $_) -File -Filter '*.ps1'
        })
        $scripts.Count | Should -BeGreaterThan 0
        foreach ($script in $scripts) {
            $script.BaseName | Should -Match '^[A-Z][a-z]+-[A-Z][A-Za-z0-9]+$'
            $script.BaseName.Split('-')[0] | Should -BeIn $approvedVerbs -Because $script.Name
            $help = Get-Help $script.FullName -Full
            $help.Description | Should -Not -BeNullOrEmpty -Because $script.Name
            $help.Examples.Example.Count | Should -BeGreaterThan 0 -Because $script.Name
        }
    }

    It 'isolates child test processes from the real Actions summary but allows explicit owned output paths' {
        $summary = Join-Path $TestDrive 'summary.md'
        $saved = $env:GITHUB_STEP_SUMMARY
        $command = 'if ($env:GITHUB_STEP_SUMMARY) { [IO.File]::WriteAllText($env:GITHUB_STEP_SUMMARY, "child output") }'
        try {
            $env:GITHUB_STEP_SUMMARY = $summary
            $result = Invoke-TestProcess $pwsh @('-NoProfile', '-Command', $command)
            $result.Code | Should -Be 0 -Because $result.Error
            Test-Path -LiteralPath $summary | Should -BeFalse
            $result = Invoke-TestProcess $pwsh @('-NoProfile', '-Command', $command) -Environment @{ GITHUB_STEP_SUMMARY = $summary }
            $result.Code | Should -Be 0 -Because $result.Error
            Get-Content -LiteralPath $summary -Raw | Should -Be 'child output'
        }
        finally { $env:GITHUB_STEP_SUMMARY = $saved }
    }

    It 'rejects filtered coverage before starting a partial suite' {
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner, '-Coverage', '-Filter', 'Example')
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'full suite'
    }

    . (Join-Path $PSScriptRoot 'CompatibilityReporting.Cases.ps1')
    . (Join-Path $PSScriptRoot '..\Compatibility\Support\Harness.Tests.ps1')

    It 'refuses to reuse a coverage results directory' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'existing')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner, '-Coverage', '-ResultsDirectory', $directory.FullName)
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'fresh coverage directory'
    }

    It 'exports test counts and combines platform tables without losing failure or skip details' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'test-platforms')
        $linux = New-Item -ItemType Directory -Path (Join-Path $directory.FullName 'test-results-linux-x64-1')
        $input = Join-Path $linux.FullName 'cli.xml'
        @'
<test-results><test-suite><results>
<test-case name="success" result="Success"/>
<test-case name="failure" result="Failure"><failure><message>wrong | value</message></failure></test-case>
<test-case name="skip" result="Ignored"><reason><message>requires device</message></reason></test-case>
</results></test-suite></test-results>
'@ | Set-Content -LiteralPath $input
        $export = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestSummary.ps1'), '-Path', $input,
            '-JsonPath', (Join-Path $linux.FullName 'summary-native.json'))
        $export.Code | Should -Be 0 -Because $export.Error
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'),
            '-ReportsDirectory', $directory.FullName)
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\| cli \| 1 / 1 / 1 \| Unavailable \| Unavailable'
        $result.Output | Should -Match 'Linux.*failure.*wrong &#124; value'
        $result.Output | Should -Match 'Linux.*skipped \(1\).*requires device'
        $result.Output | Should -Match '#### Unit tests'
        $result.Output | Should -Match '#### Integration tests'
        $result.Output | Should -Match '#### Native tests'
    }
}
