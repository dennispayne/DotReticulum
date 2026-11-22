# rnstatus, rnpath and rnprobe

These three utilities let you inspect and manage a running Reticulum instance: `rnstatus` shows interface statistics, `rnpath` shows and manages paths, and `rnprobe` checks whether a destination is reachable.

The reference contract is Reticulum 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`. The implementation is experimental and AI-assisted, and these utilities do not imply complete Reticulum parity. Source references below are relative to `RNS/Utilities/` in that checkout.

## rnstatus

The `rnstatus` utility shows the status of the interfaces on a shared instance, much like `ifconfig`. It reads statistics either from the local shared instance (selected with `--config`, and required) or from an identified remote transport instance (`-R` with an identity file given by `-i`). This follows `rnstatus.py:159-870, 873-959`.

### Usage

```sh
rnstatus
rnstatus -a
rnstatus -j
rnstatus -m -I 2
rnstatus -d
rnstatus -R <transport-hash> -i <identity-file>
```

### Command-line options

```text
DotReticulum rnstatus - Reticulum network status (experimental)

Usage:
  rnstatus [options] [interface-filter]
  rnstatus -h, --help

Options:
  --config <dir>          Reticulum configuration directory
  --version               Show version
  --attach <name>         Attach an interface by name
  --detach <name>         Detach an interface by name
  --reload <name>         Reload an interface by name
  -a, --all               Show every interface
  -A, --announce-stats    Show announce statistics
  -P, --pr-stats          Show path-request statistics
  -l, --link-stats        Show link-table counts
  -B, --burst             Show interfaces with active bursts
  -b, --blocked-ips       Show blocked-IP counts when measured
  -t, --totals            Show traffic totals
  -p, --pps               Include packets/second when measured
  -q, --queues            Show queue statistics when available
  -z, --profiling         Show profiling results when available
  -s, --sort <field>      Sort interfaces by a supported statistic
  -r, --reverse           Reverse sort order
  -j, --json              Emit statistics as JSON
  -R <hash>               Query an identified remote transport
  -i <identity-file>      Identity for remote management
  -w <seconds>            Remote query timeout (default: 15)
  -d, --discovered        List discovered interfaces
  -D                      Show discovered-interface details
  --show-stale            Include stale discovery records
  --show-unknown          Include unknown discovery records
  -m, --monitor           Monitor continuously
  -I, --monitor-interval  Monitor interval (default: 1)
  -v, --verbose           Increase verbosity
```

### Behaviour

Remote queries go to the `rnstransport.remote.management` destination, whose `/status` request accepts `[link_stats, profiling]`. Local queries use the existing bounded management RPC.

Implemented command paths include interface statistics and JSON output, name and burst filters, supported sorting, monitor refresh and cancellation, interface attach/detach/reload requests, link counts, discovery-record listing and details, and JSON serialization without reflection.

**Discovery.** Discovery output reads the runtime's persisted `DiscoveryService` records. Text output honors upstream's name, stale and unknown filters. JSON output follows the reference and emits the unfiltered snapshot. Configuration templates for discovered interfaces are reconstructed from the validated packed announcement; they do not start the advertised interface. Details and config output are rendered from the currently persisted discovery schema, and bearer configuration differences remain platform-specific.

**Measured fields.** Status reports:

- actual bearer byte totals, and separately named decoded packet-boundary totals
- sampled per-interface and transport bit rates
- announce and path-request rates and counts
- accepted-packet and send packet rates
- queue depth, drops, pressure and packet-filter rejections
- with announce statistics enabled, observed queued and held announces
- positive packet-filter, protocol, IFAC, blocked-IP and bearer TX queue/drop measurements, when present
- configured gravity, IFAC identity and announce-rate limits

Aggregate control traffic includes spawned-peer observations, while byte and packet totals use the manager's parent-rolled counters once. Bit rates are measured over consecutive status snapshots, so the first snapshot only establishes a baseline and has no rate. Path and announce frequencies use the bounded router observation windows. Ingress-burst state and activation timestamps come from the runtime controls, and spawned-peer aggregates include currently active child bursts.

**What is not invented.** Fields are left out or null rather than estimated:

- Bearer totals and bit rates stay null when an extension supplies no measured diagnostics. Decoded packet totals are never substituted for TCP framing bytes.
- Upstream TX-buffered bytes, stalled state and TX drop counters appear only when a bearer measures them; currently that is the Backbone peer endpoint.
- Local shared TCP writes instead report the bytes in an incomplete `NetworkStream.WriteAsync` as `.NET tx_write_pending_bytes`. This is not upstream's transmit-buffer size or a drop count, and no local queue or drop policy is invented.
- Blocked-IP counts appear only when the bearer reports a measured count.
- Physical radio readings, hardware speed samples and bearer framing overhead are unavailable unless the device exposes them.

See [runtime operations](runtime-operations.md) for measured-field boundaries.

**Profiling.** `-z` output is .NET-specific and labeled as such. It is not the upstream profiler.

## rnpath

The `rnpath` utility lets you look up and manage paths to destinations: display the path table, request paths, drop paths and announces, and manage blackholed identities. This follows `rnpath.py:43-94, 94-478, 481-528`.

### Usage

```sh
rnpath <destination-hash>
rnpath -t
rnpath -t -m 2
rnpath -r
rnpath -d <destination-hash>
rnpath -b
rnpath -t -R <transport-hash> -i <identity-file>
```

### Command-line options

```text
DotReticulum rnpath - Reticulum path management (experimental)

