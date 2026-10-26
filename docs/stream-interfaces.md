# TCP and serial interfaces

TCP and serial interfaces carry Reticulum packets over HDLC-framed streams. A TCP client can optionally use port-zero KISS framing; a TCP server uses HDLC for accepted peers. Each accepted peer has an independent ingress identity.

## TCP configuration

`TCPClientInterface` requires `target_host` and `target_port`. Optional keys are `connect_timeout` (5 seconds), `max_reconnect_tries` (unlimited by default), `kiss_framing` (false), `i2p_tunneled` (false), `fixed_mtu`, and generic `bitrate`. Reconnect delay is five seconds. `i2p_tunneled` controls native tunnel synthesis for clients; server children remain HDLC. TCP_NODELAY is enabled.

`TCPServerInterface` requires `listen_port`. `listen_ip` or `device` selects the bind address; the native daemon also supports a port-only wildcard IPv4 bind. `port` is an alias for the server port. `prefer_ipv6` defaults false. The native server admits at most 64 clients by default; `fixed_mtu` and queue limits are native API controls. Server constructors defer hostname/device resolution until `StartAsync`.

TCP fixed MTU disables bitrate-driven MTU autoconfiguration while link MTU negotiation remains available. Offline sends are discarded with diagnostics. A native send failure is diagnosed; a failed child is isolated from other peers.

## Serial configuration

`SerialInterface` requires `port`; supported settings are `speed` (9600), `parity` (none), `databits` (8), and `stopbits` (1). Hardware MTU is 564 bytes. DTR and RTS remain asserted as in pyserial even when handshake flow control is disabled. Stream constructors accept either an owned duplex stream or a reopenable factory. A supplied stream is one-shot. Source-style runtime operation enables reconnect for a port; the standalone constructor defaults it off.

Serial uses alternating opening and closing HDLC flags. Partial frames expire after more than 100 ms idle. A port or reopenable factory retries open/read failures every five seconds. One serial driver open call is synchronous and cannot itself be interrupted by a cancellation token.

## Native API and behaviour

`TcpClientPacketInterface`, `TcpServerPacketInterface`, and `SerialPacketInterface` start explicitly and asynchronously dispose owned resources. Client address resolution tries resolved addresses in order within one timeout; accepted server peers are separate interfaces. Receive queues and send operations are bounded. Concurrent writes serialize before framing allocation. Cancellation interrupts owned reads/writes; a failed ambiguous active write is not replayed after reconnect. TCP/serial byte accounting reports actual framed/unframed boundaries described by diagnostics.

### TCP and serial transport details

The source TCP client enables TCP_NODELAY. On Linux, ordinary TCP uses a 24-second TCP user timeout and keepalive idle/interval/count of 5/2/12 seconds; I2P TCP uses 45 seconds and 10/9/5 seconds. Darwin sets keepalive and a five-second idle time. Native code configures matching supported platform options where implemented. A configured TCP client `connect_timeout` defaults to five seconds; `max_reconnect_tries` counts actual connection calls after the initial call in the reference contract. Native address fallback tries resolver results in order within one shared timeout, unlike upstream's first-address-only behavior.

TCP fixed MTU disables bitrate-driven MTU autoconfiguration while link MTU negotiation remains enabled. Port-zero KISS applies only to initiating clients; accepted TCP server children always use HDLC. A KISS client must not request Reticulum transport tunnel synthesis. Native servers cap clients at 64; standalone client reconnection remains opt-in, while daemon runtime supplies source-style reconnect.

Serial source defaults are 9600 baud, 8/N/1, no handshake, and MTU 564. Partial data is checked on an 80 ms polling loop and expires after more than 100 ms idle. Port/factory reopen uses a five-second delay and source's 500 ms post-open settle period. The driver open call is synchronous; cancellation can be checked before opening and during asynchronous settling, but cannot interrupt a native driver blocked inside open. Supplied streams remain owned one-shot streams.

The receive side uses bounded queues: native stream buffering is capped at 256 queued frames plus the producer frame and decoded frames from a 4096-byte read buffer. TCP send admission is serialized before frame allocation; queued callers retain input memory rather than allocating framed copies. Offline TCP sends are discarded with a diagnostic counter, matching source behavior. Serial counts decoded receive bytes, while transmitted byte accounting includes HDLC framing.
## Platform notes

The TCP and stream abstractions are exercised on Windows x64 and Linux x64. macOS ARM64 builds and managed tests pass, but rnsh's Darwin PTY backend is unavailable. Generic stream/socket tests do not establish device-driver or physical serial support. See [interface platforms](interface-platforms.md).

## Differences from upstream

The native parser rejects malformed escapes and overlong frames rather than accepting malformed escape sequences or forwarding an oversized serial prefix. The server caps clients at 64; upstream has no cap. Standalone TCP and serial constructors retain opt-in reconnect behavior, while daemon wiring selects source-style reconnect. Native TCP honors its explicit connection timeout; upstream parses that setting but uses a fixed five-second timeout. The native client tries multiple DNS results, while upstream uses the first result.

## Verification

`Parity6StreamTests` and `GenerateParity6StreamVectors.py` exercise pinned TCP framing/reconnect and serial parsing/configuration with simulated sockets and ports. No physical serial device is opened by vector generation. These tests are software evidence, not live Python TCP/KISS exchange or physical serial acceptance.
