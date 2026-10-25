# `rnstatus`, `rnpath` and `rnprobe`

The reference contract is Reticulum 1.5.7 at
`a29bfb51c468945c96a1ee22cae0114e993e2391`. The implementation is experimental
and AI-assisted; these utilities do not imply complete Reticulum parity.
Source references below are relative to `RNS/Utilities/` in that checkout.

## `rnstatus`

`rnstatus.py:159-870, 873-959` obtains interface statistics either from a
required shared instance (`--config`) or from an identified remote transport
(`-R` and identity file `-i`). The remote destination is
`rnstransport.remote.management`; `/status` accepts `[link_stats, profiling]`.
The implementation uses the existing bounded local management RPC and the
identified remote management client.

Implemented command paths include interface statistics and JSON output,
name/burst filters, supported sorting, monitor refresh/cancellation,
interface attach/detach/reload requests, link counts, discovery-record listing
and details, and JSON-safe serialization without reflection-based serialization.
Discovery reads the runtime's persisted `DiscoveryService` records. Text
discovery honors upstream name/stale/unknown filters; JSON discovery follows
the reference behavior and emits the unfiltered snapshot. Discovered-interface
config templates are reconstructed from the validated packed announcement;
they do not start the advertised interface.

Native status reports actual decoded packet-boundary byte totals and sampled
per-interface/transport bit rates, announce and path-request rates and counts,
accepted-packet/send packet rates, queue depth/drop/pressure, and packet-filter
rejections. With announce statistics enabled it also reports observed queued
and held announces; positive packet-filter, protocol, IFAC, blocked-IP, and
bearer TX queue/drop measurements are shown when present. Aggregate control
traffic includes spawned-peer observations, while byte and packet totals use
the manager's parent-rolled counters once. Bit-rate samples are measured over consecutive status snapshots;
the first snapshot establishes a baseline and has no rate value. Path and
announce frequency samples use the bounded router observation windows.
Ingress-burst active state and activation timestamps are sourced from the
runtime controls; spawned-peer aggregates include currently active child
bursts. Upstream TX-buffered bytes, stalled state and TX drop counters are
emitted only when a bearer supplies those measurements (currently the Backbone
peer endpoint); local shared TCP writes instead report the actual bytes in an
incomplete `NetworkStream.WriteAsync` as `.NET tx_write_pending_bytes`. This is
not the upstream transmit-buffer size or a drop count, and no local queue/drop
policy is invented. Blocked-IP counts are likewise included only when the
bearer reports a measured count. Gravity, IFAC identity, and announce-rate
limits are shown from configured runtime values. Physical radio readings,
hardware speed samples and bearer framing overhead remain unavailable unless
exposed by the device.
Profiling is .NET-specific and is labeled accordingly, not claimed as the
upstream profiler. Exact Python terminal formatting and every upstream
exit-code distinction are not established. Discovered-interface
details/config output is rendered from the currently persisted discovery
schema; bearer configuration differences remain platform-specific. See
[runtime operations](runtime-operations.md) for measured-field boundaries.

## `rnpath`

`rnpath.py:43-94, 94-478, 481-528` supports path-table display, maximum-hop
filtering, announce-rate history, individual path drops, queued-announce
clearing, path drops via a transport, path requests, and blackhole list/add/
remove plus published-list requests. The implementation uses existing local
management operations, the authorized remote `/path` table/rates API, and the
native blackhole-store/list request path. It does not emulate remote mutations
that the pinned management service does not implement; these are explicitly
reported as unsupported.

Hashes are exactly 32 hexadecimal characters. Table JSON converts binary hash
values to lowercase, delimiter-free hex; human path rows use live interface and
expiry data. Rate rows use stored announce timestamps and violation/block
fields. A path request uses the node's cancellation-aware path wait, which may
repeat requests while waiting (the upstream CLI sends an initial request and
then waits). Missing paths and unsuccessful drops are errors; empty unfiltered
tables remain valid output. Blackhole text lists support filtering; pinned
`--json` output is for path/rate tables, not blackhole lists. CLI help/version
and pre-runtime argument validation have focused tests. Empty remote table
responses are successful empty results, not authentication failures.
When both `--table` and `--rates` are supplied, the path table takes precedence
as in the pinned CLI.

The authenticated remote table/rates implementation is the pinned management
request shape. `-R`-selected path drops, announce-queue changes, via drops and
blackhole management remain unsupported exactly where the upstream CLI marks
them unimplemented. Mixed-process tests exercise authenticated remote status
and path-table requests against both a native instance and the pinned Python
transport node; remote-link error/teardown exit distinctions beyond those
exercised cases remain unverified.

## `rnprobe`

`rnprobe.py:42-206, 209-249` accepts a full dotted destination name and a
32-character destination hash. `-s/--size` defaults to a 16-byte plaintext;
`-n/--probes` defaults to one; `-t/--timeout` applies to path discovery and
each delivery receipt; `-w/--wait` is the interval between sends; `-v` adds
route information; `--config` selects configuration. Path discovery defaults
to `max(12 seconds + first-hop timeout, medium-path timeout)`. Each probe uses
fresh random plaintext, a normal encrypted single-destination DATA packet and
the standard signed packet receipt. Success reports delivered RTT, hop count
and (when actually available) received PHY values. The summary reports sent,
received and percentage loss. Source exits 0 for zero loss, 2 for any loss, 3
when the encoded packet exceeds MTU, and 1 when path discovery times out.
Missing required arguments print usage; malformed name/hash and configuration
errors are diagnosed.

The native packet/receipt path is used; an application-level reply protocol is
not substituted for packet delivery proofs. Mixed-process tests against the
pinned Python transport node verify successful delivery with a proof and
delivery without a proof (reported as loss), and verify the Python destination
callbacks observed both payloads. The native receipt currently provides
delivered state, RTT and route hop count; no RSSI/SNR/quality value is inferred
from interface type. Hardware readings are not applicable when the bearer does
not expose them. Missing hash prints pinned usage; a supplied hash without a
full name retains the source diagnostic. Repeated and combined `-v` options
increase verbosity, and the no-hash, repeated-verbosity and text-only
blackhole-list `--json` branches have reference-backed regression tests.

## Reference provenance and verification boundary

Pinned source SHA-256 digests:

| Source | SHA-256 |
|---|---|
| `rnstatus.py` | `259b187e6184c4a62fd824c83a6e0fa798bfcd0c165ec7c2f185751aeced3efd` |
| `rnpath.py` | `9674bdea46ed8c34fee3f4aafa9b675de51aa9e04bbee7cd27eb1f06f2c1ea2b` |
| `rnprobe.py` | `adfba42a976f7e85152b815f2b87b7602fa15500351b87e33b48ab17a0cdef34` |

The source digests identify the pinned CLI behavior; they are not wire vectors.
`tests/DotReticulum.Applications.Tests/GenerateParity7NetworkUtilityVectors.py`
executes each pinned Python utility's `--help` and records the exact captured
help text and source-file digests in
`tests/DotReticulum.Applications.Tests/Fixtures/parity7-network-utilities.json`.
The managed and Pester suites verify the captured option contract, dispatch,
help/version and malformed-input behavior. Pester exercises local management
RPC and utility operations from separate processes, authenticated remote
status/path queries against native and pinned Python transport nodes, and
successful/lost probes in both proof-policy cases. The current native Pester
run reports all seven network-utility cases passing; the exact test command,
broader suite results, and NativeAOT evidence are recorded by the parent in
`.github/development-history.md`. These results are focused interop evidence,
not proof of complete utility or platform parity.
