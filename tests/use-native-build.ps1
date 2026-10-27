#Requires -Version 7.0
<#
.SYNOPSIS
Validates a downloaded native build artifact and exports DOTRETICULUM_NATIVE_EXE for later workflow steps.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [string] $Rid,
    [string] $Commit = $env:GITHUB_SHA
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
if ($env:GITHUB_ENV) { "DOTRETICULUM_NATIVE_EXE=$binary" | Add-Content -LiteralPath $env:GITHUB_ENV }
Write-Host "Using native build $($manifest.commit) ($Rid): $binary"
