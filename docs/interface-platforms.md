# Interface platforms and local sharing

Platform support depends on the interface and the operation. A managed build or a passing simulator test does not show that a physical device, native provider or network feature works on a platform. No platform is considered supported just because the code builds for it.

## Tested platforms

| Platform | What has been exercised |
| --- | --- |
| Windows x64 | Managed build and test suites, and NativeAOT acceptance. Pipe process ownership and selected serial-device detection have software and hardware evidence. Physical radio operation is not established. |
| Linux x64 | Managed suites and NativeAOT acceptance. Backbone is Linux-gated. AutoInterface traffic with pinned Python across network namespaces is tested, which does not prove physical LAN or multicast hardware support. |
| macOS ARM64 | Hosted build, managed test suite and NativeAOT resource test pass. The rnsh Darwin PTY backend has bounded macOS execution acceptance; see [rnsh](rnsh.md). These results do not establish behaviour for every interface on macOS. |
| Android | Outside the current roadmap and unverified. No Android application target, package, permission workflow or native USB or Bluetooth provider is implemented. Shared-client policy and some platform branches exist only at source level. |
| Other operating systems and architectures | Outside the current roadmap and unverified. Check each interface rather than assuming support from .NET socket portability. |

See [TODO](../TODO.md) for the active capability list. Audio and video interfaces, and extensions absent from the pinned upstream, are outside this implementation's interface scope.

## Local shared instances

A shared instance lets one Reticulum runtime own the physical interfaces while other programs on the same machine connect to it as local clients.

```
[reticulum]
  share_instance = yes

  # Optional. TCP is the default on Windows and macOS.
  # shared_instance_type = tcp
  # shared_instance_port = 37428
  # instance_control_port = 37429
```

### Endpoints

On Linux, the local packet endpoint is an abstract AF_UNIX address, `NUL + "rns/" + instance_name`, with `default` as the instance name when none is set. Management RPC appends `/rpc`. These live in the kernel's abstract namespace; they are not files to create, chmod or unlink.

With `shared_instance_type = tcp`, packets use `127.0.0.1:37428` and RPC uses `127.0.0.1:37429` by default. Windows and macOS always use TCP. Instance names do not select TCP ports, so independent TCP instances need distinct port pairs. Upstream also selects abstract sockets for Android, and the native configuration code does the same, but this is not Android runtime support.

### Behaviour

- The native host elects a single owner before constructing any physical interfaces. If a shared instance is required and cannot be established, startup fails instead of silently running standalone.
- The owner constructs and starts the physical interfaces. Clients do not create duplicate physical interfaces.
- The local packet bearer uses HDLC framing (`0x7e` flag, `0x7d` escape, XOR `0x20`), a 262144-byte hardware MTU and a nominal 1 Gbit/s bitrate.
- Each accepted client is a separate peer, and at most 64 are admitted. A failed client does not terminate the owner or other clients.
- If the owner is lost, shared links and routing state are torn down. A recovered owner re-announces local SINGLE destinations.
- Management RPC uses its own endpoint and authentication key. A packet connection does not authenticate management operations.
- Android-specific policy keeps upstream's three-second initiator keepalive and pauses outbound traffic to an accepted client after 12 seconds without incoming activity. Android suspend, resume and battery behaviour are untested.

## Android interfaces

The pinned Python distribution includes Android-specific adapters that depend on Android APIs and Python packages such as `usb4a`, `usbserial4a`, `jnius` or the optional BLE dispatcher. They have no native .NET equivalents here. Desktop `System.IO.Ports` support does not provide USB host permissions, Android driver selection, RFCOMM or Bluetooth GATT. Android is outside the current roadmap, and everything below is unverified.

