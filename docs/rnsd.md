# rnsd: transport daemon

The `rnsd` utility runs Reticulum as a service. It opens the interfaces in your configuration, learns paths from announces and, when transport is enabled, rebroadcasts eligible announces with transport-header rewriting, answers path requests and forwards DATA packets and their proofs between interfaces. Other programs on the same system can share the running instance.

## Usage

Run the daemon from a source checkout:

```sh
dotnet run --project src/DotReticulum.Applications -- rnsd --config <dir>
```

Or, with the published executable, write an example configuration and start it:

```sh
rnsd --exampleconfig > <dir>/config
rnsd --config <dir>
```

Stop the daemon with Ctrl+C or SIGTERM. Both shut it down gracefully and persist runtime state.

## Command-line options

```text
DotReticulum rnsd - Reticulum transport daemon (experimental)

Usage:
  rnsd [--config <dir|file>] [-v|-vv] [-q] [--service] [--interactive]
  rnsd --exampleconfig       Print an example configuration
  rnsd --help

Options:
  -c, --config <path>   Configuration directory (containing 'config') or a config file.
                        Search: system /etc/reticulum, ~/.config/reticulum, ~/.reticulum.
                        Missing configs are created. State (transport identity) is kept in
                        <dir>/storage/transport_identity.
  -v, --verbose         Raise the log level (repeatable); -q lowers it. The default level
                        comes from [logging] loglevel (0 critical .. 8 extreme).
  -s, --service         Log to <config dir>/logfile; ignore verbosity adjustments.
  -i, --interactive     Native management shell: help, status, interfaces, persist, quit.
                        A deliberate replacement for the upstream Python REPL; no code execution.
  --allow-discovery-commands
                        Explicitly authorize configured discovery metadata executables.
                        No shell or arguments; bounded execution. Disabled by default.
  --version             Show DotReticulum and pinned reference versions.
  --exampleconfig       Print an example configuration to stdout and exit.
  -h, --help            Show this help.

Unsupported configuration controls are errors; see
https://github.com/dennispayne/DotReticulum/blob/main/docs/rnsd.md
Stop with Ctrl+C or SIGTERM.
```

`-q`/`--quiet` is repeatable like `-v`. The pinned upstream options are `--config`, `-v`, `-q`, `-s`, `-i`, `--exampleconfig`, `--version` and help. `-c`, `-?`, passing a config file instead of a directory, and `--allow-discovery-commands` are native extensions.

