# Compatibility contracts

DotReticulum is an experimental, AI-assisted, independent implementation; it is not an official or endorsed Reticulum implementation. Here, “compatible” means that a specifically described behavior has native implementation and evidence at the stated boundary. It does not imply complete API, platform, malformed-input, topology, or feature parity.

The current live reference is Reticulum (RNS) 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`; the LXMF reference is 1.2.1. Older vectors retain their original provenance at `e40191b3d193b46b7f2d8a44424a594cd758839b`. Source paths and line ranges in the tables refer to the pinned upstream checkout. Test and generator names are repository paths or test class names; evidence is limited to the behavior those artifacts actually exercise.

## Status vocabulary and evidence

| Status | Meaning |
| --- | --- |
| **Implemented** | Native code and evidence cover the specifically stated behavior. This is not a claim that every input, platform, topology, or API is equivalent. |
| **Partial** | Some behavior is implemented, while named behavior is missing, deliberately restricted, or not fully accepted. Differences and bounds remain stated. |
| **Not implemented** | The applicable reference behavior has no native equivalent. An explicit rejection is not equivalent behavior. |
| **Unverified** | Available evidence does not establish the specified behavior; this does not assert that code is absent. |
| **Not applicable** | The behavior is absent from the pinned reference, is Python-specific implementation machinery, or is outside the stated scope. Associated wire/process behavior is not excluded. |
| **Upstream placeholder** | The pinned reference declares but does not implement or enable the behavior. Observable parser and rejection behavior can still be in scope. |

Statuses describe individual contracts, not confidence scores. A family can have an implemented valid-input behavior and partial failure, platform, or topology coverage. Missing implementation, failed acceptance, unavailable hardware, and upstream placeholders are distinct.

Evidence shorthand used in tables:

- **V**: independently executed upstream vectors; see the named `Generate*.py` script and fixture provenance.
- **U**: managed unit, loopback, or simulator tests; not Python interoperability.
- **L**: actual pinned Python peer in the stated roles and topology.
- **P**: separate-process Pester acceptance, including process/filesystem effects.
- **N**: the published native executable ran the feature, not only `--help`.
- **H**: named physical hardware, distinct from a device simulator.

## Source coverage and ownership


Every public module family in the pinned `RNS` tree, every installed utility
from `setup.py:175–190`, and all fourteen shipped examples is assigned below.
The rows classify the present native behavior against the pinned source;
source presence or a parser accepting a setting is not evidence that its
runtime effect is implemented.

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
| Ed25519/X25519, hash/random helpers, HKDF, AES/HMAC token; `Cryptography\{Ed25519,X25519,HKDF,Token,PKCS7,Hashes}.py`, `Identity.py:707–957` | **Implemented** valid-input behavior, V/U/L: Crypto `RfcVectorTests`, `TokenTests`; Core `IdentityTests`; live encrypted packets, links and resources. **Partial intentional malformed-input compatibility**: strict low-order/noncanonical X25519/invalid Ed25519 rejection, strict authenticated PKCS#7, nonempty HKDF input and 8160-byte RFC ceiling. See [endpoints](endpoints.md). Do not weaken these to match permissive providers. |
| Raw identity public/private files, nonoverwrite/permissions, recall/app-data/use/retention; `Identity.py:101–349,625–687,791–799` | **Implemented** bounded V/U/P/N coverage: `IdentityFileTests`, `Parity5EndpointKnownDestinationsTests`, `RuntimeStateStoreTests`, `Parity7Identity.Tests.ps1`. Raw keys preserve X25519 then Ed25519 order. Local public recall, retained expiry and persisted remote state are covered; Windows ACL protection is operator-owned. |
| Destination types/direction/names/key lifecycle/default link acceptance, registration and proofs; `Destination.py:96–198,320–435,544–692`, `Transport.py:2596–2606,2877–2980` | **Implemented** bounded V/U/L coverage: `Parity5EndpointTests`, `Parity5LocalEndpointTests`, `Parity5RequestTests`. PLAIN passthrough, GROUP legacy/current token keys, SINGLE encrypted delivery, default accept-links, duplicate/direction validation, APP/ALL/NONE proofs and no-subscriber processing. Arbitrary Python object/callback identity **not applicable**; bounded native callbacks differ as disclosed below. |
| Announces, dynamic/default/per-call app data, ratchets/tag cache, recall; `Destination.py:206–318,437–542,678–690`, `Identity.py:382–612` | **Implemented** V/U/L coverage: `AnnounceTests`, `DestinationRatchetsTests`, `Parity5EndpointRatchetTests`, `Parity5EndpointRuntimeRatchetTests`, `LiveAnnounceTests`. Signed reload/rotation/restart and selected-ratchet metadata are explicit. **Intentional differences**: cache-hit ratchet context bug fixed; reload failure preserves usable keys and fails rather than silently resetting. Cooperating native writer lease is not Python/.NET concurrent-writer coordination. |
| Packet headers/flags/context/hash/MTU/hops, send/resend orchestration and receipt eligibility; `Packet.py:123–389`, `Transport.py:1352–1605` | **Implemented** bounded V/U/L coverage: `PacketTests`, `Parity4ReceiptTests`, `Parity5ChannelRetryCryptoTests`; codec remains in Core, send/prove/resend in Transport. Reserved-bit/hash rules, authenticated fresh-IV Channel retries and receipt lookup are distinct contracts. Exact acceptance of malformed peer packets is intentionally hardened. |
| Generic application MessagePack and file-response metadata; `vendor\umsgpack.py:283–535,646–805`, `Link.py:473–510,804–918` | **Partial**, explicit supported native value domain with V/U: `MsgPackApplicationValueTests`, `Parity5RequestTests`. Extensions/timestamps, numeric/binary key equality and strict UTF-8/bounds implemented; Python extension registries, tuple/null/nonfinite map keys, old-spec/permissive decoding and float32-writing options are not provided. Lossless request/response values are opt-in; legacy metadata binary keys project to hex and collisions fail. [Application values](application-values.md) defines accepted/rejected values, not arbitrary Python serialization parity. |
| Packet-scoped RSSI/SNR/quality, shared PHY cache and opt-in Link tracking; `Transport.py:1913–1935`, `Packet.py:370–389`, `Reticulum.py:1918–1959`, `Link.py:540–570,777–793` | **Implemented bounded software** V/U in four projects' `Parity8PhyTests`,; independent `GenerateParity8PhyVectors.py`/`Parity8PhyVectors.json`. Frame-time readings, interleaved peers/Multi radios, reconnect/reset snapshots, bounded per-field cache/eviction, authenticated owner lookup, shared-client opt-in, Link/receipt snapshots and nonreporting/transit absence are tested. Full live/native and physical RF attribution remain **unverified**. |

### Routing, runtime and transfers

| Contract / pinned source | Status, native evidence and boundary |
| --- | --- |
| Routing/filtering/forward/reverse proofs, shared-client hops, path health/gravity/expiry and link-table rebalance; `Transport.py:1624–1680,1913–2761,2983–3322` | **Implemented** bounded V/U/L/N coverage: `Parity4RoutingTests`, `Parity4RoutingTrafficTests`, `Parity4TransportLiveTests`, `LiveMixedTransportTests`, `Parity4NativeTransportTests`. Actual Python **transport** and leaf roles, authenticated proof effects, conflicting/expired paths and disabled-transport shared forwarding are separate from healthy leaf-only loopback. Full bearer/topology/soak acceptance remains **unverified**. |
| Announce/path-request rates, modes, bandwidth caps, recursive/internal controls, held/inflight work and saturation; `Transport.py:46–162,1448–1591,1753–1895,3279–3599`, `Interfaces\Interface.py:189–432` | **Implemented** bounded V/U/L coverage: `Parity4RoutingTrafficTests`, `Parity4RoutingConfigurationTests`, `Parity6OutgoingTests`; priority transport queues coexist with the manager's bounded backpressure channel. Per-interface/origin/egress behavior, concurrent request targets and inherited child controls are covered, not identical Python scheduling under arbitrary load. |
| Packet receipts/conclusion/limits/timeouts, resource-proof cache queries; `Packet.py:417–587`, `Transport.py:739–762,2764–3124`, `Resource.py:653–676,765–777` | **Implemented** V/U/L coverage: `Parity4ReceiptTests`, `Parity4ReceiptCacheTests`, `Parity4SnapshotCapacityTests`. Forced final resource-proof recovery is not a general replay cache. Implicit **link** proofs are disabled upstream and are not an omitted feature. |
| IFAC derivation/masking/authentication, bearer MTU/bitrate and negotiated link payloads; `Reticulum.py:1221–1269`, `Interfaces\Interface.py:251–265`, `Link.py:148–228,512–644` | **Implemented** bounded V/U/L coverage: `Parity4IfacTests`, `Parity4IfacConfigurationTests`, `Parity4LinkMtuTests`, `Parity5TransferLiveTests`. Legacy IFAC HKDF expansion is scoped separately from strict public HKDF. Native ceiling is 524288; actual TCP mixed-path evidence covers 1024/16384, **not all MTUs/bearers** or upstream's entire representable MTU range. |
| Authenticated transport tunnels, dormant state/restoration/expiry; `Transport.py:2764–2874,3879–3950` | **Implemented** V/U/L/N coverage: `Parity4TunnelTests`, `Parity4NativeTransportTests`, `RuntimeStateStoreTests`. Stable transport/interface identity and valid signed synthesis required; no unauthenticated peer rebinding. Tunnels do not resume encrypted links, commands or resources after restart. |
| Mixed Python/.NET transport crash recovery; `Transport.py:319–327`, `Interfaces/TCPInterface.py:271–300,410–435` | **Bounded L** `Parity8CompatibilityLiveTests`: force-killed actual pinned Python transport interrupts an active bidirectionally proved link; observed TCP offline and .NET child detachment remove its forwarding entry, and an attempted send obtains no delivery proof or peer receipt. Both surviving endpoints explicitly close old links before recovery. The actual transport reloads its persisted identity; observed TCP online and peer-count transitions precede fresh path discovery, a distinct new link, proved DATA and exact hashed impaired resources both ways. Loss follows each resource's initial complete four-part measured window; all 15 dropped parts must retry. A separate actual all-upstream negative control proves cold first-window cancellation before repair, not passing cold recovery. Existing endpoint `PROVE_ALL` policy is retained. This is managed .NET transport integration, not shared Compatibility, automatic endpoint timeout/cleanup, active-link resumption, alternate-route selection or sustained/native/per-bearer soak. [Mesh boundaries](compatibility-mesh.md). |
| Config discovery/creation, aliases/enables/defaults, policy and diagnostics; `Reticulum.py:215–345,461–730,843–1207`, ConfigObj | **Partial intentional syntax/default policy** with V/U/P: `RnsdConfigurationParityTests`, `Parity4RoutingConfigurationTests`, `Parity6InterfaceConfigurationTests`, `Parity3Configuration.Tests.ps1`. Supported key families are indexed below. Unknown/ineffective settings fail, native default does not enable multicast, invalid RPC keys fail instead of fallback, arbitrary ConfigObj interpolation/multiline runtime config is not accepted. A source key accepted elsewhere is not proof its native runtime effect is implemented. |
| Shared election/fallback, Local HDLC, mutual-authenticated local RPC and identified remote management; `Reticulum.py:297–305,355–451,1366–1496`, `LocalInterface.py:53–494` | **Implemented** bounded V/U/L/P/N coverage: `SharedRuntimeTests`, `SharedLiveTests`, `SharedHostIntegrationTests`, `SharedRuntime.Tests.ps1`. Both Python owner/client roles and restart/persistence; remote `/status` and `/path` are allowlisted/read-only. Linux abstract owner/client process evidence is separate from the TCP Python adapter. Android execution and a full abstract-AF_UNIX Python role matrix remain **unverified**. |
| Attach/detach/reload/extensions/failure ownership; `Reticulum.py:771–841,1185–1207,1971–1996` | **Implemented native equivalent** U/P/N: `RuntimeOperationsTests`, `RuntimeDiscoveryHostTests`, manager/lifecycle/shared tests. Named bearer reload, no-subscriber processing and child isolation; global policy is not hot-reloaded. Native static `InterfaceFactoryRegistry` replaces dynamic Python execution. Failed replacement does not restore old bearer. Source `panic_on_interface_error` is explicitly rejected; native top-level versus child ownership is fixed. |
| Known destinations/ratchets/paths/hashlists/cache/tunnel persistence and cleanup; `Identity.py:101–335,410–513`, `Reticulum.py:1316–1357`, `Transport.py:3745–3969` | **Implemented** bounded V/U/P/N coverage: `RuntimeStateStoreTests`, `LifecycleStateTests`, `Parity4TunnelTests`, `Parity5ResourceStorageTests`, `Parity5Resources.Tests.ps1`. Public state uses one checksummed atomic native generation; interchange schemas are available but the runtime directory is **not drop-in Python storage**. Signed native ratchet metadata differs from the upstream key-only file; neither checksum nor raw state hash authenticates a publisher. No persistent encrypted-session/resource resume. |
| Logging/rotation/timestamps/service/interactive/shutdown and housekeeping; `RNS\__init__.py:58–160`, `Reticulum.py:157–206,351–397,461–472`, `Utilities\rnsd.py:40–110` | **Implemented native equivalent** V/U/P/N coverage: `RnsdConfigurationParityTests`, `RnsdInteractiveTests`, CLI/shared Pester. Levels 0–8 and rotation, configured graceful persistence; bounded management shell instead of Python REPL. Windows Ctrl+C, Unix SIGTERM and daemon process death are distinct scenarios; not all console/service managers or signals have evidence. |
| Statistics, profiling and formatting; `Reticulum.py:1498–1969`, `Transport.py:591–676`, `RNS\__init__.py:168–349,357–615,641–664` | **Partial**: `RuntimeOperationsTests.StatisticsPreserveActualFramedTcpBytesAndSampleBearerRatherThanDecodedBitRates` and related measured/unknown-extension and child-aggregate tests cover the counter boundary. Diagnostics supply bearer counters, TCP TX counts framed bytes, unknown extension counters remain null, and decoded totals stay separately named. Native profiler and Local queued-write bytes are not Python profiler/TX-buffer semantics. Exact all-output/exit behavior remains **unverified**. |
| Discovery parsing/stamps/encryption/source/age/capacity/bootstrap/autoconnect; `Discovery.py:43–78,290–650,652–879` | **Implemented** bounded V/U/L/P coverage: `DiscoveryTests`, `DiscoveryMetadataTests`, `DiscoveryLiveTests`, `RuntimeDiscoveryHostTests`. Actual Backbone autoconnect on Linux, bounded TCP/HDLC fallback elsewhere. Only Backbone autoconnect is applicable at this pin; I2P autoconnect and Yggdrasil/Tor availability detection are upstream placeholders. |
| Discovery publication and reload lifecycle; `Discovery.py:136–258`, `Reticulum.py:988–1026` | **Implemented bounded software** for TCPServer/Backbone plus connectable I2P, RNode, Weave, KISS and KISS-framed TCPClient. `DiscoveryTests.BearerPublicationMatchesExecutedReferenceAndMetadataDoesNotTreatRadioAsTcp`, rejection/pinned-KISS-defect test, factory/metadata and existing host lifecycle tests pass in focused tests. Independent `GenerateDiscoveryVectors.py --bearer-output` fixture records source-backed schemas/defaults. Numeric KISS modulation's pinned failure is explicitly diagnosed, not repaired by invented wire values. All-bearer live/native/RF publication and full gates remain **unverified**. |
| Blackhole identities/publication/update/persistence and endpoint rejection; `Reticulum.py:1998–2041`, `Discovery.py:883–978` | **Implemented** bounded V/U/L/P coverage: `BlackholeTests`, `BlackholeLiveTests`, network Pester. Expiry/reason/source provenance, failed-update preservation and identified endpoint/path teardown covered. Anonymous datagrams cannot supply an authenticated sender identity; sender-based rejection there is **not applicable**, not secretly implemented. |
| Link handshake/mode/proofs/RTT/keepalive/stale/close/identification/policies; `Link.py:73–228,336–704,929–1306` | **Implemented** bounded V/U/L/N coverage: `LinkVectorTests`, `LinkTests`, `LiveLinkTests`, `Parity5RequestTests`, mixed transport/native resources/rnsh. Only AES256-CBC is enabled at the pin; named AES128/GCM/OTP/PQ link modes are **upstream-disabled/placeholders**. Opt-in PHY tracking has bounded software evidence above, not physical/live certification. Exact all-state impaired/long-running platform matrix is **unverified**. |
| Requests/responses/receipts/progress/limits and owned callback work; `Link.py:473–510,804–918,1324–1498`, `Destination.py:320–390` | **Implemented** bounded V/U/L: `Parity5RequestTests`, `Parity5TransferLiveTests`. Preinstalled limits, small/resource responses, denial/list/identified policies, cancellation and core settlement without observers. Explicit bounded handlers/queues and overflow diagnostics are intentional; arbitrary blocking synchronous user callbacks cannot be force-cancelled. |
| Resources, metadata/compression/hashes/parts/HMU/proofs/queue/cancel and multipart file storage; `Resource.py:169–514,576–1123,1138–1379` | **Implemented** bounded V/U/L/P/N: `Parity5ResourceTests`, `Parity5ResourceStorageTests`, `Parity5TransferLiveTests`, `NativeResourceTests`, resource/rncp Pester. Seekable/forward-only input, spooling, mandatory teardown joins, malformed metadata/decompression bounds and next-queue release covered. Explicit size/concurrency/storage limits differ from upstream. Cross-link/restart arbitrary-offset resume is **not applicable**: no such pinned protocol exists. Source-generated XOR resource fixtures do not prove encryption. |
| Channel envelope/order/window/retry/receipts/exhaustion; `Channel.py:113–148,175–245,303–608` | **Implemented** bounded V/U/L coverage and rnsh N: `ChannelResilienceTests`, `Parity5ChannelTests`, `Parity5ChannelRetryCryptoTests`, mixed transfers/rnsh. Wrap/reorder/RTT boundaries have independent reference evidence, not live 65536-message wrap-under-loss evidence. Retries preserve envelope/sequence but use fresh encryption/hash/receipt. Prolonged impaired soak is **unverified**. |
| Buffer stream IDs/compression/EOF/flow/closure/callbacks; `Buffer.py:44–379` | **Implemented native equivalent** V/U/L coverage and rnsh N: `Parity5ChannelBufferTests`, mixed transfer/rnsh. Bounded slow-reader failure, outstanding-read settlement and concurrent close/write covered. Native blocking async reads, internal-handler precedence, bounded callbacks and 15-second close deadline intentionally differ from Python's nonblocking stream API and RTT-based close. |

Shared advertised-claim case **T057 / C009** additionally drives the actual
candidate **rnsh CLI** against a pinned upstream Channel application using
`rnsh/protocol.py:100-107`'s `Buffer.StreamDataMessage` subclass. Both directions
persist and compare distinct exact ordered stream bytes and observe EOF across
multiple messages. The application does **not** execute a subprocess: this is
Channel/stream-message acceptance, not remote-execution acceptance or a replacement
upstream listener. It does not cover the public RawChannelReader/Writer APIs,
loss/reordering, sustained soak or arbitrary binary stdout. Both candidates use
precreated owned shell-state directories; this avoids the upstream CLI's ordinary
new-directory notice contaminating its stdout before logging initialization,
without filtering output or patching upstream. Local Windows NativeAOT and actual
Linux upstream-to-upstream executions passed, followed by all four hosted
candidates in run **40915647175**, exact source **52dc6f0**. Separate impaired
mesh failures in that run do not invalidate or expand this bounded Channel case.

The linked [endpoints](endpoints.md), [transport controls](transport-controls.md),
[runtime operations](runtime-operations.md), [runtime state](runtime-state.md),
[resources](resources.md) and [Channel/Buffer](channel-buffer.md) specify local
bounds and ownership decisions. A passing test does not erase any stated
limitation.

### Configuration reader families

This index prevents constructor-only or silently ignored controls from being
mistaken for full configuration coverage. The effective settings are documented in [rnsd](rnsd.md) and the per-bearer
documents. Acceptance for each key includes omission,
valid nondefault, invalid type, boundary and conflicting-alias observations
from the pin, followed by the actual runtime effect and diagnosed failure.

| Reader family / source | Classification and acceptance boundary |
| --- | --- |
| Sharing, identity, transport/management/probe/MTU/gravity/implicit-proof policy; `Reticulum.py:263–327,478–588` | **Implemented supported settings / partial intentional differences**: explicit native validation, fixed failure isolation rather than `panic_on_interface_error`, no Python REPL. This is not arbitrary constructor/API argument compatibility. |
| Discovery/network identity/blackhole/autoconnect; `Reticulum.py:590–657,988–1026` | **Implemented supported bounded software**, including non-TCP publication's independent fixture and focused tests; all-bearer live/native publication remains **unverified**. Parse success alone is insufficient. Executable metadata is explicitly authorized and bounded, not implicit config execution. |
| Default/per-interface AR/IC/EC/gravity and four transport queue sizes; `Reticulum.py:659–730,843–984`, `Interface.py:72–96`, `Transport.py:134–162` | **Implemented bounded equivalent**. Check control effect, inheritance and saturation, not only normalized values. Manager wait-on-full queue and transport priority queues are different boundaries. |
| Enable aliases ORed, mode aliases/precedence, outgoing/recursive/internal/bootstrap/IFAC/bitrate; `Reticulum.py:843–984,1029–1182` | **Implemented bounded equivalent**, including source-exact known-path/direct-send exceptions to `outgoing=no`; strict unknown-mode rejection and retained native aliases are explicit differences. |
| Logging levels/timestamps/verbosity/destination; `Reticulum.py:232–245,306–315,460–472` | **Implemented** documented native process contract; configured versus absent level and service precedence matter. |
| UDP/TCP/Serial/Auto/Backbone/Pipe/Local/KISS/AX25/RNode/Multi/I2P/Weave readers | **Implemented desktop supported subsets / partial platform coverage**, individually classified below. Nested Multi radios and generic post-init inheritance are not inferred from a standalone constructor. Local endpoints are runtime-created, not a fictitious user interface type. |
| Android serial/USB/Bluetooth/BLE selectors; `Interfaces\Android\*.py`, Weave Android branches | **Not implemented** adapters and host; desktop parser rejection is not a native Android equivalent. RNodeMulti on Android is explicitly unsupported upstream. |
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
| Auto; `AutoInterface.py:44–747` | **Implemented** V/U/L: actual Linux namespaces/veth IPv6 multicast/raw/IFAC, wrong-key rejection, peer expiry; deterministic disappearance/reappearance. `Parity6AutoLiveTests`, multicast tests, `Invoke-AutoTopology.ps1`; [Auto](auto-interface.md). | **Unverified** actual Windows/macOS/Android multicast/device transitions and native per-target bearer flow. Group tokens are not authentication; IFAC is separate. Native startup-selected device recovery is deliberate hardening, not dynamic discovery of all  devices. |
| Backbone initiating/listener/accepted peer; `BackboneInterface.py:42–183,333–767,789–1121` | **Implemented software and Linux L**: actual Python advertisement/autoconnect/encrypted link; shared ingress, partial writes, zero/interrupted/would-block/stall/hysteresis and flap tests. `Parity6BackboneTests`, shared-ingress tests; [Backbone/Pipe](backbone-pipe.md). | Windows/macOS direct Backbone **not applicable** at pin (Linux/Android gate); compatible HDLC fallback is distinct. **Unverified** Android and native Backbone-specific impaired feature execution. Hardware MTU 1048576 does not imply every link can exceed the native link ceiling. |
| Local initiating/listener/accepted shared client; `LocalInterface.py:44–494` | **Implemented** V/U/L/P/N TCP shared roles; Linux native abstract owner/client process flow. Decoded receive accounting, keepalive and simulated Android sleep policy corrected. `LocalSharedPacketInterfaceParityTests`, shared tests; [platforms](interface-platforms.md). | **Unverified** complete Python/native abstract Unix role/restart/statistics matrix, real Android 3-second keepalive/12-second pause/resume. Linux/Android abstract endpoints are distinct from filesystem Unix sockets; macOS/Windows use TCP. |
| Pipe; `PipeInterface.py:44–204` | **Implemented** V/U/P/N Windows and Linux process flows, argv/no-shell, child EOF/nonzero/respawn/cancel/cleanup. `Parity6PipeTests`, interface Pester. | **Unverified** actual pinned-Python/RNS exchange through Pipe and broader OS process launch. Malformed/overlong rejection intentionally differs from upstream prefix truncation. |
| KISS and AX25KISS; `KISSInterface.py:38–396`, `AX25KISSInterface.py:38–411` | **Implemented software** V/U TNC controls/READY/beacons, source quirks, AX.25 addressing/strip, bounded flows/reconnect; `Parity6KissTests`; [KISS](kiss-interfaces.md). | **Unverified L/H** authorized TNC, actual modulation/RF and physical recovery. A USB descriptor is not device authorization. Android provider missing separately. |
| RNode serial/TCP and Multi shared radios; `RNodeInterface.py:135–1257,1452–1587`, `RNodeMultiInterface.py:148–1164` | **Implemented software** V/U serial/TCP control/data/echo/firmware/telemetry/display/reset, vports/shared credit/inheritance and recovery; `Parity6RNodeTests`, configuration tests. **H/N bounded**: one Windows RAK4631/SX1262, firmware 1.86/nRF52/Normal, passed receive-only managed startup/live configuration/radio-off/reopen and two native daemon starts with unchanged EEPROM. Native USB DFU/reflash, port recovery and reversible 200-byte EEPROM persistence/restoration also passed. Signer remains unverified. [RNode](rnode-interfaces.md), [maintenance](rnodeconf.md). | **Unverified H/L** data/RF/unplug/Multi simultaneous radios/physical TCP adapter and other boards/settings/platforms. Only one radio available; receive-only control and maintenance evidence are not RF acceptance. Desktop BLE is partial as classified below. |
| RNode desktop BLE; `RNodeInterface.py:174–186,385–400,1241–1450` | **Partial Windows software** V/U: source-derived `ble://` selection/bond matching, native paired-device SetupAPI/GATT provider, bounded notifications, write-without-response chunking and simulated reconnect/redetection. `RNodeBleTests`, `RNodeBleLifecycleTests`; generator records exact pin/source hash. | Windows uses cached paired inventory rather than live advertisement scanning and conservative 20-byte writes rather than negotiated maximum chunks. Native GATT calls observe cancellation before/after, not during OS transactions. **Missing Linux/macOS providers; unverified physical BLE** discovery/bonding/notification/write/recovery. Local Windows host has no Bluetooth adapter. Not an Android-only gap or upstream placeholder. |
| I2P; `I2PInterface.py:76–330,322–728,729–762`, `vendor\i2plib\*` | **Partial** V/U/L: SAM destination/session/persistence/framing, actual i2pd streams and both Python roles; simulator restarted persisted current/legacy destinations. Actual Java 2.14.0 local-destination restart passes independently for managed .NET and pinned upstream: bidirectional DATA before/after restart, unchanged keys/address and surviving initiator. `Parity6I2pTests`, topology runner; [I2P](i2p-interface.md). | **Public-tunnel restart failed** for both .NET and unmodified pinned upstream after successful initial DATA. Local same-router delivery is not public-overlay recovery; separate-router overlay and published-NativeAOT I2P remain unverified. Readiness/STREAM STATUS OK/same b32 alone are not DATA evidence. |
| Weave WDCL/endpoint peers; `WeaveInterface.py:37–326,329–640,699–1102` | **Implemented software** V/U signed switch handshake, endpoint peers/via/dedup/expiry, display/telemetry and bounded shared queue/reconnect; `Parity6WeaveTests`; [Weave](weave-interface.md). | **Unverified L/H** actual Weave device, Python peer, physical reconnect and Android USB. Type-6 encapsulation has no source processing implementation: **upstream placeholder**, not omitted invented functionality. |
| Android Serial/KISS/RNode; `Android\SerialInterface.py:63–289`, `KISSInterface.py:73–448`, `RNodeInterface.py:119–552,623–750,1526–2007`; Weave `71–100,162–224` | **Not implemented** installable native host, permission/lifecycle wiring and USB/RFCOMM/GATT providers. Desktop `net10.0` streams, Android policy branches or a desktop build are not providers. Detailed contract and requirements in [platforms](interface-platforms.md). | **Unverified H** device/service/background/network/USB/Bluetooth execution after implementation. Android Multi is **not applicable/upstream explicitly unsupported**, not an adapter to invent. |

