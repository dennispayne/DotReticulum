# RNode library slice (Parity 6)

## Source and acceptance contract

Reference: RNS 1.5.7, Reticulum-hosted revision
`a29bfb51c468945c96a1ee22cae0114e993e2391`.
Source paths below are relative to that verified checkout:

| Source | SHA-256 | Relevant ranges (1-based) |
| --- | --- | --- |
| `RNS/Interfaces/RNodeInterface.py` | `ae3ec2e539ca9a888dcd9dad3bdd52f1914c24e33b3dcf63cc066ba0883ab369` | 43–133 constants; 135–365 config; 367–501 startup/detect; 502–572 display/reset; 574–745 controls/validation/data; 746–1173 reader/PHY/errors; 1175–1228 lifecycle/battery; 1452–1587 TCP |
| `RNS/Interfaces/RNodeMultiInterface.py` | `f99f562355bc19e1ae7896f0a3039cb57fd377fcfc8ec9f8b4d7e913bf1350d9` | 42–146 commands; 148–333 nested config/open; 334–550 discovery/control/data; 569–893 reader; 895–935 lifecycle/global READY; 938–1164 child configuration/queues |
| `RNS/Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` | 843–945 common keys; 1029–1104 post-init; 1174–1177 multi post-init then start |
| `RNS/Interfaces/Interface.py` | `70bf362bc54872e24c124e503d8c52c487886aa141f62faed739e46c396c689c` | 98–174 base/default ingress context |

Observable criteria established before implementation:

* Execute the actual pinned `detect`, setters, `initRadio`, `process_outgoing`,
  `KISS.escape`, and radio validation methods, without opening serial devices.
  Store generated framing/control/configuration vectors and source/import provenance.
* Serial defaults: 115200, 8N1, no flow handshake; explicit asynchronous start.
  Frequency, bandwidth, power, SF and CR must be supplied; the single Python
  config defaults them to zero and consequently rejects missing configuration.
  Single: 137 MHz–3 GHz, power 0–37 dBm; multi SX127x/SX126x:
  137 MHz–1 GHz; SX128x: 2.2–2.6 GHz, power -9–37 dBm.
  Both: bandwidth 7800–1625000 Hz, SF 5–12, CR 5–8.
  Flow control defaults false. Optional short/long airtime percentages are
  0–100, encoded as truncated hundredths, big-endian ushort.
* Detection sends 08/73, version query 50/00, platform 48/00, MCU 49/00;
  multi adds interfaces 71/00. Reply 08/46 identifies a device. Required
  firmware is >=1.52 single, >=1.74 multi (higher majors accepted).
  Configure frequency/bandwidth (big-endian uint), power (signed on multi),
  SF, CR, optional airtime limits, then radio-on. Echo validation allows
  frequency error <=100 Hz and compares bandwidth, power, SF, state.
  Upstream does not validate CR echo; this slice requires its arrival for
  reliable bitrate reporting but does not compare it.
* Frames use C0 delimiters; payload DB -> DB DD and C0 -> DB DC. The command
  byte is not escaped.
  Multi TX selects the virtual port (1F/index frame) immediately before each
  command/data frame. Multi defines separate command identifiers
  00,10,20,70,75,90,A0,B0,C0,D0,E0,F0, but the actual pinned reader delivers
  completed data only for command 00, on the last selected 1F/index radio.
  Other identifiers are not an evidenced RX delivery contract. This slice
  follows selected-radio CMD_DATA reception, not an invented port-nibble format.
  Hardware MTU is 508. No Python nibble-based standard-KISS port encoding.
* Multi interface discovery reports pairs (index,type); recognized chip
  families are SX127x (00/01/02), SX126x (10/11), SX128x (20/21).
  Nested options retain child name, vport, enabled/outgoing, all radio
  parameters and flow/airtime controls. Upstream enables children with
  child `interface_enabled` OR parent `enabled`; retain this explicit rule.
* Initial flow credit is one per child. A global READY releases each
  configured child's next queued transmission independently. No timeout
  invents new firmware credits. Serialize selection+command atomically;
  a blocked child's flow queue must not block another child.
