#Requires -Version 7.0

<#
.SYNOPSIS
Builds platform-native installers from a staged DotReticulum package.
.DESCRIPTION
Validates the staged package, then invokes CMake and CPack for the current host.
Windows produces an MSI, macOS a PKG, and Linux both DEB and RPM packages.
Each installer receives a SHA-256 sidecar. CMake 3.30+ and the platform's
packaging tools must already be installed.
.PARAMETER Rid
The runtime identifier of the staged package and current host.
.PARAMETER PackageDirectory
Directory produced by Invoke-DotReticulumPackaging.ps1 -Action Package.
.PARAMETER Version
Four-part DotReticulum version embedded in the packaged application.
.PARAMETER OutputDirectory
Directory for the installers and their SHA-256 sidecars.
.PARAMETER PackageBasename
Optional filename prefix without an extension.
.EXAMPLE
.\New-NativeInstallers.ps1 -Rid win-x64 -PackageDirectory artifacts\package-win-x64 -Version 1.5.7.42 -OutputDirectory artifacts\release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string] $Rid,
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [Parameter(Mandatory)]
    [string] $Version,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [string] $PackageBasename
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$parsedVersion = [version]'0.0'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
    ![version]::TryParse($Version, [ref]$parsedVersion) -or
    $parsedVersion.Major -gt 65535 -or $parsedVersion.Minor -gt 65535 -or
    $parsedVersion.Build -gt 65535 -or $parsedVersion.Revision -gt 65535) {
    throw "Invalid four-part application version '$Version'."
}
if (!$PackageBasename) { $PackageBasename = "DotReticulum-$Version-$Rid" }
if ($PackageBasename -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw 'PackageBasename must be a simple filename without an extension or directory.'
}

$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$generators = switch ($Rid) {
    'win-x64' {
        if (!$IsWindows -or $architecture -ne [Runtime.InteropServices.Architecture]::X64) {
            throw 'win-x64 installers require a Windows x64 host.'
        }
        @(@{ Generator = 'WIX'; Extension = 'msi' })
    }
    'linux-x64' {
        if (!$IsLinux -or $architecture -ne [Runtime.InteropServices.Architecture]::X64) {
            throw 'linux-x64 installers require a Linux x64 host.'
        }
        @(@{ Generator = 'DEB'; Extension = 'deb' }, @{ Generator = 'RPM'; Extension = 'rpm' })
    }
    'osx-arm64' {
        if (!$IsMacOS -or $architecture -ne [Runtime.InteropServices.Architecture]::Arm64) {
            throw 'osx-arm64 installers require a macOS ARM64 host.'
        }
        @(@{ Generator = 'productbuild'; Extension = 'pkg' })
    }
}

$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
& (Join-Path $PSScriptRoot 'Invoke-DotReticulumPackaging.ps1') -Action Validate -Rid $Rid -PackageDirectory $package
if (!$?) { throw 'Staged package validation failed.' }

