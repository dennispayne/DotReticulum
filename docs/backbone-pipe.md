# Backbone and subprocess pipe bearers

## Source contract and acceptance boundary

Reference: Reticulum `a29bfb51c468945c96a1ee22cae0114e993e2391`
(1.5.7), fetched reference checkout, not an installed `rns` package.
Source SHA-256:

* `RNS/Interfaces/BackboneInterface.py`:
  `c3445132d89af04998c35b12e5101c8975d73599f62fe71b501b3d734c150438`
* `RNS/Interfaces/PipeInterface.py`:
  `d053e58f90b8ec0ce8ffef44ff954c4f9cdc9042115352083a8b580575abfa46`

Read before implementation:

* Backbone lines 42–183: Linux/Android restriction; hardware MTU 1,048,576;
  estimated bitrate 100 Mbit/s; default IFAC 16; bind `device` or
  `listen_ip`, `listen_port` (`port` overrides); IPv4 preferred unless
  `prefer_ipv6`; fast-flap options default enabled, threshold 20 seconds,
  grace 5, block expiry 12 hours (`fast_flapping_block_time` is minutes).
* Lines 333–562: ingress global-transport-queue watermarks 90/68/10 percent,
  250 ms sampling, producer allocation with minimum 32 interfaces and
  penalty 1.5; 32 KiB socket receive buffer. Egress sampling 1 second,
  mid watermark 128 KiB, high watermark 4 MiB, 3 zero-progress ticks,
  gate ETA >10 seconds, release ETA <5 seconds, disconnect after 12 seconds.
* Lines 564–767, 789–835, 843–1121: separate accepted-peer identities;
  listener itself does not send; parent/child receive counts unescaped bytes,
  transmit counts actual escaped bytes written. Fast-flap increments on a
  disconnect before threshold, blocks only when count **exceeds** grace,
  expires relative to last flap. Initiator retry defaults 5 seconds and
  unlimited attempts, initial timeout 5 seconds; Linux TCP user timeout 24
  seconds, keepalive idle/interval/count 5/2/12. IFAC and transport policy
  inheritance belong to manager/runtime integration, not the byte bearer.
* `util/HDLC.py` lines 32–127: flag `7e`, escape `7d`, xor `20`;
  Backbone accepts shared delimiters, preserves unrecognised escape pairs,
  and only emits frames longer than 19 bytes, within MTU plus IFAC.
* `util/TransmitBuffer.py`: bounded append is atomic; partial send preserves
  offset, zero/would-block retains bytes, interrupted send retries;
  accounting advances only on successful writes.
* Pipe lines 44–204: required command; POSIX `shlex.split`, no shell;
  stdin/stdout only; stderr inherited; respawn delay 5 seconds; hardware
  MTU 1064, estimated bitrate 1 Mbit/s, default IFAC 8, shared medium.
  RX counts unescaped data, TX counts escaped framing. Initial spawn failure
  raises; later EOF/read failure respawns after delay. Decoder alternates
  open/close flags (not shared flags), accepts unknown escapes by retaining
  the following byte, and truncates oversized frames at 1064 bytes upstream.

Acceptance criteria for these library slices: independent pinned-Python
framing and state vectors; bounded frame/byte queues; whole-frame concurrent
send ordering and accurate successful-write accounting; separate accepted
peer routing identity and child-failure isolation; retry/restart, cancellation,
start failure and idempotent disposal tests; egress stall/hysteresis and
fast-flap boundary tests; subprocess argument boundaries, binary stdin/stdout,
nonzero exit, bounded error reporting, failed respawn and owned-process cleanup.
Tests are local software evidence, not physical hardware or live RNS topology
evidence. Executed acceptance is recorded in the development history.

## Native .NET boundary and intentional differences

Backbone uses .NET asynchronous sockets rather than a process-global epoll
thread. The upstream Linux/Android platform gate is preserved at start.
Frame codec/control unit tests are platform-independent; Windows cannot
establish a supported Backbone listener/client. Pipe uses `ProcessStartInfo`
with an explicit executable and `ArgumentList`, never shell interpretation;
configuration must translate command text to argv outside this bearer.
The child inherits stderr (matching upstream), avoiding a hidden stderr queue.
Both bearers start explicitly and asynchronously dispose owned resources.