## Utility and Git contract matrix

These cover all fourteen installed entry points, including nested command
families. Native branding, explicit authorization and bounded I/O are deliberate
differences, not exact Python help/spinner/REPL compatibility.

| Command / pinned source | Classification and concrete evidence |
| --- | --- |
| `rnsd`; `Utilities\rnsd.py:40–110` | **Implemented documented native equivalent** V/U/P/N for config/example/version/service/logging/interactive/graceful persistence; parser exit 2/runtime 1/success 0. Python REPL and service-manager installation are **not applicable equivalents** of its native management shell/file-logging flag. |
| `rnid`; `rnid.py` import/export/metadata/sign/encrypt/main | **Implemented bounded** V/U/L/P/N: `Parity7RnidTests`, identity Pester; >16 MiB RFE both Python directions, lookup/retained restart/announce, detached/current/legacy/embedded/wrapped signatures/base256, explicit bounded editor and metadata validators. Raw/embedded and metadata bounds, native binary stdin/stdout extensions and exact supported exits in [rnid](rnid.md). Arbitrary ConfigObj/custom-validator runtime is not claimed. |
| `rnstatus`; `rnstatus.py:159–959` | **Partial** U/L/P/N accepted software options: local/remote stats, JSON/filter/sort/monitor/link/profiler/interface/discovery controls, measured child totals/rates/bursts/pressure. `Parity7NetworkUtilitiesTests`, network Pester; runtime/shared tests verify the bearer-counter regression without redefining measurements. PHY  has bounded four-project software evidence; physical/live attribution and exact full human output/exit distinctions remain unverified. |
| `rnpath`; `rnpath.py:43–528` | **Implemented bounded / partial exact-output coverage** U/L/P/N table/rates/filter/drop/void/via/request/blackholes, authenticated remote read tables. Remote mutation/path-request branches are **upstream unimplemented** (`136,210,229,384,394,415,436`), not missing native remote administration. Native path wait may repeat requests unlike the CLI's initial-send-then-wait. |
| `rnprobe`; `rnprobe.py:42–249` | **Implemented bounded / partial PHY coverage** U/L/P/N encrypted packet/signed receipt RTT/hops/loss and 0/1/2/3 outcomes; actual Python proof/no-proof destination callbacks observed. `Parity7NetworkUtilitiesReference.py`, network Pester. Physical RSSI/SNR/Q not inferred from TCP or substituted application replies. [Network utilities](network-utilities.md). |
| `rncp`; `rncp.py` listen/send/fetch/main | **Implemented bounded** V/U/L/P/N send/fetch both Python roles, 3–5 MB Python interop and streamed multi-megabyte native tests; file collision/overwrite/jail/allow precedence/writable-output/progress policies. Concurrent native in-process publication fixed, **partial filesystem race guarantee** against unrelated Unix writers; documented lexical jail is not a symlink sandbox. Windows reference move shim limits unmodified-upstream claims. |
| `rnx`; `rnx.py` listen/execute_received_command/execute/main | **Implemented native equivalent** V/U/L/P/N explicit listener authorization, binary streams/limits/negative slicing, failed spawn, timeout/cancel/process cleanup and exit mapping. Bounded execution/concurrency differs from upstream; no implicit shell. `-x` is a command loop, **not rnsh or a PTY**. [Transfers](rncp-rnx.md). |
| `rnsh`; `rnsh\protocol.py:46–148`, `args.py:47–104`, `session.py:88–425`, `initiator.py:206–472`, `process.py:371–704` | **Implemented Windows/Linux coverage / partial platform acceptance** V/U/L/P/N both Python roles, binary streams, loss/retry, PTY resize, eight pipe/terminal combinations, exact mode restoration, native terminal-hosted initiator, failed spawn/cancel/new-session recovery. Actual Python process backend runs on Linux/WSL, not a fake Windows Python replacement. Darwin PTY/termios/ioctl/session implementation and actual-OS tests are present; **bounded macOS execution verified** for controlling TTY/resize/literal argv/signals/group teardown, mixed endpoints, mode restoration, redirected exit and native terminal initiation; bounded read-ahead and first-window-update corrections await hosted acceptance. EOF reference race is a bounded negative result, not successful EOF completion. [rnsh](rnsh.md). |
| `rnodeconf`; `rnodeconf.py:715–1433,1480–1547,1599–4495` | **Implemented software / bounded physical acceptance** V/U/P/N/H: parser/command/model/flasher plans, maintenance/provisioning/recovery simulators and native executable Pester. One Windows RAK4631/SX1262 firmware 1.86 passed USB-triggered DFU, same-version reflash, changed-port recovery, configuration inspection and reversible EEPROM-setting persistence/restoration of all 200 starting bytes. Firmware target/actual hashes match; EEPROM signer remains unverified. Other devices/platforms, bootloader replacement, power-loss recovery and RF remain unverified. External flashers remain dependencies. [Maintenance](rnodeconf.md). |
| `rnir`, `rnpkg`; `rnir.py:40–72`, `rnpkg.py:39–83`, `Resolver.py:31–34` | **Upstream placeholders** for resolver/package functionality; **implemented native startup/parser shells** V/U/P/N. Real native no-interface initialization/disposal is tested; source constructor-capture vectors prove only arguments. No resolver wire/name-service/package manager is invented. [Placeholders](upstream-placeholders.md). |
| `git-remote-rns`; `rngit\client.py:55–76,130–213,335–718`, server list/fetch/push `2869–3140` | **Implemented bounded SHA-1** V/U/L/P/N ordinary Git capability/option/ref/HEAD/list/batched fetch/incremental/no-change/Unicode/push/force/delete/per-ref status, local ref ownership, explicit malformed/unknown rejection and deadlines; `GitRemoteRnsTests`, `RngitPushTests`, `RngitClientConfigurationTests`, Git Pester. **Partial object-format/platform coverage**: helper rejects SHA-256, does not advertise negotiation, upstream list has no empty-repo format field; direct bare client is separate. Lazy connection/error exit differs from upstream. |
| `rngit` direct clone/fetch; client/list/fetch and link/file-response contracts | **Implemented bounded** V/U/L/P/N bare direct client and actual hosted-source incremental retrieval, strict fsck/object/ref/tree validation with disclosed seed and nonzero bundle bytes. Buffered application bundles/list/response caps are not arbitrary-repository streaming support. Detached remote HEAD and custom direct URL/CLI restrictions are explicit in [Git](rngit.md). |
| `rngit` node/config/identity/announce/block/stats/access/admin; `server.py:139–285,1979–2703,4443–4910` | **Implemented bounded** V/U/L/P/N `RngitHostTests`, configuration/statistics/process/native-host tests. Raw persisted identity, aliases, `.allowed` union/inheritance/none/all/admin/hidden-vs-denied, bounded requests/jobs, restart/corruption/concurrent mutation. Explicitly authorized executables replace automatic resolver execution; native directory state is not an all-files Python transaction. |
| `rngit` create/fork/mirror/sync/HEAD; `server.py:568–717,2097–2172,2784–2838,3141–3366` | **Implemented bounded** V/U/L/P/N `RngitRepositoryStoreTests`, HTTP/process/native-host tests; native RNS all communication, authorized external Git sources, cleanup/concurrency/persisted provenance and mirror/fork HEAD differences. External SSH/HTTP tooling success and platform-specific failures beyond exercised cases remain **unverified**. |
| `rngit` releases/artifacts/manifests/latest/offline signer; `server.py:761–1238,3367–3787` | **Implemented bounded** V/U/L/P/N `RngitReleaseTests`, native hosting/Pester. Multi-step drafts are not atomic transactions; interrupted drafts/restart/permission/tamper cases covered. Embedded key validity is not publisher trust. |
| `rngit` work documents/scopes/comments/permissions; `server.py:1346–1841,3789–4642` | **Implemented bounded** V/U/L/P/N `RngitWorkServiceTests`, native hosting/Pester. Active/completed/proposed/all, signed content, IDs, inheritance/admin/restart/concurrency. Document authorization is not inherited implicitly from client-side display. |
| `rngit` NomadNet pages/downloads/Markdown/Micron/templates; `pages.py:250–312,366–2041,2288–2320,2931–3007`, `util.py:130–818`, `highlight.py` | **Partial** V/U/L/P/N default rendering/page routes and bounded authorized templates/editors implemented; `RngitPageTests`, `RngitTemplatesTests`, media/templates Pester. Optional Pygments highlighting and wcwidth-enhanced layout are **missing equivalents**, not proven by fallback output; no Python dependency implementation required. Unicode/escaping/pagination/binary/download paths covered only by the named corpus. |
| `rngit` still-image/WebP media; `media.py:57–215` | **Implemented supported backend selection / partial installed-backend evidence** V/U/P/N `RngitMediaTests`; actual Windows FFmpeg, capture/failure tests for magick/convert/gm/avconv. Absence/failure can retain original as source does. No evidence for every encoder/OS/version; audio/video feature expansion is **not applicable**. |
| `rngcs`; `commitsigs.py:48–331` | **Implemented bounded** V/U/L/P/N `RngcsTests`, signer Pester: SSHSIG/RSG, sign/find-principals/check-novalidate/verify, real commit/tag and author/tagger checks. Allowed-signers database is not implemented by the pinned signer either; structural checking is not cryptographic verification or publisher discovery. |

