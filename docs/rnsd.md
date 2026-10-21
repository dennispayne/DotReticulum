# rnsd – DotReticulum transport daemon

`rnsd` is a Reticulum transport node: it listens on / connects to interfaces, learns paths from
announces, rebroadcasts announces (with HEADER_2 transport rewriting), answers path requests and
forwards DATA packets (plus their proofs, via the reverse table) between interfaces.

    dotnet run --project src/DotReticulum.Applications -- rnsd --config <dir>
    rnsd --exampleconfig > <dir>/config

The config directory contains `config`; the transport identity is stored (created on first run) in
`<dir>/storage/transport_identity`. With no `--config`, search for an existing `config` in
`/etc/reticulum`, then `~/.config/reticulum`, otherwise use `~/.reticulum`.
On Windows the system-directory candidate is `<home drive>\etc\reticulum`.
An explicit nonexistent `--config` path is a **directory**, not a filename.
An existing config file is also accepted as a .NET extension.
Stop with Ctrl+C or SIGTERM (graceful shutdown).

## Config format

A supported subset of Reticulum's ConfigObj format: `[section]`, `[[subsection]]`, `key = value`,
`#` comments outside quotes, single/double quoted values, and comma-delimited lists (including
quoted items containing commas and trailing list commas). Duplicate settings/interfaces,
unclosed quotes, unknown sections, and ineffective controls are errors. Multiline/triple-quoted
values and arbitrary deeper nesting are not supported; they are not silently treated as working controls.

Missing directories/configs are created without replacing existing files. Upstream's default config
enables `AutoInterface`, which is not yet implemented natively. The native default explicitly disables
that interface and logs a warning; it does **not** silently open a TCP listener instead.
Enable a supported interface before expecting external connectivity.

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
The historical native alias `point_to_point` remains accepted. Unknown modes are rejected instead
of silently becoming full mode. The native parser also accepts `interface_mode=gateway/internal`
without reproducing the pinned Python branch's erroneous `mode` key lookup.
`bootstrap_only=yes/no` (default `no`) marks a configured interface as a discovery bootstrap lease;
the host/discovery lifecycle owns detaching and reactivating that interface as automatic connections change.

| `type` | Keys |
|---|---|
| `TCPServerInterface` | `listen_ip` (0.0.0.0), required `listen_port` or `port`. If both are set, **port wins**; 0 permits an ephemeral listener. Each client becomes its own interface. |
| `TCPClientInterface` | Required `target_host` and `target_port`. `port` is not an upstream alias here. |
| `UDPInterface` | Explicit `listen_ip`, `forward_ip`, and ports. `port` supplies missing `listen_port`/`forward_port`; it does not override explicit values. Receive-only/send-only configurations are explicitly unsupported by this native adapter. |
| `SerialInterface` | `port` (required), `speed` (9600). |

Other types (AutoInterface, RNodeInterface, …) are errors when enabled, ignored when disabled.
Statically compiled extensions can be registered through `Runtime.InterfaceFactoryRegistry`; parsing
requires that registry and validates the factory's declared option names. The daemon does not execute
upstream Python files from `<config>/interfaces`.

Controls not listed above are rejected, including IFAC/networkname/passphrase, ingress/egress caps,
queue sizing, `panic_on_interface_error` (native top-level/child failure ownership is fixed),
interface device selection, KISS framing and serial-format overrides. This is an explicit
remaining parity limitation, not a claim that all upstream configuration is supported.

### Discovery advertisement settings

Supported-interface configuration accepts the pinned source's discovery metadata:
`discoverable`, `announce_interval`, `discovery_stamp_value`, `discovery_name`,
`discovery_encrypt`, `reachable_on`, `publish_ifac`, `location_cmd`, `latitude`,
`longitude`, `height`, `discovery_frequency`, `discovery_bandwidth`,
`discovery_modulation`, `discovery_lxmf_address`, and `ignore_config_warnings`.
These populate `RnsdInterfaceConfig.Discovery`; runtime publication still requires a supported
discoverable bearer and host registration.

Defaults are non-discoverable, six-hour announces (minimum five minutes), stamp value 16,
no encryption, no IFAC publication, and no optional location/operator/radio fields.
`announce_interval` is configured in minutes. Nonpositive stamp values select 16; values above
the native maximum 256 fail. Coordinates must be finite; LXMF addresses must be 16-byte hex.
Discovery normally promotes a mode other than gateway/AP/internal to gateway; setting
`ignore_config_warnings=yes` retains the explicit mode. Discoverability also implies a static
active transport identity, matching the pinned source.

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
Reticulum release. `--exampleconfig` prints the supported native example.
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

Configuration enable/mode and logger output fixtures are generated by
`tests/DotReticulum.Applications.Tests/GenerateRnsdConfiguration.py` against hosted revision
`a29bfb51c468945c96a1ee22cae0114e993e2391`. The generator verifies the actual imported checkout
and records/checks SHA-256 hashes of Reticulum.py, the logger, rnsd.py, ConfigObj and TCP/UDP sources.
Its missing `FixtureOnlyInterface` is a disclosed no-I/O synthesis probe, not live interface interop.

## Limitations

No ingress control or announce bandwidth caps, persisted live tunnel recreation or IFAC.
Link requests, proofs and link-addressed packets are forwarded through the link table.
A failing interface at startup stops
the daemon; TCP clients do not reconnect.
