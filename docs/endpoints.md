# Endpoints and signed ratchets

A destination endpoint combines a destination address with direction-specific identity, encryption, announcement and request-handler behavior. It owns only the state documented below; caller-supplied identities and ratchet rings remain caller-owned.

`Destination` remains the name/address codec. `DestinationEndpoint` owns
direction, plain/group encryption state and announce defaults/cache; it borrows
supplied identities and ratchet rings. Dispose an endpoint to erase its group
key. The factory owns an identity only when it generated that identity for an
incoming endpoint. As in RNS, generated incoming identity hashes are also
appended as an aspect. Outgoing encrypted endpoints require an identity.

Incoming SINGLE endpoints can announce. Link acceptance defaults to true.
`SetDefaultAppData` clones its input; `SetDefaultAppDataProvider` evaluates the
current default for each new announce. A null per-call value selects the default;
an empty array suppresses it. Tagged path responses reuse the signed payload
for 30 seconds (including the exact boundary), without invoking the provider.
The cache holds at most 256 entries by default and tags are limited to 16 bytes.
Oldest entries are evicted under pressure. Cached ratchet payloads preserve
their context flag; upstream clears that flag on a cache hit, which would make
the cached payload fail validation. DotReticulum intentionally fixes that bug.

PLAIN endpoints return a detached copy of unencrypted data and have no identity
or signing/symmetric-key operations. GROUP endpoints must create or load a
32-byte legacy or 64-byte current token key before encryption/decryption.
Invalid key imports leave the existing key intact. Replacement/disposal erase
owned key material. Caller-owned copies remain the caller's responsibility.

`DecryptWithMetadata` on an identity, ratchet ring or endpoint returns an owned
plaintext buffer and a detached selected-ratchet ID. It reports the key that
actually decrypted the packet, including an older retained key; base-key,
GROUP and PLAIN decryptions report no ratchet ID. Failure returns no result.
The existing `Decrypt` APIs remain available. This per-operation result avoids
upstream's mutable `latest_ratchet_id` state and the stale value left by its
short-token failure branch (`Identity.py:852–906`). `Destination.py:413–414`
copies that value to `packet.ratchet_id`; the .NET router exposes the equivalent
as receive-side metadata, without changing encoded packet bytes.

`KnownDestinations.RegisterLocal` provides public-only local recall without
inventing an announcement or persisting local private keys. Remote entries take
precedence. Local fallback does not supply app data or participate in
identity-hash searches, matching upstream. Recalled remote identities contain a
detached `AppData` value. The router must own registration/deregistration.

## Signed file API

`SignedDestinationRatchetFile.Open(path, identity)` reads/writes the upstream
`{"signature": bytes, "ratchets": packed_private_keys}` envelope. This is not
the application's existing versioned, destination-bound runtime-state format.
The signature authenticates the packed key list before it is decoded. Files
are bounded to 4 MiB. Saves write an owner-only sibling file on Unix, flush and
atomically replace the destination; Windows users must configure suitable ACLs.

The returned ring owns an independent signer and exclusive `.lock` file lease.
The API rejects another cooperating writer for that path rather than silently
losing keys. Its `.lock` file may remain after disposal but contains no secrets.
The identity supplied to `Open` remains caller-owned. Failed persistence does
not publish a new ratchet or replace the existing ring. Failed reload leaves
existing keys usable. Decrypt failure retries after a signed reload. Rotation,
reload, decryption and disposal are serialized within a ring.

Upstream does not cooperate with the `.lock` lease. Simultaneous Python/.NET
writers are not supported by this API; externally replacing a signed file can
be observed through reload but does not establish cross-process rotation
coordination. Unlike upstream's delayed second read, reload failures propagate
immediately.
Filesystem failures retain native .NET exception types: replacing a directory
with the ratchet file raises `UnauthorizedAccessException` on Windows and an
`IOException` on Unix; either failure leaves the current ring unchanged.

