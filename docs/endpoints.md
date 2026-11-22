# Endpoints and signed ratchets

In Reticulum, a *destination* is the addressable endpoint that packets, links and requests are sent to. Its address is derived from an application name, a set of aspects and, for most types, an identity. On top of the address, an endpoint carries behaviour that depends on its direction and type: whether it can announce, how it encrypts, which ratchet keys it holds and which requests it answers.

DotReticulum splits these two concerns:

* `Destination` is the name and address codec only.
* `DestinationEndpoint` owns the direction, PLAIN or GROUP encryption state, announce defaults and the path-response cache.

An endpoint borrows the identity and ratchet ring you give it. They remain yours to manage. The only exception is an incoming endpoint created without an identity: the factory then generates one and owns it. As in RNS, the hash of a generated incoming identity is also appended as an aspect. Disposing an endpoint erases its group key.

## Creating an endpoint

```csharp
using var identity = Identity.Generate();
using var endpoint = DestinationEndpoint.Create(
    DestinationType.Single, DestinationDirection.In, "example_utilities", ["announcesample"], identity);

endpoint.SetDefaultAppData("hello"u8.ToArray());
Packet announce = endpoint.CreateAnnounce();
```

Outgoing encrypted endpoints require an identity. Only incoming SINGLE endpoints can announce. Link acceptance defaults to true.

## Announces and app data

You can supply announce app data in three ways:

* `SetDefaultAppData` stores a copy of its input.
* `SetDefaultAppDataProvider` evaluates the current default for each new announce.
* A per-call value overrides the default. A null value selects the default, and an empty array suppresses it.

Tagged path responses reuse the signed announce payload for 30 seconds, including the exact 30-second boundary, without calling the provider again. The cache holds at most 256 entries by default, tags are limited to 16 bytes, and the oldest entries are evicted under pressure.

Cached ratchet announce payloads keep their context flag. Upstream clears that flag on a cache hit, which makes the cached payload fail validation. DotReticulum intentionally fixes that bug.

## PLAIN and GROUP endpoints

PLAIN endpoints return a detached copy of unencrypted data. They have no identity and no signing or symmetric-key operations.

GROUP endpoints must create or load a token key before they can encrypt or decrypt. The key is either a 32-byte legacy key or a 64-byte current key. An invalid key import leaves the existing key intact. Replacing or disposing the key erases owned key material. Copies you take yourself remain your responsibility.

## Decryption metadata

`DecryptWithMetadata` is available on identities, ratchet rings and endpoints. On success it returns an owned plaintext buffer and a detached ID for the ratchet that actually decrypted the packet, which may be an older retained key. Base-key, GROUP and PLAIN decryptions report no ratchet ID. Failure returns no result. The existing `Decrypt` APIs remain available.

Upstream records this as mutable `latest_ratchet_id` state on the identity, and its short-token failure branch can leave a stale value behind (`Identity.py:852–906`). `Destination.py:413–414` then copies it to `packet.ratchet_id`. The per-operation result avoids that shared state. The .NET router exposes the equivalent value as receive-side metadata without changing encoded packet bytes.

## Known destinations

`KnownDestinations.RegisterLocal` lets local destinations be recalled by their public keys without inventing an announcement or persisting local private keys. The router owns registration and deregistration.

* Remote entries take precedence over local ones.
* As in upstream, the local fallback does not supply app data and does not take part in identity-hash searches.
* Recalled remote identities carry a detached `AppData` value.

## Signed ratchet files

`SignedDestinationRatchetFile.Open(path, identity)` reads and writes the upstream envelope `{"signature": bytes, "ratchets": packed_private_keys}`. This is separate from the application's own versioned, destination-bound runtime-state format.

* The signature authenticates the packed key list before it is decoded.
* Files are limited to 4 MiB.
* Saves write an owner-only sibling file on Unix, flush it, and atomically replace the destination. On Windows you must configure suitable ACLs.

The returned ring owns an independent signer and an exclusive `.lock` file lease, so a second cooperating writer for the same path is rejected rather than silently losing keys. The `.lock` file may remain after disposal but contains no secrets. The identity you pass to `Open` stays yours.

Rotation, reload, decryption and disposal are serialized within a ring. Failure handling is conservative:

* Failed persistence does not publish a new ratchet or replace the existing ring.
* A failed reload leaves existing keys usable.
* A decryption failure retries after a signed reload.
* Filesystem failures keep their native .NET exception types. For example, a directory in place of the ratchet file raises `UnauthorizedAccessException` on Windows and `IOException` on Unix. Either way the current ring is unchanged.

Upstream does not cooperate with the `.lock` lease. Simultaneous Python and .NET writers are not supported by this API. A signed file replaced externally can be picked up by reload, but that does not establish cross-process rotation coordination.

### Runtime-state ratchets

The application's `RuntimeStateStore.OpenPersistentRatchetsAsync` installs the same signed reload-on-decryption-failure for its versioned, destination-bound files. Reload uses the independently owned signer and is unaffected by cancelling the token used to open the ring.

Reload imports keys only. Live retention, rotation timing and enforcement policy are unchanged, as with upstream's key-only reload. A corrupt or missing file fails explicitly, without overwriting the file or discarding usable in-memory keys. No failure causes a fresh key to be announced before it has been persisted.

## Link packet receivers and receiptless sending

`Link.PacketReceived` is an asynchronous observer. When it is scheduled says nothing about packet order. Pinned Python likewise starts a thread per packet callback (`RNS/Link.py:957`).

A protocol adapter that needs deterministic ingress order can install one short core receiver instead. It runs outside the link lock, in ingress order, before the asynchronous observers:

```csharp
using var registration = link.RegisterPacketReceiver((plaintext, packet) => queue.Writer.TryWrite(plaintext));
```

