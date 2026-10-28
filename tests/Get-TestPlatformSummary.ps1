#Requires -Version 7.0

<#
.SYNOPSIS
Combines saved test summaries into cross-platform Markdown tables.
.DESCRIPTION
Reads summary JSON artifacts beneath ReportsDirectory and reports platform
counts, failures and skips. Missing platforms remain explicitly unavailable.
.PARAMETER ReportsDirectory
Directory containing downloaded test-results-<rid>-* artifact directories with
suite summary JSON and optional independent Unit/Integration coverage JSON.
.EXAMPLE
.\Get-TestPlatformSummary.ps1 -ReportsDirectory ..\TestResults\platforms
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $ReportsDirectory)

$ErrorActionPreference = 'Stop'
$platforms = [ordered]@{ 'linux-x64' = 'Linux'; 'osx-arm64' = 'macOS'; 'win-x64' = 'Windows' }

function ConvertTo-MarkdownCell([string] $Value) {
    return ($Value -replace '\|', '&#124;' -replace '[\r\n]+', ' ')
}

function Format-CoverageCell($Row) {
    if ($null -eq $Row) { return 'Unavailable' }
    $values = foreach ($kind in 'Lines', 'Branches') {
        $points = $Row.$kind
        if ($null -eq $points -or $points.Hit -isnot [ValueType] -or $points.Total -isnot [ValueType] -or
            $points.Hit -lt 0 -or $points.Total -lt $points.Hit) {
            throw 'Invalid coverage hit/total counts.'
        }
        if ($points.Total -eq 0) { 'n/a' }
        else {
            [string]::Format([cultureinfo]::InvariantCulture, '{0}/{1} ({2:F2}%)',
                $points.Hit, $points.Total, (100.0 * $points.Hit / $points.Total))
        }
    }
    return "$($values[0]) / $($values[1])"
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
            $details.Add("- **$($platforms[$rid]) failure** / $(ConvertTo-MarkdownCell $failure.Name): $(ConvertTo-MarkdownCell $failure.Message)")
        }
        foreach ($reason in $report.Skips.PSObject.Properties) {
            $details.Add("- **$($platforms[$rid]) skipped ($($reason.Value))**: $(ConvertTo-MarkdownCell $reason.Name)")
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
        "| $(ConvertTo-MarkdownCell $name) | $($cells -join ' | ') |"
    }
    if ($details.Count -gt 0) {
        ''
        '<details><summary>Failures and skip reasons by platform</summary>'
        ''
        $details
        ''
        '</details>'
    }
    if ($suite -eq 'native') { continue }
    $coverage = @{}
    $projects = [Collections.Generic.HashSet[string]]::new()
    foreach ($rid in $platforms.Keys) {
        $files = @(Get-ChildItem -LiteralPath $ReportsDirectory -Directory -Filter "test-results-$rid-*" |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter "coverage-$suite.json" })
        if ($files.Count -gt 1) { throw "Multiple $suite coverage reports for $rid." }
        $coverage[$rid] = @{}
        if ($files.Count -eq 0) { continue }
        $report = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
        if (!$report.ReportCount -or @($report.Rows).Count -eq 0 -or 'Overall' -notin $report.Rows.Name) {
            throw "Incomplete $suite coverage report for $rid."
        }
        foreach ($row in $report.Rows) {
            if (!$row.Name -or $coverage[$rid].ContainsKey($row.Name)) { throw "Invalid or duplicate $suite coverage row for $rid." }
            $null = Format-CoverageCell $row
            $null = $projects.Add($row.Name)
            $coverage[$rid][$row.Name] = $row
        }
    }
    ''
    '##### Production coverage'
    ''
    'Cells show **lines / branches** (covered/total and percentage); each suite is measured independently, not averaged or added together.'
    ''
    '| Project | Linux | macOS | Windows |'
    '| --- | --- | --- | --- |'
    if ($projects.Count -eq 0) { '| No coverage | Unavailable | Unavailable | Unavailable |' }
    foreach ($project in ($projects | Sort-Object { $_ -eq 'Overall' }, { $_ })) {
        $cells = foreach ($rid in $platforms.Keys) { Format-CoverageCell $coverage[$rid][$project] }
        "| $(ConvertTo-MarkdownCell $project) | $($cells -join ' | ') |"
    }
}
