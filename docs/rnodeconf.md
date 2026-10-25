# Native RNode maintenance

`rnodeconf` is an experimental native implementation of upstream
RNode Config Utility **2.5.0** in RNS **1.5.7**, commit
`a29bfb51c468945c96a1ee22cae0114e993e2391`. Its device/firmware operations are
simulator-tested, not a claim of physical utility parity. Do not use it concurrently with a daemon
that owns the serial port.

```powershell
dotnet run --project src\DotReticulum.Applications -- rnodeconf --help
# Stop the owning daemon before an independently authorized device query:
dotnet run --project src\DotReticulum.Applications -- rnodeconf COM_PORT --info
```

No COM8 device query or mutation was performed for this implementation.
The only available radio was previously identified as RNode 1.86 / nRF52;
that earlier interface evidence does not verify this utility. No second radio
or firmware installation target was available.

## Source inventory and acceptance boundaries

All citations below refer to `RNS/Utilities/rnodeconf.py` at the pin above,
SHA-256 `f8b43da8b25802dada7e437011f6282a25697365db94511d7451c1b64e993d0f`.
`tests/DotReticulum.Applications.Tests/GenerateRnodeconfVectors.py` executes
the real upstream `RNode` methods with a capture-only serial adapter. Its
30 cases inventory exact maintenance and destructive-operation command bytes.
Those vectors establish serialization only, not physical execution or device
support. The native CLI's conservative “partial implementation” version/help
branding refers to unverified hardware/platform acceptance and intentional
workflow differences, not an absent firmware/manufacturing operation family.

| Contract / source | Native status and measurable acceptance |
| --- | --- |
| CLI ledger, lines 1480–1547 | All pinned operation/option families below have native implementations. Long `--option=value`, attached short values, grouped short flags, repeated last-value options and `--` positional delimiter are supported. Independent vectors execute the actual upstream argparse construction. Syntax/unknown-option errors return argparse-style 2; operational failures return 1 instead of several upstream success-shaped error exits. Help/version remain native-branded, not an upstream executable. |
| Serial open, lines 1418–1433; `RNode.detect`, 715–719 | Reuses the production native serial factory at 115200/8N1. Opening sends nothing. Identification transmits exact combined detect/version/platform/MCU/board/device-hash/hash requests; simulator must answer detect, version, platform, MCU and board. Three-second bounded deadline, cancellation, EOF and hardware errors fail clearly; no radio startup. `--list-ports` enumerates without opening ports. `--usb-serial ID` selects exactly one port and tracks changed ports after reset/tool completion. Native discovery uses Windows USB/FTDIBUS registry identity, Linux sysfs and macOS IOKit; real changed-port recovery/platform execution remains unverified. |
| Settings, lines 728–907, 3822–3990 | `--display/-D`, `--timeout/-t`, `--rotation/-R`, `--np`, `--display-addr`, `--recondition-display`, `--ia-enable/-x`, `--ia-disable/-X`, Bluetooth `-b/-B/-p`, Wi-Fi `-w`, channel, SSID, PSK, IP and netmask serialized against executed upstream vectors. Pairing waits boundedly for the actual 32-bit PIN response and reports its six digits, rejects invalid PINs and timeouts. Brightness/timeout/rotation clamp as upstream; UTF-8 byte limits, channel and IPv4 validation precede opening the port. No actual setting, pairing, display or Wi-Fi outcome verified. |
| Normal/TNC, lines 907–979, 4445–4495 | `--normal/-N`; `--tnc/-T` with all five explicit `--freq/--bw/--txp/--sf/--cr` parameters. Exact initialization/save commands; ESP32 adds reset. Zero TX power is accepted without upstream's accidental truthiness prompt. TNC enables RF and therefore requires explicit device-write authorization; **never exercised physically**. Hardware/model/regulatory acceptance is not claimed. |
| EEPROM read and provisioning, lines 1008–1082 | Read-command vector; bounded response, lock byte and MD5 metadata checksum validation before settings. A checksum alone is not signer authentication; the independent RSA-PSS check is described below. Simulator rejects invalid checksum before a setting command. |
| Detailed information, lines 3982–4035 | `--info/-i` prints product/model/board, EEPROM signer status, firmware/hardware revision, serial, modem, frequency/power limits, manufacture date, stored normal/TNC mode and TNC parameters/bitrate from actual source-backed model tables and EEPROM offsets. Unknown models are reported rather than invented. |
| Configuration query, lines 3700–3801 | `--config/-c` reads EEPROM plus configuration sector, reports Bluetooth/Wi-Fi/channel/display address/brightness/blanking/rotation, configured NeoPixel intensity, IA and Wi-Fi credentials, with PSK masked unless `--show-psk`. AP address defaults and DHCP decoded. Native text layout differs; not byte-for-byte output parity. |
| EEPROM export, lines 3803–3820 | `--eeprom-dump` gives raw lowercase hex; `--eeprom-backup` saves exact downloaded bytes. `--backup-file PATH` and `--state-dir PATH` are native extensions. No overwrite; owner-only Unix creation. Default directory `~/.config/rnodeconf/eeprom`; timestamped filename. Upstream's missing path separator bug is not reproduced. Failed/cancelled writes remove only the newly created file. |
| Trust import, lines 1599–1619 | Explicit `--trust-key DER_HEX --authorize-trust`; RSA SubjectPublicKeyInfo DER saved as `trusted_keys/<SHA256>.pubkey` under the state directory. No overwrite; reject malformed/trailing data. Narrower than upstream's generic DER loader. Later info/update queries check enrolled signer keys; enrollment alone proves no physical device identity. |
| Device signature checking, lines 1080–1216 | Local/vendor/trusted RSA-PSS SHA256 MAX_LENGTH verification and signer attribution. Native .NET's fixed PSS salt size does not match this contract; the existing BouncyCastle dependency supplies exact PSS parameters (94-byte salt for 1024-bit keys). Independently generated public EEPROM vector is accepted by the actual upstream parser; no fixture private key persisted. EEPROM signer identity is distinct from authenticating firmware downloads or a serial connection. |
| Firmware acquisition/cache, lines 1233–1416 | Latest/specific/custom release metadata, bounded downloads, cached `.version` metadata and constant-time SHA-256 integrity validation. `--fw-version`, `--fw-url`, `--nocheck`, `--clear-cache/-C`, `--use-extracted/-E`. Explicit download/cache authorization; missing cache, invalid metadata/hash, corrupt downloads and unsafe filenames fail clearly. No actual network firmware was fetched for tests. Upstream hash metadata is not authenticated firmware provenance. `--authorize-firmware-fallback` explicitly permits exactly one fallback metadata request after default-server I/O failure; the pinned fallback endpoint equals its latest endpoint (not an invented alternate publisher). Custom URLs never fall back. |
| Extraction/installation/update, lines 1620–3700 | `--extract/-e` emits the five actual ESP32 read-flash parts and checks output lengths. `--autoinstall/-a` and `--flash/-f` require explicit platform/product/model/hwrev instead of interactive model menus. `--update/-u` skips equal/older versions; `--force-update/-U` allows reinstall/downgrade. All 45 source model mappings and 126 version/platform/variant flasher plans were captured by executing upstream's original planner. AVR avrdude, ESP32 esptool and NRF52 DFU argument contracts use an explicitly selected independently installed `--flasher` executable. Downloaded Python scripts are never executed. Archive expansion is bounded and rejects traversal, links and duplicate entries. Update closes the port before tool execution, reopens and checks reported version and provisioned EEPROM afterward. Simulator covers complete update/reconnect and extraction flows; no real flasher ran. |
| Manufacturing/signing/EEPROM mutation, lines 981–1006 and 3990–4445 | `--eeprom-wipe`; `--rom/-r` bootstraps byte-exact product/model/hwrev/serial/timestamp/MD5/RSA-PSS EEPROM, lock written last; serial counter exclusively owned and reserved before device writes, read-back checksum/signature verified. `--key/-k` stores native RNS identity private bytes plus RSA1024 PKCS8, no overwrite; `--public/-P` prints public parts only. `--sign/-S` signs reported device hash with native identity and writes the Ed25519 signature. `--firmware-hash/-H`, hidden `-K/-L` set/query 32-byte firmware hashes. Source pacing (6ms EEPROM byte writes, NRF52 completion/wipe delays) is explicit and replaceable only by test adapters. Physical destructive-operation evidence remains deferred. |

