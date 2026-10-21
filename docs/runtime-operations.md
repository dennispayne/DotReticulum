# Native runtime operations

The runtime is experimental and does not establish complete RNS parity. The
source baseline is RNS 1.5.7, commit
`a29bfb51c468945c96a1ee22cae0114e993e2391`. See [rnsd](rnsd.md) for
configuration and [development history](../.github/development-history.md) for
the exact exercised platforms and reference flows.

## Interface ownership and extensions

`RnsdHost` owns its interfaces. `AttachInterfaceAsync`, `DetachInterfaceAsync`
and `ReloadInterfaceAsync` serialize configuration mutations without stopping
unrelated receivers. A reload detaches before attaching the replacement, like
the reference; if replacement initialization fails, the old interface is not
silently restored. Missing configuration/attachment returns `null`; an already
attached interface returns `false`. Initialization and disposal errors are
explicit exceptions with diagnostic logging.

`PacketInterfaceManager.AttachAsync` transfers ownership of a newly registered
interface. Failed initialization cleans up that interface and any accepted
peers. Detach cancels blocked receives, removes queued packets from that
source from delivery, disposes its peers and joins the receiver work. Runtime
hosts keep their receive channel open when temporarily empty. Standalone
managers retain their default complete-when-all-receivers-end behavior.
Autoconnected leaf bearers use explicit failure isolation: an ended receive
loop reports the error without completing the daemon channel, and the discovery
lease retains ownership until its offline-grace/detach policy releases it.
Ordinary configured top-level interface faults remain terminal.

Python's external-interface contract loads a user `.py` file and executes it
(`Reticulum.py:1185-1207`). DotReticulum deliberately does **not** load scripts,
assemblies, arbitrary type names or reflection-discovered plug-ins at runtime.
NativeAOT extensions are statically compiled `IPacketInterface` implementations,
registered explicitly through an `InterfaceFactoryRegistry`:

```csharp
var factories = new InterfaceFactoryRegistry();
factories.Register("MyBearer", ["endpoint"], configuration =>
    new MyBearer(configuration.Get("endpoint")
        ?? throw new FormatException("MyBearer requires endpoint.")));
var configuration = RnsdConfigParser.Parse(configText, factories);
await using var node = ReticulumNode.Create(configuration, configDirectory,
    extensions: factories);
await node.StartAsync(cancellationToken);
```

`MyBearer` in this example is application-supplied and must implement the
normal `IPacketInterface` lifecycle, idempotent disposal, cancellation and bounded-I/O contracts.
Its configuration uses `type = MyBearer` and `enabled = yes`. Each factory
declares its allowed option names; common type/enable/mode keys are also
accepted. Unknown options, unregistered types, duplicate registrations and
factories returning no interface are diagnosed. Factories are called only
after parsing/validation and own no global discovery mechanism. The stock
executable contains no user extension factories; an application must compile
and register its extensions before publishing its own native executable.

## Statistics boundaries

`RuntimeStatistics.Collect` takes detached snapshots, including accepted TCP
peers and parent aggregates. A parent retains cumulative traffic after a peer
disconnects. Global bearer and separately named decoded packet totals count
parents only, avoiding double-counting their children. Global bearer totals
are `null` if any top-level extension cannot report its actual byte boundary.

Native bearer `rxb` counts delivered, decoded receive bytes. HDLC bearer `txb`
counts successfully written framed bytes, including flags and escape
expansion, matching the asymmetric TCP reference counters
(`TCPInterface.py:305-331`). UDP counts actual datagram bytes. Unknown extension
bearer counters remain `null` unless it implements
`IPacketInterfaceDiagnostics`; decoded packet-boundary counters are separately
named `dotreticulum_decoded_rxb` and `dotreticulum_decoded_txb`.

Malformed decoded packets, failed sends and their attempted bytes, pending
sends and actual backpressure waits have separate counters. Failed sends are
not labeled physical drops. The bounded inbound queue uses wait-on-full
backpressure, not reference priority queues; its capacity, current depth and
pressure are exposed without inventing per-priority queue measurements.
Malformed HDLC frames discarded before the packet boundary are not included
in the decoded-packet violation counter.

PHY values (RSSI/SNR/quality), IFAC violations and device/queue controls that
the bearer cannot measure are absent or `null`, never fabricated zeros.
Optional bearer diagnostics can supply actual additional measurements.
Profiling is opt-in and reports actual process RSS, CPU time, GC heap,
allocated bytes and thread count under `.NET`-specific field names; these are
not represented as Python profiler results.
TCP/UDP nominal bitrates reproduce the reference's 10,000,000-bit/s guess;
discovered Backbone connections use its 5,000,000-bit/s configured estimate.
Serial ports expose their configured baud, while a supplied stream has no
invented baud. These are explicitly labeled estimates/configuration, not PHY
throughput measurements.

