# rnodeconf: RNode maintenance

The `rnodeconf` utility inspects, configures and maintains RNode devices. You can use it to read device information, change device settings, back up and manage the EEPROM, download and install firmware, and run manufacturing and signing workflows. It targets the upstream RNode Config Utility 2.5.0 in RNS 1.5.7, with partial parity.

Simulator tests and source-derived command evidence do not imply physical-device parity. Before opening a device, stop any daemon that owns its serial port.

## Usage

List serial ports and known models:

```sh
rnodeconf --list-ports
rnodeconf --list-models
```

Show device information, or back up its EEPROM:

```sh
rnodeconf <serial-port> --info
rnodeconf <serial-port> --eeprom-backup --backup-file <path>
```

The command takes one serial port plus operation options. From a source checkout, use `dotnet run --project src/DotReticulum.Applications -- rnodeconf ...`.

## Command-line options

```text
rnodeconf — native RNode maintenance (experimental, partial RNS 1.5.7 parity)
Usage: rnodeconf PORT [--info | --config | --eeprom-dump | --eeprom-backup]
  --backup-file PATH                 Never overwrites an existing backup
  --normal | --tnc --freq HZ --bw HZ --txp DBM --sf N --cr N
  --display N --timeout N --rotation N --np N --display-addr HEX
  --recondition-display --ia-enable --ia-disable
  --bluetooth-on --bluetooth-off --bluetooth-pair
  --wifi OFF|AP|STATION --channel N --ssid TEXT --psk TEXT --ip IP --nm IP
  --authorize-device-write           Required for every device setting/RF change
  --trust-key DER_HEX --authorize-trust [--state-dir PATH]
  --show-psk                         Explicitly reveal configured PSK
  --key/-k --public/-P --sign/-S --rom/-r --eeprom-wipe --firmware-hash HEX
  --update/-u --force-update/-U --flash/-f --autoinstall/-a --extract/-e
  --fw-version VERSION --fw-url RELEASE_BASE --nocheck --use-extracted
  --platform HEX --product HEX --model HEX --hwrev N --flasher INSTALLED_EXE
  --list-models --list-ports --usb-serial USB_ID
  --authorize-key-write --authorize-cache-write --authorize-eeprom
  --authorize-flash --authorize-firmware-download --authorize-unverified-device
  --authorize-firmware-fallback       Retry default metadata source once on failure
Explicit independent installed flash tools only; downloaded scripts are NOT executed.
Hardware/RF acceptance is NOT verified by simulator tests. Stop the owning daemon.
```

The parser also accepts these short forms and options that the help block does not list:

| Short | Long |
|---|---|
| `-i` | `--info` |
| `-c` | `--config` |
| `-N`, `-T` | `--normal`, `--tnc` |
| `-b`, `-B`, `-p` | `--bluetooth-on`, `--bluetooth-off`, `--bluetooth-pair` |
| `-D`, `-t`, `-R` | `--display`, `--timeout`, `--rotation` |
| `-w` | `--wifi` |
| `-x`, `-X` | `--ia-enable`, `--ia-disable` |
| `-H` | `--firmware-hash` (set) |
| `-K`, `-L` | `--get-target-firmware-hash`, `--get-firmware-hash` |
| `-C` | `--clear-cache` |
| `-E` | `--use-extracted` |

## Device connection

The utility opens the serial port at 115200 baud, 8N1. Opening the port alone sends nothing. Identification sends detect, version, platform, MCU, board, device-hash and hash requests and requires a reply within three seconds. Read-only identification does not reset the board or send a graceful-exit command. Listing ports does not open any device.

`--usb-serial ID` selects a device by its USB identity and can follow it to a new port after a reset, with recovery bounded to 20 seconds. On Windows, discovery excludes disconnected registry entries and follows composite USB parents to get the physical serial number rather than a location-dependent interface identifier. If a device exposes no stable serial, the original port is retried and a device that moved must be selected again.

## Operations

### Inspecting a device

- `--info`/`-i` reports model, board, EEPROM signer status, firmware and hardware revision, serial, modem, RF limits, manufacture data, operating mode and TNC values.
- `--config`/`-c` reports Bluetooth, Wi-Fi, channel, display, IA and network settings. The PSK is masked unless you add `--show-psk`.
- `--eeprom-dump` prints the raw EEPROM as lowercase hex.
- `--eeprom-backup` saves the raw EEPROM bytes. `--backup-file PATH` chooses a path that is never overwritten; by default backups go to `~/.config/rnodeconf/eeprom` with timestamped filenames.

