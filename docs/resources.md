# Resources

A *resource* is a reliable transfer of an arbitrary amount of data over an established link. Where a single packet is limited by the link MDU, a resource lets you send a file, a stream or a large byte array and have Reticulum take care of segmentation, sequencing, compression, checksumming and retransmission for you.

## How a transfer works

The sender first advertises the resource, along with any metadata. The receiver decides whether to accept it, then requests parts through a sliding window. Each part is verified against the advertised hashmap, and the hashmap is extended with update messages as the transfer progresses. When all parts have arrived, the receiver checks the length and the final resource hash, and only then sends a proof back to the sender.

Large inputs are split into segments of at most 1,048,575 plaintext bytes. Each segment is advertised and transferred in turn, and all segments carry the original resource hash. Data can be compressed before transfer, and everything travels under the link's encryption.

Compression, encryption, hashmap updates, cancellation, rejection and segmentation follow the pinned reference. The reference has no protocol for resuming a transfer across links or restarts. A retransmitted request can resume an active segment, but DotReticulum does not invent arbitrary-offset persistence or resume.

## Sending a resource

```csharp
Resource resource = link.SendResourceFile("archive.tar", metadata: new Dictionary<object, object?> { ["name"] = "archive.tar" });
```

You can send from several kinds of input:

| Method | Input | Ownership |
| --- | --- | --- |
| `SendResource(byte[])` | Byte array | Caller |
| `SendResource(Stream)` | Remaining bytes of a readable, seekable stream | Transferred, unless `leaveOpen: true` |
| `SendResourceFile(path)` | File opened by the link | Link |
| `SendResourceAsync(Stream)` | Any readable stream, including forward-only streams | Transferred, unless `leaveOpen: true` |

Files and streams are read one segment at a time. Preparing a segment is synchronous and bounded rather than an unjoined speculative worker. The cancellation token stays active for the whole logical transfer.

`SendResourceAsync` first copies forward-only input to a size-bounded, owned disk proxy and only then advertises the resource. Preparation errors fault the returned task before advertisement. Your stream must honour cancellation for asynchronous reads; the library cannot make an arbitrary blocking stream cancellation-aware.

Ownership is transferred exactly once between the public method, the proxy and the resource. Owned inputs are also disposed when validation, property access, queue admission or initial preparation fails. Once you have handed over an owned stream, do not dispose it again after a helper exception.

### Responding with a stream

A request handler can return `StreamFileResponse(Stream Data, object? Metadata = null, bool LeaveOpen = false)` instead of `FileResponse(byte[], metadata)`. The response is sent directly from a seekable stream, or through the same bounded proxy for forward-only input. The stream is closed after the transfer unless `LeaveOpen` is set.

The requester's response API still returns bytes. Resource spooling avoids holding every segment until completion, but reading a MessagePack request or a byte-array response necessarily materializes that value. Upstream similarly packs request values before transfer, and no streaming MessagePack request format is introduced.

## Receiving a resource

```csharp
link.ResourceStrategy = ResourceStrategy.AcceptAll;
link.ResourceReceived = resource =>
{
    using var input = resource.OpenRead();
    // Copy or hand off the data before returning.
};
```

Incoming resources are rejected by default (`ResourceStrategy.AcceptNone`). With `AcceptApp`, the link calls `ResourceAdvertised` and accepts the resource when your callback returns true.

A completed resource within the receive-memory threshold keeps its bytes in `Data`. Larger resources spill to disk and report `IsFileBacked == true`:

* `OpenRead()` returns an independently owned stream and avoids materializing the bytes.
* Reading `Data` loads disk storage into a byte array on demand. `HasDataInMemory` tells you whether that has happened without triggering a read.
* A resource larger than `Int32.MaxValue` bytes must be read as a stream.
* Dispose the resource when you are finished with it. Link teardown also disposes outstanding file-backed results.

### Callback lifetime

A synchronous `ResourceReceived` or concluded callback for a completed incoming resource holds a reservation on its data before it enters the bounded observer queue. Peer teardown or explicit disposal cannot invalidate `OpenRead()`, `Data` or metadata while the callback runs. Retaining the resource object is not a lease after the callback returns, so open a reader inside the callback if you need the data later.

* Multicast observers are isolated, and failures are logged with the resource hash.
* No observer runs under the protocol lock.
* Disposal requested while a callback is queued or running releases storage when the callback returns, including when it throws. Rejected notifications release their reservation too.
* `DisposeAsync()` joins assembly work but does not wait for an observer, which may itself be calling dispose.
* Manager shutdown keeps its bounded observer wait instead of waiting indefinitely for application code.
* No payload copy or extra unbounded callback queue is introduced.

