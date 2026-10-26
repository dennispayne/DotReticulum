# RNode maintenance (`rnodeconf`)

`rnodeconf` is DotReticulum's native RNode maintenance utility, targeting the upstream RNode Config Utility 2.5.0 in RNS 1.5.7. It supports device inspection, configuration, EEPROM operations, firmware acquisition/installation, and manufacturing/signing workflows. Simulator and source-derived command evidence do not imply physical-device parity. Stop any daemon that owns the serial port before opening the device.

```sh
rnodeconf --help
rnodeconf <serial-port> --info
rnodeconf <serial-port> --eeprom-backup --backup-file <path>
rnodeconf --list-ports
rnodeconf --list-models
```

From a source checkout, use `dotnet run --project src/DotReticulum.Applications -- rnodeconf ...`. The command accepts one serial port and operation options; see `rnodeconf --help` for the complete syntax.

## Configuration and operations

The utility opens serial at 115200 baud, 8N1. Opening alone sends no commands. Identification sends detect, version, platform, MCU, board, device-hash, and hash requests and requires a response within three seconds. Port listing does not open a device. `--usb-serial ID` selects one USB identity and can track a changed port after reset; recovery is bounded to 20 seconds. If no stable serial is exposed, the original port is retried and a moved device must be selected again.

| Operation | Options and behavior |
| --- | --- |
| Inspect | `--info/-i` reports model, board, EEPROM signer status, firmware/hardware revision, serial, modem, RF limits, manufacture data, operating mode, and TNC values. `--config/-c` reports Bluetooth, Wi-Fi, channel, display, IA, and network settings; PSK is masked unless `--show-psk` is used. `--eeprom-dump` prints lowercase raw hex. `--eeprom-backup` saves raw EEPROM bytes. `--backup-file PATH` selects a non-overwriting path; the default directory is `~/.config/rnodeconf/eeprom` with timestamped filenames. |
| Device settings | `--display`, `--timeout`, `--rotation`, `--np`, `--display-addr`, `--recondition-display`, `--ia-enable/-x`, `--ia-disable/-X`, Bluetooth `-b/-B/-p`, `--wifi`, `--channel`, `--ssid`, `--psk`, `--ip`, and `--nm` configure device features. Values are validated before opening the port. Brightness, timeout, and rotation use upstream clamping; UTF-8 byte lengths, channel values, and IP addresses are validated before opening. |
| Normal and TNC modes | `--normal/-N` selects normal mode. `--tnc/-T` requires explicit `--freq`, `--bw`, `--txp`, `--sf`, and `--cr`. TNC enables RF. |
| Trust and EEPROM | `--trust-key DER_HEX --authorize-trust` enrolls an RSA SubjectPublicKeyInfo DER key under `--state-dir`. `--eeprom-wipe`, `--rom/-r`, `--key/-k`, `--public/-P`, `--sign/-S`, and `--firmware-hash/-H` perform manufacturing/key operations. `--key` stores native Reticulum private-key bytes and an RSA-1024 PKCS#8 key without overwriting; `--public` exports public components only, and `--sign` signs the reported device hash with Ed25519. `-K` and `-L` query target and actual firmware hashes; `-H` sets a firmware hash. A serial number is reserved before device writes and is not reused after interruption. EEPROM lock is written last; read-back checksum and signature are verified where applicable. Installation and standalone EEPROM manufacturing re-probe the device, verify the selected firmware version when installing, provision a firmware hash when available, reset, reopen by USB identity, verify EEPROM, and save a non-overwriting backup. |
| Firmware | `--fw-version`, `--fw-url`, `--nocheck`, `--clear-cache/-C`, `--use-extracted/-E`, `--extract/-e`, `--update/-u`, `--force-update/-U`, `--flash/-f`, and `--autoinstall/-a` control firmware workflows. `--platform`, `--product`, `--model`, and `--hwrev` explicitly select a target. `--flasher` selects an independently installed tool. |

