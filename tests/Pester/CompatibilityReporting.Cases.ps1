Describe 'Per-test compatibility reporting' -Tag Tooling {
    BeforeAll {
        $summaryScript = Join-Path $PSScriptRoot '..\Compatibility\Create-CompatibilitySummary.ps1'
        $canonical = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Interop\reference_environment.py') -Raw
        $pin = [regex]::Match($canonical, '(?m)^PINNED_RNS_SHA\s*=\s*"([0-9a-f]{40})"').Groups[1].Value
        $commit = '0000000000000000000000000000000000000001'
        function New-ReportRow([string] $Id = 'T001', [string] $Result = 'Passed') {
            return @{
                Id = $Id; ClaimId = 'C001'; Name = "Behavior $Id"; Result = $Result
                File = 'tests/Pester/Coverage.Tests.ps1'; Line = 1; Revision = $pin
            }
        }
        function Write-TestReport([string] $Rid, [array] $Rows, [string] $File = 'compatibility.json') {
            $directory = Join-Path $reportRoot "compatibility-$Rid-1"
            $null = New-Item -ItemType Directory -Path $directory -Force
            ConvertTo-Json -InputObject $Rows -Depth 5 | Set-Content -LiteralPath (Join-Path $directory $File)
        }
        function Invoke-CompatibilitySummary([string] $Script = $summaryScript) {
            Invoke-TestProcess $pwsh @('-NoProfile', '-File', $Script, '-ReportsDirectory', $reportRoot,
                '-Catalog', $catalog, '-Commit', $commit) -Environment @{ NO_COLOR = '1'; TERM = 'dumb' }
        }
    }

    BeforeEach {
        $reportRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $reportRoot
        $catalog = Join-Path $reportRoot 'catalog.json'
        @{ claims = @(
            @{ id = 'C001'; claim = 'Shared advertised behavior'; citation = @{ path = 'README.md'; lines = '1' } },
            @{ id = 'C002'; claim = 'No executable case'; citation = @{ path = 'README.md'; lines = '2' } }
        ) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $catalog
    }

    It 'aligns individual cases by stable ID, links source lines, and emits only the six-column table' {
        $first = New-ReportRow
        $first.Name = "Behavior | with`na delimiter"
        $second = New-ReportRow 'T002' 'Failed'
        Write-TestReport 'linux-x64' @($second, $first)
        $skipped = $first.Clone()
        $skipped.Result = 'Skipped'
        Write-TestReport 'osx-arm64' @($skipped) 'native.json'
        $baseline = $first.Clone()
        $baseline.Result = 'Failed'
        Write-TestReport 'upstream' @($baseline) 'upstream.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 0 -Because $result.Error
        $lines = @($result.Output.Trim() -split '\r?\n')
        $lines.Count | Should -Be 4
        $lines[0] | Should -Be '| Test ID | Behavior | Upstream | .NET Linux | .NET macOS | .NET Windows |'
        $lines[2] | Should -Match '\[`T001`\]\(https://github.com/[^)]+/blob/0000000000000000000000000000000000000001/tests/Pester/Coverage.Tests.ps1#L1\)'
        $lines[2] | Should -Match 'Behavior &#124; with a delimiter \| Failed \| Passed \| Skipped \| Unavailable \|$'
        $lines[3] | Should -Match 'Behavior T002 \| Not run \| Failed \| Unavailable \| Unavailable \|$'
        $result.Output | Should -Not -Match 'Family|C002|Shared advertised|###|Reference:'
    }

    It 'preserves every Pester outcome exactly instead of calculating claim verdicts' {
        $rows = @(
            New-ReportRow 'T001' 'Passed'
            New-ReportRow 'T002' 'Failed'
            New-ReportRow 'T003' 'Skipped'
            New-ReportRow 'T004' 'NotRun'
            New-ReportRow 'T005' 'Inconclusive'
        )
        Write-TestReport 'linux-x64' $rows
        Write-TestReport 'upstream' $rows 'upstream.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 0 -Because $result.Error
        foreach ($row in $rows) {
            $result.Output | Should -Match "Behavior $($row.Id) \| $($row.Result) \| $($row.Result) \| Unavailable \| Unavailable"
        }
        @($result.Output.Trim() -split '\r?\n').Count | Should -Be 7
    }

    It 'includes baseline-only cases without inventing native outcomes or synthetic advertised-claim rows' {
        $row = New-ReportRow
        $row.File = $null
        $row.Line = $null
        Write-TestReport 'upstream' @($row) 'upstream.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\| `T001` \| Behavior T001 \| Passed \| Unavailable \| Unavailable \| Unavailable'
        $result.Output | Should -Not -Match '\[`T001`\]|C002'
        @($result.Output.Trim() -split '\r?\n').Count | Should -Be 3
    }

    It 'never infers an upstream pass from a native pass and can link a test without a source line' {
        $row = New-ReportRow
        $row.Line = $null
        Write-TestReport 'win-x64' @($row)
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 0 -Because $result.Error
        $result.Output | Should -Match '\[`T001`\]\(https://github.com/[^)]+/Coverage.Tests.ps1\)'
        $result.Output | Should -Match 'Behavior T001 \| Not run \| Unavailable \| Unavailable \| Passed'
        $result.Output | Should -Not -Match '#L'
    }

    It 'emits only table headers when no actual test evidence exists' {
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 0 -Because $result.Error
        @($result.Output.Trim() -split '\r?\n').Count | Should -Be 2
        $result.Output | Should -Not -Match 'C00[12]|No executable|Shared advertised'
    }

    It 'rejects duplicate test IDs in a candidate report before emitting a table' {
        Write-TestReport 'linux-x64' @((New-ReportRow), (New-ReportRow))
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'Duplicate test ID'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects duplicate reusable upstream baseline evidence' {
        Write-TestReport 'upstream' @((New-ReportRow)) 'upstream.json'
        Write-TestReport 'linux-x64' @((New-ReportRow)) 'upstream.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'Multiple upstream baseline reports'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects two baseline sources even when their test IDs are disjoint' {
        Write-TestReport 'upstream' @((New-ReportRow 'T001')) 'upstream.json'
        Write-TestReport 'linux-x64' @((New-ReportRow 'T002')) 'upstream.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'Multiple upstream baseline reports'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects an empty <Candidate> report instead of presenting it as missing evidence' -ForEach @(
        @{ Candidate = 'linux-x64'; File = 'compatibility.json' }
        @{ Candidate = 'upstream'; File = 'upstream.json' }
    ) {
        Write-TestReport $Candidate @() $File
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'contains no Pester test records'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects multiple native reports for one platform rather than choosing an arbitrary run' {
        Write-TestReport 'linux-x64' @((New-ReportRow))
        Write-TestReport 'linux-x64' @((New-ReportRow)) 'native.json'
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'Multiple compatibility reports'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects a wrong canonical pin in <Candidate> evidence' -ForEach @(
        @{ Candidate = 'linux-x64'; File = 'compatibility.json' }
        @{ Candidate = 'upstream'; File = 'upstream.json' }
    ) {
        $row = New-ReportRow
        $row.Revision = '0000000000000000000000000000000000000002'
        Write-TestReport $Candidate @($row) $File
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $diagnostic = $result.Error -replace '(?m)^\s*\|\s*', ''
        $diagnostic | Should -Match 'does not match the canonical\s+compatibility\s+pin'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects mixed revisions inside a candidate report' {
        $wrong = New-ReportRow 'T002'
        $wrong.Revision = '0000000000000000000000000000000000000002'
        Write-TestReport 'linux-x64' @((New-ReportRow), $wrong)
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'does not match'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects mismatched definitions associated with the same stable ID' {
        Write-TestReport 'linux-x64' @((New-ReportRow))
        $different = New-ReportRow
        $different.Name = 'An unrelated behavior'
        Write-TestReport 'osx-arm64' @($different)
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'Inconsistent Name'
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects malformed <Problem> test records without a partial table' -ForEach @(
        @{ Problem = 'missing field'; Field = 'Name'; Value = $null; Remove = $true }
        @{ Problem = 'missing identity'; Field = 'Id'; Value = ''; Remove = $false }
        @{ Problem = 'claim identity used as test identity'; Field = 'Id'; Value = 'C001'; Remove = $false }
        @{ Problem = 'unknown claim'; Field = 'ClaimId'; Value = 'C999'; Remove = $false }
        @{ Problem = 'test identity without numeric suffix'; Field = 'Id'; Value = 'TEXAMPLE'; Remove = $false }
        @{ Problem = 'old aggregate status'; Field = 'Result'; Value = 'Pass'; Remove = $false }
        @{ Problem = 'array outcome'; Field = 'Result'; Value = @('Passed'); Remove = $false }
        @{ Problem = 'array revision'; Field = 'Revision'; Value = @('not-a-pin'); Remove = $false }
        @{ Problem = 'empty name'; Field = 'Name'; Value = ''; Remove = $false }
        @{ Problem = 'escaping source path'; Field = 'File'; Value = 'tests/../Example.Tests.ps1'; Remove = $false }
        @{ Problem = 'absolute source path'; Field = 'File'; Value = 'C:\Example.Tests.ps1'; Remove = $false }
        @{ Problem = 'non-test source path'; Field = 'File'; Value = 'tests/Compatibility/Scenarios.ps1'; Remove = $false }
        @{ Problem = 'nonpositive source line'; Field = 'Line'; Value = 0; Remove = $false }
        @{ Problem = 'string source line'; Field = 'Line'; Value = '12'; Remove = $false }
    ) {
        $row = New-ReportRow
        if ($Remove) { $row.Remove($Field) } else { $row[$Field] = $Value }
        Write-TestReport 'linux-x64' @($row)
        $result = Invoke-CompatibilitySummary
        $result.Code | Should -Be 1
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Output | Should -BeNullOrEmpty
    }

    It 'rejects malformed JSON and a singleton object masquerading as an array' {
        Write-TestReport 'linux-x64' @((New-ReportRow))
        $path = Join-Path $reportRoot 'compatibility-linux-x64-1\compatibility.json'
        foreach ($json in '{invalid', ((New-ReportRow) | ConvertTo-Json)) {
            Set-Content -LiteralPath $path -Value $json
            $result = Invoke-CompatibilitySummary
            $result.Code | Should -Be 1
            $result.Error | Should -Not -BeNullOrEmpty
            $result.Output | Should -BeNullOrEmpty
        }
    }
}
