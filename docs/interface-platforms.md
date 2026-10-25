# Local sharing and Android interface boundaries

This is a source audit, not a mobile or physical-device acceptance report.
DotReticulum is experimental, AI-assisted and independent of the Reticulum
project. Integrated native desktop bearers do **not** establish Android support.
See [runtime operations](runtime-operations.md), [rnsd](rnsd.md), and
[development history](../.github/development-history.md) for execution evidence.
The coordinator's current Parity 6 verification record belongs in that history;
this document does not anticipate its results or duplicate the roadmap.

## Reference provenance and source map

Audited the authoritative, previously fetched working checkout
`upstream-reticulum-rns` at
`a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7).
Paths below are relative to that checkout, line ranges are inclusive, and
SHA-256 values fingerprint the actual raw source bytes read during this audit.
No fixture was generated, reference pin changed, or upstream code executed.

| Pinned source | Relevant ranges and observable contract |
| --- | --- |
| `RNS\vendor\platformutils.py` | 31–61: Android environment detection; Linux/Android select epoll and abstract Unix sockets, Windows/macOS do not. |
| `RNS\Reticulum.py` | 31–47: Android adapter imports; 297–305, 355–368: defaults/RPC endpoint; 399–451: host election, client fallback; 475–507: sharing configuration; 736–766: socket selection and physical-interface ownership; 1154–1177: adapter construction. |
| `RNS\Interfaces\LocalInterface.py` | 44–51: platform-specific TCP bind exclusivity/reuse; 53–151: client endpoint, MTU, Android sleep policy; 153–202: reconnect/keepalive; 204–301: framed traffic and EOF; 303–349: detach/teardown; 356–460: listener and independent spawned clients; 462–494: parent accounting/handler. |
| `RNS\Transport.py` | 1149–1158: PHY keepalive job; 3690–3710: owner loss tears down links/clears routing state, recovery reannounces SINGLE destinations as path responses. |
| `RNS\Interfaces\Android\__init__.py` | 31–39: module export discovery, not an Android application/service/permission implementation. |
| `RNS\Interfaces\Android\SerialInterface.py` | 63–132: platform/dependency gates and configuration; 134–203: detach, USB driver selection and startup; 205–289: HDLC, read timeout and reconnect. |
| `RNS\Interfaces\Android\KISSInterface.py` | 73–163: gates/configuration; 165–242: USB opening and setup; 244–302: TNC controls; 304–448: data, READY, queue, beacon, timeout and reconnect. |
| `RNS\Interfaces\Android\RNodeInterface.py` | 40–47: optional BLE dependency; 119–249: classic Bluetooth; 281–355: Bluetooth device-control writes; 357–552: settings/dependency gates and transport selection; 623–750: USB/Bluetooth/BLE/TCP opening and detection; 1526–1581: reconnect/detach; 1628–1873: BLE UART/GATT; 1875–2007: TCP device transport. |
| `RNS\Interfaces\RNodeMultiInterface.py` | 149–150 and 952–953: explicitly rejects Android; importing the desktop module on Android does not supply an Android Multi adapter. |
| `RNS\Interfaces\WeaveInterface.py` | 71–100, 162–224: Android USB API/driver branch exists inside this shared source file, rather than in the Android adapter directory. |
| `RNS\Interfaces\AutoInterface.py` | 60–68, 149–152, 222–223: Android system-interface exclusions and 1.25× peering timeout. |
| `RNS\Utilities\rnsd.py` | 40–59, 68: `--service` selects file logging; it does not create an Android foreground service. |

| Pinned source | Raw SHA-256 |
| --- | --- |
| `RNS\vendor\platformutils.py` | `ddbcfeb8b5c561519193e50f5ba95df4dde40455bee34e34faee570221c5d5af` |
| `RNS\Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `RNS\Interfaces\LocalInterface.py` | `b241095c0a528f0c9a88817429a9ce54c7adb5aa850ae08e29c9b84dd8fa4371` |
| `RNS\Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `RNS\Interfaces\Android\__init__.py` | `33b59567c5abd1876b537c95ac092fd5f7471eeff624320b2a4073ad670e8d7b` |
| `RNS\Interfaces\Android\SerialInterface.py` | `53e99c4acdd5995bd0a940fc444326b79371d8669cfa6f42973c8985d6e59150` |
| `RNS\Interfaces\Android\KISSInterface.py` | `33c9f14256b8fa90b05581715678bbb3d77d4a49c6875f0cbc383e5c0ee46376` |
| `RNS\Interfaces\Android\RNodeInterface.py` | `6c6bfc1f42a76b46c38b57c3b1af70dc977696cfa8aef14ea3c2b36cc1d0b52c` |
| `RNS\Interfaces\RNodeMultiInterface.py` | `f99f562355bc19e1ae7896f0a3039cb57fd377fcfc8ec9f8b4d7e913bf1350d9` |
| `RNS\Interfaces\WeaveInterface.py` | `99b4f217fce0afdfef16ea9641fc740065358eec512e22ec92366558051195c5` |
| `RNS\Interfaces\AutoInterface.py` | `c92fe1da13d7633b4bd443660041c8471fd8762fc9f3535cf92926f9f323c9df` |
| `RNS\Utilities\rnsd.py` | `e8920d1acde17e09b0aad57651348e86aabc84fbb29a531c340b7365d220e748` |

## LocalInterface contract and native mapping

Upstream defaults to sharing. Linux **and Android** use the abstract AF_UNIX
address `NUL + "rns/" + instance_name`, default name `default`; RPC appends
`/rpc`. These are kernel namespace addresses, not socket files to create,
chmod or unlink. Explicit `shared_instance_type = tcp` selects
`127.0.0.1:37428` for packets and `127.0.0.1:37429` for RPC by default.
Other upstream platforms force TCP. Instance names select Unix namespaces,
not separate TCP ports; independent TCP instances need distinct port pairs.
Abstract/local addresses alone do not authenticate the packet bearer; RPC
authentication is a separate contract.

The first successful listener owns physical interfaces; subsequent instances
connect as clients and do not construct their configured physical interfaces.
When both bind and connect fail, ordinary upstream startup becomes standalone.
The native required-shared API explicitly fails instead of becoming a host or
silently continuing standalone. Native election reserves the bearer before
constructing the router and physical factories
(`src\DotReticulum.Applications\Runtime\SharedInstanceSession.cs`;
`src\DotReticulum.Applications\Rnsd\RnsdHost.cs:125–143`).
Management RPC uses a separate endpoint/key; cross-directory clients need a
matching explicit key, not merely the same packet port.

`src\DotReticulum.Interfaces\LocalSharedPacketInterface.cs` supplies the native
initiator and spawning server. Each accepted socket has its own peer identity,
lifetime and manager registration, so ingress exclusion is per peer rather
than per listener. The packet bearer uses HDLC (`7e`, `7d`, XOR `20`), hardware
MTU 262144 and nominal 1 Gbit/s. An initiator reconnects, default eight-second
delay; an accepted child never reconnects to its former client. A child EOF
must retire that child without terminating the owner or siblings. No-subscriber
acceptance disposes an unadopted native child.
`src\DotReticulum.Transport\TransportRouter.Shared.cs` handles shared-client
loss/recovery and local hop/fanout rules, including local routing when transport
forwarding is disabled.

### Intentional differences and remaining observable gaps

Native async sockets, explicit start/disposal, cancellation, bounded manager
queues and a default **64-client** admission cap replace Python's epoll/thread
machinery and uncapped accepted-client count. Required-shared election does not
temporarily start an unwanted owner. Native packet validation and malformed
escape rejection are hardening, not a claim of identical acceptance of malformed
upstream frames. Connection timeout is bounded, default ten seconds.
Recovery notification is native lifecycle-driven rather than reproducing
Python's extra `RECONNECT_WAIT + 2` sleep before reannouncement.

Upstream enables physical empty-HDLC keepalives on Android initiators and
pauses an Android owner's accepted client's outbound traffic after **12 seconds**
without incoming activity; any received socket bytes refresh the timer
(`LocalInterface.py:98–100,146,188–202,213–217,274`).
`LocalSharedPlatformPolicy` now carries the explicit three-second initiator
keepalive and 12-second accepted-client pause defaults. It enables the pause
only when `OperatingSystem.IsAndroid()` is true; desktop's existing three-second
keepalive behavior is preserved. The accepted-client pause uses monotonic
elapsed time, drops outbound traffic after the timeout, and refreshes on socket
activity (including an empty keepalive). These are source-backed software
contracts; no Android runtime has been exercised. Background suspension,
battery use, sleeping-client queue pressure and resume behavior remain
**missing/unverified**.

Local `rxb` now adds the decoded lengths of accepted frames, matching upstream
`process_incoming`; framing bytes and empty/malformed frames do not count.
Outbound `txb` continues to count encoded packet frame bytes, while separate
empty keepalives do not count. This closes the source-backed counter difference,
but live statistics comparison remains **unverified**.

`RnsdConfig.cs` and `SharedInstanceOptions` now select abstract Unix endpoints
on both Linux and Android, with TCP as the explicit override and on other
platforms. `LocalSharedPlatformPolicy.SupportsAbstractUnixSockets` centralizes
that platform decision. Android endpoint selection is implemented in software
but remains **unverified on Android**; it is not evidence that .NET Android
socket support, packaging or an Android host works.

## Android adapter and host responsibilities

### USB serial and KISS

The pinned Android Serial/KISS adapters require Android, `usbserial4a` and
`jnius`; device lookup uses `usb4a.usb.get_usb_device(port)`, not desktop COM/TTY
enumeration. Missing native access is a fatal dependency gate, including the
documented Termux-like environment limitation. They are not generic pyserial
adapters with a platform label.

Both default to 9600/8N1, no XON/XOFF/RTS/CTS/DSR/DTR flow control, MTU 564,
100 ms partial-data timeout, and five-second reconnect attempts. USB driver
selection forces CDC ACM for VID:PID `1a86:55d4`. FTDI uses 16 KiB/100 ms reads;
CP210x uses 64 bytes/12 ms; the override uses 64 bytes/12 ms USB timeout with
0.1 s serial timeout; others use 1 KiB/100 ms. These values are bridge/driver
behavior, not a promise that desktop `SerialPort` can configure Android USB.

KISS also configures preamble/tail/persistence/slot/READY controls, queues under
flow control, unlocks after five seconds, and pads beacon data to 15 bytes.
Its setting names include `beacon_interval`/`beacon_data`; current native KISS
configuration uses `id_interval`/`id_callsign`. Existing desktop protocol controls
and stream tests are described in [KISS interfaces](kiss-interfaces.md) and
[stream interfaces](stream-interfaces.md); they do not establish Android USB
driver, permission, attach/detach or power-management acceptance.

### RNode Bluetooth, BLE and TCP

The Android RNode constructor still gates on `usbserial4a`/`jnius`, even when a
non-USB transport is selected. Config includes `allow_bluetooth` (default false),
`target_device_name`, `target_device_address`, `ble_name`, `ble_addr`,
`force_ble`, `tcp_host` and `force_tcp`. BLE selection takes precedence over TCP
in `open_port`; otherwise a configured USB port precedes classic Bluetooth.
These Android selectors are not accepted by the current native desktop RNode
configuration allowlist (`RnsdConfig.cs:290–296`).

Classic Bluetooth queries **bonded** devices using the Android adapter, matches
address/name or the default `rnode ` name prefix case-insensitively, and opens
RFCOMM SPP UUID `00001101-0000-1000-8000-00805f9b34fb`.
This is neither BLE nor proof of a physical radio link.

BLE additionally uses the optional `able` dispatcher. It selects bonded LE/dual
devices (address, then name, then `RNode ` prefix), uses Nordic UART service
`6e400001-b5a3-f393-e0a9-e50e24dcca9e`, writes characteristic
`6e400002-b5a3-f393-e0a9-e50e24dcca9e`, and subscribes to notifications on
`6e400003-b5a3-f393-e0a9-e50e24dcca9e`. It requests MTU 512, then uses
`min(negotiated_mtu - 5, 512)` write chunks; initial chunk size is 20. Connection
timeout is seven seconds, reconnect delay 2.5 seconds; missing services,
notification/MTU failure and disconnect must not be reported as successful
connectivity. MTU update timeout records a hardware error/reset requirement.
The audited path selects paired devices; it is not evidence of active BLE
scanning/pairing UI.

The Android TCP device adapter targets port **7633**, with five-second initial
connection and detection deadlines; it is raw RNode control/data over TCP, not
a TCP Reticulum packet interface. Native desktop `RNodePacketInterface` accepts
`tcp://host` through `RNodeTcpConnection` and bounded owned streams, but
`RNodeDevice.cs:93–94` explicitly **rejects Android**, for supplied streams too.
No current stream constructor bypasses that gate.
The pinned Android radio validator caps bandwidth at 500000, whereas the
current desktop native validator permits 1625000: sharing a protocol codec
would not by itself establish Android behavioral parity.

