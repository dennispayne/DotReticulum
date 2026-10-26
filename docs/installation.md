# Installing native DotReticulum utilities

DotReticulum's Applications project publishes a self-contained NativeAOT executable. The
packaging script stages that executable and the executable names that its dispatcher actually
recognizes; no .NET runtime installation is needed to run a published binary.

Supported package RIDs are **Windows x64** (`win-x64`), **Linux x64** (`linux-x64`), and
**macOS ARM64** (`osx-arm64`). A package can be staged for any supported RID, but installation
only accepts a package matching the current host. macOS packaging and execution have not been
verified in this environment and are not a platform-support claim.

Binaries carry the project version `<upstream RNS version>.<build number>` (for example `1.5.7.42`); see [Versioning](../README.md#versioning). Each tool's `--version` reports the upstream RNS version it targets.

Publish for the target RID, then stage and install to a user-selected directory:

```powershell
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true `
  -o artifacts\native-win-x64

pwsh -NoProfile -File tools\dotreticulum-packaging.ps1 -Action Package `
  -Rid win-x64 -PublishDirectory artifacts\native-win-x64 `
  -OutputDirectory artifacts\package-win-x64

pwsh -NoProfile -File tools\dotreticulum-packaging.ps1 -Action Install `
  -PackageDirectory artifacts\package-win-x64 `
  -InstallRoot "$HOME\DotReticulum"
```

Use `linux-x64` or `osx-arm64` instead of `win-x64` for those respective publish targets. The
publish directory must contain the native `DotReticulum.Applications` executable for that RID.
Package staging creates an integrity manifest and refuses a non-empty output directory. It copies
the native binary into `DotReticulum.Applications`, the repository's exact `LICENSE` file, and these
dispatcher aliases:

`git-remote-rns`, `rngit`, `rngcs`, `rnid`, `rnstatus`, `rnpath`, `rnprobe`, `rnsh`,
`rnodeconf`, `rnir`, `rnpkg`, `rncp`, `rnx`, and `rnsd`.

`telemetry` is a subcommand rather than an executable-name alias in the current dispatcher; use
`DotReticulum.Applications telemetry --help`. `rnir` and `rnpkg` retain their explicitly
documented upstream-placeholder behavior. This packaging step does not enable remote execution or
authorize device operations.

Installations are local to the chosen directory and require no administrator rights. The script
does not change `PATH`, system configuration, or Git configuration. Add `<install-root>/bin` to
your own user `PATH` only if you choose; Git discovers `git-remote-rns` through normal executable
search when that directory is on `PATH`. `Discover` lists matching executables in the selected
install root and current `PATH`:

```powershell
pwsh -NoProfile -File tools\dotreticulum-packaging.ps1 -Action Discover `
  -InstallRoot "$HOME\DotReticulum"
```

Reinstalling an identical package is a no-op. Non-identical upgrades are refused rather than
partially replacing files; uninstall the existing version before installing a different package.
Uninstallation removes only unchanged files recorded in the install manifest, then removes the
manifest and an empty `bin` directory; it retains the install root and any unrelated content.
Unowned collisions, modified owned files, invalid manifests, path escapes and package integrity
failures are errors rather than overwrite/delete requests:

```powershell
pwsh -NoProfile -File tools\dotreticulum-packaging.ps1 -Action Uninstall `
  -InstallRoot "$HOME\DotReticulum"
```

These operations stage/copy the native application only; they do not install services, create
identities, configure interfaces, update `PATH`, or perform device/remote-command actions.