* Optional UTF-8 ID callsign (at most 32 bytes) and interval activate only
  together; no beacon is emitted before ordinary outbound activity. Single
  sends on its radio; multi sends on each online radio (source 858–869).
  `Outgoing=false` is not a low-level send ban and does not suppress these
  device-originated callsign beacons.
* Receive processing/firmware progress must run without event subscribers.
  Bound receive and send queues. Saturated child RX drops only that child's
  packets, with counters, not control responses or sibling traffic.
  Report observer exceptions without disabling processing.
* Decode reported bitrate as SF*4/CR*bandwidth/2^SF; RSSI byte-157,
  signed SNR/4, upstream SF-dependent quality clamped 0–100; retain
  airtime/channel-load, PHY timing/CSMA, battery and temperature where
  present. Multi PHY has 10 bytes versus single's 12.
* Errors 01/02 and unknown fatal errors disconnect; single errors 05/06
  are retained as nonfatal diagnostics. ESP32 reset 55/F8 reconnects when
  online. Reopen factory/serial after five seconds, re-detect and
  reconfigure, without replacing child identities. A supplied single
  stream cannot reconnect. Cancel/dispose unblock readers and flow waits.
  Do not replay ambiguous writes after I/O failure.
* Simulated-device tests must cover byte vectors, echo rejection, minimum
  firmware, invalid config, malformed/truncated/oversize frames, cancellation,
  queue pressure, child isolation, unplug/reconnect and idempotent disposal.

## Intentional boundaries and differences

This is a native library slice, not physical-radio certification. No real
device is opened by generators/tests. COM4's observed USB identifier does not
establish ownership or RNode identity. Hardware acceptance requires explicit
owned-device/port, chip/firmware, frequency, bandwidth, power, SF/CR, legal RF
authorization, and platform-specific unplug/replug evidence.

Bounded queues, strict malformed-escape rejection and full-frame length checks
are hardening relative to Python's unbounded queues/per-byte parser.
Partial frames expire after 100 ms serial idle (upstream data timeout);
control partials also expire. Startup uses bounded response deadlines rather
than relying on Python's fixed sleeps. Missing detect/firmware/configuration
echoes fail startup rather than enabling an unvalidated radio.
Missing frequency and CR echoes fail closed; Python allows absent frequency
and does not include CR in `validateRadioState`. Empty callsigns do not create
empty wire packets. Intervals must be positive; NaN/infinite percentages are
rejected. Hardware-error history is bounded to the most recent error/code
rather than Python's unbounded list. Ambiguous/canceled partial writes force
device reconnection instead of replaying a potentially transmitted packet.

Android's platform-specific class and BLE/bonding remain platform work, not
verified hardware support. EEPROM, firmware updates, trust changes and
rnodeconf are separate later utility contracts. Display/framebuffer/reset
and single-radio TCP controls are implemented below; physical device support
is still unverified.
No helper process is spawned. Each successful stream-factory return transfers
ownership; serial owners and streams are closed on failure/disposal.
No persistence or firmware modifications are performed.

## API and validation evidence

`RNodePacketInterface` and `RNodeMultiPacketInterface` each accept:

```csharp
(Stream duplexStream, RNodeOptions options) // or RNodeMultiOptions
(Func<CancellationToken, ValueTask<Stream>> openStream, RNodeOptions options)
(string portName, RNodeOptions options)
```

The first form owns even an unstarted stream and cannot reconnect. Factory
and serial forms reopen after disconnect when `Reconnect=true`. Constructors
only validate; `StartAsync` opens/configures. A factory must honor cancellation
and return a fresh readable/writable stream. Disposal cannot bound a malicious
factory or stream which ignores cancellation **and** disposal.
Single-radio `portName` accepts case-insensitive `tcp://hostname`, as upstream.
The host is the entire remainder, not an invented `host:port` parser.
The source's fixed port is 7633; `RNodeOptions.Tcp.Port` is an explicit native
adapter/test extension for other ports, not an additional Python config key.
Multi's source only supports serial ports; its constructor rejects URIs.
BLE URIs remain explicitly unsupported here.

`RNodeOptions.Radio` is a `RNodeRadioOptions` containing `Frequency` (Hz, long),
`Bandwidth`, `TxPower` (dBm), `SpreadingFactor`, `CodingRate`, `FlowControl`,
`AirtimeLimitShort`, `AirtimeLimitLong`. The first five must be explicitly
supplied; their zero defaults fail validation.

