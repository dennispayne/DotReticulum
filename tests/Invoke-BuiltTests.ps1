#Requires -Version 7.3

<#
.SYNOPSIS
Runs already-built test assemblies without restoring or compiling projects.
.DESCRIPTION
Checks the producing commit/runtime and archive hash before extraction.
Runs the five test assemblies sequentially with optional production coverage.
Every failed assembly remains failed; missing coverage is an error.
.PARAMETER Directory
Downloaded managed artifact directory containing its manifest, archive and collector.
.PARAMETER Rid
Expected runtime identifier.
.PARAMETER Commit
Expected exact producer commit.
.PARAMETER Filter
VSTest filter selecting the required Unit or Integration cases.
.PARAMETER ResultsDirectory
Fresh results directory for TRX and optional OpenCover reports.
.PARAMETER Coverage
Collects production-only coverage using the packaged collector and repository settings.
.PARAMETER Assembly
Optional test assembly names, without the .dll extension. Defaults to all five
assemblies; select Applications for native executable flows.
.EXAMPLE
.\Invoke-BuiltTests.ps1 -Directory artifacts\managed -Rid win-x64 -Commit (git rev-parse HEAD) -Filter 'Category=Unit' -ResultsDirectory TestResults\unit -Coverage
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'osx-arm64', 'win-x64')] [string] $Rid,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [string] $Filter,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [switch] $Coverage,
    [ValidateSet('DotReticulum.Applications.Tests', 'DotReticulum.Core.Tests', 'DotReticulum.Crypto.Tests',
        'DotReticulum.Interfaces.Tests', 'DotReticulum.Transport.Tests')]
    [string[]] $Assembly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'managed-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.commit -cne $Commit -or $manifest.rid -cne $Rid) { throw 'Managed artifact commit/runtime mismatch.' }
$archive = Join-Path $Directory 'managed.tar'
if ($manifest.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.sha256) {
    throw 'Managed artifact digest mismatch.'
}
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Test results directory must be fresh.' }
$expected = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Directory -Filter 'DotReticulum.*.Tests' |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj") } |
    ForEach-Object { "tests/$($_.Name)/bin/Release/net10.0/$($_.Name).dll" } | Sort-Object)
$actual = @($manifest.assemblies | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
if ($expected.Count -ne 5 -or ($expected -join "`n") -cne ($actual -join "`n")) {
    throw 'Managed artifact test assembly inventory mismatch.'
}
$entries = @(& tar -tf $archive)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read managed archive.' }
foreach ($entry in $entries) {
    if ($entry -notmatch '^(((src|tests)/DotReticulum\.[A-Za-z.]+|tests/Fixtures/[A-Za-z]+)/bin/Release/net10\.0/|collector/)' -or
        $entry -match '(^|/)\.\.(/|$)|\\|:') { throw "Unexpected managed archive path: $entry" }
}
& tar -xf $archive -C $root --exclude=collector
if ($LASTEXITCODE -ne 0) { throw 'Managed test artifact extraction failed.' }
& tar -xf $archive -C $Directory collector
if ($LASTEXITCODE -ne 0) { throw 'Coverage collector extraction failed.' }
$collector = (Resolve-Path -LiteralPath (Join-Path $Directory 'collector')).Path
$failed = $false
$savedEnvironment = @{}
$null = New-Item -ItemType Directory -Path $ResultsDirectory
foreach ($variable in 'GITHUB_STEP_SUMMARY', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_PATH', 'GITHUB_STATE') {
    $savedEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable)
    [Environment]::SetEnvironmentVariable($variable, $null)
}
try {
    foreach ($testAssembly in $expected) {
        $name = [IO.Path]::GetFileNameWithoutExtension($testAssembly)
        if ($Assembly -and $name -notin $Assembly) { continue }
        $reportsBefore = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.opencover.xml -Recurse).Count
        $arguments = @('vstest', (Join-Path $root $testAssembly), "/TestCaseFilter:$Filter",
            "/ResultsDirectory:$ResultsDirectory", "/Logger:trx;LogFileName=$name.trx",
            "/TestAdapterPath:$collector", "/Settings:$(Join-Path $root 'tests\coverage.runsettings')")
        if ($Coverage) { $arguments += '/Collect:XPlat Code Coverage' }
        & dotnet @arguments -- xUnit.MaxParallelThreads=1
        if ($LASTEXITCODE -ne 0) { $failed = $true }
        $trx = Join-Path $ResultsDirectory "$name.trx"
        if (!(Test-Path -LiteralPath $trx)) { throw "Missing test execution report for $name." }
        $report = [xml](Get-Content -LiteralPath $trx -Raw)
        if ($Coverage -and [int]$report.TestRun.ResultSummary.Counters.executed -gt 0 -and
            @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.opencover.xml -Recurse).Count -le $reportsBefore) {
            throw "Requested coverage was not collected for $name."
        }
    }
}
finally {
    foreach ($variable in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($variable, $savedEnvironment[$variable])
    }
}
if ($Coverage -and !(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.opencover.xml -Recurse)) {
    throw 'Requested coverage was not collected.'
}
if ($failed) { exit 1 }
