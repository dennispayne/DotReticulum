# DotReticulum documentation

This is the documentation for DotReticulum, an experimental, AI-assisted .NET implementation of the Reticulum Network Stack. It is arranged roughly like the chapters of the upstream [Reticulum manual](https://reticulum.network/manual/), which remains the authoritative description of Reticulum itself. These pages describe how DotReticulum implements, configures and verifies that behavior, and where it differs or falls short.

Every page targets RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`. Statements of support are limited to the evidence each page records. For the overall status, see [Compatibility](../README.md#compatibility) in the project README.

## Getting started

- [Installing the utilities](installation.md): publish, package and install the native NativeAOT executables, one per tool.
- [Accessibility](accessibility.md): accessibility goals, known barriers and how to report problems.

## Using DotReticulum on your system

- [rnsd](rnsd.md): run a transport node that opens interfaces, learns paths, rebroadcasts announces and forwards packets.
- [Runtime operations](runtime-operations.md): interface ownership, shared instances, management, discovery, blackhole services and housekeeping.
- [Runtime state](runtime-state.md): what path, destination, tunnel and packet-cache state persists across restarts, and how private material is kept separate.
- [rnid](rnid.md): manage identities, compute destination hashes, announce, encrypt and decrypt files, and sign and verify.
- [rnstatus, rnpath and rnprobe](network-utilities.md): view interface status, inspect and modify path tables, and probe destinations.
- [rncp and rnx](rncp-rnx.md): transfer files, and run request/response commands on remote nodes.
- [rnsh](rnsh.md): open Channel-based remote shell sessions.
- [rnodeconf](rnodeconf.md): inspect, configure, flash and maintain RNode devices.
- [Git over Reticulum](rngit.md): `rngit`, `git-remote-rns` and `rngcs` for repository access, hosting and signing over Reticulum.
- [Upstream placeholders](upstream-placeholders.md): the resolver and package-manager entry points that upstream ships without operations, and why DotReticulum does not invent them.

## Configuring interfaces

- [UDP interface](udp-interface.md): one packet per datagram, with independent receive and forward endpoints.
- [TCP and serial interfaces](stream-interfaces.md): HDLC-framed TCP client, TCP server and serial bearers, with optional KISS framing for TCP clients.
- [AutoInterface](auto-interface.md): zero-configuration peer discovery on IPv6 multicast links.
- [Backbone and Pipe interfaces](backbone-pipe.md): the Linux TCP backbone bearer, and packet exchange with a child process over stdin/stdout.
- [KISS and AX.25 KISS interfaces](kiss-interfaces.md): packets through a serial TNC, with or without AX.25 UI framing.
- [RNode interfaces](rnode-interfaces.md): LoRa radios over serial, TCP or Windows BLE, including multi-radio RNode devices.
- [I2P interface](i2p-interface.md): Reticulum streams through an operator-provided I2P router's SAM API.
- [Weave serial interface](weave-interface.md): Weave WDCL devices and their endpoint children.
- [Interface platforms and local sharing](interface-platforms.md): per-platform bearer scope, local shared instances and the Android boundary.
- [Packet PHY measurements](packet-phy.md): receive-side radio measurements attached to packets at ingress.

## Building networks

- [Transport controls](transport-controls.md): route selection, scheduling, ingress and egress rate control, receipts, link MTU negotiation and tunnel recovery.
- [Interface selection and ingress feedback](interface-policy.md): which bearers carry outgoing traffic, and how ingress pressure is signaled to Backbone interfaces.

## Protocol building blocks

- [Endpoints and signed ratchets](endpoints.md): destination endpoints, encryption, announces, request handlers and ratchet ownership.
- [MessagePack application values](application-values.md): the reflection-free MessagePack codec used for application data.
- [Resources](resources.md): segmented, compressed and verified transfer of arbitrary amounts of data over links.
- [Channel and Buffer](channel-buffer.md): reliable sequenced messages and byte streams over links.

## Compatibility

- [Compatibility contracts](compatibility-contracts.md): the status and evidence for each upstream contract, and how compatibility is tested.
- [Mixed Python/.NET mesh](compatibility-mesh.md): the opt-in mixed mesh test, its topology and its recovery observations.

## Project records

- [Contributing](../.github/CONTRIBUTING.md): development workflow, tests, and moving to a new upstream release.
- [TODO](../TODO.md): the active checklist of remaining work.
- [Development history](../.github/development-history.md): chronology and verification evidence.
- [Archive](archive/): the historical roadmap ([plan](archive/plan.md)) and [completed checklist items](archive/completed-todos.md).
