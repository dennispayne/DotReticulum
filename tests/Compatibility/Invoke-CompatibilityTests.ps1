#Requires -Version 7.0

<#
.SYNOPSIS
Runs shared advertised-claim acceptance against candidate executables.
.DESCRIPTION
Requires Pester 5.7.1+ and a verified pinned Python reference. Selects DotReticulum,
Upstream or Both and saves individual outcomes. -FetchReference enables hosted
Git retrieval; other cases use local peers. Results are saved without writing an
Actions job summary; New-CompatibilitySummary.ps1 renders the combined table.
.PARAMETER Application
Path to the candidate executable or managed DLL. Defaults to the built Release DLL.
Hosted reference retrieval requires a published native executable.
.PARAMETER Python
Python interpreter for the verified reference. Defaults to DOTRETICULUM_PYTHON.
With FetchReference, the retrieved reference supplies this path.
.PARAMETER Upstream
Verified pinned reference checkout. Defaults to DOTRETICULUM_UPSTREAM.
With FetchReference, the retrieved reference supplies this path.
.PARAMETER OutputDirectory
Fresh directory for candidate observations, NUnit XML and JSON reports.
Defaults to TestResults\compatibility beneath the repository root.
.PARAMETER Label
Legacy label accepted for existing callers; currently has no effect on saved reports.
.PARAMETER NoJobSummary
Legacy switch accepted for existing callers; this runner never writes a job summary.
.PARAMETER Implementation
Candidate selection: Both, DotReticulum or Upstream. Defaults to Both.
.PARAMETER FetchReference
Enables hosted Git retrieval with the native candidate and provisions its reference.
Requires ReferenceDirectory and cannot be used with Implementation Upstream alone.
.PARAMETER ReferenceDirectory
Reference artifact directory. Must be fresh when FetchReference is enabled;
the upstream Git baseline uses the existing reference-provenance.json in this directory.
.PARAMETER Gateway
Reticulum TCP gateway hostname or IPv4 address for hosted reference retrieval.
Defaults to reticulum.thecryptosquid.com.
.EXAMPLE
.\Invoke-CompatibilityTests.ps1 -Python C:\Reference\venv\Scripts\python.exe -Upstream C:\Reference\upstream
#>
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\..\TestResults\compatibility'),
    [string] $Label = 'Advertised-core compatibility',
    [switch] $NoJobSummary,
    [ValidateSet('Both', 'DotReticulum', 'Upstream')] [string] $Implementation = 'Both',
    [switch] $FetchReference,
    [string] $ReferenceDirectory,
    [string] $Gateway = 'reticulum.thecryptosquid.com'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Support\Discovery.ps1')
. (Join-Path $PSScriptRoot 'Support\Export.ps1')
Initialize-CompatibilityPester
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
if ($FetchReference -and ($Implementation -eq 'Upstream' -or !$ReferenceDirectory)) {
    throw '-FetchReference requires a fresh -ReferenceDirectory and a native candidate.'
}
$revision = Get-CompatibilityReferenceRevision
$null = New-Item -ItemType Directory -Path $OutputDirectory
$root = (Resolve-Path -LiteralPath $OutputDirectory).Path
$native = if ($Implementation -ne 'Upstream') { (Resolve-Path -LiteralPath $Application).Path } else { $null }
$referenceRoot = if ($ReferenceDirectory) { [IO.Path]::GetFullPath($ReferenceDirectory) } else { $null }
$implementations = if ($Implementation -eq 'Both') { @('DotReticulum', 'Upstream') } else { @($Implementation) }
$failed = $false
foreach ($candidate in $implementations) {
    $directory = Join-Path $root $candidate
    $null = New-Item -ItemType Directory -Path $directory
    $context = @{
        Application = $native; Python = $Python; Upstream = $Upstream; Revision = $revision
        OutputDirectory = $directory; Implementation = $candidate; ReferenceDirectory = $referenceRoot
        FetchReference = ($FetchReference -and $candidate -eq 'DotReticulum'); Gateway = $Gateway
    }
    $null = Get-CompatibilityDiscovery $context
    $configuration = New-CompatibilityConfiguration $context
    $configuration.Output.Verbosity = 'Detailed'
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputFormat = 'NUnitXml'
    $configuration.TestResult.OutputPath = Join-Path $root "$candidate.nunit.xml"
    $result = Invoke-Pester -Configuration $configuration
    $file = if ($candidate -eq 'Upstream') { 'upstream.json' } else { 'compatibility.json' }
    Export-CompatibilityResults $result $revision (Join-Path $root $file)
    if ($result.FailedCount -gt 0 -or $result.FailedContainersCount -gt 0 -or $result.FailedBlocksCount -gt 0 -or $result.PassedCount -eq 0 -or
        $result.Result -notin 'Passed') { $failed = $true }
    if ($candidate -eq 'DotReticulum') {
        $Python = $context.Python
        $Upstream = $context.Upstream
    }
}
if ($failed) { exit 1 }
