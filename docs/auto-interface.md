# AutoInterface: bounded Parity 6 slice

## Source and acceptance contract (established before implementation)

The reference is the authoritative hosted RNS checkout at
`a29bfb51c468945c96a1ee22cae0114e993e2391` (1.5.7), specifically
`RNS/Interfaces/AutoInterface.py`. This slice does not change the reference pin.
The generator records the source SHA-256 and actual imported module paths.
The original Windows checkout's **raw-file** SHA-256 is
`c92fe1da13d7633b4bd443660041c8471fd8762fc9f3535cf92926f9f323c9df`.
The independently read pinned Git blob and canonical-LF source SHA-256 are both
`88efeadbe2e48e374910fa9b7ea747076cdf6ee6163bc95a098596096672aedf`.
These are different provenance fields, not a replacement or relabeling of the
original Windows raw-file evidence. The same clean pin can have CRLF checkout
bytes on Windows and LF checkout bytes on Linux.

| Source lines / entry points | Required observable behavior |
| --- | --- |
| 44–108, constants/address helpers | Fixed 1196-byte MTU; 10 Mbit/s estimate; IPv6 link-local discovery; remove textual/embedded scope identifiers. |
| 110–225, constructor | UTF-8 group ID (default `reticulum`); discovery/data ports 29716/42671; reverse discovery port +1; temporary/permanent flag; link/admin/site/organisation/global scope. Multicast address uses hash bytes 2–13, preceded by `ff<flag><scope>:0`. |
| 226–343, selection/socket setup | Respect devices and ignored_devices; always ignore lo0, plus Darwin/Android exclusions unless explicitly allowed (lo0 still ignored). Select the last link-local address per device. Join the group on that interface index; isolate device setup failures and diagnose no connectivity. Windows uses wildcard discovery binds. |
| 345–384, final_init/discovery_handler | Start per-device data listener; 1.92-second discovery warmup. Token is SHA-256(group bytes + canonical, unscoped source address UTF-8). A valid 32-byte prefix permits trailing bytes, as upstream does. Invalid discovery never creates peers. |
| 386–524, periodic work | Announce every 1.6 seconds; jobs every 4 seconds; peer expires strictly after 22 seconds (27.5 on Android); reverse announce strictly after 5.2 seconds; own echoes track 6.5-second carrier timeout. Replace listeners after link-local-address changes. |
| 526–649, peers | One child interface per discovered address, independently identifiable ingress; own-address discovery is an echo, not a peer. Parent itself neither sends nor receives data. Refresh on discovery and accepted data. |
| 672–747, peer I/O | One raw UDP datagram per frame, no HDLC or invented envelope. Drop unknown peers and duplicate frames across devices/peers; duplicate window is strictly less than 0.75 seconds and cache is 48 entries. Child detach removes only that peer. |
| 651–670, detach | Cancel and join owned tasks, close all owned sockets and child streams; idempotent shutdown. |

Acceptance requires independent Python vectors for constructor/address/token,
discovery prefix validation and duplicate/timeout boundaries; deterministic
managed tests for child routing, malformed/bounded input, observer failures,
peer expiry, cancellation, start failure, address replacement and disposal.
Actual IPv6 multicast tests must be separately marked, explicitly skipped with
the unavailable capability reason, and never represented as Python interop.

## Deliberate .NET boundaries

The bearer is an `ISpawningPacketInterface`. Runtime configuration, inherited
IFAC/routing/ingress policy and factory wiring belong to the coordinator; they
must be applied to children by the existing manager, not reimplemented here.
Discovery tokens separate groups but are **not authentication**: anyone who
knows the group ID can compute them.

