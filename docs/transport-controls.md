# Transport controls

These settings govern route selection, packet scheduling, ingress and egress rate control, receipts, link MTU negotiation and tunnel recovery. DotReticulum targets the Reticulum 1.5.7 reference at `a29bfb51c468945c96a1ee22cae0114e993e2391`; it is experimental, AI-assisted and not an official Reticulum implementation. Missing or unverified capabilities are listed in [TODO.md](../TODO.md).

## Paths, modes and recovery

The router distinguishes ingress, announcement origin and egress interface modes,
including internal, roaming, access-point, boundary and shared-client boundaries.
Path replacement considers hop count, gravity, responsiveness, announce timebase
and mode-specific expiry; successful local outbound use refreshes path usage.
Authenticated link proofs can rebalance the destination path and expected
terminus hops. Invalid proofs cannot change either.

Failed pending and forwarded links schedule bounded, throttled rediscovery,
with separate standalone/shared-client and one-hop/boundary behavior. Removing a
bearer removes its validated link routes and live paths without tearing down
unrelated peers. Concurrent requests retain their individual requesting bearers;
engaged/inflight state prevents duplicate work without losing later requesters.

## Scheduling and configuration

`[reticulum]` supports `default_gravity`, `default_ar_target`,
`default_ar_grace`, `default_ar_penalty` and `qlen_in_data`,
`qlen_in_announce`, `qlen_in_pr`, `qlen_in_il`. Queue defaults are respectively
1024, 128, 128 and 8. The manager's separate bounded channel still applies
wait-on-full backpressure; the router's nonblocking priority queues diagnose
saturation and count rejection per class. Their service order is data,
announcements, path requests, then ingress-limited work.

Per-interface controls include `gravity`, `announce_cap`, `announce_rate_target`,
`announce_rate_grace`, `announce_rate_penalty`, `announces_from_internal`,
`announces_to_internal` and `recursive_prs`. `announce_cap` is a **percentage**
in configuration (default 2), but a fraction in the native API. Announce rate
targets/penalties are seconds. Transport interfaces inherit the source defaults
of 3600 seconds and grace 5; an explicitly positive per-interface target
defaults its omitted grace and penalty to zero.

`ingress_control` defaults on; `egress_control` defaults off. Their bounded
controllers consume actual received/transmitted traffic, not application-supplied
delegates. Controls include `ic_max_held_announces`, `ic_burst_hold`,
`ic_burst_freq_new`, `ic_burst_freq`, `ic_pr_burst_freq_new`, `ic_pr_burst_freq`,
`ic_new_time`, `ic_burst_penalty`, `ic_held_release_interval` and `ec_pr_freq`.
Durations are seconds and frequencies are events per second. Controllers retain
at most 48 timing samples per traffic window; held announcements default to 256.
Held release, burst cooldown and recursive egress checks remain scheduled work.
Accepted TCP children get independent controller state. Discovery applies its
configured gravity/internal-announcement overrides with the same global defaults.

Announce transmission uses the bearer bitrate estimate and bandwidth cap, with
bounded queues, age expiry, destination rate penalties and newer-announcement
replacement even when a queue is full. An unknown/zero bitrate has no fabricated
airtime cap. These are scheduling estimates, not measurements of a physical link.

`TransportRouter.FirstHopTimeout(destinationHash)` exposes the existing next-hop
estimate in seconds. `GetLowestInterfaceBitrate()` returns the lowest positive
online bearer bitrate in bits per second, or null; disconnected reconnecting
bearers and diagnostics reporting offline are excluded. Bearers without optional
online diagnostics are considered available while owned by the manager.
`GetMediumPathTimeout()` returns zero if no rate is known; otherwise it estimates
one full-MTU round trip at the slowest rate, clamped to the reference minimum of
5 bits/second, plus six seconds. Authenticated management queries reuse these
same calculations. Dynamic metadata changes are observed at query time rather
than waiting for Python's interface-prioritization refresh.

## Receipts and resource-proof recovery

`SendWithReceiptAsync` returns `null` for source-ineligible packet types/contexts,
instead of manufacturing a receipt that cannot complete. Eligible receipts
record sent/conclusion times, delivery/timeout/cull status and RTT. The bounded
1024-receipt table concludes evicted receipts explicitly. Timeouts follow the
source hop/first-hop policy; an unknown 128-hop path has a 774-second timeout,
not the old native fixed timeout.