Usage:
  rnpath [options] [destination-hash] [blackhole-list-filter]
  rnpath -h, --help
  rnpath --version

Options:
  --config <dir>           Reticulum configuration directory
  -t, --table              Show known paths
  -m, --max <hops>         Filter table by maximum hops
  -r, --rates              Show announce rate information
  -d, --drop               Remove the path to a destination
  -D, --drop-announces     Drop queued announces
  -x, --drop-via           Drop paths via a transport identity
  -w <seconds>             Path-request timeout
  -R <hash>                Manage a remote transport instance
  -i <identity-file>       Identity for authenticated remote management
  -W <seconds>             Remote query timeout
  -b, --blackholed         List blackholed identities
  -B, --blackhole          Blackhole an identity
  -U, --unblackhole        Remove a blackhole
  --duration <hours>       Blackhole duration
  --reason <text>          Blackhole reason
  -p, --blackholed-list    Read a transport's published blackhole list
  -j, --json               Emit JSON for tables/lists
  -v, --verbose            Increase verbosity
```

### Behaviour

Local operations use the existing management operations. Remote table and rate queries use the authorized remote `/path` API with the pinned management request shape. Blackhole list, add and remove use the native blackhole store, and published lists are read with the native list request.

- Hashes are exactly 32 hexadecimal characters.
- Table JSON renders binary hashes as lowercase hex without delimiters. Human-readable path rows use live interface and expiry data.
- Rate rows use stored announce timestamps and violation and block fields.
- When both `--table` and `--rates` are given, the path table wins, as in the pinned CLI.
- Missing paths and unsuccessful drops are errors. An empty unfiltered table is valid output, and an empty remote table response is a successful empty result, not an authentication failure.
- Blackhole text lists support filtering. In the pinned CLI, `--json` applies to path and rate tables, not to blackhole lists.

A path request uses the node's cancellation-aware path wait, which may repeat requests while waiting. The upstream CLI sends one initial request and then waits.

**Remote mutations.** `-R` together with path drops, announce-queue changes, via drops or blackhole management is reported as unsupported, exactly where the upstream CLI marks it unimplemented. DotReticulum does not emulate remote mutations that the pinned management service does not implement.

## rnprobe

The `rnprobe` utility sends probe packets to a destination and reports whether, and how quickly, they were delivered. It needs the full dotted destination name and the 32-character destination hash. This follows `rnprobe.py:42-206, 209-249`.

### Usage

```sh
rnprobe rnstransport.probe <destination-hash>
rnprobe rnstransport.probe <destination-hash> -s 256 -n 5 -w 1
```

A transport instance answers probes on `rnstransport.probe` only when `respond_to_probes` is enabled; see [rnsd](rnsd.md#proofs-and-the-probe-responder).

### Command-line options

```text
DotReticulum rnprobe - probe a Reticulum destination (experimental)