Peer count, per-peer queued frames and per-peer active transmit operations are
bounded. A full receive queue drops the newest datagram with a diagnostic
rather than blocking discovery/expiry or allowing one stalled child to disable
other children. Frames are copied into owned bounded memory before publication.
The fixed MTU permits at most 64 additional IFAC bytes only when its flag is set.
Empty/oversized datagrams are dropped; packet parsing belongs to the manager.
These are explicit hardening differences from upstream's unbounded callbacks.
At the defaults, queued packet storage is at most 128 × 32 × 1260 =
5,160,960 bytes, plus at most one active 1260-byte transmit per peer and bounded
socket receive buffers. Duplicate tracking retains exactly at most 48 hashes.
Concurrent caller sends on the same peer fail explicitly rather than queuing
unbounded caller memory. Send failures are diagnosed and propagated to the
awaiting caller, rather than upstream's log-only failure handling.

Unknown discovery scopes fail configuration validation instead of upstream's
later missing-attribute failure; unknown multicast-address types fall back to
temporary as upstream does. Port 0 is not an AutoInterface configuration:
discovery and reverse discovery must be mutually known. No dynamic Python
loading, Android permission acquisition, radio/device certification or general
interface rescan is claimed. As upstream does, periodic address tracking covers
only interfaces successfully adopted at startup.
Discovery sources must be IPv6 link-local addresses; a valid token from a global
source is rejected rather than creating an unusable link-local peer. Interface
names and stable `.NET NetworkInterface.Id` values are accepted in device lists,
including Windows GUID identifiers. The numeric IPv6 index comes from native
interface properties rather than parsing Windows friendly names. Last-address
selection and BSD embedded-scope removal follow the reference. Echoes from an
unselected local link-local address do not create a self-peer.

Socket setup uses portable `.NET ReuseAddress`, IPv6-only datagrams, interface
membership and loopback; it does not invoke Python/netinfo or raw per-platform
`SO_REUSEPORT`. Discovery and data sends use the local bound data socket,
preserving the source address and token, rather than allocating a new temporary
announce socket. The source UDP port is not a discovery protocol field.
Darwin/Android exclusion rules and Android's extended peer timeout are present;
Android permissions and native network-information availability are not
established by those branches. Windows wildcard discovery binds and Unix
group/local-address binds mirror the source branches. No Windows, Linux,
macOS, BSD, mobile or multicast-hardware support claim follows without actual
execution on that target.

## Coordinator wiring API

```csharp
var bearer = new AutoPacketInterface(new AutoPacketInterfaceOptions
{
    GroupId = "reticulum",
    DiscoveryScope = "link",
    MulticastAddressType = "temporary",
    DiscoveryPort = 29716,
    DataPort = 42671,
    Devices = [],
    IgnoredDevices = [],
    ConfiguredBitrate = 10_000_000,
    MaximumPeers = 128,
    ReceiveQueueCapacity = 32,
    Diagnostic = error => /* route to daemon logging */ Console.Error.WriteLine(error),
});
```

The only public constructor is
`AutoPacketInterface(AutoPacketInterfaceOptions? options = null)`.
Options are copied/validated on construction. The bearer-level upstream keys map directly:
`group_id`, `discovery_scope`, `multicast_address_type`, `discovery_port`,
`data_port`, `devices`, `ignored_devices`. `ConfiguredBitrate` is the typed
constructor input corresponding to the reference AutoInterface constructor's
`configured_bitrate` field; it is **not** an additional supported native INI
key. Native INI configuration uses the generic `bitrate` setting, which the
coordinator-owned factory maps to `AutoPacketInterfaceOptions.ConfiguredBitrate`.
The upstream Reticulum configuration layer likewise supplies/overwrites the
constructor's typed bitrate field from its generic interface setting.
The last three .NET-only policy settings are bounded peer/receive limits and
the diagnostic callback. Discovery port must be 1–65534, data port 1–65535;
peer/queue capacities are 1–4096 each; bitrate must be positive.

