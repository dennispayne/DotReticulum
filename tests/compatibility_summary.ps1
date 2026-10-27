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
}

$upstreamFiles = @(Get-ChildItem -LiteralPath $ReportsDirectory -Recurse -File -Filter upstream.json)
foreach ($file in $upstreamFiles) {
    foreach ($row in @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)) {
        if ($row.Revision -ne $catalogue.reference.revision) { throw 'Upstream result revision does not match the compatibility pin.' }
        if ($row.Id -notin $catalogue.contracts.id -or $row.Status -notin 'Pass', 'Fail', 'Skipped') {
            throw "Invalid upstream result for $($row.Id)."
        }
        if ($upstream.ContainsKey($row.Id) -and $upstream[$row.Id].Status -ne $row.Status) {
            throw "Conflicting upstream evidence for $($row.Id)."
        }
        $upstream[$row.Id] = $row
    }
}

'### Upstream compatibility - all platforms'
''
"Reference: $($catalogue.reference.name), ``$($catalogue.reference.revision)``."
''
'Each row is one contract, not a feature-family total. Unavailable means no result was received.'
''
'Upstream means the reference itself executed the corresponding acceptance case; a Python peer used by a .NET test does not automatically pass that column.'
''
'| Contract | Family | Behavior | Upstream | .NET Linux | .NET macOS | .NET Windows |'
'| --- | --- | --- | --- | --- | --- | --- |'
foreach ($contract in $catalogue.contracts) {
    $upstreamStatus = if ($upstream.ContainsKey($contract.id)) { $upstream[$contract.id].Status } else { 'Not run' }
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