| Upstream surface | Native status |
| --- | --- |
| Android USB serial and KISS | Not implemented. Desktop serial and KISS simulation is not evidence of USB permission or driver support. |
| RNode Bluetooth and BLE | Not implemented on Android. The desktop RNode interface explicitly rejects Android, including supplied-stream construction. |
| RNode Multi | Not applicable. The pinned upstream explicitly rejects Android for Multi, so this is an upstream limitation rather than a missing native port. |
| Weave Android USB branch | Not implemented or verified. |
| AutoInterface Android policy | Some source-compatible device exclusions and the longer peer timeout exist. Android network permissions and multicast lock integration are unverified. |
| Backbone | The source-compatible platform guard permits Linux and Android, but Android socket-option execution and service behaviour are unverified. |
| Daemon or service | Not implemented. `rnsd --service` is not an Android foreground service. |

### Upstream Android adapter details

These describe the pinned Python adapters, not native .NET providers:

| Upstream adapter | Behaviour |
| --- | --- |
| Serial and KISS | Require `usb4a`, `usbserial4a` and `jnius`, and discover USB devices through Android USB APIs. Both use 9600 8N1, a 564-byte MTU, a 100 ms partial-data timeout and a five-second reconnect. USB VID:PID `1a86:55d4` is forced to CDC ACM. FTDI reads use 16 KiB/100 ms, CP210x 64 bytes/12 ms, the override 64 bytes/12 ms USB timeout and 100 ms serial timeout, and other devices 1 KiB/100 ms. Android KISS uses `beacon_interval` and `beacon_data`, unlike the desktop `id_interval` and `id_callsign`. |
| RNode | The constructor requires the Android USB dependencies even for other transports. Selectors are `allow_bluetooth`, `target_device_name`, `target_device_address`, `ble_name`, `ble_addr`, `force_ble`, `tcp_host` and `force_tcp`. BLE selection comes before TCP, and configured USB before classic Bluetooth. Classic Bluetooth uses bonded devices and the RFCOMM SPP UUID `00001101-0000-1000-8000-00805f9b34fb`. |
| RNode BLE | Uses the optional `able` dispatcher and the Nordic UART service `6e400001-b5a3-f393-e0a9-e50e24dcca9e`, with write characteristic `6e400002-b5a3-f393-e0a9-e50e24dcca9e` and notification characteristic `6e400003-b5a3-f393-e0a9-e50e24dcca9e`. It requests MTU 512 and writes chunks of `min(negotiated_mtu - 5, 512)` (20 initially), with a seven-second connection timeout and 2.5-second reconnect delay. It selects bonded devices; it is not a scanning or pairing UI. |
| RNode TCP | The device transport uses port 7633 and carries RNode commands and data, not Reticulum TCP packets. The Android radio validator caps bandwidth at 500 kHz. Native desktop validation allows up to 1.625 MHz. |
| Weave | Contains a USB branch using Android USB serial facilities and permissions. The native Weave serial implementation does not provide it. |

On Android 12 and later, connecting to a bonded device needs the applicable `BLUETOOTH_CONNECT` permission. An Android host would also have to handle USB grants, device attach and detach, Bluetooth adapter denial and revocation, network, IPv6 and multicast permissions, background execution, foreground-service lifetime and power and sleep policy. No generic COM or TTY enumeration substitutes for these APIs. The existing code does not request permissions, scan for or pair devices, or provide an Android application manifest.

## Versioning

The project version is the targeted RNS release plus the git height (`1.5.7.<height>`). Tool `--version` output names the reference release (`reference RNS 1.5.7`). See [Versioning](../README.md#versioning).

## Verification boundaries

- Shared-instance tests run separate native and Python processes over TCP, covering owner and client roles and authenticated management. They do not prove Linux abstract-socket interoperability or Android behaviour.
- Native serial simulators, opt-in RNode detection and AutoInterface namespace tests are separate from physical USB or RF acceptance. Physical-hardware testing is outside the current roadmap and remains unverified.
- Windows x64 and Linux x64 are the most thoroughly exercised environments. macOS ARM64 evidence is limited to the hosted checks listed above.
