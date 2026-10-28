#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Application,
    [Parameter(Mandatory)] [string] $Python,
    [Parameter(Mandatory)] [string] $Upstream,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Revision,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Label = 'Upstream-head comparison',
    [switch] $NoJobSummary
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pester\TestProcess.ps1')
. (Join-Path $PSScriptRoot 'Compatibility\Scenarios.ps1')
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$root = (Resolve-Path -LiteralPath $OutputDirectory).Path
$native = (Resolve-Path -LiteralPath $Application).Path
$pythonPath = (Resolve-Path -LiteralPath $Python).Path
$reference = (Resolve-Path -LiteralPath $Upstream).Path
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
$environment = @{
    DOTRETICULUM_UPSTREAM = $reference
    PYTHONPATH = $reference
    PYTHONIOENCODING = 'utf-8'
}
$adapter = Join-Path $PSScriptRoot 'Interop\reference_cli.py'
$rows = [Collections.Generic.List[object]]::new()
$cases = [Collections.Generic.List[object]]::new()
foreach ($contract in $catalog.contracts) {
    $row = [ordered]@{
        Id = $contract.id
        Behavior = $contract.title
        Upstream = 'Not covered'
        DotReticulum = 'Not covered'
        Detail = @()
    }
    foreach ($implementation in 'Upstream', 'DotReticulum') {
        $statuses = @(
            foreach ($scenario in $contract.scenarios) {
                $directory = Join-Path $root "$scenario-$implementation"
                $null = New-Item -ItemType Directory -Path $directory
                $executable = if ($implementation -eq 'Upstream') { $pythonPath }
                    elseif ([IO.Path]::GetExtension($native) -eq '.dll') { 'dotnet' }
                    else { $native }
                $prefix = if ($implementation -eq 'Upstream') {
                    @($adapter, '--revision', $Revision, '--tool')
                } else { @(if ($executable -eq 'dotnet') { $native }) }
                $separator = @(if ($implementation -eq 'Upstream') { '--' })
                $status = 'Pass'
                $detail = ''
                try {
                    Invoke-ClaimScenario -Scenario $scenario -Executable $executable -Prefix $prefix -ToolSeparator $separator `
                        -Environment $(if ($implementation -eq 'Upstream') { $environment } else { @{} }) `
                        -Directory $directory -Python $pythonPath -PeerEnvironment $environment -Revision $Revision
                }
                catch {
                    $status = 'Fail'
                    $detail = $_.Exception.Message
                    $row.Detail += "$implementation / ${scenario}: $detail"
                }
                $cases.Add([pscustomobject]@{
                    Id = $contract.id; Scenario = $scenario; Implementation = $implementation; Status = $status; Detail = $detail
                })
                $status
            }
        )
        if ($statuses.Count -gt 0) { $row[$implementation] = if ('Fail' -in $statuses) { 'Fail' } else { 'Pass' } }
    }
    $rows.Add([pscustomobject] $row)
}
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'comparison.json') -Encoding utf8
$cases | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'scenarios.json') -Encoding utf8
foreach ($implementation in 'Upstream', 'DotReticulum') {
    $file = if ($implementation -eq 'Upstream') { 'upstream.json' } else { 'compatibility.json' }
    @($rows | ForEach-Object {
        $id = $_.Id
        $details = @($cases | Where-Object { $_.Id -eq $id -and $_.Implementation -eq $implementation -and $_.Detail } |
            ForEach-Object { "$($_.Scenario): $($_.Detail)" })
        [pscustomobject]@{ Id = $id; Status = $_.$implementation; Detail = ($details -join ' '); Revision = $Revision }
    }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root $file) -Encoding utf8
}
$lines = @(
    "### $Label"
    ''
    "Executed upstream revision: ``$Revision``. This does not move the compatibility pin."
    'Shared scenarios and assertions run independently on both candidates. Multi-node scenarios use a fixed upstream RNS peer, including for the upstream-to-upstream baseline.'
    ''
    '| Claim | Tested behavior | Upstream Python | DotReticulum |'
    '| --- | --- | --- | --- |'
    foreach ($row in $rows) {
        "| ``$($row.Id)`` | $($row.Behavior) | $($row.Upstream) | $($row.DotReticulum) |"
    }
    foreach ($row in $rows | Where-Object Detail) {
        foreach ($detail in $row.Detail) { "- ``$($row.Id)``: $(($detail -replace '[\r\n]+', ' ') -replace '\|', '&#124;')" }
    }
)
$lines | Set-Content -LiteralPath (Join-Path $root 'comparison.md') -Encoding utf8
$lines
if ($env:GITHUB_STEP_SUMMARY -and !$NoJobSummary) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($rows | Where-Object { $_.Upstream -eq 'Fail' -or $_.DotReticulum -eq 'Fail' }) { exit 1 }
exit 0