## Shared ownership and management

Sharing is enabled by default. Election reserves the local bearer before any
physical factories run; a local client never opens the owner's configured
physical interfaces. Required-shared mode fails instead of becoming standalone;
ordinary fallback is diagnosed. TCP is available on Windows; named abstract
Unix sockets require the corresponding platform. Default ports are 37428 and
37429, and reconnect delay is eight seconds.

The local bearer uses reference HDLC. The separate control connection uses
Python multiprocessing byte framing and mutual HMAC authentication around
MessagePack, **not pickle**. Both legacy and digest-tagged authentication are
supported, with bounded messages, clients, timeouts and owned shutdown. The
default RPC key is SHA-256 of the owner's persisted internal private transport
identity. Processes using a different configuration directory must configure
the same explicit RPC key; the runtime never reads another directory's secrets.
A control-listener bind failure fails startup and releases the reserved bearer.

`CallManagementAsync` validates request shape before dispatching. It provides
source-compatible read queries, usage/retain/unretain/identity retention,
blackhole operations and named attach/detach/reload. Shared clients forward
these operations to the owner. Remote identified-link `/status` and `/path`
requests are read-only and require an explicitly allowed identity. Unknown PHY
and Python-profiler results are `null`. A dropped path is timestamped zero
for deferred culling, as in the source.

An owner also routes between local clients and physical peers when network
transport is disabled, without introducing a local network hop. Owner
disappearance settles existing links and invalidates owner-dependent routes;
reconnection automatically reannounces registered SINGLE destinations as path
responses. Reannouncement work is queued and owned, not a blocking receive
callback or an unobserved task.

## Discovery and blackhole services

Discovery runs outside the router pump. A bounded validation queue copies only
the authenticated announcement's public identity and payload; queue saturation
is diagnosed. Payload integer keys, optional identity encryption, LXMF
workblocks/stamps, allowed sources and timestamp/status policy follow
`Discovery.py` at the pinned revision. Advertisements retain available/unknown/
stale states across restart, with one-/three-/seven-day boundaries and bounded
capacity. Source/transport policy, duplicate endpoints, configured interfaces
and connection limits are applied before attachment.

Native TCP servers advertise their actual bearer type and bound port; they
are never mislabeled Backbone servers to bypass eligibility. Upstream
autoconnect eligibility is Backbone-only, including its implementation/version
policy. The bounded native client adapter speaks that HDLC stream with a
five-second initial connection deadline; this is not a claim of full Backbone
configuration/server/platform parity. Unverified implementations require the
explicit source-compatible opt-in. Active connections release bootstrap-only
interfaces; offline removal and bootstrap restoration retain explicit leases.
IFAC credentials and unimplemented gravity/internal-announcement controls fail
explicitly rather than creating an unprotected or misconfigured connection.

Discovery executable metadata is default-deny; see `rnsd
--allow-discovery-commands`. The shared owned process runner uses a single
executable, no shell/arguments, eight seconds and 4096 combined output bytes.
Windows native executable support is a deliberate extension of upstream's
Unix-only execution. Coordinates and hostnames are validated; failed providers
abort that publication and produce a diagnostic.

Blackhole state retains identity, expiry, reason, claimed source and authenticated
obtained-from provenance. Local entries take precedence. Updates require the
configured source identity; failed/empty responses do not erase valid entries.
Only locally authored entries are published at the identified `/list` endpoint.
Binary map keys are preserved across packet and resource responses. Publication
and periodic fetches run outside the router pump. Expiry and restart reload
preserve source policy. Changes remove associated paths and close already
identified blocked links; blocked announces and new identified links are
rejected. Anonymous datagrams cannot reveal a sender identity and are not
claimed to have identity-based sender rejection.

## Persistence and housekeeping

The shared owner restores public state before opening management RPC, and saves
on graceful shutdown and every twelve hours. Actual paths require an attached
stable interface hash, matching validated cached announce and current expiry/
blackhole policy. Links and disconnected TCP children are never resurrected.
Known-destination cleanup and scoped cache cleanup run every fifteen minutes:
only hash-named regular files are eligible, with one-day resource and seven-day
packet-cache lifetimes. Symlinks and unrelated files are not followed/deleted.
See [runtime-state](runtime-state.md) for atomic formats, corruption failures,
opt-in local ratchets and the tunnel-codec versus Parity 4 protocol boundary.

## Lifetime

`ReticulumNode` exposes `Completion` so unexpected processing faults remain
observable. Disposal cancels processing and always disposes the owned host,
including after a processing/startup fault. A node is terminal after disposal
or failed initialization: construct a new node for a restart. Identity names
passed to `LoadOrCreateIdentity` are relative ASCII file paths beneath storage;
rooted paths and traversal segments are rejected. Existing tool identities
retain their `storage/identities/<tool>` locations.