## Safety differences

Every setting/RF command requires `--authorize-device-write`; local trust
enrollment requires `--authorize-trust`. These are deliberate native safety
gates, not upstream flags. Authorization never implies that a command's result
was physically verified. The success message only says commands were sent.
Read/export and settings cannot be combined; all setting values are validated
before port access, preventing partial updates from later parse failures.

Credentials are never echoed in setting diagnostics. Configuration PSKs are
masked by default; command/response byte buffers are cleared after use.
Command-line strings and explicitly requested text output remain managed
strings and can be visible to process inspection. Windows file ACLs are
inherited: protect backup/trust directories appropriately.

Advanced operations add independent `--authorize-eeprom`, `--authorize-flash`,
`--authorize-key-write`, `--authorize-cache-write` and
`--authorize-firmware-download` gates. Updating/extracting an unverified device
additionally requires `--authorize-unverified-device`. Plain authorization
does not select an arbitrary tool: `--flasher` must be a fully qualified,
existing independently installed executable. The native tool runner uses
argument arrays (never a shell), drains output without retaining unbounded
logs, owns process-tree cancellation and has a ten-minute deadline. HTTP
responses are bounded to 1 MiB metadata/64 MiB firmware and three minutes.
Archives expand to at most 128 MiB/128 entries.