`RNodeMultiOptions.Subinterfaces` contains `RNodeSubinterfaceOptions`, which
inherits all radio parameters and adds `Name`, `VirtualPort`, `Enabled=false`,
`Outgoing=true`. `EnableAllChildren=false` is the upstream parent-enable
override, not a flattened child setting. Children are enabled by
`child.Enabled || parent.EnableAllChildren`; enabled vports must be unique,
0–10, and present in firmware discovery. The detected chip validates the
frequency range before that child's radio-on command. As upstream does,
unknown chip IDs fall back to SX127X classification, not a claim of support.

Both device option types expose `ConfiguredBitrate`, `IdCallsign`, `IdInterval`,
`SendQueueCapacity=256`, `ReceiveQueueCapacity=256`, `Reconnect=true`,
`ReconnectDelay=5s`, `DeviceSettleDelay=2s`, `ResponseTimeout=2s`,
`PartialFrameTimeout=100ms`, `ShutdownTimeout=1s` and `TimeProvider`.
Receive capacity is per radio. A full RX channel drops that radio's packet
and increments `dropped_packets`; it never stalls firmware/control processing.
Send capacity bounds admitted per-radio copies including a flow-blocked send;
additional callers wait cancellation-aware without making copies.

Multi implements `ISpawningPacketInterface`, publishes already-started
`RNodeSubinterface` children once after configuration, and exposes them through
`Children` even without subscribers. Parent send is unsupported. Parent receive
is empty and waits until disposal/cancellation. Each child implements
`IReconnectingPacketInterface` and diagnostics; child `Connected`/`Disconnected`
events follow device recovery without replacing the object or ending its RX
channel. Disposing one child stops only that endpoint, not the shared device
or siblings. The manager must not start children again. Single implements
`IReconnectingPacketInterface`. Both expose `Faulted`, and observer failures
are retained in `last_error`/`observer_errors` without escaping into processing.
Child `IPacketInterfaceChildPolicy.Outgoing` exposes the nested selection flag
to coordinator-owned Core integration (`AllowOutgoingSelection` is also a native
convenience alias). It must only gate broadcast/attached interface selection: direct
and known-path sends bypass `OUT` upstream and therefore remain allowed by
child `SendAsync`, even when `Outgoing=false`. Children expose
`InheritParentOutgoing=false`, `CountParentTraffic=true`, and the native
`InheritParentAccessCode=false` property. The integrated manager applies that
exclusion rather than automatically inheriting IFAC. This avoids accidentally
inheriting parent OUT/IFAC onto children. Normalized native recursive traffic
accounting intentionally differs from Python's asymmetric counters: source
parent TX counts escaped payload bytes while child TX counts raw bytes and
parent RX is not incremented by child delivery.

## Exact configuration and inheritance mapping

Only the following nested keys are read by `RNodeMultiInterface.__init__`
(lines 175–224). They are not generic recursive interface sections:

| Nested key | Missing/default | Native mapping |
| --- | --- | --- |
| section name | required structural name | `RNodeSubinterfaceOptions.Name` |
| `interface_enabled` | false | `Enabled` |
| `enabled` | **ignored**; not a nested alias | no native child alias |
| `vport` | None; fails during startup conversion | `VirtualPort` |
| `frequency`, `bandwidth`, `txpower`, `spreadingfactor`, `codingrate` | None; invalid when child is created | corresponding radio properties |
| `flow_control` | false | `FlowControl` |
| `airtime_limit_short`, `airtime_limit_long` | None | optional percentages |
| `outgoing` | true; explicit false only disables selection | `Outgoing` |
| `networkname`, `network_name`, `passphrase`, `pass_phrase`, `ifac_size`, `mode`, `interface_mode`, `bitrate` | ignored by nested parser | do not fabricate child overrides |

Parent `enabled=true` enables **all** nested radios even if child
`interface_enabled=false`. Parent `interface_enabled=true` enables the parent
in common runtime parsing but does **not** apply that all-child override.
`EnableAllChildren` therefore maps specifically to parent `enabled`, while
child `Enabled` maps specifically to child `interface_enabled`. Executed
constructor vectors demonstrate both paths and rejection of nested
`enabled=true` alone.

