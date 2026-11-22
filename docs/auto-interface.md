# AutoInterface

The `AutoInterface` lets you communicate with other discoverable Reticulum nodes over any local Ethernet or WiFi medium. It uses IPv6 link-local multicast for peer discovery and UDP for packet transport, one packet per datagram. It does not need routers, DHCP servers or any other IP infrastructure on the network.

Link-local IPv6 must be available on your system. If you run firewall software, allow UDP traffic on the discovery and data ports (29716 and 42671 by default).

## Example configuration

```
# A bare-minimum AutoInterface. It will talk to all
# reachable peers on all usable network devices.
[[Default Interface]]
  type = AutoInterface
  enabled = yes

# A more specific configuration.
[[Default Interface]]
  type = AutoInterface
  enabled = yes

  # Separate Reticulum networks on the same
  # LAN can use different group IDs.
  group_id = reticulum

  # temporary (default) or permanent
  multicast_address_type = permanent

  # Use only these devices...
  devices = wlan0,eth1

  # ...or use all suitable devices except these.
  ignored_devices = tun0,eth0
```

## Options

| Key | Default | Meaning |
| --- | --- | --- |
| `group_id` | `reticulum` | UTF-8 discovery group name |
| `discovery_scope` | `link` | `link`, `admin`, `site`, `organisation` or `global` |
| `multicast_address_type` | `temporary` | `temporary` or `permanent`. Unrecognized values use the temporary form. |
| `discovery_port` | `29716` | IPv6 multicast discovery port (1–65534) |
| `data_port` | `42671` | Peer datagram port (1–65535) |
| `devices` | empty | Network device names or stable interface IDs to use |
| `ignored_devices` | empty | Devices to exclude |
| `bitrate` | 10 Mbit/s | Generic bitrate estimate |

The group ID only separates discovery groups. Its discovery token can be computed by anyone who knows the group ID, so it is not authentication. Use IFAC if you need access control.

## Behaviour

AutoInterface is a spawning interface. Every discovered peer address becomes its own child interface that can be routed independently. The parent itself carries no application packets, and packets are never reflected back to the peer they came from.

- **Discovery.** The multicast address is derived from the group ID and scope. Each announcement carries a token, `SHA256(UTF8(group_id) || canonical_unscoped_source_address_UTF8)`. Only IPv6 link-local sources with a valid token become peers. As in upstream, a datagram whose first 32 bytes match the token is accepted even if it has trailing bytes. Announcements from your own address are treated as echoes, not peers.
- **Timing.** The interface waits 1.92 seconds after startup, announces every 1.6 seconds and runs maintenance every four seconds. Reverse announcements follow after 5.2 seconds, and own-echo carrier state expires after 6.5 seconds. Peers expire after 22 seconds (27.5 seconds on Android).
- **Data.** Peers exchange raw UDP datagrams with no HDLC framing or added envelope. Duplicate frames seen within 750 ms are suppressed using a bounded 48-entry cache.
- **Device changes.** If a selected device disappears, its sockets are closed. They are reopened when it returns. The listener is replaced if a device's link-local address changes. A device or peer failure does not affect unrelated peers. Device setup failures are diagnosed and retried during maintenance. If no selected device is usable, the interface stays passive and reports why.
- **MTU and IFAC.** The parent and children report a 1196-byte hardware MTU and support MTU discovery. With IFAC enabled and no size configured, the IFAC size is 16 bytes.

### Discovery details

Scopes map to IPv6 multicast scope values: link 2, admin 4, site 5, organisation 8 and global 14. The multicast address uses bytes 2–13 of the group hash and the temporary or permanent multicast flag. Discovery is sent on `discovery_port`, and the reverse discovery listener uses that port plus one.

On each selected device, the interface uses the last IPv6 link-local address, joins the multicast group on that interface index, and uses a paired data socket so the source address is preserved. On Windows, discovery sockets bind to the wildcard address. Other platforms follow the upstream branches that bind to the group or local address. Device selection excludes loopback and follows upstream's platform-specific exclusions.

### Bounds

By default the interface accepts up to 128 peers and queues up to 32 frames per peer. At the largest frame size of 1260 bytes (the 1196-byte MTU plus up to 64 IFAC bytes), this caps queued payload storage at 5,160,960 bytes. When a peer's receive queue is full, new datagrams are dropped and counted. Each peer has at most one active transmit. A competing send to the same peer is rejected rather than held in caller memory without limit.

### Library API

The parent is `AutoPacketInterface(AutoPacketInterfaceOptions? options = null)`. Subscribe to `ChildConnected` before calling `StartAsync`, or let `PacketInterfaceManager` adopt the children. The peer limit (128) and per-peer queue size (32) are library options, not configuration keys. `ConfiguredBitrate` holds the configured bitrate.

## Differences from upstream

- Peer count, receive queues and concurrent peer sends are bounded. A full receive queue drops datagrams instead of letting a stalled callback block maintenance.
- Malformed or oversized datagrams, and discovery from non-link-local sources, are rejected.
- Startup and disposal are explicit and cancellable.
- The initially selected devices are not reselected dynamically, though their disappearance and return are handled.
- No Android permission acquisition or native network-information adapter is provided.

## Platform notes

IPv6 multicast membership and link-local addressing are required. Windows x64 and Linux x64 are exercised. The opt-in multicast test checks the actual OS and device capability and skips explicitly when prerequisites are absent. Managed tests do not establish multicast support on any particular physical network. Android permissions, mobile network information and BSD behaviour are unverified. See [interface platforms](interface-platforms.md).

## Verification

- `AutoInterfaceTests` covers deterministic behaviour.
- `Parity6AutoLiveTests`, when explicitly enabled, runs the pinned Python implementation and the .NET interface in two Linux network namespaces, in raw and IFAC modes. This is software interoperability across namespaces, not physical LAN or mobile support.
- `Parity6AutoMulticastTests` is a separate opt-in local multicast self-echo check.

Reference vectors and live adapters use the pinned RNS 1.5.7 source at `a29bfb51c468945c96a1ee22cae0114e993e2391`.