### Supported extension scope

LXMF and telemetry are **outside this repository**: they live in
[DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem)
as `DotLXMF` and `DotTelemetry`, which build on this stack. Neither is a pinned RNS wire protocol or a substitute for upstream management.
Absent audio/video extensions are **not applicable**
to this inventory's implementation authorization.

## Shipped example scenarios

Python example filenames are scenarios, not a requirement to copy executables
or invent a bundled application. The source anchors below are at the same pin.

| Example / source | Classification and evidence route |
| --- | --- |
| `Minimal.py:18–67` | **Implemented bounded** runtime startup/identity/announce/dispose via node/shared/announce tests and daemon processes. |
| `Announce.py:23–138` | **Implemented bounded** app data/aspect handler/ratchets/path-response/cache and callback isolation, announce/endpoint/live suites. |
| `Broadcast.py:17–72` | **Implemented native endpoint behavior / unverified exact live example topology**: plain endpoint and router tests do not themselves prove a real mixed-peer broadcast scenario. |
| `Echo.py:25–260` | **Implemented bounded** encrypted DATA/proof policies/no-subscriber and mixed Python transport datagrams; packet PHY software tests are not physical/live Echo PHY evidence. |
| `Ratchets.py:24–269` | **Implemented bounded** enforced ratchet/rotation/signed reload and live announce encryption; live restart/expiry under impaired topology remains **unverified**. |
| `Link.py:27–234`, `Identify.py:27–257` | **Implemented bounded** both link roles, identification/proof/RTT/close and malformed/observer tests; not every timer/platform/failure permutation live. |
| `Request.py:26–230` | **Implemented bounded** small/resource/streamed response, receipt/progress/deny/timeout/size/cancel via request and actual Python transfer suites. |
| `Resource.py:28–238` | **Implemented bounded** random/compressible multipart/metadata/cancel/reject/proof/queue/storage, separate encrypted live/native evidence. |
| `Filetransfer.py:44–521` | **Partial scenario evidence**: generic registered list-request and streamed file responses exist; rncp send/fetch is not this example's interactive list/select file server. No claim of running that exact mixed example application. |
| `Channel.py:60–333`, `Buffer.py:35–266` | **Implemented bounded** custom explicit message factories, full-window/retry/order/stream/EOF via Channel/Buffer/mixed/rnsh; live long-wrap/slow-reader soak remains **unverified**. |
| `Speedtest.py:36–282` | **Unverified sustained metrics scenario**; live multi-MB throughput observations exist, not a prolonged independently measured byte/rate/loss/MTU sweep. Python performance identity is not required. |
| `ExampleInterface.py:62–293` | **Implemented native equivalent** statically registered factory/declared settings/lifecycle/IFAC/mode/attach/reload tests; dynamic Python file import/exec intentionally **not applicable**. |

