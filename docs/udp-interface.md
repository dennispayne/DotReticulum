# UDP interface

A UDP interface carries Reticulum packets over IP networks, either private networks or the Internet. Each Reticulum packet travels in one UDP datagram, with no additional framing. Because UDP can also send to a broadcast address, it gives you an easy way to reach every peer on a local network segment.

If your goal is simply to reach all peers in your local Ethernet or WiFi broadcast domain, the [AutoInterface](auto-interface.md) is usually the better choice. Broadcast UDP has performance costs, especially on WiFi.

## Example configuration

```
# This example enables communication with other
# local Reticulum peers over UDP broadcast.
[[UDP Interface]]
  type = UDPInterface
  enabled = yes

  listen_ip = 0.0.0.0
  listen_port = 4242
  forward_ip = 255.255.255.255
  forward_port = 4242

  # Instead of listen_ip and forward_ip, you can
  # name a network device. Its first IPv4 broadcast
  # address is used for any address you leave out.
  # device = eth0
  # port = 4242

  # You can also communicate with a single host.
  # listen_ip = 10.55.0.15
  # listen_port = 4242
  # forward_ip = 10.55.0.16
  # forward_port = 4242
```

## Options

| Key | Default | Meaning |
| --- | --- | --- |
| `listen_ip` | none | Address to receive on |
| `listen_port` | none | Port to receive on |
| `forward_ip` | none | Address to send datagrams to |
| `forward_port` | none | Port to send datagrams to |
| `port` | none | Fills in `listen_port` or `forward_port` when that key is missing |
| `device` | none | Network device whose first IPv4 broadcast address fills in a missing `listen_ip` or `forward_ip` |
| `bitrate` | 10 Mbit/s | Generic bitrate estimate |

The interface can receive, forward or do both. Receiving needs an address (or `device`) and a port. Forwarding needs a destination address (or `device`) and a port. At least one complete direction is required, and an enabled interface with neither fails to load. The default address family is IPv4. Hardware MTU is 1064 bytes.

## Behaviour

A receive socket is bound only when reception is configured. Forwarding sends each packet as one datagram to the configured endpoint. Hostnames are resolved using the configured address family, and a literal address of another family is rejected. Datagrams larger than the hardware MTU plus the bounded IFAC allowance are discarded. Sends are serialized and cancellation-aware.

A receive-only interface rejects send attempts. A forward-only interface keeps its receive stream open until cancellation or disposal.

### Library API

The native `UdpPacketInterfaceOptions` API configures receive and forwarding separately and resolves hostnames asynchronously at startup. Direct endpoint constructors also accept matching IPv4 or IPv6 endpoints. Port zero requests an ephemeral receive port, and the bound local endpoint is exposed after startup.

## Differences from upstream

- The native API supports explicit IPv6 endpoints and asynchronous startup resolution.
- Invalid or incomplete endpoint pairs fail explicitly, and unused incomplete pairs are diagnosed.
- A send on a receive-only interface fails explicitly. The reference instead raises and catches a missing-field exception.
- Datagram input is bounded and sends are serialized, so nothing is buffered without limit.

## Platform notes

The implementation uses .NET sockets and network-interface enumeration. Windows x64 and Linux x64 are exercised. Loopback and simulator tests do not establish broadcast delivery on a physical LAN, IPv6 multicast behaviour or platform-specific interface routing.

## Verification

- `Parity6UdpTests` covers receive-only, forward-only and combined configuration, port precedence, resolution, malformed and oversized datagrams, and lifecycle behaviour.
- `GenerateParity6UdpVectors.py` executes the pinned RNS 1.5.7 constructor at `a29bfb51c468945c96a1ee22cae0114e993e2391` with ephemeral loopback listeners.

These tests are software evidence, not physical-network interoperability.
