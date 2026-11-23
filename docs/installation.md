# Installing native DotReticulum utilities

This guide covers native installers, portable release ZIPs, and building and installing the utilities into a directory of your choice.

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

Choose a package matching your operating system and architecture. The release workflow builds native installers for these targets; older releases may contain ZIPs only. The ZIP can also be extracted and used without installing.

## Native installers

The installers are attached to a release as **Assets**, not as GitHub Packages. Verify the download against the release's `SHA256SUMS.txt` before installing. The checksums detect accidental corruption; they are not signatures. The installers are unsigned, and the macOS package is not notarized, so the operating system may warn or block installation.

Native installers modify system locations and require administrator privileges. They use the operating system's package facilities, not the collision and modified-file protections of the local script below. Check for existing utilities with the same names before installing.

### Windows

Run the `win-x64.msi` installer, or install it from an elevated terminal:

```powershell
msiexec /i .\DotReticulum-<version>-win-x64.msi
```

By default the MSI installs to `C:\Program Files\DotReticulum`, adds its `bin` directory to the machine `PATH`, and removes that entry on uninstall. If you choose another location, use a directory writable only by administrators. Open a new terminal to pick up the change. Remove the package through Installed apps, or with `msiexec /x` and the MSI file.

A newer MSI from a descendant release commit upgrades an older one; Windows Installer rejects an older package when a newer one is installed. Windows Installer compares only three version components, so the MSI uses the upstream major and minor numbers and the total reachable Git commit count internally. The four-part DotReticulum version remains in the filename and product name. Building an MSI requires complete Git history; version ordering is intended for releases from `main`, not unrelated development branches.

### Linux

The `linux-x64.deb` and `linux-x64.rpm` packages install the utilities into `/usr/bin` and the license and manifest into `/usr/share/doc/dotreticulum`. Use only one package format on a system. On a compatible Debian-family system:

```sh
sudo apt install ./DotReticulum-<version>-linux-x64.deb
sudo apt remove dotreticulum
```

On a compatible RPM-based system:

```sh
sudo dnf install ./DotReticulum-<version>-linux-x64.rpm
sudo dnf remove dotreticulum
```

Package dependencies are detected from the native executables. The release packages are built on the Ubuntu Actions runner; other distributions, older C libraries and RPM-based hosts may have different dependency requirements and are not established by that build alone.

### macOS

The `osx-arm64.pkg` installs the utilities into `/usr/local/bin` and the license and manifest into `/usr/local/share/doc/dotreticulum`. It can be installed from the graphical installer or from a terminal:

```sh
sudo installer -pkg DotReticulum-<version>-osx-arm64.pkg -target /
```

macOS packages do not provide a general uninstall command. To remove this one, inspect its receipt with `pkgutil --pkgs` and `pkgutil --files <receipt-id>`, and remove its files individually after checking that they are still the files you intend to remove. The receipt begins with `net.dotreticulum.utilities`; `sudo pkgutil --forget <receipt-id>` removes the receipt, not the files. Keep unrelated or modified files.

## Build and install locally

The following steps build and stage a package from source, then use the separate PowerShell script to install it under a directory you choose.

## Prerequisites

- The .NET 10 SDK pinned in `global.json`.
- PowerShell 7, to run the packaging script.
- The platform's NativeAOT toolchain: MSVC C++ build tools on Windows, `clang` and zlib development files on Linux, or Xcode tools on macOS.

This local installation method needs no administrator rights and does not change `PATH`.

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

## Building native installers

The shared CPack configuration under `tools/packaging` builds installers from a staged package on the matching host. It requires CMake and CPack 3.30 or later, plus WiX 4.0.6 and its matching UI extension on Windows, `dpkg-shlibdeps` and `rpmbuild` on Linux, or the Xcode package tools on macOS. Windows also needs complete Git history for the MSI version.

For example, after staging `artifacts\package-win-x64`, use the version embedded in the published application:

```powershell
$version = (dotnet msbuild src\DotReticulum.Applications\DotReticulum.Applications.csproj `
  -getProperty:AssemblyFileVersion -p:Configuration=Release -t:GetBuildVersion | Select-Object -Last 1).Trim()
pwsh -NoProfile -File tools\New-NativeInstallers.ps1 -Rid win-x64 `
  -PackageDirectory artifacts\package-win-x64 -Version $version -OutputDirectory artifacts\installers
```

The builder validates the staged files, refuses to overwrite an existing installer, and writes a SHA-256 sidecar for each output.

## What installation does not do

Neither method installs services, creates identities, configures interfaces, or enables remote execution, device operations or remote commands. The local script also does not change `PATH` or Git configuration.

To configure and run a node after installing, start with `rnsd --help`.
