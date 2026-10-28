#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\TestResults\compatibility'),
    [string] $Label = 'Advertised-core compatibility',
    [switch] $NoJobSummary
)

$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream)) {
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
    $null = New-Item -ItemType Directory -Path $OutputDirectory
    $rows = @($catalog.contracts | ForEach-Object {
        [pscustomobject]@{
            Id = $_.id
            Status = if ($_.scenarios.Count) { 'Needs reference' } else { 'Not covered' }
            Detail = 'Pinned Python reference environment was not provided; no scenario was executed.'
        }
    })
    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'compatibility.json') -Encoding utf8
    throw 'Compatibility requires the pinned Python reference, including the upstream-to-upstream baseline.'
}
& (Join-Path $PSScriptRoot 'compare-upstream.ps1') -Application $Application -Python $Python -Upstream $Upstream `
    -Revision $catalog.reference.revision -OutputDirectory $OutputDirectory -Label $Label -NoJobSummary:$NoJobSummary
exit $LASTEXITCODE
