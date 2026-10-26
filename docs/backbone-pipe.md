# Backbone and Pipe interfaces

Backbone is a Linux TCP bearer with HDLC framing, accepted-peer identities, reconnecting clients, and queue-pressure control. Pipe connects a packet interface to a child process's stdin/stdout using HDLC framing. Both expose packet-interface children and isolate individual peer/process failures.

## Backbone configuration

Enable `type = BackboneInterface`. A listener needs `listen_port` and either `listen_ip` or `device`; `device` selects a local address and takes precedence over a host. `port` supplies the port, `listen_on` aliases `listen_ip`, and `remote` aliases `target_host`. Supplying `target_host` selects an initiating client; clients also require `target_port`. `prefer_ipv6` defaults false.

Fast-flap controls are `block_fast_flapping` (true), `fast_flapping_threshold` (20 seconds), `fast_flapping_grace` (5 disconnects), and `fast_flapping_block_time` (720 minutes). `connect_timeout` defaults to 5 seconds; `max_reconnect_tries` is unlimited by default. `i2p_tunneled` is accepted but does not implement I2P tunnel behavior in the pinned Backbone source. Generic `bitrate` overrides the estimate; hardware MTU is 1 MiB and default IFAC size is 16 bytes.

The native API exposes `BackbonePacketInterface` for listening and `BackboneClientPacketInterface` for initiating. Accepted peers have independent ingress identity. Queue limits, maximum clients (64), maximum flap entries, write timeouts, and reconnect controls are bounded native options. A listener sends through children, not through its parent.

## Pipe configuration

Enable `type = PipeInterface`, provide required `command`, and optionally set `respawn_delay` (5 seconds). The command is split into executable and arguments using POSIX shell-like tokenization; it is launched without a shell. Stderr is inherited. The native `PipePacketInterface` API takes an executable and argument list and does not re-split or expand argument strings.

Pipe defaults to a 1 Mbit/s estimate, 1064-byte hardware MTU, and 8-byte IFAC size. Its receive queue defaults to 256 frames. The native API bounds respawn failure policy, frame queue, and confirmed-write timeout.

## Behaviour

Backbone uses HDLC (`0x7e` flag, `0x7d` escape, XOR `0x20`). Listener and clients use bounded receive/transmit queues. Backbone ingress feedback samples the router's global DATA queue; high pressure gates the largest packet producer and low pressure releases gated producers. Fast-flap counts block only after the configured grace is exceeded and expire relative to the last short disconnect. Queue-full, offline, and stalled sends fail explicitly with diagnostics. Successful client queue admission is not confirmation of remote receipt.

Pipe reads binary stdout, writes framed packets to stdin, and respawns after a child exit or read failure. Initial spawn failure is surfaced to the owner. Disposal terminates and awaits only the process it created. Restart does not replay a possibly partial old frame. A supplied command does not invoke PowerShell or another shell implicitly.

### Transport and queue limits

Backbone defaults to a 100 Mbit/s estimate, 1 MiB hardware MTU, 16-byte IFAC, 20-second fast-flap threshold, grace 5, and 12-hour block expiry. The fast-flap block duration is expressed in minutes in configuration. A flap is a disconnect before the threshold; a peer is blocked only when its count exceeds grace. Expiry is measured from the last qualifying flap.

The source ingress controller samples every 250 ms and uses 90%, 68%, and 10% queue watermarks. Egress samples every second, gates above 4 MiB, releases below 128 KiB, and disconnects a producer when the queue remains stalled for 12 seconds. Native options bound queued bytes and frames. Linux Backbone TCP uses a 32 KiB receive buffer, 24-second TCP user timeout, and keepalive idle/interval/count of 5/2/12 seconds. It enables TCP_NODELAY. Accepted peers are represented separately so routing can exclude ingress.

Pipe defaults to a 1 Mbit/s estimate, 1064-byte MTU, and 8-byte IFAC. RX counts decoded packet bytes; TX counts framed bytes written. Initial spawn failure is an error; subsequent EOF/read failures retry after the configured delay. Native pipe writes are serialized and bounded; a failed write terminates the owned child to avoid corrupting the next frame.

Ingress uses source watermarks at 90%, 68%, and 10% and allocates producer headroom with a minimum of 32 interfaces and a 1.5 penalty factor. Egress gates when the estimated drain time exceeds ten seconds and releases below five seconds; three consecutive zero-progress ticks and a 12-second stall deadline bound recovery. The listener backlog is one. Linux socket setup explicitly enables `SO_REUSEADDR` and disables `SO_REUSEPORT` to preserve exclusive bind behavior. Backbone framing accepts shared HDLC delimiters and preserves unknown escape pairs, while the native Pipe decoder uses alternating flags and strict bounded-frame rejection rather than upstream's permissive unknown-escape/truncation behavior.
## Platform notes

Backbone preserves the reference's Linux/Android platform gate. Linux x64 is exercised; Windows/macOS Backbone operation is unsupported. Pipe uses .NET process and stream APIs and is exercised on Windows x64 and Linux x64. Android behavior is not established by the Backbone platform gate alone.

## Differences from upstream

Native Backbone scopes shared ingress/flap state to the owning manager and bounds the flap table. Queue pressure uses the router's actual DATA scheduler queue when hosted, not the manager merge queue. Native Pipe bounds receive memory and rejects malformed/oversized frames rather than forwarding a truncated corrupt packet. Native Backbone honors an explicit connection timeout; the upstream client parses `connect_timeout` but uses its fixed five-second constant. Native clients try resolved addresses in order.

## Verification

`Parity6BackboneTests`, `Parity6BackboneSharedIngressTests`, and `Parity6PipeTests` cover bounded framing, child isolation, queue feedback, flap limits, process ownership, restart, and cancellation. `GenerateParity6BackbonePipeVectors.py` invokes pinned reference methods with controlled adapters. Linux bearer selection and Windows Pipe process fixtures are exercised; these tests do not certify physical networks or macOS/Android Backbone support.