[RNode interfaces](rnode-interfaces.md) records desktop controls and simulator
boundaries. The pinned desktop RNodeMulti explicitly rejects Android and there
is no Android-specific Multi module in this checkout. Classify that specific
Multi adapter as **upstream unsupported**, not as an implemented Android feature
or an obligation to invent a reference wire protocol.
Weave's separate in-file Android USB branch remains **missing/unverified** in
the native desktop implementation; see [Weave](weave-interface.md).

### Permissions, service lifetime and native alternatives

The audited adapters use platform APIs/dependencies; they do not provide an
Android manifest, permission-request UI or application/service lifecycle.
Upstream `rnsd --service` means file logging, **not** Android service installation.
The following are requirements for a future native Android host, not claims
that the pinned Python files implement permission prompts:

| Host/platform surface | Required boundary and possible native implementation |
| --- | --- |
| USB host | Declare appropriate USB-host capabilities; enumerate via Android `UsbManager`, obtain per-device user permission, and own/release interfaces, endpoints and transfers. Use a reviewed native/.NET Android USB serial driver or explicit bridge stream. `System.IO.Ports.SerialPort` on Windows/Linux is not this provider. |
| Bluetooth | Handle adapter disabled/unavailable, bonding and permission denial/revocation explicitly. Android 12+ bonded-device access/connection requires the applicable `BLUETOOTH_CONNECT` grant. Scan/location grants depend on OS/target SDK and any actual scanning design; do not require or claim scanning just because this source queries bonds. Native alternatives are Android RFCOMM sockets and `BluetoothGatt` callbacks with cancellation and bounded queues, not desktop COM emulation. |
| Network transports | Package Android network permissions (including `INTERNET`) and validate routing/interface changes, IPv6/multicast and hotspot/VPN behavior on device. Auto's existing Android name exclusions and 27.5 s timeout in native code reproduce source policy only. Wi-Fi multicast-lock/permission management, if needed by the host, and low-power behavior need actual device evidence. Backbone's Android-permitting guard and Linux socket options are not Android validation. |
| Background operation | Supply an actual Android host/service with OS/target-SDK-appropriate foreground-service/notification permissions and lifecycle, bounded shutdown, process-death recovery and explicit power/wake policy. Foreground-service and sleep/resume behavior cannot be certified by a console daemon or desktop NativeAOT publish. |
| Native integration | A future Android-targeted application can statically register an `IPacketInterface` or provide owned streams where supported; reuse framing/routing rather than Python embedding or arbitrary runtime plug-ins. This is an architecture option, not an existing Android package. RNode requires a deliberate platform adapter/guard design before such reuse is possible there. |