Subscribe `ChildConnected` before `StartAsync`, or hand ownership directly to
`PacketInterfaceManager`, which does that automatically. Children arrive
started. The parent has `SendsThroughChildren = true`; it never yields packet
data or broadcasts application sends itself. Both parent and children report
`HardwareMtu = 1196` and `SupportsMtuDiscovery = true`; inherited generic IFAC,
mode, routing and scheduling policy must remain manager/router responsibilities.
The upstream AutoInterface IFAC default is **16 bytes** (exposed as
`AutoPacketInterface.DefaultIfacSize`); apply that default when a network name
or key enables IFAC and no explicit size is supplied.
`MulticastAddress`, `PeerCount`, `IsOnline`, `PeeringTimeout` and diagnostics
expose bounded state without leaking mutable peer collections.

Startup waits the reference 1.92 seconds, is cancellable, and is single-use
even after a failed attempt. Individual device setup failures are logged and
skipped; zero usable devices is a diagnosed passive interface, not a fake
connectivity claim. A failed listener is retried by periodic work without
terminating unrelated devices or changing already-learned peer identity.
Shutdown cancels and joins warmup, device tasks, periodic work and active peer
sends. Expiry completes that child's stream, allowing the manager to remove
its routes without failing the parent.
Address-listener failures retry once per four-second job rather than entering
upstream's blocking 1.25-second retry loop; this preserves service for other
devices while keeping retry work bounded.

## Reproduction and evidence

The generator calls `tests\Interop\reference_environment.py` with
`require_lxmf=True`, then verifies the actual AutoInterface module file. It
executes 45 constructor/address/token cases, five discovery-prefix cases,
six duplicate-time cases, four strict peer-job timeout/reverse boundaries and
five descope cases. The generator's runtime-only adapter supplies unconfigured
singleton fields to the reference's real default accessors; it does not edit
upstream source. It disables device enumeration and advances one maintenance
iteration with controlled clocks. This is **vector evidence**, not an
unmodified runtime or live-node claim.

Executed from the repository root:

```powershell
$env:DOTRETICULUM_UPSTREAM='C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
$env:PYTHONPATH=$env:DOTRETICULUM_UPSTREAM
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' tests\DotReticulum.Interfaces.Tests\GenerateParity6AutoVectors.py
```

Result: successful fixture generation, verified RNS 1.5.7 and LXMF 1.2.1,
pinned commit and clean tracked reference sources. The fixture records the
actual import paths and source hash. The initial generator invocation exposed
the reference Interface constructor's singleton-default dependency; the
disclosed default-accessor adapter resolved it without reference-source edits.

Run `AutoInterfaceTests` and `Parity6AutoMulticastTests` alongside the
serialized solution gates.

```powershell
dotnet test tests\DotReticulum.Interfaces.Tests\DotReticulum.Interfaces.Tests.csproj -c Release --filter "FullyQualifiedName~AutoInterfaceTests|FullyQualifiedName~Parity6AutoMulticastTests"
```

The opt-in socket test reports an explicit skip unless enabled and the
OS/device capabilities are present.
`AutoInterfaceTests` uses injected network/clock boundaries, not real sockets.
`Parity6AutoMulticastTests` is separately opt-in with
`DOTRETICULUM_AUTO_MULTICAST=1`, checks OS/device IPv6 membership/send capability,
and then actually receives the bearer’s multicast self-echo. It explicitly
skips opt-out, absent IPv6, missing link-local device and unsupported multicast
socket capabilities with reasons. Once those capabilities pass, failed echo
delivery is a failure with diagnostics (not silently skipped). This test does
not establish Python peer exchange or multi-machine/multi-interface behavior.

