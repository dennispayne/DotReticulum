#Requires -Version 7.0

<#
.SYNOPSIS
Creates a verified pinned checkout and isolated Python environment.
.DESCRIPTION
Validates the fetched bundle against its provenance, checks Git objects and
installs reference dependencies into a new virtual environment. Requires Git,
Python and a fresh destination. Exports reference paths in GitHub Actions.
.PARAMETER Bundle
Fetched reference.bundle file containing the canonical pinned commit.
.PARAMETER Directory
Fresh destination for the upstream checkout and isolated Python virtual environment.
.PARAMETER Python
Python command or interpreter path used to create the virtual environment.
Defaults to python.
.PARAMETER Provenance
Retrieval provenance JSON used to validate the pin, tree and bundle digest.
Defaults to reference-provenance.json beside Bundle.
.EXAMPLE
.\Initialize-Reference.ps1 -Bundle ..\TestResults\reference-fetch\reference.bundle -Directory ..\TestResults\reference-python
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Directory,
    [string] $Python = 'python',
    [string] $Provenance = (Join-Path (Split-Path $Bundle -Parent) 'reference-provenance.json')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Compatibility\Support\Reference.ps1')
$pin = Get-CompatibilityReferenceRevision
$bundlePath = (Resolve-Path -LiteralPath $Bundle).Path
$provenanceData = Get-Content -LiteralPath $Provenance -Raw | ConvertFrom-Json
if ($provenanceData.pin -ne $pin -or $provenanceData.networkBundleBytes -le 0 -or
    $provenanceData.bundleSha256 -ne (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash) {
    throw 'Reference provenance pin, network transfer evidence or bundle digest is invalid.'
}
if (Test-Path -LiteralPath $Directory) { throw 'Reference directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $Directory
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$checkout = Join-Path $directoryPath 'upstream'
$venv = Join-Path $directoryPath 'venv'
& git clone --no-checkout $bundlePath $checkout
if ($LASTEXITCODE -ne 0) { throw 'Reference bundle clone failed.' }
& git -C $checkout fsck --full --strict
if ($LASTEXITCODE -ne 0) { throw 'Reference object integrity check failed.' }
& git -C $checkout -c advice.detachedHead=false checkout --detach $pin
if ($LASTEXITCODE -ne 0) { throw 'Reference bundle does not contain the pinned revision.' }
$pinTree = & git -C $checkout rev-parse 'HEAD^{tree}'
if ($LASTEXITCODE -ne 0 -or $provenanceData.pinTree -ne $pinTree) { throw 'Reference pinned tree does not match retrieval provenance.' }
& $Python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'Reference venv creation failed.' }
$interpreter = Join-Path $venv $(if ($IsWindows) { 'Scripts\python.exe' } else { 'bin\python' })
& $interpreter -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'Interop\reference-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Reference dependency installation failed.' }
$savedPath = $env:PYTHONPATH
$savedUpstream = $env:DOTRETICULUM_UPSTREAM
try {
    $env:PYTHONPATH = $checkout
    $env:DOTRETICULUM_UPSTREAM = $checkout
    & $interpreter (Join-Path $PSScriptRoot 'Interop\verify_reference.py')
    if ($LASTEXITCODE -ne 0) { throw 'Pinned reference verification failed.' }
}
finally {
    $env:PYTHONPATH = $savedPath
    $env:DOTRETICULUM_UPSTREAM = $savedUpstream
}
if ($env:GITHUB_ENV) {
    "DOTRETICULUM_PYTHON=$interpreter" | Add-Content -LiteralPath $env:GITHUB_ENV
    "DOTRETICULUM_UPSTREAM=$checkout" | Add-Content -LiteralPath $env:GITHUB_ENV
}
[pscustomobject]@{ Python = $interpreter; Upstream = $checkout; Revision = $pin }
