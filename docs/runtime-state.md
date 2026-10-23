# Runtime persistence contracts

Reference: hosted RNS 1.5.7, commit `a29bfb51c468945c96a1ee22cae0114e993e2391`.
The independent generator `tests/DotReticulum.Applications.Tests/GenerateRuntimeStateVectors.py`
executes the reference without modifying its source. Its synthetic owner, active interface and
frozen clock replace external runtime context only. The fixture records actual imports and hashes:

| Source | SHA-256 |
| --- | --- |
| `Identity.py` | `ac336bb2b7217278d3a962b6945212313fed0ee9cc10d666e8ac17faca17ea2f` |
| `Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |
| `Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |

## Destination use and retention

`KnownDestinations` mirrors `Identity.py:101-335`: reannouncing replaces announce data
but preserves usage; recall and app-data recall mark use unless explicitly suppressed.
Usage `0` means never used, `-1` retained, and positive fractional Unix seconds mean last use.
Using retained data returns false without releasing retention; unretain records the current use
time. Identity retention applies to every remembered destination with that public identity.
Detached snapshots preserve binary hashes and fractional timestamps. Legacy second-valued
`RememberedUnix` / `RatchetReceivedUnix` remain available alongside precise timestamp properties.

Cleanup protects retained destinations and destinations with an existing path. Used pathless
entries expire strictly after seven days times 1.25. The pinned reference's `elif` also compares
`now - 0` against that timeout for never-used entries, so at contemporary Unix times even a newly
remembered pathless, never-used entry is removed; this surprising behavior is reproduced and
independently recorded, not silently changed to a six-minute guarantee.

Public ratchets follow `Identity.py:410-513`: unchanged announcements do not refresh their original
received timestamp; expired keys are unavailable on restart and cleanup. The .NET cache additionally
checks expiry on every get, instead of keeping an expired in-memory key until a later cleanup.
Capacity is a deliberate bounded-.NET policy: eviction never removes explicitly retained data.
If all slots are retained, remembering a new destination fails explicitly.

## Formats and ownership

`PersistenceFormats` reproduces these upstream MessagePack schemas byte for byte:

* Known destinations: binary destination-hash map to
  `[last_announce, packet_hash, public_key, app_data, last_use]`;
  four-field legacy entries load with `last_use = 0`.
* Paths (`Transport.py:3788-3876`):
  `[destination_hash, timestamp, received_from, hops, expires, random_blobs, interface_hash, packet_hash]`.
* Tunnels (`Transport.py:3879-3950`):
  `[tunnel_id, interface_hash_or_null, paths, expires]`.
* Local destination ratchet interchange (`Destination.py:211-221,437-475`):
  `{"signature": identity.sign(packed_keys), "ratchets": packed_keys}`.
* Packet hashes are full 32-byte hashes, matching `packet_hashlist.raw`.

Persisted paths allow zero hops for shared-instance local clients: upstream
`Transport.py:1937` removes the shared-instance hop before recording their path.

The runtime deliberately uses **one atomic generation**, not independent overwrites of upstream's
several files. `runtime-state-v1.msgpack` is a MessagePack map
`{"version": 1, "payload": binary, "sha256": binary}`. The payload map contains
`known_destinations`, `paths`, and `tunnels` as binary upstream-format blobs;
`known_ratchets` as `[destination_hash, public_key, received_timestamp]` rows;
`packet_hashes` as full binary hashes; and `cached_packets` as
`[packet_hash, raw_wire_packet, is_announce]` rows. Forced resource proofs can append a
fourth field, their original fractional Unix cache timestamp; legacy three-field rows
remain readable. Import assigns a missing proof timestamp once, and subsequent saves
preserve that age rather than extending expiry on every restart.
The checksum detects accidental corruption;
it does **not** authenticate the publisher or protect against a local writer.
Cached packet hashes and signed announces are validated during save and load.
Only public material belongs in this aggregate. Expired paths and tunnels are filtered on load.

`IRuntimeStateProvider` is the router integration boundary. It must capture synchronized existing
state, resolve persistent interface hashes to active interfaces, require the matching valid cached
announce before restoring paths, and apply current blackhole policy. Tunnel paths are loaded
**dormant**, without binding an old accepted TCP peer or resurrecting links. The live transport
restores them only after a signed synthesis from the same remote transport/interface identity
arrives on a current bearer. See [transport controls](transport-controls.md).

Tunnel synthesis follows `Transport.py:2764-2874`: the 176-byte payload is
`public_key(64) || interface_hash(32) || random_hash(16) || signature(64)`, with the signature
covering the preceding 112 bytes and the tunnel ID equal to SHA-256 of the first 96 bytes.
It is addressed to the plain `rnstransport.tunnel.synthesize` destination.
Invalid signatures, malformed keys and blocked transport identities do not establish tunnels.
The existing strict public-key checks remain in force.

