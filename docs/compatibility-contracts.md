# Compatibility contracts and acceptance boundaries

This is a source-backed acceptance inventory, **not a second progress
checklist or a release certification**. [plan.md](../plan.md) is the only
progress checklist; [development history](../.github/development-history.md)
records commands, results and chronology. DotReticulum is experimental,
AI-assisted, independent of and not endorsed by the Reticulum project.

## Scope, provenance and classification

The audited .NET baseline is `d4eac24`, after Parity 7 software acceptance.
The integration results below identify the subsequently resolved boundaries;
the existence of a new test file alone is not execution evidence.
Parity 6 I2P recovery, physical interfaces and Android, and Parity 7 physical
maintenance acceptance were still open at this baseline.

During integration the parent implemented non-TCP discovery publication with
a separately generated bearer fixture. Discovery/factory/metadata tests,
existing host lifecycle and runtime statistics/shared focused selection passed
**70, skipped 1**, with zero warnings/errors. These are bounded software
results, not all-bearer live or full milestone acceptance.
Reference observations include Weave's runtime-null
channel/modulation defaults and a pinned KISS failure when numeric
`discovery_modulation` reaches `sanitize(int)`. Preserve the observed failure
or an explicit native diagnostic; do not invent a successful wire value.

The parent also identified and corrected a Parity 7 statistics regression:
`RuntimeStatistics` replaced bearer-reported `rxb`/`txb` with manager decoded
packet totals. This does not preserve TCP's framed-transmit accounting or the
unknown/null boundary for extensions without diagnostics. Bearer fields,
aggregates and rates are restored, decoded totals remain separately named,
and focused runtime/shared tests pass in the **70 passed / 1 skipped**
integration selection. This resolves the identified software regression within
that evidence; all-platform acceptance remains pending. The documented measurement boundary
was preserved, not changed to legitimize the substituted counters.

Packet-scoped PHY attribution was also implemented. Four-project
`Parity8PhyTests` selections passed **12, skipped 0**, with compilation green,
using independently generated `Parity8PhyVectors.json` from
`GenerateParity8PhyVectors.py` at the unchanged pin. This is bounded
reference/software evidence, not physical RF.

### Available-platform integration accepted (2026-10-25)

Current complete non-live suites pass **1847 / 87 skipped** on Windows x64 and
**1856 / 78 skipped** on Ubuntu WSL Linux x64, with zero failures and warning-free
builds. Windows merged production coverage is **82.96% lines / 69.97% branches**.
Both final NativeAOT publications pass warning-as-error gates, `--help`, actual
terminal-hosted shell, encrypted multi-megabyte resource and daemon tunnel-restart
flows. Full native Pester, including package/install/discovery/uninstall, passes
**79 / 2 skipped** on Windows and **81 / 0 skipped** on Linux. The latest selected
live/native xUnit runs pass **4** on Windows and **42** on Linux.
After the final Git compatibility correction, republished native binaries also
pass **82 / 0 skipped** Git hosting, signing and ordinary-helper process cases
on each platform, including Linux Git 2.53 clone/fetch/push and host restart.

The new managed-router mixed mesh crosses actual Python and .NET transports,
retransmits all nine deliberately lost resource parts, observes bounded bearer
pressure and recovers fresh links after a persisted .NET-router restart with
surviving Python peers. It is not an impaired native-binary mesh or Python-router
restart result. [Mesh acceptance](compatibility-mesh.md) discloses the public
`PROVE_ALL` peer policy and remaining topologies.

Integration also fixes atomic create-only publication, active Unix resource
leases, process stdin-close failure reporting and an actual native rnsh
early-version registration race. Git import checks preserve Git-owned temporary
refs while still performing full strict object validation through an isolated
verification view; Windows Git long paths are enabled for its nested files.
Installed aliases preserve the exact Reticulum
License; [installation](installation.md) records scope and refusal behavior.
The earlier focused/full-gate-pending wording in the baseline discovery rows
below does not negate these completed Windows/Linux integration gates. Full
all-platform/per-bearer feature acceptance, actual RF attribution and the named
missing providers remain open; no complete Parity 8 or release claim is made.
The development history records exact commands, failures, fixes and final gates.

The user subsequently conditionally authorized reflashing the RAK LoRa board
on COM8 **if necessary** for backlog acceptance. Subsequent actual published
native `rnodeconf --info` passed: RAK4631/SX1262, firmware 1.86, nRF52, Normal
mode, valid EEPROM checksum and **unverified signer**. Actual
`--eeprom-backup` saved **200 bytes** in session files, not source control.
These were read-only operations; no mutation, flash or RF occurred. The board
works and no necessity to reflash was established. Only one radio is available. References
below to absent mutation authorization describe the earlier audited baseline,
not a current blanket prohibition. The current boundary is unperformed device
acceptance, the parent's necessity/safe recovery assessment, and no second RF
peer—not simply missing permission. Conditional approval does not justify
gratuitous firmware changes or establish any hardware result.

macOS execution is explicitly deferred to user-requested GitHub Actions
**later**, not an assumed currently available Mac host. Its missing terminal
software and unverified platform flows remain separate gaps. This audit
created no workflows and does not authorize starting CI/CD.

The reference is the working checkout materialized from the Reticulum-hosted
repository `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`:
RNS 1.5.7, commit `a29bfb51c468945c96a1ee22cae0114e993e2391`.
GitHub `markqvist/Reticulum` is a comparison mirror, not the authority for a new
pin. Source references below are relative to this pinned checkout; line ranges
are inclusive. The audit read that checkout, the existing contract documents,
implementation and named tests, and the Parity 1–7 history records.
It did not build, test, publish, contact a device, regenerate vectors or change
the pin. It separately verified the actual clean pinned RNS import and LXMF
1.2.1 import with `tests\Interop\reference_environment.py`.

| Classification | Meaning here |
| --- | --- |
| **Implemented** | The precisely bounded behavior has native code and recorded evidence. This does not imply every malformed input, platform, topology or Python API is equivalent. |
| **Partial** | Some of the family works; named behavior is missing, deliberately restricted, or lacks the family's complete acceptance. An intentional difference stays visible. |
| **Missing** | The applicable reference behavior has no equivalent implementation. A clear rejection is preferable to silent success, but is not that behavior. |
| **Unverified** | Execution evidence is absent or insufficient, or the specified acceptance fails. This does not mean the code is absent. |
| **Not applicable** | Python implementation machinery, behavior absent from the pinned reference on that platform, or an explicitly out-of-scope extension. Associated wire/process behavior is not excluded. |
| **Upstream placeholder** | The pin itself declares but does not implement or enable the behavior. Observable parser/rejection behavior can still be required. |

These statuses describe contracts, not confidence scores. A family may have an
implemented valid-input slice and partial platform/failure coverage. Missing
software, failed acceptance, unavailable hardware and upstream placeholders
are deliberately separate.

Evidence shorthand in the tables:

- **V**: independently executed upstream vectors; inspect the owning
  `Generate*.py` and fixture provenance. Older fixtures retain
  `e40191b3d193b46b7f2d8a44424a594cd758839b`; they are not relabeled as 1.5.7.
- **U**: managed unit/loopback/simulator tests, not Python interoperability.
- **L**: actual pinned Python peer, with the stated roles and topology.
- **P**: separate-process Pester acceptance, with filesystem/output/exit effects.
- **N**: the published native executable ran that feature, not just `--help`.
- **H**: actual named hardware, distinct from a device simulator.

Historical results cited here are not reruns by this audit. The final Parity 7
record reports a warning-free Windows build, **1820 passed / 86 skipped**
non-live coverage collection, **82.80% lines / 69.66% branches** merged by
production source identity, Windows managed/native Pester **73 / 2 skipped**
each, and separately opted-in live/native flows. The Linux snapshot passed
the recorded **50-case** selection and native resource/shell flows; its full
Pester run first had one test-path portability failure, followed by a passing
six-case affected container, **not a rerun of the whole entry point**.
None of those numbers establishes full parity or current Parity 8 acceptance.

Raw checkout fingerprints verified in this audit:

| Source | SHA-256 of bytes read |
| --- | --- |
| `RNS\Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `RNS\Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `RNS\Discovery.py` | `ae26445adf6325ed3e47f2b7c4b412060994d75ae583a58162849507ed7a9950` |
| `setup.py` | `b9f232ec45eb4e1214c733496e684a0175719e0d03bc2fab5e25188aee3b5e4e` |

