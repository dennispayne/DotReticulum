#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Application,
    [Parameter(Mandatory)] [string] $Python,
    [Parameter(Mandatory)] [string] $Upstream,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Revision,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Label = 'Upstream-head comparison',
    [switch] $NoJobSummary,
    [ValidateSet('Both', 'DotReticulum', 'Upstream')] [string] $Implementation = 'Both',
    [string] $ReferenceDirectory,
    [string] $Gateway = 'reticulum.thecryptosquid.com'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pester\TestProcess.ps1')
. (Join-Path $PSScriptRoot 'Compatibility\Scenarios.ps1')
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$root = (Resolve-Path -LiteralPath $OutputDirectory).Path
$native = if ($Implementation -ne 'Upstream') { (Resolve-Path -LiteralPath $Application).Path }
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
$implementations = if ($Implementation -eq 'Both') { @('Upstream', 'DotReticulum') } else { @($Implementation) }
foreach ($contract in $catalog.contracts) {
    $row = [ordered]@{
        Id = $contract.id
        Behavior = $contract.title
        Upstream = 'Not run'
        DotReticulum = 'Not run'
        Detail = @()
    }
    foreach ($candidate in $implementations) {
        $statuses = @(
            foreach ($scenario in $contract.scenarios) {
                $directory = Join-Path $root "$scenario-$candidate"
                $executable = if ($candidate -eq 'Upstream') { $pythonPath }
                    elseif ([IO.Path]::GetExtension($native) -eq '.dll') { 'dotnet' }
                    else { $native }
                $prefix = if ($candidate -eq 'Upstream') {
                    @($adapter, '--revision', $Revision, '--tool')
                } else { @(if ($executable -eq 'dotnet') { $native }) }
                $separator = @(if ($candidate -eq 'Upstream') { '--' })
                $status = 'Pass'
                $detail = ''
                try {
                    if ($scenario -eq 'reference-fetch') {
                        if (!$ReferenceDirectory) {
                            $status = 'Skipped'
                            $detail = 'Hosted retrieval was not requested; supply -FetchReference at the compatibility entry point.'
                        }
                        else {
                            $evidenceDirectory = $ReferenceDirectory
                            if ($candidate -eq 'Upstream') {
                                $evidenceDirectory = Join-Path $ReferenceDirectory 'upstream-baseline'
                                & (Join-Path $PSScriptRoot 'fetch-reference.ps1') -Python $pythonPath -Upstream $reference `
                                    -Revision $Revision -Directory $evidenceDirectory -Gateway $Gateway
                            }
                            $evidence = Get-Content -LiteralPath (Join-Path $evidenceDirectory 'reference-provenance.json') -Raw | ConvertFrom-Json
                            $canonical = Get-Content -LiteralPath (Join-Path $ReferenceDirectory 'reference-provenance.json') -Raw | ConvertFrom-Json
                            Assert-Claim ($evidence.candidate -eq $candidate -and $evidence.pin -eq $catalog.reference.revision -and
                                $evidence.networkBundleBytes -gt 0 -and $evidence.pinTree -and
                                $evidence.pinTree -eq $canonical.pinTree) 'Retrieval evidence does not match the candidate or pinned reference tree.'
                            if ($candidate -eq 'DotReticulum') {
                                Assert-Claim ($evidence.applicationSha256 -eq (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash) 'Retrieval was not performed by this native candidate.'
                            }
                            Copy-Item -LiteralPath (Join-Path $evidenceDirectory 'reference-provenance.json') `
                                -Destination (Join-Path $root 'retrieval-provenance.json')
                            $detail = "$($evidence.networkBundleBytes) bundle bytes over Reticulum; pin $($evidence.pin), tree $($evidence.pinTree); seeded incremental fetch, publisher unauthenticated."
                        }
                    }
                    else {
                        $null = New-Item -ItemType Directory -Path $directory
                        Invoke-ClaimScenario -Scenario $scenario -Executable $executable -Prefix $prefix -ToolSeparator $separator `
                            -Environment $(if ($candidate -eq 'Upstream') { $environment } else { @{} }) `
                            -Directory $directory -Python $pythonPath -PeerEnvironment $environment -Revision $Revision
                    }
                }
                catch {
                    $status = 'Fail'
                    $detail = $_.Exception.Message
                    $row.Detail += "$candidate / ${scenario}: $detail"
                }
                $cases.Add([pscustomobject]@{
                    Id = $contract.id; Scenario = $scenario; Implementation = $candidate; Status = $status; Detail = $detail
                })
                $status
            }
        )
        $row[$candidate] = if ('Fail' -in $statuses) { 'Fail' }
            elseif ('Skipped' -in $statuses) { 'Skipped' }
            elseif ($statuses.Count -gt 0) { 'Pass' }
            else { 'Not covered' }
    }
    $rows.Add([pscustomobject] $row)
}
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'comparison.json') -Encoding utf8
$cases | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'scenarios.json') -Encoding utf8
foreach ($candidate in $implementations) {
    $file = if ($candidate -eq 'Upstream') { 'upstream.json' } else { 'compatibility.json' }
    @($rows | ForEach-Object {
        $id = $_.Id
        $details = @($cases | Where-Object { $_.Id -eq $id -and $_.Implementation -eq $candidate -and $_.Detail } |
            ForEach-Object { "$($_.Scenario): $($_.Detail)" })
        [pscustomobject]@{ Id = $id; Status = $_.$candidate; Detail = ($details -join ' '); Revision = $Revision }
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
