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
    It 'keeps standalone script verbs approved and help discoverable for every declared parameter' {
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
            $command = Get-Command $script.FullName
            foreach ($parameter in $command.ScriptBlock.Ast.ParamBlock.Parameters) {
                $name = $parameter.Name.VariablePath.UserPath
                $parameterHelp = @($help.Parameters.Parameter | Where-Object Name -EQ $name)
                $parameterHelp.Count | Should -Be 1 -Because "$($script.Name) -$name"
                ($parameterHelp[0].Description.Text -join ' ').Trim() |
                    Should -Not -BeNullOrEmpty -Because "$($script.Name) -$name"
            }
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

    It 'calculates assembly versions without writing shared Actions command files' {
        $environmentFile = Join-Path $TestDrive 'version-env.txt'
        $outputFile = Join-Path $TestDrive 'version-output.txt'
        foreach ($file in $environmentFile, $outputFile) {
            [IO.File]::WriteAllText($file, "OWNED_SENTINEL=unchanged`n")
        }
        $project = Join-Path $PSScriptRoot '..\..\src\DotReticulum.Applications\DotReticulum.Applications.csproj'
        $result = Invoke-TestProcess 'dotnet' @('msbuild', $project, '-nologo',
            '-t:GetBuildVersion', '-p:Configuration=Release',
            '-getProperty:BuildVersion,AssemblyVersion,AssemblyInformationalVersion') -Environment @{
                GITHUB_ACTIONS = 'true'
                GITHUB_ENV = $environmentFile
                GITHUB_OUTPUT = $outputFile
            }
        $result.Code | Should -Be 0 -Because $result.Error
        $versions = ($result.Output | ConvertFrom-Json).Properties
        $versions.BuildVersion | Should -Match '^\d+\.\d+\.\d+\.\d+$'
        $versions.AssemblyVersion | Should -BeExactly $versions.BuildVersion
        $versions.AssemblyInformationalVersion | Should -BeLike "$($versions.BuildVersion)*"
        foreach ($file in $environmentFile, $outputFile) {
            [IO.File]::ReadAllText($file) | Should -BeExactly "OWNED_SENTINEL=unchanged`n"
        }
    }

    It 'preserves cmd environment setup and quoted paths in the bounded native publisher' -Skip:(!$IsWindows) {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'native tools')
        $setup = Join-Path $directory.FullName 'setup.cmd'
        @('@echo off', 'set DOTRETICULUM_PREPARATION_TEST=owned') | Set-Content -LiteralPath $setup
        $result = Invoke-TestProcess $env:ComSpec -RawArguments "/d /s /c `"call `"$setup`" && set DOTRETICULUM_PREPARATION_TEST`""
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output.Trim() | Should -Be 'DOTRETICULUM_PREPARATION_TEST=owned'
        $env:DOTRETICULUM_PREPARATION_TEST | Should -BeNullOrEmpty
        { Invoke-TestProcess $env:ComSpec @('/c', 'echo ignored') -RawArguments '/c echo invalid' } |
            Should -Throw '*not both*'
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

    It 'keeps repeated extended iterations separate without accepting duplicate rows within one iteration' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'repeated-platforms')
        $linux = New-Item -ItemType Directory -Path (Join-Path $directory.FullName 'test-results-linux-x64-1')
        $extended = New-Item -ItemType Directory -Path (Join-Path $linux.FullName 'extended')
        foreach ($iteration in 1, 2) {
            $group = New-Item -ItemType Directory -Path (Join-Path $extended.FullName "iteration-$iteration")
            $cases = if ($iteration -eq 1) { '<test-case name="mesh" result="Success"/>' } else {
                '<test-case name="mesh" result="Failure"><failure><message>actual resource failure</message></failure></test-case>' +
                '<test-case name="device" result="Ignored"><reason><message>requires hardware</message></reason></test-case>'
            }
            "<test-results><test-suite><results>$cases</results></test-suite></test-results>" |
                Set-Content -LiteralPath (Join-Path $group.FullName 'mesh.xml')
        }
        $json = Join-Path $linux.FullName 'summary-extended.json'
        $export = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestSummary.ps1'), '-Path', $extended.FullName,
            '-IncludeParentDirectory', '-JsonPath', $json)
        $export.Code | Should -Be 0 -Because $export.Error
        $report = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
        $report.Rows.Count | Should -Be 2
        $report.Failures[0].Name | Should -BeExactly 'iteration-2 / mesh'
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'), '-ReportsDirectory', $directory.FullName)
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\| iteration-1 / mesh \| 1 / 0 / 0 \| Unavailable \| Unavailable'
        $result.Output | Should -Match '\| iteration-2 / mesh \| 0 / 1 / 1 \| Unavailable \| Unavailable'
        $result.Output | Should -Match 'Linux.*iteration-2 / mesh.*actual resource failure'
        $result.Output | Should -Match 'Linux.*requires hardware'
        $report.Rows += $report.Rows[0]
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $json
        $duplicate = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'), '-ReportsDirectory', $directory.FullName)
        $duplicate.Code | Should -Not -Be 0
        $duplicate.Error | Should -Match 'Duplicate extended row iteration-1 / mesh'
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
        $extendedInput = Join-Path $linux.FullName 'extended.xml'
        @'
<test-results><test-suite><results>
<test-case name="live" result="Success"/>
<test-case name="hardware" result="Ignored"><reason><message>requires physical adapter</message></reason></test-case>
</results></test-suite></test-results>
'@ | Set-Content -LiteralPath $extendedInput
        $export = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestSummary.ps1'), '-Path', $extendedInput,
            '-JsonPath', (Join-Path $linux.FullName 'summary-extended.json'))
        $export.Code | Should -Be 0 -Because $export.Error
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\Get-TestPlatformSummary.ps1'),
            '-ReportsDirectory', $directory.FullName)
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '#### Extended tests'
        $result.Output | Should -Match '\| extended \| 1 / 0 / 1 \| Unavailable \| Unavailable'
        $result.Output | Should -Match '\| cli \| 1 / 1 / 1 \| Unavailable \| Unavailable'
        $result.Output | Should -Match 'Linux.*requires physical adapter'
    }
}
