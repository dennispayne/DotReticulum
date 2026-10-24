# Resource contracts

Reference: RNS 1.5.7, `a29bfb51c468945c96a1ee22cae0114e993e2391`,
`RNS/Resource.py` SHA-256
`9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699`.
Relevant contracts: `accept` lines 169–259; constructor/file segmentation
261–482; hashmap updates 485–514; assembly 685–777; next segment and
proof handling 779–839; cancel 1090 onward; advertisement 1255 onward.

Acceptance criteria for this slice:

- File/stream input reads at most one segment at a time, with the same
  metadata-first segment boundary and hashes as independently executed RNS.
- Receive storage spills above an explicit memory threshold; every failure,
  cancellation and link close releases owned work and multipart storage.
- Reject negative/oversized/inconsistent advertisements, noncontiguous HMUs,
  wrong-sized parts, invalid metadata and decompression expansion before
  unbounded allocation or successful proof.
- Cancellation, rejection and success release the next queued resource and
  deliver at most one terminal callback per logical transfer.
- Preserve the byte-array API for ordinary resources. Stream/file input and
  disk-backed output have explicit ownership and cleanup.

The reference has no resource-level cross-link/restart resume protocol.
Retransmitted part requests resume an active segment; subsequent segments
retain the original hash. Persistent arbitrary-offset resume is not invented.
`GenerateParity5ResourceVectors.py` executes the verified pinned reference
before implementation. Its XOR encryption shim is disclosed in the script;
these fixtures are segmentation/compression evidence, not live interop.

## API and ownership

`Link.SendResource(byte[])` remains available. `SendResource(Stream)` sends
the remaining bytes from a readable, seekable stream; ownership transfers
unless `leaveOpen: true`. `SendResourceFile(path)` owns its opened file.
Only one segment (at most 1,048,575 plaintext bytes) is prepared at a time.
Preparation is synchronous and bounded, not an unjoined speculative worker.
The cancellation token remains active throughout the logical transfer.

`SendResourceAsync(Stream)` also accepts forward-only streams. It first
copies to a size-bounded owned disk proxy, then advertises the resource.
Preparation errors fault the returned task before advertisement. Streams
must honor cancellation for asynchronous reads; arbitrary custom blocking
stream implementations cannot be made cancellation-aware by the library.
The link cancels and joins its asynchronous proxy/assembly jobs on teardown
through `DrainResourceWorkAsync()`; call this outside the link lock.
Owned non-null inputs are also disposed on validation, property-access,
queue-admission and initial preparation failure. `leaveOpen: true` prevents
that disposal. Ownership is transferred exactly once between the public
entry point, proxy and resource; callers must not dispose again on a helper
exception after handing over an owned stream.

An application request handler may return
`StreamFileResponse(Stream Data, object? Metadata = null, bool LeaveOpen = false)`
instead of the existing `FileResponse(byte[], metadata)`. The response sends
directly from a seekable stream, or uses the same bounded proxy for
forward-only input. The stream is closed after the logical transfer unless
ownership is explicitly retained. The requester's existing response API
still returns bytes; resource spooling avoids holding every segment before
completion, but reading a MessagePack request or legacy byte-array response
necessarily materializes that application value. Upstream similarly packs
request values before resource transfer; no new streaming MessagePack
request wire format is introduced.

Completed resources within the configured memory threshold retain `Data`.
Larger resources have `IsFileBacked == true`; use `OpenRead()` to avoid
materializing their bytes. Accessing `Data` lazily reads disk storage into a
byte array, preserving the existing in-memory API when explicitly requested.
`HasDataInMemory` observes this without triggering a read. A resource larger
than `Int32.MaxValue` must use streaming rather than `Data`.
Dispose the resource when finished. Returned read streams are independently
owned. Link teardown also disposes outstanding file-backed results.
Request/response internal completion consumes and disposes its resource
without depending on optional observer dispatch. Multipart state is
in-process only; it is deleted on failure,
cancellation, expiry or teardown. There is no durable cross-restart resume.
Filesystem cleanup errors are diagnosed and do not prevent unrelated
resources from settling or queued transfers from being released. An OS
denial can nevertheless leave a file; no library can guarantee deletion
when the filesystem refuses it.
New plaintext receive spools, input proxies and ownership markers use
`FileStreamOptions.UnixCreateMode = UserRead | UserWrite` on Unix (subject
to a more restrictive process umask). Windows files inherit directory ACLs;
applications must protect the spool directory with appropriate ACLs.
Existing caller-owned input permissions are not changed.

## Expired resource storage

`ResourceStorageScavenger.Initialize(storageDirectory)` explicitly marks a
trusted storage directory; native writers also initialize it on first use.
The `.dotreticulum-resource-storage` marker must contain the exact supported
version. Scanning an unmarked or invalid-marker directory never claims it or
deletes its contents. Callers must select a trusted directory: the marker
is ownership identification, not protection against a malicious process
that can already modify that directory.

