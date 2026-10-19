# rnsd – DotReticulum transport daemon

`rnsd` is a Reticulum transport node: it listens on / connects to interfaces, learns paths from
announces, rebroadcasts announces (with HEADER_2 transport rewriting), answers path requests and
forwards DATA packets (plus their proofs, via the reverse table) between interfaces.

    dotnet run --project src/DotReticulum.Applications -- rnsd --config <dir>
    rnsd --exampleconfig > <dir>/config

The config directory contains `config`; the transport identity is stored (created on first run) in
`<dir>/storage/transport_identity`. Default directory: `~/.dotreticulum`.
Stop with Ctrl+C or SIGTERM (graceful shutdown).

## Config format

A subset of Reticulum's ConfigObj ini format: `[section]`, `[[subsection]]`, `key = value`,
`#` comments (also trailing ` #` comments), optional quotes. Unknown keys are ignored.

| Section | Key | Meaning |
|---|---|---|
| `[reticulum]` | `enable_transport` | `yes`/`no` (default `no`). When `yes`, forward and rebroadcast. |
| `[logging]` | `loglevel` | 0 critical … 7 extreme (default 4). |
| `[interfaces]` | `[[Name]]` | One subsection per interface. |

Per interface: `type` (required), `enabled` / `interface_enabled` (default `yes`),
`mode` / `interface_mode` (`full`, `point_to_point`/`ptp`, `access_point`/`ap`, `roaming`,
`boundary`, `gateway`/`gw`, `internal`; default `full`).

| `type` | Keys |
|---|---|
| `TCPServerInterface` | `listen_ip` (0.0.0.0), `listen_port`/`port` (4242). Each client becomes its own interface. |
| `TCPClientInterface` | `target_host` (required), `target_port`/`port` (4242). |
| `UDPInterface` | `listen_ip`, `listen_port`, `forward_ip` (255.255.255.255), `forward_port`, `port`. |
| `SerialInterface` | `port` (required), `speed` (9600). |

Other types (AutoInterface, RNodeInterface, …) are errors when enabled, ignored when disabled.

## Limitations

No ingress control or announce bandwidth caps, tunnels, shared-instance/local-client mode or IFAC.
Link requests, proofs and link-addressed packets are forwarded through the link table.
A failing interface at startup stops
the daemon; TCP clients do not reconnect.