## OS, RID and platform evidence

Each target is separate. A successful build, managed test, or RID declaration
does not validate another target or every interface. `net10.0` is the library
TFM, not an Android/iOS application.

| OS / architecture / deployment | Current evidence | Status and boundary |
| --- | --- | --- |
| Windows x64 / `win-x64` | Managed tests, NativeAOT publication, native help and feature flows, Pester process/package tests, and Python desktop roles are exercised. One RAK4631 passed native USB DFU/reflash and reversible EEPROM persistence/restoration; no RF acceptance. | **Partial** desktop acceptance. Broader per-interface physical/BLE/I2P behavior and impaired/native meshes remain unverified. Unix-only signal and abstract-socket tests do not apply. |
| Linux x64 / `linux-x64` | Managed tests, NativeAOT publication, native feature flows, Pester process/package tests, Python/shared/network/process roles, Auto namespaces/multicast, and Backbone are exercised. Recorded Linux network evidence includes WSL; it is not bare-metal radio evidence. | **Partial** Linux acceptance. Other distributions, kernels, physical adapters, and broader impaired/native meshes remain unverified. |
| macOS ARM64 / `osx-arm64` | Hosted CI builds and tests the managed suite, publishes NativeAOT, and runs native help and an encrypted multi-megabyte resource test. The package acceptance suite skips macOS. | **Partial** platform evidence; Darwin rnsh controlling-TTY/signal/teardown/mixed-endpoint, mode restoration, redirected exit and native-initiation execution passed; read-ahead and first-window-update corrections remain pending. Installation acceptance is not established. |
| macOS x64 / `osx-x64`; Windows ARM64 / `win-arm64` | No target-specific feature execution is recorded. | **Unverified**; `osx-arm64` evidence does not certify macOS x64, or vice versa. |
| Linux ARM64/ARM / `linux-arm64`, `linux-arm` | No architecture-specific runtime/native feature execution is recorded. | **Unverified**, including crypto, compression, serial, terminal, and Git dependencies. |
| Linux RISC-V | No verified runtime, SDK RID/native toolchain, or feature flow is recorded. | **Unverified** target; do not claim a supported NativeAOT RID. |
| FreeBSD | No verified target/toolchain/feature execution is recorded. | **Unverified**, including native dependency, socket, and terminal behavior. |
| Android | No installable native host, USB/Bluetooth provider, or on-device execution. | Host/providers are **not implemented**; platform execution is **unverified**. Upstream Multi is explicitly unsupported on Android. |
| iOS | No pinned iOS-specific adapter or native application target/execution. | Dedicated pinned adapter is **not applicable**; project deployment is **unverified** and a native host is absent. |

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
| `Interop\live_rnsh_node.py` and POSIX peer | Real POSIX reference sessions/protocol, client driver bypasses interactive upstream UI. True EOF requires exact child-produced acknowledgement; clean return and positively observed missing-callback race are distinguished, never patched into success. A cancellation without observed exited child / RUNNING listener / absent return code fails. Windows native uses ConPTY, not a fake Python POSIX backend. |
| Actual I2P topology | Explicit opt-in public network and authenticated router provisioning; i2pd `[sam] singlethread=false` is nondefault, not an RNS patch. Java local-destination `i2cp.disableLoopback=false` permits same-router local delivery and passes restarted DATA. Forced-public `true` still fails restart for .NET and upstream; local passes are not public-tunnel acceptance. No zero-hop or hidden reference-side lease/readiness shim. |
| Real lxmd | Nondefault accepted transfer limit raised for large resources; delivery-endpoint acceptance is separate from reverse origination/propagation synchronization. |

