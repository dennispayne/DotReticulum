#Requires -Version 7.0

<#
.SYNOPSIS
Runs tooling and application process tests with Pester.
.DESCRIPTION
Requires Pester 5.7.1+. Uses the built Release DLL unless -Application is supplied.
-ToolingOnly needs no application. Git remote comparisons require Python and Upstream.
.PARAMETER Application
Application executable or managed DLL under test. Defaults to the built Release DLL.
.PARAMETER ToolingOnly
Runs only tooling/reporting tests without requiring an application build.
Cannot be combined with GitRemote, GitHosting or NativePackaging.
.PARAMETER GitRemote
Adds live Git remote comparisons against the pinned Python reference.
Requires Python and Upstream.
.PARAMETER GitHosting
Builds the presentation subprocess fixture and adds Git hosting, signing and media tests.
.PARAMETER NativePackaging
Adds installation and package process tests. Requires Application to be a published
native executable rather than a managed DLL.
.PARAMETER Python
Python interpreter for reference-dependent cases. Defaults to DOTRETICULUM_PYTHON.
.PARAMETER Upstream
Verified pinned reference checkout. Defaults to DOTRETICULUM_UPSTREAM.
.EXAMPLE
.\Invoke-PesterTests.ps1 -ToolingOnly
#>
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [switch] $ToolingOnly,
    [switch] $GitRemote,
    [switch] $GitHosting,
    [switch] $NativePackaging,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM
)

$ErrorActionPreference = 'Stop'
if (($GitRemote -or $GitHosting -or $NativePackaging) -and $ToolingOnly) { throw 'Application suites cannot be combined with -ToolingOnly.' }
if ($GitRemote -and ([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream))) {
    throw '-GitRemote requires -Python and -Upstream, or their DOTRETICULUM_* environment variables.'
}
Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
$containers = @(New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Coverage.Tests.ps1'))
$containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\StaticAnalysis.Tests.ps1')
if (!$ToolingOnly) {
    $resolved = (Resolve-Path -LiteralPath $Application).Path
    if ($NativePackaging) {
        if ([IO.Path]::GetExtension($resolved) -eq '.dll') {
            throw '-NativePackaging requires a published native application, not a managed DLL.'
        }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Parity8Packaging.Tests.ps1') -Data @{
            Application = $resolved
        }
    }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Cli.Tests.ps1') -Data @{ Application = $resolved }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Parity3Configuration.Tests.ps1') -Data @{ Application = $resolved }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Parity4Interfaces.Tests.ps1') -Data @{ Application = $resolved }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Parity5Resources.Tests.ps1') -Data @{ Application = $resolved }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Parity6Interfaces.Tests.ps1') -Data @{ Application = $resolved }
    foreach ($suite in 'Identity', 'NetworkUtilities', 'Shell', 'Device', 'Transfer') {
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot "Pester\Parity7$suite.Tests.ps1") -Data @{
            Application = $resolved
            Python = $Python
            Upstream = $Upstream
        }
    }
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\SharedRuntime.Tests.ps1') -Data @{
        Application = $resolved
        Python = $Python
        Upstream = $Upstream
    }
    if ($GitHosting) {
        $presentationProject = Join-Path $PSScriptRoot 'Fixtures\RngitPresentationBackend\RngitPresentationBackend.csproj'
        & dotnet build $presentationProject -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Presentation subprocess fixture build failed.' }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\RngitHosting.Tests.ps1') -Data @{
            Application = $resolved
            Python = $Python
            Upstream = $Upstream
        }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Rngcs.Tests.ps1') -Data @{
            Application = $resolved
            Python = $Python
            Upstream = $Upstream
        }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\MediaTemplates.Tests.ps1')
    }
    if ($GitRemote) {
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\GitRemote.Tests.ps1') -Data @{
            Application = $resolved
            Python = (Resolve-Path -LiteralPath $Python).Path
            Upstream = (Resolve-Path -LiteralPath $Upstream).Path
        }
    }
}
$configuration = New-PesterConfiguration
$configuration.Run.Container = $containers
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$result = Invoke-Pester -Configuration $configuration
if ($result.FailedCount -gt 0 -or $result.Result -ne 'Passed' -or $result.PassedCount -eq 0) { exit 1 }
