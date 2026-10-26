# rnsd: transport daemon

`rnsd` runs a Reticulum transport node. It opens configured interfaces, learns paths from announces, rebroadcasts eligible announces with transport-header rewriting, answers path requests and forwards DATA packets and their proofs between interfaces.

    dotnet run --project src/DotReticulum.Applications -- rnsd --config <dir>
    rnsd --exampleconfig > <dir>/config

The config directory contains `config`; the transport identity is stored in
`<dir>/storage/transport_identity` and created when absent. With no `--config`, search for an existing `config` in
`/etc/reticulum`, then `~/.config/reticulum`, otherwise use `~/.reticulum`.
On Windows the system-directory candidate is `<home drive>\etc\reticulum`.
An explicit nonexistent `--config` path is a **directory**, not a filename.
An existing config file is also accepted as a .NET extension.
Stop with Ctrl+C or SIGTERM (graceful shutdown).

## Config format

A supported subset of Reticulum's ConfigObj format: `[section]`, `[[subsection]]`, `key = value`,
`#` comments outside quotes, single/double quoted values, and comma-delimited lists (including
quoted items containing commas and trailing list commas). Duplicate settings/interfaces,
unclosed quotes, unknown sections, and ineffective controls are errors. RNodeMulti
radio settings use `[[[Radio name]]]` inside the parent interface. Other deeper
nesting and multiline/triple-quoted values are unsupported and diagnosed.

Missing directories/configs are created without replacing existing files. Upstream's default config
enables `AutoInterface`; the native default conservatively keeps multicast discovery
disabled until explicitly enabled and logs that decision. Auto is implemented,
but no existing configuration is rewritten and no TCP listener is substituted.
Enable an interface before expecting external connectivity.

| Section | Key | Meaning |
|---|---|---|
| `[reticulum]` | `enable_transport` | `yes`/`no` (default `no`). When `yes`, forward and rebroadcast. |
| `[reticulum]` | `static_transport_identity` | Default `no`; discoverable interfaces imply `yes`. The persisted internal identity and active transport identity have distinct ownership/lifetimes. |
| `[reticulum]` | `share_instance` | Default `yes`; elect a local shared host or connect as a local client. |
| `[reticulum]` | `instance_name`, `shared_instance_type` | Instance name `default`; `tcp` or `unix`. Linux uses abstract Unix sockets by default; other platforms use loopback TCP. No Python modules are loaded. |
| `[reticulum]` | `shared_instance_port`, `instance_control_port` | Default 37428 / 37429; valid ports 1–65535. |
| `[reticulum]` | `rpc_key` | Hexadecimal shared management key. Invalid hex is rejected rather than silently falling back to an unaudited key. |
| `[reticulum]` | `network_identity` | Optional persisted identity path for network discovery signing. |
| `[reticulum]` | `enable_remote_management`, `enable_interface_management` | Defaults `no` / `yes`; management request authorization remains enforced. |
| `[reticulum]` | `remote_management_allowed` | Comma-delimited 16-byte identity hashes in hex; duplicates removed. |
| `[reticulum]` | `discover_interfaces`, `interface_discovery_sources` | Default `no`; optional allowed source-identity hashes. |
| `[reticulum]` | `required_discovery_value` | Default 16; nonpositive values select the upstream default. Native maximum 256. |
| `[reticulum]` | `autoconnect_discovered_interfaces`, `autoconnect_unverified_implementations` | Default 0 / `no`; bounded maximum 1024 (nonpositive disables); not permission to treat TCP advertisements as Backbone endpoints. |
| `[reticulum]` | `autoconnect_interface_mode`, `autoconnect_interface_gravity`, `autoconnect_announces_to_internal` | Optional discovered-interface overrides; default mode policy, gravity 0, no internal-announce override. |
| `[reticulum]` | `publish_blackhole`, `blackhole_sources`, `blackhole_update_interval` | Default `no`, no sources, 60 minutes; interval is clamped to at least 2 minutes. Source hashes are deduplicated. |
| `[logging]` | `loglevel`, `logtimestamps` | Level 0–8, timestamps default `yes`. Created config uses 4; absent logging settings use upstream's initial NOTICE level 3. |
| `[interfaces]` | `[[Name]]` | One subsection per interface. |

