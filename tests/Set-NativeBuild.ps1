#Requires -Version 7.0

<#
.SYNOPSIS
Validates a downloaded native build artifact and exports its path for later workflow steps.
.DESCRIPTION
Checks the artifact manifest, runtime identifier, commit and every published
file's SHA-256 digest, including native dependencies and macOS app bundles. Writes
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
if (!$manifest.executable -or $manifest.executable -cnotmatch '^[A-Za-z0-9._-]+$') {
    throw 'Artifact executable must be a plain filename.'
}

$binary = Join-Path (Resolve-Path -LiteralPath $Directory).Path $manifest.executable
if (!(Test-Path -LiteralPath $binary)) { throw "Executable $binary is missing from the artifact." }
if ($manifest.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.sha256) {
    throw 'Native artifact digest mismatch.'
}
$root = (Resolve-Path -LiteralPath $Directory).Path
$expected = @{}
if (@($manifest.files).Count -eq 0) { throw 'Native artifact has no file inventory.' }
foreach ($file in $manifest.files) {
    if (!$file.path -or $file.path -match '^[\\/]|\\|:|[\r\n]|(^|/)\.\.?(/|$)' -or
        $file.path -eq 'build-manifest.json' -or $expected.ContainsKey($file.path) -or
        $file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Invalid or duplicate native artifact file inventory.'
    }
    $expected[$file.path] = $file.sha256
}
$actual = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
} | Where-Object {
    $_ -ne 'build-manifest.json' -and
    ($EnvironmentVariable -eq 'DOTRETICULUM_BUFFER_CONSUMER' -or $_ -notlike 'buffer-consumer/*')
})
if (@(Compare-Object @($expected.Keys | Sort-Object) @($actual | Sort-Object) -CaseSensitive).Count -gt 0) {
    throw 'Native artifact file inventory mismatch.'
}
foreach ($path in $expected.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$path]) {
        throw "Native artifact file digest mismatch: $path"
    }
}
if (!$IsWindows) { chmod +x $binary; if ($LASTEXITCODE -ne 0) { throw 'chmod failed.' } }
if ($env:GITHUB_ENV) { "$EnvironmentVariable=$binary" | Add-Content -LiteralPath $env:GITHUB_ENV }
Write-Host "Using native build $($manifest.commit) ($Rid): $binary"
