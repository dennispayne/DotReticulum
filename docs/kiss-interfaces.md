# KISS and AX.25 KISS interfaces

KISS interfaces send Reticulum packets through a serial TNC. `KISSInterface` uses ordinary KISS data frames. `AX25KISSInterface` wraps packets in a fixed AX.25 UI frame before KISS encoding.

## Configuration

Both types require `port`. Common settings are `speed` (9600 baud), `databits` (8), `parity` (none), `stopbits` (1), `preamble` (350 ms), `txtail` (20 ms), `persistence` (64), `slottime` (20 ms), `flow_control` (false), and generic `bitrate` (default estimate 1200 bit/s). Hardware MTU is 564 bytes; AX.25 framing consumes 16 bytes of that capacity. IFAC defaults to 8 bytes and is applied by the manager.

Plain KISS additionally accepts `id_callsign` and `id_interval` together for an optional UTF-8 beacon. AX.25 requires `callsign` (ASCII length 3–6) and `ssid` (0–15); it has no upstream beacon setting. TNC configuration sends preamble, tail, persistence, slot, and READY commands after a two-second settle period. Millisecond settings are converted to ten-millisecond units and clamped to one byte.

The API supports a serial port, an owned duplex stream, or an asynchronous owned-stream factory. `KissOptions` and `Ax25KissOptions` expose bounded queue and timing options; these are library settings, not additional INI keys.

## Wire behaviour

A KISS frame is `c0 00 escaped(payload) c0`; `0xdb` becomes `db dd` and `0xc0` becomes `db dc`. Receive accepts DATA and READY commands on every port nibble; transmit uses port zero. READY releases flow-controlled queued packets, with a five-second timeout. Surplus READY bytes do not bank unbounded send credits. Incomplete nonempty frames expire after 100 ms idle.

AX.25 uses destination `APZRNS-0` and the configured source callsign/SSID, followed by UI control `03` and PID `f0`. It has no FCS, repeater path, or configurable destination. Short callsigns use literal `0x20` padding as required by the reference encoding. Incoming AX.25 strips the first 16 bytes when the frame is long enough; it does not validate addresses, extension bits, UI, or PID.

### Framing details

TNC setup commands are KISS preamble (command 1), persistence (2), slot time (3), tail (4), and READY (15). READY is configured as 1 even when flow control is disabled. Control values are not escaped. Receive masks the port nibble and accepts DATA/READY from every port; transmit uses port zero. An unknown escaped byte is retained as that byte upstream, and pending escape state can cross delimiters; native parsing is stricter and rejects malformed escapes. Oversized upstream DATA may be delivered as a size-limited prefix; the native bearer rejects the oversize frame.

AX.25 destination is `APZRNS`, SSID 0. Each source callsign byte is shifted left one; the SSID bytes are `0x60` and `0x61 | (ssid << 1)`, followed by UI `0x03`, PID `0xf0`. Short callsigns are padded with literal `0x20`, not shifted spaces. Incoming source behavior strips 16 bytes from any sufficiently long frame without validating AX.25 addresses or control fields.

Plain KISS beacon data pads to at least 15 bytes. Upstream compares the escaped transmitted payload with the unpadded beacon when clearing its timer, so short or escaped beacons can rearm it. The native implementation exposes the source behavior without treating this beacon as an RNS announce.

## Platform notes

The bearer uses the .NET serial provider or a caller-owned stream. Windows x64 and Linux x64 simulator suites are exercised. No real TNC, radio, RF authorization, or platform-specific USB serial driver is certified by those tests.

## Differences from upstream

Receive and transmit queues are bounded; full receive queues drop frames without blocking READY handling. Native framing rejects malformed escapes and oversized frames rather than forwarding malformed data or an oversized prefix. The independent maintenance timer keeps flow-control and partial-frame deadlines progressing during continuous receive traffic. A supplied stream cannot reconnect; serial/factory endpoints can.

## Verification

`Parity6KissTests` and `GenerateParity6KissVectors.py` exercise pinned KISS and AX.25 framing, configuration, READY, beacon, and parser behavior using recording serial/owner adapters and a controlled clock; no physical serial constructor is invoked. The generator does not open a device. Software tests do not establish physical TNC or live radio interoperability.