* The receiver should only enqueue bounded application work and return promptly. Blocking it blocks that router's ingress.
* Exceptions are reported, and later packets still run.
* `RegisterPacketReceiver` atomically rejects an existing receiver and returns a disposable lease. While the lease is active, the legacy `SetPacketReceiver` setter cannot replace or remove it. Disposing an old lease again cannot remove a later registration.
* `SetPacketReceiver(receiver)` and `SetPacketReceiver(null)` remain available for non-exclusive use.
* The adapter owns its queue and lease. The link does not own application workers.
* Removal does not cancel an invocation that ingress has already captured, so adapter cleanup must tolerate one in-flight enqueue.

`TrySend(data)` sends encrypted data without creating a receipt. It returns whether the local bounded outbound queue accepted the data; false means the link or the queue is unavailable. Oversized input still throws. Success means local acceptance, not delivery or transmission. `Send(data, createReceipt: false)` is still available, but its null return cannot tell a successful receiptless enqueue from a failure.

## Requests

A destination answers requests through handlers registered by path. Handlers deny by default: `RequestPolicy.All` permits any link, and `RequestPolicy.List` requires an identified peer on the allowed list. You can replace or deregister a handler by path.

```csharp
destination.RegisterRequestHandler("/status", context => "ok", RequestPolicy.All);

link.Request("/status",
    responseCallback: receipt => Console.WriteLine(receipt.Response),
    maxResponseSize: 4096);
```

`RegisterAsyncRequestHandler` receives a cancellation token linked to manager shutdown and link closure. If it returns an owned `StreamFileResponse`, that response is disposed even when cancellation or closure prevents transmission. Set `LeaveOpen` to keep ownership yourself.

`Link.Request` installs `maxResponseSize` before transmission and accepts a cancellation token. The limit covers both small responses and resource advertisements. Small-response sizes follow the reference's serialized-value-minus-two convention, including negative sizes for tiny scalar values.

Receipt delivery, final progress and response notifications keep their order, and one failing observer does not affect the others. Cancelling a request settles its task and packet receipt and releases request resources. It does not close the link.

For binary map keys in request data and responses, see [MessagePack application values](application-values.md).

### Limits and owned work

The native link manager enforces these bounds:

| Bound | Limit |
| --- | --- |
| Links | 1,024 |
| Pending requests per link | 256 |
| Entries per outbound, request or observer queue | 256 |
| Concurrent request handlers | 4 |
| Concurrent optional observers | 4 |

An admission failure is diagnosed and never reported as a successful send. Receipt, channel and resource settlement do not depend on optional observers running. If an optional queue overflows, a notification can be rejected with a diagnostic, but that does not undo a protocol operation that has already settled.

Shutdown cancels owned asynchronous work and joins mandatory resource cleanup. Synchronous handlers and observers cannot be forcibly interrupted. After a one-second observer wait they remain tracked and keep their bounded slots until they return, with an explicit diagnostic. No replacement workers bypass those retained slots, so cancellation-aware handlers and streams must cooperate. Callbacks must not synchronously wait for their own shutdown or completion.

### Link timing

Link ages and inactivity durations use the configured monotonic time source. `EstablishmentRate` reports the authenticated request and proof bytes per RTT, in bits per second; later packet proofs do not change it. `ExpectedRate` comes from resource transfer observations, not from the configured bearer bitrate.

## Differences from upstream

These are intentional:

* **Cached ratchet announces** keep their context flag, fixing an upstream validation bug.
* **Decryption metadata** is returned per operation instead of as mutable `latest_ratchet_id` state.
* **X25519 and Ed25519 public keys** are validated strictly. Identity imports reject noncanonical or low-order X25519 keys and invalid Ed25519 keys, some of which the pinned reference accepts at load time. Independent PyCA and upstream-internal provider vectors show that the internal X25519 exchange accepts low-order inputs that yield zero, while PyCA rejects them. Both accept high-bit inputs, but their shared secrets diverge for the observed alias.
* **Token padding** is checked strictly. HMAC is verified first, then platform PKCS#7 unpadding is applied. The reference accepts mismatched padding bytes and a zero padding length after authentication; DotReticulum rejects these malformed tokens. Correct AES-128 and AES-256 tokens remain byte-compatible.
* **HKDF-SHA256** keeps RFC 5869 limits: output lengths 1–8160 and nonempty input. The pinned reference accepts empty input and wraps its counter beyond 8160 bytes.
* **Signed ratchet reload** failures propagate immediately, unlike upstream's delayed second read. A missing runtime-state ratchet file fails explicitly, where upstream reinitializes it.

## Limitations

* Simultaneous Python and .NET writers to one signed ratchet file are not supported, and cross-process rotation coordination is not established.
* Untrusted synchronous handlers and observers cannot be forcibly interrupted.
* Router delivery, proofs and link-policy integration are separate acceptance gates from the endpoint contracts described here.

## Verification

* Endpoint and ratchet contracts: `tests\DotReticulum.Core.Tests\Parity5EndpointTests.cs` and `Parity5EndpointRatchetTests.cs`.
* Local endpoints, known destinations and requests: `tests\DotReticulum.Transport.Tests\Parity5LocalEndpointTests.cs`, `Parity5EndpointKnownDestinationsTests.cs` and `Parity5RequestTests.cs`.
* Independent observations come from `tests\DotReticulum.Core.Tests\GenerateParity5EndpointVectors.py`, run against verified RNS 1.5.7 (`a29bfb51c468945c96a1ee22cae0114e993e2391`). The fixture records the imported module path, provider version and source SHA-256 values.

These vectors and managed tests are bounded contract evidence. They are not live-node or NativeAOT interoperability evidence on their own.
