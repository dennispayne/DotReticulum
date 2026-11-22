# KISS and AX.25 KISS interfaces

With the KISS interfaces you can use Reticulum over packet radio modems and TNCs connected by serial port. `KISSInterface` sends Reticulum packets in ordinary KISS data frames and can periodically send an identification beacon. `AX25KISSInterface` additionally wraps every packet in an AX.25 UI frame carrying your callsign and SSID.

Reticulum does not need the AX.25 layer, and it adds overhead to every packet. Only use the AX.25 interface if you really need it. Plain KISS with beaconing is more efficient.

Radio spectrum is legally regulated, and the rules vary around the world. It is your responsibility to know and follow the regulations that apply where you operate.

## KISS interface

```
[[Packet Radio KISS Interface]]
  type = KISSInterface
  enabled = yes

  # Serial port for the device
  port = /dev/ttyUSB1

  # Serial parameters
  speed = 115200
  databits = 8
  parity = none
  stopbits = 1

  # Modem preamble and TX tail, in milliseconds
  preamble = 150
  txtail = 10

  # CSMA parameters
  persistence = 200
  slottime = 20

  # Optional station identification. Both keys
  # are needed. The interval is in seconds.
  # id_callsign = MYCALL-0
  # id_interval = 600

  # KISS flow control, for modems with a
  # small internal packet buffer.
  flow_control = false
```

## AX.25 KISS interface

```
[[Packet Radio AX.25 KISS Interface]]
  type = AX25KISSInterface
  enabled = yes

  # Station callsign and SSID
  callsign = NO1CLL
  ssid = 0

  port = /dev/ttyUSB2
  speed = 115200
  databits = 8
  parity = none
  stopbits = 1
  preamble = 150
  txtail = 10
  persistence = 200
  slottime = 20
  flow_control = false
```

## Options

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | required | Serial port |
| `speed` | 9600 | Baud rate |
| `databits` | 8 | Data bits |
| `parity` | `none` | `none`, `even` or `odd` |
| `stopbits` | 1 | Stop bits |
| `preamble` | 350 ms | TNC preamble |
| `txtail` | 20 ms | TNC transmit tail |
| `persistence` | 64 | CSMA persistence |
| `slottime` | 20 ms | CSMA slot time |
| `flow_control` | `false` | Wait for TNC READY before each transmission |
| `id_callsign`, `id_interval` | none | KISS only. Beacon text (UTF-8) and interval in seconds. |
| `callsign` | required | AX.25 only. ASCII callsign, 3–6 characters. |
| `ssid` | required | AX.25 only. SSID, 0–15. |
| `bitrate` | 1200 bit/s | Generic bitrate estimate |

The hardware MTU is 564 bytes, and AX.25 framing uses 16 bytes of that. The default IFAC size is 8 bytes; IFAC is applied by the interface manager. The AX.25 interface has no beacon setting, matching upstream.

## Behaviour

### TNC setup

Two seconds after opening the port, the interface sends KISS commands for preamble (command 1), persistence (2), slot time (3), transmit tail (4) and READY (15). Millisecond settings are converted to ten-millisecond units and clamped to one byte. READY is configured as 1 even when flow control is off. Command values are not escaped.

### KISS framing

A frame is `c0 00 escaped(payload) c0`. `0xdb` is sent as `db dd` and `0xc0` as `db dc`. Transmission uses port zero. Reception masks the port nibble and accepts DATA and READY from every port.

With flow control, a READY from the TNC releases the next queued packet. If no READY arrives within five seconds, the wait times out. Surplus READY bytes do not build up unbounded send credit. An incomplete nonempty frame expires after 100 ms idle.

### AX.25 framing

The destination is always `APZRNS`, SSID 0. The source is your callsign and SSID. Each callsign byte is shifted left one bit, and short callsigns are padded with a literal `0x20` (not a shifted space), as the reference encoding requires. The SSID bytes are `0x60` and `0x61 | (ssid << 1)`, followed by UI control `0x03` and PID `0xf0`. There is no FCS, repeater path or configurable destination.

On reception, the first 16 bytes of any sufficiently long frame are stripped. As in upstream, addresses, extension bits, control and PID are not validated.

### Beacon

The plain KISS beacon pads its data to at least 15 bytes. Upstream compares the escaped transmitted payload with the unpadded beacon when it clears the beacon timer, so short or escaped beacons can re-arm it. The native implementation reproduces this. The beacon is station identification, not an RNS announce.

### Library API

The interfaces accept a serial port, an owned duplex stream or an asynchronous owned-stream factory. `KissOptions` and `Ax25KissOptions` expose bounded queue and timing options; these are library settings, not configuration keys. A supplied stream cannot reconnect. Serial ports and factories can.

## Differences from upstream

- Receive and transmit queues are bounded. A full receive queue drops frames without blocking READY handling.
- Native framing rejects malformed escapes and oversized frames. Upstream keeps an unknown escaped byte as itself, can carry escape state across delimiters, and can deliver an oversized DATA frame as a size-limited prefix.
- An independent maintenance timer keeps flow-control and partial-frame deadlines running during continuous receive traffic.

## Platform notes

The interface uses the .NET serial provider or a caller-owned stream. Simulator suites run on Windows x64 and Linux x64. No real TNC, radio, RF authorization or platform-specific USB serial driver has been verified. Physical radio and TNC testing is outside the current roadmap and remains unverified.

## Verification

- `Parity6KissTests` exercises KISS and AX.25 framing, configuration, READY, beacon and parser behaviour using recording serial adapters and a controlled clock.
- `GenerateParity6KissVectors.py` generates vectors from the pinned source using recording serial and owner adapters. No physical serial constructor is invoked and no device is opened.

Software tests do not establish physical TNC or live radio interoperability.
