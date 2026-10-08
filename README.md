# DotReticulum

A native C# / .NET 10 implementation of the [Reticulum Network Stack](https://github.com/markqvist/Reticulum),
aiming for wire compatibility with the Python reference and other conforming nodes.

**Experimental partial stack, not a complete network stack.** Implemented so far:
cryptography, raw identities, packet headers and destination hashes, a bounded
packet-interface manager, and UDP/TCP packet interfaces. It does not yet discover
peers, route packets, establish links, or transfer files. Do not deploy it for
production or security-critical use.

## Architecture

| Project | Responsibility | Current implementation |
| --- | --- | --- |
| `src/DotReticulum.Core` | Identities, wire packets, destinations, interface manager | Foundation primitives and bounded packet dispatch |
| `src/DotReticulum.Crypto` | Ed25519, X25519, HKDF, RNS tokens | Primitives and vector tests |
| `src/DotReticulum.Transport` | Announces, routing, links, resources | Reserved assembly |
| `src/DotReticulum.Interfaces` | TCP, UDP, serial, radio and tunnels | UDP datagrams; HDLC-framed TCP client/server |
| `src/DotReticulum.Applications` | CLI tools and telemetry | Basic `rnid` identity commands |

Production projects enable trimming and NativeAOT analysis. AES-CBC, PKCS7,
HMAC-SHA256 and HKDF use `System.Security.Cryptography`; raw Curve25519 operations
use Bouncy Castle because .NET does not provide portable raw Ed25519/X25519 APIs.
RNS tokens are binary `IV || ciphertext || HMAC`, **not** standard timestamped,
base64 Fernet tokens. Packet encoding does not automatically encrypt payloads.

## Build and test

Install the .NET 10 SDK:

```sh
dotnet restore DotReticulum.sln
dotnet build DotReticulum.sln -c Release
dotnet test DotReticulum.sln -c Release --collect:"XPlat Code Coverage"
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj \
  -c Release -r linux-x64 \
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

Native publishing requires the native compiler/linker toolchain. CI tests
`ubuntu-latest`, `windows-latest` and `macos-latest`, publishing `linux-x64`,
`win-x64` and `osx-arm64` respectively. Linux ARM, RISC-V, FreeBSD, Android and iOS
are target goals, not currently verified platforms.

## Quickstart

```sh
dotnet run --project src/DotReticulum.Applications -- --help
dotnet run --project src/DotReticulum.Applications -- rnid --generate identity
dotnet run --project src/DotReticulum.Applications -- rnid --show identity
```

Identity files contain 64 raw private-key bytes in upstream order:
X25519 private key followed by Ed25519 seed. Generation refuses to overwrite
existing files and creates owner-only permissions on Unix. On Windows, verify
the containing directory and file ACLs. Back up keys securely; never commit them.
The CLI displays only the truncated identity hash and public key.

## Packet interfaces

`IPacketInterface` implementations are explicitly started and asynchronously
disposed. `PacketInterfaceManager` owns its interfaces, applies bounded
backpressure to incoming packets (capacity defaults to 256), reads packets from
all managed interfaces, and sends outgoing packets through each one. UDP sends
one complete packet per datagram to its configured peer. TCP client/server
interfaces use Reticulum's HDLC framing; TCP reconnect policy, serial, and other
bearers are not implemented. The TCP server defaults to at most 64 simultaneous
clients. These interfaces carry packets only—they do not provide discovery,
routing, or link reliability.

```csharp
var bearer = new UdpPacketInterface(localEndPoint, peerEndPoint);
await using var manager = new PacketInterfaceManager([bearer]);
await manager.StartAsync(cancellationToken);

await foreach (var packet in manager.ReceiveAsync(cancellationToken))
{
    // Process complete packet bytes.
}
```

## Interoperability status

| Capability | Evidence / status |
| --- | --- |
| Ed25519 / X25519 / HKDF | Standards vectors and unit tests |
| RNS token encryption / identity key layout | Python reference fixtures and unit tests |
| Header 1 / header 2 / destination and packet hashes | Python reference fixtures and unit tests |
| UDP datagrams and TCP client/server framing | Loopback tests; TCP HDLC frame checked against pinned Python source; no live-node test |
| Live Python announce discovery and encrypted delivery | Not implemented / not verified |
| Links, bidirectional multi-megabyte resources | Not implemented |
| LXMF / `lxmd` exchange | Not implemented |
| C++ / Rust node interoperability | Not verified |

Test source files record fixture provenance. Passing primitive vectors is not a
claim of full wire compatibility. Packets use the baseline 500-byte MTU; larger
transfers require the future Resource engine, not an invented fragmentation format.

## Roadmap

- **Initial milestone:** repository governance, modular solution, cryptographic
  and wire-format tests, basic identity CLI, build/test/NativeAOT CI.
- **P0:** bounded interface manager and UDP/TCP adapters are implemented; serial,
  announces and multi-hop transport, links/resources, LXMF, `rnsd`, `rncp`, `rnx`,
  and telemetry remain.
- **P1:** NativeAOT codec bindings, low-latency voice frames, half-duplex PTT.
- **P2:** adaptive low-bandwidth video and multi-party receiver proof of concept.

Live Python interop (nightly/manual or opt-in PR), five-node Docker mesh tests
(weekly/manual), weekly CodeQL and daily stale maintenance remain future workflow
milestones. They must test real implementations, not empty placeholder suites.
Fast CI filters code/build/workflow paths and excludes Markdown/docs-only changes;
GitHub does not allow `paths` and `paths-ignore` together, so exclusions are
negative patterns within `paths`. Root SDK/build settings are also included to
avoid silently skipping configuration changes.

## Contributing and licensing

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md),
and [.github/SECURITY.md](.github/SECURITY.md).

Released under the official [Reticulum License](LICENSE), copied from upstream.
The protocol's public-domain status does not remove the software license's
conditions, including restrictions on harmful systems and AI-training datasets.