Fixtures may use public deterministic test keys for reproducibility; these are
not publisher identities. A simulator's agreement with our framing is not a
second independent Python node. Reference-side source remains clean; that fact
alone does not negate the explicitly disclosed in-process adapters.

## Known gaps

The active list of missing and partially implemented capabilities is maintained
in [TODO.md](../TODO.md). The contract tables give their current status and evidence; the active list is not a substitute for those per-contract
classifications.

In particular, desktop RNode BLE is partial Windows software without physical
acceptance or Linux/macOS providers; macOS rnsh execution acceptance is pending.
Android has no native host or USB/Bluetooth provider and is deferred. I2P
local-destination persisted restart passes, while forced-public-tunnel restart
fails for both .NET and upstream. Physical RF,
per-bearer hardware operation, broader impaired/native mesh topologies, and
several non-Windows/Linux target platforms remain unverified. These statements
do not change the narrower implemented software and live-peer coverage recorded
in the tables.

### Intentional differences

The following are intentional differences or bounded implementations: explicit
start/dispose and cancellation; Core codec separated from routing; static AOT-safe factories
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
output/exit differences stay documented including where valid flows pass.

## Source integrity, signatures and publisher trust

The .NET `rngit` client has retrieved missing Git objects from the hosted source
using Reticulum, with a disclosed seed, nonzero received bundle, recorded refs,
commit/tree IDs, and strict Git object checks. Git object consistency does not
authenticate the endpoint, locator, or publisher. A checksum, transport
connection, signed announce, embedded key, or self-contained release signature
alone does not establish publisher identity.

The published maintainer release identity is
`bc7291552be7a58f361522990465165c`. A trusted-release claim requires an
independently obtained and validated binding to that identity and verification
of the relevant manifest, artifact, commit, or tag, including tampering, wrong
signer, missing signature/key, and author/tagger mismatch cases. `rngcs
check-novalidate` is structural only; Git allowed-signers is not the pinned
signer's trust database; choosing `--signer` is caller policy, not authenticated
key discovery. This document does not claim publisher authentication.

Preserve the Reticulum License and notices. This experimental implementation
is not official, production-certified, fully interoperable, or relicensed.
