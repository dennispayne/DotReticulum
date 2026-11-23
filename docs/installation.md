# Installing native DotReticulum utilities

This guide shows how to build the DotReticulum utilities as a native executable, stage them as a package, install them into a directory of your choice and check that they run.

DotReticulum is experimental. Installing it does not make it an official Reticulum implementation, and a version number names the upstream release it targets, not completed parity.

## What gets installed

The Applications project publishes one self-contained NativeAOT executable, `DotReticulum.Applications`. You do not need a .NET runtime to run it. The packaging script copies that executable together with a copy for each utility name its dispatcher recognizes:

`git-remote-rns`, `rngit`, `rngcs`, `rnid`, `rnstatus`, `rnpath`, `rnprobe`, `rnsh`, `rnodeconf`, `rnir`, `rnpkg`, `rncp`, `rnx` and `rnsd`.

The package also contains the repository's exact `LICENSE` file and an integrity manifest.

`rnir` and `rnpkg` keep their documented upstream-placeholder behavior. Telemetry is no longer part of this repository; see [DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem).

## Supported platforms

You can build packages for these runtime identifiers (RIDs):

| Platform | RID |
|---|---|
| Windows x64 | `win-x64` |
| Linux x64 | `linux-x64` |
| macOS ARM64 | `osx-arm64` |

You can stage a package for any of these RIDs, but installation only accepts a package that matches the current host. On macOS, Actions has published and smoke-tested the native binary and staged a package; package installation remains unverified.

## Prerequisites

- The .NET 10 SDK pinned in `global.json`.
- PowerShell 7, to run the packaging script.
- The platform's NativeAOT toolchain: MSVC C++ build tools on Windows, `clang` and zlib development files on Linux, or Xcode tools on macOS.

Installation needs no administrator rights.

## Step 1: Publish the native executable

From the repository root, publish for your platform's RID. This example uses `win-x64`; substitute `linux-x64` or `osx-arm64` as needed:

```powershell
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true `
  -o artifacts\native-win-x64
```

The publish directory must contain the native `DotReticulum.Applications` executable for that RID.

## Step 2: Stage a package

```powershell
pwsh -NoProfile -File tools\Invoke-DotReticulumPackaging.ps1 -Action Package `
  -Rid win-x64 -PublishDirectory artifacts\native-win-x64 `
  -OutputDirectory artifacts\package-win-x64
```

Staging writes the executable, the utility copies, `LICENSE` and the integrity manifest. It refuses an output directory that is not empty.

## Step 3: Install

Choose an install root. The utilities are placed in its `bin` directory:

```powershell
pwsh -NoProfile -File tools\Invoke-DotReticulumPackaging.ps1 -Action Install `
  -PackageDirectory artifacts\package-win-x64 `
  -InstallRoot "$HOME\DotReticulum"
```

The install is local to that directory. The script does not change `PATH`, system configuration or Git configuration.

## Step 4: Add the utilities to your PATH

If you want to run the utilities by name, add `<install-root>/bin` to your own user `PATH`. This is optional, and you do it yourself. For example, in the current PowerShell session on Windows:

```powershell
$env:PATH = "$HOME\DotReticulum\bin;$env:PATH"
```

Or in a POSIX shell on Linux or macOS:

```sh
export PATH="$HOME/DotReticulum/bin:$PATH"
```

To make the change permanent, add it to your shell profile or your user environment settings.

Git discovers `git-remote-rns` through the normal executable search when that directory is on your `PATH`.

## Step 5: Check the installation

Ask a utility for its version:

```sh
rnsd --version
rnid --version
```

Binaries carry the project version `<upstream RNS version>.<build number>`, for example `1.5.7.42`; see [Versioning](../README.md#versioning). Each utility's `--version` output reports the upstream RNS version it targets.

You can also list the matching executables found in the install root and on your current `PATH`:

```powershell
pwsh -NoProfile -File tools\Invoke-DotReticulumPackaging.ps1 -Action Discover `
  -InstallRoot "$HOME\DotReticulum"
```

## Upgrading and uninstalling

Reinstalling an identical package does nothing. A different package is refused rather than partially replacing files, so uninstall the existing version before you install another one:

```powershell
pwsh -NoProfile -File tools\Invoke-DotReticulumPackaging.ps1 -Action Uninstall `
  -InstallRoot "$HOME\DotReticulum"
```

Uninstalling removes only unchanged files recorded in the install manifest, then the manifest itself and the `bin` directory if it is empty. The install root and any unrelated content are kept.

The script treats these as errors rather than overwriting or deleting anything:

- Collisions with files the package does not own.
- Owned files that have been modified.
- Invalid manifests.
- Paths that escape the install root.
- Package integrity failures.

## What installation does not do

These operations only stage and copy the native application. They do not:

- Install services.
- Create identities.
- Configure interfaces.
- Update `PATH`.
- Enable remote execution, authorize device operations or perform any device or remote-command action.

To configure and run a node after installing, start with `rnsd --help`.
