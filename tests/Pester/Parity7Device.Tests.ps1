param(
    [string] $Application,
    [string] $Python,
    [string] $Upstream,
    [bool] $AuthorizeRNodeFirmware = $false,
    [string] $RNodeUsbSerial,
    [string] $RNodeFirmwareVersion,
    [string] $RNodeFlasher
)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
}

Describe 'Authorized physical RAK4631 maintenance' -Tag Hardware {
    It 'persists a reversible EEPROM setting across USB DFU reflash and restores the original EEPROM' -Skip:(!$AuthorizeRNodeFirmware) {
        $RNodeUsbSerial | Should -Not -BeNullOrEmpty
        $RNodeFirmwareVersion | Should -Match '^\d+\.\d+$'
        Test-Path -LiteralPath $RNodeFlasher -PathType Leaf | Should -BeTrue
        function Invoke-RNodeCommand {
            param([string[]] $Options)
            $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--usb-serial', $RNodeUsbSerial) + $Options) -TimeoutSeconds 660
            $result.Code | Should -Be 0 -Because ($result.Error + $result.Output)
            return $result
        }
        $information = Invoke-RNodeCommand @('--info')
        $information.Output | Should -Match 'RAK4631'
        $information.Output | Should -Match ('Firmware version:\s+' + [regex]::Escape($RNodeFirmwareVersion) + '\b')
        $beforePath = Join-Path $TestDrive 'before.eeprom'
        $null = Invoke-RNodeCommand @('--eeprom-backup', '--backup-file', $beforePath)
        $before = [IO.File]::ReadAllBytes($beforePath)
        $before.Length | Should -Be 200
        $brightness = $before[0xb2]
        $configuration = Invoke-RNodeCommand @('--config')
        $configuration.Output | Should -Match ('Display brightness:\s+' + $brightness + '\b')
        $configuration.Output | Should -Not -Match 'SSID:'
        $changed = $before.Clone()
        $changed[0xb2] = $brightness -bxor 1
        try {
            $null = Invoke-RNodeCommand @('--display', [string] $changed[0xb2], '--authorize-device-write')
            $changedPath = Join-Path $TestDrive 'changed.eeprom'
            $null = Invoke-RNodeCommand @('--eeprom-backup', '--backup-file', $changedPath)
            ([Convert]::ToHexString([IO.File]::ReadAllBytes($changedPath)) -ceq [Convert]::ToHexString($changed)) |
                Should -BeTrue -Because 'only the requested brightness byte may change'
            $null = Invoke-RNodeCommand @('--force-update', '--fw-version', $RNodeFirmwareVersion,
                '--flasher', $RNodeFlasher, '--state-dir', (Join-Path $TestDrive 'firmware-state'),
                '--authorize-device-write', '--authorize-flash', '--authorize-firmware-download', '--authorize-unverified-device')
            $afterPath = Join-Path $TestDrive 'after-reflash.eeprom'
            $null = Invoke-RNodeCommand @('--eeprom-backup', '--backup-file', $afterPath)
            ([Convert]::ToHexString([IO.File]::ReadAllBytes($afterPath)) -ceq [Convert]::ToHexString($changed)) |
                Should -BeTrue -Because 'every EEPROM byte must survive firmware replacement'
        } finally {
            $null = Invoke-RNodeCommand @('--display', [string] $brightness, '--authorize-device-write')
            $restoredPath = Join-Path $TestDrive 'restored.eeprom'
            $null = Invoke-RNodeCommand @('--eeprom-backup', '--backup-file', $restoredPath)
            ([Convert]::ToHexString([IO.File]::ReadAllBytes($restoredPath)) -ceq [Convert]::ToHexString($before)) |
                Should -BeTrue -Because 'every original EEPROM byte must be restored'
        }
    }
}

