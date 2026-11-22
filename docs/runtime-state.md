# Runtime state and persistence

A DotReticulum node remembers some public network state between runs, so that a transport node can recover its routes after a restart instead of relearning everything. This page describes what is saved, where, in what format, and how failures are handled. Private identity and ratchet material is kept separate and has its own ownership and persistence rules.

Contracts target RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`.

## What is restored on startup

Only transport-enabled owners restore saved paths, tunnels and the packet replay hash list, as upstream does (`Transport.py:340-355,404-485`).

A leaf node still restores known identities and cached resource proofs, but it must learn fresh routes after startup. This also applies when existing transport storage is reopened with transport disabled. The point is to avoid treating an old route as evidence that an asynchronous bearer is already ready. It does not change offline-send behaviour or add implicit probe retries.

The runtime never resurrects links. Tunnel paths are loaded **dormant**, without binding to an old accepted TCP peer. The live transport restores them only after a signed synthesis from the same remote transport and interface identity arrives on a current bearer. See [transport controls](transport-controls.md).

## Where state is stored

Public runtime state lives in a single file, `runtime-state-v1.msgpack`, in the node's storage directory (normally `<config>/storage`). Locally persisted private ratchets live in `destination-ratchets/<destination-hash>.v1.msgpack` under the same directory. Streamed resource files default to `<config>/storage/resources`; see [Resource storage](#resource-storage).

## Known destinations

`KnownDestinations` mirrors `Identity.py:101-335`:

- Reannouncing replaces announce data but preserves usage.
- Recall and app-data recall mark the destination as used, unless that is explicitly suppressed.
- Usage `0` means never used, `-1` means retained, and a positive fractional Unix time is the last use.
- Using retained data returns false without releasing retention. Unretaining records the current use time.
- Identity retention applies to every remembered destination with that public identity.

Detached snapshots preserve binary hashes and fractional timestamps. The legacy second-valued `RememberedUnix` and `RatchetReceivedUnix` properties remain available alongside the precise timestamp properties.

Cleanup protects retained destinations and destinations with an existing path. Used entries without a path expire strictly after seven days times 1.25.

The pinned reference has a surprising edge case here. Its `elif` also compares `now - 0` against that timeout for never-used entries, so at contemporary Unix times even a newly remembered, never-used entry without a path is removed. DotReticulum reproduces and independently records this behaviour rather than silently changing it to a six-minute guarantee.

Capacity is a deliberate bounded .NET policy. Eviction never removes explicitly retained data. If every slot is retained, remembering a new destination fails explicitly.

## Public ratchets

Public ratchets follow `Identity.py:410-513`. Unchanged announcements do not refresh the original received timestamp, and expired keys are unavailable on restart and cleanup. The .NET cache also checks expiry on every read, instead of keeping an expired in-memory key until a later cleanup.

## Formats

`PersistenceFormats` reproduces these upstream MessagePack schemas byte for byte:

| Data | Upstream schema |
| --- | --- |
| Known destinations | Binary destination-hash map to `[last_announce, packet_hash, public_key, app_data, last_use]`. Four-field legacy entries load with `last_use = 0`. |
| Paths (`Transport.py:3788-3876`) | `[destination_hash, timestamp, received_from, hops, expires, random_blobs, interface_hash, packet_hash]` |
| Tunnels (`Transport.py:3879-3950`) | `[tunnel_id, interface_hash_or_null, paths, expires]` |
| Local destination ratchet interchange (`Destination.py:211-221,437-475`) | `{"signature": identity.sign(packed_keys), "ratchets": packed_keys}` |
| Packet hashes | Full 32-byte hashes, matching `packet_hashlist.raw` |

Persisted paths allow zero hops for shared-instance local clients, because upstream `Transport.py:1937` removes the shared-instance hop before recording their path.

### The aggregate state file

Upstream overwrites several files independently. DotReticulum instead writes **one atomic generation**. `runtime-state-v1.msgpack` is a MessagePack map `{"version": 1, "payload": binary, "sha256": binary}`. The payload map contains:

| Key | Contents |
| --- | --- |
| `known_destinations`, `paths`, `tunnels` | Binary blobs in the upstream formats above |
| `known_ratchets` | `[destination_hash, public_key, received_timestamp]` rows |
| `packet_hashes` | Full binary hashes |
| `cached_packets` | `[packet_hash, raw_wire_packet, is_announce]` rows |

Forced resource proofs can append a fourth field to their `cached_packets` row: the original fractional Unix cache timestamp. Legacy three-field rows remain readable. Import assigns a missing proof timestamp once, and later saves preserve that age rather than extending expiry on every restart.

The checksum detects accidental corruption. It does **not** authenticate the publisher or protect against a local writer. Cached packet hashes and signed announces are validated on save and on load. Only public material belongs in this file. Expired paths and tunnels are filtered on load.

### Router integration

`IRuntimeStateProvider` is the router integration boundary. A provider must:

- capture synchronized existing state
- resolve persistent interface hashes to active interfaces
- require the matching, valid cached announce before restoring a path
- apply the current blackhole policy

## Tunnel persistence

Tunnel synthesis follows `Transport.py:2764-2874`. The 176-byte payload is `public_key(64) || interface_hash(32) || random_hash(16) || signature(64)`. The signature covers the preceding 112 bytes, and the tunnel ID is SHA-256 of the first 96 bytes. It is addressed to the plain `rnstransport.tunnel.synthesize` destination. Invalid signatures, malformed keys and blocked transport identities do not establish tunnels, and the strict public-key checks remain in force.

Accepted announcements associate their actual path and signed cached packet with the ingress tunnel. Detaching an interface voids its live association but keeps these paths for recovery. Authenticated reappearance rebinds eligible paths to the new interface. Hop count, expiry and the newest five-byte announce timebase decide whether a current path can be replaced. Rejected restoration candidates are removed from that tunnel, not broadcast as fresh announces.

Tunnel and tunnel-path housekeeping use the reference's eight-hour windows, including rejection of implausibly far-future tunnel expiry. Runtime tables are bounded to 1024 tunnels and 65,536 tunnel paths. Tunnel-capacity eviction is diagnosed, and the aggregate path bound evicts the oldest saved path. No arbitrary packet cache is inferred from this.

## Writing files safely

### Replacing state

Every state write stages an exclusively created file in the same directory, flushes it, and atomically renames it into place. Cancellation before publication preserves the previous generation. Capture validates the new state before replacing the committed generation.

On Unix, files are created readable and writable by the owner only. On Windows they inherit the directory ACL, so the operator must protect that directory.

Atomic replacement gives you complete-file visibility. It does not give you a cross-file transaction with identity files, directory fsync durability, or coordination between independent hosts. Only the shared instance owner should persist shared state.

On Windows, concurrent delete-shared readers and replacers can briefly leave a filename delete-pending. Reads and replacement retry only Win32 access, sharing and lock failures, at most eight times, with cancellable delays capped at 200 ms. If the retries are exhausted, you get an explicit contextual error.

### Creating files once

Stable runtime identities keep upstream's raw 64-byte private-key format. Create-only files use `AtomicFilePublication.TryPublishNew`, so competing processes adopt the same complete identity, and corrupt existing files are never replaced. The same boundary is used for configuration files, runtime, Git and I2P identities, initial Git statistics and non-overwriting received files.

Create-only publication writes and closes a staging file in the same directory, then publishes it with an atomic hard link (`CreateHardLinkW` on Windows, `link` on Unix) and removes the staging file. This avoids the existence-check/rename race in .NET 10's Unix `File.Move(overwrite: false)`. The helper itself does not delete staging files; its caller owns cleanup on success, collision and failure. Native error context retains both paths and the OS error.

The filesystem must support hard links. This is verified on Windows NTFS and Linux ext4. Unsupported filesystems fail explicitly rather than falling back to a racy overwrite.

On Windows, ordinary paths are made absolute and normalized, then converted to `\\?\` (local) or `\\?\UNC\` (UNC). Existing extended or device prefixes are preserved. This keeps create-only publication usable beyond `MAX_PATH` without a `longPathAware` manifest or a registry setting. Published Windows NativeAOT `rncp -p` processes verified concurrent identity creation and restart at paths over 650 characters containing Unicode, spaces, `#` and `%`, through local, extended-local, loopback UNC and extended-UNC paths. The UNC tests need an explicitly authorized writable `DOTRETICULUM_PUBLICATION_UNC_ROOT` and otherwise skip with a reason. Loopback administrative-share evidence is not remote SMB server acceptance.

