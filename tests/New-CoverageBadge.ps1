#Requires -Version 7.0

<#
.SYNOPSIS
Creates Shields endpoint JSON from merged production coverage.
.DESCRIPTION
Validates overall line counts and calculates coverage from covered/total points.
Missing or malformed evidence fails instead of producing a success badge.
.PARAMETER Report
Merged coverage JSON produced by Get-CoverageSummary.ps1.
.PARAMETER Output
Destination Shields endpoint JSON file.
.PARAMETER Label
Badge label, including the platform and coverage metric.
.EXAMPLE
.\New-CoverageBadge.ps1 -Report coverage-merged-linux-x64.json -Output linux.json -Label 'Linux line coverage'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Report,
    [Parameter(Mandatory)]
    [string] $Output,
    [Parameter(Mandatory)]
    [string] $Label
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$coverage = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
$overall = @($coverage.Rows | Where-Object Name -CEQ Overall)
if ($coverage.ReportCount -lt 1 -or $overall.Count -ne 1) {
    throw 'Coverage badge requires exactly one Overall row and collected reports.'
}
$hit = $overall[0].Lines.Hit
$total = $overall[0].Lines.Total
if ($hit -isnot [long] -and $hit -isnot [int] -or
    $total -isnot [long] -and $total -isnot [int] -or
    $total -le 0 -or $hit -lt 0 -or $hit -gt $total) {
    throw 'Invalid coverage hit/total counts.'
}
$percentage = 100.0 * $hit / $total
[ordered]@{
    schemaVersion = 1
    label = $Label
    message = $percentage.ToString('F2', [Globalization.CultureInfo]::InvariantCulture) + '%'
    color = if ($percentage -ge 80) { 'brightgreen' } elseif ($percentage -ge 60) { 'yellowgreen' } else { 'orange' }
    cacheSeconds = 300
} | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
