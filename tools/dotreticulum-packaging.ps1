#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Package', 'Install', 'Uninstall', 'Discover')]
    [string] $Action,
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string] $Rid,
    [string] $PublishDirectory,
    [string] $OutputDirectory,
    [string] $PackageDirectory,
    [string] $InstallRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:SupportedRids = @('win-x64', 'linux-x64', 'osx-arm64')
$script:Aliases = @(
    'git-remote-rns', 'rngit', 'rngcs', 'rnid', 'rnstatus', 'rnpath', 'rnprobe',
    'rnsh', 'rnodeconf', 'rnir', 'rnpkg', 'rncp', 'rnx', 'rnsd'
)
$script:AppNames = @('DotReticulum.Applications') + $script:Aliases
$script:LicenseName = 'LICENSE'
$script:MaxBinaryBytes = 128MB
$script:PackageManifestName = 'dotreticulum-package.json'
$script:InstallManifestName = '.dotreticulum-install.json'

function Get-RidExtension([string] $TargetRid) {
    if ($TargetRid -notin $script:SupportedRids) { throw "Unsupported RID '$TargetRid'." }
    if ($TargetRid -eq 'win-x64') { return '.exe' }
    return ''
}

function Get-ExpectedNames([string] $TargetRid) {
    $extension = Get-RidExtension $TargetRid
    return @(@($script:AppNames | ForEach-Object { "$_$extension" }) + $script:LicenseName)
}

function Get-RepositoryLicensePath {
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\LICENSE'))
}

function Assert-LicenseMatchesRepository([string] $Path) {
    $repositoryLicense = Get-RepositoryLicensePath
    Assert-OrdinaryFile $repositoryLicense 'Repository LICENSE'
    Assert-OrdinaryFile $Path 'Packaged LICENSE'
    if ((Get-Item -LiteralPath $Path -Force).Length -ne (Get-Item -LiteralPath $repositoryLicense -Force).Length -or
        (Get-Sha256 $Path) -ne (Get-Sha256 $repositoryLicense)) {
        throw 'Packaged LICENSE must be byte-for-byte identical to the repository LICENSE.'
    }
}

function Get-CurrentRid {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    if ([OperatingSystem]::IsWindows() -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) { return 'win-x64' }
    if ([OperatingSystem]::IsLinux() -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) { return 'linux-x64' }
    if ([OperatingSystem]::IsMacOS() -and $architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { return 'osx-arm64' }
    throw "Installation is supported only on Windows x64, Linux x64, and macOS ARM64; current platform is $([Runtime.InteropServices.RuntimeInformation]::OSDescription) $architecture."
}

function Get-FullPath([string] $Path, [string] $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label is required." }
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-NotRoot([string] $Path, [string] $Label) {
    $fullPath = Get-FullPath $Path $Label
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
            $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label cannot be a filesystem root."
    }
    return $fullPath
}

function Assert-OrdinaryDirectory([string] $Path, [string] $Label) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (!$item.PSIsContainer) { throw "$Label is not a directory: $Path" }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label cannot be a symbolic link or reparse point: $Path" }
    }
}

function Assert-OrdinaryFile([string] $Path, [string] $Label) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or $item.PSIsContainer) { throw "$Label does not exist or is not a file: $Path" }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label cannot be a symbolic link or reparse point: $Path" }
    if ($item.Length -le 0 -or $item.Length -gt $script:MaxBinaryBytes) { throw "$Label must be between 1 byte and $($script:MaxBinaryBytes) bytes: $Path" }
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-Manifest([string] $Path, [string] $Label) {
    Assert-OrdinaryFile $Path $Label
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -gt 32768) { throw "$Label exceeds the 32 KiB limit." }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "$Label is not valid JSON: $($_.Exception.Message)" }
}

function Assert-ManifestFiles($Manifest, [string] $Label) {
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.rid -notin $script:SupportedRids) {
        throw "$Label has an unsupported schema or RID."
    }
    $expected = @(Get-ExpectedNames $Manifest.rid)
    $files = @($Manifest.files)
    if ($files.Count -ne $expected.Count) { throw "$Label does not own the complete expected executable set." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        if ($file.name -notin $expected -or !$seen.Add([string]$file.name)) { throw "$Label contains an unexpected or duplicate file name." }
        if ([string]$file.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$file.length -le 0 -or [long]$file.length -gt $script:MaxBinaryBytes) {
            throw "$Label contains invalid file metadata."
        }
    }
}