`ResourceStorageScavenger.Scavenge(storageDirectory, diagnostic, now,
maxEntries: 4096, cancellationToken)` removes only exact native names
`resource-<32 lowercase hex>.bin` and `input-<32 lowercase hex>.bin` whose
last-write time is **strictly older** than 24 hours. It never recursively
scans, deletes unrelated names or directories, or follows reparse points
on the storage directory, marker or candidate. Ancestors of the explicitly
trusted directory must themselves be trusted.

An exclusive read/write sharing lease excludes open native writers and
readers, including active jobs in another process on filesystems supporting
the platform's file-sharing semantics. Age is checked again after acquiring
the lease. Open/unavailable candidates are retained and diagnosed. There is
no static directory/job registry to grow indefinitely. The result reports
ownership, examined/deleted/fresh/active-or-unavailable/unrelated/error
counts and entry-budget exhaustion. A budget-limited pass is not a complete
scan: inspect `ReachedLimit` and choose a larger explicit budget if needed.
Cancellation propagates; diagnostic-observer exceptions are traced without
disabling cleanup. Invalid ownership markers are never overwritten and
require operator repair.

### Config-owned application defaults

The pinned reference sets `resourcepath` to the configuration directory's
`storage/resources` (`Reticulum.py:247–249`) and creates it at startup
(lines 327–329). A library-only default may remain relative to the current
directory; application nodes must instead default to their own configuration
storage, independent of CWD.

`LinkManager.DefaultResourcePolicy` validates defaults for new links.
`ResourcePolicy` is an immutable record. Initiator and recipient
constructors capture their manager's current default without sharing
mutable policy state. Reassigning `link.ResourcePolicy` still overrides one
link; changing the manager default affects only subsequently created links.

`RnsdHost` installs `<config>/storage/resources` before links/services start.
This also covers application nodes, which delegate to that host.
Startup initializes and scavenges the owned directory; runtime maintenance
repeats the bounded pass every 15 minutes and reports cleanup diagnostics.

If an application overrides a link's storage directory,
it must maintain that explicitly owned directory too; scanning the default
does not scan arbitrary policy overrides. Pester exercises two receiver
configurations sharing one CWD: each receives a 73,400,357-byte streamed file,
spills above 64 MiB in its own `storage/resources`, persists matching output,
removes expired owned cache files and leaves fresh/unrelated/CWD files intact.

Pinned `RNS/Reticulum.py:157,1333–1340` supplies the 24-hour age and strict
boundary; `Resource.py:200–201,721–724` supplies the reference cache
ownership/naming behavior. `Reticulum.py` SHA-256 is
`c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7`.
The independent generator executes the actual reference cache cleaner at
ages 86,401/86,400/86,399 seconds plus an unrelated old name. Native random
names and the ownership marker intentionally avoid deleting reference
hash-named files or unrelated contents.

## Explicit local policy

Set `Link.ResourcePolicy` before accepting/sending resources. Defaults:

| Limit | Default |
|---|---|
| Logical uncompressed size, including metadata | 1 GiB |
| Plaintext bytes per segment | 1,048,575 |
| Parts per segment | 8,192 |
| Segments per transfer | 1,025 |
| Packed metadata | 1,048,571 bytes |
| Simultaneous incoming logical transfers | 4 |
| Queued outgoing resources | 64 |
| Retained completed disk-backed resources | 64 |
| Receive-memory threshold | 64 MiB |
| Missing-next-segment expiry | 300 seconds |
| Owned spool directory | Application: `<config>/storage/resources`; library-only default: `.reticulum/resources` under CWD |

Admission reserves worst-case file-retention slots for in-flight logical
transfers, so a lower retention quota can also lower receive concurrency.

Compression expansion is limited to the exact advertised segment plaintext
size, additionally bounded by policy and the upstream segment size. Length
and resource hash must both match before a proof is sent. Metadata length,
MessagePack decoding, initial hashmap shape, HMU sequence/shape/collision
guard and part lengths are validated. Request retransmission-hash retention
is bounded by the part limit times the retry budget.

These are intentional .NET hardening differences: upstream does not impose
all these limits, permits a larger metadata header than fits its first
segment, appends to a hash-named receive file without validating multipart
ordering, and may tear down the link on decompression overflow. Here corrupt
resources terminate once and release their own state without disabling
unrelated transfers. Files use random exclusive names, not peer-supplied
paths. No compression failure is silently converted into uncompressed data.

Upgraded-MTU resource parts use the negotiated link MTU. Advertisement/HMU
hashmap chunks deliberately remain the reference's static 74-entry chunks
(`ResourceAdvertisement.HASHMAP_MAX_LEN`, lines 1255–1258); increasing them
with link MDU would change the pinned protocol.

