# Runtime operations

This page describes how a running DotReticulum node behaves on your system: how programs share one instance, how interfaces are owned and managed while the node runs, how interface discovery and blackhole lists work, what statistics mean, and what is cleaned up and saved over time.

DotReticulum is experimental and is not an official or endorsed Reticulum implementation. The pinned reference is RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`. See [rnsd](rnsd.md) for the full daemon configuration reference, and [TODO.md](../TODO.md) for capabilities that are not implemented or verified.

## Shared instance

As in upstream, the first program to start a Reticulum node on a system creates a shared instance. Only the shared instance owner opens the configured physical interfaces. Other local programs connect to the owner over a local socket and use its interfaces transparently. Sharing is enabled by default.

```
[reticulum]
  share_instance = yes
  instance_name = default

  # Use TCP instead of an abstract Unix socket:
  # shared_instance_type = tcp
  # shared_instance_port = 37428
  # instance_control_port = 37429

  # Only needed when instances cannot read the same configuration directory:
  # rpc_key = e5c032d3ec4e64a6aca9927ba8ab73336780f6d71790
```

| Option | Default | Meaning |
| --- | --- | --- |
| `share_instance` | `yes` | Create or join a shared instance. |
| `instance_name` | `default` | Abstract Unix socket name. Must be nonempty, without path separators. |
| `shared_instance_type` | `unix` where supported, otherwise TCP | `tcp` or `unix`. Abstract Unix sockets need Linux or Android; on other platforms, such as Windows, the instance uses TCP. |
| `shared_instance_port` | `37428` | TCP port for the local data bearer. |
| `instance_control_port` | `37429` | TCP port for the control connection. |
| `rpc_key` | derived | Hexadecimal key for the control connection. |

### How the instance is elected

Election reserves the local bearer before any physical interface factories run, so a local client never opens the owner's configured physical interfaces. If a program requires a shared instance, it fails instead of becoming standalone. Ordinary fallback to standalone operation is diagnosed. A client reconnects to the owner after eight seconds.

### Control connection

The local data bearer uses reference HDLC framing. The separate control connection uses Python multiprocessing byte framing and mutual HMAC authentication around MessagePack, **not pickle**. Both legacy and digest-tagged authentication are supported. Messages, clients and timeouts are bounded, and shutdown is owned.

The default RPC key is the SHA-256 of the owner's persisted internal private transport identity. Processes using a different configuration directory must configure the same explicit `rpc_key`; the runtime never reads another directory's secrets. If the control listener cannot bind, startup fails and the reserved bearer is released.

### Routing for local clients

The owner routes between local clients and physical peers even when network transport is disabled, without adding a network hop for the local client. If the owner disappears, existing links are settled and routes that depended on it are invalidated. On reconnection, registered SINGLE destinations are automatically reannounced as path responses. That reannouncement work is queued and owned, not run as a blocking receive callback or an unobserved task.

## Managing interfaces while running

`RnsdHost` owns its interfaces. You can attach, detach and reload a named interface without stopping unrelated receivers, using `AttachInterfaceAsync`, `DetachInterfaceAsync` and `ReloadInterfaceAsync` or the equivalent management calls. These operations are serialized. They act on one named interface; they do not reload global runtime settings, identities or discovery source policy.

Interface management is on by default and is controlled by `enable_interface_management` in `[reticulum]`.

Where the configuration comes from:

- Management attach and reload reparse the host's source configuration file.
- Direct API calls use the supplied `RnsdConfig`, or the host's original configuration if none is given.

What a reload does:

1. Validates the replacement's discovery bearer support, encryption identity requirement, location-command authorization and positive interval, before touching the old bearer. Endpoint and provider output is still validated at publication time; this step does not run providers.
2. Detaches the old interface.
3. Initializes the replacement. If that fails, the old interface is **not** silently restored.

Results:

| Situation | Result |
| --- | --- |
| Missing configuration or attachment | `null` |
| Interface already attached | `false` |
| Interface management disabled | `false` |
| Attempt to detach the shared-instance bearer | `false` |
| Initialization or disposal error | Exception, with diagnostic logging |

### Ownership inside the manager

`PacketInterfaceManager.AttachAsync` takes ownership of a newly registered interface. If initialization fails, that interface and any peers it accepted are cleaned up. Detach cancels blocked receives, drops queued packets from that source, disposes its peers and joins the receiver work.

Runtime hosts keep their receive channel open while it is temporarily empty. Standalone managers keep their default behaviour of completing when all receivers end.

Autoconnected leaf bearers use explicit failure isolation. An ended receive loop reports the error without completing the daemon channel, and the discovery lease keeps ownership until its offline-grace or detach policy releases it. Faults in ordinary configured top-level interfaces remain terminal.

## Management calls

`CallManagementAsync` validates the request shape before dispatching. It provides:

- source-compatible read queries
- usage, retain and unretain, and identity retention
- blackhole operations
- named attach, detach and reload

Shared clients forward these operations to the owner. Remote identified-link `/status` and `/path` requests are read-only and require an explicitly allowed identity. Unknown PHY values and Python profiler results are `null`. A dropped path is timestamped zero for deferred culling, as in the source.

## Interface discovery

Interface discovery lets a node advertise its interfaces, and lets other nodes find and optionally connect to them. A minimal setup looks like this:

```
[reticulum]
  discover_interfaces = yes
  autoconnect_discovered_interfaces = 2