Per interface: `type` (required when enabled), `enabled` / `interface_enabled` (absent means **disabled**).
These flags are **ORed**: either `yes` enables the interface, even if the other says `no`.
`interface_mode` takes precedence over `mode` (`full`, `pointtopoint`/`ptp`, `access_point`/`accesspoint`/`ap`, `roaming`,
`boundary`, `gateway`/`gw`, `internal`; default `full`).
The `point_to_point` alias is also accepted. Unknown modes are rejected instead
of silently becoming full mode. The native parser also accepts `interface_mode=gateway/internal`
without reproducing the pinned Python branch's erroneous `mode` key lookup.
`bootstrap_only=yes/no` (default `no`) marks a configured interface as a discovery bootstrap lease;
the host/discovery lifecycle owns detaching and reactivating that interface as automatic connections change.

| `type` | Keys |
|---|---|
| `TCPServerInterface` | `listen_ip` (0.0.0.0), required `listen_port` or `port`. If both are set, **port wins**; 0 permits an ephemeral listener. Each client becomes its own interface. |
| `TCPClientInterface` | Required `target_host` and `target_port`. `port` is not an upstream alias here. |
| `UDPInterface` | Independent receive and forward address/port pairs; receive-only and forward-only operation are supported. `port` supplies missing direction ports without overriding explicit values. `device` and IPv4 hostname resolution follow the [UDP contract](udp-interface.md). |
| `SerialInterface` | Required `port`; `speed` (9600), `parity`, `databits`, `stopbits`. Owned reconnect and partial-frame expiry: [stream interfaces](stream-interfaces.md). |
| `AutoInterface` | IPv6 multicast group/device/scope/port controls: [Auto](auto-interface.md). |
| `BackboneInterface`, `BackboneClientInterface` | Linux/Android; bind/device/IPv6, listener/client aliases, reconnect and flap controls: [Backbone/Pipe](backbone-pipe.md). `port` overrides both direction ports, `remote` overrides `target_host`, and `listen_on` overrides `listen_ip`. A target host selects client operation; the explicit client type requires one. |
| `PipeInterface` | Required executable `command` (argument splitting, no shell); fractional `respawn_delay`: [Backbone/Pipe](backbone-pipe.md). |
| `KISSInterface`, `AX25KISSInterface` | Serial format, KISS flow/timing/port and AX.25 addressing: [KISS interfaces](kiss-interfaces.md). |
| `RNodeInterface`, `RNodeMultiInterface` | Serial/TCP device, required radio configuration, flow/airtime controls and nested radios: [RNode](rnode-interfaces.md). |
| `I2PInterface` | SAM router, destinations, peer policies and persistent inbound keys: [I2P](i2p-interface.md). |
| `WeaveInterface` | Required serial `port`, generic bitrate/IFAC/policy; fixed device format: [Weave](weave-interface.md). |

Unknown types are errors when enabled, ignored when disabled. A registered type
does not establish hardware/platform acceptance; see the individual evidence boundaries.
Statically compiled extensions can be registered through `Runtime.InterfaceFactoryRegistry`; parsing
requires that registry and validates the factory's declared option names. The daemon does not execute
upstream Python files from `<config>/interfaces`.

Interfaces support IFAC `networkname` / `network_name` and `passphrase` /
`pass_phrase`. The nonempty underscored alias wins; empty strings and the exact
string `None` are ignored without erasing an earlier alias. `ifac_size` is in
**bits**, while the native API exposes bytes. IFAC authenticates and masks
complete frames before packet parsing; wrong, missing, tampered or unexpected
authentication is rejected and counted. Neither passphrases nor derived private
keys appear in runtime statistics. Discovery publication includes credentials
only when explicitly configured with `publish_ifac`.

