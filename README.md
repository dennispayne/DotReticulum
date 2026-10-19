# DotReticulum

An **LLM-assisted learning project**: an experimental native C# / .NET 10 implementation of the [Reticulum Network Stack](https://reticulum.network), aiming for wire compatibility with the Python reference implementation. It was produced with AI assistance, is not an official or community-recognized Reticulum implementation, and is not endorsed by the Reticulum project or its maintainer. Do not use it for production or security-critical purposes.

Compatibility claims here are exactly as strong as the recorded evidence; see the status table and [development history](.github/development-history.md). Phase progress is tracked in [plan.md](plan.md).

The roadmap targets high-fidelity feature and behavioral parity with the pinned upstream RNS repository, using idiomatic, well-architected native .NET rather than a line-by-line Python translation. It covers upstream runtime behavior, interfaces and bundled utilities, not just wire formats. Audio/video extensions are out of scope; existing LXMF and .NET-specific telemetry remain supported but their expansion is separate from RNS parity.

Our authoritative reference upstream is [`rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`](rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum), retrieved through our .NET `rngit` client. Live tests pin its commit `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7); GitHub is a secondary mirror. Stored vectors retain their original generation provenance.

P0's hosted-upstream gate is complete: our NativeAOT .NET client fetched missing Git objects over a public Reticulum path, verified the resulting repository and compared it with the GitHub reference. This was an incremental fetch seeded with historical GitHub objects, not a full download from scratch. Evidence and remaining limits are in the [development history](.github/development-history.md).

## Architecture

| Project | Responsibility |
| --- | --- |
| `src/DotReticulum.Core` | Identities, wire packets, destinations, packet-interface contract and manager |
| `src/DotReticulum.Crypto` | Ed25519, X25519, HKDF, Reticulum tokens |
| `src/DotReticulum.Transport` | Announces, routing, links, resources |
| `src/DotReticulum.Interfaces` | `IPacketInterface` bearers: UDP, TCP client/server (HDLC), serial (HDLC); bounded `PacketInterfaceManager` lives in Core |
| `src/DotReticulum.Lxmf` | LXMF messages, stamps and a router-lite (opportunistic/direct/Resource delivery, receipts, retry queue); see [docs/lxmf.md](docs/lxmf.md) |
| `src/DotReticulum.Applications` | `ReticulumNode` client runtime and CLI tools (`rnid`, `rnsd`, `rncp`, `rnx`, `telemetry`, `rngit`) |

Production projects enable trimming and NativeAOT analysis. Raw Curve25519 uses Bouncy Castle because .NET has no portable raw Ed25519/X25519 API; AES, HMAC, SHA and HKDF use `System.Security.Cryptography`. Reticulum tokens are binary `IV || ciphertext || HMAC`, not standard Fernet tokens.

## Build and test

```sh
dotnet build DotReticulum.slnx -c Release
dotnet test DotReticulum.slnx -c Release
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

PowerShell 7 entry point: `pwsh -NoProfile -File tests/run-tests.ps1 -Pester`.
Pester 5.7.1+ covers tooling and separate-process CLI flows, including an
authenticated multi-megabyte rncp send/fetch. See [CONTRIBUTING.md](CONTRIBUTING.md)
for dependencies, coverage, live-reference and published-native options.

## Quickstart

```sh
dotnet run --project src/DotReticulum.Applications -- --help
dotnet run --project src/DotReticulum.Applications -- rnid --generate identity
dotnet run --project src/DotReticulum.Applications -- rnid --show identity
dotnet run --project src/DotReticulum.Applications -- rnsd --exampleconfig > rnsd-config   # then place as <dir>/config
dotnet run --project src/DotReticulum.Applications -- rnsd --config <dir>
dotnet run --project src/DotReticulum.Applications -- rncp -l -a <identity hash> -s <dir> --config <dir>   # receive files
dotnet run --project src/DotReticulum.Applications -- rncp <file> <destination hash> --config <dir>       # send a file
dotnet run --project src/DotReticulum.Applications -- rnx -l -a <identity hash> --config <dir>            # remote commands
dotnet run --project src/DotReticulum.Applications -- telemetry daemon --config <dir>
```

See [docs/rnsd.md](docs/rnsd.md) for the daemon config format and limitations, [docs/rncp-rnx.md](docs/rncp-rnx.md) for rncp/rnx, [docs/telemetry.md](docs/telemetry.md) for the telemetry format and [docs/rngit.md](docs/rngit.md) for read-only Git clone/fetch.

Identity files hold 64 raw private-key bytes (X25519 then Ed25519). Generation refuses to overwrite. Protect and back up keys; never commit them.

## Interoperability status

