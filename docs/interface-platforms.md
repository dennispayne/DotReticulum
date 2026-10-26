# Interface platforms and local sharing

Platform support is specific to each bearer and operation. A managed build or a passing simulator does not establish access to a physical device, native provider, or network feature.

## Tested platform scope

| Platform | Evidence and limits |
| --- | --- |
| Windows x64 | Managed build/test suites and Windows NativeAOT acceptance are exercised. Pipe process ownership and selected serial-device detection have software/hardware evidence; physical radio operation is not established. |
| Linux x64 | Managed suites and Linux NativeAOT acceptance are exercised. Backbone is Linux-gated. AutoInterface namespace/veth traffic with pinned Python is tested; it does not prove physical LAN or multicast hardware support. |
| macOS ARM64 | Hosted build, managed test suite, and NativeAOT resource test pass. The rnsh Darwin PTY backend is unavailable. These results do not establish native bearer behavior for every macOS interface. |
| Android | No Android application target, package, permission workflow, or native USB/Bluetooth provider is implemented. Shared-client policy and some platform branches are source-level implementation only. |
| Other operating systems and architectures | Not generally implemented or verified. Check each bearer rather than inferring support from .NET socket portability. |

See [TODO](../TODO.md) for the active capability list. Audio/video interfaces and extensions absent from pinned upstream are outside this implementation's interface scope.

## Local shared instances

Local sharing lets one Reticulum runtime own physical interfaces while other local runtimes connect as packet clients. The native host elects a single owner before constructing physical interfaces; required-shared startup fails instead of silently becoming a standalone node. Management RPC uses a separate endpoint and authentication key. Sharing a packet socket alone does not authenticate management operations.

The native packet bearer uses HDLC (`0x7e` flag, `0x7d` escape, XOR `0x20`), a 262144-byte hardware MTU, and a nominal 1 Gbit/s estimate. Linux uses an abstract AF_UNIX endpoint by default; TCP is available by explicit configuration and is the default on Windows and macOS. Abstract local sockets are also selected for Android in configuration code, but this is not Android runtime support. Each accepted client is a distinct peer; admission is capped at 64. An accepted child failure does not terminate the owner or sibling clients.

The owner constructs and starts physical interfaces. Shared clients do not create duplicate physical interfaces. Owner loss tears down shared links and routing state; a recovered owner reannounces local SINGLE destinations. Android-specific policy preserves the upstream three-second initiator keepalive and pauses accepted-client outbound traffic after 12 seconds without incoming activity, but Android suspend/resume and battery behavior have not been tested.

## Android interface boundary

The pinned Python distribution has Android-specific adapters that depend on Android APIs and Python packages such as `usb4a`, `usbserial4a`, `jnius`, or the optional BLE dispatcher. Those adapters are not native .NET providers. Desktop `System.IO.Ports` support does not supply USB host permissions, Android driver selection, RFCOMM, or Bluetooth GATT.

| Upstream surface | Native status |
| --- | --- |
| Android USB serial and KISS | Not implemented as an Android USB adapter; desktop serial/KISS simulation is not evidence of USB permission or driver support. |
| RNode Bluetooth/BLE | Not implemented on Android. Desktop RNode explicitly rejects Android, including supplied-stream construction. |
| RNode Multi on Android | Not applicable: pinned upstream explicitly rejects Android for Multi. |
| Weave Android USB branch | Not implemented or verified as a native Android provider. |
| AutoInterface Android policy | Some source-compatible device exclusions and peer timeout behavior exist; Android network permissions and multicast lock/provider integration are unverified. |
| Backbone Android | The source-compatible platform guard permits Linux/Android, but Android socket-option execution and service behavior are unverified. |
| Android daemon/service | Not implemented. `rnsd --service` is not an Android foreground service. |

### Upstream Android adapter details

These behaviors describe pinned Python adapters, not native .NET providers:

