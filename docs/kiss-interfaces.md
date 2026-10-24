# KISS and AX.25 KISS bearer contract

## Source and scope

This slice is grounded in Reticulum `a29bfb51c468945c96a1ee22cae0114e993e2391`
(1.5.7), materialized from the verified Reticulum-hosted objects. Source paths
below are relative to that checkout:

| Source | SHA-256 | Reviewed contract |
| --- | --- | --- |
| `RNS/Interfaces/KISSInterface.py` | `ddd04ca2459545ded8e36514aa3daf4a909fcc4e841b32ab97d74d7da8dd2911` | 38–67 constants/escaping; 73–154 options; 156–254 serial/configuration; 256–298 receive/send/queue; 300–396 parser, timers, beacon, reconnect |
| `RNS/Interfaces/AX25KISSInterface.py` | `2eef91b829199ce0e0dfcc2ae5de69bbb2ce62128a54625bc23bcd819ead216b` | 38–74 constants; 80–167 options/validation; 169–266 serial/configuration; 268–326 AX.25/receive/send/queue; 328–411 parser/timers/reconnect |
| `RNS/Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` | 134 minimum bitrate; 929–931 generic configured-bitrate threshold; 1029–1039 post-init override for all bearers |

Acceptance is defined here before implementation. This is a simulator/reference
vector slice, not physical TNC/radio, platform, or live Python-node evidence.
No device discovery, firmware, EEPROM, transmit probing, or COM4 access is
authorized. RNode, runtime configuration and factory wiring are outside this
file's ownership.
The coordinator reports COM4 as VID `239A`, PID `8029`, "TinyUSB Serial";
that descriptor does not establish device ownership, a compatible TNC, or RF
authorization. Its port remains unopened. WSL2 without .NET/native toolchains
and a Docker CLI without a running engine are not platform-test evidence.

## Observable wire and configuration contracts

* Serial defaults: 9600 baud, 8 data bits, no parity, one stop bit; software,
  RTS/CTS and DSR/DTR flow control disabled. Nominal radio bitrate is 1200,
  hardware packet MTU 564 (AX.25 adds 16 framing bytes). Both upstream classes
  advertise shared-medium behavior, default IFAC size 8, and disable ingress
  limiting; runtime/factory integration owns those generic transport settings.
  Generic `bitrate` configuration overrides the 1200 estimate for either bearer
  when at least 5 bit/s. `KissOptions.ConfiguredBitrate` is nullable and inherited
  by `Ax25KissOptions`; `null` preserves 1200. Direct options below 5 are rejected
  before opening anything; the runtime owns upstream's ignore-below-5 config
  parsing and passes either an accepted value or `null`. An override changes
  reported/scheduling bitrate, not serial baud, TNC configuration or wire bytes.
  Diagnostics identify the result as `configured_estimate` or `upstream_estimate`.
* After a two-second device-settle delay, configuration writes, in order,
  preamble (command 1, default 350 ms), tail (4, 20 ms), persistence (2, 64),
  slot (3, 20 ms), READY (15, **always 1 even when flow control is false**).
  Milliseconds truncate toward zero after division by ten; values clamp to
  0–255. Upstream configuration command values are not escaped.
* Data frame: `c0 00 escaped(payload) c0`; DB maps to DB DD, C0 to DB DC.
  Receive masks the port nibble, accepting DATA/READY from every port; transmit
  uses port zero. Unknown commands are ignored. READY is processed for **each
  subsequent non-delimiter byte**, not merely upon seeing the command byte.
* Flow control defaults off. When enabled, one packet locks readiness; each
  READY payload byte or a five-second timeout releases one queued packet.
  Surplus READY bytes leave one idle-ready state after draining the queue;
  they do not bank unlimited permissions for future sends.
  Timeout checks and incomplete-frame expiry run on a 50 ms maintenance tick.
  A partial nonempty frame expires after more than 100 ms without input.
* Upstream tolerates an unknown escaped byte as that byte, carries pending
  escape state across delimiter boundaries, and delivers a size-limited prefix
  of oversized DATA frames. Preserve these observable parser quirks; never
  allocate beyond the fixed receive frame bound.
* AX.25 transmit header is fixed destination `APZRNS`, SSID 0; source callsign
  uppercased ASCII, length 3–6; SSID 0–15. Each callsign byte shifts left one;
  **short callsigns pad with literal 0x20, not shifted spaces**. SSID bytes
  are `60` and `61 | (ssid << 1)`, followed by UI `03`, PID `f0`.
  No FCS, repeater path, variable destination, or extra KISS port option exists.
  Upstream receive only requires more than 16 bytes and strips the first 16:
  it does **not** validate addresses, extension bits, UI or PID. A stricter AX.25
  parser would be an incompatible behavior, not a parity fix. Non-ASCII and
  Unicode case expansions outside ASCII are rejected before opening a device.