### Failures and limits

A missing file means initial empty state. Corrupt, unsupported, oversized or invalidly signed files raise contextual exceptions. The runtime never silently reports success or regenerates identities or keys.

| Limit | Value |
| --- | --- |
| Public state file | 64 MiB |
| Local secret ratchet file | 4 MiB |
| Collection entries | 65,536 |

## Local ratchet persistence

Like upstream `Destination.enable_ratchets(path)` and `rotate_ratchets` (`Destination.py:228-241,477-500`), persisting a destination's private ratchets is an **explicit per-destination opt-in**. It is not turned on automatically for every local destination:

```csharp
using var ratchets = await stateStore.OpenPersistentRatchetsAsync(
    destination.Hash, identity, cancellationToken);
```

Assign this ring to the local destination's `Ratchets` configuration. Automatic announce rotation then persists the signed candidate **before** returning the new public key for announcement; no extra save hook is needed. Opening persists an empty ring if no file exists. A corrupt existing file still fails rather than being reset.

Rotation, and changes to `Retained`, `Interval` or `Enforce`, persist synchronously before they are committed. This is intentional, because packet creation and rotation are synchronous. If persistence fails, the error surfaces to the announcing caller, the uncommitted new key is zeroed, and the previous ring, rotation time and retained keys are kept.

