#Requires -Version 7.3

<#
.SYNOPSIS
Pins Test to one successful Build run and its exact source commit.
.DESCRIPTION
Checks the producer's repository, workflow, event, conclusion and complete
unexpired artifact inventory before exporting GitHub job outputs. Automatic
runs use the triggering Build; trusted Test requests pin a successful main
Build and a validated Integration cadence. There is no artifact fallback.
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
.PARAMETER RequestPath
Downloaded JSON from a trusted Test request producer. Requests accept main
Builds only, never PR or feature-branch feedback.
.PARAMETER RequestProducerOnly
Validate the Test request producer and its artifact inventory before download.
.PARAMETER PassThru
Return the selected Build metadata for the non-executing request workflow.
.EXAMPLE
.\Resolve-TestBuild.ps1 -RunId 123456789 -Repository dennispayne/DotReticulum
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]*$')] [string] $RunId,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $EventPath = $env:GITHUB_EVENT_PATH,
    [string] $NativeManifest,
    [string] $RequestPath,
    [switch] $RequestProducerOnly,
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Expected an explicit owner/repository.' }
function Read-GitHub([string] $Endpoint) {
    $json = & gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "GitHub API request failed: $Endpoint" }
    return ($json | ConvertFrom-Json)
}
$eventData = if ($EventPath) { Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json } else { $null }
$requestTrigger = $false
if ($RequestPath -or $RequestProducerOnly) {
    if ([string]$eventData.workflow_run.id -cnotmatch '^[1-9][0-9]*$') { throw 'A triggering Test request run is required.' }
    $producer = Read-GitHub "repos/$Repository/actions/runs/$($eventData.workflow_run.id)"
    if ($producer.repository.full_name -cne $Repository -or $producer.head_repository.full_name -cne $Repository -or
        $producer.path -cne '.github/workflows/test-request.yml' -or $producer.head_branch -cne 'main' -or
        $producer.event -cnotin 'schedule', 'workflow_dispatch' -or $producer.status -cne 'completed' -or
        $producer.conclusion -cne 'success' -or $producer.head_sha -cnotmatch '^[0-9a-f]{40}$' -or
        [string]$producer.id -cne [string]$eventData.workflow_run.id -or
        [string]$producer.run_attempt -cnotmatch '^[1-9][0-9]*$') {
        throw 'Producer must be a successful main Test request running in this repository.'
    }
    $requestTrigger = $true
    if ($RequestProducerOnly) {
        $inventory = Read-GitHub "repos/$Repository/actions/runs/$($producer.id)/artifacts?per_page=100"
        $name = "test-request-$($producer.run_attempt)"
        $matches = @($inventory.artifacts | Where-Object { $_.name -ceq $name })
        if ($matches.Count -ne 1 -or $matches[0].expired) { throw 'Test request requires exactly one unexpired request artifact.' }
        if ($env:GITHUB_OUTPUT) { "request-artifact=$name" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
        return
    }
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($request -isnot [System.Collections.IDictionary] -or $request.Count -ne 4 -or
        @($request.Keys | Where-Object { $_ -cnotin 'build_run_id', 'cadence', 'full_integration', 'iterations' }).Count -ne 0 -or
        $request.build_run_id -isnot [string] -or $request.build_run_id -cnotmatch '^[1-9][0-9]*$' -or
        $request.cadence -cnotin 'weekly-live', 'weekly-mesh', 'manual' -or
        $request.full_integration -isnot [bool] -or
        ($request.iterations -isnot [long] -and $request.iterations -isnot [int]) -or
        $request.iterations -lt 1 -or $request.iterations -gt 10) {
        throw 'Malformed Test request fields or cadence.'
    }
    if (($producer.event -ceq 'workflow_dispatch' -and $request.cadence -cne 'manual') -or
        ($producer.event -ceq 'schedule' -and $request.cadence -ceq 'manual') -or
        ($request.cadence -ceq 'weekly-live' -and (!$request.full_integration -or $request.iterations -ne 1)) -or
        ($request.cadence -ceq 'weekly-mesh' -and (!$request.full_integration -or $request.iterations -ne 3))) {
        throw 'Test request cadence does not match its producer or repetitions.'
    }
    if ($RunId -and $RunId -cne $request.build_run_id) { throw 'Build selection differs from the Test request.' }
    $RunId = $request.build_run_id
} elseif ($eventData.workflow_run) {
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
if (!$trusted -and ($requestTrigger -or !$eventData.workflow_run)) {
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
if ($requestTrigger) {
    $mesh = $request.cadence -ceq 'weekly-mesh'
    $filter = if ($mesh) {
        'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~LiveMixedTransportTests|FullyQualifiedName~Parity4NativeTransportTests|FullyQualifiedName~PublishedPublicStreamsSustainBidirectionalByteExactDeliveryAcrossConstrainedLostFrames'
    } else { 'Category=Live' }
    $constrained = $mesh -or ($request.cadence -ceq 'manual' -and $request.full_integration)
    $outputs += @("full-integration=$($request.full_integration.ToString().ToLowerInvariant())",
        "iterations=$($request.iterations)", "integration-filter=$filter",
        "constrained-streams=$($constrained.ToString().ToLowerInvariant())")
} else {
    $outputs += @('full-integration=false', 'iterations=1', 'integration-filter=Category=Live', 'constrained-streams=false')
}
Write-Host "Selected Build $RunId attempt $($run.run_attempt), checkout $commit, head $($run.head_sha), event $($run.event)."
if ($env:GITHUB_OUTPUT) { $outputs | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
if ($env:GITHUB_STEP_SUMMARY) {
    "Build source: [$RunId]($($run.html_url)), commit ``$commit``. No rebuild or artifact fallback." |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
if ($PassThru) { [pscustomobject]@{ RunId = $RunId; Commit = $commit; Attested = $trusted } }
