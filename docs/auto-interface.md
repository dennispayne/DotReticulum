# AutoInterface

AutoInterface discovers Reticulum peers on IPv6 multicast links and carries one packet per UDP datagram. The .NET bearer is a spawning interface: each discovered address is represented by an independently routable child. The parent does not transmit application packets itself.

## Configuration

Enable `type = AutoInterface` in an interface section. Supported type-specific keys are:

| Key | Default | Meaning |
| --- | --- | --- |
| `group_id` | `reticulum` | UTF-8 discovery group discriminator |
| `discovery_scope` | `link` | `link`, `admin`, `site`, `organisation`, or `global` |
| `multicast_address_type` | `temporary` | Multicast address type; unrecognized values use the temporary form |
| `discovery_port` | `29716` | IPv6 multicast discovery port (1–65534) |
| `data_port` | `42671` | Peer datagram port (1–65535) |
| `devices` | empty | Selected network device names or stable interface IDs |
| `ignored_devices` | empty | Devices excluded from selection |
| `bitrate` | `10000000` bit/s | Generic configured estimate; native options expose `ConfiguredBitrate` |

The native options also bound peers to 128 and each peer receive queue to 32 frames by default. These are library settings, not additional INI keys. Group names only distinguish discovery groups; the discovery token is not authentication.

## Behaviour

The multicast address is derived from the UTF-8 group ID and scope. Discovery announcements use a SHA-256 token over the group ID and canonical IPv6 source address. Only IPv6 link-local sources with a valid token create peers. Interface selection excludes loopback and follows the reference's platform-specific device exclusions. Each peer sends and receives raw UDP datagrams without HDLC framing or an added envelope.

The bearer observes a 1.92-second startup warmup, announces every 1.6 seconds, and runs maintenance every four seconds. Peers expire after 22 seconds (27.5 seconds on Android). Duplicate frames are suppressed for less than 750 ms using a bounded 48-entry cache. Receive queues and peer counts are bounded; full queues drop new datagrams with diagnostics. A child disconnect or device failure does not terminate unrelated peers. Selected-device disappearance closes its socket session; a later return reopens it.

The parent exposes `AutoPacketInterface(AutoPacketInterfaceOptions? options = null)`. Subscribe to `ChildConnected` before `StartAsync`, or let `PacketInterfaceManager` adopt children. The parent and children report a 1196-byte hardware MTU and support MTU discovery. The default IFAC size is 16 bytes when IFAC is enabled and no size is configured.

### Discovery details

The scope field maps to IPv6 multicast scope values: link 2, admin 4, site 5, organisation 8, and global 14. The multicast address uses group-hash bytes 2–13 and the selected temporary/permanent multicast flag. Discovery sends on `discovery_port`; the reverse discovery listener uses that port plus one. The token is `SHA256(UTF8(group_id) || canonical_unscoped_source_address_UTF8)`. A matching 32-byte token prefix is accepted even if a datagram has trailing bytes, matching upstream. This token is publicly computable by anyone who knows the group ID.

The bearer selects the last IPv6 link-local address on each selected device, joins multicast on that interface index, and uses the paired data socket to preserve source address. Windows uses wildcard discovery binds; other source branches bind to group/local-address selection. The parent itself carries no data: every discovered address is a child, and packets are not reflected to ingress. Own-address announcements are treated as echoes, not peers. Reverse announcements follow after 5.2 seconds; own-echo carrier state expires after 6.5 seconds. A listener is replaced if the selected link-local address changes.

The default maximum of 128 peers and 32 queued frames per peer bounds queued payload storage to 5,160,960 bytes at the maximum 1260-byte frame size (1196-byte packet MTU plus up to 64 IFAC bytes). Each peer may also have one active transmit. Calls competing for the same peer send are rejected rather than accumulated in unbounded caller memory. Device setup failures are isolated, retried on maintenance, and diagnosed; no usable selected device leaves a diagnosed passive interface.
## Platform notes

IPv6 multicast membership and link-local addressing are required. Windows x64 and Linux x64 are exercised; the opt-in multicast test checks actual OS/device capability and explicitly skips when prerequisites are absent. Managed tests do not establish multicast support on a particular physical network. Android permissions, mobile network information, BSD behavior, and untested platforms remain unverified. See [interface platforms](interface-platforms.md).

## Differences from upstream

The .NET implementation bounds peer count, receive queues, and concurrent peer sends; it drops on a full receive queue rather than allowing a stalled callback to block maintenance. It rejects malformed/oversized datagrams and discovery from non-link-local sources. Startup and disposal are explicit and cancellable. Initial selected devices are not dynamically reselected, though their disappearance/reappearance is handled. No Android permission acquisition or native network-information adapter is provided.

## Verification

`AutoInterfaceTests` covers deterministic behavior. `Parity6AutoLiveTests` exercises two Linux network namespaces with the pinned Python implementation in raw and IFAC modes when explicitly enabled; this verifies software namespace interoperability, not physical LAN or mobile support. `Parity6AutoMulticastTests` is a separate opt-in local multicast self-echo check. Reference vectors and live adapters verify the pinned RNS 1.5.7 source at `a29bfb51c468945c96a1ee22cae0114e993e2391`.
