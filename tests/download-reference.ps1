#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Directory)

$ErrorActionPreference = 'Stop'
$json = & gh api "repos/$env:GITHUB_REPOSITORY/actions/artifacts?name=pinned-reference&per_page=30"
if ($LASTEXITCODE -ne 0) { throw 'Could not query Reticulum-fetched reference artifacts.' }
$artifacts = ($json | ConvertFrom-Json).artifacts |
    Where-Object { !$_.expired -and $_.workflow_run.head_branch -eq 'main' } |
    Sort-Object created_at -Descending
if (!$artifacts) {
    Write-Warning 'No Reticulum-fetched reference artifact is available. Reference cases remain explicitly unexecuted; run Fetch upstream over Reticulum to provision it.'
    return
}
$artifact = @($artifacts)[0]
$metadata = & gh run view $artifact.workflow_run.id --repo $env:GITHUB_REPOSITORY --json workflowName | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $metadata.workflowName -ne 'Fetch upstream over Reticulum') {
    throw 'Reference artifact did not originate from the expected fetch workflow.'
}
& gh run download $artifact.workflow_run.id --repo $env:GITHUB_REPOSITORY --name pinned-reference --dir $Directory
if ($LASTEXITCODE -ne 0) { throw 'Reference artifact download failed.' }
$provenance = Get-Content -LiteralPath (Join-Path $Directory 'reference-provenance.json') -Raw | ConvertFrom-Json
$bundle = Join-Path $Directory 'reference.bundle'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
if ($provenance.pin -ne $catalog.reference.revision -or $provenance.networkBundleBytes -le 0 -or
    $provenance.bundleSha256 -ne (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash) {
    throw 'Reference artifact pin, network transfer evidence or bundle digest is invalid.'
}
Write-Host "Using reference objects fetched over Reticulum by Actions run $($artifact.workflow_run.id)."
