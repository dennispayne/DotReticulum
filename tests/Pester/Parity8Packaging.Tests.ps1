param(
    [string] $Application = [IO.Path]::Combine($PSScriptRoot, '..', '..', 'artifacts', 'native-parity7', 'DotReticulum.Applications.exe'),
    [string] $PackagingScript = [IO.Path]::Combine($PSScriptRoot, '..', '..', 'tools', 'dotreticulum-packaging.ps1')
)

BeforeAll {
    . ([IO.Path]::Combine($PSScriptRoot, 'TestProcess.ps1'))
    $script:application = (Resolve-Path -LiteralPath $Application).Path
    $script:packaging = (Resolve-Path -LiteralPath $PackagingScript).Path
    $script:rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { $null }
    if ($null -eq $script:rid) { return }
    $script:work = [IO.Path]::Combine((Get-Location).Path, '.artifacts', 'p8-packaging-' + [guid]::NewGuid().ToString('N'))
    $script:package = [IO.Path]::Combine($script:work, 'package')
    $script:install = [IO.Path]::Combine($script:work, 'installed')
    $null = New-Item -ItemType Directory -Path $script:work

    function Invoke-Packaging([string] $Action, [hashtable] $Options = @{}) {
        $arguments = @('-NoProfile', '-File', $script:packaging, '-Action', $Action)
        foreach ($key in $Options.Keys) { $arguments += @("-$key", [string]$Options[$key]) }
        $hostExecutable = (Get-Process -Id $PID).Path
        if ([IO.Path]::GetFileNameWithoutExtension($hostExecutable) -eq 'dotnet') {
            $arguments = @((Join-Path $PSHOME 'pwsh.dll')) + $arguments
        }
        Invoke-TestProcess $hostExecutable $arguments
    }

    function Invoke-Installed([string] $Name, [string[]] $Arguments, [string] $InputText = '') {
        $extension = if ($IsWindows) { '.exe' } else { '' }
        Invoke-TestProcess (Join-Path (Join-Path $script:install 'bin') "$Name$extension") $Arguments -InputText $InputText
    }

    $script:packageResult = Invoke-Packaging 'Package' @{
        Rid = $script:rid
        PublishDirectory = (Split-Path -Parent $script:application)
        OutputDirectory = $script:package
    }
}

AfterAll {
    if ($script:work -and (Test-Path -LiteralPath $script:work)) {
        Remove-Item -LiteralPath $script:work -Recurse -Force
    }
}

