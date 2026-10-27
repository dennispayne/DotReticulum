#Requires -Version 7.0
<#
.SYNOPSIS
Summarizes xUnit/VSTest TRX and Pester (NUnit 2.5/3) result files as Markdown.

.DESCRIPTION
Writes counts per result file, then every failed test with a truncated message, and the number of
skipped tests with their distinct skip reasons. Passing tests are not listed individually so the
output stays under the GitHub job-summary size limit. Output goes to stdout; the caller appends it
to $env:GITHUB_STEP_SUMMARY.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $Title = 'Test results',
    [int] $MaxFailures = 50,
    [int] $MaxMessageLength = 600
)

$ErrorActionPreference = 'Stop'

function Limit([string] $text) {
    if (-not $text) { return '' }
    $text = ($text -replace '\s+', ' ').Trim() -replace '\|', '\|'
    if ($text.Length -gt $MaxMessageLength) { $text = $text.Substring(0, $MaxMessageLength) + '...' }
    return $text
}

function Read-Trx([string] $file) {
    $xml = [xml](Get-Content -LiteralPath $file -Raw)
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    foreach ($r in $xml.SelectNodes('//t:UnitTestResult', $ns)) {
        $outcome = switch ($r.outcome) { 'Passed' { 'Passed' } 'Failed' { 'Failed' } default { 'Skipped' } }
        $message = $r.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)?.InnerText
        if ($outcome -eq 'Skipped' -and -not $message) { $message = $r.SelectSingleNode('t:Output/t:StdOut', $ns)?.InnerText }
        [pscustomobject]@{ Name = $r.testName; Outcome = $outcome; Message = $message }
    }
}

function Read-NUnit([string] $file) {
    $xml = [xml](Get-Content -LiteralPath $file -Raw)
    foreach ($c in $xml.SelectNodes('//test-case')) {
        $outcome = switch -Regex ($c.result) { '^(Success|Passed)$' { 'Passed' } '^(Failure|Failed|Error)$' { 'Failed' } default { 'Skipped' } }
        $message = $c.SelectSingleNode('failure/message')?.InnerText
        if (-not $message) { $message = $c.SelectSingleNode('reason/message')?.InnerText }
        [pscustomobject]@{ Name = $c.name; Outcome = $outcome; Message = $message }
    }
}

$files = foreach ($p in $Path) {
    if (Test-Path -LiteralPath $p -PathType Container) { Get-ChildItem -LiteralPath $p -Recurse -File -Include *.trx, *.xml }
    elseif (Test-Path -LiteralPath $p) { Get-Item -LiteralPath $p }
}
$files = @($files | Sort-Object FullName -Unique)

$rows = @()
$failures = @()
$skips = @{}
foreach ($file in $files) {
    $results = @(if ($file.Extension -eq '.trx') { Read-Trx $file.FullName } else { Read-NUnit $file.FullName })
    if ($results.Count -eq 0) { continue }
    $label = $file.BaseName
    if ($file.Extension -eq '.trx') {
        $code = ([xml](Get-Content -LiteralPath $file.FullName -Raw)).GetElementsByTagName('TestMethod') | Select-Object -First 1
        if ($code) { $label = [IO.Path]::GetFileNameWithoutExtension($code.GetAttribute('codeBase')) }
    }
    $passed = @($results | Where-Object Outcome -eq 'Passed').Count
    $failed = @($results | Where-Object Outcome -eq 'Failed')
    $skipped = @($results | Where-Object Outcome -eq 'Skipped')
    $rows += [pscustomobject]@{ File = $label; Passed = $passed; Failed = $failed.Count; Skipped = $skipped.Count; Total = $results.Count }
    $failures += $failed
    foreach ($s in $skipped) { $reason = Limit $s.Message; if (-not $reason) { $reason = '(no reason recorded)' }; $skips[$reason] = 1 + [int]$skips[$reason] }
}

"### $Title"
''
if ($rows.Count -eq 0) { '_No result files were found._'; return }

$failedTotal = ($rows | Measure-Object Failed -Sum).Sum
$status = if ($failedTotal -gt 0) { ':x: Failed' } else { ':white_check_mark: Passed' }
"**$status** - $(($rows | Measure-Object Passed -Sum).Sum) passed, $failedTotal failed, $(($rows | Measure-Object Skipped -Sum).Sum) skipped, $(($rows | Measure-Object Total -Sum).Sum) total"
''
'| Result file | Passed | Failed | Skipped | Total |'
'|---|---:|---:|---:|---:|'
foreach ($r in $rows) { "| $($r.File) | $($r.Passed) | $($r.Failed) | $($r.Skipped) | $($r.Total) |" }

if ($failures.Count -gt 0) {
    ''
    "#### Failed tests ($($failures.Count))"
    ''
    '| Test | Message |'
    '|---|---|'
    foreach ($f in ($failures | Select-Object -First $MaxFailures)) { "| $(Limit $f.Name) | $(Limit $f.Message) |" }
    if ($failures.Count -gt $MaxFailures) { ''; "_$($failures.Count - $MaxFailures) more failures not shown._" }
}

if ($skips.Count -gt 0) {
    ''
    '<details><summary>Skipped tests by reason</summary>'
    ''
    '| Skipped | Reason |'
    '|---:|---|'
    foreach ($k in ($skips.Keys | Sort-Object { $skips[$_] } -Descending | Select-Object -First 20)) { "| $($skips[$k]) | $k |" }
    ''
    '</details>'
}