These are bounded verified slices, not complete module parity. The post-Git
source audit found additional correctness and lifecycle gaps, including
no-subscriber automatic proofs, resource-queue cancellation, stream shutdown,
configuration defaults and persistent state. They remain open in
[plan.md](plan.md), with source references and acceptance criteria in the
[development history](.github/development-history.md).

| Capability | Evidence / status |
| --- | --- |
| Ed25519, X25519, HKDF, Token, Identity (encrypt/decrypt/sign) | RFC/reference vectors and .NET-to-upstream cross-checks; exercised in live Python encrypted-packet, link and resource tests below |
| Packet and Destination codec | Reference-generated vectors; exercised in live Python interface, announce and routing tests below |
| UDP, TCP client, TCP server interfaces + HDLC framing | Live-verified on loopback against a real Python RNS (pinned upstream): .NET receives and parses Python announces over all three; Python receives .NET-built packets over all three. HDLC vectors generated by executing upstream Python. Opt-in: `DOTRETICULUM_LIVE=1` (see `tests/Interop/live_interfaces_harness.py`) |
| Announce create/validate, ratchets, known-destination recall, announce engine | Reference-generated vectors, upstream validation of .NET announces, and live Python TCP tests in both directions (validate/recall, ratchet encryption/decryption). Multi-hop announce rebroadcast is exercised by the routing tests below. Observer failures are reported without stopping processing. Opt-in: `DOTRETICULUM_LIVE=1` |
| Transport router + `rnsd` (path discovery, announce rebroadcast, DATA/link forwarding, proofs, dedup, expiry) | Live Python leaf nodes through a .NET router, plus Python leaf -> Python transport -> .NET router -> Python leaf (three hops): bidirectional path discovery, announces, encrypted datagrams/proofs, links/data/proofs and teardown. Native `rnsd` exercised forwarding; registering a local destination is explicitly skipped in external-exe test mode. Not covered: ingress control, announce caps, tunnels, IFAC, shared instance, UDP/serial multi-hop. Opt-in: `DOTRETICULUM_LIVE=1` |
| Links, Resources, Channel/Buffer, link-table forwarding | Reference-generated vectors and live Python links, multi-MB resources both ways, request/response and channel/buffer streams, directly and through a .NET hop. Deterministic Channel tests cover 20 ms one-way latency, lost data/proofs, full windows, retry exhaustion, teardown and recovery. A NativeAOT rncp listener roundtrips 2,200,017 bytes with compression asserted both ways. Not covered: MTU discovery, file-backed resources, live Python channel retry under impaired links. Opt-in: `DOTRETICULUM_LIVE=1`; native test additionally uses `DOTRETICULUM_NATIVE_EXE` |
| LXMF (`LxMessage`, `LxmRouter`), `rncp`, `rnx`, telemetry | LXMF: reference-generated vectors; live Python `LXMRouter` delivery both ways (opportunistic, direct, 600,000-char Resource), plus separately .NET -> real `lxmd` delivery (including a 200 kB Resource with its transfer limit raised). Propagation sync, tickets and delivery-destination ratchets are not implemented; delivery to lxmd does not prove propagation support. rncp: send/fetch both ways against Python, 3-5 MB byte-identical; reference-side Windows file-move shim disclosed in [docs/rncp-rnx.md](docs/rncp-rnx.md). rnx: live both ways against Python, with authorization tests. Telemetry: .NET-specific format, tested directly and across a .NET hop, no Python peer. Opt-in: `DOTRETICULUM_LIVE=1` |
| Serial interface | HDLC over a configured port or a caller-supplied stream; tested over an in-memory stream only, not on real hardware |
| Read-only `rngit clone` / `fetch` | Real upstream server tests verify exact refs/HEAD/tree, incremental missing-object retrieval, no-change fetch and error handling. NativeAOT fetched the hosted Reticulum upstream through a public six-hop path; strict Git object checks passed. Bare repositories only, buffered transfers, no publisher/release-signature verification. See [docs/rngit.md](docs/rngit.md) |
| Native `git-remote-rns` (read-only) | Ordinary Git discovers the renamed native executable on PATH. Managed/native Pester exercises working-tree and bare clone/fetch, branches, tags, HEAD, remote-tracking refs, incremental/no-change behavior and failures against the pinned Python server. Ordinary Git with the native helper fetched 11,502 hosted bundle bytes (92 new objects) from an explicitly seeded parent; strict fsck passed. Git owns refs/checkout. No push, hosting or signing; see [docs/rngit.md](docs/rngit.md). |
| `rnid` (generate/show) | Implemented; NativeAOT build verified. Upstream loads its identity files |
| Everything else | Not yet implemented; see [plan.md](plan.md) |

## License

Released under the [Reticulum License](LICENSE), copied verbatim from upstream. It adds restrictions on harmful systems and AI-training datasets that are not part of MIT, so this project is not MIT-licensed.
