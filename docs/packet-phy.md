# Packet PHY measurements

PHY measurements describe **local ingress**, not the distant endpoint's radio
conditions, and are not cryptographically authenticated peer claims. They do not
change packet serialization, hashes, encryption or forwarding.

## Native API and ownership

`Packet.PhysicalLayer` holds nullable `Rssi`, `Snr` and `Quality` measurements.
Absent data stays null, including ordinary TCP/UDP traffic. A reporting bearer
implements `IPacketInterfacePhysicalLayer.ReceiveFramesAsync` and captures a
`ReceivedFrame` (bytes plus immutable measurements) **before** enqueueing it.
The manager selects this receive boundary instead of the ordinary byte stream.
Extension interfaces without it continue to work and report no PHY measurements.

RNode single and Multi children capture the selected radio's readings at DATA
frame reception. Later telemetry cannot change an earlier queued frame.
Disconnect clears pending radio measurements; a new connection starts unknown.
An explicit device hard reset clears pending readings as well.
An already received packet retains its snapshot. Each radio retains its latest
reading until superseded within the connection, as upstream does; a DATA frame
without a new telemetry command is not evidence of a new measurement.

Each `PacketInterfaceManager` owns three FIFO caches, each bounded to **512
reported measurements**, keyed by the full 32-byte packet hash. Only valid,
IFAC-accepted frames enter the caches. Missing fields do not evict other fields.
Like pinned RNS, caches have **no clock-based expiry** and duplicate hashes return
the oldest still-retained measurement for each field. A packet's direct snapshot
takes precedence over this fallback. A new owner has empty caches; these values
are not persisted. No process-global singleton or unbounded packet map is used.
The native owner records at decoded ingress, before router admission/scheduling;
therefore valid frames subsequently dropped by routing policy can still have
bounded diagnostic cache entries. Upstream inserts after its early inbound
filters. This intentional placement never attributes device-global state after
queueing and does not imply those packets were delivered.

`manager.GetPacketPhysicalLayer(hash)` queries the owner-local caches.
`await manager.ResolvePacketPhysicalLayerAsync(packet, cancellationToken)` fills
missing packet fields from the cache, or an explicitly configured shared-owner
lookup. Configure `SharedPhysicalLayerLookup` before starting routing. The host
provides `await host.GetPacketPhysicalLayerAsync(packet, cancellationToken)` for
the same operation, including `receipt.ProofPacket`. Outgoing packet objects are
not silently populated with another packet's readings.

## Shared instances and Links

The existing authenticated management RPC exposes `packet_rssi`, `packet_snr`
and `packet_q` with the existing `packet_hash` argument validation (32 bytes).
Unknown or evicted entries return null; no alternate unauthenticated endpoint or
new remote mutation operation is added.

`link.TrackPhysicalLayer(true)` enables `link.PhysicalLayer`. Authenticated link
data, requests, responses, identification, handshake and resource handling update
the last available fields independently. Missing fields preserve earlier known
link fields. Link processing does not require a packet callback or receipt
subscriber. A proof receipt retains its received proof packet and measurements.

Tracking is disabled by default. Disabling clears retained measurements
immediately (upstream hides them immediately and clears them in its watchdog).
Re-enabling does not resurrect measurements discarded while disabled. Accepted
incoming requests seed the initial measurements, matching upstream's forced
request update, but the getters remain hidden unless enabled.

A shared client's opt-in link resolves missing measurements through the existing
authenticated owner RPC before processing the packet. This is cancellable,
bounded by the existing operation timeout and creates no background work.
Optional query failures are diagnosed without discarding otherwise valid link
traffic; caller cancellation still propagates. Nontracking data traffic does not
issue these queries. Explicit packet/receipt queries remain available separately.

Forwarding carries wire bytes only. A transit node's radio reading stays in that
node's cache; it does **not** appear as an endpoint TCP measurement. A local shared
client can ask its owner about that owner's ingress, not about arbitrary routers.

## Reference and evidence

The source is hosted RNS 1.5.7 commit
`a29bfb51c468945c96a1ee22cae0114e993e2391`:
`Transport.py:1913–1935` (FIFO insertion, limit 512),
`Packet.py:370–389` (direct/fallback queries),
`Reticulum.py:1918–1959` (local/shared queries), and
`Link.py:215,439,540–570,777–793,954–1164` (tracking and update sites).

`tests\DotReticulum.Core.Tests\GenerateParity8PhyVectors.py` executes the actual
pinned packet pack/unpack, transport inbound, Reticulum queries and Link methods.
It verifies source/import paths and LXMF 1.2.1, and records source SHA-256 values
in `Fixtures\Parity8PhyVectors.json`. Its in-memory interface/singleton setup
avoids starting a daemon; it does not replace the protocol implementation.

`Parity8PhyTests` in Core, Interfaces, Transport and Applications exercise
independent reference observations, FIFO eviction/duplicates/sparse fields,
interleaved peers and radios, delayed enumeration, bounded queues, reconnect,
TCP absence, disabled tracking, authentication failure, receipts without
observers, transit separation and authenticated shared-owner Link lookup.
These are reference-vector/simulated/native-loopback software tests, **not
physical RF acceptance or Python radio-node interoperability**. No physical
radio was opened for this work.