Accepted announcements associate their actual path and signed cached packet with the ingress
tunnel. Detaching an interface voids its live association but retains these paths for recovery.
Authenticated reappearance rebinds eligible paths to the new interface: hop count/expiry and
the newest five-byte announce timebase decide whether a current path can be replaced.
Rejected restoration candidates are removed from that tunnel, not broadcast as fresh announces.
Tunnel and tunnel-path housekeeping use the reference's eight-hour windows, including rejection
of implausibly far-future tunnel expiry. Runtime tables are bounded to 1024 tunnels and 65,536
tunnel paths; tunnel-capacity eviction is diagnosed, and the aggregate path bound
evicts the oldest saved path. No arbitrary packet cache is inferred from this.

Stable runtime identities retain upstream's raw 64-private-byte format. Atomic create-only
publication makes competing processes adopt the same complete identity; corrupt existing files
are not replaced. Local private ratchets use
`destination-ratchets/<destination-hash>.v1.msgpack`, a deliberate .NET format:
`{"payload": binary, "signature": binary}`. The signed payload map holds `version=1`,
`destination`, `identity`, newest-first `keys`, `rotation`, `retained`, `interval_ticks`,
and `enforce`. Unlike upstream's key-only signature, this binds all rotation metadata and the
destination to the owning identity. Unlike upstream's restart reset to rotation time zero,
the .NET format preserves the recorded rotation schedule. Local keys expire by retained count,
not wall time. This file is not claimed to be directly readable by upstream.

## Explicit persistent local ratchets

Like upstream `Destination.enable_ratchets(path)` and `rotate_ratchets`
(`Destination.py:228-241,477-500`), persistence is an **explicit destination opt-in**, not an
automatic setting for every local destination:

```csharp
using var ratchets = await stateStore.OpenPersistentRatchetsAsync(
    destination.Hash, identity, cancellationToken);
```

Assign this ring to the local destination's `Ratchets` configuration. Existing automatic
announce rotation then persists the signed candidate **before** returning the new public key
for announcement. No additional runtime/announcement save hook is required. Initial opening
persists the empty ring if no file exists; corrupt existing files still fail rather than reset.
Rotation and changes to `Retained`, `Interval` or `Enforce` synchronously persist before committing.
The synchronous durability boundary is intentional because packet creation/rotation is synchronous.
Failures surface to the announcement caller, zero the uncommitted new key, and preserve the
previous ring, rotation time and retained keys. This transactional rollback is a deliberate
stronger .NET recovery behavior than upstream's disabling a ring on a save failure.

The returned ring owns an independent signing-identity clone; the caller still owns its supplied
identity and may dispose it independently after opening. Disposing the ring zeros both its private
ratchets and its owned signer. The opening cancellation token controls loading and the first save
only; later rotations are not disabled if that token is subsequently cancelled. Use one live ring
writer per destination; the explicit snapshot save/load helpers do not coordinate independent
owners or replace the automatic persistence binding.

At the Core boundary, `DestinationRatchets.EnablePersistence(Action<DestinationRatchetSnapshot>,
IDisposable? ownedLifetime = null)` binds a mandatory synchronous persistence callback exactly once.
The initial save must succeed before the binding/lifetime ownership transfers. The callback receives
a borrowed snapshot valid during the call; the ring zeroes/disposes it on success or failure.
Callbacks may inspect but must not mutate or dispose the ring; reentrant mutation is rejected.
Snapshot imports intentionally create independent **unbound** rings, not duplicate persistence owners.

Every write stages an exclusively created same-directory file, flushes its contents, and atomically
renames it into place. Cancellation before publication preserves the preceding generation.
Files are created owner-readable/writable only on Unix; Windows inherits the directory ACL,
which the operator must protect. Atomic replacement provides complete-file visibility, not a
cross-file transaction with identity files, filesystem-directory fsync durability, or coordination
between independent hosts. Only the shared instance owner should persist shared state.
On Windows, concurrent delete-shared readers/replacers can briefly leave a filename delete-pending.
Reads and replacement retry only Win32 access/sharing/lock failures, at most eight retries with
cancellable delays capped at 200 ms; exhaustion remains an explicit contextual error.

Missing files mean initial empty state. Corrupt, unsupported, oversized or invalidly signed files
raise contextual exceptions without silently reporting success or regenerating identities/keys.
Limits are 64 MiB per public state file, 4 MiB per local secret ratchet file and 65,536 collection
entries. Capture validates before replacing the committed generation.
`DestinationRatchetSnapshot` owns its cloned secrets and must be disposed; persistence zeroes its
owned secret snapshots and serialized secret buffers on success, failure and cancellation.
Ratchet decrypt/rotate/trim/dispose share one lock so a private key cannot be zeroed while in use.