These identify source bytes, not publisher authentication. A clean Git checkout
can have CRLF on Windows and LF on Linux; do not silently substitute a Git-blob
hash for a historical raw-file hash. [AutoInterface](auto-interface.md) records
both forms explicitly.

Audit verification used these commands from the repository root (the external
path is the existing reference supplied to the audit, not an installation
instruction):

```powershell
$r = 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns'
git -C $r rev-parse HEAD
git -C $r status --short -- RNS setup.py Examples
$env:DOTRETICULUM_UPSTREAM = $r
$env:PYTHONPATH = "$r;$((Get-Location).Path)\tests\Interop"
$env:PYTHONDONTWRITEBYTECODE = '1'
& 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\refvenv\Scripts\python.exe' -B -c 'from reference_environment import verify_reference; verify_reference(require_lxmf=True); import RNS,LXMF,importlib.metadata; print("verified RNS="+RNS.__version__+" path="+RNS.__file__); print("verified LXMF="+importlib.metadata.version("lxmf")+" path="+LXMF.__file__)'
git --no-pager diff --check -- docs\compatibility-contracts.md
```

Results: exact pinned HEAD; no tracked/source/example changes reported;
reference verification exited 0, RNS 1.5.7 imported from that checkout and LXMF
1.2.1 from the isolated venv; whitespace check exited 0. A read-only local-link
check also found every relative document link exists and no progress-checkbox
markers in this document. No build/test/output-directory interference, fixture
regeneration, physical access or production implementation was part of this
audit.

## Source coverage and ownership

Every public module family in the pinned `RNS` tree, every installed utility
from `setup.py:175–190`, and all fourteen shipped examples is assigned below.
The Parity 1 history's configuration, malformed-input and public API ledgers
remain detailed source indexes, **not current implementation statuses**:
their missing/partial entries predate Parity 2–7. The rows here supersede those
statuses only to the extent supported by the later named evidence.

| Pinned source family | Native owner and contract location |
| --- | --- |
| `Identity.py`, `Destination.py`, `Packet.py`, `Cryptography\*` | Core/Crypto; endpoint, identity, announce, packet, receipt and crypto rows below |
| `Transport.py`, `Interfaces\Interface.py` | Transport/Core/Interfaces; routing, queues, IFAC, tunnels and bearer lifecycle |
| `Link.py`, `Resource.py`, `Channel.py`, `Buffer.py` | Transport; authenticated sessions, requests, resources and reliable streams |
| `Reticulum.py`, `Discovery.py`, `RNS\__init__.py` | Applications runtime/configuration/state/management/logging and discovery |
| All thirteen top-level concrete bearer files in `Interfaces` | Per-interface matrix below; TCP, Backbone and Local include initiating and accepted-child roles |
| `Interfaces\Android\SerialInterface.py`, `KISSInterface.py`, `RNodeInterface.py`; Weave's in-file Android branch | Mobile/provider row; not covered by desktop serial classes |
| `Interfaces\util\HDLC.py`, `TransmitBuffer.py`, `netinfo.py` | Framing, bounded successful-write accounting, device/address selection; Python epoll/netinfo layout is not an API requirement |
| `Utilities\rngit\{client,server,util,pages,highlight,media,commitsigs,main}.py` | Applications Git helper/client/host/signing/rendering; all rows below |
| `Utilities\rnsh\{args,rnsh,protocol,process,session,listener,initiator,loop,retry,helpers,exception}.py` | Applications rnsh protocol, authorization, terminal/process ownership and recovery |
| Remaining `Utilities\*.py` | Utility matrix, including `rnir`/`rnpkg` shells |
| `Resolver.py` | Upstream placeholder; no resolver protocol invented |
| `vendor\{umsgpack,configobj,validate,platformutils}.py`, `vendor\i2plib\*` | MessagePack domain, configuration/metadata validation, platform and SAM behavior; not the Python dependency implementation |
| Vendored AES/pure25519/SHA/HMAC/provider/proxy code; `CRNS\__init__.py` | Native primitive equivalents required; vendored source layout, Python imports/Cython loader/cache, interpreter checks and thread/decorator APIs **not applicable** |
| `_version.py`, package `__init__.py` files, `setup.py`, manuals and examples | Honest version/installation/discovery contracts and documented scenarios; Python package/export identity and upstream's “OS Independent” packaging classifier are not platform evidence |

### Crypto, endpoints and serialization

| Contract / pinned source | Status, native evidence and boundary |
| --- | --- |
| Ed25519/X25519, hash/random helpers, HKDF, AES/HMAC token; `Cryptography\{Ed25519,X25519,HKDF,Token,PKCS7,Hashes}.py`, `Identity.py:707–957` | **Implemented** valid-input slice, V/U/L: Crypto `RfcVectorTests`, `TokenTests`; Core `IdentityTests`; live encrypted packets, links and resources. **Partial intentional malformed-input compatibility**: strict low-order/noncanonical X25519/invalid Ed25519 rejection, strict authenticated PKCS#7, nonempty HKDF input and 8160-byte RFC ceiling. See [endpoints](endpoints.md). Do not weaken these to match permissive providers. |
| Raw identity public/private files, nonoverwrite/permissions, recall/app-data/use/retention; `Identity.py:101–349,625–687,791–799` | **Implemented** bounded V/U/P/N slices: `IdentityFileTests`, `Parity5EndpointKnownDestinationsTests`, `RuntimeStateStoreTests`, `Parity7Identity.Tests.ps1`. Raw keys preserve X25519 then Ed25519 order. Local public recall, retained expiry and persisted remote state are covered; Windows ACL protection is operator-owned. |
| Destination types/direction/names/key lifecycle/default link acceptance, registration and proofs; `Destination.py:96–198,320–435,544–692`, `Transport.py:2596–2606,2877–2980` | **Implemented** bounded V/U/L slices: `Parity5EndpointTests`, `Parity5LocalEndpointTests`, `Parity5RequestTests`. PLAIN passthrough, GROUP legacy/current token keys, SINGLE encrypted delivery, default accept-links, duplicate/direction validation, APP/ALL/NONE proofs and no-subscriber processing. Arbitrary Python object/callback identity **not applicable**; bounded native callbacks differ as disclosed below. |
| Announces, dynamic/default/per-call app data, ratchets/tag cache, recall; `Destination.py:206–318,437–542,678–690`, `Identity.py:382–612` | **Implemented** V/U/L slices: `AnnounceTests`, `DestinationRatchetsTests`, `Parity5EndpointRatchetTests`, `Parity5EndpointRuntimeRatchetTests`, `LiveAnnounceTests`. Signed reload/rotation/restart and selected-ratchet metadata are explicit. **Intentional differences**: cache-hit ratchet context bug fixed; reload failure preserves usable keys and fails rather than silently resetting. Cooperating native writer lease is not Python/.NET concurrent-writer coordination. |
| Packet headers/flags/context/hash/MTU/hops, send/resend orchestration and receipt eligibility; `Packet.py:123–389`, `Transport.py:1352–1605` | **Implemented** bounded V/U/L slices: `PacketTests`, `Parity4ReceiptTests`, `Parity5ChannelRetryCryptoTests`; codec remains in Core, send/prove/resend in Transport. Reserved-bit/hash rules, authenticated fresh-IV Channel retries and receipt lookup are distinct contracts. Exact acceptance of malformed peer packets is intentionally hardened. |
| Generic application MessagePack and file-response metadata; `vendor\umsgpack.py:283–535,646–805`, `Link.py:473–510,804–918` | **Partial**, explicit supported native value domain with V/U: `MsgPackApplicationValueTests`, `Parity5RequestTests`. Extensions/timestamps, numeric/binary key equality and strict UTF-8/bounds implemented; Python extension registries, tuple/null/nonfinite map keys, old-spec/permissive decoding and float32-writing options are not provided. Lossless request/response values are opt-in; legacy metadata binary keys project to hex and collisions fail. [Application values](application-values.md) defines accepted/rejected values, not arbitrary Python serialization parity. |
| Packet-scoped RSSI/SNR/quality, shared PHY cache and opt-in Link tracking; `Transport.py:1913–1935`, `Packet.py:370–389`, `Reticulum.py:1918–1959`, `Link.py:540–570,777–793` | **Implemented bounded software** V/U in four projects' `Parity8PhyTests`, **12 passed / 0 skipped**; independent `GenerateParity8PhyVectors.py`/`Parity8PhyVectors.json`. Frame-time readings, interleaved peers/Multi radios, reconnect/reset snapshots, bounded per-field cache/eviction, authenticated owner lookup, shared-client opt-in, Link/receipt snapshots and nonreporting/transit absence are tested. Full live/native and physical RF attribution remain **unverified**. |

