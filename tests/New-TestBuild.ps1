#Requires -Version 7.3

<#
.SYNOPSIS
Packages managed test outputs and their coverage collector for another job.
.DESCRIPTION
Archives Release outputs at their repository-relative locations. Records the
exact commit, runtime and archive digest; no restore or compilation is performed.
.PARAMETER Directory
Fresh artifact directory receiving managed.tar and managed-manifest.json.
.PARAMETER Rid
Runtime identifier of the producing build.
.PARAMETER Commit
Exact checked-out commit of the producing build.
.EXAMPLE
.\New-TestBuild.ps1 -Directory artifacts\managed -Rid win-x64 -Commit (git rev-parse HEAD)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'osx-arm64', 'win-x64')] [string] $Rid,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Commit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (Test-Path -LiteralPath $Directory) { throw 'Managed artifact directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $Directory
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$solution = [xml](Get-Content -LiteralPath (Join-Path $root 'DotReticulum.slnx') -Raw)
$outputs = @($solution.Solution.Folder.Project.Path | ForEach-Object {
        $project = Get-Item -LiteralPath (Join-Path $root $_)
        $output = Join-Path $project.DirectoryName 'bin\Release\net10.0'
        $inventory = Join-Path $project.DirectoryName "obj\Release\net10.0\$($project.Name).FileListAbsolute.txt"
        if (!(Test-Path -LiteralPath $inventory)) { throw "Missing build output inventory $inventory." }
        foreach ($file in (Get-Content -LiteralPath $inventory)) {
            if ($file.StartsWith("$output$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath $file -PathType Leaf)) {
                [IO.Path]::GetRelativePath($root, $file).Replace('\', '/')
            }
        }
})
$assemblies = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Directory -Filter 'DotReticulum.*.Tests' |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj") } | ForEach-Object {
        $assembly = Join-Path $_.FullName "bin\Release\net10.0\$($_.Name).dll"
        if (!(Test-Path -LiteralPath $assembly)) { throw "Missing built test assembly $assembly." }
        [IO.Path]::GetRelativePath($root, $assembly)
    })
if ($assemblies.Count -ne 5) { throw 'Expected all five production test assemblies.' }
$assets = Get-Content -LiteralPath (Join-Path $root 'tests\DotReticulum.Core.Tests\obj\project.assets.json') -Raw | ConvertFrom-Json
$collector = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'coverlet.collector/*' })
if ($collector.Count -ne 1) { throw 'Expected exactly one restored coverage collector version.' }
$collectorDirectory = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder "$($collector[0])/build/net10.0"
    if (Test-Path -LiteralPath (Join-Path $candidate 'coverlet.collector.dll')) {
        $collectorDirectory = $candidate
        break
    }
}
if (!$collectorDirectory) { throw 'Restored coverage collector is missing.' }
Copy-Item -LiteralPath $collectorDirectory -Destination (Join-Path $directoryPath 'collector') -Recurse
Push-Location $root
try {
    $list = Join-Path $directoryPath 'managed-files.txt'
    $outputs | Sort-Object -Unique | Set-Content -LiteralPath $list -Encoding utf8NoBOM
    & tar -cf (Join-Path $directoryPath 'managed.tar') -T $list
    if ($LASTEXITCODE -ne 0) { throw 'Managed test artifact creation failed.' }
    & tar -rf (Join-Path $directoryPath 'managed.tar') -C $directoryPath collector
    if ($LASTEXITCODE -ne 0) { throw 'Coverage collector artifact creation failed.' }
}
finally { Pop-Location }
[ordered]@{
    commit = $Commit
    rid = $Rid
    sha256 = (Get-FileHash -LiteralPath (Join-Path $directoryPath 'managed.tar') -Algorithm SHA256).Hash.ToLowerInvariant()
    assemblies = $assemblies
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directoryPath 'managed-manifest.json') -Encoding utf8
