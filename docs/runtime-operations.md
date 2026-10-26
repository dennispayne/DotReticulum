# Runtime operations

DotReticulum provides native .NET runtime services for interface ownership, shared-instance operation, discovery, management and persistence. It is experimental and is not an official or endorsed Reticulum implementation. The pinned reference is RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`. See [rnsd](rnsd.md) for daemon configuration and [TODO.md](../TODO.md) for capabilities that are not implemented or verified.

## Interface ownership and extensions

`RnsdHost` owns its interfaces. `AttachInterfaceAsync`, `DetachInterfaceAsync`
and `ReloadInterfaceAsync` serialize configuration mutations without stopping
unrelated receivers. Management attach/reload reparses the host's source config
file; direct API calls use the supplied `RnsdConfig`, or the host's original
configuration when omitted. These are named interface operations, not a reload
of global runtime settings, identities or discovery source policy.

A reload validates the replacement's supported discovery bearer, encryption
identity requirement, location-command authorization and positive interval before
removing the old bearer. Endpoint/provider output validation still occurs at
publication time; this prevalidation does not execute providers.
Reload then detaches before initializing the replacement;
if replacement initialization fails, the old interface is not silently restored.
Missing configuration/attachment returns `null`; an already attached interface
returns `false`. Disabled interface management and attempts to detach the
shared-instance bearer return `false`. Initialization and disposal errors are
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

Unlike the upstream external-interface contract (`Reticulum.py:1185-1207`), DotReticulum does **not** load or execute user `.py` files, assemblies, arbitrary type names or reflection-discovered plug-ins at runtime.
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
not labeled physical drops. The manager's bounded inbound queue uses wait-on-full backpressure. The router
also exposes its actual data/announce/path-request/ingress-limited queue
depths, rejections and capacity pressure; these are distinct from manager
backpressure, not invented per-priority queue measurements.
Malformed HDLC frames discarded before the packet boundary are not included
in the decoded-packet violation counter.

PHY values (RSSI/SNR/quality) and device/queue controls that
the bearer cannot measure are absent or `null`, never fabricated zeros.
The packet manager now measures IFAC rejections, size, public network name and
signature; these are reported alongside configured bitrate, hardware MTU and
MTU-discovery capability. An IFAC rejection count of zero is an observed manager
counter, not an unavailable PHY measurement. Passphrases and derived private
keys are never statistics fields.
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

Native TCP and Backbone servers advertise their actual bearer type and bound port; they
are never mislabeled Backbone servers to bypass eligibility. Publication requires
both interface enablement and `discoverable=yes`. A successful named attach/reload
registers the replacement's metadata, discovery name, `reachable_on`, location
provider, encryption/stamp policy and interval, using the current listener's bound
port (including an ephemeral port). The first dynamically attached discoverable
bearer can initialize publication even if none was configured at startup.
Explicit named attach retains its existing ability to open a disabled
configuration entry, but that entry is not advertised. Likewise, clearing
`discoverable` on reload leaves the replacement bearer unadvertised.

Connectable I2P advertises its live b32 endpoint; RNode advertises configured
frequency/bandwidth/spreading factor/coding rate. Weave and KISS advertise
the pinned discovery metadata fields; a KISS-framed TCP client publishes the
upstream canonical `KISSInterface` type rather than a TCP listener endpoint.
Nonconnectable I2P and non-KISS TCP clients are rejected explicitly. Radio
advertisements need no TCP host/port, and endpoint providers do not run for
them. These map/field contracts have independently executed pinned-source
vectors; they do not prove RF reception or physical peer discovery. The
pinned runtime parses `discovery_modulation` as an integer, but its KISS
publisher calls string sanitization on it and fails. Native publication
diagnoses that combination rather than inventing a wire value; omitted
KISS modulation remains null. Weave's unconfigured channel remains null,
matching the actual pinned defaults.

Detach unregisters and waits for any in-flight advertisement before returning;
reload does not leave a callback to the removed listener or its old metadata.
Cancellation is honored while waiting to unregister; once unregistered, bearer
removal finishes without caller cancellation so it cannot leave an active bearer
with its publication ownership already removed.
Each new registration is eligible at the next discovery scheduler job, not
published synchronously by attach/reload. The scheduler considers at most one due
interface per 60-second job; subsequent publications use the registered interval.
Metadata providers run at publication time; provider failures are diagnosed and
abort that publication, not silently replaced with stale metadata.

Upstream autoconnect eligibility is Backbone-only, including its implementation/version
policy. Linux/Android uses the native Backbone client with scoped DATA-pressure
and egress/flap controls. Other platforms retain the explicitly compatible bounded
TCP/HDLC adapter with a five-second initial connection deadline, not a claim
of native Backbone platform support. Unverified implementations require the
explicit source-compatible opt-in. Active connections release bootstrap-only
interfaces; offline removal and bootstrap restoration retain explicit leases.
Published IFAC credentials configure authentication before the bearer is
attached. Gravity and internal-announcement overrides use the same operational
controls as configured interfaces; reconnect retains that configuration.
Actual Python Backbone execution remains platform-specific evidence, separate
from a controlled compatible HDLC adapter.

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
opt-in local ratchets, forced proof-cache age and authenticated dormant-tunnel
restoration. Transport priority queues are separate from the packet manager's
backpressure channel; see [transport controls](transport-controls.md).

## Lifetime

`ReticulumNode` exposes `Completion` so unexpected processing faults remain
observable. Disposal cancels processing and always disposes the owned host,
including after a processing/startup fault. A node is terminal after disposal
or failed initialization: construct a new node for a restart. Identity names
passed to `LoadOrCreateIdentity` are relative ASCII file paths beneath storage;
rooted paths and traversal segments are rejected. Existing tool identities
retain their `storage/identities/<tool>` locations.
## Verification and platform scope

`tests/DotReticulum.Applications.Tests/RuntimeOperationsTests.cs`, `SharedRuntimeTests.cs`, `SharedHostIntegrationTests.cs` and `RuntimeStateStoreTests.cs` cover owned interfaces, shared-instance behavior, discovery, persistence and failure handling. Windows x64 and Linux x64 are exercised. macOS ARM64 builds and passes the managed suite and NativeAOT resource test, but the Darwin rnsh terminal backend is unavailable. Android, desktop RNode BLE and other unverified providers are listed in [TODO.md](../TODO.md).