### Routing, runtime and transfers

| Contract / pinned source | Status, native evidence and boundary |
| --- | --- |
| Routing/filtering/forward/reverse proofs, shared-client hops, path health/gravity/expiry and link-table rebalance; `Transport.py:1624–1680,1913–2761,2983–3322` | **Implemented** bounded V/U/L/N slices: `Parity4RoutingTests`, `Parity4RoutingTrafficTests`, `Parity4TransportLiveTests`, `LiveMixedTransportTests`, `Parity4NativeTransportTests`. Actual Python **transport** and leaf roles, authenticated proof effects, conflicting/expired paths and disabled-transport shared forwarding are separate from healthy leaf-only loopback. Full bearer/topology/soak acceptance remains **unverified**. |
| Announce/path-request rates, modes, bandwidth caps, recursive/internal controls, held/inflight work and saturation; `Transport.py:46–162,1448–1591,1753–1895,3279–3599`, `Interfaces\Interface.py:189–432` | **Implemented** bounded V/U/L slices: `Parity4RoutingTrafficTests`, `Parity4RoutingConfigurationTests`, `Parity6OutgoingTests`; priority transport queues coexist with the manager's bounded backpressure channel. Per-interface/origin/egress behavior, concurrent request targets and inherited child controls are covered, not identical Python scheduling under arbitrary load. |
| Packet receipts/conclusion/limits/timeouts, resource-proof cache queries; `Packet.py:417–587`, `Transport.py:739–762,2764–3124`, `Resource.py:653–676,765–777` | **Implemented** V/U/L slices: `Parity4ReceiptTests`, `Parity4ReceiptCacheTests`, `Parity4SnapshotCapacityTests`. Forced final resource-proof recovery is not a general replay cache. Implicit **link** proofs are disabled upstream and are not an omitted feature. |
| IFAC derivation/masking/authentication, bearer MTU/bitrate and negotiated link payloads; `Reticulum.py:1221–1269`, `Interfaces\Interface.py:251–265`, `Link.py:148–228,512–644` | **Implemented** bounded V/U/L slices: `Parity4IfacTests`, `Parity4IfacConfigurationTests`, `Parity4LinkMtuTests`, `Parity5TransferLiveTests`. Legacy IFAC HKDF expansion is scoped separately from strict public HKDF. Native ceiling is 524288; actual TCP mixed-path evidence covers 1024/16384, **not all MTUs/bearers** or upstream's entire representable MTU range. |
| Authenticated transport tunnels, dormant state/restoration/expiry; `Transport.py:2764–2874,3879–3950` | **Implemented** V/U/L/N slices: `Parity4TunnelTests`, `Parity4NativeTransportTests`, `RuntimeStateStoreTests`. Stable transport/interface identity and valid signed synthesis required; no unauthenticated peer rebinding. Tunnels do not resume encrypted links, commands or resources after restart. |
| Config discovery/creation, aliases/enables/defaults, policy and diagnostics; `Reticulum.py:215–345,461–730,843–1207`, ConfigObj | **Partial intentional syntax/default policy** with V/U/P: `RnsdConfigurationParityTests`, `Parity4RoutingConfigurationTests`, `Parity6InterfaceConfigurationTests`, `Parity3Configuration.Tests.ps1`. Supported key families are indexed below. Unknown/ineffective settings fail, native default does not enable multicast, invalid RPC keys fail instead of fallback, arbitrary ConfigObj interpolation/multiline runtime config is not accepted. A source key accepted elsewhere is not proof its native runtime effect is implemented. |
| Shared election/fallback, Local HDLC, mutual-authenticated local RPC and identified remote management; `Reticulum.py:297–305,355–451,1366–1496`, `LocalInterface.py:53–494` | **Implemented** bounded V/U/L/P/N slices: `SharedRuntimeTests`, `SharedLiveTests`, `SharedHostIntegrationTests`, `SharedRuntime.Tests.ps1`. Both Python owner/client roles and restart/persistence; remote `/status` and `/path` are allowlisted/read-only. Linux abstract owner/client process evidence is separate from the TCP Python adapter. Android execution and a full abstract-AF_UNIX Python role matrix remain **unverified**. |
| Attach/detach/reload/extensions/failure ownership; `Reticulum.py:771–841,1185–1207,1971–1996` | **Implemented native equivalent** U/P/N: `RuntimeOperationsTests`, `RuntimeDiscoveryHostTests`, manager/lifecycle/shared tests. Named bearer reload, no-subscriber processing and child isolation; global policy is not hot-reloaded. Native static `InterfaceFactoryRegistry` replaces dynamic Python execution. Failed replacement does not restore old bearer. Source `panic_on_interface_error` is explicitly rejected; native top-level versus child ownership is fixed. |
| Known destinations/ratchets/paths/hashlists/cache/tunnel persistence and cleanup; `Identity.py:101–335,410–513`, `Reticulum.py:1316–1357`, `Transport.py:3745–3969` | **Implemented** bounded V/U/P/N slices: `RuntimeStateStoreTests`, `LifecycleStateTests`, `Parity4TunnelTests`, `Parity5ResourceStorageTests`, `Parity5Resources.Tests.ps1`. Public state uses one checksummed atomic native generation; interchange schemas are available but the runtime directory is **not drop-in Python storage**. Signed native ratchet metadata differs from the upstream key-only file; neither checksum nor raw state hash authenticates a publisher. No persistent encrypted-session/resource resume. |
| Logging/rotation/timestamps/service/interactive/shutdown and housekeeping; `RNS\__init__.py:58–160`, `Reticulum.py:157–206,351–397,461–472`, `Utilities\rnsd.py:40–110` | **Implemented native equivalent** V/U/P/N slices: `RnsdConfigurationParityTests`, `RnsdInteractiveTests`, CLI/shared Pester. Levels 0–8 and rotation, configured graceful persistence; bounded management shell instead of Python REPL. Windows Ctrl+C, Unix SIGTERM and daemon process death are distinct scenarios; not all console/service managers or signals have evidence. |
| Statistics, profiling and formatting; `Reticulum.py:1498–1969`, `Transport.py:591–676`, `RNS\__init__.py:168–349,357–615,641–664` | **Partial family; identified counter regression resolved in bounded software**: `RuntimeOperationsTests.StatisticsPreserveActualFramedTcpBytesAndSampleBearerRatherThanDecodedBitRates`, measured/unknown-extension and child-aggregate tests, network/shared tests pass within the **70 passed / 1 skipped** focused selection. Diagnostics supply bearer counters, TCP TX counts framed bytes, unknown extension counters remain null, and decoded totals stay separately named. Native profiler and Local pending-write bytes are not Python profiler/TX-buffer semantics. Exact all-output/exit and full integration gates remain **unverified**. |
| Discovery parsing/stamps/encryption/source/age/capacity/bootstrap/autoconnect; `Discovery.py:43–78,290–650,652–879` | **Implemented** bounded V/U/L/P slices: `DiscoveryTests`, `DiscoveryMetadataTests`, `DiscoveryLiveTests`, `RuntimeDiscoveryHostTests`. Actual Backbone autoconnect on Linux, bounded TCP/HDLC fallback elsewhere. Only Backbone autoconnect is applicable at this pin; I2P autoconnect and Yggdrasil/Tor availability detection are upstream placeholders. |
| Discovery publication and reload lifecycle; `Discovery.py:136–258`, `Reticulum.py:988–1026` | **Implemented bounded software** for TCPServer/Backbone plus connectable I2P, RNode, Weave, KISS and KISS-framed TCPClient. `DiscoveryTests.BearerPublicationMatchesExecutedReferenceAndMetadataDoesNotTreatRadioAsTcp`, rejection/pinned-KISS-defect test, factory/metadata and existing host lifecycle tests pass in the **70 passed / 1 skipped** focused selection. Independent `GenerateDiscoveryVectors.py --bearer-output` fixture records source-backed schemas/defaults. Numeric KISS modulation's pinned failure is explicitly diagnosed, not repaired by invented wire values. All-bearer live/native/RF publication and full gates remain **unverified**. |
| Blackhole identities/publication/update/persistence and endpoint rejection; `Reticulum.py:1998–2041`, `Discovery.py:883–978` | **Implemented** bounded V/U/L/P slices: `BlackholeTests`, `BlackholeLiveTests`, network Pester. Expiry/reason/source provenance, failed-update preservation and identified endpoint/path teardown covered. Anonymous datagrams cannot supply an authenticated sender identity; sender-based rejection there is **not applicable**, not secretly implemented. |
| Link handshake/mode/proofs/RTT/keepalive/stale/close/identification/policies; `Link.py:73–228,336–704,929–1306` | **Implemented** bounded V/U/L/N slices: `LinkVectorTests`, `LinkTests`, `LiveLinkTests`, `Parity5RequestTests`, mixed transport/native resources/rnsh. Only AES256-CBC is enabled at the pin; named AES128/GCM/OTP/PQ link modes are **upstream-disabled/placeholders**. Opt-in PHY tracking now has focused software evidence above, not physical/live certification. Exact all-state impaired/long-running platform matrix is **unverified**. |
| Requests/responses/receipts/progress/limits and owned callback work; `Link.py:473–510,804–918,1324–1498`, `Destination.py:320–390` | **Implemented** bounded V/U/L: `Parity5RequestTests`, `Parity5TransferLiveTests`. Preinstalled limits, small/resource responses, denial/list/identified policies, cancellation and core settlement without observers. Explicit bounded handlers/queues and overflow diagnostics are intentional; arbitrary blocking synchronous user callbacks cannot be force-cancelled. |
| Resources, metadata/compression/hashes/parts/HMU/proofs/queue/cancel and multipart file storage; `Resource.py:169–514,576–1123,1138–1379` | **Implemented** bounded V/U/L/P/N: `Parity5ResourceTests`, `Parity5ResourceStorageTests`, `Parity5TransferLiveTests`, `NativeResourceTests`, resource/rncp Pester. Seekable/forward-only input, spooling, mandatory teardown joins, malformed metadata/decompression bounds and next-queue release covered. Explicit size/concurrency/storage limits differ from upstream. Cross-link/restart arbitrary-offset resume is **not applicable**: no such pinned protocol exists. Source-generated XOR resource fixtures do not prove encryption. |
| Channel envelope/order/window/retry/receipts/exhaustion; `Channel.py:113–148,175–245,303–608` | **Implemented** bounded V/U/L and rnsh N: `ChannelResilienceTests`, `Parity5ChannelTests`, `Parity5ChannelRetryCryptoTests`, mixed transfers/rnsh. Wrap/reorder/RTT boundaries have independent reference evidence, not live 65536-message wrap-under-loss evidence. Retries preserve envelope/sequence but use fresh encryption/hash/receipt. Prolonged impaired soak is **unverified**. |
| Buffer stream IDs/compression/EOF/flow/closure/callbacks; `Buffer.py:44–379` | **Implemented native equivalent** V/U/L and rnsh N: `Parity5ChannelBufferTests`, mixed transfer/rnsh. Bounded slow-reader failure, pending-read settlement and concurrent close/write covered. Native blocking async reads, internal-handler precedence, bounded callbacks and 15-second close deadline intentionally differ from Python's nonblocking stream API and RTT-based close. |