The application's `RuntimeStateStore.OpenPersistentRatchetsAsync` also installs
signed reload-on-decryption-failure for its versioned, destination-bound files.
Reload uses the independently owned signer and is unaffected by cancellation
of the token used to open the ring. It imports keys only: live retention,
rotation timing and enforcement policy remain unchanged, as with upstream's
key-only reload. Corruption or a missing file fails explicitly without
overwriting the file or discarding usable in-memory keys; upstream instead
reinitializes a missing ratchet file. No failure causes a fresh key to be
announced before successful persistence.

## Explicit cryptographic boundary differences

Independent observations are generated by
`tests\DotReticulum.Core.Tests\GenerateParity5EndpointVectors.py` from verified
RNS `a29bfb51c468945c96a1ee22cae0114e993e2391`. The fixture records the actual
module path, provider version and source SHA-256 values.

* Identity imports reject noncanonical/low-order X25519 and invalid Ed25519
  public keys, while the pinned reference accepts some at load time. Independent
  PyCA and upstream-internal X25519 provider vectors show that internal exchange
  accepts low-order inputs yielding zero, while PyCA rejects them. Both accept
  high-bit inputs, but their shared-secret results diverge for the observed
  alias. DotReticulum intentionally rejects these inputs.
* Token HMAC is verified before strict platform PKCS#7 unpadding. The reference
  accepts mismatched padding bytes and zero padding length after authentication;
  DotReticulum rejects these malformed tokens. Correct AES-128/AES-256 tokens
  remain byte-compatible.
* HKDF-SHA256 accepts output lengths 1–8160 and nonempty input bytes. The pinned
  reference accepts empty byte input and wraps its counter beyond 8160 bytes.
  DotReticulum deliberately retains RFC 5869 limits instead.

The endpoint vectors and managed tests are bounded contract evidence, not new
live-node or NativeAOT interoperability evidence. Router delivery/proofs and link
policy integration are separate acceptance gates.

## Requests and owned application work

Request handlers default to deny. `All` permits any link; `List` requires an
identified, allowed peer. Handlers may be replaced or deregistered by path.
`RegisterAsyncRequestHandler` receives cancellation linked to manager shutdown
and link closure. A returned owned `StreamFileResponse` is disposed even if
cancellation or closure prevents transmission; `LeaveOpen` preserves caller
ownership.

`Link.Request` installs `maxResponseSize` before transmission and accepts a
cancellation token. Limits cover small responses and resource advertisements.
Small-response sizes preserve the reference's serialized-value-minus-two
convention, including negative sizes for tiny scalar values. Receipt delivery,
final progress and response notifications preserve their order and isolate
individual observer errors. Cancellation settles the task and its packet
receipt, releases request resources and does not close the link.

The native link manager permits 1,024 links, 256 pending requests per link,
256 entries per outbound/request/observer queue, four concurrent handlers and
four concurrent optional observers. Admission failure is diagnosed, not reported
as a successful send. Core receipt/channel/resource settlement does not depend
on optional observer execution. Optional queue overload can reject notifications
with a diagnostic; it does not undo an already settled protocol operation.

Shutdown cancels owned asynchronous work and joins mandatory resource cleanup.
Untrusted synchronous handlers/observers cannot be forcibly interrupted: after
a one-second observer wait they remain tracked and keep their bounded slots
until returning, with an explicit diagnostic. Cancellation-aware handlers and
streams must cooperate; no replacement workers bypass those retained slots.
Callbacks must not synchronously await their own shutdown/completion.

Link ages and inactivity durations use the configured monotonic time source.
`EstablishmentRate` reports authenticated request/proof bytes per RTT in bits
per second; later packet proofs do not change it. `ExpectedRate` comes from
resource transfer observations, not configured bearer bitrate.

## Verification

Endpoint and ratchet contracts are covered by `tests/DotReticulum.Core.Tests/Parity5EndpointTests.cs`, `Parity5EndpointRatchetTests.cs`, `tests/DotReticulum.Transport.Tests/Parity5LocalEndpointTests.cs`, `Parity5EndpointKnownDestinationsTests.cs` and `Parity5RequestTests.cs`. The independent endpoint vector generator records pinned RNS 1.5.7 imports and provider behavior. These vectors and managed tests are not, by themselves, live-node interoperability evidence; router delivery and proof integration are tested separately.