Installer tools are external dependencies, not native implementations of
AVR/ESP bootloader or Nordic DFU protocols. Only captured tools were used by
tests. A successful tool exit alone is not reported as verified device support.
Extraction resets a device even though it reads flash and therefore requires
device-write/flash authorization too. Existing extraction files are not
overwritten. Model/platform selection and regulatory suitability remain the
operator's responsibility. Interactive upstream USB enumeration/model menus and
embedded recovery-esptool execution have explicit native equivalents rather
than being silently treated as successful. Native USB-serial discovery
replaces pyserial port discovery: `--usb-serial ID`/`--list-ports` plus
`--list-models` and explicit platform/product/model/hwrev provide noninteractive
equivalents of board selection. After a reset, the same USB identity must
reappear uniquely within a bounded 20-second retry window. If the device
exposes no stable serial, the original explicit port is retried; a moved
serial-less device must be explicitly reselected. Embedded recovery-esptool
execution is intentionally replaced by an independently installed explicit
native esptool executable receiving the same arguments.

Installation and standalone EEPROM manufacturing re-probe the device, verify
the selected firmware version when installing, provision its firmware hash
when available, reset, reopen by USB identity, verify EEPROM checksum/signature,
and save a non-overwriting `firmware/device_db/<serial-hex>` backup. A reserved
serial number is never reused after interruption. Extraction rejects existing
targets and rolls back only files created by the failed extraction, permitting
retry. Reuse of extracted firmware preserves upstream's recorded device hash
semantics rather than misrepresenting a SHA-256 of a padded flash dump as an
authenticated firmware image.

Whenever a selected firmware partition hash is available, both device-reported
**target and actual** firmware hashes are freshly queried after restart and
compared in constant time. A mismatch is an error, never an update/install
success. Unknown/unavailable partition hashes (notably upstream AVR paths) are
not fabricated from the archive hash and do not establish firmware-image
verification. Tests include an actual-hash mismatch despite matching version
and valid EEPROM, plus missing/ambiguous USB recovery and interrupted EEPROM
writes that reserve a serial and successfully retry using a new one.

Native operation isolation deliberately rejects upstream combinations whose
early-exit precedence would silently ignore later requested mutations: read
hashes cannot combine with writes; local-key/install/extract/cache operations
are exclusive; export/config and settings are separate; display-address reset
is isolated. Both normal and TNC or contradictory IA/Bluetooth flags fail before
opening a port rather than guessing which operation the operator intended.

The reference's single-byte display commands do **not** escape `C0` or `DB`.
The native command serializer preserves this observable behavior (including
the resulting framing hazard); independent vectors cover both values.
Read-only identification deliberately does not send upstream's Windows
`graceful_exit` Leave command, reset the board, or automatically reconfigure RF.
Pairing is noninteractive, prints only a valid received PIN and does not claim
successful pairing. Ordinary setting operations do not reconnect automatically;
explicit firmware/reset recovery tracks the USB identity as described below.

## Evidence classification

Reference generators verify actual RNS/LXMF imports with
`tests/Interop/reference_environment.py` (RNS pin above, LXMF 1.2.1).
The capture adapter replaces sleeps and, only for read-command capture,
EEPROM parsing and response delivery. This is wire serialization evidence,
**not** unmodified upstream serial/hardware interoperability.

Focused tests cover reference bytes, pre-open authorization/validation,
simulated identification/configuration/settings/TNC, checksum rejection,
timeout/cancellation/error/truncation, exact non-overwriting backups and trust
persistence, RSA-PSS reference verification, key restart/no-overwrite,
manufacturing read-back and serial counter, firmware HTTP/cache/hash failures,
archive traversal, exact flasher plans, nonzero tool failure,
update/reconnect/version verification, full autoinstall/reset/backup,
USB-identity changed-port selection, extracted offline reuse and five-part
extraction rollback/retry. An actual owned `dotnet` subprocess runs the utility
version path, a failing utility path and a bounded cancellable no-interface
daemon to exercise the external-tool process owner without executing firmware.
`GenerateRnodeconfFirmwareVectors.py` executes the original AST-extracted
upstream flasher function with executable/file probes captured; it generates
the checked-in native reference-data table and test fixture. No command planner
is independently reimplemented in Python. `GenerateRnodeconfSigningVectors.py`
uses ephemeral Python RSA signing and executes actual upstream EEPROM parsing,
with only filesystem discovery and the public capture-only signer supplied.
Pester exercises separate native/managed CLI processes for safety
rejections and placeholder startup. Parent integration records commands/results,
NativeAOT feature execution and any separately authorized physical query.

Integration evidence reported by the coordinating session: focused utilities
suite **119 passed, 4 skipped**, including firmware/signing/process/device tests;
`win-x64` NativeAOT publish passed with linker/compiler warnings treated as
errors; the published native device/placeholder Pester suite **13 passed**.
The native process flows exercise authorization rejection, model listing,
non-overwriting signing-key persistence/public export and placeholder runtime
initialization. They do not execute a real flash tool, write a physical EEPROM
or establish RF/device acceptance. Exact full-gate commands and results belong
in the development history maintained by the coordinating session.

Full physical `rnodeconf` acceptance remains open; simulator success cannot
verify flash/reset/re-enumeration/EEPROM persistence, RF legality/operation,
Bluetooth/Wi-Fi/display behavior or recovery after interrupted physical writes.
