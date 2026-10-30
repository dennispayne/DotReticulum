<p align="center">
  <img src="assets/branding/dotreticulum.svg" width="240" height="240" alt="DotReticulum: Reticulum network motif on a purple hexagon">
</p>

# DotReticulum

A native C# / .NET 10 implementation of the [Reticulum Network Stack](https://reticulum.network) (RNS), built to interoperate with the Python reference implementation: discover, link with, route for and exchange data with upstream Reticulum nodes. It targets high-fidelity feature and behavioral parity with the pinned upstream RNS repository, using idiomatic, well-architected native .NET rather than a line-by-line Python translation.

> [!WARNING]
> DotReticulum is an **experiment in having an LLM build a Reticulum implementation**. It is as much about developing skills in AI agent wrangling and using CI to improve coding outcomes as it is about doing odd things in .NET just to see whether they work. Almost all of its code, tests and documentation were written by AI coding agents under human direction. It is experimental, audited as much as this human is capable of (i.e. not nearly enough), and **not** for production or security-critical use. Be careful with it.
>
> It is **not** an official, endorsed or community-recognized Reticulum implementation, and it is not the work of the Reticulum project or its maintainer. Do not treat it as a known-good implementation; those are listed in the [upstream README](https://github.com/markqvist/Reticulum#community-implementations).
>
> The upstream project takes a strict but fair view of LLM-generated implementations, documentation and licensing claims; see the [Brandolini's Reference](https://reticulum.network/manual/brandolinis.html) chapter of the manual. DotReticulum acknowledges that position and makes no attempt to influence it, to seek recognition, or to claim anything on upstream's behalf. The upstream author's opinions are shared here, and this is a good faith effort to comply with them.
>
> In keeping with it, this project keeps the [Reticulum License](LICENSE) and its copyright notice intact and does not relicense or assert its own license over derived work. Compatibility claims here are exactly as strong as the recorded evidence.

## Upstream

The Python code in the upstream repository is the Reference Implementation and, with its manual, defines the Reticulum Protocol. DotReticulum tracks it; it does not replace it.

- **Primary home:** Reticulum hosts its own repository on the Reticulum network, retrievable with `rngit` (Git over Reticulum): `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`. DotReticulum's reference pins are taken from this source, retrieved with our own .NET `rngit` client.
- **GitHub mirror:** [github.com/markqvist/Reticulum](https://github.com/markqvist/Reticulum) is a public mirror of that repository ("all development is happening elsewhere"). It is the easy place to browse upstream's code; we use it only as a secondary comparison source, never as the origin of a new reference pin.
- **Manual:** [reticulum.network/manual](https://reticulum.network/manual/).

Currently targeted: **RNS 1.5.7**, upstream commit `a29bfb51c468945c96a1ee22cae0114e993e2391`, with LXMF 1.2.1 in the Python reference environment (used by the harnesses; LXMF itself is implemented in DotReticulum.Ecosystem).

## Notable features

- Wire-compatible identities, destinations, packets, announces (including ratchets), paths, links, requests, resources, Channel and Buffer
- Multi-hop transport and the `rnsd` daemon, with shared instances, management and discovery
- Interfaces: UDP, TCP client/server, serial, Auto, Backbone, Pipe, KISS/AX.25, RNode, Multi-RNode, I2P, Weave and Local, with IFAC authentication
- The upstream utility set as one NativeAOT executable: `rnsd`, `rnstatus`, `rnpath`, `rnprobe`, `rnid`, `rncp`, `rnx`, `rnsh`, `rnodeconf`, plus `rngit`, `git-remote-rns` and `rngcs` for Git over Reticulum
- LXMF (DotLXMF) and a .NET-specific telemetry tool (DotTelemetry) live in the separate [DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem) repository
- Idiomatic, trimming- and NativeAOT-friendly .NET: asynchronous I/O, cancellation, bounded resource use, no reflection-based serialization

This is a target, not a claim of complete parity; see [Compatibility](#compatibility).

## Architecture

| Project | Responsibility |
| --- | --- |
| `src/DotReticulum.Core` | Identities, wire packets, destinations, packet-interface contract, bounded interface manager |
| `src/DotReticulum.Crypto` | Ed25519, X25519, HKDF, Reticulum tokens |
| `src/DotReticulum.Transport` | Announces, routing, links, resources |
| `src/DotReticulum.Interfaces` | Interface implementations |
| `src/DotReticulum.Applications` | Client runtime and the command-line utilities |

Raw Curve25519 uses Bouncy Castle because .NET has no portable raw Ed25519/X25519 API; everything else uses `System.Security.Cryptography`. Reticulum tokens are binary `IV || ciphertext || HMAC`, not standard Fernet.

## Install

Building requires the .NET 10 SDK (pinned in `global.json`). NativeAOT publishing also needs the platform's native toolchain (MSVC build tools on Windows, `clang` and `zlib1g-dev` on Linux, Xcode tools on macOS).

Native packages contain one executable per tool, so you run `rnsd`, not `DotReticulum.Applications rnsd`. Building, packaging and installing them is described in [Installing the utilities](docs/installation.md). Nothing is changed outside the install directory, and your `PATH` is yours to edit.

To run from source:

```sh
dotnet run --project src/DotReticulum.Applications -- rnsd --help
```

## Documentation

| Topic | Pages |
| --- | --- |
| Daemon and runtime | [rnsd](docs/rnsd.md), [runtime operations](docs/runtime-operations.md), [runtime state](docs/runtime-state.md), [transport controls](docs/transport-controls.md), [interface policy](docs/interface-policy.md) |
| Interfaces | [UDP](docs/udp-interface.md), [TCP/serial/stream](docs/stream-interfaces.md), [Auto](docs/auto-interface.md), [Backbone and Pipe](docs/backbone-pipe.md), [KISS/AX.25](docs/kiss-interfaces.md), [RNode](docs/rnode-interfaces.md), [I2P](docs/i2p-interface.md), [Weave](docs/weave-interface.md), [Local and Android](docs/interface-platforms.md), [packet PHY](docs/packet-phy.md) |
| Protocol building blocks | [Endpoints](docs/endpoints.md), [application values](docs/application-values.md), [resources](docs/resources.md), [Channel and Buffer](docs/channel-buffer.md) |
| Utilities | [rnid](docs/rnid.md), [rnstatus/rnpath/rnprobe](docs/network-utilities.md), [rncp and rnx](docs/rncp-rnx.md), [rnsh](docs/rnsh.md), [rnodeconf](docs/rnodeconf.md), [upstream placeholders](docs/upstream-placeholders.md) |
| Git over Reticulum | [rngit, git-remote-rns, rngcs](docs/rngit.md) |
| Compatibility | [Contract reference](docs/compatibility-contracts.md), [mixed Python/.NET mesh](docs/compatibility-mesh.md) |
| Project | [Installing](docs/installation.md), [Contributing](CONTRIBUTING.md), [TODO](TODO.md), [development history](.github/development-history.md) |

## Compatibility

"Live" means a real, unmodified Python reference node in the pinned environment; unit vectors and loopback tests are not live interoperability. The [contract reference](docs/compatibility-contracts.md) gives each upstream contract's status and evidence; open gaps are in [TODO.md](TODO.md).

CI's Compatibility table runs shared, upstream-advertised acceptance scenarios on
Python RNS and native .NET Linux/macOS/Windows. Multi-node scenarios keep upstream
as the fixed peer, with upstream-to-upstream as the baseline. Behavioral/native-only
checks remain under Test; uncovered advertised claims stay visible in the discovery inventory. The
[Compatibility jobs](.github/workflows/test.yml) retrieve missing reference
objects using each native candidate's own `rngit` over `rns://`, then run the
shared scenarios. One verified source bundle is reused by Test and the subsequent
Python baseline; there is no separate fetch workflow. Each table row is a real
Pester test with its own outcome, not a calculated claim verdict, and its Test ID
links to the shared test source. Readable cases live under
`tests\Compatibility\Claims`; `tests\Compatibility\Get-CompatibilityTests.ps1`
discovers tests and joins their upstream citations without executing them.
The shared inventory now has **42 distinct tests and 17 catalogued claims**,
including malformed cryptography/proofs, authenticated requests, TCP IFAC
isolation and bidirectional multi-megabyte transfers with compression and
authorization/recovery checks, plus exact bidirectional file delivery across
a two-hop UDP/TCP path through a fixed upstream router and exact ordered
bidirectional Channel stream delivery through actual CLI consumers. Management
requests additionally check intact nested responses on both sides of the
negotiated packet-size boundary, accepted by all four hosted candidates in
run 40956389129. An authorized `rnx` case also persists large request input and
returns its exact output through resource-sized requests and responses; all four
hosted candidates passed in run 40961479281. This is
bounded evidence, not comprehensive parity: the Channel case covers Buffer
message encoding, not general reader/writer APIs, loss or soak; routing cases
do not establish alternate routes, RF or impaired meshes.
Source quotations are checked against the fetched pin; `CitationUri`
stays blank until primary-source navigation is verified (no GitHub fallback).
Windows rncp peer cases disclose a file-retention workaround in
[Contributing](CONTRIBUTING.md#moving-to-a-new-upstream-release).

| Area | Status |
| --- | --- |
| Crypto, identities, tokens, packets, destinations | Reference-generated vectors including malformed and boundary input; exercised live |
| UDP, TCP client/server, HDLC | Live against Python in both directions |
| Announces, ratchets, paths, routing, `rnsd` | Live Python and .NET transports both ways, including IFAC, a rate-limited latent link and reconnects |
| Runtime configuration, shared instances, management, discovery | Both roles tested against Python; persistence and restart covered |
| Links, requests, resources, Channel, Buffer | Live both ways with multi-megabyte payloads, loss, retries and teardown recovery |
| Auto, Backbone, Pipe, KISS, RNode, Weave, I2P | Software/process tests and live Python Auto/Backbone; one Windows RAK4631 passed managed/native receive-only startup and reopen, live configuration and radio-off control. Java I2P local-destination persisted bearer restart exchanges DATA for .NET and upstream; Windows native fresh/reused-configuration probes also pass with validated proofs. Public-tunnel restart still fails. No over-the-air radio acceptance |
| `rncp`, `rnx` | Live both ways |
| `rngit`, `git-remote-rns`, `rngcs` | Clone, fetch and push against the pinned Python server; hosting, releases, work documents, signing; verified fetch of the hosted upstream |
| `rnid`, `rnstatus`, `rnpath`, `rnprobe`, `rnsh`, `rnodeconf` | Windows and Linux native-process tests against Python; one Windows RAK4631 passed native USB DFU/reflash and reversible EEPROM persistence/restoration; no RF acceptance |
| Mixed mesh | Measured-window resource loss/retry and .NET/Python-router recovery; bounded Windows/Linux/macOS managed endpoint timeout/fresh-session recovery and Windows managed fresh links through a surviving alternate .NET router after new announcements, both initiator roles. Separate all-upstream cold-window cancellation is negative evidence, not passing recovery; see [the mesh reference](docs/compatibility-mesh.md) |

| Platform | Status |
| --- | --- |
| Windows x64 | Built, tested, NativeAOT-published; primary development platform |
| Linux x64 | Built, tested, NativeAOT-published; Auto and Backbone live-tested |
| macOS ARM64 | CI builds, runs the managed suite and an encrypted multi-megabyte native resource transfer; bounded rnsh terminal/native flows and actual Python roles verified; packaging not accepted |
| Linux ARM/RISC-V, FreeBSD, Android, iOS | Not verified; a managed build is not platform support |

## Build and test

```sh
dotnet build DotReticulum.slnx -c Release
dotnet test DotReticulum.slnx -c Release
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

`<rid>` is `win-x64`, `linux-x64` or `osx-arm64`. The default suite needs no Python; live tests are opt-in and need the pinned reference. See [CONTRIBUTING.md](CONTRIBUTING.md). CI builds and tests on Windows, Linux and macOS and runs a NativeAOT publish with an encrypted multi-megabyte resource round trip on each.

## Versioning

A DotReticulum version is **the upstream RNS release it targets plus a build number**: `1.5.7.<height>`.

- The first three components are the upstream RNS version, entered by hand in [`version.json`](version.json). They change only when the project's reference moves to a new upstream release.
- The last component is the git height from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning): commits since `version.json` last changed. It rises with every commit and resets when the upstream number changes.
- Tool `--version` output reports the upstream RNS version the tool is compatible with ("reference RNS 1.5.7").

So `1.5.7.42` is the forty-second build aimed at RNS 1.5.7. A version number names the target, not completed parity. To move to a newer upstream, see [Moving to a new upstream release](CONTRIBUTING.md#moving-to-a-new-upstream-release).

### Releases

A release is a `v<version>` tag (for example `v1.5.7.42`) on `main`. Create it with `pwsh tools/New-ReleaseTag.ps1` and push it; the [Release workflow](.github/workflows/release.yml) then builds the NativeAOT packages for `linux-x64`, `win-x64` and `osx-arm64`, zips them with SHA-256 checksums, and attaches them to a GitHub release. A manual run of the workflow builds the zips as artifacts without publishing. Each zip is the output of `tools/Invoke-DotReticulumPackaging.ps1` (see [installation](docs/installation.md)). Platform support is only as strong as the recorded evidence; macOS in particular is built but not verified.

## Contributing

Contributions are welcome; read [CONTRIBUTING.md](CONTRIBUTING.md). The [development history](.github/development-history.md) is a condensed historical reference.
See [ACCESSIBILITY.md](ACCESSIBILITY.md) for accessibility goals, limitations and reporting.

## License

Released under the [Reticulum License](LICENSE), copied verbatim from upstream. It adds restrictions on harmful systems and AI-training datasets that are not part of MIT, so this project is not MIT-licensed.