Request and response completion consumes and disposes its resource internally, without depending on optional observers.

### Spool files

Multipart state is held in-process only and is deleted on failure, cancellation, expiry or teardown. There is no durable resume across restarts.

New plaintext receive spools, input proxies and ownership markers are created with `FileStreamOptions.UnixCreateMode = UserRead | UserWrite` on Unix, subject to a more restrictive umask. On Windows, files inherit the directory's ACLs, so you must protect the spool directory yourself. Permissions on your own input files are not changed.

Filesystem cleanup errors are diagnosed and do not stop unrelated resources from settling or queued transfers from being released. If the operating system refuses a deletion, a file can be left behind; no library can guarantee otherwise.

## Resource policy

`Link.ResourcePolicy` sets local limits for one link. Set it before accepting or sending resources. These limits are local hardening, not wire fields.

| Limit | `ResourcePolicy` property | Default |
| --- | --- | --- |
| Logical uncompressed size, including metadata | `MaxDataSize` | 1 GiB |
| Plaintext bytes per segment | `MaxSegmentSize` | 1,048,575 |
| Parts per segment | `MaxParts` | 8,192 |
| Segments per transfer | `MaxSegments` | 1,025 |
| Packed metadata | `MaxMetadataSize` | 1,048,571 bytes |
| Simultaneous incoming logical transfers | `MaxIncomingResources` | 4 |
| Queued outgoing resources | `MaxQueuedResources` | 64 |
| Retained completed disk-backed resources | `MaxCompletedFileResources` | 64 |
| Receive-memory threshold | `MemoryThreshold` | 64 MiB |
| Missing-next-segment expiry | `MultipartTimeout` | 300 seconds |
| Owned spool directory | `StorageDirectory` | Application: `<config>/storage/resources`. Library-only: `.reticulum/resources` under the current directory |

Admission reserves worst-case retention slots for in-flight transfers, so a lower retention quota can also lower receive concurrency.

`LinkManager.DefaultResourcePolicy` validates and supplies the default for new links. `ResourcePolicy` is an immutable record. Initiator and recipient links capture the manager's default when they are created, without sharing mutable state. Assigning `link.ResourcePolicy` overrides one link, and changing the manager default affects only links created afterwards.

### Validation

Before any proof is sent, both the length and the resource hash must match. The receiver also validates:

* metadata length and MessagePack decoding
* the initial hashmap shape
* hashmap update sequence, shape and collision guard
* part lengths
* decompressed size, which may not exceed the advertised segment plaintext size, the policy or the upstream segment size

Malformed input is rejected before unbounded allocation or a successful proof. Retention of request retransmission hashes is bounded by the part limit times the retry budget. A decompression failure is never silently treated as uncompressed data.

Upgraded-MTU links send resource parts at the negotiated link MTU. Advertisement and hashmap update chunks deliberately stay at the reference's static 74 entries (`ResourceAdvertisement.HASHMAP_MAX_LEN`, `Resource.py` lines 1255–1258), because scaling them with the link MDU would change the pinned protocol.

## Resource storage

The pinned reference keeps resource data under the configuration directory's `storage/resources` (`Reticulum.py:247–249`), creates it at startup (lines 327–329) and removes cache files older than 24 hours.

`RnsdHost` installs `<config>/storage/resources` as the default before links and services start, and application nodes inherit this by delegating to the host. Unlike the library-only default, it does not depend on the current directory. At startup the host initializes and scavenges the directory, and runtime maintenance repeats a bounded pass every 15 minutes with cleanup diagnostics. If you override a link's storage directory, you must maintain that directory yourself; scanning the default does not cover policy overrides.

### Scavenging expired files

`ResourceStorageScavenger.Initialize(storageDirectory)` marks a trusted directory as owned. Native writers also initialize it on first use. The `.dotreticulum-resource-storage` marker must contain the exact supported version. An unmarked directory, or one with an invalid marker, is never claimed and its contents are never deleted. Invalid markers are not overwritten and require operator repair.

The marker identifies ownership. It does not protect against a malicious process that can already modify the directory, so choose a trusted directory with trusted ancestors.

`ResourceStorageScavenger.Scavenge(storageDirectory, diagnostic, now, maxEntries: 4096, cancellationToken)` removes only files that:

* have the exact native names `resource-<32 lowercase hex>.bin` or `input-<32 lowercase hex>.bin`, and
* were last written strictly more than 24 hours ago.

It never scans recursively, deletes unrelated names or directories, or follows reparse points on the directory, marker or candidate.