Single-radio keys are `port`, `frequency`, `bandwidth`, `txpower`,
`spreadingfactor`, `codingrate`, `flow_control`, `airtime_limit_short`,
`airtime_limit_long`, `id_interval`, `id_callsign`; missing numeric radio
values default to zero and fail validation. Multi parent-specific keys are
`port`, `id_interval`, `id_callsign` plus its nested sections and enable rule.
`name` is injected from the section by common runtime setup.
`id_interval` is integer seconds upstream; native API uses `TimeSpan`.

Common **parent-level** runtime keys remain coordinator-owned:

* `interface_enabled` / `enabled`: either true attaches the interface.
* `outgoing`: defaults true. For multi this sets only the parent wrapper's
  `OUT`; each child receives its **own** nested `outgoing`, independently.
* `mode` / `interface_mode`: default full; common aliases include
  full, access_point/accesspoint/ap, pointtopoint/ptp, roaming, boundary,
  gateway/gw, internal. `interface_mode` has precedence. The pinned common
  `interface_mode` branch accidentally tests `c["mode"]` for gateway/internal;
  native normalization need not reproduce that Python KeyError, but must
  disclose that compatibility difference if accepting those aliases there.
* `networkname` / `network_name`, `passphrase` / `pass_phrase`: default None.
  Empty or literal `"None"` values are ignored; a nonempty later alias
  overrides the earlier alias. `ifac_size` is in bits: values >=8 are divided
  by eight with integer truncation; smaller values use the default.
  RNode parent default IFAC size is **8 bytes**, not eight bits.
* `bitrate`: integer >=5 becomes `ConfiguredBitrate`; smaller values are
  ignored by runtime normalization, not passed to the native options validator.
  Single startup applies the common post-init override after radio echoes.
  A subsequent frequency/bandwidth/SF/CR report recomputes its measured bitrate,
  exactly as upstream does; reconnect also reinitializes measured bitrate.
  Multi applies the override to the parent only; children retain measured
  bitrate. A nested `bitrate` value has no effect.
* Other common routing/ingress/discovery configuration belongs to common
  runtime integration, not a second RNode-specific parser.

Multi config runs common post-init **before** `start()`, then creates children.
Source lines 373–380 copy `OUT` from nested config, `IN=true`, parent mode,
parent announce-rate target and hardware MTU. It does **not** copy parent
IFAC identity/name/key/size or bitrate; child initializes `ifac_size=None`.
Thus a parent's IFAC config is not source evidence of IFAC-protected radio
children. Child IFAC fields must not silently inherit simply because the native
manager treats other spawners' peers that way.

## TCP and public control mapping

Single TCP uses a cancellation-aware owned `TcpClient`/`NetworkStream`,
`NoDelay`, five-second connection/detection deadlines, 1500-ms partial-frame
idle timeout and detection keepalives after 3.5 seconds without writes.
Linux configures keepalive idle=5 seconds, interval=2 seconds, count=12 and
`TCP_USER_TIMEOUT=24000ms`; macOS sets keepalive/idle=5 seconds, matching the
reference's platform branches. Windows has no extra reference socket tuning.
DNS may try multiple addresses natively rather than only Python's first result.
The owning device handles reconfiguration/retry every five seconds; the
Python helper's `RECONNECT_WAIT=4` constant is not used by its read-loop path.
`RNodeOptions.RNodeTcpOptions` exposes timing overrides for deterministic adapters/tests.