The current projects target `net10.0`, with no Android application target found
in this audit. Missing packaging, permission handling, USB/RFCOMM/GATT providers
and Android device/service behavior remain **missing or unverified**. The
shared-client timing policy is implemented, but its Android runtime integration
is not validated. Generic socket, stream and codec reuse opportunities are
**not applicable evidence** of device support.

## Evidence boundaries and measurable acceptance

The focused implementation adds deterministic-clock unit coverage for the
pause boundary, keepalive accounting, decoded-frame counters and endpoint
policy. This subtask did not run builds/tests, installation, network commands
or device access; the coordinator owns validation and must report its actual
results separately.

Previously recorded Parity 3 evidence in
[development history](../.github/development-history.md#parity-3-runtime-configuration-and-operations-2026-10-21)
reports Windows shared-instance live/process flows. Its test sources are:

- `tests\DotReticulum.Applications.Tests\SharedLiveTests.cs`: actual Python
  owner/native client and native owner/required Python client escaped-packet
  exchange, plus management authentication. The adapter
  `tests\Interop\SharedReference.py` explicitly selects **TCP**.
- `tests\DotReticulum.Applications.Tests\SharedRuntimeTests.cs`: HDLC, election,
  required-shared/fallback, reconnect and bounded RPC failure cases.
- `tests\DotReticulum.Applications.Tests\SharedHostIntegrationTests.cs`: two
  shared clients plus external peer, owner-only factories, disabled-transport
  local forwarding, persistence, link settlement and restart/reannouncement.
- `tests\Pester\SharedRuntime.Tests.ps1`: separate daemon/client processes,
  owner-only physical listener, Python RPC/bad-key survival and owner restart.

These files were read, not rerun. Their TCP fixtures do not prove Linux abstract
Unix interoperability, Android service behavior, physical USB/Bluetooth, RF,
or every LocalInterface-specific sibling-failure path.

The following are acceptance criteria, not a second progress checklist:

| Surface/status at audit | Measurable acceptance and minimum access |
| --- | --- |
| Local TCP — implemented, prior Windows evidence | Reproduce both owner/client roles with the pinned Python process, byte-identical escaped packets, independent peers, one physical owner, wrong-key/malformed-peer survival and restart/reannouncement. Requires loopback/process access only; no radio or Internet. Current-run results belong in the coordinator's history record. |
| Local abstract Unix — implemented Linux path, platform evidence not established by this audit | On Linux, verify default and named `NUL+rns/...` packet/RPC endpoints with actual Python in both roles, name isolation, competing ownership, owner restart, and no filesystem socket artifacts. Confirm TCP override separately. Requires a Linux host/container permitting AF_UNIX in the same relevant namespace, not physical hardware. |
| Local peer/lifecycle/statistics — software contract implemented; runtime acceptance partial | Drop/fault one admitted child while a sibling continues traffic and new clients connect; exercise admission cap, blocked sends, no subscriber, cancellation and disposal. Compare decoded/framed counters and keepalives against Python, including empty keepalive behavior. Requires owned local processes/sockets; latest focused tests await coordinator validation. |
| Android sharing/service — timing and endpoint policy implemented; device/runtime missing or unverified | On a named Android version/target SDK, verify endpoint operation, pause after 12 seconds, wake/reconnect, path-response reannouncements, service termination/restart and permission revocation while unrelated peers survive. Requires an installable Android host plus authorized emulator/device; desktop Linux success alone is insufficient. |
| Android USB Serial/KISS/Weave — missing provider, hardware-dependent acceptance | On an owned Android USB-host device and identified adapter/TNC/Weave, record driver VID/PID and line/control settings; test grant/deny/revoke, unplug/replug, short writes, partial frames, reconnect and cleanup. Compare pinned configuration/control bytes, and validate real bidirectional traffic. An emulator or simulated stream covers protocol software only. |
| Android RNode RFCOMM/BLE — missing provider, hardware-dependent acceptance | With authorized bonded RNode/firmware, validate target matching, adapter disabled/permission denied, SPP or exact GATT UUIDs, notification/MTU failure, fragmentation, reconnect and clean shutdown. Test control readback and both traffic directions; record negotiated MTU and actual transport. Requires Bluetooth-capable Android hardware and identified device. |
| Android/native network transports — unverified on Android | Test TCP/UDP, Auto multicast/IPv6 and any configured Backbone/I2P path on an actual Android host, including network transitions and background restrictions. I2P additionally needs a controlled SAM router; desktop/simulated SAM success does not verify Android deployment. No RF is needed for ordinary IP tests. |
| Physical RF — hardware-dependent, not accepted | Identify each radio, firmware, owned port and peer; obtain explicit permission for configuration/transmission and lawful frequency/bandwidth/power/SF/CR before opening. Record actual bidirectional RF, impairment/recovery and teardown, not only device-control replies. Simulation is never hardware acceptance. |

The observed Windows **COM4 / VID_239A PID_8029 remains unidentified and unopened**.
That enumeration grants no permission to open/configure it, alter firmware,
pair a device or transmit RF. No such action is authorized by this audit.
Minimum external access is an identified, explicitly authorized device/peer and
platform-specific host as listed above; no additional desktop software test can
substitute for those physical/mobile acceptance gates.