[interfaces]
  [[Public TCP]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 0.0.0.0
    listen_port = 4242
    discoverable = yes
    reachable_on = node.example.org
```

Discovery is off by default, and `autoconnect_discovered_interfaces` defaults to `0`. Per-interface `announce_interval` is in minutes, defaults to 360 and has a minimum of 5. See [rnsd](rnsd.md) for the full list of discovery options.

### Receiving advertisements

Discovery runs outside the router pump. A bounded validation queue copies only the authenticated announcement's public identity and payload, and queue saturation is diagnosed. Payload integer keys, optional identity encryption, LXMF workblocks and stamps, allowed sources and timestamp and status policy follow `Discovery.py` at the pinned revision.

Advertisements keep their available, unknown or stale state across restart, with one-, three- and seven-day boundaries and bounded capacity. Source and transport policy, duplicate endpoints, configured interfaces and connection limits are applied before attachment.

### Publishing advertisements

An interface is advertised only if it is enabled **and** has `discoverable = yes`. Native TCP and Backbone servers advertise their actual bearer type and bound port; they are never mislabeled as Backbone servers to bypass eligibility.

A successful named attach or reload registers the replacement's metadata, discovery name, `reachable_on`, location provider, encryption and stamp policy and interval, using the current listener's bound port, including an ephemeral port. The first dynamically attached discoverable bearer can start publication even if none was configured at startup. Explicit named attach can still open a disabled configuration entry, but that entry is not advertised. Likewise, clearing `discoverable` on reload leaves the replacement unadvertised.

Per bearer type:

- Connectable I2P advertises its live b32 endpoint.
- RNode advertises its configured frequency, bandwidth, spreading factor and coding rate.
- Weave and KISS advertise the pinned discovery metadata fields. A KISS-framed TCP client publishes the upstream canonical `KISSInterface` type, not a TCP listener endpoint.
- Nonconnectable I2P and non-KISS TCP clients are rejected explicitly.
- Radio advertisements need no TCP host or port, and endpoint providers do not run for them.

These map and field contracts have independently executed pinned-source vectors. They do not prove RF reception or physical peer discovery.

The pinned runtime parses `discovery_modulation` as an integer, but its KISS publisher then calls string sanitization on it and fails. Native publication diagnoses that combination rather than inventing a wire value. Omitted KISS modulation stays null. Weave's unconfigured channel stays null, matching the pinned defaults.

### Publication timing

A new registration becomes eligible at the next discovery scheduler job; attach and reload do not publish synchronously. The scheduler considers at most one due interface per 60-second job. Later publications use the registered interval. Metadata providers run at publication time. A provider failure is diagnosed and aborts that publication; stale metadata is not substituted.

Detach unregisters the interface and waits for any in-flight advertisement before returning, so reload does not leave a callback to the removed listener or its old metadata. Cancellation is honoured while waiting to unregister. Once unregistered, bearer removal finishes without caller cancellation, so it cannot leave an active bearer whose publication ownership has already been removed.

### Metadata commands

Discovery executable metadata, such as `location_cmd`, is denied by default; see `rnsd --allow-discovery-commands`. The shared process runner runs a single executable with no shell or arguments, an eight-second limit and at most 4096 bytes of combined output. Coordinates and hostnames are validated. Failed providers abort that publication and produce a diagnostic.

### Autoconnect

Upstream autoconnect eligibility is Backbone-only, including its implementation and version policy. Unverified implementations require the explicit source-compatible opt-in, `autoconnect_unverified_implementations`.

- On Linux and Android, autoconnect uses the native Backbone client with scoped DATA-pressure and egress and flap controls.
- Other platforms use the explicitly compatible bounded TCP/HDLC adapter, with a five-second initial connection deadline. This is not a claim of native Backbone support on those platforms.

Active connections release bootstrap-only interfaces. Offline removal and bootstrap restoration keep explicit leases. Published IFAC credentials configure authentication before the bearer is attached. Gravity and internal-announcement overrides use the same operational controls as configured interfaces, and reconnect keeps that configuration.

## Blackhole lists

A blackhole list makes your node drop announces, paths and identified links belonging to specific identities. You can maintain a local list, publish it, and fetch lists from sources you trust:

```
[reticulum]
  publish_blackhole = yes
  blackhole_sources = 9fb6d773498fb3feda407ed8ef2c3229
  blackhole_update_interval = 60
```

`publish_blackhole` defaults to `no`. `blackhole_update_interval` is in minutes, defaults to 60 and has a minimum of 2.

How lists are handled:

- Each entry keeps the identity, expiry, reason, claimed source and authenticated obtained-from provenance.
- Local entries take precedence.
- Updates require the configured source identity. Failed or empty responses do not erase valid entries.
- Only locally authored entries are published, at the identified `/list` endpoint.
- Binary map keys are preserved across packet and resource responses.
- Publication and periodic fetches run outside the router pump.
- Expiry and restart reload preserve source policy.

When the list changes, associated paths are removed and already identified blocked links are closed. Blocked announces and new identified links are rejected. Anonymous datagrams cannot reveal a sender identity, so no identity-based sender rejection is claimed for them.

## Statistics

`RuntimeStatistics.Collect` takes detached snapshots, including accepted TCP peers and their parent aggregates. A parent keeps its cumulative traffic after a peer disconnects. Global bearer totals and the separately named decoded packet totals count parents only, so children are not counted twice. Global bearer totals are `null` if any top-level extension cannot report its actual byte boundary.

### Byte counters

| Counter | Meaning |
| --- | --- |
| `rxb` | Delivered, decoded receive bytes. |
| `txb` on HDLC bearers | Successfully written framed bytes, including flags and escape expansion. This matches the asymmetric TCP reference counters (`TCPInterface.py:305-331`). |
| UDP `rxb`/`txb` | Actual datagram bytes. |
| Extension bearers | `null`, unless the bearer implements `IPacketInterfaceDiagnostics`. |
| `dotreticulum_decoded_rxb`, `dotreticulum_decoded_txb` | Decoded packet-boundary counters. |

### Errors and queues

Malformed decoded packets, failed sends and their attempted bytes, pending sends and actual backpressure waits have separate counters. Failed sends are not labelled physical drops. Malformed HDLC frames discarded before the packet boundary are not included in the decoded-packet violation counter.

The manager's bounded inbound queue uses wait-on-full backpressure. The router separately reports its actual data, announce, path-request and ingress-limited queue depths, rejections and capacity pressure. These are distinct from manager backpressure, and are not invented per-priority queue measurements. See [transport controls](transport-controls.md).

### Physical and estimated values

PHY values such as RSSI, SNR and quality, and device or queue controls that a bearer cannot measure, are absent or `null`, never fabricated zeros. Optional bearer diagnostics can supply actual additional measurements.

The packet manager measures IFAC rejections, size, public network name and signature, reported alongside configured bitrate, hardware MTU and MTU-discovery capability. An IFAC rejection count of zero is an observed counter, not an unavailable PHY measurement. Passphrases and derived private keys are never statistics fields.

Nominal bitrates are labelled as estimates or configuration, not PHY throughput measurements:

- TCP and UDP reproduce the reference's 10,000,000 bit/s guess.
- Discovered Backbone connections use its 5,000,000 bit/s configured estimate.
- Serial ports report their configured baud. A supplied stream has no invented baud.

Profiling is opt-in. It reports actual process RSS, CPU time, GC heap, allocated bytes and thread count under .NET-specific field names. These are not presented as Python profiler results.

## Persistence and housekeeping

The shared owner restores public state before opening management RPC. It saves on graceful shutdown and every twelve hours. Restored paths require an attached stable interface hash, a matching validated cached announce, and current expiry and blackhole policy. Links and disconnected TCP children are never resurrected.

Known-destination cleanup and scoped cache cleanup run every fifteen minutes. Only hash-named regular files are eligible, with a one-day lifetime for resources and seven days for the packet cache. Symlinks and unrelated files are not followed or deleted.

See [runtime state](runtime-state.md) for formats, corruption handling, opt-in local ratchets, proof-cache age and authenticated dormant-tunnel restoration.

## Custom interfaces

Upstream can load external interface modules from user `.py` files (`Reticulum.py:1185-1207`). DotReticulum does **not** load or execute user `.py` files, assemblies, arbitrary type names or reflection-discovered plug-ins at runtime.

Instead, a NativeAOT extension is a statically compiled `IPacketInterface` implementation, registered explicitly through an `InterfaceFactoryRegistry`:

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

The matching configuration entry uses the registered type:

```
[interfaces]
  [[My Interface]]
    type = MyBearer
    enabled = yes
    endpoint = example
```

`MyBearer` is supplied by your application. It must implement the normal `IPacketInterface` lifecycle, idempotent disposal, cancellation and bounded I/O contracts.

Each factory declares its allowed option names. The common type, enable and mode keys are also accepted. Unknown options, unregistered types, duplicate registrations and factories that return no interface are diagnosed. Factories are called only after parsing and validation, and have no global discovery mechanism. The stock executable contains no user extension factories; to use one, your application must compile and register it, then publish its own native executable.

## Node lifetime

`ReticulumNode` exposes `Completion`, so unexpected processing faults stay observable. Disposal cancels processing and always disposes the owned host, including after a processing or startup fault. A node is terminal after disposal or failed initialization; construct a new node to restart.

Identity names passed to `LoadOrCreateIdentity` are relative ASCII file paths beneath storage. Rooted paths and traversal segments are rejected. Existing tool identities keep their `storage/identities/<tool>` locations.

## Differences from upstream

- External interfaces are statically compiled and explicitly registered. User `.py` files and runtime plug-ins are not loaded.
- The control connection carries MessagePack, not pickle.
- Discovery executable metadata is denied by default and must be explicitly allowed. Windows native executable support is a deliberate extension of upstream's Unix-only execution.
- Autoconnect outside Linux and Android uses a compatible bounded TCP/HDLC adapter rather than the native Backbone client.
- KISS publication with an integer `discovery_modulation` is diagnosed instead of failing as the pinned runtime does.
- Statistics use `null` for values a bearer cannot measure, and add separately named decoded-packet and .NET profiling fields.

## Limitations

- Discovery vectors do not prove RF reception or physical peer discovery.
- Actual Python Backbone execution is platform-specific evidence, separate from the controlled compatible HDLC adapter.
- Anonymous datagrams cannot be rejected by sender identity.
- The Linux and Android native Backbone path has no Android verification; Android is outside the roadmap.
- A failed reload does not restore the previous interface.

## Verification

`tests/DotReticulum.Applications.Tests/RuntimeOperationsTests.cs`, `SharedRuntimeTests.cs`, `SharedHostIntegrationTests.cs` and `RuntimeStateStoreTests.cs` cover owned interfaces, shared-instance behaviour, discovery, persistence and failure handling.

Windows x64 and Linux x64 are exercised. macOS ARM64 builds and passes the managed suite and the NativeAOT resource test. Android and other platforms outside these CI targets are outside the roadmap and remain unverified. Desktop RNode BLE and other missing or partial providers are tracked in [TODO.md](../TODO.md).
