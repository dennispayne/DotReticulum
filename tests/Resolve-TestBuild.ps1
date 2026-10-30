#Requires -Version 7.3

<#
.SYNOPSIS
Pins Test to one successful Build run and its exact source commit.
.DESCRIPTION
Checks the producer's repository, workflow, event, conclusion and complete
unexpired artifact inventory before exporting GitHub job outputs. Automatic
runs use the triggering Build; scheduled/manual runs select a successful main
Build unless an explicit run is requested. There is no artifact fallback.
.PARAMETER RunId
Explicit Build run ID. Defaults to the workflow_run event's producer, otherwise
the newest successful main Build. Selection is recorded before any download.
.PARAMETER Repository
Expected owner/repository. Defaults to GITHUB_REPOSITORY.
.PARAMETER EventPath
GitHub event payload. Defaults to GITHUB_EVENT_PATH.
.PARAMETER NativeManifest
Optional Linux native manifest downloaded from the selected run. Resolves the
actual PR merge commit, checking its parent against the producer's head SHA.
Main builds must match the producer's head SHA exactly.
.EXAMPLE
.\Resolve-TestBuild.ps1 -RunId 123456789 -Repository dennispayne/DotReticulum
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]*$')] [string] $RunId,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $EventPath = $env:GITHUB_EVENT_PATH,
    [string] $NativeManifest
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Expected an explicit owner/repository.' }
function Read-GitHub([string] $Endpoint) {
    $json = & gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "GitHub API request failed: $Endpoint" }
    return ($json | ConvertFrom-Json)
}
$eventData = if ($EventPath) { Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json } else { $null }
if ($eventData.workflow_run) {
    if ($RunId -and $RunId -ne [string]$eventData.workflow_run.id) { throw 'Build selection differs from the triggering run.' }
    $RunId = [string]$eventData.workflow_run.id
}
if (!$RunId) {
    $runs = Read-GitHub "repos/$Repository/actions/workflows/build.yml/runs?branch=main&status=success&per_page=20"
    $run = $runs.workflow_runs | Where-Object { $_.event -in 'push', 'workflow_dispatch' } | Select-Object -First 1
    if (!$run) { throw 'No successful main Build is available; run Build first.' }
    $RunId = [string]$run.id
}
$run = Read-GitHub "repos/$Repository/actions/runs/$RunId"
if ($run.repository.full_name -cne $Repository -or
    ($run.head_repository.full_name -cne $Repository -and $run.event -ne 'pull_request') -or
    $run.path -cne '.github/workflows/build.yml' -or $run.status -ne 'completed' -or
    $run.conclusion -ne 'success' -or $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or
    $run.event -notin 'push', 'pull_request', 'workflow_dispatch') {
    throw 'Producer must be a successful Build running in this repository.'
}
$trusted = $run.event -in 'push', 'workflow_dispatch' -and $run.head_branch -eq 'main'
if (!$trusted -and !$eventData.workflow_run) {
    throw 'Scheduled/manual Test requires a successful main Build.'
}
$artifacts = Read-GitHub "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100"
foreach ($rid in 'linux-x64', 'osx-arm64', 'win-x64') {
    foreach ($kind in 'native', 'managed') {
        $matches = @($artifacts.artifacts | Where-Object { $_.name -ceq "$kind-$rid" })
        if ($matches.Count -ne 1 -or $matches[0].expired) {
            throw "Build $RunId must contain exactly one unexpired $kind-$rid artifact."
        }
    }
}
$commit = $run.head_sha
if ($NativeManifest) {
    $manifest = Get-Content -LiteralPath $NativeManifest -Raw | ConvertFrom-Json
    if ($manifest.rid -cne 'linux-x64' -or $manifest.commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Invalid producing Linux native manifest.'
    }
    if ($manifest.commit -cne $run.head_sha) {
        if ($trusted) { throw 'Main Build manifest does not match the producing commit.' }
        if ($run.event -ne 'pull_request') { throw 'Feature Build manifest does not match the producing commit.' }
        $merge = Read-GitHub "repos/$Repository/git/commits/$($manifest.commit)"
        if (@($merge.parents).Count -ne 2 -or $merge.parents[1].sha -cne $run.head_sha) {
            throw 'PR Build manifest is not a merge of the producing head commit.'
        }
    }
    $commit = $manifest.commit
}
$outputs = @("run-id=$RunId", "commit=$commit", "attested=$($trusted.ToString().ToLowerInvariant())")
Write-Host "Selected Build $RunId attempt $($run.run_attempt), checkout $commit, head $($run.head_sha), event $($run.event)."
if ($env:GITHUB_OUTPUT) { $outputs | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
if ($env:GITHUB_STEP_SUMMARY) {
    "Build source: [$RunId]($($run.html_url)), commit ``$commit``. No rebuild or artifact fallback." |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
