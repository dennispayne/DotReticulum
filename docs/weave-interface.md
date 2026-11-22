# Weave interface

The Weave interface connects Reticulum to a Weave device over a serial port. Weave devices speak WDCL, a device and endpoint-switch protocol; it is not Ethernet. Each remote endpoint the device reports becomes an independently routable child interface, and packets are sent through those children rather than broadcast by the parent.

## Example configuration

```
[[Weave Interface]]
  type = WeaveInterface
  enabled = yes
  port = /dev/ttyUSB0
```

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | required | Serial port for the device |
| `bitrate` | 250,000 bit/s | Generic bitrate estimate. This is not the serial baud rate. |

The section name becomes the interface name. There are no serial speed or flow-control settings, because the device always runs at 3,000,000 baud, 8N1, with no flow control. The default IFAC size is 16 bytes; IFAC is applied by the interface manager, not the serial framing layer. Hardware MTU is 1024 bytes.

## Behaviour

The parent is a spawning, reconnecting interface. Each child represents one eight-byte endpoint ID. A send on the parent itself does nothing; it is not a broadcast. When an endpoint expires, only its child is removed. If the device is lost, all its children end, and a reconnect can publish new ones. Failures in observers are reported without stopping processing.

- **Framing.** HDLC with `0x7e` delimiters and `0x7d` escaping with XOR `0x20`.
- **Discovery.** Discovery is broadcast. A signed connect response proves possession of the advertised Ed25519 key. It does not establish operator trust.
- **Data.** Outgoing packets carry an endpoint ID prefix, and incoming packets an endpoint ID suffix.
- **Duplicates.** Duplicate packets within 750 ms are suppressed using a 48-entry cache shared by the device.
- **Peer expiry.** Endpoints expire after 20 seconds, checked every 22 seconds.
- **Device reports.** The device reports endpoint liveness, path and via information, logs, display updates and CPU, task and memory telemetry.

### WDCL message details

WDCL uses big-endian fields and four-byte switch addresses, and defines message types 0–6. Discovery and connection commands are signed over the remote switch ID. Event codes include connection `0x3002`, local endpoint discovery `0x3003`, peer-alive `0x3102` and peer-via `0x3104`. Display enable is `0x0a00`. Unknown types are ignored, including type 6: the pinned source declares encapsulation but does not implement it.

### Library API

Use `WeavePacketInterface(portName, WeaveOptions)` or the owned stream-factory overload. Subscribe to `ChildConnected` before startup. `WeaveOptions` holds timer and resource limits that are not configuration keys:

| Option | Default |
| --- | --- |
| Handshake timeout | 2 seconds |
| Reconnect delay | 5 seconds |
| Peer expiry | 20 seconds, checked every 22 seconds |
| Peer limit | 256 |
| Receive queue | 256 frames |
| Transmit queue | 256 frames or 256 KiB |
| Display storage | 32 KiB |

## Differences from upstream

- The native frame decoder enforces a frame limit. Upstream declares `MAX_CHUNK = 32768` but its receive accumulator does not enforce it.
- Unknown WDCL types, including declared encapsulation type 6, are ignored because the pinned source has no processing for them.
- Malformed frames, unbounded display data and unchecked allocations are rejected or bounded.
- Startup failure is surfaced to the owner, and reconnection runs as owned, cancellable work.

## Platform notes

The interface uses the shared serial provider and owned streams. Software suites run on Windows x64 and Linux x64. No physical Weave device has been verified; physical-device testing is outside the current roadmap. The upstream Android USB implementation is not provided as a native Android adapter. Android USB permissions, Bluetooth, mobile packaging and untested platform drivers are not supported claims.

## Verification

- `Parity6WeaveTests` exercises WDCL discovery, handshake, packet delivery, display, command and framing behaviour with owned streams and recording adapters.
- `GenerateParity6WeaveVectors.py` generates the pinned reference vectors without opening a serial device.

These tests do not establish physical serial, Bluetooth, Android or live mixed-node interoperability.