Non-ESP32 firmware can return a valid, empty Wi-Fi configuration sector. `--config` still reports the EEPROM-backed settings and says explicitly that the Wi-Fi sector is absent. An empty sector is not treated as a timeout or as a reason to invent SSID or IP defaults. Truncated nonempty sectors remain errors.

### Device settings

`--display`, `--timeout`, `--rotation`, `--np`, `--display-addr`, `--recondition-display`, `--ia-enable`, `--ia-disable`, the Bluetooth options, `--wifi`, `--channel`, `--ssid`, `--psk`, `--ip` and `--nm` configure device features. Values are validated before the port is opened: brightness, timeout and rotation use upstream clamping, and UTF-8 byte lengths, channel values and IP addresses are checked.

Pairing waits for a valid 32-bit PIN reply and prints its six digits. It does not claim that pairing succeeded. Ordinary settings do not reconnect automatically.

### Normal and TNC modes

`--normal`/`-N` selects normal mode. `--tnc`/`-T` requires explicit `--freq`, `--bw`, `--txp`, `--sf` and `--cr`, and enables RF.

### Trust, keys and EEPROM

- `--trust-key DER_HEX --authorize-trust` enrolls an RSA SubjectPublicKeyInfo DER key under `--state-dir`.
- `--eeprom-wipe`, `--rom`/`-r`, `--key`/`-k`, `--public`/`-P`, `--sign`/`-S` and `--firmware-hash`/`-H` perform manufacturing and key operations.
- `--key` stores native Reticulum private-key bytes and an RSA-1024 PKCS#8 key without overwriting. `--public` exports only public components. `--sign` signs the reported device hash with Ed25519.
- `-K` and `-L` query the target and actual firmware hashes; `-H` sets a firmware hash.

During manufacturing, a serial number is reserved before any device write and is never reused after an interruption. The EEPROM lock is written last, and the read-back checksum and signature are verified where applicable. Installation and standalone EEPROM manufacturing re-probe the device, verify the selected firmware version when installing, provision a firmware hash when available, reset, reopen the device by USB identity, verify the EEPROM and save a backup that never overwrites an existing one.

### Firmware

`--fw-version` selects a release, `--fw-url` selects a custom release base and `--nocheck` disables the upstream-style version check. `--clear-cache`/`-C`, `--use-extracted`/`-E`, `--extract`/`-e`, `--update`/`-u`, `--force-update`/`-U`, `--flash`/`-f` and `--autoinstall`/`-a` control the firmware workflows. `--platform`, `--product`, `--model` and `--hwrev` select a target explicitly, and `--flasher` selects an independently installed flashing tool.

Limits and safeguards:

- Firmware metadata responses are limited to 1 MiB and three minutes; firmware downloads to 64 MiB.
- Archive expansion is limited to 128 MiB and 128 entries. Path traversal, links and duplicate paths are rejected.
- Cache clearing and offline reuse of extracted firmware are explicit operations. Cached hashes are integrity data, not authenticated publisher provenance.
- Extract writes the source's five ESP32 flash components and validates their sizes.
- AVR avrdude, ESP32 esptool and NRF52 DFU remain external, installed tools. Downloaded scripts are never executed.
- Tool processes run from argument arrays without a shell, with bounded output, cancellation and a ten-minute deadline.

The native planner contains 45 source-backed model mappings and 126 target/version/flasher plans. These describe plans, not proof that devices accept them.

An update closes the port before running the tool, then reopens it and checks the reported version and EEPROM. When a selected partition hash is available, the target and actual device hashes are queried again after restart and compared in constant time; a mismatch is an error. If the port changes, recovery follows the USB identity when it is unique.

For nRF52 updates on a device with a discoverable USB serial identity, the utility requests DFU by opening and closing the port at 1200 baud, finds the bootloader port by that identity, and runs the installed flasher without touching the old port again. Windows may invalidate the serial handle during the reset itself; that I/O error is reported and accepted only if a different port with the same USB identity actually appears. After the application recovers, the firmware version, a valid EEPROM and any available target and actual firmware hashes are checked. Devices without a stable identity keep the upstream flasher's `-t 1200` behavior, and changed-port recovery is not inferred for them. The separate `--flash` and `--autoinstall` provisioning paths keep their existing plans.

## Authorization and security

