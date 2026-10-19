# Telemetry (DotReticulum-specific)

`telemetry` publishes periodic system and node metrics from one DotReticulum node and lets another node watch them. The format is defined here; it is **not** part of upstream Reticulum or LXMF, so only DotReticulum nodes speak it.

## Destination

| Item | Value |
| --- | --- |
| Destination | `dotreticulum.telemetry` (SINGLE, inbound, identity file `storage/identities/telemetry` by default) |
| Announce app_data | msgpack map `{"v": 1, "name": <host name>}` |
| Links | accepted; no authentication (reports contain no secrets, but do reveal host details: do not run this on networks you do not trust) |
| Request path | `report` (policy: allow all) returns one fresh report as msgpack `bin` |
| Push | every active link receives one report per interval as an encrypted link packet (msgpack map). A report larger than the link MDU is sent as a Resource instead |

## Report

A report is a msgpack map with string keys. Receivers must ignore unknown keys; `v` changes only for breaking changes and receivers drop reports whose `v` they do not know.

| Key | Type | Meaning |
| --- | --- | --- |
| `v` | int | format version, currently `1` |
| `seq` | int | per-daemon sequence number, increasing by 1 per report |
| `ts` | float | Unix time (seconds) when the report was built |
| `host` | str | machine name |
| `os`, `arch`, `runtime` | str | OS description, CPU architecture, .NET runtime description |
| `uptime` | float | seconds since the process started |
| `cpu_count` | int | logical processors |
| `cpu_time` | float | total process CPU time in seconds |
| `mem_rss` | int | process working set, bytes |
| `mem_total` | int | memory available to the process (GC), bytes |
| `gc_heap` | int | managed heap bytes |
| `threads` | int | process thread count |
| `load` | float or nil | 1-minute load average (Linux only, nil elsewhere) |
| `disk_total`, `disk_free` | int or nil | drive holding the config `storage` directory, bytes |
| `transport` | bool | whether this node routes for others |
| `links` | int | links currently known to this node's link manager |
| `interfaces` | array of map | one `{"name", "type"}` per configured interface |

The format has no per-interface traffic counters because the packet-interface contract does not expose them yet.

## Usage

```sh
# publisher (announces at startup; -b N re-announces every N seconds)
telemetry daemon --config <dir> -i 10

# viewer: print every report, optionally stop after N
telemetry watch <destination hash> --config <dir>
telemetry get <destination hash> --config <dir>

# print this node's telemetry destination without starting the network
telemetry -p --config <dir>
```

The viewer resolves the destination from its path table, so the daemon's announce (or a path request answered by a transport node such as `rnsd`) must reach it first.

## Verification

Automated tests (`tests/DotReticulum.Applications.Tests/TelemetryTests.cs`) cover the msgpack round trip, version rejection, a daemon publishing to a receiver directly, and the same through a .NET `rnsd` transport hop. There is no Python implementation of this format to interoperate with.
