#Requires -Version 7.0

<#
.SYNOPSIS
Summarizes CodeQL and PSScriptAnalyzer SARIF artifacts as Markdown.
.DESCRIPTION
Reports analysis job status separately from findings. Missing or failed analyses
are explicitly incomplete, never counted as zero findings. Requires one SARIF
run per expected category. Output can be appended to GITHUB_STEP_SUMMARY.
.PARAMETER ReportsDirectory
Directory searched recursively for CodeQL and PSScriptAnalyzer SARIF reports.
Duplicate, unexpected or malformed analysis categories are rejected.
.PARAMETER CodeQLStatus
CodeQL workflow job result displayed separately from findings.
Defaults to unknown; CI supplies the actual dependency job result.
.PARAMETER PowerShellStatus
PSScriptAnalyzer workflow job result displayed separately from findings.
Defaults to unknown; CI supplies the actual dependency job result.
.EXAMPLE
.\tools\Get-StaticAnalysisSummary.ps1 -ReportsDirectory TestResults\analysis -CodeQLStatus success -PowerShellStatus success
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReportsDirectory,
    [string] $CodeQLStatus = 'unknown',
    [string] $PowerShellStatus = 'unknown'
)

$ErrorActionPreference = 'Stop'
$expected = [ordered]@{
    '/language:actions' = @{ Tool = 'CodeQL'; Target = 'Actions'; Status = $CodeQLStatus }
    '/language:csharp' = @{ Tool = 'CodeQL'; Target = 'C#'; Status = $CodeQLStatus }
    '/language:python' = @{ Tool = 'CodeQL'; Target = 'Python'; Status = $CodeQLStatus }
    '/language:powershell' = @{ Tool = 'PSScriptAnalyzer'; Target = 'PowerShell'; Status = $PowerShellStatus }
}
$reports = @{}
$details = [Collections.Generic.List[string]]::new()
function ConvertTo-MarkdownCell([string] $Value) {
    return [Net.WebUtility]::HtmlEncode(($Value -replace '[\r\n]+', ' ')) -replace '\|', '&#124;'
}
foreach ($file in Get-ChildItem -LiteralPath $ReportsDirectory -Recurse -File -Filter '*.sarif') {
    $document = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($document.version -ne '2.1.0' -or !$document.ContainsKey('runs') -or @($document.runs).Count -eq 0) {
        throw "Invalid SARIF report: $($file.Name)"
    }
    foreach ($run in $document.runs) {
        $category = ([string] $run.automationDetails.id).TrimEnd('/')
        if (!$expected.Contains($category)) { throw "Unexpected analysis category '$category'." }
        if ($reports.ContainsKey($category)) { throw "Duplicate analysis category '$category'." }
        if ($run.tool.driver.name -ne $expected[$category].Tool -or $run.results -isnot [array]) {
            throw "Incomplete analysis report for '$category'."
        }
        $counts = @{ error = 0; warning = 0; note = 0; none = 0 }
        $suppressed = 0
        foreach ($result in $run.results) {
            if (@($result.suppressions | Where-Object { $_.status -eq 'accepted' }).Count -gt 0) {
                $suppressed++
                continue
            }
            $rule = @($run.tool.driver.rules | Where-Object { $_.id -eq $result.ruleId }) | Select-Object -First 1
            $level = if ($result.level) { $result.level } elseif ($rule.defaultConfiguration.level) {
                $rule.defaultConfiguration.level
            } else { 'warning' }
            if (!$counts.ContainsKey($level)) { throw "Unknown SARIF level '$level'." }
            $counts[$level]++
            $location = @($result.locations | Select-Object -First 1)[0].physicalLocation
            $source = if ($location) { "$($location.artifactLocation.uri):$($location.region.startLine)" } else { '(no source location)' }
            $message = if ($result.message.text) { $result.message.text } else { $result.message.markdown }
            if (!$message) { throw "Missing finding message in '$category'." }
            if ($message.Length -gt 600) { $message = $message.Substring(0, 600) + '...' }
            $details.Add("| $($expected[$category].Tool) / $($expected[$category].Target) | $level | $(ConvertTo-MarkdownCell $result.ruleId) | $(ConvertTo-MarkdownCell $source) | $(ConvertTo-MarkdownCell $message) |")
        }
        $failed = @($run.invocations | Where-Object { $_.executionSuccessful -eq $false }).Count -gt 0
        $reports[$category] = @{ Counts = $counts; Suppressed = $suppressed; Failed = $failed }
    }
}
'### Static analysis'
''
'Analysis completion and findings are separate: a successful CodeQL job does not mean zero findings.'
''
'| Analyzer | Target | Job status | Errors | Warnings | Notes | Suppressed |'
'| --- | --- | --- | ---: | ---: | ---: | ---: |'
foreach ($category in $expected.Keys) {
    $entry = $expected[$category]
    $report = $reports[$category]
    if ($null -eq $report -or $report.Failed) {
        "| $($entry.Tool) | $($entry.Target) | $($entry.Status); incomplete analysis | Unavailable | Unavailable | Unavailable | Unavailable |"
    } else {
        "| $($entry.Tool) | $($entry.Target) | $($entry.Status) | $($report.Counts.error) | $($report.Counts.warning) | $($report.Counts.note + $report.Counts.none) | $($report.Suppressed) |"
    }
}
if ($details.Count -gt 0) {
    ''
    '<details><summary>Findings (first 50; full results in Code Scanning and SARIF artifacts)</summary>'
    ''
    '| Analyzer / target | Level | Rule | Location | Message |'
    '| --- | --- | --- | --- | --- |'
    $details | Select-Object -First 50
    ''
    '</details>'
}