The linked [endpoints](endpoints.md), [transport controls](transport-controls.md),
[runtime operations](runtime-operations.md), [runtime state](runtime-state.md),
[resources](resources.md) and [Channel/Buffer](channel-buffer.md) specify the
exact local bounds and ownership decisions. Their earlier “coordinator must
run” wording is not fresh failure evidence; consult the later history result.
Conversely, a passed gate cannot erase an explicit remaining boundary.

### Configuration reader families

This index prevents constructor-only or silently ignored controls from being
mistaken for full configuration coverage. The detailed key/default ledger is
in the Parity 1 history, with effective current settings in [rnsd](rnsd.md) and
the per-bearer documents. For a new or changed key, acceptance is omission,
valid nondefault, invalid type, boundary and conflicting-alias observations
from the pin, followed by the actual runtime effect and diagnosed failure.

| Reader family / source | Classification and acceptance boundary |
| --- | --- |
| Sharing, identity, transport/management/probe/MTU/gravity/implicit-proof policy; `Reticulum.py:263–327,478–588` | **Implemented supported settings / partial intentional differences**: explicit native validation, fixed failure isolation rather than `panic_on_interface_error`, no Python REPL. This is not arbitrary constructor/API argument compatibility. |
| Discovery/network identity/blackhole/autoconnect; `Reticulum.py:590–657,988–1026` | **Implemented supported bounded software**, including non-TCP publication's independent fixture/focused tests; all-bearer live/full gates pending. Parse success alone is insufficient. Executable metadata is explicitly authorized and bounded, not implicit config execution. |
| Default/per-interface AR/IC/EC/gravity and four transport queue sizes; `Reticulum.py:659–730,843–984`, `Interface.py:72–96`, `Transport.py:134–162` | **Implemented bounded equivalent**. Check control effect, inheritance and saturation, not only normalized values. Manager wait-on-full queue and transport priority queues are different boundaries. |
| Enable aliases ORed, mode aliases/precedence, outgoing/recursive/internal/bootstrap/IFAC/bitrate; `Reticulum.py:843–984,1029–1182` | **Implemented bounded equivalent**, including source-exact known-path/direct-send exceptions to `outgoing=no`; strict unknown-mode rejection and retained native aliases are explicit differences. |
| Logging levels/timestamps/verbosity/destination; `Reticulum.py:232–245,306–315,460–472` | **Implemented** documented native process contract; configured versus absent level and service precedence matter. |
| UDP/TCP/Serial/Auto/Backbone/Pipe/Local/KISS/AX25/RNode/Multi/I2P/Weave readers | **Implemented desktop supported subsets / partial platform coverage**, individually classified below. Nested Multi radios and generic post-init inheritance are not inferred from a standalone constructor. Local endpoints are runtime-created, not a fictitious user interface type. |
| Android serial/USB/Bluetooth/BLE selectors; `Interfaces\Android\*.py`, Weave Android branches | **Missing adapters/host**, desktop parser rejection is not a native Android equivalent. RNodeMulti on Android is explicitly unsupported upstream. |
| Rngit helper client config/environment/aliases/batch/logging; `rngit\client.py:55–76,130–213,679–718` | **Implemented supported flat schema / partial ConfigObj syntax**, `RngitClientConfigurationTests`; zero batch is bounded rather than copying a nonprogress loop. |
| Rngit node/group access/blocked/statistics/pages/templates/mirror; `rngit\server.py:1979–2250,4644–4910` | **Implemented supported schema / partial optional rendering and platform coverage**, `RngitHostConfigurationTests`; executable resolvers/templates and external Git sources require authorization. |
| rnid metadata/spec and authorization files for rncp/rnx/rnsh | **Implemented documented domains / partial intentional limits**: `Parity7RnidTests`, transfer/rnsh tests; metadata built-in validators/interpolation are not arbitrary Python validators, path-jail syntax is not a symlink sandbox, current-user `~` is supported but Unix `~user` lookup is not. |

## Per-interface evidence matrix

All rows include inherited IFAC/mode/bitrate/traffic policy only where wired
through the manager/runtime. A framing oracle, serial simulator or equivalent
stream does not establish the corresponding device/provider.

