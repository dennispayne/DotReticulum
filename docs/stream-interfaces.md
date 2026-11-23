# TCP and serial interfaces

The TCP and serial interfaces carry Reticulum packets over byte streams, using HDLC framing to separate packets. Use the TCP server and client interfaces to connect peers over the Internet or private IPv4 and IPv6 networks. Use the serial interface to run Reticulum directly over a serial port, a wire pair, or any device that passes serial data transparently, such as a data radio.

## TCP server interface

The TCP server interface listens for incoming connections from TCP client interfaces. Many clients can connect to the same server at once, and each accepted client becomes a separate peer interface with its own ingress identity, so routing can exclude the peer a packet arrived from.

```
# Listen on all IPv4 addresses on port 4242.
[[TCP Server Interface]]
  type = TCPServerInterface
  enabled = yes
  listen_ip = 0.0.0.0
  listen_port = 4242

# Or bind to the address of a specific
# network device, preferring IPv6.
[[TCP Server Interface]]
  type = TCPServerInterface
  enabled = yes
  device = eth0
  prefer_ipv6 = yes
  port = 4242
```

| Key | Default | Meaning |
| --- | --- | --- |
| `listen_port` | required | Port to listen on. `port` is an alias. |
| `listen_ip` | wildcard IPv4 | Address to bind |
| `device` | none | Network device whose address is used for the bind |
| `prefer_ipv6` | `no` | Use the device's IPv6 address when it has both |
| `fixed_mtu` | none | Fixed hardware MTU, at least 500 bytes |
| `i2p_tunneled` | `no` | Use the I2P socket timeout and keepalive profile |
| `bitrate` | 10 Mbit/s | Generic bitrate estimate |

If you give only a port, the native daemon binds to all IPv4 addresses. Hostname and device resolution is deferred until the interface starts. The native server admits at most 64 clients. Upstream has no client limit. Accepted peers always use HDLC framing.

## TCP client interface

The TCP client interface connects to a TCP server interface. It tolerates intermittent IP links: if the connection drops, the interface reconnects once the other end reappears.

```
# target_host can be a hostname or an
# IPv4 or IPv6 address.
[[TCP Client Interface]]
  type = TCPClientInterface
  enabled = yes
  target_host = 127.0.0.1
  target_port = 4242
```

You can also use the client to reach programs or devices that expose KISS on a TCP port, such as software soundmodems:

```
[[TCP KISS Interface]]
  type = TCPClientInterface
  enabled = yes
  kiss_framing = yes
  target_host = 127.0.0.1
  target_port = 8001
  fixed_mtu = 500
```

Only use `kiss_framing` for external KISS devices and programs, never between Reticulum TCP client and server interfaces. A KISS client does not request the Reticulum transport tunnel that an HDLC client does.

| Key | Default | Meaning |
| --- | --- | --- |
| `target_host` | required | Host name or address to connect to |
| `target_port` | required | Port to connect to |
| `connect_timeout` | 5 seconds | Time allowed for one connection attempt |
| `max_reconnect_tries` | unlimited | Connection attempts after the initial one |
| `kiss_framing` | `no` | Use port-zero KISS framing instead of HDLC |
| `fixed_mtu` | none | Fixed hardware MTU, at least 500 bytes |
| `i2p_tunneled` | `no` | Use the I2P socket timeout and keepalive profile |
| `bitrate` | 10 Mbit/s | Generic bitrate estimate |

The reconnect delay is five seconds. Resolved addresses are tried in order within one shared connection timeout. Upstream connects only to the first resolved address.

## Serial interface

```
[[Serial Interface]]
  type = SerialInterface
  enabled = yes

  # Serial port for the device
  port = /dev/ttyUSB0

  # Set the serial baud rate and other
  # configuration parameters.
  speed = 115200
  databits = 8
  parity = none
  stopbits = 1
```

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | required | Serial port name |
| `speed` | 9600 | Baud rate. Also the default bitrate estimate. |
| `databits` | 8 | Data bits (5–8) |
| `parity` | `none` | `none`, `even` or `odd` |
| `stopbits` | 1 | Stop bits (1 or 2) |

The serial port runs with no handshake, but DTR and RTS stay asserted, as they do with pyserial. Hardware MTU is 564 bytes and the default IFAC size is 8 bytes. The daemon reopens a failed port after five seconds.

## Behaviour

### TCP

- `fixed_mtu` disables bitrate-driven MTU autoconfiguration. Link MTU negotiation remains available.
- TCP_NODELAY is enabled.
- On Linux, ordinary TCP uses a 24-second TCP user timeout and keepalive idle, interval and count of 5 s, 2 s and 12. With `i2p_tunneled`, these become 45 seconds and 10 s, 9 s and 5. On macOS, keepalive is enabled with a 5-second idle time (10 seconds with `i2p_tunneled`). These match the upstream values. On Windows, only TCP_NODELAY is set.
- Port-zero KISS applies only to initiating clients. A KISS client does not request a Reticulum transport tunnel.
- `max_reconnect_tries` counts actual connection calls after the initial call.
- Sends while offline are discarded and counted in diagnostics, as in upstream.
- Send admission is serialized before a frame is allocated, so queued callers hold their own input memory rather than framed copies.
- A failed accepted child is isolated from the other peers.

### Serial

- Frames use alternating opening and closing HDLC flags.
- Partial data is checked on an 80 ms polling loop and expires after more than 100 ms idle.
- A port or reopenable stream factory retries open and read failures every five seconds, followed by upstream's 500 ms settle period after each open.
- The driver open call is synchronous. Cancellation is checked before opening and during the settle period, but cannot interrupt a driver blocked inside open.
- Received byte counts are decoded bytes. Transmitted byte counts include HDLC framing.

### Library API

`TcpClientPacketInterface`, `TcpServerPacketInterface` and `SerialPacketInterface` are started explicitly and dispose their owned resources asynchronously. The serial interface accepts a port name, an owned duplex stream or a reopenable stream factory. A supplied stream is one-shot and cannot reconnect.

The standalone TCP client and serial constructors leave reconnection off by default. The daemon turns it on to match upstream. Server queue limits and `fixed_mtu` are also available as native options.

Receive buffering is bounded at 256 queued frames plus the producer's frame and the frames decoded from one 4096-byte read buffer. Cancellation interrupts owned reads and unfinished writes; an interrupted write or flush retires the stream because the frame may be partial. A successful write and flush win a concurrent cancellation and leave the stream usable. A write that failed ambiguously is not replayed after reconnecting.

## Differences from upstream

- The native decoders reject malformed escapes and overlong frames. Upstream accepts malformed escape sequences and can forward an oversized serial prefix.
- The TCP server admits at most 64 clients. Upstream has no limit.
- Native TCP honours `connect_timeout`. Upstream parses the setting but uses a fixed five-second timeout.
- The native client tries every resolved address in order. Upstream uses only the first.
- Standalone TCP and serial constructors make reconnection opt-in. Daemon wiring selects upstream-style reconnection.

## Platform notes

TCP and stream handling is exercised on Windows x64 and Linux x64. macOS ARM64 builds and passes the managed tests. Generic stream and socket tests do not establish device-driver or physical serial support. See [interface platforms](interface-platforms.md).

## Verification

- `Parity6StreamTests` exercises TCP framing and reconnection, and serial parsing and configuration, against simulated sockets and ports.
- `GenerateParity6StreamVectors.py` produces vectors from the pinned source. It never opens a physical serial device.

These tests are software evidence. They are not a live Python TCP or KISS exchange, and not physical serial acceptance.