Targeted tests: `FullyQualifiedName~Parity5Resource`,
`FullyQualifiedName~ResourceTests`, `FullyQualifiedName~ResourceVectorTests`.
Fixture generation alone is not live/native execution evidence.

## Lifecycle integration and remaining evidence

`Link.DrainResourceWorkAsync()` returns a `Task` joining owned input-proxy
and assembly jobs, including cancellation callbacks. `CancelResources`
initiates tracked asynchronous input cancellation during link closure.
Caller-stream token callbacks execute off the protocol lock; blocking or
throwing callbacks cannot synchronously prevent core closure. Their
exceptions are diagnosed, and the cancellation source is disposed only
after its callbacks finish. Mandatory cleanup still joins those callbacks:
a non-cooperative callback can delay shutdown, not be abandoned.
`Link.CloseLocked` registers the drain through
`LinkManager.ObserveRequiredCleanup`; `StopCoreAsync` fully joins required
cleanup before its bounded optional-observer wait. The optional callback
timeout does not abandon resource work. The focused manager-stop regression
holds an input's cancellation cleanup behind an explicit gate for more than
one second and requires stop to remain pending until that gate is released.

`Resource.InternalCallback` and the final optional
`Resource.Accept(..., bool internalCallback = false)` parameter distinguish
protocol settlement/progress from user observations. Internal
request/response callbacks execute immediately under the link gate; those
callbacks dispatch only their optional notifications through the observer
queue. Arbitrary user callbacks do not receive this internal flag.
Core terminal settlement occurs once. Optional observer dispatch is bounded;
an exhausted observer queue can reject a notification with a diagnostic,
rather than block core settlement. Thus observer overload does not promise
successful invocation of every optional callback.

Acceptance mapping (execution evidence is recorded in the development history):

| Parity 5 resource item | Implementation/evidence boundary |
|---|---|
| File-backed/streamed multipart and applicable resume | Seekable input, bounded forward-only proxy, streamed file responses, receive spooling; independent three-segment metadata vectors. Active-part retries remain; no cross-restart resume protocol exists upstream. |
| Queue release and once-only terminals | Synchronous release on success/rejection/cancel; once-only core terminal settlement, diagnosed optional-observer overload policy. |
| Dispose/cancel/join preparation, assembly and multipart state | Mandatory manager cleanup join, per-resource async disposal, cancellation/expiry/rejection cleanup, parked request-resource cancellation; gated manager-stop and ownership failure tests. |
| Malformed metadata/advertisement/HMU/segment/compression and limits | Strict size/type/order/shape checks, bounded decompression, metadata decoding and explicit local policy. |
| Post-crash cache cleanup | Config-owned startup/15-minute maintenance, strict 24-hour reference vectors, native-name filtering and active-file lease; expiry, ownership, budget, diagnostic-isolation and separate-process tests. |

Remaining limitations and evidence boundaries:

- Reference-generated segmentation covers three segments with metadata;
  decompression covers limits 0, 65,536 and 65,537 plus truncated input.
- Managed focused tests include one-byte receive spooling and the minimum
  multipart boundary (1,048,576 payload bytes), file-backed three-segment
  transfer, explicit ownership, queue release for success/rejection/cancel,
  internal settlement with all optional observer slots occupied, malformed
  advertisements/HMUs/metadata, assembly cancellation, preparation faults,
  storage faults, quota release and missing-next-segment expiry.
- Actual resource packets at MTUs 1,024, 16,384 and 262,144 are tested
  in-process. Live Python mixed-transport file-backed flows exercise
  1,024/16,384-byte bottlenecks and 40-ms bridge RTT; direct Python
  requests also read streamed multipart file responses.
- Windows native rncp executes encrypted/compressed multi-megabyte
  transfers and the 70-MiB spill flow. This is not filesystem, physical-radio
  or NativeAOT support evidence for another platform.
- Cross-link/restart arbitrary-offset resume is not applicable: the pinned
  reference exposes no such resource protocol. Retry-based resumption is
  limited to an active segment and in-process multipart continuation.
- Caller-supplied streams must honor cancellation. An intentionally
  non-cooperative stream can prevent shutdown from completing: mandatory
  joining deliberately does not abandon that work. Seekable reads,
  stream disposal, compression and filesystem operations are synchronous
  or caller-owned operations;
  cancellation is checked between operations, not a guarantee of
  interrupting an arbitrary blocking caller/kernel operation.
- MessagePack request values and legacy byte-array response values are
  materialized when decoded/consumed, even when their resource transport
  used disk spooling. No streaming application-value wire extension is
  claimed.
- Scavenger active-file exclusion is exercised on the host; it is not
  evidence for every OS or network filesystem.