| Interface / pinned source | Native software and actual recorded execution | Remaining classification |
| --- | --- | --- |
| UDP; `UDPInterface.py:40–115` | **Implemented** V/U/L/P: independent RX/TX, hostname/device/address selection, exact port precedence; actual Python UDP both directions; Windows/Linux boundary tests. `Parity6UdpTests`, `LiveInteropTests`, [UDP](udp-interface.md). | **Unverified** impaired UDP multihop, physical-network/device changes and macOS native flows. Upstream primarily IPv4 semantics are not a reason to claim arbitrary IPv6 parity. |
| TCP initiating/listener/accepted peer; `TCPInterface.py:75–110,181–410,490–551` | **Implemented** V/U/L/P/N bounded HDLC, reconnect/retry/fixed MTU/IPv6/device/KISS software; independent peers, client failure isolation. `Parity6StreamTests`, `TcpPacketInterfaceTests`, live and Pester. | **Unverified** actual Python TCP/KISS option flow and every OS socket option. **Intentional differences**: default 64-peer cap, native DNS address fallback/effective timeout, strict malformed HDLC; standalone reconnect opt-in. [Streams](stream-interfaces.md). |
| Serial HDLC; `SerialInterface.py:63–217` | **Implemented software** V/U: 9600/8N1 options, idle reset, asserted DTR/RTS, owned reopen and bounded serialized frames; `SerialPacketInterfaceTests`, `Parity6StreamTests`. | **Unverified H** actual generic serial formats/driver cancellation/unplug/replug and pinned Python peer over physical serial. COM8 RNode identification is not these tests. |
| Auto; `AutoInterface.py:44–747` | **Implemented** V/U/L: actual Linux namespaces/veth IPv6 multicast/raw/IFAC, wrong-key rejection, peer expiry; deterministic disappearance/reappearance. `Parity6AutoLiveTests`, multicast tests, `invoke-auto-topology.ps1`; [Auto](auto-interface.md). | **Unverified** actual Windows/macOS/Android multicast/device transitions and native per-target bearer flow. Group tokens are not authentication; IFAC is separate. Native startup-selected device recovery is deliberate hardening, not dynamic discovery of all later devices. |
| Backbone initiating/listener/accepted peer; `BackboneInterface.py:42–183,333–767,789–1121` | **Implemented software and Linux L**: actual Python advertisement/autoconnect/encrypted link; shared ingress, partial writes, zero/interrupted/would-block/stall/hysteresis and flap tests. `Parity6BackboneTests`, shared-ingress tests; [Backbone/Pipe](backbone-pipe.md). | Windows/macOS direct Backbone **not applicable** at pin (Linux/Android gate); compatible HDLC fallback is distinct. **Unverified** Android and native Backbone-specific impaired feature execution. Hardware MTU 1048576 does not imply every link can exceed the native link ceiling. |
| Local initiating/listener/accepted shared client; `LocalInterface.py:44–494` | **Implemented** V/U/L/P/N TCP shared roles; Linux native abstract owner/client process flow. Decoded receive accounting, keepalive and simulated Android sleep policy corrected. `LocalSharedPacketInterfaceParityTests`, shared tests; [platforms](interface-platforms.md). | **Unverified** complete Python/native abstract Unix role/restart/statistics matrix, real Android 3-second keepalive/12-second pause/resume. Linux/Android abstract endpoints are distinct from filesystem Unix sockets; macOS/Windows use TCP. |
| Pipe; `PipeInterface.py:44–204` | **Implemented** V/U/P/N Windows and Linux process flows, argv/no-shell, child EOF/nonzero/respawn/cancel/cleanup. `Parity6PipeTests`, interface Pester. | **Unverified** actual pinned-Python/RNS exchange through Pipe and broader OS process launch. Malformed/overlong rejection intentionally differs from upstream prefix truncation. |
| KISS and AX25KISS; `KISSInterface.py:38–396`, `AX25KISSInterface.py:38–411` | **Implemented software** V/U TNC controls/READY/beacons, source quirks, AX.25 addressing/strip, bounded flows/reconnect; `Parity6KissTests`; [KISS](kiss-interfaces.md). | **Unverified L/H** authorized TNC, actual modulation/RF and physical recovery. A USB descriptor is not device authorization. Android provider missing separately. |
| RNode serial/TCP and Multi shared radios; `RNodeInterface.py:135–1257,1452–1587`, `RNodeMultiInterface.py:148–1164` | **Implemented software** V/U serial/TCP control/data/echo/firmware/telemetry/display/reset, vports/shared credit/inheritance and recovery; `Parity6RNodeTests`, configuration tests. **H read-only**: initial COM8 detection plus subsequent actual native maintenance information identifies RAK4631/SX1262, firmware 1.86/nRF52/Normal and valid EEPROM checksum; actual 200-byte backup retained outside source. Signer remains unverified. [RNode](rnode-interfaces.md). | **Unverified H/L** full startup/data/RF/unplug/Multi simultaneous radios/physical TCP adapter. Only one radio available. **Missing desktop BLE provider** below, not just Android. Necessary reflashing is conditionally authorized but not needed or performed; no EEPROM mutation or RF evidence. |
| RNode desktop BLE; `RNodeInterface.py:174–186,385–400,1241–1450` | **Missing** applicable `ble://` selector/provider: source implements Nordic UART GATT via optional Bleak, scan/name/address/bond matching (including Windows paired-address filter), notify/write chunking/reconnect. Native `RNodeDevice` explicitly rejects BLE URIs; accepting a caller-supplied stream is not this provider. | Software adapter and failure tests required before physical BLE acceptance; do not call this an upstream placeholder or an Android-only gap. |
| I2P; `I2PInterface.py:76–330,322–728,729–762`, `vendor\i2plib\*` | **Partial** V/U/L: SAM destination/session/persistence/framing, actual i2pd streams and both Python roles; simulator restarted persisted current/legacy destinations. `Parity6I2pTests`, live/reference-recovery tests, topology runner; [I2P](i2p-interface.md). | **Failed actual-router restart acceptance** on i2pd and Java I2P remains **unverified**, not hardware-only. Readiness/STREAM STATUS OK/same b32 do not prove replacement-child DATA. Separate-router overlay and published-native I2P also unverified. |
| Weave WDCL/endpoint peers; `WeaveInterface.py:37–326,329–640,699–1102` | **Implemented software** V/U signed switch handshake, endpoint peers/via/dedup/expiry, display/telemetry and bounded shared queue/reconnect; `Parity6WeaveTests`; [Weave](weave-interface.md). | **Unverified L/H** actual Weave device, Python peer, physical reconnect and Android USB. Type-6 encapsulation has no source processing implementation: **upstream placeholder**, not omitted invented functionality. |
| Android Serial/KISS/RNode; `Android\SerialInterface.py:63–289`, `KISSInterface.py:73–448`, `RNodeInterface.py:119–552,623–750,1526–2007`; Weave `71–100,162–224` | **Missing** installable native host, permission/lifecycle wiring and USB/RFCOMM/GATT providers. Desktop `net10.0` streams, Android policy branches or a desktop build are not providers. Detailed contract and requirements in [platforms](interface-platforms.md). | **Unverified H** device/service/background/network/USB/Bluetooth execution after implementation. Android Multi is **not applicable/upstream explicitly unsupported**, not an adapter to invent. |

## Utility and Git contract matrix

These cover all fourteen installed entry points, including nested command
families. Native branding, explicit authorization and bounded I/O are deliberate
differences, not exact Python help/spinner/REPL compatibility.

