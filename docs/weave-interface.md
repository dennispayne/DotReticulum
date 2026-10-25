# Native Weave serial interface

## Source contract and acceptance criteria

The reference is `RNS/Interfaces/WeaveInterface.py` at
`a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7), SHA-256
`99b4f217fce0afdfef16ea9641fc740065358eec512e22ec92366558051195c5`.
Weave is a serial WDCL device/endpoint switch, **not Ethernet**.

Source trace (1-based lines):

- 37–69, 111–136, 168–235: HDLC escaping, WDCL types 0–6, 4-byte
  switch addresses, 3,000,000 baud 8N1/no flow control, discovery and
  two-second connection deadline.
- 248–326: serial HDLC framing, receive accounting, errors and five-second
  reconnect. Upstream declares MAX_CHUNK=32768 but does not enforce it in
  its receive accumulator; this native implementation enforces a frame cap.
- 329–334, 599–640: big-endian commands, broadcast discovery, connect
  signature over the remote switch ID.
- 699–780: endpoint liveness/via, command 0x0001 outgoing endpoint prefix,
  incoming endpoint suffix, signed discovery, log and display parsing.
- 781–844: connection event 0x3002, local endpoint 0x3003, peer alive
  0x3102/via 0x3104, CPU/task/memory telemetry.
- 846–1032: fixed 1024 MTU, nominal 250 kbit/s, 20-second peer expiry
  checked every 22 seconds, independently routable peers, parent no-op send.
- 1069–1102: shared 48-entry/0.75-second duplicate cache and serialized
  endpoint delivery/accounting.

Measurable software acceptance: independently execute the pinned Python
discovery/connect/command/delivery/display methods for oracle frames; match
those frames natively; verify signatures before adopting a remote identity;
spawn bounded endpoint peers without requiring subscribers; preserve source
identity, duplicate suppression and timeout removal; isolate observers;
bound framing, display, peer receive and shared queued transmit resources;
verify start failure/cancellation, unplug/reconnect and idempotent disposal
using owned duplex simulators. All .NET validation is run by the coordinator.

## Deliberate native boundaries

Unknown WDCL types (including encapsulation type 6) are ignored: the pinned
source declares them but has no encapsulation processing implementation.
Malformed/oversized frames are dropped rather than reproducing unchecked
indexing, indefinite accumulator growth or display allocation. Logs are exposed
as structured events, not a Python terminal/UI rendering API. Display updates,
CPU/memory/task telemetry and endpoint events are supported independently of
subscribers. Discovery signatures establish possession of the advertised
Ed25519 key, **not publisher trust or authenticated device enrollment**.

No real Weave serial device or Bluetooth device was opened. The separately
authorized COM8 RAK answers RNode firmware queries, not Weave acceptance.
Native serial construction now asserts DTR/RTS like upstream pyserial;
disabled flow handshake does not mean deasserted control lines.
Simulators/reference vectors do not establish physical Weave, live mixed-node
or platform acceptance. Android upstream uses usb4a/usbserial4a + jnius,
requires USB permission, overrides CH34x CDC, and tunes FTDI/CP210x buffers and
timeouts (lines 73–96, 180–223). A caller-owned native Android USB adapter
can supply the duplex stream factory, but no Android adapter, permission
workflow or platform execution is claimed.

## Native API and integration

`WeavePacketInterface(portName, WeaveOptions)` is an `ISpawningPacketInterface`
and `IReconnectingPacketInterface`: register the parent before `StartAsync`,
adopt its `ChildConnected` children, and route sends only through children.
`WeavePeer.EndpointId` is eight bytes; `ViaSwitchId` is an optional four-byte
switch ID. The parent send is an upstream-compatible no-op, not a broadcast.
Use IFAC size 16 by default, fixed hardware MTU 1024 and nominal configured
bitrate 250000. Child IFAC/policy inheritance is the manager's responsibility.

### Exact configuration handoff

The type-specific reference constructor consumes only `name`, required `port`
(no default), and `configured_bitrate` (default null, selecting 250000).
`name` is normally supplied from the configuration section name. Serial
speed/parity/databits/stopbits/flow-control keys are **not** consumed by Weave:
the device protocol requires 3000000/none/8/1/no flow control.

The native host sets `WeaveOptions.ConfiguredBitrate` from generic runtime
`bitrate`, falling back to 250000 when absent; this is the
**effective estimate**, shared unchanged by parent, children and diagnostics,
not the 3000000 serial baud. Upstream `Reticulum.py:929–931` accepts generic
`bitrate` only at >=5; `1029–1042` applies it after construction.
`1112` also injects the generic value into constructor `configured_bitrate`,
overwriting any user entry with that name. Native INI configuration therefore
does not accept a separate `configured_bitrate` fallback. Direct library callers
can supply the typed option.

Generic `enabled`/`interface_enabled` controls creation (default disabled);
`outgoing` defaults true after generic post-init, even though the raw Weave
constructor initializes OUT false. Parent/children inherit that policy.
Generic `ifac_size` is expressed in **bits**, converted to bytes by the host;
the Weave default is 16 **bytes** (`DefaultIfacSize`). `networkname`/
`network_name` and `passphrase`/`pass_phrase` use the existing generic IFAC
derivation; the Weave serial driver must not apply IFAC itself.
Keep existing generic mode/gravity, announce and ingress-control handling.

Each `ChildConnected` child is already started and represents one eight-byte
endpoint, not one serial device or a broadcast domain. The manager must apply
the parent's direction, IFAC, mode/gravity, announce and ingress policy to
each child. It must never echo traffic to its ingress child; other endpoints
remain eligible. Child closure/expiry removes only that child; device loss
completes all old children, and reconnection creates fresh children when
endpoints reappear. `SendsThroughChildren=true`; registering the parent must
not introduce additional parent-level sends. Queue/timer/display limits in
`WeaveOptions` are explicit native API options, not claimed upstream INI keys.

The stream-factory constructor transfers ownership on every open, and copies
an optional host `Identity` rather than borrowing it. `WeaveOptions` makes the
handshake/reconnect/peer timers and resource budgets explicit. An initial
open/handshake failure is surfaced to the lifecycle owner; after successful
start, unplug/read/write faults close all old peers and retry fresh sessions
unless reconnection is disabled. This fail-fast first start is an intentional
difference from upstream's constructor launching a retry thread after failure.

`SendCommandAsync` encodes a big-endian command; `SetDisplayEnabledAsync`
controls command 0x0a00 and update notifications. `DisplayBuffer`, structured
`LogReceived`, `DisplayUpdated` and diagnostics expose bounded device state.
Observer failures are reported to `Faulted` and retained in diagnostics;
event handlers must return promptly. No subscriber is required for processing.
The shared transmit byte budget counts decoded packet bytes (or command plus
command data), not serial escape overhead. Framed allocations are additionally
bounded by the frame-count limit and at most twice the reserved payload plus
fixed address/endpoint overhead. No frame is copied before a reservation.
Receive overflow drops only the affected packet and records a diagnostic,
rather than blocking the one shared device reader and unrelated peers.

`GenerateParity6WeaveVectors.py` uses `WDCL.__new__` and a recording
`serial.write` adapter; it does not call the port-opening constructor or launch
threads. It executes unmodified `WeaveDevice.discover`, `incoming_frame`
(including signature validation/handshake), `deliver_packet`, display
connect/disconnect, command and broadcast methods, and unmodified
`WDCL.process_outgoing`. This is reference-vector evidence, not unmodified
device-driver or physical/live acceptance. The seven recorded writes include
escaped payload bytes and declared-but-unimplemented encapsulation framing.

## Slice verification record

The isolated reference Python was executed twice:

```powershell
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' tests\DotReticulum.Interfaces.Tests\GenerateParity6WeaveVectors.py --upstream 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
```

Both runs exited 0, verified the actual RNS/LXMF imports and exact upstream
revision through `reference_environment.verify_reference(require_lxmf=True)`,
and recorded seven oracle writes. Exact import paths and source digest are
persisted in `Fixtures\parity6-weave.json`. No reference source was modified.

Thirteen focused xUnit tests cover the oracle and controlled owned-device
simulator, passing in the full Windows gate and Linux bearer selection.
See the [Parity 6 record](../.github/development-history.md#parity-6-interface-integration-partial-2026-10-24).
Focused selector:
`FullyQualifiedName~Parity6WeaveTests`. No simulator result may be labeled
physical serial, Android, Bluetooth or live Weave acceptance.