Backbone ingress combines bounded per-peer channel/socket backpressure with
the shared source control policy described below. Listeners, initiating peers
and accepted children implement `IPacketInterfaceIngressFeedback` and use the
same owning manager's `PacketInterfaceIngressQueue` identity. Separate managers
do not share control or flap state. Its live provider must represent the
router's actual global **TC_DATA** queue; the manager merge channel, containing
multiple traffic classes, is not evidence for that upstream contract. Provider
rebinding keeps the queue object's identity and does not recreate controller
state. Standalone bearers retain private scoped
state; `UpdateIngressQueueDepth` remains a manual sampling entry point for a
standalone listener, not a required runtime timer. Flap entries are bounded;
existing offenders are not evicted to admit new attackers when full. This
explicit hardening differs from upstream's unbounded process-static table.

Pipe rejects oversized/truncated/bad-escape frames rather than forwarding an
upstream-truncated corrupt packet. It enforces a bounded read queue and
cancellation-aware serialized writes. Offline sends fail explicitly rather
than silently disappearing. Restart never replays an interrupted old frame.
Disposal terminates only the process tree it created and awaits exit.

## Evidence

The serialized Windows solution gate and Linux bearer selection pass; an actual
pinned Python Backbone advertisement autoconnects, releases the bootstrap and
carries encrypted link traffic on Linux. Windows managed/native Pester executes
owned Pipe startup/shutdown. Native Backbone-specific and physical-network
acceptance remain separate from generic native publication/resource flows.
Exact commands/results are in the
[Parity 6 record](../.github/development-history.md#parity-6-interface-integration-partial-2026-10-24).

## Library integration API

* `BackbonePacketInterface(IPEndPoint, BackbonePacketInterfaceOptions? = null)`
  is a spawning listener; its `ChildConnected` children arrive already started.
  The listener exposes `LocalEndpoint`, `ClientCount`, diagnostics, and
  `UpdateIngressQueueDepth(int depth, int capacity)`. Subscribe before start;
  an unobserved child is closed instead of leaking. Parent `SendAsync` is
  unsupported; route via the accepted child. Socket reuse-address and backlog
  1 follow `BackboneInterface.py` lines 272–292.
* `BackbonePacketInterface(string? listenHost, int listenPort,
  BackbonePacketInterfaceOptions? = null)` defers hostname/device resolution
  until `StartAsync`; neither constructor performs I/O. `options.Device`
  takes precedence over hostname or a supplied literal endpoint.
* `BackboneClientPacketInterface(string host, int port,
  BackbonePacketInterfaceOptions? = null)` is the initiating variant.
  It implements `IReconnectingPacketInterface`, retains object identity across
  reconnections, prefers IPv4 unless requested, and never replays queued
  frames from a dead connection. A client has the same bounded backpressure,
  egress control and accounting as an accepted child.
* Options carry bitrate, MTU, IFAC overhead bound, max clients, RX/TX frame
  bounds, TX byte bound, flap controls, DNS family preference, connection
  timeout, device and reconnection limit/delay. Queue admission is immediate/atomic;
  successful Backbone `SendAsync` means accepted into the bounded transmit
  queue, not confirmed remote receipt. Queue-full/stalled/offline sends fail
  with `IOException`; diagnostics account framed drops and actual bytes sent.
* `PipePacketInterface(string executable, IEnumerable<string>? arguments =
  null, PipePacketInterfaceOptions? options = null)` implements
  `IReconnectingPacketInterface`; options are respawn delay (5 seconds),
  optional consecutive-spawn-failure limit (unlimited), confirmed-write timeout
  (12 seconds, a .NET ownership bound), RX queue capacity (256 frames), and
  nullable `ConfiguredBitrate` (positive when supplied). `Bitrate` uses that
  generic post-init override or the source's 1,000,000 bit/s default; the fixed
  1064-byte MTU and disabled MTU discovery do not change with this estimate.
  Argument strings are never re-split or shell-expanded. `IsSharedMedium` is
  true; hardware MTU is fixed at 1064. TX counts confirmed complete
  write-and-flush frames: .NET streams do not report partial bytes on a failed
  write. A failed/canceled in-flight write terminates that owned child to avoid
  joining a new frame to an incomplete old prefix. Cancellation waiting for
  the send lock does not kill a healthy child. Error text is bounded to 4096
  characters; stderr remains inherited. There is no subprocess-shell fallback.

Concrete coordinator integration requirements (no shared files changed here):
pass bind host/port and `Device`/`PreferIPv6` without doing runtime-constructor
DNS; map command text to executable/argv; pass configured IFAC size and bitrate;
adopt spawning children and provide the shared ingress queue before start.

### Shared ingress and flap scope

`BackboneSharedIngressState` uses a
`ConditionalWeakTable<PacketInterfaceIngressQueue, ...>`; queue identity is the
manager lifetime boundary. One cancellation-aware 250 ms sampler serves all
registered accepted and initiating Backbone peers, across every listener in
that manager. Endpoint teardown unregisters its producer. Last-owner disposal
unsubscribes queue notifications and awaits the sampler; no per-packet tasks
or historical-peer registry is retained. Bounded flap history remains keyed
to the manager queue between interface removal/re-addition, and is collectible
with that queue. A restarted sampler awaits its predecessor before sampling.

Source references: `BackboneInterface.py` lines 348–410, 502–562, 688–713 and
1084–1121; `Reticulum.py` lines 749–752; `Transport.py` lines 44–64. The fixture
records hashes for the additional source files. Periodic thresholds are
`max(2, floor(.68*capacity))` and `floor(.10*capacity)`, with strict `>`/`<`
comparisons. It selects the largest packet producer globally, uses all peer
wire bytes for the available budget, minimum 32-peer headroom and penalty
1.5, and resets every peer's sampling counters after each periodic evaluation.
Hold is `available / max(available*allocation, 1) * span`; after a hold expires,
low occupancy releases at most one gated producer per tick.
The .NET window uses monotonic time, starts when sampling is acquired, and
has a 1 ms minimum span (the reference already floors its periodic span).
It intentionally avoids wall-clock jumps, divide-by-zero immediate samples
and epoch-sized pre-first-tick holds; no exact wall-clock pathology parity is
claimed.

Immediate pressure uses the same global producer window without resetting it,
with hold `span/allocation`. The source dispatch threshold is
`max(128, max(4, floor(.90*capacity)))`, not simply 90% for small queues.
The Core boundary exposes `event Action<int,int>? HighWatermarkReached`,
carrying captured `(depth, capacity)`, and invokes it on **every** pre-enqueue
observation at or above that threshold, outside manager/router locks
(`Transport.py`'s `InboundQueues.put` behavior). The
controller subscribes while it has owners and reacts without waiting for a
periodic tick. A notification remains effective even if a concurrent consumer
has since reduced live depth, matching the source's pre-enqueue observation.
Sampling alone cannot claim this path.
No reflection/event discovery or silent polling fallback is used.

The coordinator's `PacketInterfaceManager.SetIngressQueueSource(Func<int>, int)`
rebinds the same queue object's provider. `TransportRouter` supplies its actual
DATA scheduler queue count and configured capacity, notifying before scheduler
locking/admission, including full-queue rejection attempts. Announce and
path-request queues do not drive this event. Standalone managers retain a
merge-queue view; that convenience is explicitly not router DATA-queue parity.
Producer statistics still include all Backbone frames, as in the source;
only the pressure signal is DATA-class-specific.

Flap counts are shared across listeners but each listener's enabled flag,
grace, threshold and expiry govern its own source-equivalent query/update.
Expiry is relative to the globally recorded last short disconnect; counts
block only when they exceed that listener's grace. Cross-manager isolation,
different grace policies, exact expiry and persistence across active-owner
gaps have dedicated tests. The source's process-wide state is intentionally
manager-scoped in .NET rather than affecting unrelated stack instances.

### Asynchronous bind selection

`device` wins over `listen_ip` (`BackboneInterface.py` lines 85–121,
126–183). Without a device, `listen_ip` is required: there is no invented
wildcard, localhost or discovery default. Configuration's `port` alias
overrides `listen_port` before the factory supplies the constructor port.
Port 0 remains valid and the resolved port is exposed after startup.

Device selection uses its exact kernel name and OS-reported unicast-address
order. Prefer the first IPv4 address unless IPv6 is requested or IPv4 is
absent; fall back to the first available address of the other family.
Link-local IPv6 receives the selected interface's IPv6 scope index, equivalent
to upstream appending `%device` before address resolution. Missing devices
or devices without IPv4/IPv6 addresses fail explicitly and never fall back
to an otherwise valid listen hostname. Without a device, DNS is awaited with
cancellation and uses the same family preference/first-address rule.
An explicitly supplied `IPEndPoint` avoids DNS, unless `Device` overrides it.
Disposal cancels and awaits owned startup work as well as the accept loop.

### Source-backed INI map for the factory

Names and conversions below come from the pinned bearer constructors and
`Reticulum._synthesize_interface`, not inferred aliases. Section names become the required `name`
in the generic runtime. Generic enable/outgoing/mode/IFAC settings remain
manager/runtime-owned.

| Bearer | Key | Reference conversion/default | .NET mapping |
| --- | --- | --- | --- |
| Backbone listener | `device` | optional string | `options.Device`; wins over bind host/address |
| Backbone listener | `listen_ip` | optional string, required if no device | hostname constructor; **no default** |
| Backbone listener | `listen_port` | `int`, required unless `port` supplied | constructor port |
| Backbone listener/client | `port` | `int`, overwrites **both** `listen_port` and `target_port` | choose this value first for either role |
| Backbone listener/client | `remote` | string, overwrites `target_host` | normalize before role dispatch |
| Backbone listener/client | `listen_on` | string, overwrites `listen_ip` | normalize before binding |
| Backbone listener/client | `prefer_ipv6` | bool, false | `PreferIPv6` |
| Backbone listener | `block_fast_flapping` | bool, true | `BlockFastFlapping`; **not** `fast_flapping` |
| Backbone listener | `fast_flapping_threshold` | **float seconds**, 20 | `TimeSpan.FromSeconds(GetDouble(...))` |
| Backbone listener | `fast_flapping_grace` | **int**, 5 | `FastFlapGrace` |
| Backbone listener | `fast_flapping_block_time` | **float minutes**, 720 (source default 43200 seconds) | `TimeSpan.FromMinutes(GetDouble(...))` |
| Backbone client | `target_host` | optional string; required for an initiating connection | client host |
| Backbone client | `target_port` | `int`, read when `target_host` is non-null | client port |
| Backbone client | `connect_timeout` | **int seconds**, 5 | `ConnectTimeout`, caveat below |
| Backbone client | `max_reconnect_tries` | **int**, absent means unlimited | nullable `MaximumReconnectAttempts` |
| Backbone client | `i2p_tunneled` | bool, false, parsed/stored but never used in this source | upstream no-op, not a tunneling capability |
| Pipe | `command` | required string, POSIX `shlex.split`, no shell | `CliSupport.ShellSplit` to executable/argv |
| Pipe | `respawn_delay` | **float seconds**, 5 | `TimeSpan.FromSeconds(GetDouble(...))` |
| Both, generic runtime | `bitrate` | **integer**; only values >=5 become configured overrides | `NominalBitrate` / nullable Pipe `ConfiguredBitrate` |

References: `BackboneInterface.py` lines 126–151 and 860–923;
`PipeInterface.py` lines 61–90; `Reticulum.py` lines 929–931, 1029–1038 and
1118–1131.
`GetDouble` must preserve fractional seconds/minutes; do not truncate these
settings through `GetInt`. Native queue capacities, byte bounds, write timeout,
maximum flap entries and optional Pipe spawn-failure limit are library
hardening options, not invented upstream INI keys.

Two source/native distinctions are explicit. The pinned Backbone client
parses `connect_timeout` into `self.connect_timeout`, but `connect()` line 976
uses `INITIAL_CONNECT_TIMEOUT` (5 seconds) instead of the instance field.
Our explicit library `ConnectTimeout` is effective; accepting it from INI
must disclose that bounded-startup native correction rather than claim exact
configured-timeout behavior. Native duration options require positive finite
delays/thresholds and a nonnegative grace; invalid values fail rather than
creating zero-delay respawn loops. The generic parser's >=5 bitrate rule is
separate from the library API's positive-bitrate validation.

Exact upstream dispatch (`Reticulum.py` lines 1118–1131): both
`type=BackboneInterface` and `type=BackboneClientInterface` are accepted.
Normalize the three aliases above first, overwriting canonical fields when
aliases are supplied. `BackboneInterface` selects the initiating client when
the **key** `target_host` exists, not by truthiness; otherwise it selects the
listener. Explicit `BackboneClientInterface` always selects the client.
A missing/empty target for an explicit client must never cause native factory
fallback to a listener. The .NET initiating constructor explicitly rejects a
missing/empty host; upstream can construct an inert unconnected client when
host/port are absent, so that invalid/incomplete-config behavior is not claimed
as exact parity.

### Linux native socket option mapping

`BackboneSocketOptions` uses the public .NET `Socket.SetRawSocketOption` and
`GetRawSocketOption` APIs with native-endian four-byte integer values. These
are AOT-safe native operations with SafeSocketHandle ownership maintained by
the BCL; no unmanaged helper library, unsafe code, dynamic generation or
unsupported-option fallback is used. Linux/Android TCP uses native
`IPPROTO_TCP=6`, `TCP_USER_TIMEOUT=18`, value 24000 ms, matching source
`BackboneClientInterface.set_timeouts_linux` lines 933–938.

The former `(SocketOptionName)18` cast was incorrect: Unix `SetSocketOption`
translates managed option identifiers through the .NET PAL rather than
passing arbitrary integers through as native names. The coordinator's first
Linux run exposed `Operation not supported`; this was a real defect, not an
optional platform feature.

Likewise `.NET SocketOptionName.ReuseAddress` is **not** an exact translation
of upstream's native `SO_REUSEADDR`: the .NET 10 Unix PAL enables both
`SO_REUSEPORT` and `SO_REUSEADDR` for Windows compatibility. That caused two
listeners to bind the same endpoint, invalidating the expected exclusive
listener behavior. Production now explicitly sets native Linux `SOL_SOCKET=1`,
`SO_REUSEPORT=15` to 0 and `SO_REUSEADDR=2` to 1. The original conflicting-bind
assertion remains unchanged. A Linux-only test independently reads back all
three native options, including the disabled port-sharing option.

Mapping audit: .NET runtime `v10.0.0`
[`src/native/libs/System.Native/pal_networking.c`](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_networking.c),
`SystemNative_SetSockOpt` compatibility branch; public raw APIs verified in
[`Socket.cs`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs).
The upstream listener uses only `SO_REUSEADDR`, lines 272–292. These Linux
native constants do not establish evidence for additional OS/architecture
targets; the public Backbone gate still rejects non-Linux/non-Android systems.
Corrected .NET tests and NativeAOT execution remain coordinator-owned.

`Parity6PipeChild.ps1` is an explicitly owned PowerShell 7 child fixture with
bounded binary echo, immediate exit, hex emission/wait and inherited-stderr
modes. Tests locate the source-tree fixture without editing the shared project
file. Independent fixture smoke execution on Windows passed all four modes
(binary output `7E017D5E7E`, exit status 23, process-tree kill and bounded exit).
This is fixture evidence only, not executed C# bearer evidence.

Reference vectors were generated successfully by running:

```powershell
$env:DOTRETICULUM_UPSTREAM = 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
$env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' tests\DotReticulum.Interfaces.Tests\GenerateParity6BackbonePipeVectors.py
```

Result: RNS 1.5.7 imported from that checkout's `RNS\__init__.py`; reference
revision and tracked-source cleanliness verified by `reference_environment`.
The fixture records source-file SHA-256s and actual import path. Backbone
constructor/platform startup is not executed by this vector generator.
Pipe's `reconnect_pipe` is replaced by a disclosed no-op after the reference
decoder consumes owned in-memory stdout; no real reference subprocess or
live-node interoperability is claimed.

The same generator now executes the actual private periodic ingress job for
one controlled iteration and the actual immediate throttle method with fake
queue/epoll/socket descriptors, plus actual child teardown/shared block queries.
On Windows it supplies missing `select.EPOLLIN/EPOLLOUT/EPOLLHUP` constants
solely to the fake registration. Values 1/4/16 were independently read from
real Linux `select` using `wsl.exe --exec python3 -c "import select;
print(select.EPOLLIN, select.EPOLLOUT, select.EPOLLHUP)"`. A controlled
`BaseException` from the second sleep stops the otherwise infinite reference
periodic job. `Transport.remove_interface` is a no-op only for fake teardown.
All shims are recorded in the fixture; these are control-state vectors, not
actual epoll, Linux socket, process or live topology evidence. Regeneration
passed with real global holds 12.25/15.25 seconds for the recorded scenarios.
