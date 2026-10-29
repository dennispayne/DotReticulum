# I2P interface

The native I2P interface connects to an operator-provided I2P router through its SAM 3.1 API. It does not launch, install, or configure i2pd or Java I2P. The interface uses SAM stream sessions and exposes each Reticulum stream as an independent packet-interface child.

## Router provisioning

Install and run an I2P router separately, and enable its SAM 3.1 service on a local endpoint. The conventional endpoint is `127.0.0.1:7656`; the native API also honors `I2P_SAM_ADDRESS=host:port` when `SamHost` is unspecified. The SAM endpoint must be reachable by the process running DotReticulum. SAM availability alone does not mean that the router has outbound tunnels, a usable lease set, or reachability to a remote destination.

For overlay traffic, allow the router to bootstrap normally and establish client tunnels/lease sets. An isolated, newly started router may answer SAM HELLO while remaining unable to resolve or connect to destinations. Public-network tests are opt-in and must use operator-authorized network access. The owned topology runner requires explicit public-network authorization and preserves reseed certificate verification. Seeding public RouterInfo records is only a bootstrap aid: it is not a fresh bootstrap, does not seed private destinations or leases, and does not guarantee reachable peers.

The native bearer does not modify router configuration. In i2pd 2.59.0, `sam.singlethread=false` was used in some controlled tests to avoid an observed destination-cleanup hazard; it is non-default and did not make native post-restart stream recovery pass. It is not a required setting for normal use. Neither that test setting nor a SAM STATUS OK response proves Reticulum stream delivery.

## Configuration

In an interface section, use `type = I2PInterface`; the section name is the interface `name`. Supported type-specific keys are:

| Key | Default | Meaning |
| --- | --- | --- |
| `connectable` | false | Create a server destination and accept incoming SAM streams |
| `peers` | empty | Outbound I2P destination tokens/addresses to connect |
| `bitrate` | 256000 bit/s | Generic configured estimate; values below 5 are ignored upstream |

The runtime supplies `storagepath` and the current transport identity hash. Generic `ifac_size` is measured in bits and converted to bytes; I2P's default is 16 bytes. IFAC and routing policies are applied to child interfaces by the manager.

Native library options additionally permit explicit `SamHost`/`SamPort`, a 30-second setup timeout, 15-second reconnect delay, receive queue capacity 256 per child, 64 incoming and 64 outgoing peers, and a finite reconnect-attempt limit. These are native API controls, not extra INI keys. `SamHost = null` selects the environment/default endpoint; the API default is `127.0.0.1:7656`.

## Destination storage and identity

The bearer sends SAM `HELLO VERSION MIN=3.1 MAX=3.1`, requests Ed25519 destinations (`SIGNATURE_TYPE=7`), creates `STYLE=STREAM` sessions, and uses SAM naming lookup/stream connect for configured peers. Connectable destination private data is persisted under `storagepath/i2p/`. Existing legacy filename derivation is preferred; otherwise the filename is based on the interface name and active 16-byte transport identity hash. The same key is reused after process restart. The published address is available as `Base32Address` with `.b32.i2p` suffix.

Unix key files are created owner-read/write only and published without overwriting an existing file. Operators on Windows must protect the storage directory with appropriate ACLs. Existing corrupt or oversized key files are reported, not silently replaced. Private destination strings and SAM credentials are never included in diagnostics or fixtures; managed immutable strings cannot be reliably zeroed.

## Behaviour and limits

The parent does not send packet data; it owns SAM sessions and publishes independently identified peers. Initiating peers reconnect by default every 15 seconds; accepted peers do not reconnect. Keepalive flags are emitted after ten seconds without packet writes, peers are stale after 20 seconds, and reads time out after 110 seconds. A failed data stream is isolated from other peers. Loss of the session control socket retires and recreates the session. A pending stream setup uses one shared bounded setup deadline rather than waiting indefinitely.

Frames use HDLC, with a 1064-byte hardware MTU and 256 kbit/s default estimate. The default IFAC size is 16 bytes. Receive queues and peer counts are bounded; malformed SAM lines, invalid destination metadata, malformed frames, and oversized input are rejected. A canceled or failed packet write retires that stream to avoid joining a partial frame to a later packet. Disposing the bearer closes only its owned SAM sockets; it does not stop the I2P router.

### SAM protocol and session lifetime

Each SAM control/data connection negotiates version 3.1. A connectable endpoint issues `SESSION CREATE STYLE=STREAM`; an initiator performs naming lookup and `STREAM CONNECT`, and accepted streams receive `STREAM ACCEPT`. The SAM parser handles fragmented lines and preserves packet bytes coalesced after the destination line. Session control sockets stay open while their streams are active. SAM `INVALID_ID`, a failed control socket, or incomplete session creation retires that session; ordinary stream EOF or a peer-specific connect failure does not by itself discard an otherwise healthy initiator session. Keepalive is an empty HDLC flag pair, not a Reticulum packet, and does not increase packet counters.