Firmware metadata responses are bounded to 1 MiB and three minutes; firmware downloads to 64 MiB. The native planner contains 45 source-backed model mappings and 126 target/version/flasher plans; these describe plans, not proof of device acceptance. `--fw-version` selects a release, `--fw-url` selects a custom release base, and `--nocheck` disables the upstream-style check. Cache clearing and offline extracted-firmware reuse are explicit operations. Cached hashes are integrity data, not authenticated publisher provenance. Archive expansion is limited to 128 MiB and 128 entries, with traversal, links, and duplicate paths rejected. Extract emits the source's five ESP32 flash components and validates their sizes. Update closes the port before tool execution, then reopens and checks the reported version and EEPROM. When a selected partition hash is available, both target and actual device hashes are queried again after restart and compared in constant time; a mismatch is an error. Changed-port recovery follows the USB identity when it is unique. AVR avrdude, ESP32 esptool, and NRF52 DFU remain external installed tools; downloaded scripts are never executed. Tool processes use argument arrays without a shell, bounded output, cancellation, and a ten-minute deadline.

## Authorization and security

Every device setting or RF change requires `--authorize-device-write`; reset operations such as extraction also require device-write/flash authorization. Trust enrollment requires `--authorize-trust`. EEPROM, flash, key, cache, and firmware-download actions require `--authorize-eeprom`, `--authorize-flash`, `--authorize-key-write`, `--authorize-cache-write`, and `--authorize-firmware-download` respectively. Updating or extracting an unverified device additionally requires `--authorize-unverified-device`. `--authorize-firmware-fallback` permits one retry against the pinned default metadata source after an I/O failure; the pinned fallback endpoint is the same as the latest-release endpoint, and custom URLs do not fall back. Authorization permits an operation but does not prove that the device accepted it.

Credentials are not echoed in setting diagnostics and PSKs remain masked unless explicitly revealed. Command-line secrets and requested text output may still be visible to process inspection. Unix key and backup files are owner-only; Windows files inherit directory ACLs, which must be protected. Backups and extracted targets are not overwritten. Failed/cancelled writes remove only files created by that operation.

Firmware hashes provide integrity checks, not authenticated publisher provenance. An EEPROM checksum does not authenticate a signer. RSA-PSS signature checks use local/vendor/trusted keys; .NET uses the BouncyCastle implementation for the upstream 94-byte salt size with 1024-bit keys. A serial connection or enrolled signer is not itself proof of a physical device identity. Unknown or unavailable firmware partition hashes are not inferred from an archive hash. A successful external flasher exit is not device verification.

## Platform notes and differences from upstream

Serial discovery has Windows USB/FTDIBUS, Linux sysfs, and macOS IOKit implementations. Changed-port recovery and device workflows are not verified on every OS. Windows x64 and Linux x64 are exercised application platforms; macOS ARM64 build/managed-suite execution does not constitute device acceptance. Android, desktop RNode BLE, and RNode BLE providers are not implemented or verified. See [remaining capabilities](../TODO.md).

Native operations require explicit platform/product/model/hardware revision rather than upstream's interactive menus. USB identity tracking replaces pyserial discovery. Firmware tools must be independently installed and selected; embedded downloaded esptool execution is not reproduced. Native output layouts and some operational error statuses differ from Python. The upstream display-byte framing behavior, including unescaped `C0` and `DB`, is preserved. Read-only identification does not reset the board or issue a graceful-exit command. Pairing waits for a valid 32-bit PIN response and prints its six digits; it does not claim successful pairing. Ordinary settings do not reconnect automatically. The native parser rejects ambiguous/conflicting settings rather than inheriting upstream's early-exit option precedence.

## Physical device status

A read-only physical query identified a RAK4631/SX1262 on nRF52, firmware 1.86, in Normal mode; its EEPROM checksum is valid and signer remains unverified. A 200-byte native EEPROM backup is available. No physical setting mutation, firmware flash, RF transmission, or recovery has been verified. This is limited device evidence, not general hardware support.

## Verification

`tests/DotReticulum.Applications.Tests/RnodeconfTests.cs` and `RnodeconfFirmwareTests.cs` cover reference command bytes, parsers, authorization, simulator workflows, firmware/cache failures, signing, recovery planning, and process behavior. The capture adapter replaces sleeps and, for read-command capture, EEPROM parsing and response delivery. Reference vectors establish serialization and planning, not unmodified upstream serial or physical operation. Physical acceptance of flash/reset/re-enumeration, EEPROM persistence, RF legality/operation, and hardware recovery remains unverified.