| Command / pinned source | Classification and concrete evidence |
| --- | --- |
| `rnsd`; `Utilities\rnsd.py:40–110` | **Implemented documented native equivalent** V/U/P/N for config/example/version/service/logging/interactive/graceful persistence; parser exit 2/runtime 1/success 0. Python REPL and service-manager installation are **not applicable equivalents** of its native management shell/file-logging flag. |
| `rnid`; `rnid.py` import/export/metadata/sign/encrypt/main | **Implemented bounded** V/U/L/P/N: `Parity7RnidTests`, identity Pester; >16 MiB RFE both Python directions, lookup/retained restart/announce, detached/current/legacy/embedded/wrapped signatures/base256, explicit bounded editor and metadata validators. Raw/embedded and metadata bounds, native binary stdin/stdout extensions and exact supported exits in [rnid](rnid.md). Arbitrary ConfigObj/custom-validator runtime is not claimed. |
| `rnstatus`; `rnstatus.py:159–959` | **Partial** U/L/P/N accepted software options: local/remote stats, JSON/filter/sort/monitor/link/profiler/interface/discovery controls, measured child totals/rates/bursts/pressure. `Parity7NetworkUtilitiesTests`, network Pester; new focused runtime/shared results resolve the bearer-counter regression without redefining measurements. PHY now has bounded four-project software evidence; physical/live attribution and exact full human output/exit distinctions remain unverified. |
| `rnpath`; `rnpath.py:43–528` | **Implemented bounded / partial exact-output coverage** U/L/P/N table/rates/filter/drop/void/via/request/blackholes, authenticated remote read tables. Remote mutation/path-request branches are **upstream unimplemented** (`136,210,229,384,394,415,436`), not missing native remote administration. Native path wait may repeat requests unlike the CLI's initial-send-then-wait. |
| `rnprobe`; `rnprobe.py:42–249` | **Implemented bounded / partial PHY coverage** U/L/P/N encrypted packet/signed receipt RTT/hops/loss and 0/1/2/3 outcomes; actual Python proof/no-proof destination callbacks observed. `Parity7NetworkUtilitiesReference.py`, network Pester. Physical RSSI/SNR/Q not inferred from TCP or substituted application replies. [Network utilities](network-utilities.md). |
| `rncp`; `rncp.py` listen/send/fetch/main | **Implemented bounded** V/U/L/P/N send/fetch both Python roles, 3–5 MB P0 evidence and later streamed multi-MB native tests; file collision/overwrite/jail/allow precedence/writable-output/progress policies. Concurrent native in-process publication fixed, **partial filesystem race guarantee** against unrelated Unix writers; documented lexical jail is not a symlink sandbox. Windows reference move shim limits unmodified-upstream claims. |
| `rnx`; `rnx.py` listen/execute_received_command/execute/main | **Implemented native equivalent** V/U/L/P/N explicit listener authorization, binary streams/limits/negative slicing, failed spawn, timeout/cancel/process cleanup and exit mapping. Bounded execution/concurrency differs from upstream; no implicit shell. `-x` is a command loop, **not rnsh or a PTY**. [Transfers](rncp-rnx.md). |
| `rnsh`; `rnsh\protocol.py:46–148`, `args.py:47–104`, `session.py:88–425`, `initiator.py:206–472`, `process.py:371–704` | **Implemented Windows/Linux slices / partial platform acceptance** V/U/L/P/N both Python roles, binary streams, loss/retry, PTY resize, eight pipe/terminal combinations, exact mode restoration, native terminal-hosted initiator, failed spawn/cancel/new-session recovery. Actual Python process backend runs on Linux/WSL, not a fake Windows Python replacement. **Missing macOS native terminal backend** at baseline, not simply untested. EOF reference race is a bounded negative result, not successful EOF completion. [rnsh](rnsh.md). |
| `rnodeconf`; `rnodeconf.py:715–1433,1480–1547,1599–4495` | **Implemented software / partial read-only physical evidence** V/U/P/N/H: complete parser/command/model/flasher plans and bounded maintenance/provisioning/recovery simulators; `RnodeconfTests`, `RnodeconfFirmwareTests`, device Pester. Actual published-native COM8 `--info` and 200-byte `--eeprom-backup` pass; RAK4631/SX1262 firmware 1.86/nRF52/Normal, checksum valid, signer unverified. Mutation/reset/reenumeration/persistence/RF remain unverified. Conditional necessary reflash approval exists, but the working board has not needed or received a flash; no real flasher or firmware download ran. External flashers remain dependencies. [Maintenance](rnodeconf.md). |
| `rnir`, `rnpkg`; `rnir.py:40–72`, `rnpkg.py:39–83`, `Resolver.py:31–34` | **Upstream placeholders** for resolver/package functionality; **implemented native startup/parser shells** V/U/P/N. Real native no-interface initialization/disposal is tested; source constructor-capture vectors prove only arguments. No resolver wire/name-service/package manager is invented. [Placeholders](upstream-placeholders.md). |
| `git-remote-rns`; `rngit\client.py:55–76,130–213,335–718`, server list/fetch/push `2869–3140` | **Implemented bounded SHA-1** V/U/L/P/N ordinary Git capability/option/ref/HEAD/list/batched fetch/incremental/no-change/Unicode/push/force/delete/per-ref status, local ref ownership, explicit malformed/unknown rejection and deadlines; `GitRemoteRnsTests`, `RngitPushTests`, `RngitClientConfigurationTests`, Git Pester. **Partial object-format/platform coverage**: helper rejects SHA-256, does not advertise negotiation, upstream list has no empty-repo format field; direct bare client is separate. Lazy connection/error exit differs from upstream. |
| `rngit` direct clone/fetch; client/list/fetch and link/file-response contracts | **Implemented bounded** V/U/L/P/N bare direct client and historical actual hosted-source incremental retrieval, strict fsck/object/ref/tree validation with disclosed seed and nonzero bundle bytes. Buffered application bundles/list/response caps are not arbitrary-repository streaming support. Detached remote HEAD and custom direct URL/CLI restrictions are explicit in [Git](rngit.md). |
| `rngit` node/config/identity/announce/block/stats/access/admin; `server.py:139–285,1979–2703,4443–4910` | **Implemented bounded** V/U/L/P/N `RngitHostTests`, configuration/statistics/process/native-host tests. Raw persisted identity, aliases, `.allowed` union/inheritance/none/all/admin/hidden-vs-denied, bounded requests/jobs, restart/corruption/concurrent mutation. Explicitly authorized executables replace automatic resolver execution; native directory state is not an all-files Python transaction. |
| `rngit` create/fork/mirror/sync/HEAD; `server.py:568–717,2097–2172,2784–2838,3141–3366` | **Implemented bounded** V/U/L/P/N `RngitRepositoryStoreTests`, HTTP/process/native-host tests; native RNS all communication, authorized external Git sources, cleanup/concurrency/persisted provenance and mirror/fork HEAD differences. External SSH/HTTP tooling success and platform-specific failures beyond exercised cases remain **unverified**. |
| `rngit` releases/artifacts/manifests/latest/offline signer; `server.py:761–1238,3367–3787` | **Implemented bounded** V/U/L/P/N `RngitReleaseTests`, native hosting/Pester. Multi-step drafts are not atomic transactions; interrupted drafts/restart/permission/tamper cases covered. Embedded key validity is not publisher trust. |
| `rngit` work documents/scopes/comments/permissions; `server.py:1346–1841,3789–4642` | **Implemented bounded** V/U/L/P/N `RngitWorkServiceTests`, native hosting/Pester. Active/completed/proposed/all, signed content, IDs, inheritance/admin/restart/concurrency. Document authorization is not inherited implicitly from client-side display. |
| `rngit` NomadNet pages/downloads/Markdown/Micron/templates; `pages.py:250–312,366–2041,2288–2320,2931–3007`, `util.py:130–818`, `highlight.py` | **Partial** V/U/L/P/N default rendering/page routes and bounded authorized templates/editors implemented; `RngitPageTests`, `RngitTemplatesTests`, media/templates Pester. Optional Pygments highlighting and wcwidth-enhanced layout are **missing equivalents**, not proven by fallback output; no Python dependency implementation required. Unicode/escaping/pagination/binary/download paths covered only by the named corpus. |
| `rngit` still-image/WebP media; `media.py:57–215` | **Implemented supported backend selection / partial installed-backend evidence** V/U/P/N `RngitMediaTests`; actual Windows FFmpeg, capture/failure tests for magick/convert/gm/avconv. Absence/failure can retain original as source does. No evidence for every encoder/OS/version; audio/video feature expansion is **not applicable**. |
| `rngcs`; `commitsigs.py:48–331` | **Implemented bounded** V/U/L/P/N `RngcsTests`, signer Pester: SSHSIG/RSG, sign/find-principals/check-novalidate/verify, real commit/tag and author/tagger checks. Allowed-signers database is not implemented by the pinned signer either; structural checking is not cryptographic verification or publisher discovery. |

### Supported extension scope

Existing LXMF and telemetry are regressions, not permission to expand RNS
parity. `LxMessage`, `LxmRouter`, stamp/vector and live suites support
opportunistic/direct/Resource delivery both directions with Python LXMRouter.
Real `lxmd` evidence is separately **.NET-to-lxmd**, with its maximum accepted
transfer size raised. **Partial/outside this RNS completion scope**: LXMF
propagation synchronization, tickets, paper delivery and delivery-endpoint
ratchets are absent; real lxmd reverse origination is not established merely
by delivery to the daemon. [LXMF](lxmf.md) preserves these limits.
Telemetry is **implemented .NET-only**, U/process/direct-and-.NET-hop evidence,
not a pinned RNS wire protocol or a substitute for upstream management.
Absent audio/video extensions and new LXMF functionality are **not applicable**
to this inventory's implementation authorization.

## Shipped example scenarios

Python example filenames are scenarios, not a requirement to copy executables
or invent a bundled application. The source anchors below are at the same pin.