Private destination generation uses SAM Ed25519 type 7. The public destination ends at the certificate length field (offsets 385–386). Legacy key names derive from `SHA256(SHA256(UTF8(name)))`; otherwise the derivation is `SHA256(SHA256(UTF8(name)) || SHA256(transport_identity_hash))`. Existing legacy files are preferred for compatibility. Storage uses a create-new staging file and non-overwriting publication; malformed or oversized existing destination files fail closed.

The native setup timeout (30 seconds by default) is one budget for destination generation, session creation, naming, and stream connect. The pinned Python SAM calls can wait for replies without an application timeout. Native timeout cancels a pending data attempt but retains an already-ready session; this is a bounded recovery difference, not a guarantee that a router will complete a stream. Incoming SAM tokens and public destination structures are untrusted input and are validated before publishing a peer.

### Router topology and authorization

A router isolated to loopback can be used for SAM persistence checks, but a fresh router without usable floodfills/lease-set discovery cannot prove STREAM delivery. i2pd requires an actual transport service object; the owned isolated i2pd topology uses a real loopback NTCP2 listener and disables unrelated external transports, proxy/control services, subscriptions, time sync, UPnP, and transit. The core isolation settings are IPv4 loopback binding, IPv6 disabled, `reservedrange=false`, non-main `netid=222`, published loopback NTCP2 on an owned port, and SSU2 disabled. This is test-runner isolation, not a host firewall or OS namespace boundary. It does not create tunnels or lease sets.

Public-overlay acceptance requires explicit operator authorization, a public I2P network ID, usable router bootstrap, and the router's public certificate directory. Reseed signature verification remains enabled; missing certificates are not bypassed. The runner's optional `SeedRouterInfoDirectory` accepts only public `routerInfo-<hash>.dat` files in the standard `r<first-hash-character>` layout. It does not copy router identity, private destinations, profiles, or lease sets. A seeded topology must be described as seeded, not as fresh bootstrap. Java I2P testing uses its internal SAM bridge; external I2CP is disabled, transit participation is disabled, and default three-hop client tunnels are retained. The test runner does not install or launch a router automatically.

### Java I2P test provider

The opt-in topology runner can use either an already-obtained i2pd executable or an already-extracted official Java I2P distribution; it does not download, install, or start a service. The Java branch starts the foreground `net.i2p.router.Router` entry point and enables only its in-process SAM bridge. External I2CP is disabled, proxy/tunnel client applications are not started, and no zero-hop session options are injected. Client tunnel defaults remain three hops inbound and outbound, variance zero.

The Java test profile disables UPnP and router transit (`router.sharePercentage=0`, `router.maxParticipatingTunnels=0`), assigns owned NTCP and UDP transport ports, disables the external I2CP interface and local shortcut, and requires the distribution's public `hosts.txt` and certificate data. Reseed signature validation remains enabled. On Linux, the Java 2.14.0 portable-mode path can recurse while creating temporary context before a global context exists; the runner uses an existing owned configuration directory rather than portable mode. These settings describe the test harness, not native bearer configuration.

The runner's opt-in `-JavaLocalDestinationRouting` retains Java I2P's normal
local-to-local destination delivery. Java I2P 2.14.0
`router/java/src/net/i2p/router/client/ClientManager.java:85-86,462-501` defines
`i2cp.disableLoopback`: when true, even two destinations on the same router must
send through public tunnels; otherwise the router delivers to its active local
destination. This option changes router topology, not the SAM client, RNS source,
framing, packet assertions, or client tunnel lengths. A successful local-route
test is actual-router stream evidence only, not public-overlay recovery.

Use `-NativeRestartOnly` to select the existing native bidirectional DATA and
persisted-bearer restart case without unrelated initial mixed-peer cases. Its
unchanged four-minute deadline, persisted-key comparison, stable initiating child,
and exact packet assertions still apply. `-ReferenceRestartOnly` separately
selects the pinned-Python process-restart counterexample.

Use `-PublishedFeatureOnly` with `DOTRETICULUM_NATIVE_EXE` pointing to an existing
published executable to run cold-start NativeAOT `rnprobe` acceptance. This
selects the Applications test project and requires explicit
`-AllowPublicNetwork`; it does not require Python. The receiver announces only
after each actual I2P stream connects. Two candidates have isolated fresh state;
the third process reuses the first candidate's configuration/storage and must
find the previous valid path in storage. This establishes bounded leaf
persisted-candidate startup readiness, not transport-enabled recovery.

For concurrent development, first copy the complete Release test-output directory
into an owned directory under `TestResults`, then supply its
`DotReticulum.Interfaces.Tests.dll` via `-TestAssemblyPath`. The runner invokes
`dotnet vstest` on that snapshot instead of building or locking shared project
outputs. Do not combine it with `-NoBuild`. Test results and router state remain
unique to each topology run; child processes do not inherit `GITHUB_STEP_SUMMARY`.
Retained router artifacts are private: Java router diagnostics can include
session-key material. Do not commit or publish raw router logs or state.

## Differences from upstream

