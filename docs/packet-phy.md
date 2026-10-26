# Packet PHY measurements

PHY measurements describe the local receive radio at packet ingress. They are not authenticated claims about a distant peer and do not alter packet serialization, hashes, encryption, or forwarding.

## Native API and ownership

`Packet.PhysicalLayer` contains nullable RSSI, SNR, and quality values. A PHY-reporting bearer implements `IPacketInterfacePhysicalLayer.ReceiveFramesAsync` and captures immutable measurements alongside frame bytes before the manager enqueues the frame. Other interfaces continue to report no PHY values.

RNode single and Multi children capture the selected radio's current readings when a DATA frame arrives. Later telemetry cannot change an already queued frame. Disconnect or hard reset clears pending radio readings; received packets retain their snapshots. A frame without newer telemetry does not imply a new measurement.

Each `PacketInterfaceManager` owns three FIFO caches, each limited to 512 measurements and keyed by the full 32-byte packet hash. Only valid, IFAC-accepted frames enter the cache. Missing fields do not evict other fields; duplicate hashes resolve to the oldest retained value per field. There is no time-based expiry or persistence. A direct frame snapshot takes precedence over cache lookup. Ingress records measurements before routing admission, so a valid frame subsequently rejected by router policy can still occupy a bounded diagnostic cache entry.

Use `manager.GetPacketPhysicalLayer(hash)` for a cache lookup or `ResolvePacketPhysicalLayerAsync(packet, cancellationToken)` to fill missing fields. `RnsdHost.GetPacketPhysicalLayerAsync` provides the host-level form, including proof receipts. Shared clients may request missing values from their owner through the existing authenticated management RPC. The RPC exposes `packet_rssi`, `packet_snr`, and `packet_q` with a 32-byte `packet_hash`; absent or evicted values return null.

## Links

Link tracking is disabled by default. `link.TrackPhysicalLayer(true)` enables `link.PhysicalLayer`. Incoming authenticated data, requests, responses, identification, handshake, and resource handling update available fields independently; missing fields preserve prior known values. Processing does not depend on packet callbacks or receipt subscribers. A proof receipt retains its received proof packet and its measurement snapshot.

Disabling tracking clears retained measurements immediately; re-enabling does not restore cleared data. Accepted requests seed initial measurements, while getters remain hidden when tracking is disabled. A shared client performs cancellable, bounded owner lookups only when tracking is enabled and local fields are missing; query failure is diagnosed without discarding valid link traffic.

Forwarding carries wire bytes only. Transit-node measurements remain local to that node and are not represented as the endpoint's radio conditions.

## Differences from upstream

The native caches are manager-owned and bounded, with no global singleton. Their insertion point is decoded ingress before router scheduling; upstream inserts after early inbound filters. This placement preserves the original packet's measurement attribution but may cache frames later dropped by routing policy. Shared lookups use the existing authenticated management channel and do not add an unauthenticated endpoint.

## Verification

`Parity8PhyTests` in Core, Interfaces, Transport, and Applications covers reference observations, FIFO eviction and duplicate handling, sparse fields, radio/peer separation, reconnect, TCP absence, disabled tracking, authenticated shared-owner lookup, and receipts without observers. `tests\DotReticulum.Core.Tests\GenerateParity8PhyVectors.py` executes the pinned RNS 1.5.7 packet, transport, Reticulum, and Link methods at `a29bfb51c468945c96a1ee22cae0114e993e2391`. Its in-memory interface/singleton setup avoids starting a daemon and does not replace protocol methods. Simulator and loopback tests do not establish physical RF behavior.