* Plain KISS optionally sends UTF-8 `id_callsign` after `id_interval` seconds
  from the first transmit. Beacon bytes pad to at least 15 bytes. Upstream
  compares the **escaped** transmitted payload to the unpadded beacon to clear
  the timer; short or escaped beacons can consequently rearm it. AX.25 has no
  beacon option.

## Native API, ownership and intentional differences

`KissPacketInterface` and `Ax25KissPacketInterface` implement the packet and
reconnecting-interface contracts. Constructors accept a serial port name, an
owned duplex `Stream`, or an asynchronous owned-stream factory. `KissOptions`
contains serial/TNC settings, bounded queue capacities, timer defaults,
optional beacon settings, and a `TimeProvider` for deterministic testing.
`Ax25KissOptions` adds required `Callsign` and `Ssid`.
The asynchronous factory signature is
`Func<CancellationToken, ValueTask<Stream>>`; serial and owned-stream overloads
take `(string portName, KissOptions? options = null)` and
`(Stream duplexStream, KissOptions? options = null)`. AX.25 overloads require
`Ax25KissOptions` as their second argument.
Maintenance uses an independent owned timer rather than polling the serial
driver's `in_waiting`; timeout and beacon checks therefore do not starve under
continuous receive traffic (upstream performs them in its idle-read branch).
`Stream.WriteAsync` has full-buffer-or-exception semantics; there is no native
partial-byte-count return to mistake for a successful full write.

Constructors do no I/O. `StartAsync` is one-shot; a failed/cancelled initial
start cleans up and faults rather than silently succeeding offline. Factory/
serial connections reconnect after runtime faults at five-second intervals;
a single supplied stream cannot be reopened. Reconnect reconfigures the TNC.
Disposal cancels and awaits owned workers and closes owned streams/serial ports.
Cancellation never owns or terminates an external process.
The system `SerialPort.Open` call is synchronous and cannot be interrupted by
a cancellation token inside a native driver; cancellation is checked before
opening and during asynchronous settling/configuration. Simulator cancellation
tests do not establish real driver unplug/open cancellation behavior.

Transmit admission is bounded in both count and bytes (capacity times MTU);
callers wait without copying payloads until admitted. `SendAsync` completes on
actual write, not queue insertion; cancellation removes a queued send logically.
An ambiguous failed active write is not replayed after reconnect. Queued sends
may survive reconnect. Receive buffering is bounded; full receive queues drop
and count packets rather than preventing READY processing in a no-subscriber
path. These explicit bounds replace upstream's unbounded list. Empty/oversized
outbound packets and oversized beacon configuration are rejected. Diagnostics
retain faults, observer exceptions, receive drops and flow timeouts; optional
event observers cannot stop core processing.

## Acceptance criteria

Independent fixtures must execute pinned upstream methods/readLoop using a
recording serial adapter, verify actual imports and clean source, and record
revision/source hashes. Assertions cover default/clamped configuration, DATA
escaping and all receive port nibbles, READY/unknown commands, malformed escapes,
truncated/overlong frames and recovery, AX.25 short/full callsign headers and
permissive inbound stripping. Simulator tests cover no-subscriber processing,
initial failure, cancellation, ownership/dispose races, queue backpressure,
flow timeout, partial-frame timeout, beacon scheduling and reconnect.

The generator uses recording serial/owner adapters, and a clock/sleep adapter
for the beacon timer only. It does not open a serial device or modify reference
source. It verifies actual imported RNS/module paths, clean tracked reference
source, SHA and version before generation. Its fixture contains 9 outbound,
3 configuration, 24 receive cases, 6 flow-state observations and one scheduled
beacon observation.

The coordinator owns all .NET build/test/NativeAOT execution and integration.
Physical serial/TNC/radio tests, actual RF permissions, other OS/device drivers,
and live pinned-Python bidirectional radio exchange remain unverified. No
simulator result establishes any of those claims.

## Slice handoff evidence

Executed from the repository root:

```powershell
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' tests\DotReticulum.Interfaces.Tests\GenerateParity6KissVectors.py --upstream 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
```

Result: exit 0; actual imports resolve to the specified clean pinned checkout,
RNS 1.5.7; 9 transmit, 3 configuration, 24 receive, 6 flow-state and one scheduled beacon
observation written to `Fixtures\parity6-kiss.json`. Source hashes match the
table above. No Python serial-port constructor is invoked by the generator.

Owned production files: `KissOptions.cs`, `KissPacketFraming.cs`,
`Ax25KissFraming.cs`, `KissPacketInterface.cs`, `Ax25KissPacketInterface.cs`.
The new `Parity6KissTests.cs`, generator and fixture live in the Interfaces test
project; its existing fixture-copy rule covers the new JSON without a project
change. This document is the only documentation file edited by this slice.
The integrated Serial/core/manager/runtime changes are verified separately:
the full Windows gate and Linux bearer selection pass these simulator tests.
Exact commands/results are in the
[Parity 6 record](../.github/development-history.md#parity-6-interface-integration-partial-2026-10-24);
physical-device acceptance is still open.
