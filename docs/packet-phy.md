# Packet PHY measurements

Some interfaces, such as RNode radios, can report physical-layer conditions for each received frame: signal strength (RSSI), signal-to-noise ratio (SNR) and a link quality value. Reticulum attaches these readings to the received packet, so applications can see how well a packet arrived over the last radio hop.

PHY measurements describe the local receiving radio at the moment the packet came in. They are not authenticated claims about a distant peer, and they do not change packet serialization, hashes, encryption or forwarding.

## Reading measurements

Every decoded packet has a `PhysicalLayer` value with nullable `Rssi`, `Snr` and `Quality` fields. Interfaces that cannot report PHY data leave all three null.

```csharp
PacketPhysicalLayer phy = await manager.ResolvePacketPhysicalLayerAsync(packet, cancellationToken);
if (phy.Rssi is { } rssi) Console.WriteLine($"RSSI {rssi}");
```

You can look measurements up in several ways:

* `manager.GetPacketPhysicalLayer(hash)` looks up the interface manager's cache by full packet hash.
* `manager.ResolvePacketPhysicalLayerAsync(packet, cancellationToken)` fills in any fields missing from the packet itself.
* `RnsdHost.GetPacketPhysicalLayerAsync` is the host-level form, and also works for proof receipts.
* Shared-instance clients can ask the owning instance for missing values over the existing authenticated management RPC. The RPC returns `packet_rssi`, `packet_snr` and `packet_q` for a 32-byte `packet_hash`. Absent or evicted values return null.

## Tracking a link

Link tracking is off by default. Call `link.TrackPhysicalLayer(true)` to enable `link.PhysicalLayer`:

```csharp
link.TrackPhysicalLayer(true);
// Later:
var phy = link.PhysicalLayer;
```

* Incoming authenticated data, requests, responses, identification, handshake and resource packets each update whatever fields they carry. Missing fields keep their previous values.
* Accepted requests seed the initial measurements.
* Updates do not depend on packet callbacks or receipt subscribers.
* A proof receipt keeps the proof packet it received, together with that packet's measurement snapshot.
* Disabling tracking clears retained measurements immediately, and re-enabling does not restore them. While tracking is disabled, the getter reports no values.
* A shared-instance client performs cancellable, bounded lookups from the owning instance only when tracking is enabled and local fields are missing. A failed lookup is diagnosed without discarding valid link traffic.

Forwarding carries only wire bytes. Measurements taken on a transit node stay local to that node and are never presented as the endpoint's radio conditions.

## How measurements are captured

A PHY-reporting bearer implements `IPacketInterfacePhysicalLayer.ReceiveFramesAsync`. It captures an immutable measurement alongside the frame bytes before the manager enqueues the frame.

RNode single and Multi interface children capture the selected radio's current readings when a DATA frame arrives. Later telemetry cannot change a frame that is already queued. A disconnect or hard reset clears pending radio readings, but packets already received keep their snapshots. A frame that arrives without newer telemetry does not imply a new measurement.

### Manager cache

Each `PacketInterfaceManager` owns three FIFO caches, one per field. Each holds at most 512 measurements, keyed by the full 32-byte packet hash.

* Only valid, IFAC-accepted frames enter the cache.
* A missing field does not evict the other fields.
* For duplicate hashes, the oldest retained value of each field wins.
* There is no time-based expiry and no persistence.
* A snapshot carried on the frame takes precedence over the cache.

Measurements are recorded at ingress, before routing admission. A valid frame that the router later rejects by policy can therefore still occupy a bounded diagnostic cache entry.

## Differences from upstream

* The caches are owned and bounded by each manager, with no global singleton.
* They are filled at decoded ingress, before router scheduling. Upstream inserts after its early inbound filters. This keeps the measurement attributed to the original packet, but may cache frames that routing later drops.
* Shared-instance lookups use the existing authenticated management channel and add no unauthenticated endpoint.

## Limitations

Simulator and loopback tests do not establish physical RF behaviour. Physical-hardware testing is outside the current roadmap and remains unverified.

## Verification

`Parity8PhyTests` in the Core, Interfaces, Transport and Applications test projects covers reference observations, FIFO eviction and duplicate handling, sparse fields, radio and peer separation, reconnect, absence of PHY data on TCP, disabled tracking, authenticated shared-instance lookup and receipts without observers.

`tests\DotReticulum.Core.Tests\GenerateParity8PhyVectors.py` executes the pinned RNS 1.5.7 packet, transport, Reticulum and Link methods at `a29bfb51c468945c96a1ee22cae0114e993e2391`. Its in-memory interface and singleton setup avoids starting a daemon and does not replace protocol methods.