Usage:
  rnprobe [--config <dir>] [-s <bytes>] [-n <count>] [-t <seconds>] [-w <seconds>] [-v] <full-name> <destination-hash>
  rnprobe -h, --help
  rnprobe --version

Options:
  --config <dir>       Alternative Reticulum configuration directory
  -s, --size <bytes>   Probe payload size (default: 16)
  -n, --probes <count> Number of probes (default: 1)
  -t, --timeout <sec>  Timeout for path discovery and each probe (0 uses the default)
  -w, --wait <sec>     Delay between probes (default: 0)
  -v, --verbose        Show next-hop information
```

### Behaviour

`-t` applies both to path discovery and to each delivery receipt. The default path-discovery timeout is `max(12 seconds + first-hop timeout, medium-path timeout)`. `-v` can be repeated or combined to increase verbosity.

Each probe carries fresh random plaintext in a normal encrypted single-destination DATA packet, and delivery is confirmed by the standard signed packet receipt. An application-level reply protocol is not substituted for delivery proofs. On success the utility reports the round-trip time and hop count, and received PHY values when they are actually available. The summary reports sent and received counts and percentage loss.

The native receipt currently provides delivered state, RTT and route hop count. No RSSI, SNR or quality value is inferred from the interface type, and hardware readings do not apply when the bearer does not expose them.

| Status | Meaning |
|---|---|
| 0 | No loss |
| 1 | Path discovery timed out |
| 2 | Any loss |
| 3 | The encoded packet exceeds the MTU |

Missing required arguments print usage. A missing hash prints the pinned usage; a hash without a full name keeps the source diagnostic. Malformed names, hashes and configuration errors are diagnosed.

## Differences from upstream

- Exact Python terminal formatting and every upstream exit-code distinction for `rnstatus` are not established.
- `rnstatus` profiling is .NET-specific.
- `rnpath` path requests may repeat while waiting, where upstream sends one request.
- Unmeasured statistics are omitted rather than estimated.

## Verification

The reference provenance is recorded as source digests. They identify the pinned CLI behavior; they are not wire vectors.

| Source | SHA-256 |
|---|---|
| `rnstatus.py` | `259b187e6184c4a62fd824c83a6e0fa798bfcd0c165ec7c2f185751aeced3efd` |
| `rnpath.py` | `9674bdea46ed8c34fee3f4aafa9b675de51aa9e04bbee7cd27eb1f06f2c1ea2b` |
| `rnprobe.py` | `adfba42a976f7e85152b815f2b87b7602fa15500351b87e33b48ab17a0cdef34` |

`tests/DotReticulum.Applications.Tests/GenerateParity7NetworkUtilityVectors.py` runs each pinned Python utility's `--help` and records the exact help text and source-file digests in `tests/DotReticulum.Applications.Tests/Fixtures/parity7-network-utilities.json`.

The managed tests in `tests/DotReticulum.Applications.Tests/Parity7NetworkUtilitiesTests.cs` and the Pester process tests verify:

- captured option contracts, dispatch, help and version, and malformed input
- pre-runtime argument validation for `rnpath`
- local management operations
- authenticated remote status and path-table queries against both a native instance and the pinned Python transport node
- `rnprobe` against the pinned Python transport node: successful delivery with a proof, and delivery without a proof (reported as loss), with the Python destination callbacks observing both payloads
- reference-backed regression cases for the no-hash, repeated-verbosity and text-only blackhole-list `--json` branches

These are focused contract and interoperability tests, not proof of complete utility or platform parity. Remote-link error and teardown exit distinctions beyond the exercised cases remain unverified. Windows x64 and Linux x64 are exercised; macOS ARM64 has managed-suite coverage. Platform-specific utility limits are listed in [TODO.md](../TODO.md).