The complete solution and Linux bearer selection have passed. The owned Linux
namespace/veth runner additionally passed both actual Python/.NET acceptance
cases: discovery, bidirectional raw/IFAC packets, wrong-key rejection and expiry.
This is managed Linux/Python traffic, not physical LAN, mobile or published-native
Auto acceptance. See the
[Parity 6 record](../.github/development-history.md#parity-6-interface-integration-partial-2026-10-24).

## Live Python acceptance extension (criteria before implementation)

The next owned acceptance layer uses two coordinator-created Linux network
namespaces joined only by an owned veth pair, with explicit `fe80::1/64` and
`fe80::2/64` addresses. This is real OS multicast/unicast between distinct
addresses and a real pinned Python process, **not physical-network evidence**.
No existing NIC, route, serial device or hardware may be changed.

Acceptance requires both real AutoInterface discovery tables to identify the
other namespace's address and its local device; .NET-to-Python application
packet callbacks and Python-to-.NET decoded packets with independently
identifiable child ingress; successful traffic without IFAC and with 16-byte
IFAC configured through the normal runtime/manager contracts; and peer expiry
after the other side detaches while the surviving parent remains operational.
IFAC adds a wrong-key rejection observation without weakening the valid-flow
assertions. This does not claim link/resource, impaired-link, multi-hop or
hardware acceptance.

The adapter must execute the actual `AutoInterface`, `Destination`, `Packet`
and IFAC runtime APIs at the unchanged pin. It may reuse `live_link_node.emit`
for observation framing but must not replace a bearer, shorten reference
timeouts, modify peer tables or implement discovery/IFAC itself. Before opening
the reference runtime it must call `reference_environment.verify_reference`
with LXMF verification and report actual imported paths/versions/source hash.
The opt-in xUnit cases must explicitly skip unavailable topology/capabilities.
The coordinator alone executes the namespace setup and .NET commands.

### Implemented live harness and exact coordinator commands

`Parity6AutoLiveTests.cs` has two explicitly opt-in cases (raw and IFAC).
`live_auto_node.py` uses the real reference configuration/runtime, a normal
PLAIN destination callback and `RNS.Packet(..., attached_interface=peer)`.
It imports only the observation emitter from `live_link_node.py`; there are no
reference-side protocol/timer/peer-table shims. Its ready observation records
the verified commit, actual RNS/AutoInterface/LXMF paths and versions, source
hash and adopted address. PLAIN traffic is intentionally bounded to 16, 128
and 450 payload bytes, carrying framing-special octets without inventing an
Auto envelope. This is bearer/IFAC packet acceptance, not encrypted links or
resources, and does not establish upgraded-MTU application acceptance.
The source entry points are `AutoInterface.discovery_handler/add_peer/peer_jobs`
and `AutoInterfacePeer.process_incoming/process_outgoing` (audited above),
`Destination.__init__/set_packet_callback`, and `Packet.__init__/send`.
`Reticulum.py` lines 885–899 supply the IFAC size/name/passphrase mapping
(configuration bits to bytes), and lines 1031–1032 set the normal outgoing
default before children inherit it. The adapter does not implement these paths.

The native child ingress and the Python callback ingress are checked against
the opposite address and expected local device. IFAC is configured normally
at 16 bytes / 128 configuration bits. The IFAC case also sends one deliberately
wrong-key frame through the real bearer, then requires all valid exchanges
and observes that the invalid application payload never reaches the reference
callback. The raw case detaches the real Python interface and waits for
native peer expiry and manager child removal. The IFAC case shuts down the
native manager and waits for the real reference peer to expire while its
parent remains online. Reference timeouts are not shortened.

`invoke-auto-topology.ps1` creates only uniquely named `dr-auto-*` namespaces
and `dra*` veth endpoints, disables automatic address generation on those new
endpoints, enables IPv6 only on those owned endpoints in their private
namespaces, assigns the two link-local addresses with `nodad`, and raises their
links and their private loopbacks. It never alters existing NICs/routes.
It runs the already-built test project in the native namespace; that test
launches its actual Python child using `ip netns exec` in the peer namespace.
It bounds the process lifetime, cleans up only its own namespace processes,
namespaces/veth and working directory, and preserves a unique project-local
TRX report. Reference configuration/private identity files are restricted to
that owned work directory and removed even if the test host is terminated.
Cleanup failures fail the runner. No `/tmp` paths are used.
For root execution against a user-owned checkout, it grants Git's
`safe.directory` only to the canonical reference path through process-local
`GIT_CONFIG_*` environment variables; no global Git configuration is edited.
The normal exact-pin and clean-source checks still run.
The runner requires two **passed** live cases in the report: skips or an empty
filter are not acceptance evidence. Requested multicast smoke must also pass.

The coordinator first synchronizes these new files into its isolated snapshot
and runs its serialized build there. The topology runner defaults to
`--no-build --no-restore`; `-Build` explicitly permits a coordinator-owned build.
From Windows PowerShell, the following runs the provided Linux snapshot as root
without assuming sudo, opening COM4 or touching any physical interface:

```powershell
$linuxRepo = '/home/dennis/dotreticulum-parity6-92dfd28911ec44f98a0dd6ade55ba2a8'
$distro = wsl --list --quiet | ForEach-Object { ($_ -replace "`0", '').Trim() } | Where-Object { $_ -match '^Ubuntu' } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($distro)) { throw 'The configured Ubuntu WSL distribution is unavailable.' }
wsl --distribution $distro --user root -- /home/dennis/.dotreticulum-powershell/pwsh -NoProfile -File "$linuxRepo/tests/Interop/invoke-auto-topology.ps1" -Repository $linuxRepo -Dotnet /home/dennis/.dotreticulum-dotnet/dotnet -Python "$linuxRepo/.reference-venv/bin/python" -Upstream "$linuxRepo/.reference" -MulticastSmoke
```

The exact new live opt-in is **`DOTRETICULUM_AUTO_LIVE=1`**; the topology runner
supplies it and all private-topology paths/device/address variables. The
existing multicast self-echo opt-in remains exactly
**`DOTRETICULUM_AUTO_MULTICAST=1`**. They are separate evidence levels.
`-MulticastSmoke` enables that existing flag and includes its test in the same
owned topology; it does not substitute for the real Python cases.

The xUnit attribute explicitly skips missing opt-in, non-Linux hosts, absent
IPv6, missing owned topology variables/namespace/devices/executables and absent
namespace-entry capability. Configuration/provenance/traffic failures after
capability checks fail rather than masquerading as skips. Namespace creation
failures are explicit runner errors before tests, not purported passed tests.

Agent-side checks executed without opening the runtime or changing networking:
the PowerShell parser accepted the runner, and the Windows reference Python
successfully ran `tests\Interop\live_auto_node.py --help`, which performs the
pinned RNS/LXMF import verification before parsing. No live test, .NET command
or namespace operation was executed by this agent. The coordinator separately
reported that its prior WSL selection passed 82 combined Auto/KISS/Pipe cases;
that combined count is not an Auto-only count or Python interop evidence.

The coordinator's first actual private-topology run subsequently reached
Python startup but failed both cases before traffic because the original live
assertion compared the Windows raw-file hash with Linux LF bytes. The generator
was independently rerun on the verified Windows reference: all 65 cases were
regenerated, retaining the original raw hash and adding separately named
canonical-LF and pinned-Git-blob SHA-256 fields. Both new hashes equal
`88efeadbe2e48e374910fa9b7ea747076cdf6ee6163bc95a098596096672aedf`.
The live adapter now reads that exact pinned blob through binary
`git cat-file blob <pin>:RNS/Interfaces/AutoInterface.py` and requires byte
equality with the actual imported file after **only CRLF-to-LF normalization**,
before starting the runtime. It reports raw, canonical-LF and Git-blob hashes
separately. The C# test compares canonical/blob provenance with the fixture,
verifies exact actual module paths, and independently hashes the current raw
imported file against the reported raw hash. Exact-pin, clean tracked-source
and LXMF import checks remain mandatory. The corrected owned-topology rerun
passed both cases; the original raw-file evidence is retained.