Before deleting, the scavenger takes an exclusive read/write sharing lease. This excludes open native writers and readers, including active jobs in other processes on filesystems that support the platform's sharing semantics. The age is checked again after the lease is acquired, and open or unavailable files are kept and diagnosed. There is no static registry of directories or jobs to grow without bound.

The result reports ownership and counts of examined, deleted, fresh, active or unavailable, unrelated and failed entries. A pass that hits its entry budget is not a complete scan, so check `ReachedLimit` and choose a larger budget if needed. Cancellation propagates, and exceptions from the diagnostic callback are traced without stopping cleanup.

## Lifecycle

`Link.DrainResourceWorkAsync()` joins the link's owned input-proxy and assembly jobs, including cancellation callbacks. Call it outside the link lock. Link teardown uses it to cancel and join asynchronous work.

Link closure tracks asynchronous cancellation and required cleanup. Mandatory resource work is joined even when the optional-observer wait expires. A non-cooperative stream or synchronous callback can therefore delay shutdown, because the library does not abandon required cleanup. Optional notification overload is diagnosed and may reject a notification without undoing settled protocol state.

Cancellation, rejection, success, expiry and link teardown all release owned transfer work and multipart storage.

## Differences from upstream

These are intentional hardening differences:

* Upstream does not impose all of the limits in the resource policy.
* Upstream permits a metadata header larger than fits in its first segment.
* Upstream appends to a hash-named receive file without validating multipart ordering. DotReticulum uses random exclusive file names, never peer-supplied paths.
* Upstream may tear down the link on decompression overflow. In DotReticulum a corrupt resource fails once and releases its own state without affecting other transfers.
* Native spool names and the ownership marker deliberately avoid deleting the reference's hash-named files or unrelated contents.

## Limitations

* There is no resume across links or restarts, matching the reference.
* The operating system can refuse to delete a spool file.
* Custom blocking streams cannot be made cancellation-aware by the library.
* Physical-radio behaviour, arbitrary filesystems and platform-wide parity are not established. See [TODO.md](../TODO.md) for missing and unverified capabilities.

## Verification

The reference is RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`:

* `RNS/Resource.py`, SHA-256 `9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699`. The relevant ranges are `accept` (lines 169–259), constructor and file segmentation (261–482), hashmap updates (485–514), assembly (685–777), next segment and proof handling (779–839), cancel (1090 onward) and advertisement (1255 onward). Lines 200–201 and 721–724 define the reference cache naming and ownership.
* `RNS/Reticulum.py`, SHA-256 `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7`. Lines 157 and 1333–1340 define the 24-hour age and its strict boundary.

`tests\DotReticulum.Transport.Tests\GenerateParity5ResourceVectors.py` generates independent segmentation and compression vectors from the pinned source, and executes the reference cache cleaner at ages of 86,401, 86,400 and 86,399 seconds plus an unrelated old name. Its XOR encryption shim is disclosed in the generator. These vectors establish segmentation, compression and cleanup contracts, not live interoperability, and fixture generation alone is not live or native execution evidence.

The xUnit coverage is in `tests\DotReticulum.Transport.Tests\Parity5ResourceTests.cs`, `ResourceTests.cs`, `ResourceVectorTests.cs`, `Parity5ResourceStorageTests.cs` and `ResourceCallbackLifetimeTests.cs`. It covers reference vectors, malformed input, ownership, cancellation, queue release, file-backed transfers, quotas and storage cleanup. Focused filters are `FullyQualifiedName~Parity5Resource`, `FullyQualifiedName~ResourceTests` and `FullyQualifiedName~ResourceVectorTests`.

The callback-lifetime tests delay ordinary observers until after peer teardown for single-segment and 4 MiB multipart data held in memory and on disk. They also cover disposal from observers, shutdown with a running observer, throwing observers, no subscribers, full queues and rejected shutdown notifications, on Windows and Linux. These are deterministic stack tests, not a live Python application replay.

Beyond unit tests:

* A Pester test runs two receiver configurations sharing one current directory. Each receives a 73,400,357-byte streamed file, spills above 64 MiB into its own `storage/resources`, persists matching output, removes expired owned cache files and leaves fresh, unrelated and current-directory files intact.
* Live mixed-transport tests exchange resources with Python nodes at 1,024 and 16,384-byte bottlenecks, including streamed multipart responses.
* Windows x64 native `rncp` tests exercise encrypted, compressed multi-megabyte transfers and a 70 MiB spill flow.
* Hosted NativeAOT resource tests run on Windows x64, Linux x64 and macOS ARM64.