Ownership rules:

- The returned ring owns an independent clone of the signing identity. You still own the identity you passed in and may dispose it after opening.
- Disposing the ring zeroes its private ratchets and its owned signer.
- The opening cancellation token controls loading and the first save only. Cancelling it later does not disable rotation.
- Use one live ring writer per destination. The explicit snapshot save and load helpers do not coordinate independent owners or replace the automatic persistence binding.

At the Core boundary, `DestinationRatchets.EnablePersistence(Action<DestinationRatchetSnapshot>, IDisposable? ownedLifetime = null)` binds a mandatory synchronous persistence callback exactly once. The initial save must succeed before the binding and lifetime ownership transfer. The callback receives a borrowed snapshot that is valid during the call, and the ring zeroes and disposes it on success or failure. Callbacks may inspect the ring but must not mutate or dispose it; reentrant mutation is rejected. Snapshot imports create independent **unbound** rings, not duplicate persistence owners.

When decryption fails, the ring reloads authenticated keys and retries, without replacing live rotation, retention or enforcement policy and without binding later reads to the opening token. Missing or corrupt files on reload fail explicitly while keeping usable in-memory keys. Selected-ratchet IDs are detached per-delivery metadata, not shared mutable identity state. See [endpoint ownership](endpoints.md) for signed upstream-format file interchange and intentional recovery differences.

`DestinationRatchetSnapshot` owns its cloned secrets and must be disposed. Persistence zeroes owned secret snapshots and serialized secret buffers on success, failure and cancellation. Ratchet decrypt, rotate, trim and dispose share one lock, so a private key cannot be zeroed while in use.

### Local ratchet file format

The local ratchet file is a deliberate .NET format, `{"payload": binary, "signature": binary}`. The signed payload map holds `version=1`, `destination`, `identity`, newest-first `keys`, `rotation`, `retained`, `interval_ticks` and `enforce`. Local keys expire by retained count, not wall time. This file is not claimed to be directly readable by upstream.

## Resource storage

New streamed resource storage defaults to `<config>/storage/resources` for daemon and application nodes. Startup and 15-minute maintenance perform bounded, owner-marked 24-hour scavenging. Active native files, unrelated names and other configuration directories are not deleted. Resource files are ephemeral; they are not a cross-restart transfer-resume format. See [resource storage](resources.md).

## Differences from upstream

- One atomic aggregate state generation replaces upstream's independently overwritten files.
- The public ratchet cache checks expiry on every read.
- Known-destination capacity is bounded; retained entries are never evicted, and a full retained table fails explicitly.
- The local ratchet file signs all rotation metadata and binds the destination to its owning identity, where upstream signs only the keys.
- The local ratchet file preserves the recorded rotation schedule, where upstream resets rotation time to zero on restart.
- A failed ratchet save rolls back the uncommitted rotation. Upstream instead disables the ring on a save failure.

## Limitations

- The aggregate checksum detects corruption only. It does not authenticate the publisher or protect against a local writer.
- Writes are not a cross-file transaction, do not fsync the directory and do not coordinate between independent hosts.
- On Windows, file protection depends on the directory ACL.
- Create-only publication needs a filesystem with hard links.

## Verification

The independent generator `tests/DotReticulum.Applications.Tests/GenerateRuntimeStateVectors.py` executes the reference at RNS 1.5.7 without modifying its source. Its synthetic owner, active interface and frozen clock supply the external runtime context. The fixture records the imported source hashes:

| Source | SHA-256 |
| --- | --- |
| `Identity.py` | `ac336bb2b7217278d3a962b6945212313fed0ee9cc10d666e8ac17faca17ea2f` |
| `Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |
| `Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |

`tests/DotReticulum.Applications.Tests/RuntimeStateStoreTests.cs` covers atomic generations, corruption, bounds, persistence and recovery. Identity and ratchet contracts are also exercised by the Core tests. Vector agreement does not authenticate a local state-file publisher or prove live-node interoperability.