| Python public control | Native mapping |
| --- | --- |
| detect | start/reconnect and `RefreshDeviceInformationAsync` on single/multi |
| setters, initRadio, validate firmware/state | typed options at startup plus single/child `ConfigureRadioAsync`; `SetRadioStateAsync`; `RNodeProtocol` |
| process incoming/outgoing, queue/process_queue | bounded per-child asynchronous receive/send and READY credit |
| enable/disable_external_framebuffer | `EnableExternalFramebufferAsync` / `DisableExternalFramebufferAsync` on single and multi |
| write_framebuffer, display_image | `WriteFramebufferLineAsync`, `DisplayImageAsync` on single and multi; 8-byte image lines, incomplete final line ignored |
| read_framebuffer, read_display | single `ReadFramebufferAsync` (512 bytes), `ReadDisplayAsync` (1024 bytes), cached `LastFramebuffer`/`LastDisplay`, latency diagnostics |
| start/stop_display_updates | single cancellation-aware `ReadDisplayUpdatesAsync`; default interval 1 second, no hidden unowned worker |
| hard_reset | `HardResetAsync`, command 55/F8 then 2.25-second wait; response-driven reconnect remains active |
| get_battery_state/string/percent | single `BatteryState`, `BatteryStateDescription`, `BatteryPercent` plus diagnostics |
| detach/reconnect_port | async disposal and factory/serial/TCP recovery |
| framebuffer/BLE/TCP adapters and callback buffers | native stream/socket boundary, not exposed unbounded Python queues |

Display writes are no-ops on platforms without the reported ESP32/NRF52
display capability, as upstream. Read calls remain explicit command requests.
Concurrent same-kind reads are serialized, response waiting is bounded and
cancellation-aware, and malformed responses cannot satisfy them.
Live reconfiguration validates the same radio constraints/echoes as startup,
retains the new radio parameters for reconnect, and rolls back/reconnects on
failure. Changing the endpoint's asynchronous flow-control policy requires
recreating the endpoint; the wire radio parameters/airtime controls remain
live-configurable. Radio-off is a device command, not an `OUT` selection change.
Low-level framebuffer payloads are bounded to 1023 bytes plus the line byte;
images are capped at 256 addressable lines before any writes (Python instead
fails when a later line number can no longer fit a byte). No firmware image,
EEPROM, trust or updater operation is performed.

`RNodeProtocol` and `RNodeFrameDecoder` expose the bounded stateless wire
boundary for adapter use and fixture verification. They do not execute any
device operation.

Independent oracle command (Windows; run 2026-10-24, exit 0):

```powershell
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' tests\DotReticulum.Interfaces.Tests\GenerateParity6RNodeVectors.py --upstream 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
```

The generator calls `verify_reference(require_lxmf=True)` and verifies the
actual imported RNS modules under that checkout; LXMF 1.2.1 imports from the
isolated venv. It records the above SHA-256 hashes and revision. Serial adapters
only record writes/replay bytes; no constructor or real serial port is opened.
It executes unmodified upstream detection, initialization/setters, outgoing
data, leave, display controls/reset, radio echo validation, bitrate update,
multi nested constructor and the single-radio `readLoop` for RX/PHY/display
values. TCP defaults are read from the imported helper class.
The intentional 101-Hz mismatch prints
`Frequency mismatch`; this is a negative oracle case, not a generator failure.
Output: `tests\DotReticulum.Interfaces.Tests\Fixtures\parity6-rnode.json`.
No source or method is patched. The nested-constructor oracle supplies an
unstarted real Reticulum singleton with default ingress fields set to None,
because base `Interface.__init__` reads runtime defaults through that singleton.
This no-I/O dependency context is recorded in the fixture; it does not prove
full upstream runtime initialization or hardware startup.

Owned C# tests (`Parity6RNodeTests` and `Parity6RNodeSimulator`) exercise vectors,
bytewise framing, malformed/oversize/idle-partial recovery, configuration
validation, firmware floors, missing/mismatched echoes, flow credit/queue
cancellation, nested enable/outgoing behavior, independent children,
congested-child RX isolation, stable-identity reconnect, reset/reopen retries,
callsign activity beacons, PHY/display diagnostics, configured bitrate, direct
sends with selection disabled, display polling/cancellation and disposal.
Live radio control/reconnect persistence is also covered. They use in-memory
duplex streams and a loopback TCP device simulator only.
Neither their existence nor oracle generation constitutes
physical-radio or cross-platform evidence.

The warning-free solution build, full Windows live/native suite and Linux
bearer selection pass, including these simulator/TCP/display tests and runtime
configuration wiring. See the
[Parity 6 record](../.github/development-history.md#parity-6-interface-integration-partial-2026-10-24).
Hardware and Android/BLE platform behavior remain unverified; EEPROM/updater
utility behavior remains the later utility phase.