`bitrate` supplies the configured bits/second estimate used by MTU selection and
transport scheduling. TCP accepts device/address-family selection, optional
KISS framing, `fixed_mtu`, `connect_timeout` and
`max_reconnect_tries`; the daemon enables owned client reconnects. A reusable
TCP client retains its configured mode/authentication across disconnections,
while routes, links and the disconnected peer are released. See
[transport controls](transport-controls.md) for bounded recovery and staged link
MTU support.

Routing defaults, priority queue sizes, announce bandwidth/rate controls and
operational ingress/egress burst controls are documented in
[transport controls](transport-controls.md). These controls apply to configured,
accepted-child and discovered interfaces, not merely to parsed configuration.
`connect_timeout` is seconds (default 5); omitted `max_reconnect_tries` leaves
recovery unbounded in attempts but cancellation-aware.

Unrecognized or ineffective controls are rejected, including
`panic_on_interface_error` (native top-level/child failure ownership is fixed).
Type-specific settings are validated against their bearer, not silently applied
to unrelated types. Generic `outgoing=no` controls selection, not known-path or
explicit low-level sends; see [interface policy](interface-policy.md).

### Discovery advertisement settings

Supported-interface configuration accepts the pinned source's discovery metadata:
`discoverable`, `announce_interval`, `discovery_stamp_value`, `discovery_name`,
`discovery_encrypt`, `reachable_on`, `publish_ifac`, `location_cmd`, `latitude`,
`longitude`, `height`, `discovery_frequency`, `discovery_bandwidth`,
`discovery_modulation`, `discovery_lxmf_address`, and `ignore_config_warnings`.
These populate `RnsdInterfaceConfig.Discovery`; native publication supports enabled,
discoverable TCP and Backbone listeners. It advertises the actual bearer type and current
bound listening port; a TCP listener is never mislabeled Backbone. Parsed radio frequency/bandwidth/modulation
settings do not turn a TCP bearer into a radio interface or fabricated radio measurements.

Defaults are non-discoverable, six-hour announces (minimum five minutes), stamp value 16,
no encryption, no IFAC publication, and no optional location/operator/radio fields.
`announce_interval` is configured in minutes. Nonpositive stamp values select 16; values above
the native maximum 256 fail. Coordinates must be finite; LXMF addresses must be 16-byte hex.
Discovery normally promotes a mode other than gateway/AP/internal to gateway
(AP for RNode interfaces); setting
`ignore_config_warnings=yes` retains the explicit mode. Discoverability also implies a static
active transport identity, matching the pinned source.

Named attach/reload updates advertisement registration from that interface's current
configuration, including its discovery name, endpoint, interval and location provider.
Detach removes the registration and waits for any in-flight publication. Turning off
`discoverable` or interface enablement removes publication eligibility on reload; explicit
named attach/reload can still open a disabled configuration entry. These operations do not
reload global identities or discovery policy. Replacement discovery requirements are checked
before reload detaches the old bearer; a later bearer initialization failure does not restore
it. See [runtime operations](runtime-operations.md) for ownership and scheduling details.

`location_cmd` and executable `reachable_on` values are **not execution permission**. Dynamic
metadata resolution requires an explicit runtime executable authorizer, bounded output/time,
and cancellation. The parser never executes commands; the default runtime policy denies execution.
For `rnsd`, `--allow-discovery-commands` explicitly authorizes configured discovery metadata
executables. The host executes a single file path, with no shell or arguments, an eight-second
default timeout and a combined 4096-byte output limit. Cancellation terminates the owned process.
Configured `location_cmd` without authorization fails explicitly. Windows execution under this
policy is a deliberate native extension; the pinned Python implementation disables it there.
`network_identity` is a private identity-file path: expand `~`, resolve relative paths against the
process working directory (not the config directory), and load/create the identity there.

## Logging and CLI