| Upstream adapter | Platform-specific contract |
| --- | --- |
| Android Serial and KISS | Requires `usb4a`, `usbserial4a`, and `jnius`; discovers USB devices through Android USB APIs. Both use 9600/8N1, 564-byte MTU, 100 ms partial-data timeout, and five-second reconnect. USB VID:PID `1a86:55d4` is forced to CDC ACM. FTDI reads use 16 KiB/100 ms; CP210x uses 64 bytes/12 ms; the override uses 64 bytes/12 ms USB timeout and 100 ms serial timeout; other devices use 1 KiB/100 ms. Android KISS uses `beacon_interval`/`beacon_data`, unlike the native desktop `id_interval`/`id_callsign` configuration. |
| Android RNode | Constructor requires the Android USB dependencies even for alternate transports. Selectors include `allow_bluetooth`, `target_device_name`, `target_device_address`, `ble_name`, `ble_addr`, `force_ble`, `tcp_host`, and `force_tcp`. BLE selection precedes TCP; configured USB precedes classic Bluetooth. Classic Bluetooth uses bonded devices and RFCOMM SPP UUID `00001101-0000-1000-8000-00805f9b34fb`. |
| Android RNode BLE | Uses optional `able` dispatch, the Nordic UART service `6e400001-b5a3-f393-e0a9-e50e24dcca9e`, write characteristic `6e400002-b5a3-f393-e0a9-e50e24dcca9e`, and notification characteristic `6e400003-b5a3-f393-e0a9-e50e24dcca9e`. It requests MTU 512, then writes chunks of `min(negotiated_mtu - 5, 512)` (initial chunk 20), with a seven-second connection timeout and 2.5-second reconnect delay. The adapter selects bonded devices; it is not a scanning/pairing UI. |
| Android RNode TCP | The device transport uses port 7633 and carries RNode commands/data, not Reticulum TCP packets. The pinned Android radio validator caps bandwidth at 500 kHz; native desktop validation allows up to 1.625 MHz. |
| Android Weave | The Python source contains a USB API branch using Android USB serial facilities and permissions. The native Weave serial implementation does not supply that adapter. |

Android 12+ bonded-device connection needs the applicable `BLUETOOTH_CONNECT` permission. A host must separately address USB grants, device attach/detach, Bluetooth adapter denial/revocation, network/IPv6/multicast permissions, background execution, foreground-service lifetime, and power/sleep policy. No generic COM/TTY enumeration substitutes for these Android APIs. The pinned Android Multi adapter explicitly rejects Android; this is upstream unsupported rather than an absent native port of a supported upstream feature.
These capabilities require an Android host; the existing code does not request permissions, scan or pair devices, or provide an Android application manifest.


On Linux and Android, the upstream local packet endpoint is an abstract AF_UNIX address `NUL + "rns/" + instance_name` (`default` when omitted); management RPC appends `/rpc`. These are kernel namespace endpoints, not files to create, chmod, or unlink. Explicit `shared_instance_type = tcp` uses `127.0.0.1:37428` for packets and `127.0.0.1:37429` for RPC by default. Windows and macOS use TCP. Instance names do not select separate TCP ports, so independent TCP instances need distinct port pairs. The native host selects abstract Unix sockets on Linux and Android configuration, TCP elsewhere, and TCP when explicitly requested. Management RPC uses its own key and endpoint; a packet connection is not management authentication.

The project version is the targeted RNS release plus git height (`1.5.7.<height>`). Tool `--version` output identifies the reference release (`reference RNS 1.5.7`); see [Versioning](../README.md#versioning).
## Verification boundaries

Shared-instance tests exercise separate native/Python processes over TCP, including owner/client roles and authenticated management. They do not prove Linux abstract-socket interoperability or Android behavior. Native serial simulators, opt-in RNode detection, and AutoInterface namespace tests are separate from physical USB/RF acceptance. Windows x64 and Linux x64 are the primary exercised environments; macOS ARM64's test/AOT evidence is limited to the stated hosted checks. No platform is considered supported solely because it builds.
