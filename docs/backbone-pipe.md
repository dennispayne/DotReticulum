# Backbone and Pipe interfaces

## Backbone interface

The Backbone interface is a fast TCP bearer intended for interconnecting Reticulum instances. It uses HDLC framing, represents every accepted connection as a separate peer, reconnects as a client, and applies queue-pressure control. As in upstream, **this interface type is only supported on Linux and Android**, and in this implementation only Linux is exercised.

A Backbone interface is either a listener or a remote. It becomes a remote when you give it a target host.

### Listeners

```
# Listen on a specific IP address and port.
[[Backbone Listener]]
  type = BackboneInterface
  enabled = yes
  listen_on = 0.0.0.0
  port = 4242

# Or on the address of a network device,
# preferring IPv6 if it has both.
[[Backbone Listener]]
  type = BackboneInterface
  enabled = yes
  prefer_ipv6 = yes
  device = eth0
  port = 4242
```

### Connecting remotes

```
[[Backbone Remote]]
  type = BackboneInterface
  enabled = yes
  remote = amsterdam.connect.reticulum.network
  target_port = 4251
```

`BackboneClientInterface` is also accepted as a type name and always selects a remote.

### Automated blocking

Listeners automatically block fast-flapping clients that repeatedly connect for a short time and then disconnect. The values below are the defaults, so you only need them to change the behaviour.

```
[[Backbone Listener]]
  type = BackboneInterface
  enabled = yes
  listen_on = 0.0.0.0
  port = 4242

  # Whether to enable blocking
  block_fast_flapping = yes

  # How long an address stays blocked,
  # in minutes (12 hours).
  fast_flapping_block_time = 720

  # Minimum seconds a connection must last
  # to not count as a fast flap.
  fast_flapping_threshold = 20

  # Fast flaps allowed before blocking.
  fast_flapping_grace = 5
```

A peer is blocked only once its flap count exceeds the grace value. Flap counts expire relative to the last short disconnect.

### Options

| Key | Default | Meaning |
| --- | --- | --- |
| `listen_ip` | none | Listener bind address. `listen_on` is an alias. |
| `device` | none | Network device whose address the listener binds. Takes precedence over a host. |
| `listen_port` | required for listeners | Listener port |
| `target_host` | none | Remote host. `remote` is an alias. Setting it makes the interface a remote. |
| `target_port` | required for remotes | Remote port |
| `port` | none | Supplies both `listen_port` and `target_port` |
| `prefer_ipv6` | `no` | Use the device's IPv6 address |
| `connect_timeout` | 5 seconds | Time allowed for one connection attempt |
| `max_reconnect_tries` | unlimited | Connection attempts after the initial one |
| `block_fast_flapping` | `yes` | Block fast-flapping clients |
| `fast_flapping_threshold` | 20 seconds | Disconnects sooner than this count as flaps |
| `fast_flapping_grace` | 5 | Flaps allowed before blocking |
| `fast_flapping_block_time` | 720 minutes | Block duration |
| `bitrate` | 100 Mbit/s | Generic bitrate estimate |

A listener needs a port and either an address or a device. The hardware MTU is 1 MiB and the default IFAC size is 16 bytes. Upstream accepts an `i2p_tunneled` setting for Backbone, but the pinned Backbone source does not implement I2P tunnel behaviour, and the native runtime does not accept the key.

### Behaviour

Backbone uses HDLC framing (`0x7e` flag, `0x7d` escape, XOR `0x20`). A listener sends through its children, never through the parent, so routing can exclude the ingress peer. Queue-full, offline and stalled sends fail explicitly and are diagnosed. Successful queue admission on a client does not confirm that the remote received the packet.

Queue pressure is controlled in both directions:

- **Ingress.** The controller samples the router's global DATA queue every 250 ms, with watermarks at 90%, 68% and 10%. High pressure gates the largest packet producer and low pressure releases gated producers. Producer headroom is allocated with a minimum of 32 interfaces and a 1.5 penalty factor.
- **Egress.** Sampled every second. A peer with more than 128 KiB buffered is marked stalled when its estimated drain time exceeds ten seconds, or after three consecutive ticks with no progress. It is released when the drain time falls below five seconds or the buffer drops to 128 KiB. A peer that makes no progress for 12 seconds is disconnected. The transmit queue holds at most 4 MiB by default.

On Linux, Backbone sockets use a 32 KiB receive buffer, a 24-second TCP user timeout, keepalive idle, interval and count of 5 s, 2 s and 12, and TCP_NODELAY. The listener backlog is one. `SO_REUSEADDR` is enabled and `SO_REUSEPORT` disabled to keep the bind exclusive. Backbone framing accepts shared HDLC delimiters and preserves unknown escape pairs, as upstream does.

### Library API

`BackbonePacketInterface` listens and `BackboneClientPacketInterface` connects. Queue limits (bytes and frames), maximum clients (64), maximum flap-table entries, write timeouts and reconnect controls are bounded native options.

## Pipe interface

The Pipe interface lets Reticulum use any program as an interface through its stdin and stdout. You can use it to build virtual interfaces or to connect custom hardware and other systems.

```
[[Pipe Interface]]
  type = PipeInterface
  enabled = yes

  # External command to execute
  command = netcat -l 5757

  # Optional respawn delay, in seconds
  respawn_delay = 5
```

| Key | Default | Meaning |
| --- | --- | --- |
| `command` | required | Program and arguments to run |
| `respawn_delay` | 5 seconds | Wait before restarting the program |
| `bitrate` | 1 Mbit/s | Generic bitrate estimate |

The command is split into a program and arguments using POSIX shell-like rules, then launched directly without a shell. No PowerShell or other shell is invoked implicitly. Standard error is inherited. Hardware MTU is 1064 bytes, the default IFAC size is 8 bytes, and the receive queue holds 256 frames by default.

### Behaviour

Reticulum writes HDLC-framed packets to the program's stdin and reads packets from its binary stdout. If the initial launch fails, the error is surfaced to the owner. After a later exit, end of file or read failure, the program is restarted after the respawn delay. A partial frame from the old process is never replayed. Writes are serialized and bounded, and a failed write terminates the owned process so the next frame is not corrupted. Disposal terminates and awaits only the process the interface started.

Received byte counts are decoded packet bytes. Transmitted byte counts are framed bytes written.

The native `PipePacketInterface` API takes a program and an argument list and does not split or expand argument strings. It bounds the respawn failure policy, the frame queue and the confirmed-write timeout.

## Differences from upstream

- Native Backbone scopes shared ingress and flap state to the owning manager and bounds the flap table.
- When hosted by the router, queue pressure is measured on the router's actual DATA scheduler queue, not the manager's merge queue.
- Native Backbone honours `connect_timeout`. The upstream client parses it but uses a fixed five-second constant.
- Native Backbone clients try resolved addresses in order.
- The native Pipe decoder uses alternating flags and rejects malformed or oversized frames. Upstream accepts unknown escapes and can forward a truncated, corrupt packet.
- Pipe receive memory is bounded.

## Platform notes

Backbone keeps the upstream Linux and Android platform gate. Linux x64 is exercised. Backbone on Windows and macOS is unsupported, and Android behaviour is not established by the platform gate alone. Pipe uses .NET process and stream APIs and is exercised on Windows x64 and Linux x64.

## Verification

- `Parity6BackboneTests`, `Parity6BackboneSharedIngressTests` and `Parity6PipeTests` cover bounded framing, child isolation, queue feedback, flap limits, process ownership, restart and cancellation.
- `GenerateParity6BackbonePipeVectors.py` invokes pinned reference methods through controlled adapters.

Linux bearer selection and Windows Pipe process fixtures are exercised. These tests do not certify physical networks or Backbone support on macOS or Android.
