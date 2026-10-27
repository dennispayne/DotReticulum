#Requires -Version 7.0
<#
.SYNOPSIS
Runs the tagged upstream-compatibility Pester cases against the native executable and
reports one row per catalogued contract.
#>
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\TestResults\compatibility'),
    [string] $Label = ''
)

$ErrorActionPreference = 'Stop'
Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop

$referenceAvailable = ![string]::IsNullOrWhiteSpace($Python) -and ![string]::IsNullOrWhiteSpace($Upstream)
$resolved = (Resolve-Path -LiteralPath $Application).Path
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$data = @{ Application = $resolved; Python = $Python; Upstream = $Upstream }
$suites = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Pester') -Filter '*.Tests.ps1' |
    Where-Object { Select-String -LiteralPath $_.FullName -Pattern "Contract:" -Quiet }
$containers = @($suites | ForEach-Object {
    $declared = (Get-Command $_.FullName).Parameters.Keys
    $suiteData = @{}
    foreach ($key in $data.Keys) { if ($key -in $declared) { $suiteData[$key] = $data[$key] } }
    New-PesterContainer -Path $_.FullName -Data $suiteData
})

$configuration = New-PesterConfiguration
$configuration.Run.Container = $containers
$configuration.Run.PassThru = $true
$configuration.Filter.Tag = 'Contract:*'
if (!$referenceAvailable) { $configuration.Filter.ExcludeTag = 'Reference' }
$configuration.Output.Verbosity = 'Minimal'
$result = Invoke-Pester -Configuration $configuration

$cases = foreach ($test in $result.Tests) {
    foreach ($tag in $test.Block.Tag + $test.Tag | Where-Object { $_ -like 'Contract:*' } | Select-Object -Unique) {
        [pscustomobject]@{
            Contract = $tag.Substring('Contract:'.Length)
            Name = $test.ExpandedName
            Result = $test.Result
            Message = if ($test.ErrorRecord) { ($test.ErrorRecord[0].Exception.Message -split "`n")[0] } else { '' }
        }
    }
}
$cases = @($cases)

$rows = foreach ($contract in $catalog.contracts) {
    $mine = @($cases | Where-Object Contract -eq $contract.id)
    $status = if ($mine | Where-Object Result -eq 'Failed') { 'Fail' }
    elseif ($mine | Where-Object Result -eq 'Passed') { 'Pass' }
    elseif ($mine | Where-Object Result -eq 'Skipped') { 'Skipped' }
    elseif ($mine | Where-Object Result -eq 'NotRun') { 'Needs reference' }
    else { 'Not covered' }
    [pscustomobject]@{
        Id = $contract.id
        Family = $contract.family
        Title = $contract.title
        Source = $contract.source
        Status = $status
        Cases = $mine.Count
        Detail = ($mine | Where-Object Message | ForEach-Object { $_.Message } | Select-Object -First 1)
    }
}
$rows = @($rows)
$unknown = @($cases.Contract | Select-Object -Unique | Where-Object { $_ -notin $catalog.contracts.id })
if ($unknown) { throw "Tests claim contracts missing from catalog.json: $($unknown -join ', ')" }

$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'compatibility.json') -Encoding utf8

$icons = @{ 'Pass' = '✅ Pass'; 'Fail' = '❌ Fail'; 'Skipped' = '⏭️ Skipped'; 'Needs reference' = '🐍 Needs reference'; 'Not covered' = '⚪ Not covered' }
$counts = $rows | Group-Object Status | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name.ToLowerInvariant())" }
$title = if ($Label) { "Upstream compatibility ($Label)" } else { 'Upstream compatibility' }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("### $title")
$lines.Add('')
$lines.Add("Reference: $($catalog.reference.name) at ``$($catalog.reference.revision)``. Python reference peer: $(if ($referenceAvailable) { 'provided' } else { 'not provided' }). Contracts: $($rows.Count) ($($counts -join ', ')).")
$lines.Add('')
$lines.Add('| Contract | Family | Behavior | Status | Cases |')
$lines.Add('| --- | --- | --- | --- | --- |')
foreach ($row in $rows) {
    $lines.Add("| ``$($row.Id)`` | $($row.Family) | $($row.Title -replace '\|', '\|') | $($icons[$row.Status]) | $($row.Cases) |")
}
$failed = @($rows | Where-Object Status -eq 'Fail')
if ($failed) {
    $lines.Add('')
    $lines.Add('#### Failures')
    foreach ($row in $failed) { $lines.Add("- ``$($row.Id)``: $(($row.Detail -replace '\s+', ' '))") }
}
$markdown = $lines -join "`n"
Set-Content -LiteralPath (Join-Path $OutputDirectory 'compatibility.md') -Value $markdown -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "`n$markdown`n" -Encoding utf8 }
Write-Host $markdown

if ($failed -or $result.FailedCount -gt 0) { exit 1 }
