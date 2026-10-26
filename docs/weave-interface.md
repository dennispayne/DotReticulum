# Weave serial interface

Weave is a serial WDCL device and endpoint-switch protocol, not Ethernet. The native interface publishes independently routable endpoint children; packets are sent through those children rather than broadcast by the parent.

## Configuration

Enable `type = WeaveInterface` and provide `port`. `name` comes from the interface section. The reference type-specific constructor consumes no serial speed or flow-control settings: the device requires 3,000,000 baud, 8N1, and no flow control. Generic `bitrate` sets the reported estimate (default 250,000 bit/s); it is not the serial baud. Generic IFAC defaults to 16 bytes and is applied by the manager, not the serial framing layer.

The library API is `WeavePacketInterface(portName, WeaveOptions)` or an owned stream-factory overload. Native timer and resource limits in `WeaveOptions` are not INI keys: handshake timeout defaults to 2 seconds, reconnect delay to 5 seconds, peer expiry to 20 seconds (checked every 22 seconds), peer limit to 256, receive queue to 256 frames, and transmit queue to 256 frames / 256 KiB. Display storage is bounded to 32 KiB.

## Behaviour

WDCL commands use big-endian fields and four-byte switch addresses. HDLC uses `0x7e` delimiters and `0x7d` escaping with XOR `0x20`. Discovery is broadcast; a signed connect response authenticates possession of the advertised Ed25519 key, not operator trust. Data packets carry the endpoint prefix on output and suffix on input. Duplicate suppression uses a shared 48-entry cache and a 750 ms window. The device reports endpoint liveness, path/via, logs, display updates, and CPU/task/memory telemetry. Hardware MTU is 1024 bytes.

The parent is a spawning and reconnecting interface. Subscribe to `ChildConnected` before startup; child identities represent eight-byte endpoint IDs. The parent send is a no-op, not a broadcast. Endpoint expiry removes only that child; device loss ends all old children and reconnect can publish new children. Observer failures are reported without disabling processing.

### WDCL message details

The protocol defines WDCL types 0–6 and four-byte switch addresses. Discovery and connection commands are signed over the remote switch ID. Packet delivery prefixes the outgoing endpoint ID and appends the incoming endpoint ID. Connection events use `0x3002`, local endpoint discovery `0x3003`, peer-alive `0x3102`, and peer-via `0x3104`. Display enable uses `0x0a00`; telemetry includes CPU, task, and memory observations. Unknown types, including type 6, are ignored because the pinned source declares but does not implement encapsulation handling.

The declared upstream `MAX_CHUNK` is 32768 bytes but the reference receive accumulator does not enforce it. The native frame decoder enforces its bound. Duplicate packet tracking is shared by the device and capped at 48 hashes over a 750 ms window. Peer expiry is checked every 22 seconds with a 20-second threshold. The device's serial bitrate is 3 Mbit/s; the 250 kbit/s interface bitrate is only the default network estimate.

## Platform notes

The native bearer uses the shared serial provider and owned streams. Windows x64 and Linux x64 are exercised by software suites; no physical Weave device has been verified. The upstream Android USB implementation is not a native Android adapter here. Android USB permissions, Bluetooth, mobile packaging, and untested platform drivers are not supported claims.

## Differences from upstream

The native implementation enforces a frame limit where upstream's declared `MAX_CHUNK=32768` is not enforced by its receive accumulator. Unknown WDCL types, including declared encapsulation type 6, are ignored because the pinned source has no processing implementation. Malformed frames, unbounded displays, and unchecked allocations are rejected or bounded. Startup failure is surfaced to the owner; reconnection uses owned cancellable work.

## Verification

`Parity6WeaveTests` and `GenerateParity6WeaveVectors.py` exercise pinned WDCL discovery, handshake, packet delivery, display, command, and framing behavior with owned streams and recording adapters. The generator does not open a serial device. Tests do not establish physical serial, Bluetooth, Android, or live mixed-node interoperability.
