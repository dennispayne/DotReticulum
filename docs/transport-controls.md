# Transport controls

Transport controls govern how a DotReticulum node selects routes, schedules packets, limits ingress and egress bursts, tracks receipts, negotiates link MTU and recovers tunnels. Most nodes never need to change them. The defaults follow the pinned reference, RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`, and you only need to tune them if you are running a transport node on an unusual or constrained network.

DotReticulum is experimental, AI-assisted and not an official Reticulum implementation. Missing or unverified capabilities are listed in [TODO.md](../TODO.md).

## Configuring transport controls

Global defaults live in the `[reticulum]` section. Per-interface overrides go in the interface's own section. A transport node that wants a lower announce bandwidth on a slow link, and recursive path requests on a backbone link, might use:

```
[reticulum]
  enable_transport = yes
  default_ar_target = 3600
  qlen_in_data = 1024

[interfaces]
  [[Slow Link]]
    type = TCPClientInterface
    enabled = yes
    target_host = 192.0.2.10
    target_port = 4242
    announce_cap = 1
    announce_rate_target = 600

  [[Backbone Link]]
    type = TCPClientInterface
    enabled = yes
    target_host = 192.0.2.20
    target_port = 4242
    recursive_prs = yes
    egress_control = yes
```

### Global options

These go in `[reticulum]`:

| Option | Default | Meaning |
| --- | --- | --- |
| `default_gravity` | `0` | Gravity applied to interfaces that do not set `gravity`. |
| `default_ar_target` | `3600` | Announce rate target in seconds, inherited by interfaces on a transport node. |
| `default_ar_grace` | `5` | Announce rate grace count. As in upstream, an explicit zero falls back to the default. |
| `default_ar_penalty` | `0` | Announce rate penalty in seconds. |
| `qlen_in_data` | `1024` | Router inbound DATA queue capacity. |
| `qlen_in_announce` | `128` | Router inbound announce queue capacity. |
| `qlen_in_pr` | `128` | Router inbound path-request queue capacity. |
| `qlen_in_il` | `8` | Router inbound ingress-limited queue capacity. |

Nonpositive queue lengths and announce targets fall back to their defaults. The ingress and egress control options listed below, except `ingress_control`, can also be set here as global defaults. In the global section, nonpositive values fall back to the default.

### Per-interface options

| Option | Default | Meaning |
| --- | --- | --- |
| `gravity` | `default_gravity` | Path-selection gravity for this interface. |
| `announce_cap` | `2` | Share of interface bandwidth for announces, as a **percentage**. Values that are not above 0 and at most 100 fall back to 2. The native API takes a fraction instead. |
| `announce_rate_target` | inherited on transport nodes | Announce rate target in seconds. |
| `announce_rate_grace` | inherited, or `0` | Announce rate grace count. |
| `announce_rate_penalty` | inherited, or `0` | Announce rate penalty in seconds. |
| `announces_from_internal` | `yes` | Pass announces arriving from internal interfaces. |
| `announces_to_internal` | not set | Pass announces to internal interfaces. |
| `recursive_prs` | `no` | Forward path requests recursively. |

On a transport node, interfaces inherit the global target of 3600 seconds and grace of 5. If you set a positive `announce_rate_target` on an interface, its omitted grace and penalty default to zero rather than to the global values.

### Ingress and egress control

| Option | Default | Meaning |
| --- | --- | --- |
| `ingress_control` | `yes` | Enable the ingress controller. Interface sections only. |
| `egress_control` | `no` | Enable recursive egress control. |
| `ic_max_held_announces` | `256` | Maximum announces held during a burst. |
| `ic_burst_hold` | `15` | Seconds to hold after a burst. |
| `ic_burst_freq_new` | `3` | Announce burst frequency for new interfaces, per second. |
| `ic_burst_freq` | `10` | Announce burst frequency, per second. |
| `ic_pr_burst_freq_new` | `3` | Path-request burst frequency for new interfaces, per second. |
| `ic_pr_burst_freq` | `8` | Path-request burst frequency, per second. |
| `ic_new_time` | `7200` | Seconds an interface counts as new. |
| `ic_burst_penalty` | `15` | Burst penalty in seconds. |
| `ic_held_release_interval` | `5` | Seconds between releases of held announces. |
| `ec_pr_freq` | `5` | Egress path-request frequency, per second. |

On an interface, negative values are rejected. The controllers consume actual received and transmitted traffic, not application-supplied delegates. They keep at most 48 timing samples per traffic window. Held release, burst cooldown and recursive egress checks run as scheduled work. Accepted TCP children get independent controller state. Discovered interfaces apply their configured gravity and internal-announcement overrides with the same global defaults.

### Interface modes

The `mode` (or `interface_mode`) option accepts `full`, `pointtopoint` (`point_to_point`, `ptp`), `access_point` (`accesspoint`, `ap`), `roaming`, `boundary`, `gateway` (`gw`) and `internal`. An unknown mode is a configuration error.

## Paths and recovery

The router distinguishes ingress, announcement-origin and egress interface modes, including internal, roaming, access-point, boundary and shared-client boundaries. Path replacement considers hop count, gravity, responsiveness, announce timebase and mode-specific expiry. Successful local outbound use refreshes path usage. Authenticated link proofs can rebalance the destination path and expected terminus hops; invalid proofs cannot change either.

When pending or forwarded links fail, the router schedules bounded, throttled rediscovery. Standalone and shared-client nodes behave differently here, as do one-hop and boundary paths.

Removing a bearer removes its validated link routes and live paths without tearing down unrelated peers. Concurrent path requests retain their individual requesting bearers. Engaged and in-flight state prevents duplicate work without losing later requesters.

## Packet scheduling

The router has four nonblocking priority queues, sized by the `qlen_in_*` options. They are serviced in this order:

1. data
2. announcements
3. path requests
4. ingress-limited work

When a queue is full, the router diagnoses the saturation and counts the rejection for that class. These queues are separate from the interface manager's bounded channel, which still applies wait-on-full backpressure.

Announce transmission uses the bearer's bitrate estimate and the announce bandwidth cap. Announce queues are bounded, with age expiry, per-destination rate penalties, and replacement by a newer announcement even when the queue is full. A bearer with an unknown or zero bitrate gets no fabricated airtime cap. These are scheduling estimates, not measurements of a physical link.

## Timeout estimates

`TransportRouter` exposes the estimates it uses internally:

- `FirstHopTimeout(destinationHash)` returns the next-hop estimate in seconds.
- `GetLowestInterfaceBitrate()` returns the lowest positive bitrate of an online bearer in bits per second, or null. Disconnected reconnecting bearers, and bearers whose diagnostics report offline, are excluded. Bearers without optional online diagnostics count as available while the manager owns them.
- `GetMediumPathTimeout()` returns zero if no rate is known. Otherwise it estimates one full-MTU round trip at the slowest rate, clamped to the reference minimum of 5 bits per second, plus six seconds.

Authenticated management queries use the same calculations. Dynamic metadata changes are observed at query time rather than waiting for Python's interface-prioritization refresh.

## Receipts and resource-proof recovery

`SendWithReceiptAsync` returns `null` for packet types and contexts that the source does not issue receipts for, rather than manufacturing a receipt that can never complete. Eligible receipts record sent and conclusion times, delivery, timeout or cull status, and RTT.

The receipt table holds at most 1024 receipts, and evicted receipts are concluded explicitly. Timeouts follow the source hop and first-hop policy. An unknown 128-hop path has a 774-second timeout.

Only forced resource proofs enter the dedicated proof cache, which is bounded to 1024 entries and 24 hours. A sender recovering a lost proof uses the source's plaintext cache query. Replay validates the packet hash, the established link and the actual bearer. Malformed, cross-link and corrupted queries do not turn this into a general packet-cache service. A restart preserves the original proof-cache age, including fractional seconds. Legacy entries without a timestamp receive an import timestamp once.

## IFAC, bitrate and MTU

Interface access codes (IFAC) derive the network identity and key from the source salt and the normalized network name or passphrase. The sender signs the decoded packet, and the frame is authenticated and unmasked before the packet parser sees it. Wrong credentials, missing or unexpected IFAC, tampering and malformed frames are rejected and counted. Accepted children inherit their listener's IFAC without exposing private material through statistics. See [rnsd](rnsd.md) for option aliases, sizes in bits and publication policy.

The legacy HKDF block-counter rollover is confined to the IFAC mask expansion needed for upgraded raw frames. The general HKDF primitive keeps its strict RFC length limit. Low-order or noncanonical Curve25519 keys and invalid Ed25519 keys remain rejected. Reference vectors cover long frames across the rollover, not just 500-byte packets.

Interfaces expose their configured bitrate, hardware MTU and MTU-discovery capability:

- TCP hardware MTU follows the source bitrate thresholds, or an explicit `fixed_mtu`.
- UDP does not advertise link MTU discovery.
- IFAC overhead is allowed at the raw frame boundary, without accepting oversized decoded packets on fixed-MTU bearers.

Link request and proof MTU fields are signed and validated. Offers and confirmations follow discovery-capable bearers and the smallest path MTU, capped at the native **524,288-byte** frame limit. A bearer without MTU discovery keeps the default **500-byte MTU / 431-byte MDU**. Signed-field vectors also cover larger encodable values without claiming those values are operational.

## Tunnels and reconnects

`TransportRouter.SynthesizeTunnelAsync` sends a signed tunnel synthesis on exactly one attached bearer, using that bearer's stable 32-byte interface hash. The receiving router authenticates the transport public key, interface hash and fresh random value. The tunnel ID excludes the random value, so it stays stable across reconnects. The synthesis is a control packet, not an application destination callback, and it works without subscribers.

When a peer disconnects, its tunnel keeps the announcement-backed paths while its active routes and links are released. When the peer reappears, it must authenticate as the same tunnel identity before those paths bind to the new connection. An existing shorter, unexpired route wins, and an older announce timebase cannot replace a newer one. Tunnels and their paths are culled after the source-defined eight-hour windows. See [runtime state](runtime-state.md) for the persistence schema and dormant restart behaviour.

The reference's restoration check has a deliberate edge case. When an existing route has expired, the hop and timebase branch does not separately test the saved tunnel path's expiry. With no existing route, saved expiry is required. The native state machine preserves this distinction, and restart loading still rejects expired persisted paths.

The daemon owns TCP reconnects. It reapplies the configured mode and controls, and queues targeted synthesis without blocking a receive callback. Optional reconnect limits end with a diagnostic instead of silently abandoning service. Each accepted peer is isolated: malformed traffic, a disconnect or a child fault cannot terminate the listener or other peers.

Runtime tunnel tables are bounded to 1024 tunnels and 65,536 paths. The aggregate persistence format also bounds cached packets to 65,536. Snapshots prioritize active paths, then tunnel paths, then proof-cache entries. They diagnose capacity truncation and remove any omitted path's dangling packet reference. Snapshot bounds do not evict live routes.

## Differences from upstream

- `announce_cap` is a percentage in configuration, as upstream, but a fraction in the native API.
- `SendWithReceiptAsync` returns `null` for receipt-ineligible packets instead of a receipt that cannot complete.
- Timeout and bitrate queries observe dynamic metadata at query time, not at Python's interface-prioritization refresh.
- Link MTU is capped at the native 524,288-byte frame limit.
- Runtime tunnel tables and snapshots have explicit capacity bounds.

## Limitations

Tunnels do not restore encrypted link sessions, resume resources, authorize a peer to execute commands or authenticate a software publisher. Interface hashes are endpoint identifiers, not secrets. A valid signature is checked with the same hardened Ed25519 and X25519 public-key validation used elsewhere.

Bitrate, airtime and MTU values are configuration and scheduling estimates, not physical-link measurements. Upgraded MTU has been exercised live only at the bottlenecks listed below. In-process tests cover other bearer bottleneck positions; they are not physical-radio or all-MTU live evidence.

## Verification

Routing, queue, receipt, MTU and tunnel behaviour is covered by the Core and Transport test suites, including `TransportRouterTests.cs`, `Parity4RoutingTests.cs`, `Parity4ReceiptTests.cs`, `Parity4LinkMtuTests.cs` and `Parity4TunnelTests.cs`. Deterministic differential tests exercise both branches of the tunnel restoration edge case.

Flows between the actual pinned Python transport and a .NET transport exercise upgraded DATA, Channel and multipart resource traffic at **1,024** and **16,384** bytes, including selective Channel loss and recovery. These tests do not establish physical-radio behaviour or support for every bearer and platform; see [TODO.md](../TODO.md).
