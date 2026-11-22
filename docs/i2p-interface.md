# I2P interface

The I2P interface lets you connect Reticulum instances over the [Invisible Internet Protocol](https://geti2p.net/en/about/intro). This is useful when you want a globally reachable Reticulum instance but have no public IP address, have a frequently changing address, or are behind firewalls that block inbound traffic. A connectable I2P interface gives you a persistent I2P address that your instance can be reached at.

The native interface talks to an I2P router that you provide, through the router's SAM 3.1 API. It does not install, launch or configure i2pd or Java I2P. Each Reticulum stream over I2P becomes an independent child interface.

## Example configuration

```
# Accept incoming connections on a persistent
# I2P address.
[[I2P]]
  type = I2PInterface
  enabled = yes
  connectable = yes

# Also connect to other Reticulum instances.
[[I2P]]
  type = I2PInterface
  enabled = yes
  connectable = yes
  peers = 5urvjicpzi7q3ybztsef4i5ow2aq4soktfj7zedz53s47r54jnqq.b32.i2p
```

| Key | Default | Meaning |
| --- | --- | --- |
| `connectable` | `no` | Create a server destination and accept incoming streams |
| `peers` | empty | Comma-separated I2P addresses to connect to |
| `bitrate` | 256 kbit/s | Generic bitrate estimate. Values below 5 are ignored. |
| `ifac_size` | 16 bytes | Generic IFAC size, configured in bits |

The section name is the interface name. The runtime supplies the storage path and the transport identity hash. IFAC and routing policies are applied to the child interfaces by the interface manager.

It can take from a few seconds to several minutes to establish I2P connections, so the interface works in the background and reports progress in the log. The published address is available as `Base32Address`, ending in `.b32.i2p`.

## Setting up the router

Install and run an I2P router separately, and enable its SAM 3.1 service on a local endpoint. The conventional endpoint is `127.0.0.1:7656`. The native API also honours `I2P_SAM_ADDRESS=host:port` when no SAM host is given. The process running DotReticulum must be able to reach that endpoint.

A SAM endpoint that answers does not mean the router is ready. Let the router bootstrap normally and build client tunnels and lease sets. A freshly started, isolated router can answer SAM `HELLO` while still being unable to resolve or connect to destinations. Neither a SAM `STATUS OK` reply nor any router setting proves Reticulum stream delivery.

The interface never changes router configuration. In some controlled i2pd 2.59.0 tests, `sam.singlethread=false` was used to avoid an observed destination-cleanup hazard. That setting is non-default, did not make native post-restart stream recovery pass, and is not required for normal use.

## Behaviour

### SAM sessions

Each SAM control and data connection negotiates `HELLO VERSION MIN=3.1 MAX=3.1`. Destinations use Ed25519 (`SIGNATURE_TYPE=7`). A connectable interface creates a `SESSION CREATE STYLE=STREAM` session and accepts streams with `STREAM ACCEPT`. Configured peers are reached with a naming lookup and `STREAM CONNECT`.

The SAM parser handles fragmented lines and keeps packet bytes that arrive in the same read as the destination line. Session control sockets stay open while their streams are active. SAM `INVALID_ID`, a failed control socket or an incomplete session creation retires that session and creates a new one. An ordinary stream EOF or a connect failure for one peer does not discard an otherwise healthy initiator session.

One setup deadline, 30 seconds by default, covers destination generation, session creation, naming and stream connect. The pinned Python SAM calls can wait for replies with no application timeout. When the native deadline expires, the pending data attempt is cancelled but a ready session is kept. This bounds recovery; it does not guarantee that a router will complete a stream.

### Peers and framing

The parent sends no packet data. It owns the SAM sessions and publishes peers with independent identities.

- Initiating peers reconnect every 15 seconds by default. Accepted peers do not reconnect.
- A keepalive, an empty HDLC flag pair, is sent after ten seconds without packet writes. It is not a Reticulum packet and is not counted as one.
- Peers are stale after 20 seconds, and reads time out after 110 seconds.
- Frames use HDLC. Hardware MTU is 1064 bytes.
- A failed data stream is isolated from other peers. A cancelled or failed packet write retires its stream, so a partial frame is never joined to a later packet.
- Receive queues and peer counts are bounded. Malformed SAM lines, invalid destination data, malformed frames and oversized input are rejected. SAM tokens and destination structures from the router are validated before a peer is published.
- Disposal closes only the interface's own SAM sockets. It does not stop the router.

### Destination storage

A connectable destination's private key is stored under `storagepath/i2p/` and reused after restart, so your I2P address stays the same.

The file name is derived from the interface name and transport identity:

- Legacy name: `SHA256(SHA256(UTF8(name)))`. An existing legacy file is preferred for compatibility.
- Otherwise: `SHA256(SHA256(UTF8(name)) || SHA256(transport_identity_hash))`.

The public destination ends at the certificate length field (offsets 385–386). Key files are written to a new staging file and published without overwriting an existing file. On Unix, they are created readable and writable only by the owner. On Windows, protect the storage directory with appropriate ACLs yourself. Corrupt or oversized key files are reported and fail closed; they are never silently replaced. Private destination strings and SAM credentials are kept out of diagnostics and fixtures, but managed immutable strings cannot be reliably zeroed.

### Library API

`I2pPacketInterface` takes `I2pPacketInterfaceOptions`. Native options that are not configuration keys include `SamHost` and `SamPort` (`null` selects `I2P_SAM_ADDRESS` or `127.0.0.1:7656`), the 30-second setup timeout, the 15-second reconnect delay, a receive queue of 256 frames per child, 64 incoming and 64 outgoing peers, and an optional finite reconnect-attempt limit.

## Differences from upstream

- The native interface uses SAM data sockets directly, rather than spawning Python tunnel proxy listeners and threads.
- Queues and peer counts are bounded, setup has explicit deadlines, retries can be finite, shutdown is cancellable, and malformed frames are rejected strictly.
- Native initiators keep a ready SAM session through peer-specific refusal, timeout or ordinary stream EOF, and only recreate it on real session-control failures. Upstream's tunnel status handling can stop and rebuild a tunnel after a reported setup failure.
- The IFAC size is normalized to the documented 16-byte default, rather than reproducing a nullable constructor-ordering edge case in upstream initiators.

## Platform notes

SAM is implemented with .NET sockets and filesystem APIs. Managed suites run on Windows x64 and Linux x64. Router availability, local firewall rules, filesystem permissions, I2P overlay readiness and network reachability are your responsibility as operator. macOS ARM64 build and test runs do not establish live I2P interoperability, and other platforms are unverified.

## Verification

| Test class | What it covers |
| --- | --- |
| `Parity6I2pTests` | SAM parsing, session lifetime, key persistence, reconnection, peer isolation, bounded queues and pinned-source vectors, using controlled simulators |
| `Parity6I2pLiveTests` | Opt-in tests against an actual SAM router |
| `Parity6I2pReferenceRecoveryTests` | Actual pinned-Python process restart |

Tests target RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391` and LXMF 1.2.1. Detailed commands, timings and results are recorded in the [development history](../.github/development-history.md).

### Results against a real router

These results are from Windows x64 with Java I2P 2.14.0.

| Scenario | Result |
| --- | --- |
| Managed native bearer restart, same router, local-destination routing | Passed. Exact DATA packets travelled both ways before and after the connectable bearer was recreated, with the same persisted key and address, and the initiator survived. |
| Pinned Python process restart, same topology | Passed, as an upstream baseline. Not a mixed-peer or NativeAOT claim. |
| Native restart with Java local delivery disabled (forced public tunnels) | Failed. Initial DATA passed both ways. After restart the replacement was SAM-ready with the same key and address, but no accepted peer arrived and the initiator's `STREAM CONNECT` timed out. |
| Pinned Python baseline with forced public tunnels | Failed the same way, with no .NET code involved. |
| Forced public tunnels, rerun after confirming Windows firewall rules | Both the .NET and the all-upstream cases failed again, so late firewall approval does not explain the failure. |
| Published native `rnprobe`, two fresh processes and a third reusing the first one's storage, leaf mode | Passed. Each process sent encrypted 257-byte DATA over its own accepted I2P child and validated a signed delivery proof. I2P only, with no shared-instance, TCP or UDP fallback. |
| Same, with `enable_transport = yes` | Failed. Both fresh processes passed, but the reused process sent through its saved I2P path and received nothing. |
| Same, with actual pinned-upstream `rnprobe` candidates and the .NET receiver | Failed the same way on the reused process, reproducing the transport-enabled failure independently. |

In the native forced-public-tunnel failure, router diagnostics showed cached-lease reuse and garlic decryption and unknown-session-tag failures after restart. That places the observed failure below the managed packet and framing path, but does not yet show which router cache or recovery policy must change. Because the tested implementations share this failure, it is not evidence that a native-only framing or persistence change would fix public-overlay recovery.

The leaf-mode reused-storage case originally failed. The cause was that the leaf restored routes, tunnels and replay hashes, which pinned `Transport.py:340-355,404-485` restores only in transport mode. The runtime now follows that rule while keeping known identities and cached resource proofs. The receiver announces explicitly after each I2P stream connects; this is part of the scenario, not a discovery retry or a pre-seeded path. No cached-state deletion, offline-send buffering, extra retry or longer deadline was added to make any of these cases pass.

Local-route success does not resolve the forced-public-tunnel failures. Impaired networks, IFAC and routing over I2P, successful transport-enabled persisted startup, other native platforms, remote routers and router-process restart all remain unverified.

### Running the topology tests

The opt-in topology runner [`tests/Interop/Invoke-I2pTopology.ps1`](../tests/Interop/Invoke-I2pTopology.ps1) uses an i2pd executable or extracted official Java I2P distribution that you have already obtained. It starts its own router process from those files and stops it on exit, but never downloads or installs a router or starts a system service. Public-network runs require explicit `-AllowPublicNetwork` authorization and keep reseed certificate verification enabled.

| Option | Effect |
| --- | --- |
| `-JavaLocalDestinationRouting` | Keep Java I2P's normal local-to-local destination delivery. Java I2P's `i2cp.disableLoopback` (see `router/java/src/net/i2p/router/client/ClientManager.java:85-86,462-501` in 2.14.0) otherwise forces even same-router destinations through public tunnels. This changes router topology only, not the SAM client, RNS source, framing, assertions or tunnel lengths. |
| `-NativeRestartOnly` | Run only the native bidirectional DATA and persisted-bearer restart case, with the same four-minute deadline and assertions. |
| `-ReferenceRestartOnly` | Run only the pinned-Python process-restart counterexample. |
| `-PublishedFeatureOnly` | With `DOTRETICULUM_NATIVE_EXE` set to a published executable, run the cold-start NativeAOT `rnprobe` case. Requires `-AllowPublicNetwork` but not Python. |
| `-SeedRouterInfoDirectory` | Seed public `routerInfo-<hash>.dat` files in the standard `r<first-hash-character>` layout. This is a bootstrap aid only. It copies no router identity, private destinations, profiles or lease sets, and a seeded run must be described as seeded, not as a fresh bootstrap. |
| `-TestAssemblyPath` | Run `dotnet vstest` against a copied snapshot of the Release test output, for example under `TestResults`, instead of building shared outputs. Do not combine with `-NoBuild`. |

Router profiles used by the runner:

- **Isolated i2pd.** Real loopback NTCP2 on an owned port, IPv4 loopback binding, IPv6 disabled, `reservedrange=false`, non-main `netid=222`, SSU2 disabled, and unrelated transports, proxy and control services, subscriptions, time sync, UPnP and transit disabled. This is test isolation, not a firewall or OS namespace. It creates no tunnels or lease sets, so it can check SAM persistence but cannot prove stream delivery.
- **Java I2P.** Starts the foreground `net.i2p.router.Router` with only its in-process SAM bridge. External I2CP, its local shortcut, UPnP and transit (`router.sharePercentage=0`, `router.maxParticipatingTunnels=0`) are disabled. NTCP and UDP use owned ports. Default three-hop inbound and outbound client tunnels, variance zero, are kept, and no zero-hop options are injected. The distribution's public `hosts.txt` and certificate data are required. On Linux, Java 2.14.0 portable mode can recurse while creating a temporary context, so the runner uses an owned configuration directory instead.

Test results and router state are unique to each run, and child processes do not inherit `GITHUB_STEP_SUMMARY`. Retained router artifacts are private: Java router diagnostics can include session-key material. Do not commit or publish raw router logs or state.
