#Requires -Version 7.0

<#
.SYNOPSIS
Summarizes merged production line and branch coverage as Markdown.
.DESCRIPTION
Reads OpenCover reports from a results directory, deduplicates shared code and
rejects missing production modules. Returns exit code 2 for invalid reports.
Optionally saves deduplicated hit/total counts as JSON for cross-platform summaries.
.EXAMPLE
.\Get-CoverageSummary.ps1 -Results ..\TestResults\coverage-run
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Results,
    [string] $JsonPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Merge-CoverageReports {
    param([System.IO.FileInfo[]] $Reports)

    $modules = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($report in $Reports) {
        $document = [System.Xml.XmlDocument]::new()
        $document.Load($report.FullName)
        foreach ($module in $document.SelectNodes('/CoverageSession/Modules/Module')) {
            $name = $module.SelectSingleNode('ModuleName').InnerText
            if (!$name.StartsWith('DotReticulum.') -or $name.EndsWith('.Tests')) { continue }
            if (!$modules.ContainsKey($name)) {
                $modules[$name] = @{
                    Lines = [hashtable]::new([StringComparer]::Ordinal)
                    Branches = [hashtable]::new([StringComparer]::Ordinal)
                }
            }
            $coverage = $modules[$name]
            $files = [hashtable]::new([StringComparer]::Ordinal)
            foreach ($file in $module.SelectNodes('Files/File')) {
                $path = [System.IO.Path]::GetFullPath($file.GetAttribute('fullPath'))
                if ($IsWindows) { $path = $path.ToUpperInvariant() }
                $files[$file.GetAttribute('uid')] = $path
            }
            foreach ($method in $module.SelectNodes('Classes/Class/Methods/Method')) {
                $methodName = $method.SelectSingleNode('Name').InnerText
                $fileRef = $method.SelectSingleNode('FileRef')
                $defaultFile = if ($null -ne $fileRef) { $fileRef.GetAttribute('uid') } else { '' }
                foreach ($kind in 'Lines', 'Branches') {
                    $selector = if ($kind -eq 'Lines') { 'SequencePoints/SequencePoint' } else { 'BranchPoints/BranchPoint' }
                    foreach ($point in $method.SelectNodes($selector)) {
                        $fileId = if ($point.HasAttribute('fileid')) { $point.GetAttribute('fileid') } else { $defaultFile }
                        if (!$files.ContainsKey($fileId)) { throw "Unknown source file '$fileId' in $($report.FullName)" }
                        $parts = @($files[$fileId], $point.GetAttribute('sl'))
                        if ($kind -eq 'Branches') {
                            $parts += @($methodName, $point.GetAttribute('offset'), $point.GetAttribute('path'), $point.GetAttribute('offsetend'))
                        }
                        $key = ConvertTo-Json -InputObject $parts -Compress
                        $hit = [long]::Parse($point.GetAttribute('vc')) -gt 0
                        $coverage[$kind][$key] = $hit -or ($coverage[$kind].ContainsKey($key) -and $coverage[$kind][$key])
                    }
                }
            }
        }
    }
    return $modules
}

function Format-Coverage {
    param([long] $Hit, [long] $Total)
    if ($Total -eq 0) { return 'n/a (no instrumented points)' }
    return [string]::Format([cultureinfo]::InvariantCulture, '{0}/{1} ({2:F2}%)', $Hit, $Total, (100.0 * $Hit / $Total))
}

try {
    $reports = @(Get-ChildItem -LiteralPath $Results -Recurse -File -Filter coverage.opencover.xml)
    if ($reports.Count -eq 0) { throw "No coverage.opencover.xml reports found in $Results" }
    $modules = Merge-CoverageReports $reports
    $projects = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\src') -Directory |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.csproj' }
    $missing = @($projects.BaseName | Where-Object { !$modules.ContainsKey($_) })
    if ($missing.Count -gt 0) { throw "Incomplete production coverage; missing modules: $($missing -join ', ')" }
    if ($modules.Count -eq 0) { throw 'No DotReticulum production modules found in coverage reports' }
    $totals = @{ Lines = @(0L, 0L); Branches = @(0L, 0L) }
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($name in ($modules.Keys | Sort-Object)) {
        $row = [ordered]@{ Name = $name }
        foreach ($kind in 'Lines', 'Branches') {
            $points = $modules[$name][$kind]
            $hit = @($points.Values | Where-Object { $_ }).Count
            $totals[$kind][0] += $hit
            $totals[$kind][1] += $points.Count
            $row[$kind] = @{ Hit = $hit; Total = $points.Count }
        }
        $rows.Add($row)
    }
    $rows.Add([ordered]@{
        Name = 'Overall'
        Lines = @{ Hit = $totals.Lines[0]; Total = $totals.Lines[1] }
        Branches = @{ Hit = $totals.Branches[0]; Total = $totals.Branches[1] }
    })
    if ($JsonPath) {
        @{ ReportCount = $reports.Count; Rows = @($rows.ToArray()) } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $JsonPath -Encoding utf8
    }
    '| Project | Lines | Branches |'
    '|---|---:|---:|'
    foreach ($row in $rows) {
        $name = if ($row.Name -eq 'Overall') { '**Overall**' } else { $row.Name }
        "| $name | $(Format-Coverage $row.Lines.Hit $row.Lines.Total) | $(Format-Coverage $row.Branches.Hit $row.Branches.Total) |"
    }
    "`nMerged $($reports.Count) reports; shared production code counted once."
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 2
}