The CLI contract is audited against `RNS/Utilities/rnsd.py` (`main`,
`program_setup`) at RNS 1.5.7 revision
`a29bfb51c468945c96a1ee22cae0114e993e2391`. Pinned options are
`--config`, repeatable `-v`/`--verbose`, `-q`/`--quiet`,
`-s`/`--service`, `-i`/`--interactive`, `--exampleconfig`,
`--version` and argparse help. Unknown arguments and missing option values
return 2 (parser failure), configuration/startup/runtime failures return 1,
and help, example, version and graceful cancellation return 0.
The native `-c`, `-?`, config-file-path support and
`--allow-discovery-commands` are explicit extensions.
Version output identifies DotReticulum and its pinned reference; it does not
impersonate upstream. The example is a supported native configuration rather
than a verbatim claim that every upstream example directive is implemented.
The interactive native management shell intentionally replaces upstream's
arbitrary Python REPL; see the documented commands below.

`-v`/`--verbose` and `-q`/`--quiet` are repeatable, including `-vvv`, `-qq`, and mixed short
clusters. The difference is added to a configured `loglevel` **before** clamping to 0–8.
As in the pinned reference, if there is no `loglevel` setting, these adjustments do not change
the initial NOTICE level. `-s`/`--service` ignores verbosity adjustments and appends to
`<config>/logfile` instead of stdout.

| Level | Name |
|---|---|
| 0–3 | Critical, Error, Warning, Notice |
| 4–6 | Info, Verbose, Debug |
| 7 | Pathing |
| 8 | Extreme (upstream log label `[Extra]`) |

Log records use local `[yyyy-MM-dd HH:mm:ss]` timestamps unless `logtimestamps=no`; labels and
padding match the Python logger. Service logging rotates **after** an append makes the file larger
than 5 MiB: `logfile` becomes `logfile.1`, replacing the single prior backup. The next record creates
a new logfile. A file-write/rotation failure emits an explicit diagnostic and permanently falls back
to the console rather than silently dropping messages.

`--version` identifies this experimental .NET implementation and pinned RNS 1.5.7, not an official
Reticulum release. The build-version scheme is documented in [README versioning](../README.md#versioning). `--exampleconfig` prints the supported native example.
`-i`/`--interactive` starts a bounded native management shell while routing continues:
`help`, `status`, `interfaces`, `persist`, and `quit`/`exit`. Status uses live host statistics,
and `persist` explicitly saves runtime state. Each input line is limited to 4096 characters;
an oversized line is discarded in full with a diagnostic, and later commands remain usable.
EOF, `quit`/`exit`, or cancellation cause graceful routing shutdown and persistence.
The shell also works with redirected stdin and with service logging; command responses remain on stdout.

This is a deliberate NativeAOT replacement for upstream `code.interact(local=globals())`,
**not** Python interpreter compatibility. It cannot evaluate expressions, execute scripts, or
mutate arbitrary runtime objects. A non-cancellation-aware custom input reader can leave at
most one pending read until input arrives; that read never invokes commands or accesses the disposed host.
Cancellation from Ctrl+C/SIGTERM follows normal daemon disposal and persistence; Unix SIGTERM
process coverage and managed cancellation coverage are distinct from Windows console-signal support.

Configuration enable/mode and logger output are checked by `tests/DotReticulum.Applications.Tests/RnsdConfigurationParityTests.cs` and fixtures generated by `tests/DotReticulum.Applications.Tests/GenerateRnsdConfiguration.py` against RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`. The generator verifies the imported checkout and hashes the relevant Reticulum, logger, rnsd, ConfigObj and TCP/UDP sources. Its absent `FixtureOnlyInterface` is a disclosed no-I/O synthesis probe, not live interface interoperability.

## Limitations

Link requests, proofs and link-addressed packets are forwarded through the link table. An owned top-level interface failure is terminal unless its bearer implements the configured reconnect lifecycle. Accepted-peer failures are isolated; tunnel paths bind to a replacement peer only after authenticated synthesis. Hardware-specific and unverified capabilities are listed in [TODO.md](../TODO.md).

## Verification

The configuration, CLI and runtime behavior described here is exercised by `tests/DotReticulum.Applications.Tests/RnsdConfigurationParityTests.cs`, `RnsdTests.cs`, `RnsdInteractiveTests.cs`, `RuntimeOperationsTests.cs` and the focused bearer tests. Generated Python-reference fixtures establish configuration and logger contracts; they are not live-interface evidence. See [runtime operations](runtime-operations.md) for runtime failure and platform boundaries.
