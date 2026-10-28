#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReportsDirectory,
    [string] $Catalog = (Join-Path $PSScriptRoot 'Compatibility\catalog.json'),
    [string] $Commit = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
$catalogue = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json
$platforms = [ordered]@{ 'linux-x64' = 'Linux'; 'osx-arm64' = 'macOS'; 'win-x64' = 'Windows' }
$reports = @{}
$upstream = @{}
if (!$Commit) {
    $Commit = & git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the tested commit for source links.' }
}
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'Source links require an exact tested commit SHA.' }
$repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'dennispayne/DotReticulum' }
$sourceUrl = "https://github.com/$repository/blob/$Commit"

function Escape-Cell([string] $Value) {
    return ($Value -replace '\|', '&#124;' -replace '[\r\n]+', ' ')
}

foreach ($rid in $platforms.Keys) {
    $files = @(Get-ChildItem -LiteralPath $ReportsDirectory -Directory -Filter "compatibility-$rid-*" |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter compatibility.json })
    if ($files.Count -gt 1) { throw "Multiple compatibility reports for $rid." }
    $reports[$rid] = @{}
    if ($files.Count -eq 0) { continue }
    foreach ($row in @(Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json)) {
        if ($row.Id -notin $catalogue.contracts.id) { throw "Unknown contract $($row.Id) in $rid report." }
        if ($reports[$rid].ContainsKey($row.Id)) { throw "Duplicate contract $($row.Id) in $rid report." }
        if ($row.Status -notin 'Pass', 'Fail', 'Skipped', 'Needs reference', 'Not covered', 'Incomplete') {
            throw "Unknown status '$($row.Status)' for $($row.Id) in $rid report."
        }
        $reports[$rid][$row.Id] = $row
    }
}
$upstreamFiles = @(Get-ChildItem -LiteralPath $ReportsDirectory -Recurse -File -Filter upstream.json)
foreach ($file in $upstreamFiles) {
    foreach ($row in @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)) {
        if ($row.Revision -ne $catalogue.reference.revision) { throw 'Upstream result revision does not match the compatibility pin.' }
        if ($row.Id -notin $catalogue.contracts.id -or $row.Status -notin 'Pass', 'Fail', 'Skipped', 'Not covered', 'Needs reference') {
            throw "Invalid upstream result for $($row.Id)."
        }
        if ($upstream.ContainsKey($row.Id)) { throw "Duplicate upstream baseline for $($row.Id)." }
        $upstream[$row.Id] = $row
    }
}

'| Test ID | Behavior | Upstream | .NET Linux | .NET macOS | .NET Windows |'
'| --- | --- | --- | --- | --- | --- |'
foreach ($contract in $catalogue.contracts) {
    $upstreamStatus = if ($upstream.ContainsKey($contract.id)) { $upstream[$contract.id].Status } else { 'Not run' }
    $cells = foreach ($rid in $platforms.Keys) {
        $row = $reports[$rid][$contract.id]
        if ($null -eq $row) { 'Unavailable'; continue }
        Escape-Cell $row.Status
    }
    $testId = "``$($contract.id)``"
    if ($contract.code) { $testId = "[$testId]($sourceUrl/$($contract.code))" }
    "| $testId | $(Escape-Cell $contract.title) | $upstreamStatus | $($cells -join ' | ') |"
}
