# RNode interfaces

The RNode interface controls a Reticulum-compatible LoRa radio through its serial protocol. `RNodeMultiInterface` controls one device exposing multiple virtual radio ports and publishes each radio as an independently routable child. The native implementation also supports the single-radio TCP device adapter at the upstream device port; this is RNode control/data over TCP, not a TCP Reticulum bearer.

## Configuration

A single radio uses `type = RNodeInterface` and requires `port`, `frequency`, `bandwidth`, `txpower`, `spreadingfactor`, and `codingrate`. Optional keys are `flow_control` (false), `airtime_limit_short`, `airtime_limit_long`, `id_callsign`, and `id_interval`. A missing required radio value is invalid. The port can be a serial name or `tcp://host` for the single-radio device protocol (default TCP port 7633).

Multi uses `type = RNodeMultiInterface`, `port`, optional parent `id_callsign`/`id_interval`, and nested radio sections. Each child requires `vport`, `frequency`, `bandwidth`, `txpower`, `spreadingfactor`, and `codingrate`; `interface_enabled` defaults false and `outgoing` defaults true. Child `flow_control` defaults false and airtime limits are optional. Parent `enabled = yes` enables all nested radios; `interface_enabled = yes` enables the parent but does not enable every radio. Nested `enabled` is not an alias for `interface_enabled`.

Generic `bitrate` values of at least 5 bit/s set the configured estimate. IFAC defaults to 8 bytes and is applied by the manager. Serial defaults are 115200 baud, 8N1, no handshake; DTR/RTS are asserted as in pyserial.

## Radio limits and wire behaviour

Frequency is 137 MHz–3 GHz for single radios; detected Multi chip families further constrain the supported band (SX127x/SX126x up to 1 GHz, SX128x 2.2–2.6 GHz). Bandwidth is 7800–1,625,000 Hz, spreading factor 5–12, and coding rate 5–8. Single-radio power is 0–37 dBm; Multi is −9–37 dBm. Optional airtime limits are finite percentages from 0 to 100. Actual device capability and firmware validation still apply.

Frames use `0xc0` delimiters; `0xdb` becomes `db dd` and `0xc0` becomes `db dc`. The command byte is not escaped. Hardware MTU is 508 bytes. Multi selects its virtual port with a separate index command immediately before each data/control frame. The pinned Multi reader delivers DATA for the selected radio; it does not establish a general KISS port-nibble receive format.

Flow control starts with one transmit credit per child. A firmware READY response releases the next queued transmission for that radio. Native queues are bounded per radio; a congested child does not block sibling traffic or firmware control processing. No timeout invents radio credits. Device reconnect re-detects and configures the radio without replacing child identity; ambiguous partial writes are not replayed.

## Native controls

`RNodePacketInterface` and `RNodeMultiPacketInterface` accept an owned stream, a reopenable stream factory, or a serial port. A single radio also accepts `tcp://host`. Constructors validate options but do not open devices; `StartAsync` performs detection and configuration. Multi publishes already-started `RNodeSubinterface` children; the manager must not start them again.

Native controls include radio reconfiguration/state, device refresh, framebuffer/display operations, battery state, hard reset, and cancellation-aware display updates. Multi child removal does not dispose the shared device or siblings. No EEPROM writes, firmware updates, trust changes, or firmware installer operations are performed.

## Startup, control, and telemetry details

The serial protocol uses command/data frames delimited by `0xc0`; payload `0xdb` and `0xc0` bytes are escaped as `db dd` and `db dc`, while the command byte is not escaped. Detection sends `08/73`, then version `50/00`, platform `48/00`, and MCU `49/00` queries; Multi also queries interfaces with `71/00`. Reply `08/46` identifies a device. Required firmware is at least 1.52 for single and 1.74 for Multi (newer major versions are accepted). Startup sends frequency and bandwidth as big-endian unsigned values, power, SF, CR, optional airtime limits, and radio-on; echoes validate frequency within 100 Hz and compare bandwidth, power, SF, and state. CR echo is required for reliable bitrate reporting but is not compared, matching the documented native boundary.

Multi supports recognized SX127x, SX126x, and SX128x families and virtual ports 0–10. Child enablement is `child.interface_enabled || parent.enabled`; parent `interface_enabled` alone does not enable all children. Firmware discovery must report an enabled virtual port. Each child has independent transmit credit and receive queue. A global READY releases each child's next queued packet independently. No readiness timeout fabricates credit.

Reported PHY values include RSSI, signed SNR, and SF-dependent quality; bitrate derives from spreading factor, coding rate, and bandwidth. Airtime/channel load, timing/CSMA, battery, and temperature are exposed where the device reports them. Single and Multi PHY telemetry frame lengths differ (12 and 10 bytes). ESP32 reset command `55/F8` triggers reconnect when online. Optional callsign identification is limited to 32 UTF-8 bytes and begins only after ordinary outbound activity; Multi sends it on each online radio.

The single-radio TCP device adapter uses port 7633, a five-second connect/detection timeout, 1.5-second partial-frame timeout, and activity keepalives after 3.5 seconds without writes. Linux keepalive is 5/2/12 seconds with 24-second TCP user timeout; macOS uses a five-second keepalive idle period. This is a radio's device-control transport, not a Reticulum TCP client interface.

Native operations include firmware/device refresh, radio configuration/state, framebuffer/display enable/read/write, image display, cancellable display updates, battery state, hard reset, and disposal/reconnect. Display writes are no-ops when the device reports no display capability; reads remain explicit requests. Image transfers are capped to addressable lines, and response waits are bounded. These APIs do not perform firmware updates or EEPROM writes.

Unknown Multi chip IDs follow the source's SX127x classification fallback; this fallback is not a claim that an unknown device is supported. Single-radio frequency echo may differ by at most 100 Hz. Python does not validate CR echo; the native implementation waits for it to report bitrate consistently but does not compare the returned value. Single radio errors 05/06 are retained as nonfatal diagnostics; errors 01/02 and unknown fatal errors disconnect. Hardware error history is bounded in .NET.
## Platform notes

Windows x64 and Linux x64 software suites exercise serial simulators and the TCP device simulator. A read-only hardware identification path has confirmed an authorized RAK device responds to detection queries with DTR asserted; this does not verify radio configuration, RF transmission, over-air reception, or unplug/replug behavior. Android BLE/Bluetooth adapters and Android-specific permission flows are not implemented. Multi is unsupported by the pinned Android implementation; BLE, other serial drivers, and untested platforms remain unverified. See [interface platforms](interface-platforms.md) and [TODO](../TODO.md).

## Differences from upstream

The native parser rejects malformed escapes and bounds partial frames and queues. Missing configuration echoes and invalid firmware fail startup rather than enabling an unvalidated radio. The native Multi interface requires enabled virtual ports to be unique and supported by firmware. It preserves source-specific parent/child selection behavior; `outgoing = no` is not a low-level send prohibition. Response deadlines and cancellation are explicit. Native desktop validation permits bandwidths up to 1.625 MHz; the pinned Android validator is narrower and is not implemented here.

## Verification

`Parity6RNodeTests` and `GenerateParity6RNodeVectors.py` exercise pinned detection, configuration, radio framing, READY flow control, display/control methods, and bitrate observations with simulated devices. `Parity6RNodeHardwareTests` is opt-in and requires an explicitly configured device port; read-only detection is not RF acceptance. Simulator tests and reference vectors do not establish physical radio interoperability.
