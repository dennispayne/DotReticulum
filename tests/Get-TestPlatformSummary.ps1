#Requires -Version 7.0

<#
.SYNOPSIS
Combines saved test summaries into cross-platform Markdown tables.
.DESCRIPTION
Reads summary JSON artifacts beneath ReportsDirectory and reports platform
counts, failures and skips. Missing platforms remain explicitly unavailable.
.EXAMPLE
.\Get-TestPlatformSummary.ps1 -ReportsDirectory ..\TestResults\platforms
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $ReportsDirectory)

$ErrorActionPreference = 'Stop'
$platforms = [ordered]@{ 'linux-x64' = 'Linux'; 'osx-arm64' = 'macOS'; 'win-x64' = 'Windows' }

function Escape-Cell([string] $Value) {
    return ($Value -replace '\|', '&#124;' -replace '[\r\n]+', ' ')
}

'### Test results - all platforms'
''
'Cells show **passed / failed / skipped**. Unavailable means no result was received.'
foreach ($suite in 'unit', 'integration', 'native') {
    $reports = @{}
    $names = [Collections.Generic.HashSet[string]]::new()
    $details = [Collections.Generic.List[string]]::new()
    foreach ($rid in $platforms.Keys) {
        $files = @(Get-ChildItem -LiteralPath $ReportsDirectory -Directory -Filter "test-results-$rid-*" |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter "summary-$suite.json" })
        if ($files.Count -gt 1) { throw "Multiple $suite reports for $rid." }
        $reports[$rid] = @{}
        if ($files.Count -eq 0) { continue }
        $report = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
        foreach ($row in $report.Rows) {
            $null = $names.Add($row.File)
            if ($reports[$rid].ContainsKey($row.File)) { throw "Duplicate $suite row $($row.File) for $rid." }
            $reports[$rid][$row.File] = $row
        }
        foreach ($failure in $report.Failures) {
            $details.Add("- **$($platforms[$rid]) failure** / $(Escape-Cell $failure.Name): $(Escape-Cell $failure.Message)")
        }
        foreach ($reason in $report.Skips.PSObject.Properties) {
            $details.Add("- **$($platforms[$rid]) skipped ($($reason.Value))**: $(Escape-Cell $reason.Name)")
        }
    }
    ''
    "#### $((Get-Culture).TextInfo.ToTitleCase($suite)) tests"
    ''
    '| Result file | Linux | macOS | Windows |'
    '| --- | --- | --- | --- |'
    if ($names.Count -eq 0) {
        '| No results | Unavailable | Unavailable | Unavailable |'
    }
    foreach ($name in ($names | Sort-Object)) {
        $cells = foreach ($rid in $platforms.Keys) {
            $row = $reports[$rid][$name]
            if ($null -eq $row) { 'Unavailable' }
            else { "$($row.Passed) / $($row.Failed) / $($row.Skipped)" }
        }
        "| $(Escape-Cell $name) | $($cells -join ' | ') |"
    }
    if ($details.Count -gt 0) {
        ''
        '<details><summary>Failures and skip reasons by platform</summary>'
        ''
        $details
        ''
        '</details>'
    }
}
