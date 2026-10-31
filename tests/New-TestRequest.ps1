#Requires -Version 7.3

<#
.SYNOPSIS
Pins a scheduled or manual Integration request without executing Build artifacts.
.DESCRIPTION
Uses the normal resolver's successful-main Build guard and writes a bounded JSON
request. Only trusted main workflow controls are used; no producing source is
checked out and no Build artifact is downloaded or executed.
.PARAMETER RunId
Successful main Build ID, or blank to select the newest successful main Build.
.PARAMETER OutputPath
Owned request JSON output file.
.PARAMETER Repository
Expected owner/repository, defaulting to GITHUB_REPOSITORY.
.PARAMETER EventPath
Request workflow event payload, defaulting to GITHUB_EVENT_PATH.
.EXAMPLE
.\tests\New-TestRequest.ps1 -OutputPath TestResults\request.json
#>
[CmdletBinding()]
param(
    [string] $RunId,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $EventPath = $env:GITHUB_EVENT_PATH
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cnotin 'schedule', 'workflow_dispatch') {
    throw 'Test requests must originate from scheduled/manual main workflow controls.'
}
$eventData = Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json
$cadence = 'manual'
$full = $false
$iterations = 1
if ($env:GITHUB_EVENT_NAME -ceq 'schedule') {
    switch -CaseSensitive ($eventData.schedule) {
        '0 6 * * 2' { $cadence = 'weekly-live' }
        '0 6 * * 6' { $cadence = 'weekly-mesh'; $iterations = 3 }
        default { throw 'Unknown Test request schedule.' }
    }
    $full = $true
} else {
    if ($eventData.inputs.full_integration -cnotin 'true', 'false' -or
        [string]$eventData.inputs.iterations -cnotmatch '^(?:[1-9]|10)$') {
        throw 'Manual Integration requires a boolean and 1-10 integer repetitions.'
    }
    $full = $eventData.inputs.full_integration -ceq 'true'
    $iterations = [int]$eventData.inputs.iterations
}
$build = & (Join-Path $PSScriptRoot 'Resolve-TestBuild.ps1') -RunId $RunId -Repository $Repository -EventPath '' -PassThru
@{ build_run_id = $build.RunId; cadence = $cadence; full_integration = $full; iterations = $iterations } |
    ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
