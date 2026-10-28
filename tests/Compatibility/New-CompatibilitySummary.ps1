#Requires -Version 7.0

<#
.SYNOPSIS
Creates a Markdown compatibility table from candidate reports.
.DESCRIPTION
Combines saved candidate outcomes with catalog metadata and commit-pinned test links.
.EXAMPLE
.\New-CompatibilitySummary.ps1 -ReportsDirectory ..\..\TestResults\compatibility
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReportsDirectory,
    [string] $Catalog = (Join-Path $PSScriptRoot 'catalog.json'),
    [string] $Commit = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot)
. (Join-Path $PSScriptRoot 'Support\Reference.ps1')
$pin = Get-CompatibilityReferenceRevision
$catalogue = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json -AsHashtable
if (!$catalogue.ContainsKey('claims') -or $catalogue.claims -isnot [array]) {
    throw 'Compatibility catalog must contain a claims array.'
}
$claims = @{}
foreach ($claim in $catalogue.claims) {
    if ($claim.id -cnotmatch '^C[0-9]{3,}$' -or $claims.ContainsKey($claim.id)) {
        throw 'Invalid or duplicate claim ID in compatibility catalog.'
    }
    $claims[$claim.id] = $claim
}
if (!$Commit) {
    $Commit = & git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the tested commit for source links.' }
}
if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Source links require an exact tested commit SHA.' }
$repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'dennispayne/DotReticulum' }
if ($repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Invalid source repository for test links.' }
$sourceUrl = "https://github.com/$repository/blob/$Commit"
$platforms = [ordered]@{ 'linux-x64' = 'Linux'; 'osx-arm64' = 'macOS'; 'win-x64' = 'Windows' }
$definitions = @{}

function Escape-Cell([string] $Value) {
    return ($Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') -replace '\|', '&#124;' -replace '[\r\n]+', ' ')
}

function Read-TestReport([string] $Path, [string] $Candidate) {
    $rows = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
    if ($rows -isnot [array]) { throw "Report for $Candidate must be a JSON array of Pester test records." }
    if ($rows.Count -eq 0) { throw "Report for $Candidate contains no Pester test records." }
    $tests = @{}
    foreach ($row in $rows) {
        if ($row -isnot [System.Collections.IDictionary]) { throw "Malformed test record in $Candidate report." }
        foreach ($field in 'Id', 'ClaimId', 'Name', 'Result', 'File', 'Line', 'Revision') {
            if (!$row.Contains($field)) { throw "Missing $field in $Candidate test record." }
        }
        if ($row.Id -isnot [string] -or $row.Id -cnotmatch '^T[0-9]{3,}$') {
            throw "Invalid test ID in $Candidate report."
        }
        if ($tests.ContainsKey($row.Id)) { throw "Duplicate test ID $($row.Id) in $Candidate report." }
        if ($row.ClaimId -isnot [string] -or $row.ClaimId -cnotmatch '^C[0-9]{3,}$' -or !$claims.ContainsKey($row.ClaimId)) {
            throw "Unknown claim ID for $($row.Id) in $Candidate report."
        }
        if ($row.Name -isnot [string] -or [string]::IsNullOrWhiteSpace($row.Name)) {
            throw "Missing test name for $($row.Id) in $Candidate report."
        }
        if ($row.Result -isnot [string] -or $row.Result -cnotin 'Passed', 'Failed', 'Skipped', 'NotRun', 'Inconclusive') {
            throw "Invalid Pester result for $($row.Id) in $Candidate report."
        }
        if ($row.Revision -isnot [string] -or $row.Revision -cne $pin) {
            throw "Revision for $($row.Id) in $Candidate does not match the canonical compatibility pin."
        }
        if ($null -ne $row.File) {
            if ($row.File -isnot [string] -or $row.File -cnotmatch '^tests[\\/](?:[A-Za-z0-9_.-]+[\\/])*[A-Za-z0-9_.-]+\.Tests\.ps1$' -or
                @($row.File -split '[\\/]' | Where-Object { $_ -in '.', '..' }).Count -gt 0) {
                throw "Invalid repo-relative test File for $($row.Id) in $Candidate report."
            }
            $row.File = $row.File.Replace('\', '/')
        }
        if ($null -ne $row.Line -and (($row.Line -isnot [long] -and $row.Line -isnot [int]) -or $row.Line -lt 1 -or $null -eq $row.File)) {
            throw "Invalid test Line for $($row.Id) in $Candidate report."
        }
        if ($definitions.ContainsKey($row.Id)) {
            foreach ($field in 'ClaimId', 'Name', 'File', 'Line') {
                if ($definitions[$row.Id][$field] -cne $row[$field]) {
                    throw "Inconsistent $field for test ID $($row.Id) across candidate reports."
                }
            }
        }
        else { $definitions[$row.Id] = $row }
        $tests[$row.Id] = $row
    }
    return $tests
}

$reports = @{}
foreach ($rid in $platforms.Keys) {
    $files = @(Get-ChildItem -LiteralPath $ReportsDirectory -Directory -Filter "compatibility-$rid-*" |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -File |
            Where-Object Name -In 'compatibility.json', 'native.json' })
    if ($files.Count -gt 1) { throw "Multiple compatibility reports for $rid." }
    $reports[$rid] = if ($files.Count -eq 1) { Read-TestReport $files[0].FullName $rid } else { @{} }
}
$upstream = @{}
$upstreamFiles = @(Get-ChildItem -LiteralPath $ReportsDirectory -Recurse -File -Filter upstream.json)
if ($upstreamFiles.Count -gt 1) { throw 'Multiple upstream baseline reports; exactly one reusable baseline source is allowed.' }
foreach ($file in $upstreamFiles) {
    $baseline = Read-TestReport $file.FullName 'Upstream'
    foreach ($id in $baseline.Keys) {
        if ($upstream.ContainsKey($id)) { throw "Duplicate upstream baseline test ID $id." }
        $upstream[$id] = $baseline[$id]
    }
}

# Validate all inputs before emitting any part of the table.
'| Test ID | Behavior | Upstream | .NET Linux | .NET macOS | .NET Windows |'
'| --- | --- | --- | --- | --- | --- |'
foreach ($id in @($definitions.Keys | Sort-Object -CaseSensitive)) {
    $test = $definitions[$id]
    $testId = "``$id``"
    if ($null -ne $test.File) {
        $anchor = if ($null -ne $test.Line) { "#L$($test.Line)" } else { '' }
        $testId = "[$testId]($sourceUrl/$($test.File)$anchor)"
    }
    $upstreamResult = if ($upstream.ContainsKey($id)) { $upstream[$id].Result } else { 'Not run' }
    $cells = foreach ($rid in $platforms.Keys) {
        if ($reports[$rid].ContainsKey($id)) { $reports[$rid][$id].Result } else { 'Unavailable' }
    }
    "| $testId | $(Escape-Cell $test.Name) | $upstreamResult | $($cells -join ' | ') |"
}