| Example / source | Classification and evidence route |
| --- | --- |
| `Minimal.py:18–67` | **Implemented bounded** runtime startup/identity/announce/dispose via node/shared/announce tests and daemon processes. |
| `Announce.py:23–138` | **Implemented bounded** app data/aspect handler/ratchets/path-response/cache and callback isolation, announce/endpoint/live suites. |
| `Broadcast.py:17–72` | **Implemented native endpoint slice / unverified exact live example topology**: plain endpoint and router tests do not themselves prove a real mixed-peer broadcast scenario. |
| `Echo.py:25–260` | **Implemented bounded** encrypted DATA/proof policies/no-subscriber and mixed Python transport datagrams; new packet PHY software tests are not physical/live Echo PHY evidence. |
| `Ratchets.py:24–269` | **Implemented bounded** enforced ratchet/rotation/signed reload and live announce encryption; live restart/expiry under impaired topology remains **unverified**. |
| `Link.py:27–234`, `Identify.py:27–257` | **Implemented bounded** both link roles, identification/proof/RTT/close and malformed/observer tests; not every timer/platform/failure permutation live. |
| `Request.py:26–230` | **Implemented bounded** small/resource/streamed response, receipt/progress/deny/timeout/size/cancel via request and actual Python transfer suites. |
| `Resource.py:28–238` | **Implemented bounded** random/compressible multipart/metadata/cancel/reject/proof/queue/storage, separate encrypted live/native evidence. |
| `Filetransfer.py:44–521` | **Partial scenario evidence**: generic registered list-request and streamed file responses exist; rncp send/fetch is not this example's interactive list/select file server. No claim of running that exact mixed example application. |
| `Channel.py:60–333`, `Buffer.py:35–266` | **Implemented bounded** custom explicit message factories, full-window/retry/order/stream/EOF via Channel/Buffer/mixed/rnsh; live long-wrap/slow-reader soak remains **unverified**. |
| `Speedtest.py:36–282` | **Unverified sustained metrics scenario**; live multi-MB throughput observations exist, not a prolonged independently measured byte/rate/loss/MTU sweep. Python performance identity is not required. |
| `ExampleInterface.py:62–293` | **Implemented native equivalent** statically registered factory/declared settings/lifecycle/IFAC/mode/attach/reload tests; dynamic Python file import/exec intentionally **not applicable**. |

## OS, RID and platform evidence

Each row is a separate target. A Windows result, WSL Linux snapshot, RID
declaration or successful build does not validate another target or every
interface. `net10.0` is the library TFM, not an Android/iOS application.

| OS / architecture / deployment | Recorded baseline evidence | Classification and measurable remaining acceptance |
| --- | --- | --- |
| Windows x64 / `win-x64` | Managed suite, fresh merged coverage, native warnings-as-errors publish, Pester real processes, actual Python desktop roles, native rnid/rncp/rnx/Git/management/resource/rnsh terminal flows; one read-only COM8 RNode. | **Implemented bounded desktop feature set**, **partial release acceptance**: per-interface physical/BLE/I2P and broader impaired/native meshes still open. Unix-only SIGTERM/abstract-socket skips are honest, not Windows failures or Unix successes. |
| Ubuntu Linux x64 under WSL / `linux-x64` | Warning-free managed build; native warnings-as-errors publish/help/resource/rnsh; selected actual Python/shared/network/process tests, real Auto namespaces/multicast and Backbone. P7 full Linux Pester then targeted portability rerun caveat above. | **Implemented bounded Linux slices**, **partial release acceptance**: no whole Linux final-gate rerun after every later integration, all native bearers/Git installed flows or arbitrary distributions/kernel/physical adapters established. WSL virtual networking is not bare-metal radio evidence. |
| macOS arm64 / `osx-arm64` | No recorded managed/native feature execution. Auto/Darwin socket branches and source support are code, not evidence. User defers Mac execution to GitHub Actions later; no Mac host is assumed available now and no workflow was created. | **Deferred/unverified platform**, plus **missing native rnsh terminal backend** (Linux ABI guard). Require actual macOS build/test/publish and protocol/CLI/install/PTY feature execution later. Windows/Linux success cannot close it. |
| macOS x64 / `osx-x64`; Windows arm64 / `win-arm64` | No target-specific feature evidence. | **Unverified**; separate actual host/toolchain/native dependencies/terminal/bearer tests before any support claim. `osx-arm64` results would not automatically certify x64, or conversely. |
| Linux ARM64/ARM / `linux-arm64`, `linux-arm` | No architecture-specific feature evidence. | **Unverified** managed/runtime/native availability and feature flows independently; crypto/native bzip2/serial/terminal/Git/OS ABI dependencies must be exercised. |
| Linux RISC-V | No verified runtime, SDK RID/native toolchain or feature flow in this repository. | **Unverified target**, not a claimed supported NativeAOT RID; establish a viable .NET target/toolchain before promising publish/run commands. |
| FreeBSD | No verified target/toolchain/feature execution; Python's non-Linux paths do not establish .NET deployment. | **Unverified target**, including native dependency/socket/terminal behavior. Do not equate Linux with BSD or invent a shipped SDK NativeAOT RID. |
| Android | Policy branches and audited source requirements only; no installable Android host/provider/on-device execution. | **Missing host and USB/Bluetooth providers; unverified platform execution**, including network-only adapters, foreground/background lifecycle, permission revoke/restore and power transitions. Upstream Multi explicitly unsupported. |
| iOS | No pinned iOS-specific adapter or native application target/execution. | Pinned dedicated iOS adapter **not applicable** (absent), project iOS deployment **unverified/missing host**. iOS signing/AOT/sandbox/background/network policy requires a separate viable host and authorized target; do not invent RNS radio APIs. |

## Adapters, shims and what they do not prove

`tests\Interop\reference_environment.py` checks the actual RNS import under the
selected checkout, exact SHA and clean tracked `RNS` sources. When LXMF is used
it checks distribution import path and version 1.2.1. Tests must opt in or
explicitly skip with a reason when the required peer/platform is absent.
Adapters drive real upstream APIs; their existence does not make every
underlying CLI/UI/backend path unmodified execution.

| Evidence adapter / setting | Disclosure and claim boundary |
| --- | --- |
| `Interop\rncp_win_shim.py` | Windows replaces upstream move of an open resource with copy; wire/resource behavior still uses Python, but **unmodified upstream Windows receive/filesystem behavior is not verified**. |
| `Interop\live_rngit_client.py` | Windows closes received resource immediately before original retention callback moves it; actual upstream client/handlers retained. Not unmodified Windows file-lifetime behavior. |
| Resource vector generators | Frozen randomness and reversible XOR replacing link encryption; exact segmentation/compression/hashmap/state evidence only. Live/native encrypted resources and retry-crypto vectors independently supply crypto evidence. |
| Channel window vectors versus retry-crypto vectors | Synthetic outlet/clock/receipts prove state. Separate real Packet/Link/Token/Transport retry path uses an installed synthetic session key/capture sink: fresh ciphertext/hash/receipt proven, **not handshake/network** by that fixture. |
| Auto/Backbone/Pipe/Stream/KISS/RNode/Weave/I2P generators | Controlled clock/socket/serial/thread/epoll/queue/existence adapters or `__new__` avoid real devices/processes. Execute actual reference methods; Linux constants/fake one-tick jobs and capture-only writes are disclosed in generators. Not actual epoll, RF, device startup, USB permissions or router acceptance. |
| Runtime/config/routing/state/blackhole/placeholder generators | Synthetic owner/interface/clock, captured packet sink/constructor or source-extracted actual parser/planner isolate decisions. Not real startup/restart/network merely because reference code executed. Placeholder constructor capture does not prove Python daemon initialization. |
| Git page/template/repository/work fixtures | Static-template/executable `.allowed` classification overrides on Windows, frozen generation time/stats, synthetic node/job context and explicit Git Unicode settings; see generator disclosures. Default-rendering oracle, not every installed optional backend or unmodified Windows executable-file semantics. |
| Rnodeconf command/planner/signing generators | Captured serial/tools/filesystem/sleeps and original AST-extracted flasher planner; ephemeral independent RSA public verification; no private signing key retained as hardware identity. Not a real flasher/download/EEPROM device. |
| `Interop\live_rnsh_node.py` and POSIX peer | Real Linux reference sessions/protocol, client driver bypasses interactive upstream UI. Pinned EOF `waitpid` race is reproduced, bounded and canceled, not patched into success. Windows native uses ConPTY, not a fake Python POSIX backend. |
| Actual I2P topology | Explicit opt-in public network and authenticated router provisioning; i2pd `[sam] singlethread=false` is nondefault, not an RNS patch. Separate zero-hop/readiness-only diagnostics and Java alternative are not successful restarted DATA. No hidden lease/tunnel readiness shim. |
| Real lxmd | Nondefault accepted transfer limit raised for large resources; delivery-endpoint acceptance is separate from reverse origination/propagation synchronization. |

Fixtures may use public deterministic test keys for reproducibility; these are
not publisher identities. A simulator's agreement with our framing is not a
second independent Python node. Reference-side source remains clean; that fact
alone does not negate the explicitly disclosed in-process adapters.

## Known residual contracts and measurable acceptance

