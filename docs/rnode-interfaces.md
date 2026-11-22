# RNode interfaces

To use Reticulum over LoRa, you can use an [RNode](https://unsigned.io/rnode/) radio. The `RNodeInterface` controls one radio through the RNode serial protocol and gives you full control over the LoRa parameters. For RNodes with several transceivers, `RNodeMultiInterface` controls one device exposing multiple virtual radio ports and publishes each radio as an independently routable child interface.

A single radio can be reached over a serial port, over TCP for WiFi-capable devices, or over Bluetooth Low Energy on Windows. The TCP and BLE paths carry the RNode's own control and data protocol. They are not a Reticulum TCP bearer or a direct BLE Reticulum protocol.

Radio spectrum is legally regulated, and the rules vary around the world. It is your responsibility to know and follow the regulations that apply where you operate.

## RNode LoRa interface

```
[[RNode LoRa Interface]]
  type = RNodeInterface
  enabled = yes

  # Serial port for the device
  port = /dev/ttyUSB0

  # Or connect to a WiFi-capable RNode
  # by IP address or hostname.
  # port = tcp://10.0.0.1

  # On Windows, you can connect to a paired
  # BLE RNode by name, by address, or to the
  # first paired device.
  # port = ble://RNode 3B87
  # port = ble://F4:12:73:29:4E:89
  # port = ble://

  # Frequency in Hz (867.2 MHz)
  frequency = 867200000

  # LoRa bandwidth in Hz (125 kHz)
  bandwidth = 125000

  # TX power in dBm
  txpower = 7

  # Spreading factor
  spreadingfactor = 8

  # Coding rate
  codingrate = 5

  # Optional station identification.
  # id_callsign = MYCALL-0
  # id_interval = 600

  # Optional packet flow control,
  # disabled by default.
  # flow_control = false

  # Optional airtime limits, in percent.
  # airtime_limit_long = 1.5
  # airtime_limit_short = 33
```

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | required | Serial port, `tcp://host`, or `ble://selector` on Windows |
| `frequency` | required | Frequency in Hz, 137 MHz to 3 GHz |
| `bandwidth` | required | Bandwidth in Hz, 7800 to 1,625,000 |
| `txpower` | required | Transmit power in dBm, 0 to 37 |
| `spreadingfactor` | required | 5 to 12 |
| `codingrate` | required | 5 to 8 |
| `flow_control` | `false` | Wait for READY from the radio before each transmission |
| `airtime_limit_short`, `airtime_limit_long` | none | Airtime limits in percent, 0 to 100 |
| `id_callsign`, `id_interval` | none | Station identification text and interval in seconds |
| `bitrate` | derived | Generic bitrate estimate. Values of at least 5 bit/s override the radio-derived value. |

A missing radio parameter is a configuration error. The device firmware still applies its own validation. `tcp://` uses device port 7633. Serial ports run at 115200 baud, 8N1, with no handshake, and DTR and RTS asserted as in pyserial. The default IFAC size is 8 bytes; IFAC is applied by the interface manager.

## RNode Multi interface

```
[[RNode Multi Interface]]
  type = RNodeMultiInterface
  enabled = yes

  # Serial port for the device
  port = /dev/ttyACM0

  # Optional station identification,
  # sent on each online radio.
  # id_callsign = MYCALL-0
  # id_interval = 600

  [[[High Datarate]]]
    interface_enabled = yes

    # The virtual port. Only the manufacturer or
    # board configuration can tell you which
    # physical radio this is.
    vport = 1

    frequency = 2400000000
    bandwidth = 1625000
    txpower = 0
    spreadingfactor = 5
    codingrate = 5

  [[[Low Datarate]]]
    interface_enabled = yes
    vport = 0
    frequency = 865600000
    bandwidth = 125000
    txpower = 0
    spreadingfactor = 7
    codingrate = 5
```

Each radio section requires `vport` (0–10), `frequency`, `bandwidth`, `txpower`, `spreadingfactor` and `codingrate`. It also accepts `interface_enabled` (default `no`), `outgoing` (default `yes`), `flow_control` (default `false`) and the two airtime limits. Multi radios accept transmit power from −9 to 37 dBm. The detected chip family further limits frequency: SX127x and SX126x up to 1 GHz, SX128x from 2.2 to 2.6 GHz.

Radio enablement follows the pinned source:

- Parent `enabled = yes` enables all radios.
- Parent `interface_enabled = yes` enables the parent, but not every radio.
- A radio is enabled by its own `interface_enabled = yes`.
- Nested `enabled` is not an alias for `interface_enabled`. The upstream manual's Multi example uses `enabled` inside radio sections; the native runtime rejects that key there.

Enabled virtual ports must be unique and must be reported as present by the firmware. As in the source, `outgoing = no` is not a low-level send prohibition.

## Behaviour

### Framing

Frames are delimited by `0xc0`. In the payload, `0xdb` is sent as `db dd` and `0xc0` as `db dc`. The command byte is not escaped. Hardware MTU is 508 bytes. Multi selects a virtual port with a separate index command immediately before each data or control frame. The pinned Multi reader delivers DATA for the selected radio; this is not a general KISS port-nibble receive format.

### Detection and startup

Detection sends `08/73`, then queries version (`50/00`), platform (`48/00`) and MCU (`49/00`). Multi also queries interfaces with `71/00`. A `08/46` reply identifies a device. Firmware must be at least 1.52 for a single radio and 1.74 for Multi; newer major versions are accepted.

Startup sends frequency and bandwidth as big-endian unsigned values, then power, spreading factor, coding rate, optional airtime limits and radio-on. The echoed configuration is checked: frequency within 100 Hz, and bandwidth, power, spreading factor and state exactly. The coding-rate echo must arrive so the bitrate can be reported reliably, but its value is not compared. Upstream does not wait for it. Startup clears configuration snapshots from detection and waits for radio-on, not an intermediate radio-off report, within the response deadline. Live reconfiguration uses the same rule. A radio that never acknowledges radio-on fails explicitly rather than appearing connected.

Multi recognizes SX127x, SX126x and SX128x families. Unknown chip IDs fall back to SX127x classification as in the source; this is not a claim that unknown devices are supported.

### Flow control and queues

Each radio starts with one transmit credit. A READY from the firmware releases that radio's next queued packet, and a global READY releases each radio's next packet independently. No timeout invents credit. Queues are bounded per radio, so a congested radio does not block its siblings or firmware control processing.

### Errors and reconnection

Device reconnection re-runs detection and configuration without replacing child identities. A partially written frame is not replayed. ESP32 reset command `55/F8` triggers a reconnect when online. Single-radio errors 05 and 06 are kept as nonfatal diagnostics. Errors 01 and 02, and unknown fatal errors, disconnect. Hardware error history is bounded.

### Telemetry

Reported PHY values include RSSI, signed SNR and spreading-factor-dependent quality. The bitrate is derived from spreading factor, coding rate and bandwidth. Airtime and channel load, timing and CSMA, battery and temperature are exposed where the device reports them. Single and Multi PHY telemetry frames differ in length (12 and 10 bytes). Identification text is limited to 32 UTF-8 bytes and is sent only after ordinary outbound activity has begun.

### TCP device transport

The single-radio TCP device transport uses port 7633, a five-second connect and detection timeout, a 1.5-second partial-frame timeout, and activity keepalives after 3.5 seconds without writes. On Linux, keepalive is 5 s, 2 s and 12 with a 24-second TCP user timeout. On macOS, the keepalive idle time is five seconds.

### Library API

`RNodePacketInterface` and `RNodeMultiPacketInterface` accept an owned stream, a reopenable stream factory or a serial port. A single radio also accepts `tcp://host` and, on Windows, `ble://selector`. Constructors validate options but do not open devices. `StartAsync` performs detection and configuration. Multi publishes `RNodeSubinterface` children that are already started; the manager must not start them again. Removing a Multi child does not dispose the shared device or its siblings.

Native operations include firmware and device refresh, radio configuration and state, framebuffer and display enable, read and write, image display, cancellable display updates, battery state, hard reset, and disposal with reconnection. Display writes do nothing when the device reports no display. Image transfers are capped to addressable lines and response waits are bounded. No EEPROM writes, firmware updates, trust changes or firmware installation are performed.

## Windows BLE

On Windows, a single RNode can be reached over Bluetooth Low Energy. Pair the RNode in Windows settings first. The provider uses Windows SetupAPI and Bluetooth GATT directly through static native imports. It needs no Python process, reflection-based WinRT activation, extra driver or caller-supplied stream. It does not pair devices, change trust, update firmware or configure the host adapter.

### Selecting a device

| Selector | Meaning |
| --- | --- |
| `ble://` | First matching paired Nordic UART device |
| `ble://RNode Example` | Exact, case-sensitive device name |
| `ble://AA:BB:CC:DD:EE:FF` | Device address. A selector of 17 characters with six colon-separated parts is treated as an address. |

Address comparison is case-sensitive, matching the pinned source, and Windows renders addresses in uppercase. The source does not check for hexadecimal characters when parsing a selector. Its apparent preference for names starting with `RNode ` falls through to accepting any bonded UART device, so use an explicit selector to avoid connecting to an unintended device.

Discovery uses the cached paired PnP/GATT inventory, not live advertisement scanning. Unlike upstream's scanner, an offline first match can fail startup instead of selecting another device that is currently advertising. This discovery contract is partial.

### GATT transport

The service UUID is `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`. Writes go to RX `6E400002-...` without response, and incoming data arrives as notifications on TX `6E400003-...`. Writes are serialized and split into chunks. Windows' desktop GATT API does not expose the negotiated MTU, so the provider uses a conservative **20-byte payload**, valid at the mandatory ATT MTU of 23. It does not use upstream's negotiated maximum-size optimization.

Incoming bytes go to a 64 KiB ring buffer by default (`RNodeOptions.Ble.ReceiveBufferBytes`, 512 bytes to 1 MiB). An oversized notification, overflow, write failure or reported disconnection fails the stream rather than dropping bytes or replaying an ambiguously transmitted frame. The RNode owner then closes the GATT subscription and handles, reopens through the same selector, and repeats detection and configuration according to `Reconnect` and `ReconnectDelay`. Windows connection flags are polled where the driver exposes them. Where it does not, an idle disconnect is only detected by the next failed GATT operation, and idle disappearance detection on such drivers is unverified.

### Timeouts and cancellation

`RNodeOptions.Ble.ConnectTimeout` defaults to five seconds. `Ble.PartialFrameTimeout` defaults to 1250 ms, matching the pinned BLE receive timeout rather than serial's 100 ms. Cancellation is checked before and after each synchronous Windows GATT call. Windows cannot cancel these calls, so a call already in progress is joined and may run until the OS ATT timeout of 30 seconds. The connect timeout is therefore a cancellation deadline, **not** a hard bound on a native transaction in progress. Reads and queued writes remain cancellation-aware, and disposal waits for owned in-flight writes before releasing handles.

### Other platforms

Linux and macOS BLE providers are not implemented and reject `ble://` before any serial fallback. Android BLE is a separate adapter that is not implemented.

The Windows provider is a bounded software implementation, **not physical BLE or RF acceptance**. The development host has no Bluetooth adapter, so device discovery, GATT notification and write delivery, pairing, driver-specific disconnect flags and physical reconnection have not been verified with a real device.

## Differences from upstream

- The native parser rejects malformed escapes and bounds partial frames and queues.
- Missing configuration echoes and invalid firmware fail startup rather than enabling an unvalidated radio.
- Multi requires enabled virtual ports to be unique and supported by the firmware.
- Response deadlines and cancellation are explicit.
- Native desktop validation allows bandwidths up to 1.625 MHz. The pinned Android validator is narrower and is not implemented here.
- Windows BLE uses cached paired-device discovery and a fixed 20-byte write payload, as described above.

## Platform notes

Serial and TCP device simulators run on Windows x64 and Linux x64.

One authorized Windows RAK4631/SX1262 with firmware 1.86 passed managed receive-only startup, echoed live spreading-factor and coding-rate reconfiguration, acknowledged radio-off, and serial reopen. The published Windows native daemon also started and shut down cleanly twice with `outgoing=no`, and all 200 EEPROM bytes were unchanged. This is bounded device-control evidence only. It is not RF transmission, over-air reception, unplug and replug, or other boards, settings or platforms.

Physical radio and BLE testing beyond that is outside the current roadmap and remains unverified. Android Bluetooth and BLE adapters and Android permission flows are not implemented, and the pinned Android implementation does not support Multi. See [interface platforms](interface-platforms.md) and [TODO](../TODO.md).

## Verification

- `Parity6RNodeTests` and `GenerateParity6RNodeVectors.py` exercise pinned detection, configuration, radio framing, READY flow control, display and control methods, and bitrate observations with simulated devices.
- `Parity6RNodeHardwareTests` is opt-in. `DOTRETICULUM_RNODE_PORT` alone authorizes only read-only detection. Receive-only startup and reconfiguration also require `DOTRETICULUM_RNODE_RECEIVE_ONLY=1` and an explicit frequency in hertz in `DOTRETICULUM_RNODE_FREQUENCY`. That test enables reception and changes volatile settings, but never calls `SendAsync` or writes EEPROM. A configured 0 dBm is not zero RF power, and these tests neither measure RF emissions nor establish peer receipt.
- `RNodeBleTests`, `RNodeBleLifecycleTests` and `GenerateRNodeBleVectors.py` cover source-derived selectors, Windows paired-filter precedence, bounded notifications, write chunking and serialization, cancellation and cleanup, ambiguous-write failure, and simulated owner reconnection and redetection. The generator executes the original selector AST and `BLEConnection.find_target_device`/`device_bonded` at `a29bfb51c468945c96a1ee22cae0114e993e2391`, using only a recording scanner and paired-address query. It never opens Bluetooth. The exact source SHA-256 is kept in `Fixtures/rnode-ble.json`. The relevant upstream source is `RNS/Interfaces/RNodeInterface.py:174–186,385–400,1257–1450`.

Windows native APIs follow Microsoft's [Bluetooth device reference](https://learn.microsoft.com/windows/win32/api/_bltooth/) and [BluetoothGATTRegisterEvent](https://learn.microsoft.com/windows/win32/api/bluetoothleapis/nf-bluetoothleapis-bluetoothgattregisterevent) contracts. ABI layouts use the Windows SDK headers `bthledef.h` and `bluetoothleapis.h`.

Simulator tests and reference vectors do not establish physical radio interoperability.
