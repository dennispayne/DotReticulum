# TCP and serial stream bearer contracts

Reference: Reticulum RNS 1.5.7, commit
`a29bfb51c468945c96a1ee22cae0114e993e2391`,
`RNS/Interfaces/TCPInterface.py` and `SerialInterface.py`.
These contracts describe the bounded Parity 6 stream work, not hardware or
platform certification.

## Source-backed acceptance

* Source TCP clients select the first resolved address and its address family
  (`connect`, lines 231–269). Native clients try addresses in resolver order
  within one shared connection timeout, retaining hostname dual-stack fallback;
  this intentional native difference preserves existing discovery behavior.
  Servers prefer IPv4 unless IPv6 is requested
  (`get_address_for_host`, lines 490–506). A literal endpoint remains explicit.
* TCP clients optionally use port-zero KISS (`process_outgoing`, lines 314–336,
  `read_loop`, lines 346–410). Accepted server children always use HDLC.
  KISS clients must not request transport tunnel synthesis.
* Reconnect waits five seconds, is unlimited by default, and permits exactly
  `max_reconnect_tries` actual connection calls after the initial call.
  Python increments then tests `attempts > maximum` **before** calling connect;
  this is not an extra permitted connection. Its final, non-connecting delay is
  not a native API timing requirement.
* TCP_NODELAY is enabled. Linux ordinary TCP uses USER_TIMEOUT=24000ms,
  KEEPALIVE=on, KEEPIDLE=5s, KEEPINTVL=2s, KEEPCNT=12; I2P uses
  45000ms/10s/9s/5. Darwin sets only keepalive and idle. The Python
  `connect_timeout` field is parsed but `connect` actually uses its constant
  five seconds. Native explicit timeout remains effective, an intentional fix.
* Fixed MTU disables bitrate-driven MTU autoconfiguration and is at least 500;
  link MTU negotiation remains enabled. Native maximum frame
  size remains bounded by the stack's supported maximum. Offline sends are
  discarded as upstream, with a diagnostic counter; failed writes are diagnosed.
* Serial defaults are 9600 baud, 8/N/1, no flow control, HW_MTU=564.
  Serial uses alternating opening/closing flags, unlike TCP shared flags.
  Partial buffered data expires after more than 100ms idle, observed on 80ms
  polling. Owned ports retry initial open and unplug failures every five seconds.
  Supplied streams remain owned, one-shot streams, not reopenable ports.
* Concurrent writes serialize **before** encoding allocation. Read admission
  uses bounded queues with backpressure; cancellation/disposal interrupts stalled
  reads, writes and retries. Failed server children cannot stop other peers.

## Intentional native differences and evidence gates

Malformed escapes, dangling escapes and overlong frames are strictly rejected
instead of upstream's permissive malformed-escape and serial oversized-prefix
behavior. Invalid packets are rejected by unprotected send validation or the
packet manager; IFAC frames remain raw until authentication. This hardening must not be weakened to manufacture
parity. Native server admission is capped at 64 clients by default; upstream has
no cap. Standalone TCP reconnection remains opt-in for compatibility.
The existing real-port serial constructor also retains opt-in reconnection
(`reconnect: true` is required for daemon/source behavior); a supplied stream
remains one-shot and the factory constructor always reconnects. A reconnecting
initial open failure returns an offline, retrying interface with `last_error`,
not a fabricated connection. Retry exhaustion terminates the TCP receive stream
and leaves the reason in diagnostics, preserving its existing API.

Reference vectors execute upstream framing, reconnect and serial reader methods
with simulated sockets/ports; no device is opened. Native tests use owned duplex
streams and loopback sockets. Neither establishes real serial/radio support.
COM4 has not been opened, transmitted to or flashed. Physical unplug recovery,
serial adapter settings, live Python TCP/KISS exchanges and OS-specific socket
option execution remain separate evidence gates.

## Native API and runtime wiring

`TcpClientPacketInterface` retains its existing parameters and appends
`bool kissFraming = false, bool i2pTunneled = false`. It exposes `KissFraming`,
`WantsTunnel` and `FixedMtu`. The runtime must check `WantsTunnel` before both
initial and reconnect tunnel synthesis. `connectTimeout` is a `TimeSpan`;
`maxReconnectAttempts` counts retries, excluding the initial connection.
`TcpServerPacketInterface` appends `bool i2pTunneled = false`; accepted children
remain HDLC. Its lazy-resolution overload is
`TcpServerPacketInterface(string? listenHost, int port, string? device = null,
bool preferIpv6 = false, int maxClients = DefaultMaxClients,
bool perClientInterfaces = false, long nominalBitrate = 10_000_000,
int? fixedMtu = null, bool i2pTunneled = false)`.
DNS/device resolution happens in cancellation-aware `StartAsync`, never in this
constructor. `TcpAddressResolver.ResolveListenEndpointAsync(string? listenHost,
int port, string? device = null, bool preferIpv6 = false,
CancellationToken cancellationToken = default)` resolves hostname/device
selection without a synchronous factory-side DNS shortcut. Device selection
takes precedence over a hostname.

