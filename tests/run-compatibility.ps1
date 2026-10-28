#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\TestResults\compatibility'),
    [string] $Label = 'Advertised-core compatibility',
    [switch] $NoJobSummary,
    [ValidateSet('Both', 'DotReticulum', 'Upstream')] [string] $Implementation = 'Both',
    [switch] $FetchReference,
    [string] $ReferenceDirectory,
    [string] $Gateway = 'reticulum.thecryptosquid.com'
)

$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
function Write-UnexecutedClaims([string] $Detail, [string] $RetrievalStatus = 'Needs reference') {
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
    $null = New-Item -ItemType Directory -Path $OutputDirectory
    $rows = @($catalog.contracts | ForEach-Object {
        [pscustomobject]@{
            Id = $_.id
            Status = if ($_.id -eq 'FUNC-GIT-REFERENCE') { $RetrievalStatus }
                elseif ($_.scenarios.Count) { 'Needs reference' } else { 'Not covered' }
            Detail = $Detail
            Revision = $catalog.reference.revision
        }
    })
    $file = if ($Implementation -eq 'Upstream') { 'upstream.json' } else { 'compatibility.json' }
    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory $file) -Encoding utf8
}
if ($FetchReference) {
    if ($Implementation -eq 'Upstream') { throw 'Use an already fetched reference bundle to bootstrap the upstream baseline.' }
    if (!$ReferenceDirectory) { throw '-FetchReference requires a fresh -ReferenceDirectory outside the report artifacts.' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
    $retrievalStatus = 'Fail'
    try {
        & (Join-Path $PSScriptRoot 'fetch-reference.ps1') -Application $Application -Directory $ReferenceDirectory -Gateway $Gateway
        $retrievalStatus = 'Pass'
        $ReferenceDirectory = (Resolve-Path -LiteralPath $ReferenceDirectory).Path
        $bootstrapPython = if ($Python) { $Python } elseif ($IsWindows) { 'python' } else { 'python3' }
        $environmentDirectory = Join-Path $ReferenceDirectory 'python'
        & (Join-Path $PSScriptRoot 'prepare-reference.ps1') -Bundle (Join-Path $ReferenceDirectory 'reference.bundle') `
            -Directory $environmentDirectory -Python $bootstrapPython | Out-Host
        $Python = Join-Path $environmentDirectory $(if ($IsWindows) { 'venv\Scripts\python.exe' } else { 'venv\bin\python' })
        $Upstream = Join-Path $environmentDirectory 'upstream'
    }
    catch {
        Write-UnexecutedClaims $_.Exception.Message $retrievalStatus
        throw
    }
}
if ([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
    Write-UnexecutedClaims 'Pinned Python reference environment was not provided; no scenario was executed.'
    throw 'Compatibility requires the pinned Python reference, including the upstream-to-upstream baseline.'
}
& (Join-Path $PSScriptRoot 'compare-upstream.ps1') -Application $Application -Python $Python -Upstream $Upstream `
    -Revision $catalog.reference.revision -OutputDirectory $OutputDirectory -Label $Label -NoJobSummary:$NoJobSummary `
    -Implementation $Implementation -ReferenceDirectory $ReferenceDirectory -Gateway $Gateway
exit $LASTEXITCODE