Only forced resource proofs enter the dedicated bounded 1024-entry, 24-hour
cache. A sender recovering a lost proof uses the source plaintext cache query;
replay validates the packet hash, established link and actual bearer.
Malformed, cross-link and corrupted queries do not create a general packet-cache
service. Restart preserves original proof-cache age, including fractional seconds;
legacy timestamp-less entries receive an import timestamp once.

## IFAC, bitrate and MTU

IFAC derives the network identity/key from the source salt and normalized network
name/passphrase, signs the decoded packet and authenticates/masks the frame before
the packet parser sees it. Wrong credentials, missing/unexpected IFAC, tampering
and malformed frames are rejected and counted. Accepted children inherit their
listener's IFAC without exposing private material through statistics.
See [rnsd](rnsd.md) for aliases, sizes in bits and publication policy.

The legacy HKDF block-counter rollover is confined to the IFAC mask expansion
needed for upgraded raw frames. The general HKDF primitive retains its strict
RFC length limit, and low-order/noncanonical Curve25519 and invalid Ed25519 keys
remain rejected. Reference vectors cover long frames across the rollover, not
just 500-byte packets.

Interfaces expose configured bitrate, hardware MTU and discovery capability.
TCP hardware MTU follows source bitrate thresholds or an explicit `fixed_mtu`;
UDP does not advertise link MTU discovery. IFAC overhead is allowed at the raw
frame boundary without accepting oversized decoded packets on fixed-MTU bearers.
Link request/proof MTU fields are signed and validated. Operational offers and
confirmations follow discovery-capable bearers and the smallest path MTU, capped
at the native **524,288-byte** frame limit. A non-discovery bearer keeps the
default **500-byte MTU / 431-byte MDU**. Signed-field vectors additionally cover
larger encodable values without claiming those values are operational.
Actual pinned Python transport + .NET transport flows exercise upgraded DATA,
Channel and multipart resource traffic at **1,024** and **16,384** bytes, including
selective Channel loss and recovery. In-process tests cover other bearer
bottleneck positions; they are not physical-radio or all-MTU live evidence.

## Tunnels and reconnect ownership

`TransportRouter.SynthesizeTunnelAsync` sends a signed synthesis on exactly one
attached bearer, using its stable 32-byte interface hash. The receiving router
authenticates the transport public key, interface hash and fresh random value;
the tunnel ID excludes the random value so it remains stable across reconnects.
The synthesis is a control packet, not an application destination callback,
and works without subscribers.

A disconnected peer's tunnel retains announcement-backed paths while its active
routes and links are released. Reappearance must authenticate the same tunnel
identity before those paths can bind to the new peer. An existing shorter,
unexpired route wins; an older announce timebase cannot replace a newer one.
Tunnels and their paths are culled after the source-defined eight-hour windows.
See [runtime state](runtime-state.md) for the existing persistence schema and
dormant restart behavior.

Tunnels do not restore encrypted link sessions, resume resources, authorize a
peer to execute commands or authenticate a software publisher. Interface hashes
are endpoint identifiers rather than secrets. A valid signature is checked with
the same hardened Ed25519/X25519 public-key validation used elsewhere.

The reference's restoration check has a deliberate edge case: when an existing
route has expired, the hop/timebase branch does not separately test the saved
tunnel path's expiry. With no existing route, saved expiry is required. The
native state machine preserves this distinction; restart loading still rejects
expired persisted paths. Deterministic differential tests exercise both branches.

The daemon owns TCP reconnects, reapplies the configured mode/controls and queues
targeted synthesis without blocking a receive callback. Optional reconnect limits
terminate with a diagnostic instead of silently abandoning service. Each accepted
peer is isolated: malformed traffic, disconnect or a child fault cannot terminate
the listener or other peers.

Runtime tunnel tables are bounded to 1024 tunnels and 65,536 paths. The aggregate persistence format also bounds cached packets to 65,536: snapshots prioritize active paths, then tunnel paths, then proof-cache entries, diagnose capacity truncation and remove any omitted path's dangling packet reference. Snapshot bounds do not evict live routes.
## Verification

Routing, queue, receipt, MTU and tunnel behavior is covered by the Core and Transport test suites, including `TransportRouterTests.cs`, `Parity4RoutingTests.cs`, `Parity4ReceiptTests.cs`, `Parity4LinkMtuTests.cs` and `Parity4TunnelTests.cs`. Actual Python transport plus .NET transport tests exercise upgraded DATA, Channel and multipart resource traffic at 1,024- and 16,384-byte bottlenecks. These tests do not establish physical-radio behavior or support for every bearer and platform; see [TODO.md](../TODO.md).
