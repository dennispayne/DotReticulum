#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReportsDirectory,
    [string] $Catalog = (Join-Path $PSScriptRoot 'Compatibility\catalog.json')
)

$ErrorActionPreference = 'Stop'
$catalogue = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json
$platforms = [ordered]@{ 'linux-x64' = 'Linux'; 'osx-arm64' = 'macOS'; 'win-x64' = 'Windows' }
$reports = @{}
$upstream = @{}
$details = [Collections.Generic.List[string]]::new()

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
    $upstreamFiles = @(Get-ChildItem -LiteralPath $files[0].DirectoryName -File -Filter upstream.json)
    foreach ($file in $upstreamFiles) {
        foreach ($row in @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)) {
            if ($row.Revision -ne $catalogue.reference.revision) { throw 'Upstream result revision does not match the compatibility pin.' }
            if ($row.Id -notin $catalogue.contracts.id -or $row.Status -notin 'Pass', 'Fail', 'Skipped', 'Not covered') {
                throw "Invalid upstream result for $($row.Id)."
            }
            if (!$upstream.ContainsKey($row.Id)) { $upstream[$row.Id] = @{} }
            if ($upstream[$row.Id].ContainsKey($rid)) { throw "Duplicate upstream result for $($row.Id) on $rid." }
            $upstream[$row.Id][$rid] = $row
            if ($row.Status -ne 'Pass' -and $row.Status -ne 'Not covered') {
                $details.Add("- ``$($row.Id)`` / Upstream on $($platforms[$rid]): $($row.Status) $(Escape-Cell ([string] $row.Detail))")
            }
        }
    }
}

'### Upstream compatibility - all platforms'
''
"Reference: $($catalogue.reference.name), ``$($catalogue.reference.revision)``."
''
'Each row is one advertised claim with bounded shared acceptance scenarios. Unavailable means no result was received; Not covered means no shared scenario exists yet.'
''
'Upstream runs as the candidate against the same fixed upstream RNS peer used by the three .NET candidates. Its column combines baselines rerun on each OS: Fail takes precedence, and missing baselines prevent an overall Pass.'
''
'| Contract | Family | Behavior | Upstream | .NET Linux | .NET macOS | .NET Windows |'
'| --- | --- | --- | --- | --- | --- | --- |'
foreach ($contract in $catalogue.contracts) {
    $baselines = if ($upstream.ContainsKey($contract.id)) { @($upstream[$contract.id].Values) } else { @() }
    $upstreamStatus = if ($baselines.Status -contains 'Fail') { 'Fail' }
        elseif ($baselines.Status -contains 'Not covered') { 'Not covered' }
        elseif ($baselines.Count -lt $platforms.Count) { 'Not run' }
        elseif ($baselines.Status -contains 'Skipped') { 'Skipped' }
        else { 'Pass' }
    $cells = foreach ($rid in $platforms.Keys) {
        $row = $reports[$rid][$contract.id]
        if ($null -eq $row) { 'Unavailable'; continue }
        if ($row.Detail) {
            $details.Add("- ``$($contract.id)`` / $($platforms[$rid]): $(Escape-Cell ([string] $row.Detail))")
        }
        Escape-Cell $row.Status
    }
    "| ``$($contract.id)`` | $(Escape-Cell $contract.family) | $(Escape-Cell $contract.title) | $upstreamStatus | $($cells -join ' | ') |"
    if ($contract.boundary) {
        $details.Add("- ``$($contract.id)`` boundary: $(Escape-Cell $contract.boundary)")
    }
}
if ($details.Count -gt 0) {
    ''
    '#### Result details'
    ''
    $details
}