The .NET implementation uses SAM data sockets directly rather than spawning Python tunnel proxy listeners and threads. It adds bounded queues, peer caps, explicit setup deadlines, finite optional retries, cancellable shutdown, and strict malformed-frame rejection. Native initiators retain a ready SAM session through peer-specific refusal, timeout, or ordinary stream EOF; actual session-control failures recreate it. Upstream tunnel status handling can stop/rebuild a tunnel after a reported setup failure. IFAC size is normalized to the documented 16-byte default rather than reproducing a nullable constructor-order edge in upstream initiators.

## Platform notes

The SAM protocol is implemented using .NET sockets and filesystem APIs. Windows x64 and Linux x64 managed suites are exercised. SAM router availability, local firewall configuration, filesystem permissions, I2P overlay readiness, and network reachability remain operator responsibilities. macOS ARM64 build/test execution does not constitute live I2P interoperability; other platforms are unverified.

## Verification

`Parity6I2pTests` covers SAM parsing, session lifetime, key persistence, reconnect,
peer isolation, bounded queues, and pinned-source vectors using controlled
simulators. `Parity6I2pLiveTests` is opt-in against an actual SAM router;
`Parity6I2pReferenceRecoveryTests` separately exercises actual pinned-Python
process restart.

On 2026-10-29, Windows x64 Java I2P 2.14.0 with its default local-destination
routing passed the unchanged managed native-bearer restart case in 65.8 seconds: exact DATA
packets traveled both directions before and after the connectable bearer was
disposed/recreated, its persisted key bytes and address were unchanged, and the
initiating child survived/reconnected. The router process stayed running.
This is actual same-router SAM/stream delivery, not a simulator, SAM-readiness
assertion, router restart, or public-overlay/remote-router acceptance.
The pinned Python-only process-restart case independently passed the same
local-routing topology in 95.7 seconds, checking actual destination callbacks both
directions before/after server-process termination and persisted restart, with
the original initiator process surviving. It is an upstream baseline, not a
mixed-peer or NativeAOT restart claim.

The same unchanged native case with Java local delivery disabled failed after
four minutes on 2026-10-29. Initial DATA passed in both directions; after restart
the replacement destination was SAM-ready with the same persisted key/address,
but no accepted peer arrived. The surviving initiator timed out during
`STREAM CONNECT` after its existing 90-second setup budget. Router diagnostics
showed cached-lease reuse and garlic decryption/unknown-session-tag failures after
restart. That locates observed failure below the managed packet/framing path;
it does not yet prove which router cache or recovery policy must change.
The pinned Python-only baseline with the same forced-tunnel setting also failed
at its unchanged four-minute deadline after initial bidirectional callbacks:
the replacement server reused its I2P key, transport identity, and address, but
never acquired an accepted peer. No .NET bearer was involved in that baseline.
This is a concrete router/topology recovery blocker shared by the tested
implementations, not evidence that a native-only framing or persistence change
would fix public-overlay recovery.

On 2026-10-29, the published Windows x64 executable passed the cold-start case
in 63 seconds against actual Java I2P 2.14.0 with local-destination routing.
Two independently initialized native `rnprobe` processes each sent encrypted
257-byte DATA; the receiver decrypted each on its respective accepted I2P child,
and both senders validated signed delivery proofs. The configurations contained
only I2P, without shared-instance, TCP or UDP fallback. This is native integration,
not Python interoperability or public-overlay acceptance.

The earlier reused-configuration failure is now resolved for native leaf startup.
An unchanged executable reproduced it: two fresh processes passed, but a third
reusing the first configuration/storage sent through its saved path and timed out.
Pinned `Transport.py:340-355,404-485` restores routes, tunnels and replay hashes
only in transport mode; our leaf had incorrectly restored them too. The runtime
now follows that guard, retaining known identities and cached resource proofs.
The new strict Windows native executable passed all three processes in 65 seconds,
including the reused storage containing the prior valid route. Each process
received a fresh announce after its distinct real I2P stream connected, sent one
encrypted 257-byte packet and validated its signed delivery proof. No storage
deletion, added retry, increased timer or changed offline-send behavior was used.
An actual-router follow-up changed only the candidate's `enable_transport` to
`yes`, retaining the fresh/fresh/reused process sequence and assertions.
Both fresh processes passed, but the reused candidate sent its single probe
using the saved I2P path and timed out: zero of one received.
The otherwise identical leaf control passed. This is a measured
transport-enabled native restart failure, not accepted recovery; an independent
upstream transport-enabled control has not yet been run. No cached-path deletion,
offline-send buffering, added retry or longer deadline was introduced to mask it.
Broader restart modes remain unverified.

An earlier unannounced receiver also timed out during discovery. Explicit
connected-peer announcements are part of the accepted scenario, not a discovery
retry fix or a preseeded path/identity.

Historical forced-public-tunnel restart attempts on i2pd and Java remain separate
failed evidence; local-route success does not resolve them. Impaired-network,
IFAC/routing, successful transport-enabled native persisted startup, other native platforms,
remote-router and router-process restart acceptance remain unverified.
Tests target RNS 1.5.7 at
`a29bfb51c468945c96a1ee22cae0114e993e2391` and LXMF 1.2.1.