`--version` identifies this experimental .NET implementation and the pinned RNS 1.5.7 reference. It is not an official Reticulum release. The build-version scheme is described in [README versioning](../README.md#versioning). `--exampleconfig` prints a supported native configuration; it does not claim that every upstream example directive is implemented.

## Configuration location

The configuration directory contains a file named `config`. The transport identity is stored in `<dir>/storage/transport_identity` and created when absent.

Without `--config`, `rnsd` looks for an existing `config` in this order:

1. `/etc/reticulum` (on Windows, `<home drive>\etc\reticulum`)
2. `~/.config/reticulum`
3. `~/.reticulum`, which is used when neither of the others exists

An explicit `--config` path that does not exist is treated as a **directory**, not a filename. An existing config file is also accepted as a .NET extension. Missing directories and configs are created; existing files are never replaced.

The upstream default configuration enables `AutoInterface`. The native default conservatively keeps multicast discovery disabled until you enable it, and logs that decision. `AutoInterface` itself is implemented, but no existing configuration is rewritten and no TCP listener is substituted. Enable an interface before expecting external connectivity.

## Configuration file

The file uses a supported subset of Reticulum's ConfigObj format:

- `[section]`, `[[subsection]]` and `key = value`
- `#` comments outside quotes
- single- or double-quoted values
- comma-delimited lists, including quoted items containing commas and trailing list commas
- `[[[Radio name]]]` for RNodeMulti radios inside their parent interface

Duplicate settings or interfaces, unclosed quotes, unknown sections and ineffective controls are errors. The exception is the runtime-assigned interface keys `name`, `selected_interface_mode` and `configured_bitrate`: upstream overwrites them on load, and Python tools may write them back to the file, so they are accepted and ignored. Other deeper nesting and multiline or triple-quoted values are unsupported and diagnosed.

### Global settings

| Section | Key | Meaning |
|---|---|---|
| `[reticulum]` | `enable_transport` | `yes`/`no` (default `no`). When `yes`, forward and rebroadcast. |
| `[reticulum]` | `static_transport_identity` | Default `no`; discoverable interfaces imply `yes`. The persisted internal identity and the active transport identity have separate ownership and lifetimes. |
| `[reticulum]` | `share_instance` | Default `yes`. Elect a local shared host or connect as a local client. |
| `[reticulum]` | `instance_name`, `shared_instance_type` | Instance name `default`; type `tcp` or `unix`. Linux uses abstract Unix sockets by default; other platforms use loopback TCP. No Python modules are loaded. |
| `[reticulum]` | `shared_instance_port`, `instance_control_port` | Default 37428 / 37429; valid ports 1–65535. |
| `[reticulum]` | `rpc_key` | Hexadecimal shared management key. Invalid hex is rejected rather than silently falling back to an unaudited key. |
| `[reticulum]` | `network_identity` | Optional persisted identity path for network discovery signing. |
| `[reticulum]` | `enable_remote_management`, `enable_interface_management` | Defaults `no` / `yes`. Management request authorization is still enforced. |
| `[reticulum]` | `remote_management_allowed` | Comma-delimited 16-byte identity hashes in hex; duplicates are removed. |
| `[reticulum]` | `discover_interfaces`, `interface_discovery_sources` | Default `no`; optional allowed source-identity hashes. |
| `[reticulum]` | `required_discovery_value` | Default 16; nonpositive values select the upstream default. Native maximum 256. |
| `[reticulum]` | `autoconnect_discovered_interfaces`, `autoconnect_unverified_implementations` | Default 0 / `no`. Bounded maximum 1024; nonpositive disables. This is not permission to treat TCP advertisements as Backbone endpoints. |
| `[reticulum]` | `autoconnect_interface_mode`, `autoconnect_interface_gravity`, `autoconnect_announces_to_internal` | Optional overrides for discovered interfaces. Defaults: mode policy, gravity 0, no internal-announce override. |
| `[reticulum]` | `publish_blackhole`, `blackhole_sources`, `blackhole_update_interval` | Default `no`, no sources, 60 minutes. The interval is clamped to at least 2 minutes; source hashes are deduplicated. |
| `[logging]` | `loglevel`, `logtimestamps` | Level 0–8; timestamps default `yes`. A created config uses level 4. Without a `loglevel` setting, upstream's initial NOTICE level 3 applies. |
| `[interfaces]` | `[[Name]]` | One subsection per interface. |

`network_identity` is a private identity-file path. `~` is expanded, and relative paths resolve against the process working directory, not the config directory. The identity is loaded or created there.

### Interfaces

Each interface subsection needs a `type` when enabled. Enablement and mode follow these rules:

- `enabled` and `interface_enabled` are **ORed**: either one set to `yes` enables the interface, even if the other says `no`. If both are absent, the interface is **disabled**.
- `interface_mode` takes precedence over `mode`. Accepted values are `full` (default), `pointtopoint`/`ptp`/`point_to_point`, `access_point`/`accesspoint`/`ap`, `roaming`, `boundary`, `gateway`/`gw` and `internal`. Unknown modes are rejected instead of silently becoming full mode.
- `bootstrap_only` (default `no`) marks the interface as a discovery bootstrap lease. The host and discovery lifecycle detach and reactivate it as automatic connections change.

| `type` | Keys |
|---|---|
| `TCPServerInterface` | `listen_ip` (0.0.0.0) and a required `listen_port` or `port`. If both are set, **`port` wins**. Port 0 permits an ephemeral listener. Each client becomes its own interface. |
| `TCPClientInterface` | Required `target_host` and `target_port`. `port` is not an upstream alias here. |
| `UDPInterface` | Independent receive and forward address/port pairs; receive-only and forward-only operation are supported. `port` fills in missing direction ports without overriding explicit values. `device` and IPv4 hostname resolution follow the [UDP contract](udp-interface.md). |
| `SerialInterface` | Required `port`; `speed` (9600), `parity`, `databits`, `stopbits`. Reconnect and partial-frame expiry: [stream interfaces](stream-interfaces.md). |
| `AutoInterface` | IPv6 multicast group, device, scope and port controls: [Auto](auto-interface.md). |
| `BackboneInterface`, `BackboneClientInterface` | Linux/Android. Bind, device, IPv6, listener/client aliases, reconnect and flap controls: [Backbone/Pipe](backbone-pipe.md). `port` overrides both direction ports, `remote` overrides `target_host` and `listen_on` overrides `listen_ip`. A target host selects client operation; the explicit client type requires one. |
| `PipeInterface` | Required executable `command` (argument splitting, no shell); fractional `respawn_delay`: [Backbone/Pipe](backbone-pipe.md). |
| `KISSInterface`, `AX25KISSInterface` | Serial format, KISS flow/timing/port and AX.25 addressing: [KISS interfaces](kiss-interfaces.md). |
| `RNodeInterface`, `RNodeMultiInterface` | Serial/TCP device, required radio configuration, flow/airtime controls and nested radios: [RNode](rnode-interfaces.md). |
| `I2PInterface` | SAM router, destinations, peer policies and persistent inbound keys: [I2P](i2p-interface.md). |
| `WeaveInterface` | Required serial `port`, generic bitrate/IFAC/policy; fixed device format: [Weave](weave-interface.md). |

Unknown types are errors when enabled and ignored when disabled. A registered type does not establish hardware or platform acceptance; see each interface page for its evidence boundary.

Statically compiled extensions can be registered through `Runtime.InterfaceFactoryRegistry`. Parsing requires that registry and validates each factory's declared option names. The daemon does not execute upstream Python files from `<config>/interfaces`.

Unrecognized or ineffective controls are rejected, including `panic_on_interface_error`, because native top-level and child failure ownership is fixed. Type-specific settings are validated against their bearer and never silently applied to unrelated types. Generic `outgoing=no` controls interface selection, not known-path or explicit low-level sends; see [interface policy](interface-policy.md).

### Interface access codes

Interfaces support IFAC through `networkname`/`network_name` and `passphrase`/`pass_phrase`. The nonempty underscored alias wins. Empty strings and the exact string `None` are ignored without erasing an earlier alias. `ifac_size` is in **bits**, while the native API exposes bytes.

IFAC authenticates and masks complete frames before packet parsing. Wrong, missing, tampered or unexpected authentication is rejected and counted. Neither passphrases nor derived private keys appear in runtime statistics. Discovery publication includes credentials only when you explicitly set `publish_ifac`.

### Bitrate, reconnects and transport controls

`bitrate` supplies the configured bits-per-second estimate used for MTU selection and transport scheduling.

TCP interfaces accept device and address-family selection, optional KISS framing, `fixed_mtu`, `connect_timeout` and `max_reconnect_tries`. `connect_timeout` is in seconds (default 5). Without `max_reconnect_tries`, the daemon's owned client reconnects are unbounded in attempts but cancellation-aware. A reusable TCP client keeps its configured mode and authentication across disconnections, while routes, links and the disconnected peer are released.

Routing defaults, priority queue sizes, announce bandwidth and rate controls, ingress/egress burst controls, bounded recovery and staged link MTU support are documented in [transport controls](transport-controls.md). These controls apply to configured, accepted-child and discovered interfaces, not only to parsed configuration.

### Proofs and the probe responder

`use_implicit_proof` defaults to `yes` and controls proofs for single-destination packets:

- enabled: a 64-byte signature
- disabled: the 32-byte packet hash followed by a 64-byte signature

It does not change link proofs. The router's ordinary `ProveAsync` uses the owner's policy; its existing boolean overload keeps an explicit caller-selected format.

`respond_to_probes` defaults to `no`. When enabled on a transport owner, the `rnstransport.probe` destination uses the transport identity, proves valid encrypted packets without an application subscriber and refuses links. Non-transport and shared-client instances do not host duplicate responders. The first unsolicited announce happens strictly after 15 seconds, then strictly after each two-hour interval, driven by the existing one-second maintenance loop. Path requests can solicit announces independently. Transport statistics report a detached `probe_responder` hash.

Both policies are global and require a restart.

### Discovery advertisement settings

Interface configuration accepts the pinned source's discovery metadata keys: `discoverable`, `announce_interval`, `discovery_stamp_value`, `discovery_name`, `discovery_encrypt`, `reachable_on`, `publish_ifac`, `location_cmd`, `latitude`, `longitude`, `height`, `discovery_frequency`, `discovery_bandwidth`, `discovery_modulation`, `discovery_lxmf_address` and `ignore_config_warnings`. They populate `RnsdInterfaceConfig.Discovery`.

Native publication supports enabled, discoverable TCP and Backbone listeners. It advertises the actual bearer type and the currently bound listening port, so a TCP listener is never mislabeled Backbone. Radio frequency, bandwidth and modulation settings do not turn a TCP bearer into a radio interface or produce fabricated radio measurements.

Defaults and limits:

- Interfaces are not discoverable by default.
- `announce_interval` is in minutes; default six hours, minimum five minutes.
- Stamp value defaults to 16. Nonpositive values select 16; values above the native maximum of 256 fail.
- No encryption, no IFAC publication and no optional location, operator or radio fields.
- Coordinates must be finite; LXMF addresses must be 16-byte hex.

Discovery normally promotes any mode other than gateway, AP or internal to gateway (AP for RNode interfaces). Set `ignore_config_warnings=yes` to keep the explicit mode. As in the pinned source, discoverability also implies a static active transport identity.

Named attach and reload update advertisement registration from that interface's current configuration, including its discovery name, endpoint, interval and location provider. Detach removes the registration and waits for any in-flight publication. Turning off `discoverable` or interface enablement removes publication eligibility on reload, although an explicit named attach or reload can still open a disabled configuration entry. These operations do not reload global identities or discovery policy. Replacement discovery requirements are checked before reload detaches the old bearer; a later bearer initialization failure does not restore it. See [runtime operations](runtime-operations.md) for ownership and scheduling.

`location_cmd` and executable `reachable_on` values are **not permission to execute anything**. Dynamic metadata needs an explicit runtime executable authorizer with bounded output, time and cancellation. The parser never executes commands, and the default runtime policy denies execution. For `rnsd`, `--allow-discovery-commands` authorizes configured discovery metadata executables:

- a single file path is run, with no shell and no arguments
- the default timeout is eight seconds
- combined output is limited to 4096 bytes
- cancellation terminates the owned process

A configured `location_cmd` without authorization fails explicitly.

## Logging

`-v` and `-q` are repeatable, including clusters such as `-vvv`, `-qq` and mixed short clusters. Their difference is added to the configured `loglevel` **before** clamping to 0–8. As in the pinned reference, when there is no `loglevel` setting these adjustments leave the initial NOTICE level unchanged.

| Level | Name |
|---|---|
| 0–3 | Critical, Error, Warning, Notice |
| 4–6 | Info, Verbose, Debug |
| 7 | Pathing |
| 8 | Extreme (upstream log label `[Extra]`) |

Records use local `[yyyy-MM-dd HH:mm:ss]` timestamps unless `logtimestamps=no`. Labels and padding match the Python logger.

With `-s`/`--service`, verbosity adjustments are ignored and records are appended to `<config>/logfile` instead of stdout. The file rotates **after** an append makes it larger than 5 MiB: `logfile` becomes `logfile.1`, replacing the single prior backup, and the next record creates a new logfile. If writing or rotating fails, `rnsd` prints an explicit diagnostic and permanently falls back to the console rather than silently dropping messages.

## Interactive management shell

`-i`/`--interactive` starts a bounded native management shell while routing continues. It accepts these commands:

| Command | Action |
|---|---|
| `help` | List commands. |
| `status` | Show live host statistics. |
| `interfaces` | List interfaces. |
| `persist` | Save runtime state. |
| `quit`, `exit` | Shut down routing gracefully and persist state. |

EOF and cancellation also shut down gracefully with persistence. Each input line is limited to 4096 characters; an oversized line is discarded in full with a diagnostic and later commands still work. The shell works with redirected stdin and with service logging, and command responses stay on stdout.

This shell replaces upstream's `code.interact(local=globals())` so the daemon can be published with NativeAOT. It is **not** Python interpreter compatibility: it cannot evaluate expressions, run scripts or modify arbitrary runtime objects. A custom input reader that ignores cancellation can leave at most one pending read until input arrives; that read never runs commands or touches the disposed host.

## Exit statuses

| Status | Meaning |
|---|---|
| 0 | Help, example config, version, or graceful cancellation |
| 1 | Configuration, startup or runtime failure |
| 2 | Unknown argument or missing option value (parser failure) |

## Differences from upstream

- The native default config leaves `AutoInterface` disabled, where upstream enables it.
- `-c`, `-?`, config-file paths and `--allow-discovery-commands` are native extensions.
- The interactive shell is a fixed command set, not a Python REPL.
- Version output identifies DotReticulum and its pinned reference; it does not impersonate upstream.
- Unknown modes, unknown sections and ineffective controls such as `panic_on_interface_error` are rejected rather than ignored.
- `interface_mode=gateway` and `interface_mode=internal` work without reproducing the pinned Python branch's erroneous `mode` key lookup.
- Interface extensions are statically compiled and registered; Python files in `<config>/interfaces` are not executed.
- Discovery metadata commands can run on Windows when explicitly authorized. The pinned Python implementation disables them there.
- Ctrl+C and SIGTERM follow normal daemon disposal and persistence. Unix SIGTERM process coverage and managed cancellation coverage are separate from Windows console-signal support.

## Limitations

Link requests, proofs and link-addressed packets are forwarded through the link table. An owned top-level interface failure is terminal unless its bearer implements the configured reconnect lifecycle. Accepted-peer failures are isolated, and tunnel paths bind to a replacement peer only after authenticated synthesis. Hardware-specific and unverified capabilities are listed in [TODO.md](../TODO.md).

## Verification

The CLI contract is audited against `RNS/Utilities/rnsd.py` (`main`, `program_setup`) at RNS 1.5.7, revision `a29bfb51c468945c96a1ee22cae0114e993e2391`.

| Tests | What they show |
|---|---|
| `tests/DotReticulum.Applications.Tests/RnsdConfigurationParityTests.cs` with fixtures from `GenerateRnsdConfiguration.py` | Enable/mode handling and logger output match the pinned reference. The generator verifies the imported checkout and hashes the relevant Reticulum, logger, rnsd, ConfigObj and TCP/UDP sources. Its absent `FixtureOnlyInterface` is a disclosed no-I/O synthesis probe, not live interface interoperability. |
| `RnsdTests.cs`, `RnsdInteractiveTests.cs`, `RuntimeOperationsTests.cs` and the focused bearer tests | Configuration, CLI, interactive shell and runtime behavior. |
| `ProofProbePolicyTests` | Nine actual-primary startup and proof observations, plus strict timing, cancellation, failure reporting and recovery for the proof and probe policies. |

Windows NativeAOT and actual-primary probe and receipt checks exercise both proof formats, both directions, a native transport hop and an abrupt identity-preserving restart; see the development history for exact scope and commands. Linux replay and other bearers are not inferred from these checks.

Generated Python-reference fixtures establish configuration and logger contracts; they are not live-interface evidence. See [runtime operations](runtime-operations.md) for runtime failure and platform boundaries.
