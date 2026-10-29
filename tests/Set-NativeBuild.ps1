#Requires -Version 7.0

<#
.SYNOPSIS
Validates a downloaded native build artifact and exports its path for later workflow steps.
.DESCRIPTION
Checks the artifact manifest, runtime identifier, commit and executable. Writes
the application path to GITHUB_ENV when running in GitHub Actions.
.PARAMETER Directory
Downloaded native artifact directory containing build-manifest.json and its executable.
.PARAMETER Rid
Expected runtime identifier, such as win-x64, linux-x64 or osx-arm64.
Must match the artifact manifest.
.PARAMETER Commit
Expected source commit. Defaults to GITHUB_SHA. When empty, commit matching is
not checked; runtime identifier and executable checks still run.
.PARAMETER EnvironmentVariable
Workflow environment variable receiving the verified executable path. Defaults
to DOTRETICULUM_NATIVE_EXE; use DOTRETICULUM_BUFFER_CONSUMER for the SDK fixture.
.EXAMPLE
.\Set-NativeBuild.ps1 -Directory ..\artifacts\native -Rid win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [string] $Rid,
    [string] $Commit = $env:GITHUB_SHA,
    [ValidateSet('DOTRETICULUM_NATIVE_EXE', 'DOTRETICULUM_BUFFER_CONSUMER')]
    [string] $EnvironmentVariable = 'DOTRETICULUM_NATIVE_EXE'
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $Directory 'build-manifest.json'
if (!(Test-Path -LiteralPath $manifestPath)) { throw "Missing $manifestPath; the Build job artifact was not downloaded." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.rid -ne $Rid) { throw "Artifact RID '$($manifest.rid)' does not match '$Rid'." }
if ($Commit -and $manifest.commit -ne $Commit) { throw "Artifact was built from $($manifest.commit), not $Commit." }

$binary = Join-Path (Resolve-Path -LiteralPath $Directory).Path $manifest.executable
if (!(Test-Path -LiteralPath $binary)) { throw "Executable $binary is missing from the artifact." }
if (!$IsWindows) { chmod +x $binary; if ($LASTEXITCODE -ne 0) { throw 'chmod failed.' } }
if ($env:GITHUB_ENV) { "$EnvironmentVariable=$binary" | Add-Content -LiteralPath $env:GITHUB_ENV }
Write-Host "Using native build $($manifest.commit) ($Rid): $binary"
