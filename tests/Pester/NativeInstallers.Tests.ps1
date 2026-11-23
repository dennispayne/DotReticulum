param(
    [string] $InstallerDirectory,
    [string] $StagedPackageDirectory,
    [string] $Version,
    [string] $Basename,
    [bool] $SystemTest
)

BeforeDiscovery {
    $extensions = if ($IsWindows) { @('msi') } elseif ($IsLinux) { @('deb', 'rpm') } elseif ($IsMacOS) { @('pkg') } else { @() }
}

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    if ($SystemTest -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
            $env:DOTRETICULUM_DISPOSABLE_INSTALL_TEST -ne '1')) {
        throw 'System installation tests require an explicitly opted-in disposable GitHub-hosted runner.'
    }
    $script:installers = (Resolve-Path -LiteralPath $InstallerDirectory).Path
    $script:staged = (Resolve-Path -LiteralPath $StagedPackageDirectory).Path
    $script:manifestPath = Join-Path $script:staged 'dotreticulum-package.json'
    $script:manifest = Get-Content -LiteralPath $script:manifestPath -Raw | ConvertFrom-Json
    $script:rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { throw 'Unsupported installer test host.' }
    if ($script:manifest.rid -ne $script:rid) { throw 'The staged package does not match the test host.' }
    $script:verifiedChecksums = @{}
    $script:msiPayloadVerified = $false
    $script:debPayloadVerified = $false
    $script:rpmPayloadVerified = $false
    $script:pkgPayloadVerified = $false
    $script:work = Join-Path $TestDrive 'native-installer'
    $null = New-Item -ItemType Directory -Path $script:work

    function Invoke-Checked([string] $Executable, [string[]] $Arguments, [int] $TimeoutSeconds = 180, [string] $InputText = '') {
        $result = Invoke-TestProcess $Executable $Arguments -TimeoutSeconds $TimeoutSeconds -InputText $InputText
        $result.Code | Should -Be 0 -Because ($result.Output + $result.Error)
        return $result
    }

    function Assert-Payload([string] $Bin, [string] $Documentation) {
        foreach ($file in $script:manifest.files) {
            $path = if ($file.name -eq 'LICENSE') { Join-Path $Documentation $file.name } else { Join-Path $Bin $file.name }
            Test-Path -LiteralPath $path -PathType Leaf | Should -BeTrue -Because "installer should contain $($file.name)"
            (Get-Item -LiteralPath $path).Length | Should -Be ([long]$file.length)
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() | Should -BeExactly $file.sha256
            if (!$IsWindows -and $file.name -ne 'LICENSE') {
                $mode = [IO.File]::GetUnixFileMode($path)
                $execute = [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherExecute
                ($mode -band $execute) | Should -Be $execute
            }
        }
        $installedManifest = Join-Path $Documentation 'dotreticulum-package.json'
        (Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash |
            Should -BeExactly (Get-FileHash -LiteralPath $script:manifestPath -Algorithm SHA256).Hash
        $suffix = if ($IsWindows) { '.exe' } else { '' }
        $application = Join-Path $Bin "DotReticulum.Applications$suffix"
        if ($IsWindows) {
            $stream = [IO.File]::OpenRead($application)
            try {
                $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
                try { $reader.PEHeaders.CoffHeader.Machine.ToString() | Should -BeExactly 'Amd64' }
                finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        elseif ($IsLinux) {
            $header = Invoke-Checked (Get-Command readelf).Source @('-h', $application)
            $header.Output | Should -Match '(?m)^\s*Machine:\s+Advanced Micro Devices X86-64\r?$'
        }
        else {
            $architectures = Invoke-Checked (Get-Command lipo).Source @('-archs', $application)
            $architectures.Output.Trim() | Should -BeExactly 'arm64'
        }
        $rncp = Join-Path $Bin "rncp$suffix"
        $expectedReference = ($Version.Split('.')[0..2] -join '.')
        $versionResult = Invoke-Checked $rncp @('--version')
        $versionResult.Output | Should -Match ([regex]::Escape("reference RNS $expectedReference"))
        $identity = Join-Path $script:work ("identity-$([guid]::NewGuid().ToString('N'))")
        $null = Invoke-Checked (Join-Path $Bin "rnid$suffix") @('--generate', $identity)
        (Get-Item -LiteralPath $identity).Length | Should -Be 64
        $helper = Invoke-Checked (Join-Path $Bin "git-remote-rns$suffix") @('rns', 'rns://bad/group/repo') -InputText "capabilities`n`n"
        $helper.Output | Should -Match 'fetch'
        $helper.Output | Should -Match 'push'
    }

    function Read-MsiTable([string] $Path, [string] $Table) {
        $query = switch ($Table) {
            'Property' { 'SELECT `Property`, `Value` FROM `Property`' }
            'Environment' { 'SELECT `Name`, `Value` FROM `Environment`' }
            default { throw "Unsupported MSI table '$Table'." }
        }
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $null
        $view = $null
        $rows = [Collections.Generic.List[object]]::new()
        try {
            $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
            $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($query))
            $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
            while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
                try {
                    $name = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
                    $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(2))
                    $rows.Add([pscustomobject]@{ Name = $name; Value = $value })
                }
                finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        }
        finally {
            if ($view) {
                $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
                $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
            }
            if ($database) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
        return $rows.ToArray()
    }

    function Get-MsiProperties([string] $Path) {
        $properties = @{}
        foreach ($row in Read-MsiTable $Path 'Property') { $properties[$row.Name] = $row.Value }
        return $properties
    }

    function Get-MsiTemplate([string] $Path) {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $summary = $null
        try {
            $summary = $installer.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $installer, @($Path, 0))
            return $summary.GetType().InvokeMember('Property', 'GetProperty', $null, $summary, @(7))
        }
        finally {
            if ($summary) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }

    function Test-MsiInstalled([string] $ProductCode) {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        try {
            $state = $installer.GetType().InvokeMember('ProductState', 'GetProperty', $null, $installer, @($ProductCode))
            return $state -eq 5
        }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }

    function Get-MsiRelatedProducts([string] $UpgradeCode) {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $related = $null
        try {
            $related = $installer.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $installer, @($UpgradeCode))
            return @($related | ForEach-Object { [string]$_ })
        }
        finally {
            if ($related) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($related) }
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }

    function New-OlderInstaller([string] $Generator, [string] $Extension, [string] $PackageVersion, [string] $MsiVersion = '') {
        $build = Join-Path $script:work "older-$Generator-build"
        $output = Join-Path $script:work "older-$Generator-packages"
        $arguments = @(
            '-S', (Join-Path $PSScriptRoot '..\..\tools\packaging'), '-B', $build,
            "-DDOTRETICULUM_PACKAGE_DIRECTORY=$script:staged",
            "-DDOTRETICULUM_PACKAGE_VERSION=$PackageVersion",
            "-DDOTRETICULUM_PACKAGE_BASENAME=older-$Generator",
            "-DDOTRETICULUM_RID=$script:rid"
        )
        if ($MsiVersion) { $arguments += "-DDOTRETICULUM_MSI_VERSION=$MsiVersion" }
        $null = Invoke-Checked (Get-Command cmake).Source $arguments
        $result = Invoke-TestProcess (Get-Command cpack).Source @('--config', (Join-Path $build 'CPackConfig.cmake'), '-G', $Generator, '-B', $output) -TimeoutSeconds 180
        if ($result.Code -ne 0) {
            $logs = @(Get-ChildItem -LiteralPath $output -Filter 'wix.log' -File -Recurse -ErrorAction SilentlyContinue)
            $details = @($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 40 }) -join "`n"
            throw "Could not build the older $Generator installer.`n$($result.Output)`n$($result.Error)`n$details"
        }
        $package = Join-Path $output "older-$Generator.$Extension"
        Test-Path -LiteralPath $package -PathType Leaf | Should -BeTrue
        return $package
    }

    function Get-OlderPackageVersion {
        $value = [version]$Version
        if ($value.Revision -gt 0) { return "$($value.Major).$($value.Minor).$($value.Build).$($value.Revision - 1)" }
        if ($value.Build -gt 0) { return "$($value.Major).$($value.Minor).$($value.Build - 1).65535" }
        if ($value.Minor -gt 0) { return "$($value.Major).$($value.Minor - 1).65535.65535" }
        if ($value.Major -gt 0) { return "$($value.Major - 1).65535.65535.65535" }
        throw 'Cannot construct an older package version than 0.0.0.0.'
    }

    function Get-InstalledRecords([string] $Bin, [string] $Documentation) {
        foreach ($file in $script:manifest.files) {
            $path = if ($file.name -eq 'LICENSE') { Join-Path $Documentation $file.name } else { Join-Path $Bin $file.name }
            [pscustomobject]@{ Path = $path; Hash = [string]$file.sha256 }
        }
        [pscustomobject]@{
            Path = (Join-Path $Documentation 'dotreticulum-package.json')
            Hash = (Get-FileHash -LiteralPath $script:manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    function Assert-InstallTargetClear([string] $Bin, [string] $Documentation) {
        if (Test-Path -LiteralPath $Documentation) { throw "Install documentation directory already exists: $Documentation" }
        foreach ($item in Get-InstalledRecords $Bin $Documentation) {
            if (Test-Path -LiteralPath $item.Path) { throw "Install target already exists: $($item.Path)" }
        }
    }

    function Get-MachinePathEntries {
        $value = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        return @($value -split ';' | Where-Object { $_ -ne '' } | ForEach-Object { $_.TrimEnd('\') })
    }

    function Invoke-Msi([string[]] $Arguments, [string] $Log) {
        $result = Invoke-TestProcess (Get-Command msiexec.exe).Source ($Arguments + @('/qn', '/norestart', '/l*v', $Log)) -TimeoutSeconds 180
        if ($result.Code -ne 0) {
            $details = if (Test-Path -LiteralPath $Log) { (Get-Content -LiteralPath $Log -Tail 40) -join "`n" } else { '' }
            throw "Windows Installer failed with exit code $($result.Code).`n$($result.Error)`n$details"
        }
        return $result
    }
}

Describe 'Native installer artifacts' -Tag Tooling {
    It 'has a matching SHA-256 checksum for <_>' -ForEach $extensions {
        $extension = $_
        $name = "$Basename.$extension"
        $path = Join-Path $script:installers $name
        Test-Path -LiteralPath $path -PathType Leaf | Should -BeTrue
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        (Get-Content -LiteralPath "$path.sha256" -Raw).Trim() | Should -BeExactly "$hash  $name"
        $script:verifiedChecksums[$extension] = $true
    }

    if ($IsWindows) {
        It 'contains the expected MSI version, upgrade identity and machine PATH entry' {
            $msi = Join-Path $script:installers "$Basename.msi"
            $properties = Get-MsiProperties $msi
            $revision = & git -C (Join-Path $PSScriptRoot '..\..') rev-list --count HEAD
            $LASTEXITCODE | Should -Be 0
            $parts = $Version.Split('.')
            $properties.ProductName | Should -BeExactly "DotReticulum $Version"
            $properties.ProductVersion | Should -BeExactly "$($parts[0]).$($parts[1]).$revision"
            $properties.UpgradeCode | Should -BeExactly '{6C88E8A8-73A8-438A-90FC-83244E24E77B}'
            $properties.ALLUSERS | Should -BeExactly '1'
            (Get-MsiTemplate $msi) | Should -Match '^x64;'
            $environment = @(Read-MsiTable $msi 'Environment' | Where-Object { $_.Name -eq '=-*PATH' })
            $environment.Count | Should -Be 1
            $environment[0].Value | Should -BeExactly '[~];[CM_DP_bin]'
        }

        It 'extracts the MSI and executes its native utilities' {
            $msi = Join-Path $script:installers "$Basename.msi"
            $root = Join-Path $script:work 'msi'
            $log = Join-Path $script:work 'msi-extract.log'
            $null = Invoke-Checked (Get-Command msiexec.exe).Source @('/a', $msi, '/qn', '/norestart', "TARGETDIR=$root", '/l*v', $log)
            $prefix = [IO.Path]::Combine($root, 'PFiles64', 'DotReticulum')
            $application = [IO.Path]::Combine($prefix, 'bin', 'DotReticulum.Applications.exe')
            (Get-Item -LiteralPath $application).VersionInfo.FileVersion | Should -BeExactly $Version
            Assert-Payload (Join-Path $prefix 'bin') $prefix
            $script:msiPayloadVerified = $true
        }

        It 'builds a distinct older MSI for upgrade and downgrade acceptance' {
            $current = Get-MsiProperties (Join-Path $script:installers "$Basename.msi")
            $revision = [int](& git -C (Join-Path $PSScriptRoot '..\..') rev-list --count HEAD)
            if ($LASTEXITCODE -ne 0 -or $revision -lt 2) { throw 'Full Git history is required for the MSI upgrade fixture.' }
            $parts = $Version.Split('.')
            $olderVersion = "$($parts[0]).$($parts[1]).$($revision - 1)"
            $script:olderMsi = New-OlderInstaller 'WIX' 'msi' $Version $olderVersion
            $older = Get-MsiProperties $script:olderMsi
            $older.ProductVersion | Should -BeExactly $olderVersion
            $older.ProductCode | Should -Not -Be $current.ProductCode
            $older.UpgradeCode | Should -BeExactly $current.UpgradeCode
        }
    }

    if ($IsLinux) {
        It 'extracts the Debian package with matching files and executable utilities' {
            $deb = Join-Path $script:installers "$Basename.deb"
            $metadata = Invoke-Checked (Get-Command dpkg-deb).Source @('--field', $deb)
            $metadata.Output | Should -Match '(?m)^Package: dotreticulum\r?$'
            $metadata.Output | Should -Match "(?m)^Version: $([regex]::Escape($Version))\r?$"
            $metadata.Output | Should -Match '(?m)^Architecture: amd64\r?$'
            $metadata.Output | Should -Match '(?m)^Depends: .+'
            $root = Join-Path $script:work 'deb'
            $null = Invoke-Checked (Get-Command dpkg-deb).Source @('--extract', $deb, $root)
            Assert-Payload ([IO.Path]::Combine($root, 'usr', 'bin')) ([IO.Path]::Combine($root, 'usr', 'share', 'doc', 'dotreticulum'))
            $script:debPayloadVerified = $true
        }

        It 'extracts the RPM package with matching files and executable utilities' {
            $rpm = Join-Path $script:installers "$Basename.rpm"
            $metadata = Invoke-Checked (Get-Command rpm).Source @('-qp', '--qf', '%{NAME}|%{VERSION}|%{ARCH}', $rpm)
            $metadata.Output.Trim() | Should -BeExactly "dotreticulum|$Version|x86_64"
            $dependencies = Invoke-Checked (Get-Command rpm).Source @('-qp', '--requires', $rpm)
            $dependencies.Output | Should -Match '(?m)^libc\.so\.6'
            $root = Join-Path $script:work 'rpm'
            $null = New-Item -ItemType Directory -Path $root
            $command = 'set -o pipefail; rpm2cpio "$1" | cpio --extract --make-directories --quiet --directory "$2"'
            $null = Invoke-Checked (Get-Command bash).Source @('-c', $command, '--', $rpm, $root)
            Assert-Payload ([IO.Path]::Combine($root, 'usr', 'bin')) ([IO.Path]::Combine($root, 'usr', 'share', 'doc', 'dotreticulum'))
            $script:rpmPayloadVerified = $true
        }
    }
    elseif ($IsMacOS) {
        It 'expands the macOS package and executes its native utilities' {
            $package = Join-Path $script:installers "$Basename.pkg"
            $root = Join-Path $script:work 'pkg'
            $null = Invoke-Checked (Get-Command pkgutil).Source @('--expand-full', $package, $root)
            $rnid = @(Get-ChildItem -LiteralPath $root -File -Filter 'rnid' -Recurse)
            if ($rnid.Count -ne 1) {
                $entries = @(Get-ChildItem -LiteralPath $root -Force -Recurse | Select-Object -First 80 | ForEach-Object {
                    "{0} [{1}] {2}" -f [IO.Path]::GetRelativePath($root, $_.FullName), $_.Attributes, $_.LinkTarget
                }) -join "`n"
                $payload = Invoke-TestProcess (Get-Command pkgutil).Source @('--payload-files', $package)
                throw "Expected one expanded rnid; found $($rnid.Count).`nEntries:`n$entries`nPayload listing (exit $($payload.Code)):`n$($payload.Output)$($payload.Error)"
            }
            $bin = $rnid[0].Directory.FullName
            $prefix = Split-Path -Parent $bin
            Assert-Payload $bin ([IO.Path]::Combine($prefix, 'share', 'doc', 'dotreticulum'))
            $script:pkgPayloadVerified = $true
        }
    }

    Describe 'Native installer system transactions on disposable GitHub-hosted runners' -Tag Tooling {
        if ($IsWindows) {
            It 'upgrades, rejects a downgrade, preserves PATH and unrelated files, and uninstalls' {
                if (!$SystemTest) { Set-ItResult -Skipped -Because 'Requires -InstallerSystemTest on a disposable GitHub-hosted runner.' }
                if (!$script:verifiedChecksums['msi'] -or !$script:msiPayloadVerified) {
                    throw 'MSI checksum and payload checks must pass before system installation.'
                }
                $current = Join-Path $script:installers "$Basename.msi"
                $properties = Get-MsiProperties $current
                $older = $script:olderMsi
                if (!$older) { throw 'The older MSI upgrade fixture is unavailable.' }
                $oldProperties = Get-MsiProperties $older
                $oldProperties.ProductCode | Should -Not -Be $properties.ProductCode
                $oldProperties.UpgradeCode | Should -BeExactly $properties.UpgradeCode
                if (@(Get-MsiRelatedProducts $properties.UpgradeCode).Count -ne 0) {
                    throw 'An existing related Windows Installer product would be affected by this test.'
                }
                $root = Join-Path $script:work 'windows-install'
                $bin = Join-Path $root 'bin'
                Assert-InstallTargetClear $bin $root
                $beforePath = @(Get-MachinePathEntries)
                if ($beforePath -contains $bin.TrimEnd('\')) { throw 'The test install directory is already on machine PATH.' }
                $attempted = $false
                try {
                    $attempted = $true
                    $null = Invoke-Msi @('/i', $older, "INSTALL_ROOT=$root") (Join-Path $script:work 'msi-old.log')
                    Test-MsiInstalled $oldProperties.ProductCode | Should -BeTrue
                    Assert-Payload $bin $root
                    $sentinel = Join-Path $root 'unrelated-installer-test.txt'
                    [IO.File]::WriteAllText($sentinel, 'preserve')
                    $null = Invoke-Msi @('/i', $current, "INSTALL_ROOT=$root") (Join-Path $script:work 'msi-upgrade.log')
                    Test-MsiInstalled $oldProperties.ProductCode | Should -BeFalse
                    Test-MsiInstalled $properties.ProductCode | Should -BeTrue
                    Assert-Payload $bin $root
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                    $afterUpgrade = @(Get-MachinePathEntries)
                    @($afterUpgrade | Where-Object { $_ -eq $bin.TrimEnd('\') }).Count | Should -Be 1
                    ($afterUpgrade | Where-Object { $_ -ne $bin.TrimEnd('\') }) -join ';' | Should -BeExactly ($beforePath -join ';')
                    $downgradeLog = Join-Path $script:work 'msi-downgrade.log'
                    $downgrade = Invoke-TestProcess (Get-Command msiexec.exe).Source @('/i', $older, '/qn', '/norestart', '/l*v', $downgradeLog) -TimeoutSeconds 180
                    $downgrade.Code | Should -Not -Be 0
                    (Get-Content -LiteralPath $downgradeLog -Raw) | Should -Match 'A later version of .* is already installed'
                    Test-MsiInstalled $properties.ProductCode | Should -BeTrue
                    Assert-Payload $bin $root
                    $null = Invoke-Msi @('/x', $properties.ProductCode) (Join-Path $script:work 'msi-uninstall.log')
                    Test-MsiInstalled $properties.ProductCode | Should -BeFalse
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                    foreach ($item in Get-InstalledRecords $bin $root) { Test-Path -LiteralPath $item.Path | Should -BeFalse }
                    (@(Get-MachinePathEntries) -join ';') | Should -BeExactly ($beforePath -join ';')
                }
                finally {
                    if ($attempted) {
                        foreach ($code in @($properties.ProductCode, $oldProperties.ProductCode)) {
                            if (Test-MsiInstalled $code) {
                                $null = Invoke-Msi @('/x', $code) (Join-Path $script:work "msi-cleanup-$($code.Trim('{}')).log")
                            }
                        }
                    }
                }
            }
        }
        elseif ($IsLinux) {
            It 'installs, upgrades and removes the Debian package without removing unrelated files' {
                if (!$SystemTest) { Set-ItResult -Skipped -Because 'Requires -InstallerSystemTest on a disposable GitHub-hosted runner.' }
                if (!$script:verifiedChecksums['deb'] -or !$script:debPayloadVerified) {
                    throw 'Debian checksum and payload checks must pass before system installation.'
                }
                $sudo = (Get-Command sudo).Source
                $current = Join-Path $script:installers "$Basename.deb"
                $oldVersion = Get-OlderPackageVersion
                $older = New-OlderInstaller 'DEB' 'deb' $oldVersion
                $bin = '/usr/bin'
                $doc = '/usr/share/doc/dotreticulum'
                Assert-InstallTargetClear $bin $doc
                $query = Invoke-TestProcess (Get-Command dpkg-query).Source @('-W', 'dotreticulum')
                if ($query.Code -eq 0) { throw 'The Debian package is already registered on this runner.' }
                $sentinel = Join-Path $doc 'unrelated-installer-test.txt'
                $sentinelSource = Join-Path $script:work 'debian-sentinel.txt'
                [IO.File]::WriteAllText($sentinelSource, 'preserve')
                $attempted = $false
                $sentinelCreated = $false
                try {
                    $attempted = $true
                    $null = Invoke-Checked $sudo @('dpkg', '--install', $older)
                    $oldQuery = Invoke-Checked (Get-Command dpkg-query).Source @('-W', '-f=${Version}', 'dotreticulum')
                    $oldQuery.Output | Should -BeExactly $oldVersion
                    $sentinelCreated = $true
                    $null = Invoke-Checked $sudo @('install', '-m', '0644', $sentinelSource, $sentinel)
                    $null = Invoke-Checked $sudo @('dpkg', '--install', $current)
                    $currentQuery = Invoke-Checked (Get-Command dpkg-query).Source @('-W', '-f=${Version}', 'dotreticulum')
                    $currentQuery.Output | Should -BeExactly $Version
                    Assert-Payload $bin $doc
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                    $downgrade = Invoke-TestProcess $sudo @('env', 'LC_ALL=C', 'dpkg', '--refuse-downgrade', '--install', $older) -TimeoutSeconds 180
                    $downgrade.Code | Should -Be 0
                    ($downgrade.Output + $downgrade.Error) | Should -Match '(?i)will not downgrade .* skipping'
                    $currentQuery = Invoke-Checked (Get-Command dpkg-query).Source @('-W', '-f=${Version}', 'dotreticulum')
                    $currentQuery.Output | Should -BeExactly $Version
                    Assert-Payload $bin $doc
                    $null = Invoke-Checked $sudo @('dpkg', '--remove', 'dotreticulum')
                    foreach ($item in Get-InstalledRecords $bin $doc) { Test-Path -LiteralPath $item.Path | Should -BeFalse }
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                }
                finally {
                    if ($attempted) { $null = Invoke-Checked $sudo @('dpkg', '--purge', 'dotreticulum') }
                    if ($sentinelCreated -and (Test-Path -LiteralPath $sentinel)) {
                        $null = Invoke-Checked $sudo @('rm', '--', $sentinel)
                    }
                    if ((Test-Path -LiteralPath $doc) -and @(Get-ChildItem -LiteralPath $doc -Force).Count -eq 0) {
                        $null = Invoke-Checked $sudo @('rmdir', '--', $doc)
                    }
                }
            }

            It 'upgrades and removes the RPM in an isolated database while preserving unrelated files' {
                if (!$SystemTest) { Set-ItResult -Skipped -Because 'Requires -InstallerSystemTest on a disposable GitHub-hosted runner.' }
                if (!$script:verifiedChecksums['rpm'] -or !$script:rpmPayloadVerified) {
                    throw 'RPM checksum and payload checks must pass before system installation.'
                }
                $sudo = (Get-Command sudo).Source
                $current = Join-Path $script:installers "$Basename.rpm"
                $oldVersion = Get-OlderPackageVersion
                $older = New-OlderInstaller 'RPM' 'rpm' $oldVersion
                $root = Join-Path $script:work 'rpm-transaction-root'
                $null = New-Item -ItemType Directory -Path $root
                $bin = [IO.Path]::Combine($root, 'usr', 'bin')
                $doc = [IO.Path]::Combine($root, 'usr', 'share', 'doc', 'dotreticulum')
                $sentinel = Join-Path $doc 'unrelated-installer-test.txt'
                $sentinelSource = Join-Path $script:work 'rpm-sentinel.txt'
                [IO.File]::WriteAllText($sentinelSource, 'preserve')
                $attempted = $false
                try {
                    $null = Invoke-Checked $sudo @('rpm', '--root', $root, '--initdb')
                    $attempted = $true
                    $null = Invoke-Checked $sudo @('rpm', '--root', $root, '--install', '--nodeps', $older)
                    $oldQuery = Invoke-Checked $sudo @('rpm', '--root', $root, '-q', '--qf', '%{VERSION}', 'dotreticulum')
                    $oldQuery.Output | Should -BeExactly $oldVersion
                    $null = Invoke-Checked $sudo @('install', '-m', '0644', $sentinelSource, $sentinel)
                    $null = Invoke-Checked $sudo @('rpm', '--root', $root, '--upgrade', '--nodeps', $current)
                    $currentQuery = Invoke-Checked $sudo @('rpm', '--root', $root, '-q', '--qf', '%{VERSION}', 'dotreticulum')
                    $currentQuery.Output | Should -BeExactly $Version
                    Assert-Payload $bin $doc
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                    $downgrade = Invoke-TestProcess $sudo @('rpm', '--root', $root, '--upgrade', '--nodeps', $older) -TimeoutSeconds 180
                    $downgrade.Code | Should -Not -Be 0
                    ($downgrade.Output + $downgrade.Error) | Should -Match '(?i)newer'
                    $null = Invoke-Checked $sudo @('rpm', '--root', $root, '--erase', 'dotreticulum')
                    foreach ($item in Get-InstalledRecords $bin $doc) { Test-Path -LiteralPath $item.Path | Should -BeFalse }
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                }
                finally {
                    if ($attempted) {
                        $query = Invoke-TestProcess $sudo @('rpm', '--root', $root, '-q', 'dotreticulum')
                        if ($query.Code -eq 0) { $null = Invoke-Checked $sudo @('rpm', '--root', $root, '--erase', 'dotreticulum') }
                    }
                    $uid = (& id -u).Trim()
                    $gid = (& id -g).Trim()
                    $null = Invoke-Checked $sudo @('chown', '-R', "${uid}:${gid}", '--', $root)
                }
            }
        }
        elseif ($IsMacOS) {
            It 'installs and reinstalls the PKG, then removes only its owned files and receipt' {
                if (!$SystemTest) { Set-ItResult -Skipped -Because 'Requires -InstallerSystemTest on a disposable GitHub-hosted runner.' }
                if (!$script:verifiedChecksums['pkg'] -or !$script:pkgPayloadVerified) {
                    throw 'PKG checksum and payload checks must pass before system installation.'
                }
                $sudo = (Get-Command sudo).Source
                $package = Join-Path $script:installers "$Basename.pkg"
                $expanded = Join-Path $script:work 'pkg'
                $infoFiles = @(Get-ChildItem -LiteralPath $expanded -File -Filter 'PackageInfo' -Recurse)
                $infoFiles.Count | Should -Be 1
                [xml]$info = Get-Content -LiteralPath $infoFiles[0].FullName -Raw
                $identifier = [string]$info.'pkg-info'.identifier
                $identifier | Should -Match '^net\.dotreticulum\.utilities(\.|$)'
                $before = Invoke-TestProcess (Get-Command pkgutil).Source @('--pkg-info', $identifier)
                if ($before.Code -eq 0) { throw "The macOS receipt is already registered: $identifier" }
                $bin = '/usr/local/bin'
                $doc = '/usr/local/share/doc/dotreticulum'
                Assert-InstallTargetClear $bin $doc
                $sentinel = Join-Path $doc 'unrelated-installer-test.txt'
                $sentinelSource = Join-Path $script:work 'mac-sentinel.txt'
                [IO.File]::WriteAllText($sentinelSource, 'preserve')
                $attempted = $false
                $sentinelCreated = $false
                try {
                    try {
                        $attempted = $true
                        $null = Invoke-Checked $sudo @('installer', '-pkg', $package, '-target', '/')
                        $receipt = Invoke-Checked (Get-Command pkgutil).Source @('--pkg-info', $identifier)
                        $receipt.Output | Should -Match ([regex]::Escape($identifier))
                        Assert-Payload $bin $doc
                        $sentinelCreated = $true
                        $null = Invoke-Checked $sudo @('install', '-m', '0644', $sentinelSource, $sentinel)
                        $null = Invoke-Checked $sudo @('installer', '-pkg', $package, '-target', '/')
                        Assert-Payload $bin $doc
                    }
                    finally {
                        if ($attempted) {
                            foreach ($item in Get-InstalledRecords $bin $doc) {
                                if (Test-Path -LiteralPath $item.Path) {
                                    $actual = (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash.ToLowerInvariant()
                                    if ($actual -ne $item.Hash) { throw "Refusing to remove a changed file: $($item.Path)" }
                                    $null = Invoke-Checked $sudo @('rm', '--', $item.Path)
                                }
                            }
                            $receipt = Invoke-TestProcess (Get-Command pkgutil).Source @('--pkg-info', $identifier)
                            if ($receipt.Code -eq 0) { $null = Invoke-Checked $sudo @('pkgutil', '--forget', $identifier) }
                        }
                    }
                    foreach ($item in Get-InstalledRecords $bin $doc) { Test-Path -LiteralPath $item.Path | Should -BeFalse }
                    (Get-Content -LiteralPath $sentinel -Raw) | Should -BeExactly 'preserve'
                    (Invoke-TestProcess (Get-Command pkgutil).Source @('--pkg-info', $identifier)).Code | Should -Not -Be 0
                }
                finally {
                    if ($sentinelCreated -and (Test-Path -LiteralPath $sentinel)) {
                        $null = Invoke-Checked $sudo @('rm', '--', $sentinel)
                    }
                    if ((Test-Path -LiteralPath $doc) -and @(Get-ChildItem -LiteralPath $doc -Force).Count -eq 0) {
                        $null = Invoke-Checked $sudo @('rmdir', '--', $doc)
                    }
                }
            }
        }
    }
}
