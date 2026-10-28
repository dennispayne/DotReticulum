param(
    [string] $Application,
    [string] $Python,
    [string] $Upstream
)

Describe 'Compatibility harness' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Discovery.ps1')
        . (Join-Path $PSScriptRoot 'Export.ps1')
        . (Join-Path $PSScriptRoot '..\..\Pester\TestProcess.ps1')
        $harnessRunner = Join-Path $PSScriptRoot '..\Run-CompatibilityTests.ps1'
        $harnessWork = Join-Path $PSScriptRoot "..\..\..\TestResults\compatibility-harness-$([guid]::NewGuid().ToString('N'))"
        $null = New-Item -ItemType Directory -Path $harnessWork
        $harnessApplication = if ($Application) { (Resolve-Path -LiteralPath $Application).Path } else { $null }
        $harnessPython = if ($Python) { (Resolve-Path -LiteralPath $Python).Path } else { $null }
        $harnessUpstream = if ($Upstream) { (Resolve-Path -LiteralPath $Upstream).Path } else { $null }
    }

    AfterAll {
        if ($harnessWork -and (Test-Path -LiteralPath $harnessWork)) {
            Remove-Item -LiteralPath $harnessWork -Recurse -Force
        }
    }

    Context 'Compatibility discovery contracts' {
        It 'includes every claim test file in both discovery and execution containers' {
            $metadata = @(Get-CompatibilityDiscovery)
            $expected = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\Claims') -Filter '*.Tests.ps1' -File |
                ForEach-Object { "tests/Compatibility/Claims/$($_.Name)" } | Sort-Object)
            $actual = @($metadata.File | Sort-Object -Unique)
            $actual | Should -Be $expected
        }

        It 'rejects a suite with no discovered tests' {
            { Get-CompatibilityTestMetadata @() } | Should -Throw '*No compatibility tests*'
        }

        It 'discovers seven real tests and exposes five uncovered claims without executing anything' {
            $rows = @(& (Join-Path $PSScriptRoot '..\List-CompatibilityTests.ps1') -IncludeUncoveredClaims)
            @($rows | Where-Object TestId).Count | Should -Be 7
            @($rows | Where-Object { !$_.TestId }).Count | Should -Be 5
            @($rows | Where-Object { $_.PSObject.Properties.Name -contains 'Result' }).Count | Should -Be 0
            @($rows | Where-Object TestId | Select-Object -ExpandProperty TestId -Unique).Count | Should -Be 7
        }

        It 'joins quoted claim text and file-line citations without conflating them with test names' {
            $rows = @(& (Join-Path $PSScriptRoot '..\List-CompatibilityTests.ps1') -IncludeUncoveredClaims)
            $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\catalog.json') -Raw | ConvertFrom-Json
            foreach ($claim in $catalog.claims) {
                $joined = @($rows | Where-Object ClaimId -EQ $claim.id)
                $joined.Count | Should -BeGreaterThan 0
                foreach ($row in $joined) {
                    $row.Claim | Should -BeExactly $claim.claim
                    $row.Citation | Should -BeExactly "$($claim.citation.path):$($claim.citation.lines)"
                    $row.PSObject.Properties.Name | Should -Not -Contain 'UpstreamCitation'
                    $row.PSObject.Properties.Name | Should -Not -Contain 'Citations'
                    $claim.citation | Should -Not -BeOfType ([array])
                    if ($row.TestId) { $row.Name | Should -Not -BeNullOrEmpty }
                    else { $row.Name | Should -BeNullOrEmpty }
                }
            }
        }

        It 'rejects duplicate stable test IDs' {
            $tests = @(Get-CompatibilityDiscovery)
            { Get-CompatibilityTestMetadata @($tests[0].Test, $tests[0].Test) } | Should -Throw '*Duplicate test ID*'
        }

        It 'rejects missing stable test IDs' {
            $test = (Get-CompatibilityDiscovery)[0].Test
            $test.Tag = @('C001')
            { Get-CompatibilityTestMetadata @($test) } | Should -Throw '*requires exactly one stable Tnnn*'
        }

        It 'rejects unknown claim IDs' {
            $test = (Get-CompatibilityDiscovery)[0].Test
            $test.Tag = @('T001', 'C999')
            { Get-CompatibilityTestMetadata @($test) } | Should -Throw '*known Cnnn*'
        }
    }

    Context 'Compatibility runner failure and candidate isolation' {
        It 'records actual Pester failures rather than success when the reference is absent' {
            if (!$harnessApplication) { Set-ItResult -Skipped -Because 'Supply a published native -Application.'; return }
            $directory = Join-Path $harnessWork 'missing-reference'
            $result = Invoke-TestProcess pwsh @('-NoProfile', '-File', $harnessRunner, '-Application', $harnessApplication,
                '-Implementation', 'DotReticulum', '-Python', '', '-Upstream', '', '-OutputDirectory', $directory, '-NoJobSummary')
            $result.Code | Should -Be 1
            $rows = @(Get-Content -LiteralPath (Join-Path $directory 'compatibility.json') -Raw | ConvertFrom-Json)
            $rows.Count | Should -Be 7
            @($rows | Where-Object Result -EQ Passed).Count | Should -Be 0
            @($rows | Where-Object Result -EQ Failed).Count | Should -BeGreaterThan 0
            (Join-Path $directory 'DotReticulum.nunit.xml') | Should -Exist
            (Join-Path $directory 'upstream.json') | Should -Not -Exist
        }

        It 'preserves failed retrieval and all dependent Pester results without success-shaped provenance' {
            if (!$harnessApplication) { Set-ItResult -Skipped -Because 'Supply a published native -Application.'; return }
            $directory = Join-Path $harnessWork 'failed-retrieval'
            $reference = Join-Path $harnessWork 'unfetched'
            $result = Invoke-TestProcess pwsh @('-NoProfile', '-File', $harnessRunner, '-Application', $harnessApplication,
                '-Implementation', 'DotReticulum', '-FetchReference', '-ReferenceDirectory', $reference,
                '-Gateway', 'invalid/gateway', '-OutputDirectory', $directory, '-NoJobSummary')
            $result.Code | Should -Be 1
            $rows = @(Get-Content -LiteralPath (Join-Path $directory 'compatibility.json') -Raw | ConvertFrom-Json)
            $rows.Count | Should -Be 7
            ($rows | Where-Object Id -EQ T001).Result | Should -BeExactly Failed
            @($rows | Where-Object Result -EQ Passed).Count | Should -Be 0
            (Join-Path $reference 'reference-provenance.json') | Should -Not -Exist
            (Join-Path $directory 'DotReticulum\retrieval-provenance.json') | Should -Not -Exist
        }

        It 'retains a real case failure and continues unrelated cases for both candidates' {
            if (!$harnessApplication -or !$harnessPython -or !$harnessUpstream) {
                Set-ItResult -Skipped -Because 'Supply -Application, -Python and -Upstream to exercise real candidates.'
                return
            }
            foreach ($candidate in 'DotReticulum', 'Upstream') {
                $directory = Join-Path $harnessWork "case-failure-$candidate"
                $null = New-Item -ItemType Directory -Path (Join-Path $directory 'T002') -Force
                $context = @{
                    Application = $harnessApplication; Python = $harnessPython; Upstream = $harnessUpstream
                    Revision = Get-CompatibilityReferenceRevision
                    Implementation = $candidate; OutputDirectory = $directory
                }
                $configuration = New-CompatibilityConfiguration $context
                $configuration.Run.Container = @(New-PesterContainer -Path (Join-Path $PSScriptRoot '..\Claims\Addressing.Tests.ps1') -Data @{ Context = $context })
                $result = Invoke-Pester -Configuration $configuration
                $path = Join-Path $directory 'results.json'
                Export-CompatibilityResults $result $context.Revision $path
                $rows = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
                ($rows | Where-Object Id -EQ T002).Result | Should -BeExactly Failed
                ($rows | Where-Object Id -EQ T003).Result | Should -BeExactly Passed
            }
        }

        It 'runs an upstream-only baseline without creating native records' {
            if (!$harnessPython -or !$harnessUpstream) {
                Set-ItResult -Skipped -Because 'Supply the verified -Python and -Upstream reference.'
                return
            }
            $directory = Join-Path $harnessWork 'baseline-only'
            $result = Invoke-TestProcess pwsh @('-NoProfile', '-File', $harnessRunner, '-Implementation', 'Upstream',
                '-Python', $harnessPython, '-Upstream', $harnessUpstream, '-OutputDirectory', $directory, '-NoJobSummary')
            $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
            (Join-Path $directory 'compatibility.json') | Should -Not -Exist
            $rows = @(Get-Content -LiteralPath (Join-Path $directory 'upstream.json') -Raw | ConvertFrom-Json)
            @($rows | Where-Object Result -EQ Passed).Count | Should -Be 6
            ($rows | Where-Object Id -EQ T001).Result | Should -BeExactly Skipped
            (Join-Path $directory 'Upstream.nunit.xml') | Should -Exist
        }
    }
}
