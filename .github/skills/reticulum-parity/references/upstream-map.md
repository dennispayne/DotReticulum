# RNS subsystem and source map

This is a navigation aid for the pinned RNS 1.5.7 implementation, not a
replacement specification. Resolve the checkout from `DOTRETICULUM_UPSTREAM`,
verify it against commit `a29bfb51c468945c96a1ee22cae0114e993e2391`, and follow
the concrete call graph for the behavior under investigation. The Python
reference defines observable behavior; DotReticulum's .NET-specific lifecycle
and safety decisions are documented separately.

## Protocol layers

| Concern | Upstream source to start from | Trace next |
| --- | --- | --- |
| Identity, key material, signing, encryption and identity hashes | `RNS/Identity.py` | `RNS/Cryptography/*`, destination derivation, callers that serialize keys |
| Destination types, names, hashes, announce construction and validation | `RNS/Destination.py` | `RNS/Identity.py`, announce handling in `RNS/Transport.py` |
| Packet header, flags, hashes, serialization and parser limits | `RNS/Packet.py` | `RNS/Transport.py`, bearer interface framing and packet consumers |
| Path table, announces, routing, modes, proofs, receipts, tunnels and transport state | `RNS/Transport.py` | `RNS/Packet.py`, `RNS/Interface.py`, `RNS/Link.py`, persistence and callbacks |
| Link handshake, encryption, proofs, requests/responses, RTT and MTU | `RNS/Link.py` | `RNS/Transport.py`, destination crypto, resources/channels |
| Segmented resource transfer, compression, proof and recovery | `RNS/Resource.py` | `RNS/Link.py`, `RNS/Transport.py`, transfer callbacks and storage |
| Reliable channel framing, ordering, windows and buffers | `RNS/Channel.py`, `RNS/Buffer.py` | `RNS/Link.py`, stream/application consumers |
| Interface modes, traffic accounting, announce scheduling and generic callbacks | `RNS/Interface.py` | `RNS/Transport.py` and concrete `RNS/Interfaces/*` |
| Configuration defaults, startup, identity/state files and runtime lifecycle | `RNS/Reticulum.py` | `RNS/Config.py`, `RNS/Transport.py`, `RNS/Interfaces/*` |
| TCP/UDP/serial/Backbone/Local/Pipe/Auto/KISS/RNode and other bearers | `RNS/Interfaces/` | Constructors/config consumers, frame codecs, child peer lifecycle |
| Daemon and CLI observable behavior | `RNS/Utilities/` | Utility entry points, runtime APIs, argument parsing and process behavior |
| LXMF behavior (separate application protocol; implemented in the DotReticulum.Ecosystem repository, not here) | `LXMF/` in the pinned environment | Reference environment only: used by this repo's harnesses and by DotReticulum.Ecosystem's DotLXMF tests |

Do not assume a feature is wholly implemented by its named module. For example,
an interface contract may span configuration parsing, a generic interface,
transport callbacks and a utility. Trace both send and receive paths and the
cleanup/restart paths.

## Useful source distinctions

- A **destination hash** identifies an application destination; a **transport
  identity/ID** and a **path table entry** participate in routing. Follow the
  actual fields being compared rather than conflating these identifiers.
- An **announce** is an authenticated discovery/routing input, not proof that a
  link is already established. Verify how it affects known destinations,
  paths, forwarding and request scheduling.
- A **link** is an authenticated encrypted session. Resource and channel
  traffic are link-level contracts and should not be treated as generic packet
  payloads.
- A **resource** uses its own metadata, segmentation, compression and proof
  lifecycle. Verify what is cached/replayed and which packet limits apply.
- A **bearer frame** may contain framing or IFAC overhead around a decoded
  packet. Compare raw frame limits, decoded packet MTU, link-advertised MTU
  and application/resource sizes independently.
- **Transport tunnels** preserve authenticated routing association across
  bearer reconnection; they do not resume encrypted links or resource sessions.
- **Configuration defaults and state restoration** are observable contracts.
  Compare absent, invalid and explicit values; inspect startup ordering, file
  compatibility, corruption handling and restart behavior.
- Utilities are process contracts: options, output streams, exit status,
  filesystem effects, environment/config discovery, authorization and
  cancellation are part of parity.

## DotReticulum evidence and implementation map

- Protocol and crypto code: `src/DotReticulum.Core`,
  `src/DotReticulum.Crypto`, `src/DotReticulum.Transport`.
- Bearers and lifecycle: `src/DotReticulum.Interfaces`; application runtime
  and interface configuration: `src/DotReticulum.Applications`.
- Protocol-level tests: corresponding `tests/DotReticulum.*.Tests`; actual
  pinned-peer tests and adapters: `tests/Interop` plus focused live test files.
- Every new vector generator belongs beside its owning tests and must execute
  upstream. Consult `CONTRIBUTING.md` and existing `Generate*.py` files for the
  verified convention.
- Remaining work is in `TODO.md`; historical completed scope is in `docs/archive/plan.md` and
  `.github/development-history.md`. Do not infer that a checked module-level
  item establishes full upstream compatibility.

When upstream source is unavailable or a contract cannot be executed, state
that limitation. Do not replace the reference with memory, a mirror-only
assumption, copied expected bytes, or the .NET implementation itself.