The runtime maps server `listen_ip` to `listenHost`, `device` to `device`,
`prefer_ipv6` (default false) to `preferIpv6`, and `i2p_tunneled` (default false)
to `i2pTunneled`. Source `port` overrides `listen_port`; a bind port and either a
device or listen host are required **upstream**. The native daemon intentionally
preserves its longstanding port-only convenience: omitting both `listen_ip`
and `device` binds wildcard IPv4 (`0.0.0.0`), even if `prefer_ipv6` is true.
The lazy server constructor supports a null/blank `listenHost` for this native
difference; the standalone `TcpAddressResolver` remains an explicit resolver
and requires a host or device.
Literal bind addresses are parsed before DNS, preserving both IPv4 `0.0.0.0`
and IPv6 `::` wildcards (which .NET's DNS target lookup rejects).
Native clients require nonblank `target_host` and an explicit valid
`target_port`; `kiss_framing`/`i2p_tunneled` default
false, `connect_timeout` defaults five seconds, `max_reconnect_tries` defaults
unlimited, and `fixed_mtu` defaults absent (bitrate-driven MTU).
Existing generic configured `bitrate` remains `nominalBitrate` in both
constructors. Server `fixedMtu` is an existing native extension, not a newly
discovered upstream server setting.

There are **no TCP `remote`/`listen_on` aliases or client `port` alias** in this
pin. The rewrites `port` → both `listen_port`/`target_port`, `remote` →
`target_host`, and `listen_on` → `listen_ip` occur only inside the
Backbone-type condition in `Reticulum.py:1118–1122`; TCP dispatch at
`1137–1143` does not perform them. TCP server's own constructor separately
gives `port` precedence over `listen_port` (`TCPInterface.py:517–524`).
Broader native aliases, if retained, must be labeled native conveniences.

The upstream TCP client is less strict than the native API, not a model of
explicit host validation (`TCPInterface.py:100–103,151–165`). Both target
fields absent, host-only, null host, or `remote` plus `port` produce a
non-initiating constructor without connecting. `target_port` with a missing
`target_host` raises `KeyError`; an empty host string with a target port reaches
`initial_connect`. Explicit `target_host`/`target_port` are unaffected by
`remote`/`port`. Native early rejection avoids accepting inert or malformed
clients and is an intentional failure-behavior difference.
Native hostname connection fallback is also intentional: Python tries only
the first address, whereas a native `localhost` connection must still reach
an IPv4-only listener when DNS returns IPv6 first. Each address gets a fresh
socket of its own family; all addresses share the configured timeout and count
as one connection attempt, not additional retries. Literal IPv6 remains IPv6.

`SerialPacketInterface(string portName, int baudRate = 9600,
Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One,
long? nominalBitrate = null, bool reconnect = false)` validates configuration
without opening the port. Runtime source behavior requires `reconnect: true`.
The source accepts `N`/none (and unknown parity strings as none), `E`/even and
`O`/odd, case-insensitively; configuration parsing belongs to the runtime.
Native explicit enums reject mark/space parity and stop bits other than 1/2.
`SerialPacketInterface(Stream duplexStream, long? nominalBitrate = null)` owns
one stream and never retries.
`SerialPacketInterface(Func<CancellationToken, ValueTask<Stream>> streamFactory,
long nominalBitrate = 9600, TimeSpan? reconnectDelay = null,
TimeSpan? openDelay = null)` owns/reopens factory streams, defaults to a
five-second reconnect delay and source's 500ms post-open settling delay.
Tests override delays only for simulator execution.

Serial exposes `PortName`, `DataBits`, `Parity`, `StopBits`, `IsConnected` and
connected/disconnected events. Both reconnecting bearers report lifetime byte
counts, retry attempts, offline discarded sends and `last_error`; serial also
reports port configuration. Failed writes propagate after reporting and closing
the failed endpoint, allowing the receive worker to recover it.
Stopping a receive enumeration after a readiness packet does not close the
duplex endpoint or disable sends. EOF, read I/O failure and explicit disposal
do mark it disconnected; early consumer enumeration disposal alone does not.

Each endpoint permits one active encoded write, and the legacy TCP broadcast
API admits one broadcast before cloning bytes. Queued callers retain their own
input memory rather than allocating framed copies. Receive queues hold at most
256 frames plus one producer frame and decoded frames from a bounded 4096-byte
read buffer. Cancellation of an admitted read/write closes the owned endpoint
if needed to interrupt a driver that ignores the token; cancelling a queued
writer does not interrupt the active writer. The .NET
`Stream.WriteAsync` contract completes a whole buffer or throws; it does not
return short/zero/would-block byte counts. A dishonest custom stream that
silently discards writes cannot be detected by this API.

## Reference generation

Run `GenerateParity6StreamVectors.py` with `DOTRETICULUM_UPSTREAM` set to the
verified checkout and the isolated reference Python. `verify_reference(True)`
checks actual RNS/LXMF imports, the revision, clean tracked RNS source and LXMF
1.2.1. Generation succeeded on 2026-10-24 and records module paths in
`Fixtures/parity6-streams.json`. Source SHA-256:

* TCP: `d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d`
* Serial: `676f7b038c2950b92ac71f29653cbbe35ba6513a53ca41742d43e19966e1ef1f`
* Runtime alias dispatch (`Reticulum.py`): `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7`

Socket/port/time/thread doubles execute upstream methods without opening
devices or changing upstream source. They prove framing, serial delimiter/idle
behavior, first-address selection, the parsed-but-ignored Python connection
timeout, and finite actual retry calls (0/1/2/3). They are not live peer or
hardware evidence. Native build/test/publish evidence is coordinator-owned;
these changes do not assert an unexecuted platform gate.

Additional TCP constructor configuration observations suppress
`initial_connect` to avoid network access and bypass only the base
`Interface.__init__`, which otherwise requires an active Reticulum instance.
TCP configuration code executes unchanged. The adapter and eight observed cases
are recorded in the fixture; they do not establish unmodified full-runtime
startup behavior.