function Write-NewTextFile([string] $Path, [string] $Text) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Set-ExecutableMode([string] $Path) {
    if (![OperatingSystem]::IsWindows()) {
        [IO.File]::SetUnixFileMode($Path,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor
            [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupExecute -bor
            [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherExecute)
    }
}

function Invoke-Package {
    if ([string]::IsNullOrWhiteSpace($Rid)) { throw '-Rid is required for Package.' }
    $sourceDirectory = Get-FullPath $PublishDirectory 'PublishDirectory'
    $output = Assert-NotRoot $OutputDirectory 'OutputDirectory'
    Assert-OrdinaryDirectory $sourceDirectory 'PublishDirectory'
    Assert-OrdinaryDirectory $output 'OutputDirectory'
    if (!(Test-Path -LiteralPath $sourceDirectory -PathType Container)) { throw "PublishDirectory does not exist: $sourceDirectory" }

    $sourceName = 'DotReticulum.Applications' + (Get-RidExtension $Rid)
    $sourceBinary = Join-Path $sourceDirectory $sourceName
    Assert-OrdinaryFile $sourceBinary 'Published native application'
    $sourceLicense = Get-RepositoryLicensePath
    Assert-OrdinaryFile $sourceLicense 'Repository LICENSE'
    if ((Test-Path -LiteralPath $output -PathType Container) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
        throw "OutputDirectory must be empty; refusing to overwrite existing content: $output"
    }

    $created = [Collections.Generic.List[string]]::new()
    try {
        $null = New-Item -ItemType Directory -Path $output -Force
        foreach ($name in Get-ExpectedNames $Rid) {
            $destination = Join-Path $output $name
            $source = if ($name -eq $script:LicenseName) { $sourceLicense } else { $sourceBinary }
            [IO.File]::Copy($source, $destination, $false)
            $created.Add($destination)
            if ($name -ne $script:LicenseName) { Set-ExecutableMode $destination }
        }
        $files = @(foreach ($name in Get-ExpectedNames $Rid) {
            $path = Join-Path $output $name
            [pscustomobject]@{ name = $name; length = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        })
        $manifest = [ordered]@{ schemaVersion = 1; rid = $Rid; files = $files }
        $manifestPath = Join-Path $output $script:PackageManifestName
        Write-NewTextFile $manifestPath (($manifest | ConvertTo-Json -Depth 4) + "`n")
        $created.Add($manifestPath)
    }
    catch {
        foreach ($path in $created) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
        throw
    }
    Write-Output "Packaged the native application and $($script:Aliases.Count) utility executables for $Rid at $output"
}

function Read-Package([string] $Directory) {
    $package = Get-FullPath $Directory 'PackageDirectory'
    Assert-OrdinaryDirectory $package 'PackageDirectory'
    if (!(Test-Path -LiteralPath $package -PathType Container)) { throw "PackageDirectory does not exist: $package" }
    $manifestPath = Join-Path $package $script:PackageManifestName
    $manifest = Read-Manifest $manifestPath 'Package manifest'
    Assert-ManifestFiles $manifest 'Package manifest'
    foreach ($file in @($manifest.files)) {
        $path = Join-Path $package $file.name
        Assert-OrdinaryFile $path "Packaged executable '$($file.name)'"
        if ((Get-Item -LiteralPath $path).Length -ne [long]$file.length -or (Get-Sha256 $path) -ne [string]$file.sha256) {
            throw "Packaged executable failed integrity validation: $($file.name)"
        }
    }
    Assert-LicenseMatchesRepository (Join-Path $package $script:LicenseName)
    return @{ Directory = $package; Manifest = $manifest }
}

function Read-InstallManifest([string] $Path) {
    if (!(Test-Path -LiteralPath $Path)) { return $null }
    $manifest = Read-Manifest $Path 'Installation manifest'
    Assert-ManifestFiles $manifest 'Installation manifest'
    return $manifest
}

function Invoke-Install {
    $package = Read-Package $PackageDirectory
    $targetRid = [string]$package.Manifest.rid
    $currentRid = Get-CurrentRid
    if ($targetRid -ne $currentRid) { throw "Package RID '$targetRid' does not match this host '$currentRid'." }
    $root = Assert-NotRoot $InstallRoot 'InstallRoot'
    Assert-OrdinaryDirectory $root 'InstallRoot'
    $bin = Join-Path $root 'bin'
    Assert-OrdinaryDirectory $bin 'Install bin directory'
    $manifestPath = Join-Path $root $script:InstallManifestName
    $oldManifest = Read-InstallManifest $manifestPath
    if ($null -ne $oldManifest -and $oldManifest.rid -ne $targetRid) { throw "Existing installation RID '$($oldManifest.rid)' does not match package RID '$targetRid'." }

    $oldFiles = @{}
    if ($null -ne $oldManifest) {
        foreach ($file in @($oldManifest.files)) { $oldFiles[[string]$file.name] = $file }
    }
    foreach ($file in @($package.Manifest.files)) {
        $destination = Join-Path $bin $file.name
        if (Test-Path -LiteralPath $destination) {
            Assert-OrdinaryFile $destination "Existing executable '$($file.name)'"
            if (!$oldFiles.ContainsKey([string]$file.name)) { throw "Refusing to overwrite unowned file: $destination" }
            if ((Get-Sha256 $destination) -ne [string]$oldFiles[[string]$file.name].sha256) {
                throw "Refusing to overwrite locally modified owned file: $destination"
            }
        }
        elseif ($oldFiles.ContainsKey([string]$file.name)) {
            throw "Owned executable is missing; refusing ambiguous update: $destination"
        }
    }

    if ($null -ne $oldManifest) {
        $changed = @($package.Manifest.files | Where-Object {
            [string]$_.sha256 -ne [string]$oldFiles[[string]$_.name].sha256 -or
            [long]$_.length -ne [long]$oldFiles[[string]$_.name].length
        })
        if ($changed.Count -gt 0) {
            throw "Refusing non-identical upgrade because replacement is not transactional. Uninstall the existing version first, then install the new package."
        }
        Write-Output "The installed files already match this package; no changes were made."
        return
    }

    $null = New-Item -ItemType Directory -Path $root -Force
    $null = New-Item -ItemType Directory -Path $bin -Force
    $created = [Collections.Generic.List[string]]::new()
    try {
        foreach ($file in @($package.Manifest.files)) {
            $source = Join-Path $package.Directory $file.name
            $destination = Join-Path $bin $file.name
            [IO.File]::Copy($source, $destination, $false)
            $created.Add($destination)
            if ($file.name -ne $script:LicenseName) { Set-ExecutableMode $destination }
        }
        $installManifest = [ordered]@{ schemaVersion = 1; rid = $targetRid; files = @($package.Manifest.files) }
        $manifestText = ($installManifest | ConvertTo-Json -Depth 4) + "`n"
        Write-NewTextFile $manifestPath $manifestText
    }
    catch {
        foreach ($path in $created) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
        throw
    }
    Write-Output "Installed the native application and $($script:Aliases.Count) utility executables for $targetRid under $root. No PATH or system settings were changed."
}

function Invoke-Uninstall {
    $root = Assert-NotRoot $InstallRoot 'InstallRoot'
    Assert-OrdinaryDirectory $root 'InstallRoot'
    $manifestPath = Join-Path $root $script:InstallManifestName
    $manifest = Read-InstallManifest $manifestPath
    if ($null -eq $manifest) { throw "No DotReticulum installation manifest found under $root." }
    if ([string]$manifest.rid -ne (Get-CurrentRid)) { throw "Installation RID '$($manifest.rid)' does not match this host." }
    $bin = Join-Path $root 'bin'
    Assert-OrdinaryDirectory $bin 'Install bin directory'
    foreach ($file in @($manifest.files)) {
        $path = Join-Path $bin $file.name
        if (Test-Path -LiteralPath $path) {
            Assert-OrdinaryFile $path "Owned executable '$($file.name)'"
            if ((Get-Sha256 $path) -ne [string]$file.sha256) { throw "Refusing to remove locally modified owned file: $path" }
        }
    }
    foreach ($file in @($manifest.files)) {
        $path = Join-Path $bin $file.name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Remove-Item -LiteralPath $manifestPath -Force
    if ((Test-Path -LiteralPath $bin -PathType Container) -and @(Get-ChildItem -LiteralPath $bin -Force).Count -eq 0) {
        Remove-Item -LiteralPath $bin -Force
    }
    Write-Output "Removed the unchanged executable files owned by the installation under $root. The install root was retained."
}

function Invoke-Discover {
    $searchDirectories = [Collections.Generic.List[string]]::new()
    if (![string]::IsNullOrWhiteSpace($InstallRoot)) {
        $root = Get-FullPath $InstallRoot 'InstallRoot'
        $candidate = Join-Path $root 'bin'
        if (Test-Path -LiteralPath $candidate -PathType Container) { $searchDirectories.Add($candidate) }
    }
    foreach ($directory in ($env:PATH -split [IO.Path]::PathSeparator)) {
        if (![string]::IsNullOrWhiteSpace($directory) -and !$searchDirectories.Contains($directory)) { $searchDirectories.Add($directory) }
    }
    $found = 0
    foreach ($name in Get-ExpectedNames (Get-CurrentRid)) {
        foreach ($directory in $searchDirectories) {
            $path = Join-Path $directory $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Write-Output "$name`t$([IO.Path]::GetFullPath($path))"
                $found++
                break
            }
        }
    }
    if ($found -eq 0) { Write-Output 'No DotReticulum utility executables found in the selected install root or PATH.' }
}

try {
    switch ($Action) {
        'Package' { Invoke-Package }
        'Install' { Invoke-Install }
        'Uninstall' { Invoke-Uninstall }
        'Discover' { Invoke-Discover }
    }
}
catch {
    [Console]::Error.WriteLine("dotreticulum-packaging: $($_.Exception.Message)")
    exit 1
}
