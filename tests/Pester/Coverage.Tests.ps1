BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $coverageScript = Join-Path $PSScriptRoot '..\coverage_summary.ps1'
    $runner = Join-Path $PSScriptRoot '..\run-tests.ps1'
    $pwsh = Join-Path $PSHOME 'pwsh'
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
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $reports.FullName)
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\*\*Overall\*\* \| 2/2 \(100\.00%\) \| 2/2 \(100\.00%\)'
        $result.Output | Should -Not -Match 'Core.Tests'
    }

    It 'rejects missing production modules without printing an overall percentage' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'incomplete')
        '<CoverageSession><Modules><Module><ModuleName>DotReticulum.Core</ModuleName></Module></Modules></CoverageSession>' |
            Set-Content -LiteralPath (Join-Path $directory.FullName 'coverage.opencover.xml')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $directory.FullName)
        $result.Code | Should -Be 2
        $result.Error | Should -Match 'missing modules'
        $result.Output | Should -Not -Match '\*\*Overall\*\*'
    }

    It 'reports malformed coverage as a failure instead of a partial summary' {
        $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'malformed')
        '<CoverageSession>' | Set-Content -LiteralPath (Join-Path $directory.FullName 'coverage.opencover.xml')
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $coverageScript, $directory.FullName)
        $result.Code | Should -Be 2
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -Not -Match '\*\*Overall\*\*'
    }
}

Describe 'PowerShell test runner' -Tag Tooling {
    It 'rejects filtered coverage before starting a partial suite' {
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File', $runner, '-Coverage', '-Filter', 'Example')
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'full suite'
    }

    Describe 'Consolidated compatibility reporting' -Tag Tooling {
        It 'keeps every contract row and aligns OS results by ID, preserving missing and skipped evidence' {
            $directory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'compatibility')
            $catalog = Join-Path $directory.FullName 'catalog.json'
            @{
                reference = @{ name = 'Test reference'; revision = 'test-pin' }
                contracts = @(
                    @{ id = 'FIRST'; family = 'example'; title = 'Full first behavior | with delimiter' },
                    @{ id = 'SECOND'; family = 'example'; title = 'Full second behavior' }
                )
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $catalog
            $linux = New-Item -ItemType Directory -Path (Join-Path $directory.FullName 'compatibility-linux-x64-1')
            @(
                @{ Id = 'SECOND'; Status = 'Fail'; Detail = 'wrong output' },
                @{ Id = 'FIRST'; Status = 'Pass'; Detail = '' }
            ) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $linux.FullName 'compatibility.json')
            @(@{ Id = 'FIRST'; Status = 'Pass'; Revision = 'test-pin' }) |
                ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $linux.FullName 'upstream.json')
            $mac = New-Item -ItemType Directory -Path (Join-Path $directory.FullName 'compatibility-osx-arm64-1')
            @(@{ Id = 'FIRST'; Status = 'Skipped'; Detail = 'requires Linux' }) |
                ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $mac.FullName 'compatibility.json')
            $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
                (Join-Path $PSScriptRoot '..\compatibility_summary.ps1'),
                '-ReportsDirectory', $directory.FullName, '-Catalog', $catalog)
            $result.Code | Should -Be 0 -Because $result.Error
            $result.Output | Should -Match '\| Upstream \| .NET Linux \| .NET macOS \| .NET Windows \|'
            $result.Output | Should -Match 'Full first behavior &#124; with delimiter \| Pass \| Pass \| Skipped \| Unavailable'
            $result.Output | Should -Match 'Full second behavior \| Not run \| Fail \| Unavailable \| Unavailable'
            $result.Output | Should -Match 'FIRST.*macOS: requires Linux'
            $result.Output | Should -Match 'SECOND.*Linux: wrong output'
        }
    }

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
            (Join-Path $PSScriptRoot '..\test_summary.ps1'), '-Path', $input,
            '-JsonPath', (Join-Path $linux.FullName 'summary-native.json'))
        $export.Code | Should -Be 0 -Because $export.Error
        $result = Invoke-TestProcess $pwsh @('-NoProfile', '-File',
            (Join-Path $PSScriptRoot '..\test_platform_summary.ps1'),
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
