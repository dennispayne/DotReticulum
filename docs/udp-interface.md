# UDP interface

UDP carries one Reticulum packet per datagram, without additional framing. The interface can receive, forward, or do both using independent endpoints.

## Configuration

Enable `type = UDPInterface` in an interface section. Type-specific keys are `listen_ip`, `listen_port`, `forward_ip`, `forward_port`, `port`, and `device`. `port` supplies only a direction's missing port. A receive endpoint needs an address (or `device`) and port; forwarding needs a destination address (or `device`) and port. At least one complete direction is required. `device` supplies its first IPv4 broadcast address only where an address was omitted. The default address family is IPv4. Generic `bitrate` defaults to 10 Mbit/s; hardware MTU is 1064 bytes.

The native `UdpPacketInterfaceOptions` API permits separate receive and forwarding configuration and asynchronous hostname resolution. Direct endpoint constructors also support matching IPv4 or IPv6 endpoints. Port zero is available for native ephemeral receive binds; the resulting local endpoint is exposed after startup.

## Behaviour

The interface binds a receive socket only when reception is configured. Forwarding sends a packet as a datagram to the configured endpoint. Hostname resolution uses the configured family; a literal address of another family is rejected. Datagrams larger than hardware MTU plus the bounded IFAC allowance are discarded. Sends are serialized and cancellation-aware. A receive-only interface rejects send attempts, while a forward-only receive stream remains alive until cancellation or disposal.

## Platform notes

The implementation uses .NET sockets and network-interface enumeration. Windows x64 and Linux x64 are exercised. Loopback and simulator tests do not establish broadcast delivery on a physical LAN, IPv6 multicast behavior, or platform-specific interface routing.

## Differences from upstream

The native API supports explicit IPv6 endpoints and asynchronous startup resolution. Invalid or incomplete endpoint pairs fail explicitly; unused incomplete pairs are diagnosed. A receive-only send fails explicitly instead of reproducing the reference's caught missing-field exception. Bounded datagram input and serialized sends prevent unbounded buffering.

## Verification

`Parity6UdpTests` covers receive-only, forward-only, combined configuration, port precedence, resolution, malformed/oversized datagrams, and lifecycle behavior. `GenerateParity6UdpVectors.py` executes the pinned RNS 1.5.7 constructor at `a29bfb51c468945c96a1ee22cae0114e993e2391` with ephemeral loopback listeners. These tests are software evidence, not physical-network interoperability.