Describe 'Parity 7 native maintenance safety and placeholder processes' -Tag E2E, Device {
    It 'describes actual maintenance scope without claiming physical firmware validation' -Tag 'Contract:UTIL-RNODECONF-001' {
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--help'))
        $result.Code | Should -Be 0
        $result.Output | Should -Match 'authorize-device-write'
        $result.Output | Should -Match 'NOT verified'
    }

    It 'lists actual pinned board variants without opening serial hardware' -Tag 'Contract:UTIL-RNODECONF-001' {
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--list-models'))
        $result.Code | Should -Be 0
        $result.Output | Should -Match 'rnode_firmware_rak4631.zip'
        $result.Output | Should -Match 'SX1262'
    }

    It 'accepts long equals syntax and reports parser errors with argparse exit status' -Tag 'Contract:UTIL-RNODECONF-001' {
        $state = Join-Path $TestDrive 'equals-state'
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--key', '--authorize-key-write', "--state-dir=$state"))
        $result.Code | Should -Be 0 -Because $result.Error
        Test-Path -LiteralPath (Join-Path $state 'firmware\signing.key') | Should -BeTrue
        $missing = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--channel'))
        $missing.Code | Should -Be 2
        $unknown = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--unknown'))
        $unknown.Code | Should -Be 2
    }

    It 'rejects device writes before accessing a serial port' -Tag 'Contract:UTIL-RNODECONF-001' {
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', 'NOT-A-REAL-PORT', '--normal'))
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'authorize-device-write'
        $result.Error | Should -Match 'port was not opened'
    }

    It 'rejects unapproved firmware operations rather than invoking tools or downloading' -Tag 'Contract:UTIL-RNODECONF-001' {
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', 'NOT-A-REAL-PORT', '--update'))
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'authorize-device-write'
        $result.Error | Should -Match 'no device tool was run'
    }

    It 'does not leak a malformed credential on failure' -Tag 'Contract:UTIL-RNODECONF-001' {
        $credential = 'do-not-log-this-credential'
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', 'NOT-A-REAL-PORT', '--psk', $credential))
        $result.Code | Should -Be 1
        ($result.Output + $result.Error) | Should -Not -Match ([regex]::Escape($credential))
    }

    It 'refuses unapproved local trust enrollment without creating state' -Tag 'Contract:UTIL-RNODECONF-001' {
        $state = Join-Path $TestDrive 'unapproved-trust'
        $result = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--trust-key', '00', '--state-dir', $state))
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'authorize-trust'
        Test-Path -LiteralPath $state | Should -BeFalse
    }

    It 'generates persistent native signing keys only with explicit authorization and does not overwrite them' -Tag 'Contract:UTIL-RNODECONF-001' {
        $state = Join-Path $TestDrive 'signing-state'
        $denied = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--key', '--state-dir', $state))
        $denied.Code | Should -Be 1
        Test-Path -LiteralPath $state | Should -BeFalse
        $first = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--key', '--authorize-key-write', '--state-dir', $state))
        $first.Code | Should -Be 0 -Because $first.Error
        $key = Join-Path $state 'firmware\signing.key'
        $hash = (Get-FileHash -LiteralPath $key -Algorithm SHA256).Hash
        $second = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--key', '--authorize-key-write', '--state-dir', $state))
        $second.Code | Should -Be 0 -Because $second.Error
        (Get-FileHash -LiteralPath $key -Algorithm SHA256).Hash | Should -Be $hash
        $public = Invoke-TestProcess $executable ($prefix + @('rnodeconf', '--public', '--state-dir', $state))
        $public.Code | Should -Be 0 -Because $public.Error
        $public.Output | Should -Match 'EEPROM Signing Public key'
        $public.Output | Should -Match 'Device Signing Public key'
    }

    It 'exposes only an honest placeholder shell for <Tool>' -Tag 'Contract:UTIL-RNIR-001' -ForEach @(@{ Tool = 'rnir' }, @{ Tool = 'rnpkg' }) {
        $result = Invoke-TestProcess $executable ($prefix + @($Tool, '--help'))
        $result.Code | Should -Be 0
        $result.Output | Should -Match 'upstream placeholder'
        $result = Invoke-TestProcess $executable ($prefix + @($Tool, 'install', 'example'))
        $result.Code | Should -Be 1
        $result.Error | Should -Match 'no resolver/package operations'
    }

    It 'prints the actual upstream minimal package configuration example' -Tag 'Contract:UTIL-RNIR-001' {
        $result = Invoke-TestProcess $executable ($prefix + @('rnpkg', '--exampleconfig'))
        $result.Code | Should -Be 0
        $result.Output.Trim() | Should -Be '# This is an example package manager configuration file.'
    }

    It 'initializes configured runtime state then exits without offering placeholder operations' -Tag 'Contract:UTIL-RNIR-001' -ForEach @(@{ Tool = 'rnir' }, @{ Tool = 'rnpkg' }) {
        $state = Join-Path $TestDrive $Tool
        $null = New-Item -ItemType Directory -Path $state
        @'
[reticulum]
share_instance=no
[interfaces]
'@ | Set-Content -LiteralPath (Join-Path $state 'config')
        $result = Invoke-TestProcess $executable ($prefix + @($Tool, '--config', $state))
        $result.Code | Should -Be 0 -Because $result.Error
        Test-Path -LiteralPath (Join-Path $state 'storage') | Should -BeTrue
    }
}
