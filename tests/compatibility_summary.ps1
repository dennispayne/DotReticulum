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
}

'### Upstream compatibility - all platforms'
''
"Reference: $($catalogue.reference.name), ``$($catalogue.reference.revision)``."
''
'Each row is one contract, not a feature-family total. Unavailable means no result was received.'
''
'| Contract | Family | Behavior | Linux | macOS | Windows |'
'| --- | --- | --- | --- | --- | --- |'
foreach ($contract in $catalogue.contracts) {
    $cells = foreach ($rid in $platforms.Keys) {
        $row = $reports[$rid][$contract.id]
        if ($null -eq $row) { 'Unavailable'; continue }
        if ($row.Detail) {
            $details.Add("- ``$($contract.id)`` / $($platforms[$rid]): $(Escape-Cell ([string] $row.Detail))")
        }
        Escape-Cell $row.Status
    }
    "| ``$($contract.id)`` | $(Escape-Cell $contract.family) | $(Escape-Cell $contract.title) | $($cells -join ' | ') |"
}
if ($details.Count -gt 0) {
    ''
    '#### Result details'
    ''
    $details
}
