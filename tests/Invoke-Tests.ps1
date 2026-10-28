#Requires -Version 7.0

<#
.SYNOPSIS
Runs Release .NET tests with optional coverage, live reference and Pester suites.
.DESCRIPTION
Requires the .NET 10 SDK. -Live requires Python and Upstream. -Coverage requires
an unfiltered suite and a fresh results directory. -Pester adds CLI/process tests.
Restores process-level test environment variables on exit.
.PARAMETER Live
Enables opt-in live .NET tests and, with Pester, Git remote reference comparisons.
Requires Python and Upstream.
.PARAMETER Python
Python interpreter for live reference tests. Defaults to DOTRETICULUM_PYTHON.
.PARAMETER Upstream
Verified pinned reference checkout for live tests. Defaults to DOTRETICULUM_UPSTREAM.
.PARAMETER NativeExe
Published native executable used by native feature tests and optional Pester suites.
Defaults to DOTRETICULUM_NATIVE_EXE.
.PARAMETER Coverage
Collects production OpenCover coverage and renders its merged summary.
Cannot be combined with Filter.
.PARAMETER Pester
Runs tooling and application process tests, including Git hosting, after .NET tests.
With Live, also enables Git remote reference comparisons.
.PARAMETER ResultsDirectory
Fresh coverage output directory. Requires Coverage. When omitted, a unique
coverage directory is created beneath the repository's TestResults directory.
.PARAMETER Filter
VSTest filter expression passed to dotnet test. Cannot be combined with Coverage.
.EXAMPLE
.\Invoke-Tests.ps1 -Coverage -Pester
#>
[CmdletBinding()]
param(
    [switch] $Live,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $NativeExe = $env:DOTRETICULUM_NATIVE_EXE,
    [switch] $Coverage,
    [switch] $Pester,
    [string] $ResultsDirectory,
    [string] $Filter
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$saved = @{}
foreach ($name in 'DOTRETICULUM_LIVE', 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_NATIVE_EXE', 'DOTRETICULUM_RNSD_EXE') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    if ($Live -and ([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream))) {
        throw '-Live requires -Python and -Upstream, or their DOTRETICULUM_* environment variables.'
    }
    if ($Coverage -and $Filter) { throw 'Collect coverage with the full suite so every production module is represented.' }
    if ($ResultsDirectory -and !$Coverage) { throw '-ResultsDirectory requires -Coverage.' }
    if ($Live) {
        $env:DOTRETICULUM_PYTHON = (Resolve-Path -LiteralPath $Python).Path
        $env:DOTRETICULUM_UPSTREAM = (Resolve-Path -LiteralPath $Upstream).Path
    }
    if ($NativeExe) { $env:DOTRETICULUM_NATIVE_EXE = (Resolve-Path -LiteralPath $NativeExe).Path }
    $env:DOTRETICULUM_LIVE = if ($Live) { '1' } else { '0' }
    Remove-Item Env:DOTRETICULUM_RNSD_EXE -ErrorAction SilentlyContinue
    $arguments = @('test', (Join-Path $root 'DotReticulum.slnx'), '-c', 'Release', '--nologo', '-v', 'minimal')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    if ($Coverage) {
        if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $root "TestResults\coverage-$([guid]::NewGuid().ToString('N'))" }
        if (Test-Path -LiteralPath $ResultsDirectory) { throw "Use a fresh coverage directory: $ResultsDirectory" }
        $arguments += @('--settings', (Join-Path $PSScriptRoot 'coverage.runsettings'),
            '--collect:XPlat Code Coverage', '--results-directory', $ResultsDirectory,
            '-m:1', '--', 'xUnit.ParallelizeTestCollections=false')
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The .NET test suite failed.' }
    if ($Coverage) {
        & (Join-Path $PSHOME 'pwsh') -NoProfile -File (Join-Path $PSScriptRoot 'Get-CoverageSummary.ps1') $ResultsDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Coverage reporting failed.' }
    }
    if ($Pester) {
        $pesterArguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Invoke-PesterTests.ps1'), '-GitHosting')
        if ($NativeExe) { $pesterArguments += @('-Application', $env:DOTRETICULUM_NATIVE_EXE) }
        if ($Live) {
            $pesterArguments += @('-GitRemote', '-Python', $env:DOTRETICULUM_PYTHON, '-Upstream', $env:DOTRETICULUM_UPSTREAM)
        }
        & (Join-Path $PSHOME 'pwsh') @pesterArguments
        if ($LASTEXITCODE -ne 0) { throw 'Pester tooling/CLI tests failed.' }
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}