These are acceptance definitions, **not another checklist**. They distinguish
work executable with existing software access from hardware/platform blockers.
The parent milestone may complete an honest inventory while the product
remains partial; it must not check off full compatibility merely because every
gap now has a row.

| Residual contract at audited baseline | Minimum closure evidence / blocker |
| --- | --- |
| Non-TCP discovery publication — **implemented bounded software; full/live gates pending** | Independent bearer fixture and named discovery/factory/metadata/host lifecycle cases pass within **70 passed / 1 skipped**. Preserve source-exact I2P/radio/KISS fields, Weave null defaults and diagnosed numeric-KISS failure. Remaining acceptance: actual pinned receivers and published runtime publication/reload/detach across the supported bearers; simulator success is not RF evidence. |
| Packet/Link/shared PHY attribution — **implemented bounded software; full/live/hardware gates pending** | Four-project **12 passed / 0 skipped** selection covers frame-time atomic attribution, independent vector cache semantics, peers/Multi separation, reset/reconnect, malformed rejection, authenticated/shared lookup and opt-in Link/receipt snapshots. Preserve nonreporting TCP/transit absence. Remaining acceptance: complete integration/native flows and actual radio readings with correct packet identity, not a later device-global sample. |
| Bearer statistics boundary — **identified regression resolved in focused software evidence** | Restored device diagnostics, parent-only aggregates/rates and separately named decoded totals pass runtime/network/shared selection (**70 passed / 1 skipped**). Named escaped-TCP and unknown-extension tests retain source boundaries; full final integration/coverage/native gates remain pending. This fixes the regression rather than legitimizing decoded substitutions. |
| Desktop RNode BLE — **missing software/provider** | Implement an explicit owned native/.NET GATT adapter with source name/address/service selection, Windows bonded filter, Nordic UART notify/write-characteristic chunk limits, cancellation/timeouts/disconnect/reconnect and bounded queues. Independent API-level simulated oracle/failure cases first; then named OS/Bluetooth adapter/bonded device DATA both ways. A supplied stream alone is not the selector/GATT implementation. |
| macOS rnsh terminal — **missing platform software** | Replace Linux-specific libc/termios/ioctl/PTY assumptions with a source-backed Darwin backend/native equivalent; actual macOS child pipe/PTY combinations, raw-mode restore after cancellation/failure, resize/EOF/binary data, process-tree termination and published terminal-hosted Python session. macOS host/toolchain access required for verification, not for accurately classifying absence. |
| I2P persisted-bearer restart — **failed actual-router acceptance** | Against an actual named router, exchange initial packets both ways, close server/control/children, reopen using unchanged persisted private destination, require a fresh accepted child and new packet bytes both ways, keep unrelated peers working, and bound retry/cleanup. Repeat current and legacy destination forms and both Python roles. Record router configuration and exact failure phase; SAM readiness alone never passes. Existing failures on i2pd/Java are not solved by a simulator or by longer deadlines. |
| Native OS/release/install/topology acceptance — **unverified**, some platform software absent above | Actual Windows/Linux/macOS managed/native selected feature flows and clean-directory installed utility discovery/version/permissions/errors; real Python leaf **and transport** nodes, both initiators, encrypted multi-MB random/compressible traffic, bounded latency/loss/resource-part or proof retries, interface reconnect and router/daemon restart. Assert byte hashes, actual drops/retries, bounded queues, terminal cleanup, settled requests/streams and fresh recovery, not just reachable routes. No cross-restart encrypted-session continuation claim. |
| Per-bearer mixed/hardware matrix — **unverified execution** | For each claimed bearer, actual pinned Python DATA/announce/link/resource where applicable in both roles and target OS; record frame/IFAC/MTU constraints, child isolation and failure recovery. For Serial/KISS/AX25/RNode/Multi/Weave require authorized named device, firmware/adapter settings, legal RF parameters, counterpart and unplug/replug evidence. One read-only RNode and simulators cannot clear these. |
| Physical maintenance — **read-only native information/backup verified; mutation/RF unverified** | Actual COM8 native `--info` identifies RAK4631/SX1262, firmware 1.86/nRF52/Normal, valid checksum but unverified signer; native `--eeprom-backup` saved 200 bytes in session files. No flash/mutation/RF occurred. The working board establishes no necessity to use conditional reflash approval. Reset/reenumeration/persistence/interrupted-write recovery still require appropriately scoped safe execution/evidence; one radio/no second RF peer still limits over-air acceptance. |
| Android — **missing host/providers and unverified execution** | Deliver viable native host/package, USB serial/RFCOMM/GATT/network equivalents, manifest/permission/error/service ownership and on-device policy tests; denied/revoked permission, device/network disappear/reappear, background pause/resume and clean shutdown while sibling paths work. No device, host or grants currently establish acceptance. |
| Optional highlighting/width and installed encoders — **partial software/evidence** | Compare source output with Pygments/wcwidth present if equivalent enhanced rendering is claimed, or retain explicit unsupported optional mode; real selected backend versions must convert/malformed-fail/timeout/cleanup on each claimed OS. Default fallback does not prove enhanced behavior; external encoder installation is a dependency, not a reason to claim all backends. |
| Helper SHA-256 / detached HEAD / arbitrary-repository sizing — **partial limitations** | Keep SHA-1-only rejection/protocol capability honest; independently exercise actual upstream and Git object-format/list/bundle/empty-remote behavior before claiming wider helper support. Keep direct client's separate behavior. Detached-HEAD list representability and bounded buffered responses need explicit support or precise diagnosed restriction; do not infer full support from fsck of one repository. |
| Exact utility text/exit, sustained metrics and example-specific flows — **partial/unverified evidence** | Differentially execute remaining consumed output/remote teardown/exit cases, physical PHY fields, and actual list/select file-server/plain-broadcast scenarios if those claims are made. Run a bounded repeat-transfer/Channel soak with independent byte/rate/loss observations, capacity high-watermarks and full cleanup; no arbitrary throughput target or claim of Python performance identity. |
| Other OS/architecture targets — **unverified feasibility/execution** | Establish actual SDK/runtime/native dependency/toolchain and host availability before per-target publish/run; test crypto, compression, sockets, serial/PTY and utility dependencies. Absence of a pinned iOS adapter or Android Multi is not permission to invent an upstream protocol. |

### Intentional differences that remain visible after passing acceptance

The following are not hidden pending implementations: explicit start/dispose
and cancellation; Core codec separated from routing; static AOT-safe factories
and message values instead of Python dynamic imports/reflection; strict crypto
and diagnosed malformed input; bounded queues/peers/resources/requests and
observer overload; no optional observer required for core processing; explicit
remote-execution/editor/template/discovery/firmware/trust authorization; native
branding and documented CLI streams/statuses; no Python REPL; native atomic
state/spool formats and owner-only Unix creation with operator-owned Windows
ACLs; asynchronous .NET stream semantics and native process ownership.

Bounds are observable compatibility differences, not proof of unlimited
upstream acceptance. Mandatory cancellation/resource cleanup is joined;
arbitrary noncooperating user code/drivers cannot be forcibly made safe.
The ratchet cache-hit context fix, strict token padding/key validation, ConfigObj
syntax/default restrictions, fixed failure-isolation policy, hard-link-backed
atomic create-only publication, pinned rnsh EOF/exit quirks and parser-specific
output/exit differences stay documented even after valid flows pass.

## Source integrity, signatures and publisher trust

The historical native hosted-source fetch proved missing objects were
retrieved over the .NET stack, with a disclosed seed, nonzero received bundle,
recorded refs/commit/tree and strict Git object checks. It did **not** prove
that the endpoint or locator was controlled by the intended publisher.
Content-addressed Git consistency detects changed objects relative to their
hashes; a checksum, transport connection, signed announce, embedded key or
valid self-contained release signature alone does not authenticate a publisher.

The published maintainer release identity is
`bc7291552be7a58f361522990465165c`. A trusted-release claim requires an
independently obtained and validated binding to that identity, then actual
manifest/artifact/commit/tag verification as applicable, including tampering,
wrong signer, missing key/signature and author/tagger mismatch outcomes.
`rngcs check-novalidate` is only structural, Git allowed-signers is not the
pinned signer's trust database, and selecting `--signer` is a caller policy,
not authenticated key discovery. This audit verifies neither a new release
signature nor publisher ownership.

Preserve the Reticulum License and notices; the experimental implementation
must not be presented as official, production-certified, fully interoperable,
or relicensed. Parity 8 inventory completion is not CI/CD authorization and
does not resolve the explicitly missing software or external acceptance gates.