$cmake = (Get-Command cmake -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$cpack = (Get-Command cpack -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$cmakeVersion = @(& $cmake --version)
if ($LASTEXITCODE -ne 0 -or $cmakeVersion[0] -notmatch '^cmake version (\d+\.\d+\.\d+)' -or
    [version]$Matches[1] -lt [version]'3.30.0') {
    throw 'CMake 3.30 or later is required.'
}

$msiVersion = $null
if ($Rid -eq 'win-x64') {
    $wix = (Get-Command wix -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $toolManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\dotnet-tools.json') -Raw | ConvertFrom-Json
    $expectedWix = [string]$toolManifest.tools.wix.version
    $actualWix = @(& $wix --version)
    if ($LASTEXITCODE -ne 0 -or $actualWix[0] -notmatch "^$([regex]::Escape($expectedWix))(\+|$)") {
        throw "WiX $expectedWix and its matching UI extension are required."
    }
    if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255) {
        throw 'The target RNS major and minor versions must fit the Windows Installer version fields.'
    }
    $repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $shallow = & git -C $repository rev-parse --is-shallow-repository
    if ($LASTEXITCODE -ne 0 -or $shallow -ne 'false') {
        throw 'A complete Git history is required to calculate the Windows Installer version.'
    }
    $commitCount = & git -C $repository rev-list --count HEAD
    if ($LASTEXITCODE -ne 0 -or $commitCount -notmatch '^[1-9]\d*$' -or [long]$commitCount -gt 65535) {
        throw 'The Windows Installer commit count must be between 1 and 65535.'
    }
    $msiVersion = "$($parsedVersion.Major).$($parsedVersion.Minor).$commitCount"
}
elseif ($Rid -eq 'linux-x64') {
    foreach ($tool in 'dpkg-shlibdeps', 'rpmbuild') {
        if (!(Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "Linux packaging requires '$tool'."
        }
    }
}
else {
    foreach ($tool in 'pkgbuild', 'productbuild') {
        if (!(Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "macOS packaging requires '$tool'."
        }
    }
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -eq
    [IO.Path]::GetPathRoot($output).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw 'OutputDirectory cannot be a filesystem root.'
}
if (Test-Path -LiteralPath $output) {
    $item = Get-Item -LiteralPath $output -Force
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'OutputDirectory must be an ordinary directory.'
    }
}
else { $null = New-Item -ItemType Directory -Path $output }
foreach ($generator in $generators) {
    $target = Join-Path $output "$PackageBasename.$($generator.Extension)"
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath "$target.sha256")) {
        throw "Refusing to overwrite an existing installer or checksum: $target"
    }
}

$work = Join-Path $output ('.native-installers-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$created = [Collections.Generic.List[string]]::new()
try {
    $build = Join-Path $work 'build'
    $packages = Join-Path $work 'packages'
    $arguments = @(
        '-S', (Join-Path $PSScriptRoot 'packaging'), '-B', $build,
        "-DDOTRETICULUM_PACKAGE_DIRECTORY=$package",
        "-DDOTRETICULUM_PACKAGE_VERSION=$Version",
        "-DDOTRETICULUM_PACKAGE_BASENAME=$PackageBasename",
        "-DDOTRETICULUM_RID=$Rid"
    )
    if ($msiVersion) { $arguments += "-DDOTRETICULUM_MSI_VERSION=$msiVersion" }
    & $cmake @arguments
    if ($LASTEXITCODE -ne 0) { throw 'CMake installer configuration failed.' }
    foreach ($generator in $generators) {
        & $cpack --config (Join-Path $build 'CPackConfig.cmake') -G $generator.Generator -B $packages
        if ($LASTEXITCODE -ne 0) {
            $logs = @(Get-ChildItem -LiteralPath $packages -Filter 'wix.log' -File -Recurse -ErrorAction SilentlyContinue)
            $details = @($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 40 }) -join "`n"
            throw "CPack $($generator.Generator) generation failed.`n$details"
        }
        $artifact = Join-Path $packages "$PackageBasename.$($generator.Extension)"
        if (!(Test-Path -LiteralPath $artifact -PathType Leaf) -or (Get-Item -LiteralPath $artifact).Length -le 0) {
            throw "CPack did not produce the expected installer: $artifact"
        }
    }
    foreach ($generator in $generators) {
        $name = "$PackageBasename.$($generator.Extension)"
        $target = Join-Path $output $name
        [IO.File]::Move((Join-Path $packages $name), $target, $false)
        $created.Add($target)
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksum = "$target.sha256"
        $stream = [IO.File]::Open($checksum, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $created.Add($checksum)
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes("$hash  $name`n")
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
        Write-Output "Created $target"
    }
}
catch {
    foreach ($path in $created) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $work -PathType Container) { Remove-Item -LiteralPath $work -Recurse -Force }
}