Describe 'Parity 8 native packaging and installation' -Tag E2E -Skip:(!$IsWindows -and !$IsLinux) {
    It 'stages a bounded, integrity-manifested application and exactly the dispatcher aliases' {
        $script:packageResult.Code | Should -Be 0 -Because $script:packageResult.Error
        $manifestPath = Join-Path $script:package 'dotreticulum-package.json'
        Test-Path -LiteralPath $manifestPath | Should -BeTrue
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest.rid | Should -Be $script:rid
        $manifest.files.Count | Should -Be 16
        $manifest.files.name | Should -Contain $(if ($IsWindows) { 'DotReticulum.Applications.exe' } else { 'DotReticulum.Applications' })
        $manifest.files.name | Should -Contain $(if ($IsWindows) { 'git-remote-rns.exe' } else { 'git-remote-rns' })
        foreach ($file in $manifest.files) {
            $path = Join-Path $script:package $file.name
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() | Should -Be $file.sha256
            (Get-Item -LiteralPath $path).Length | Should -Be $file.length
        }
        $license = [IO.File]::ReadAllBytes([IO.Path]::Combine($script:package, 'LICENSE'))
        $repositoryLicense = [IO.File]::ReadAllBytes([IO.Path]::Combine((Get-Location).Path, 'LICENSE'))
        [Linq.Enumerable]::SequenceEqual[byte]($license, $repositoryLicense) | Should -BeTrue
    }

    It 'installs and runs every separate executable alias, version/help surfaces, and telemetry subcommand' {
        $installed = Invoke-Packaging 'Install' @{ PackageDirectory = $script:package; InstallRoot = $script:install }
        $installed.Code | Should -Be 0 -Because $installed.Error
        $installedLicense = [IO.File]::ReadAllBytes([IO.Path]::Combine($script:install, 'bin', 'LICENSE'))
        $repositoryLicense = [IO.File]::ReadAllBytes([IO.Path]::Combine((Get-Location).Path, 'LICENSE'))
        [Linq.Enumerable]::SequenceEqual[byte]($installedLicense, $repositoryLicense) | Should -BeTrue
        $manifestBeforeReinstall = [IO.File]::ReadAllBytes([IO.Path]::Combine($script:install, '.dotreticulum-install.json'))
        $reinstall = Invoke-Packaging 'Install' @{ PackageDirectory = $script:package; InstallRoot = $script:install }
        $reinstall.Code | Should -Be 0 -Because $reinstall.Error
        $reinstall.Output | Should -Match 'no changes were made'
        [Linq.Enumerable]::SequenceEqual[byte](
            $manifestBeforeReinstall,
            [IO.File]::ReadAllBytes([IO.Path]::Combine($script:install, '.dotreticulum-install.json'))
        ) | Should -BeTrue
        $aliases = @('rngit', 'rngcs', 'rnid', 'rnstatus', 'rnpath', 'rnprobe', 'rnsh', 'rnodeconf',
            'rnir', 'rnpkg', 'rncp', 'rnx', 'rnsd')
        foreach ($tool in $aliases) {
            $help = Invoke-Installed $tool @('--help')
            $help.Code | Should -Be 0 -Because ($help.Output + $help.Error)
            $help.Output | Should -Match $tool
            if ($tool -ne 'rngcs') {
                $version = Invoke-Installed $tool @('--version')
                $version.Code | Should -Be 0 -Because ($version.Output + $version.Error)
                $version.Output | Should -Match $tool
            }
        }
        $telemetry = Invoke-Installed 'DotReticulum.Applications' @('telemetry', '--help')
        $telemetry.Code | Should -Be 0 -Because ($telemetry.Output + $telemetry.Error)
        $telemetry.Output | Should -Match 'telemetry'
        $gitCapabilities = Invoke-Installed 'git-remote-rns' @('rns', 'rns://bad/group/repo') "capabilities`n`n"
        $gitCapabilities.Code | Should -Be 0 -Because ($gitCapabilities.Output + $gitCapabilities.Error)
        $gitCapabilities.Output | Should -Match 'fetch'
        $gitCapabilities.Output | Should -Match 'push'
    }

    It 'discovers installed utilities without changing PATH and Git launches the installed remote helper' {
        $bin = Join-Path $script:install 'bin'
        $discovery = Invoke-Packaging 'Discover' @{ InstallRoot = $script:install }
        $discovery.Code | Should -Be 0 -Because $discovery.Error
        $discovery.Output | Should -Match 'git-remote-rns'
        $discovery.Output | Should -Match ([regex]::Escape($bin))

        $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $pathSeparator = [IO.Path]::PathSeparator
        $environment = @{
            PATH = "$bin$pathSeparator$env:PATH"
            GIT_TRACE = '1'
        }
        $result = Invoke-TestProcess $git.Source @('ls-remote', 'rns://bad/group/repo') -Environment $environment
        $result.Code | Should -Not -Be 0
        ($result.Output + $result.Error) | Should -Match 'git-remote-rns'
        ($result.Output + $result.Error) | Should -Not -Match "remote helper 'rns' not found"
    }

    It 'rejects unowned collisions, corrupt packages, mismatched RIDs, and modified owned-file removal' {
        $occupiedPackage = [IO.Path]::Combine($script:work, 'occupied-package')
        $null = New-Item -ItemType Directory -Path $occupiedPackage
        Set-Content -LiteralPath ([IO.Path]::Combine($occupiedPackage, 'keep.txt')) 'keep me'
        $staging = Invoke-Packaging 'Package' @{
            Rid = $script:rid
            PublishDirectory = (Split-Path -Parent $script:application)
            OutputDirectory = $occupiedPackage
        }
        $staging.Code | Should -Be 1
        $staging.Error | Should -Match 'must be empty'
        Get-Content -LiteralPath ([IO.Path]::Combine($occupiedPackage, 'keep.txt')) | Should -Be 'keep me'

        $collisionRoot = [IO.Path]::Combine($script:work, 'collision')
        $null = New-Item -ItemType Directory -Path ([IO.Path]::Combine($collisionRoot, 'bin')) -Force
        $collisionName = if ($IsWindows) { 'rnid.exe' } else { 'rnid' }
        Set-Content -LiteralPath ([IO.Path]::Combine($collisionRoot, 'bin', $collisionName)) 'keep me'
        $collision = Invoke-Packaging 'Install' @{ PackageDirectory = $script:package; InstallRoot = $collisionRoot }
        $collision.Code | Should -Be 1
        $collision.Error | Should -Match 'Refusing to overwrite unowned file'
        Get-Content -LiteralPath ([IO.Path]::Combine($collisionRoot, 'bin', $collisionName)) | Should -Be 'keep me'
        Test-Path -LiteralPath ([IO.Path]::Combine($collisionRoot, '.dotreticulum-install.json')) | Should -BeFalse

        $badPackage = [IO.Path]::Combine($script:work, 'corrupt-package')
        Copy-Item -LiteralPath $script:package -Destination $badPackage -Recurse
        Add-Content -LiteralPath ([IO.Path]::Combine($badPackage, $(if ($IsWindows) { 'rnid.exe' } else { 'rnid' }))) 'corrupt'
        $corrupt = Invoke-Packaging 'Install' @{ PackageDirectory = $badPackage; InstallRoot = ([IO.Path]::Combine($script:work, 'corrupt-install')) }
        $corrupt.Code | Should -Be 1
        $corrupt.Error | Should -Match 'integrity validation'

        $mismatchManifest = Get-Content -LiteralPath ([IO.Path]::Combine($script:package, 'dotreticulum-package.json')) -Raw | ConvertFrom-Json
        $mismatchManifest.rid = if ($script:rid -eq 'win-x64') { 'linux-x64' } else { 'win-x64' }
        $mismatchPath = [IO.Path]::Combine($script:work, 'mismatch-package')
        Copy-Item -LiteralPath $script:package -Destination $mismatchPath -Recurse
        if ($mismatchManifest.rid -eq 'win-x64') {
            foreach ($file in $mismatchManifest.files) {
                if ($file.name -eq 'LICENSE') { continue }
                $source = [IO.Path]::Combine($mismatchPath, $file.name)
                $file.name += '.exe'
                Copy-Item -LiteralPath $source -Destination ([IO.Path]::Combine($mismatchPath, $file.name))
            }
        }
        else {
            foreach ($file in $mismatchManifest.files) {
                if ($file.name -eq 'LICENSE') { continue }
                $source = [IO.Path]::Combine($mismatchPath, $file.name)
                $file.name = $file.name.Substring(0, $file.name.Length - 4)
                Copy-Item -LiteralPath $source -Destination ([IO.Path]::Combine($mismatchPath, $file.name))
            }
        }
        $mismatchManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ([IO.Path]::Combine($mismatchPath, 'dotreticulum-package.json'))
        $mismatch = Invoke-Packaging 'Install' @{ PackageDirectory = $mismatchPath; InstallRoot = ([IO.Path]::Combine($script:work, 'mismatch-install')) }
        $mismatch.Code | Should -Be 1
        $mismatch.Error | Should -Match 'does not match this host'

        $manifestPath = [IO.Path]::Combine($script:install, '.dotreticulum-install.json')
        Test-Path -LiteralPath $manifestPath | Should -BeTrue
        $modifiedRoot = [IO.Path]::Combine($script:work, 'modified-install')
        $modified = Invoke-Packaging 'Install' @{ PackageDirectory = $script:package; InstallRoot = $modifiedRoot }
        $modified.Code | Should -Be 0 -Because $modified.Error
        $changed = [IO.Path]::Combine($modifiedRoot, 'bin', $collisionName)
        Add-Content -LiteralPath $changed 'local change'
        $uninstall = Invoke-Packaging 'Uninstall' @{ InstallRoot = $modifiedRoot }
        $uninstall.Code | Should -Be 1
        $uninstall.Error | Should -Match 'locally modified'
        Test-Path -LiteralPath ([IO.Path]::Combine($modifiedRoot, '.dotreticulum-install.json')) | Should -BeTrue
        Test-Path -LiteralPath ([IO.Path]::Combine($modifiedRoot, 'bin', $(if ($IsWindows) { 'rnsd.exe' } else { 'rnsd' }))) | Should -BeTrue
    }

    It 'rejects non-identical upgrades before changing any owned executable or manifest' {
        $upgradeRoot = [IO.Path]::Combine($script:work, 'upgrade-install')
        $initial = Invoke-Packaging 'Install' @{ PackageDirectory = $script:package; InstallRoot = $upgradeRoot }
        $initial.Code | Should -Be 0 -Because $initial.Error
        $modifiedPackage = [IO.Path]::Combine($script:work, 'modified-package')
        Copy-Item -LiteralPath $script:package -Destination $modifiedPackage -Recurse
        $manifestPath = [IO.Path]::Combine($modifiedPackage, 'dotreticulum-package.json')
        $packageManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $changedFile = $packageManifest.files | Where-Object { $_.name -eq $(if ($IsWindows) { 'rnid.exe' } else { 'rnid' }) } | Select-Object -First 1
        $changedPath = [IO.Path]::Combine($modifiedPackage, $changedFile.name)
        [IO.File]::AppendAllText($changedPath, 'new package version')
        $changedFile.length = (Get-Item -LiteralPath $changedPath).Length
        $changedFile.sha256 = (Get-FileHash -LiteralPath $changedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $packageManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath

        $bin = [IO.Path]::Combine($upgradeRoot, 'bin')
        $before = @{}
        foreach ($file in Get-ChildItem -LiteralPath $bin -File) {
            $before[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
        $installManifestPath = [IO.Path]::Combine($upgradeRoot, '.dotreticulum-install.json')
        $manifestBefore = [IO.File]::ReadAllBytes($installManifestPath)
        $refused = Invoke-Packaging 'Install' @{ PackageDirectory = $modifiedPackage; InstallRoot = $upgradeRoot }
        $refused.Code | Should -Be 1
        $refused.Error | Should -Match 'Refusing non-identical upgrade'
        foreach ($file in Get-ChildItem -LiteralPath $bin -File) {
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash | Should -Be $before[$file.Name]
        }
        [Linq.Enumerable]::SequenceEqual[byte]($manifestBefore, [IO.File]::ReadAllBytes($installManifestPath)) | Should -BeTrue
        (Get-ChildItem -LiteralPath $bin -File).Count | Should -Be 16
    }

    It 'removes only owned unchanged files and retains unrelated install-root content' {
        $unrelated = [IO.Path]::Combine($script:install, 'keep.txt')
        Set-Content -LiteralPath $unrelated 'unrelated'
        $unrelatedBin = [IO.Path]::Combine($script:install, 'bin', 'keep.txt')
        Set-Content -LiteralPath $unrelatedBin 'also unrelated'
        $removed = Invoke-Packaging 'Uninstall' @{ InstallRoot = $script:install }
        $removed.Code | Should -Be 0 -Because $removed.Error
        Test-Path -LiteralPath ([IO.Path]::Combine($script:install, '.dotreticulum-install.json')) | Should -BeFalse
        Test-Path -LiteralPath ([IO.Path]::Combine($script:install, 'bin')) | Should -BeTrue
        Get-Content -LiteralPath $unrelated | Should -Be 'unrelated'
        Get-Content -LiteralPath $unrelatedBin | Should -Be 'also unrelated'
    }
}