Every write needs an explicit authorization flag. Authorization permits an operation; it does not prove that the device accepted it.

| Action | Required flag |
|---|---|
| Any device setting or RF change | `--authorize-device-write` |
| Reset operations such as extraction | device-write or flash authorization |
| Trust enrollment | `--authorize-trust` |
| EEPROM writes | `--authorize-eeprom` |
| Flashing | `--authorize-flash` |
| Key writes | `--authorize-key-write` |
| Cache writes | `--authorize-cache-write` |
| Firmware downloads | `--authorize-firmware-download` |
| Updating or extracting an unverified device | `--authorize-unverified-device`, in addition |

`--authorize-firmware-fallback` permits one retry against the pinned default metadata source after an I/O failure. The pinned fallback endpoint is the same as the latest-release endpoint, and custom URLs never fall back.

Secrets and files:

- Credentials are not echoed in setting diagnostics, and PSKs stay masked unless explicitly revealed. Secrets given on the command line, and text output you request, may still be visible to process inspection.
- Key and backup files are owner-only on Unix. On Windows they inherit the directory's ACLs, so protect that directory.
- Backups and extracted targets are never overwritten. Failed or cancelled writes remove only the files that operation created.

What the checks do and do not prove:

- Firmware hashes are integrity checks, not authenticated publisher provenance. Unknown or unavailable partition hashes are not inferred from an archive hash.
- An EEPROM checksum does not authenticate a signer.
- RSA-PSS signature checks use local, vendor or trusted keys. .NET uses the BouncyCastle implementation for upstream's 94-byte salt size with 1024-bit keys.
- A serial connection or an enrolled signer is not proof of a physical device's identity.
- A successful external flasher exit is not device verification.

## Platform notes

Serial discovery has Windows USB/FTDIBUS, Linux sysfs and macOS IOKit implementations. Changed-port recovery and device workflows are not verified on every OS. Windows x64 and Linux x64 are the exercised application platforms; macOS ARM64 build and managed-suite execution is not device acceptance. Android, desktop RNode BLE and RNode BLE providers are not implemented or verified. Other platforms are outside the current roadmap and remain unverified. See [remaining capabilities](../TODO.md).

## Differences from upstream

- Native operations need an explicit platform, product, model and hardware revision instead of upstream's interactive menus.
- USB identity tracking replaces pyserial discovery.
- Firmware tools must be installed independently and selected; upstream's embedded execution of a downloaded esptool is not reproduced.
- Output layouts and some operational error statuses differ from Python.
- The parser rejects ambiguous or conflicting settings rather than inheriting upstream's early-exit option precedence.
- Upstream's display-byte framing, including unescaped `C0` and `DB`, is preserved.

## Physical device status

One Windows x64 RAK4631/SX1262 running firmware 1.86 has passed native executable acceptance for:

- software-triggered USB DFU with same-version application reflashing
- bootloader and application re-enumeration without a button press
- EEPROM persistence: a reversible display-brightness change persisted across the flash, and all 200 EEPROM bytes were verified and restored to the test's starting state
- target and actual SHA-256 checks of the selected firmware

The EEPROM signer remains unverified. No LoRa radio startup, RF transmission, over-air reception, bootloader replacement, or other device or platform acceptance is implied. Further physical-hardware testing is outside the current roadmap, and other devices remain unverified. Port and USB details of the recorded run are in the development history.

The physical Pester case is opt-in through `tests\Invoke-PesterTests.ps1` with `-AuthorizeRNodeFirmware -RNodeUsbSerial <ID> -RNodeFirmwareVersion <installed-release> -RNodeFlasher <installed-executable>`. Add `-Application` to test the published native candidate. Stop other serial owners first. The test changes and restores brightness and reinstalls exactly the specified release. Normal CI explicitly skips this case.

## Verification

| Tests | What they show |
|---|---|
| `tests/DotReticulum.Applications.Tests/RnodeconfTests.cs`, `RnodeconfFirmwareTests.cs` | Reference command bytes, parsers, authorization, simulator workflows, firmware and cache failures, signing, recovery planning and process behavior. |
| `tests\Pester\Parity7Device.Tests.ps1` | The separately authorized physical case described above. |

The capture adapter replaces sleeps and, for read-command capture, EEPROM parsing and response delivery. Reference vectors establish serialization and planning, not unmodified upstream serial behavior or physical operation. RF legality and operation, and broader device and platform recovery, remain unverified.
