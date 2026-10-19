# DotReticulum development history

Development chronology, verification evidence, upstream comparisons and recorded limitations. Phase and subphase completion is tracked only in [plan.md](../plan.md); this document is not a second progress checklist.

Current live interoperability uses the authoritative Reticulum-hosted upstream at `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7). Stored reference vectors and their reproduction generators retain their original `e40191b3d193b46b7f2d8a44424a594cd758839b` provenance. Earlier records describe the pins and tooling used at that time, including Python coverage scripts since replaced by PowerShell.

## Verification records

Upstream reference: markqvist/Reticulum @ `e40191b3d193b46b7f2d8a44424a594cd758839b`. Vectors are produced by executing upstream Python (`tests/**/Generate*.py`, fixtures in `tests/**/Fixtures`); this is vector/cross-check evidence, not live-node interop. Historical milestone records describe evidence at that milestone; the P0 closeout record identifies which limitations have since been resolved.

### Git helper UTF-8 closure (2026-10-19)

Explicitly set helper stdout to BOM-free UTF-8 and decode helper input using
strict UTF-8, rather than relying on a Windows console code page. Extended the
C# reference fixture and real-Git Pester assertions with Unicode branch/tag
names, including an incremental update and bare-repository ref preservation.
Non-ASCII source literals use escapes. This does not change direct rngit CLI
ref ownership or the pinned reference.

The first Unicode run was **22 passed / 4 failed**: upstream Python's
`subprocess.run(text=True)` used the Windows legacy locale to decode Git UTF-8
ref names, advertising mojibake and then failing to fetch those nonexistent
names. Kept the assertions and unchanged reference source. The Python server
child now uses **PYTHONUTF8=1** (runtime/subprocess text decoding) and
**PYTHONIOENCODING=utf-8** (stdio), with no parent-environment change or
protocol/source shim. This is explicit reference runtime configuration, not
evidence that upstream supports all default Windows code pages.

Final commands, using `$root`, `$python` and `$reference` as defined in the
Git-first record below:

```powershell
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o (Join-Path $root 'git-native-utf8') --nologo -v minimal
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe (Join-Path $root 'git-native-utf8\DotReticulum.Applications.exe') -Coverage -ResultsDirectory (Join-Path $root 'coverage-git-utf8')
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference -Application (Join-Path $root 'git-native-utf8\DotReticulum.Applications.exe')
```

Publish passed without warnings. Full live/native .NET: **402 passed, zero
skipped/failed**; managed and native Pester: **26/26 each, zero skipped/failed**.
A separate native process fed invalid UTF-8 byte FF plus newline exited **1**
within ten seconds, with empty stdout and an explicit decoding diagnostic.
Merged production coverage (six reports, shared code counted once; external
Pester/native processes not counted as managed hits):

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 913/1395 (65.45%) | 723/1579 (45.79%) |
| Core | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2439/2800 (87.11%) | 1313/1701 (77.19%) |
| **Overall** | **4908/5874 (83.55%)** | **2773/4147 (66.87%)** |

Also repeated actual hosted missing-object retrieval with the final UTF-8
native binary: a fresh `helper-hosted-utf8.git` was locally seeded only through
`7f2b3b9b524c9386316379af1313b43a5e4f7a5d` from `helper-hosted.git`; both target
commit and tree failed `cat-file -e` before networking. Using the same remote
URL/refspec/config as below, ordinary
`git "--git-dir=$seed" fetch --progress --no-tags origin` received **11,502
bundle bytes** through the six-hop route. Strict fsck, fetched commit/tree and
seed ancestry all passed, again yielding `a29bfb51c468945c96a1ee22cae0114e993e2391`
and `24d4c393de9fd57b933836cea68891fd953ab060`. Evidence is retained in
`helper-hosted-utf8-fetch.log`. This is still an explicitly seeded incremental
fetch, not a full clone or a publisher-signature authentication claim.

### Post-gate source-backed parity inventory (2026-10-19)

This inventory uses only the verified hosted snapshot
`a29bfb51c468945c96a1ee22cae0114e993e2391`; no reference update or historical
vector relabeling is implied. Status vocabulary: **implemented** means the
specified bounded contract has supporting evidence; **partial** means some
behavior exists but the full family is not covered; **missing** means no
corresponding implementation; **unverified** means insufficient execution
evidence; **not applicable** denotes an implementation detail or out-of-scope
extension rather than an observable parity obligation; **upstream placeholder**
must be established from source, not inferred from missing .NET code.
Families below are not a claim that every option/platform has been exhausted.
`plan.md` remains the only progress checklist.

#### Git family inventory and acceptance boundaries

All relative source anchors in this table are under
`RNS/Utilities/rngit/` at the pinned revision. The .NET owner is Applications;
local Git owns object storage and repository/ref semantics, while all RNS
networking remains native .NET.

| Source contract | Status and current .NET evidence | Required acceptance before closing the corresponding parity work |
| --- | --- | --- |
| `client.py:55-76,335-447,464-543`; `server.py:2869-3003`: helper invocation, list/HEAD and read-only bundle fetch | **Implemented within the Git-first gate**: ordinary managed/native Git clone/fetch and real hosted missing objects; see record below | Preserve these regressions when adding write/configuration features; do not advertise unimplemented capabilities |
| `client.py:160-213,679-718`: RNGIT_CONFIG/client_config, client_identity, aliases, ref_batch_size=25 and logging | **Partial**: fixed batch 25, .NET config/storage identity, RNS_CONFIG alias; Python client config/aliases missing | Differential default/custom/invalid configuration and persisted-identity cases, documenting any intentional .NET path difference; retain offline protocol negotiation |
| `client.py:545-676`; `server.py:3005-3140`: push bundle, existing-object operations, force and delete | **Missing**; explicitly rejected/not advertised today | Reference-generated request/status cases, actual Git branch/tag push and deletion both ways, denied/hidden repository, non-fast-forward/force, existing-object-only update, partial failures and concurrent writers; native actual push before capability promotion |
| `server.py:139-285,1979-2250,2646-2703,4644-4910`: node CLI/configuration, identity, announcements, mirror interval, blocked/stats-ignored identities and persisted statistics | **Missing**, distinct from the existing direct read-only CLI | Separate-process defaults/custom config, print/service/logging/exit behavior, restart identity/stats recovery and corrupt/unwritable storage; bounded async jobs owned by the host, not copied Python threads |
| `server.py:2262-2620,4443-4642`: group/repository/document permissions and .allowed administration | **Missing** | Match read/write/create/release/interact/propose/admin decisions, aliases/inheritance, hidden-vs-denied statuses, permission edits/reload/restart and malformed permission files against the actual server; avoid exposing inaccessible repositories |
| `server.py:568-717,2097-2172,2784-2838,3141-3366`: create/fork/mirror/sync and default HEAD | **Missing** | Real create and default .allowed ownership, existing/denied target, local/remote fork/mirror, periodic/manual sync, failure cleanup, restart and source-default-branch changes. Execute CLI dispatch as well as request handlers rather than assume the two are identical |
| `server.py:761-1238,3367-3787`: releases, artifacts, latest, manifests and signer/offline modes | **Missing** | Actual release list/view/fetch/create/delete/latest across peers; byte-identical artifacts, manifest/signature verification, unknown signer, tampering, interrupted multi-step creation, authorization, restart and concurrent creation |
| `server.py:1346-1841,3789-4441`: work documents and scoped permissions | **Missing**, newly made explicit in the plan | Reference each operation (list/view/create/propose/edit/delete/comment/complete/activate/perms), active/completed/all scopes, IDs, document visibility, file persistence/restart and concurrent changes; CLI operation names must follow executed reference behavior |
| `pages.py:250-267,366-2041,2931-3007`; `util.py:130-818`; `highlight.py`: NomadNet browsing/rendering/downloads | **Missing** | Request/response page and artifact/download comparisons for public/identified/denied peers, tree/blob/commit/ref/history/statistics/release/work routes, pagination/escaping, binary/text handling and link-owned temporary-file cleanup. Match observable output, not the Python module layout |
| `media.py:57-215`; `pages.py:2288-2320`: image conversion/WebP backends | **Missing** | Valid and malformed image, selected/absent backend, bounds, encoder failure/timeout and cleanup; justify native image facilities/external backend choice. This is upstream repository image serving, not permission to invent audio/video extensions |
| `commitsigs.py:48-210,212-331`; `setup.py:187-189`: rngcs SSHSIG/RSG operations and executable integration | **Missing** | Execute upstream-generated signatures and malformed vectors, Git commit/tag signing/verification in both directions, principal mismatch, author/tagger mismatch, absent key/signature, stdin/file modes and exact exit behavior; preserve the distinction between structural check and cryptographic verification |
| Git SHA-256 negotiation, non-Windows packaging and impaired/concurrent helper sessions | **Unverified/partial**, not established by SHA-1 Windows loopback | Real hash-format negotiation and success/failure on each supported OS; loss/latency/reconnect/cancellation and concurrent fetches without ref ownership leaks. Do not infer these from a successful publish |

The inventory found release management and work-document collaboration beyond
the earlier broad Git headings, so both are now explicit required work rather
than accidental omissions. Rngit pages' image media functionality is present
upstream and remains in scope; audio/video stack extensions absent upstream do
not. Git object hashes/strict fsck still establish consistency, not publisher
authentication. The published maintainer release identity must be validated by
the later signature workflow before making authenticated-release claims.

#### Endpoint, crypto and transport family audit

The read-only endpoint owner reviewed the named primary modules and matching
Core/Crypto/Transport implementations/tests, including existing behavior rather
than treating P0 checkboxes as complete parity. Anchors below are relative to
the pinned `RNS/` tree. Existing vectors still have their original `e40191b3...`
provenance; the audit itself produces no new reference execution evidence.
The coordinator's full live/native regression separately establishes that the
currently tested slices remain green, not that these newly discovered gaps
are already resolved.

| Contract and source | Classification / existing evidence | Required closure evidence |
| --- | --- | --- |
| Identity key layout/signing/encryption (`Identity.py:707-944`), token (`Cryptography/Token.py:50-118`), RFC primitives | **Implemented bounded valid-input slices**, Core Identity/Crypto and identity/token/RFC tests | Preserve reference bytes and live crypto; classify invalid-key/provider differences separately |
| Token padding, HKDF malformed/boundary inputs (`Cryptography/PKCS7.py:42-48`, `HKDF.py:43-116`) | **Partial / intentional hardening**: strict .NET padding, nonempty IKM and RFC output bound differ from permissive upstream behavior | Independent verdict corpus including valid-HMAC malformed padding and IFAC-sized derivations; do not weaken strict crypto merely to mimic permissive failure behavior |
| Python crypto providers/vendored implementations (`Cryptography/Provider.py`, `Proxies.py`, `HMAC.py`) | **Not applicable as architecture**; native BouncyCastle/System.Security.Cryptography replacements | Valid-result compatibility remains required; full malformed-key provider differential is **unverified** |
| Identity public/private files and recall (`Identity.py:101-349,625-687,791-799`) | **Partial**: private raw-file operations/owner-only non-overwrite evidenced; known destinations memory-only | Public-file capability, local registered-destination recall, usage/retain cleanup and restart/corrupt-store behavior; separate .NET APIs are acceptable |
| Ratchet remembrance/persistence (`Identity.py:382-509`; `Destination.py:206-242,437-542`) | **Partial**: rotations/IDs/enforcement implemented, but no signed reload/retry store; cached expiry/unchanged-ratchet timestamp differences | Signed-file verification, concurrent rotation/key lifetime, failed-decrypt reload, receipt times, restart/expiry/corruption |
| Destination names/types/defaults (`Destination.py:96-198,544-676`) | Naming vectors **implemented**; endpoint family **partial**. LocalDestination defaults links off while upstream defaults on; group symmetric crypto/plain endpoint passthrough and duplicate-registration validation missing | Plain/group/single exchange, default/explicit link policy, direction/type/registration matrices and actual native endpoint flows |
| Local DATA and automatic proofs (`Destination.py:414-435`; `Transport.py:2596-2606`) | **Partial, concrete no-subscriber gap**: router proof/decryption processing sits inside its PacketReceived subscriber branch; current receipt tests install observers | No-subscriber valid/tampered data and PROVE_NONE/APP/ALL, observer exceptions, direct/routed Python receipts. Distinct from the P0 announce/LXMF observer fixes |
| Announce default/dynamic app data and tag caching (`Destination.py:244-318,678-690`) | Exact announce vectors **implemented**; dynamic provider/override behavior **partial** | Provider/override/tag lifetime differential; do not invent multipath behavior from the upstream comment saying that purpose is unused |
| Packet codec versus send/resend (`Packet.py:172-368`) | Codec valid flags/hash/reserved/hop/MTU slices **implemented**; orchestration/receipt eligibility **partial** | Send acknowledgement, resend/re-encryption and eligible DATA/context/destination matrix. .NET codec-only ownership is intentional |
| Proofs/receipts (`Packet.py:417-586`; `Transport.py:739-762`) | Successful explicit/implicit single and explicit link proofs **implemented**; culling, 1024 limit, metadata and exact timing **partial** | 1025+ receipts, CULLED and late proof, slow first hop/RTT timing/callback failures. Implicit link proofs are disabled upstream, not a feature to invent |
| Filtering/routing/reverse proofs (`Transport.py:1353-1445,1624-1680,2012-2164,2730-2761`) | **Partial**, common multi-hop/reverse/dedup paths evidenced; local outbound path-use refresh missing | Known-path outbound timestamp use, shared-instance exceptions and saturation; retain current normal-path regressions |
| Gravity/path health/expiry (`Transport.py:2206-2345,3229-3260`) | **Partial**: no gravity/responsive state; announce selection expiry and AP/roaming culling disagree | Competing equal-emission paths, higher gravity, unresponsive worse-hop replacement and mode expiry before culling |
| Announces/modes/path requests (`Transport.py:1448-1591,2303-2329,3279-3599`) | **Partial**: global rather than per-interface rates, incomplete mode/cap decisions; one pending response per destination can lose a concurrent target | Full mode matrix, both simultaneous requesters, held announces, recursive/inflight/slow-medium/rate/queue boundaries |
| Link-table proof/rebalance (`Transport.py:2610-2718`) | **Partial**: proof relay implemented but destination-path hop correction/terminus expectations incomplete | Authenticated correct/wrong interface, invalid signature, validated/unvalidated entries and changed-hop destination effects |
| Forced cache, paths/hashlists/tunnels (`Transport.py:2764-2875,2995-3124,3745-3969`; `Resource.py:653-676,765-777`) | **Missing** forced resource-proof cache recovery and transport restart stores/tunnels | Drop final resource proof and recover via cache query, not full retransmit; expiry/corruption; restart path answer from original cached announce and stable interface identity; tunnel disappear/reappear/signature/expiry |
| Inbound queue classes (`Transport.py:46-93,111-146,1753-1895`) | **Partial / intentional architecture difference**: one bounded wait-on-full channel of 256 versus separate prioritized data/announce/PR/limited queues | Measure control-plane behavior under saturation; explicit bounded prioritization/rejection/backpressure policy, not a Python threading translation |
| Link establishment/identification/request-response (`Link.py:336-510,646-918,1311-1500`) | Normal wire/live slices **implemented**; limits/final progress/receipt coupling and ownership **partial** | Set limit before transmission; enforce small and resource responses; final progress/failure/link-close, integer compression thresholds; bounded/cancelled handlers and blocked-observer isolation |
| Link MTU/metrics (`Link.py:148-228,512-644`; `Transport.py:2064-2088,2544-2565`) | **Partial/unverified** upgraded MTU: current code clamps 500; larger-MTU vector currently checks that clamp, not upgraded parity | Actual >500-byte packets/resources/channels, mixed-path bottlenecks, absent/unsupported signalling and PHY/rate metrics |
| Resources (`Resource.py:249-486,685-763,842-1088,1255-1379`) | Buffered encryption/compression/windows/metadata/segmentation slices **implemented**; file-backed I/O/thresholds **missing** | Files beyond memory budget, segment boundaries, compression thresholds, read failures, byte-identical both-direction/native transfer and deterministic owned-file cleanup |
| Resource terminal/assembly paths | **Partial, concrete gaps**: cancel/reject removes an outgoing resource before queue-release removal; multipart streams survive failure/teardown; malformed metadata decoding lies outside heavy-work error handling | Queue A/B/C cancel/reject A and complete B/C; queued cancellation, exactly-once callbacks, accumulator/task cleanup and hash-valid malformed metadata terminal states; independent advertisement/HMU/expansion verdicts |
| Channel (`Channel.py:113-545`) | Envelope/order/loss/retry/exhaustion/teardown/recovery tests **implemented bounded slices**; wrap/RTT/adversarial concurrency **unverified** | >65536 messages, wrap-adjacent reorder/duplicate and RTT/window boundaries. Existing ChannelResilienceTests already cover loss/exhaustion; do not resurrect the stale no-retry-tests claim |
| Buffer (`Buffer.py:44-379`) | Wire streams/compression/EOF **implemented bounded slice**; async read versus Python nonblocking is intentional; slow-consumer memory/cancel/disposal/ready-callback handling **partial** | Bound memory; unblock pending read on disposal/link close; observe callback faults; concurrent writes/EOF under exhausted retries. Fixed .NET 15-second close differs from upstream RTT/outstanding timing |
| Resolver (`Resolver.py:31-34`) | **Upstream placeholder**, resolve_identity is pass | Track shell/API status without inventing a resolver |

Examples supplying acceptance scenarios include Broadcast, Echo, Ratchets,
Link, Identify, Request and Filetransfer (not obligations to copy their Python
program layout). Resource-vector generators disclose deterministic XOR in place
of link encryption; those vectors alone are not encryption interoperability
evidence. The pinned source has multipart continuation, but this review found
no public durable restart/resume protocol; do not turn disk-backed assembly into
an unsupported resumable-transfer claim.

#### Runtime, configuration, interfaces and utility audit

The runtime owner reviewed Reticulum/Discovery, every upstream interface
family, non-Git utilities, corresponding .NET application/interface code/tests,
and selected configuration/manual/examples. This was read-only; no new
hardware/platform evidence was generated.

| Contract and pinned source | Classification / current boundary | Required acceptance |
| --- | --- | --- |
| Config discovery/default creation (`Reticulum.py:230-248,327-345,1359-1364`) | **Partial**: .NET ~/.dotreticulum, existing file/directory input; no upstream bootstrap/default search | Clean-home/missing/corrupt/unwritable config and documented native path choice |
| Config schema/aliases/enables (`Reticulum.py:478-730,843-1108`) | **Partial**, concrete differences: unknown settings ignored; upstream explicit enable aliases are ORed but .NET defaults enabled/overrides; pointtopoint and TCP port precedence differ | Generated differential fixtures for absent/conflicting flags, modes/ports, quotes/lists/nesting, unsupported settings; never silently activate ineffective controls |
| Shared instance/local bearer (`Reticulum.py:297-305,399-458`) | **Missing**; sharing defaults upstream on at ports 37428/37429; .NET nodes own separate hosts | Simultaneous clients share one physical listener; required-shared failure/fallback, separate instances, daemon restart/reconnect and cancellation |
| Authenticated RPC/retention (`Reticulum.py:355-369,1366-1437`) | **Missing**, not just MessagePack maps | Actual connection authentication/framing in both directions, bad keys/stalled peers; get/drop/manage/use/retain/unretain operations and persisted effects |
| Attach/detach/reload/statistics (`Reticulum.py:771-841,1498-1969,1971-1996`) | **Missing** upstream management surface; fixed .NET host configuration and custom telemetry do not establish it | Unaffected traffic survives repeated/concurrent management; precise stats/filtering/errors, rejected remote access and restart |
| Logging/housekeeping/signals (`RNS/__init__.py:58-80,119-160`; `Reticulum.py:157-206,351-397,461-472,1316-1357`) | **Partial**: upstream PATHING=7/EXTREME=8 versus .NET clamp/names through 7; rotation/timestamp/file/service modes incomplete | Levels 0-8, 5 MiB rotation, redirection, shutdown persistence and expiry/jobs under controlled clocks; faulted router still disposes interfaces |
| Discovery (`Discovery.py:43-78,136-330,472-516,652-879`; `Reticulum.py:988-1015`) | **Missing**: signed/stamped/encrypted metadata, stored states and autoconnect | Reject bad/insufficient work and disallowed source; 1/3/7-day state transitions, duplicate/capacity/transport-only eligibility, restart, bootstrap/detach. Identify DotReticulum honestly, not as RNS |
| Blackholes (`Reticulum.py:1998-2041`; `Discovery.py:883-978`) | **Missing** publication/source/update and endpoint integration | Duration/reason/expiry, authorization, failed/timed-out source, persistence and identity/link rejection |
| Interface extension (`Reticulum.py:1185-1207`; ExampleInterface) | **Missing equivalent capability**; Python dynamic exec itself **not applicable** to AOT | Define static-factory or owned out-of-process native boundary and test registration/config/error/lifetime behavior |
| Common interface controls (`Reticulum.py:843-1097`; `Interfaces/Interface.py:72-92`) | **Missing/partial**: outgoing/gravity/bitrate/announce/ingress/egress/bootstrap/recursive/internal rules; IFAC aliases not wired | Reference networkname/network_name, passphrase/pass_phrase/ifac_size derivation and wrong-key rejection; mode/control saturation; default cap 2%, target 3600/grace 5/penalty 0, 256 held announces |
| UDP (`Interfaces/UDPInterface.py:40-115`) | **Partial**: datagram/lifecycle interop exists; .NET always configures both receive/forward, literal addresses only | Independent sides, devices/hostname/IPv4/IPv6 and MTU; upstream HW MTU 1064 versus currently exercised 500-byte packet slice |
| TCP (`Interfaces/TCPInterface.py:75-110,181-209,271-303,515-551`) | **Partial**: HDLC/peer isolation/64-client cap evidenced; reconnect/KISS/IPv6/fixed-MTU/device config missing | Initial/reconnect 5-second behavior, finite/unlimited retry, cancelled reconnect, surviving peers; document cap hardening and test aggregate-peer diagnostic reporting |
| Serial (`Interfaces/SerialInterface.py:75-80,144-217`) | **Partial; real hardware unverified**: port/speed wired, stream tests; parity/data/stop bits, idle partial-frame reset and reconnect incomplete | 9600/8/N/1 defaults, stream failure cases and separately real unplug/replug/physical-device evidence |
| AutoInterface, Backbone, Local, Pipe | **Missing** native interface families | Auto IPv6 group/discovery/expiry; Backbone MTU/watermarks/flap/reconnect/egress recovery; authenticated shared local transport; owned Pipe child/HDLC/5-second respawn/cancel. Source anchors below |
| KISS/AX25, RNode/RNodeMulti, I2P, Weave, Android adapters | **Missing** native families; hardware/platform acceptance **unverified**, not implied by stream simulators | Per-device framing/config/flow-control/recovery, nested radio settings, SAM persistence/reconnect and Android permissions/adapters; no desktop-only platform assumptions |
| rnsd (`Utilities/rnsd.py:40-71`) | **Partial**: daemon/basic config/verbosity/example, no complete service/interactive/version surface | Real-process stdout/file/service/signal/exit/restart matrix |
| rnid (`Utilities/rnid.py:109-154,202,397-945`) | **Partial**: local generate/show only; --show is a .NET addition | Import/export encodings/public/private keys, network lookup/aspects/announce, crypto/RSG/RSM/metadata/stdin/stdout/force/cache/timeout/version; independent vectors and native feature flows |
| rncp/rnx (`Utilities/rncp.py:99-145,201,799-820`; `rnx.py:206-219,556-576`) | **Partial**, both-direction substantive transfer/command evidence retained; CLI/config/process/failure closure missing | Allow-file precedence (not union), home expansion, writable output exit 4, PHY/version, streamed file bounds; binary output, zero/negative limits, failed spawn/timeout/cancel and authorization. Current output cap truncates only after collection |
| rnstatus/rnpath/rnprobe (`Utilities/rnstatus.py:876-906`; `rnpath.py:484-505`; `rnprobe.py:213-222`) | **Missing**, dependent on runtime management | Local/remote auth, tables/JSON/sort/filter/monitoring/drop/blackhole options and probe size/count/wait/timeout; actual-process/native outputs and failures |
| rnsh (`Utilities/rnsh/args.py:47-69`, `protocol.py:46-148`, `process.py:183-223,371`, `session.py:88`) | **Missing**, distinct from rnx interactive | Protocol v1 Channel messages, PTY/window resize/streams/EOF/signals/auth/session retry, real-terminal acceptance per OS |
| rnodeconf (`Utilities/rnodeconf.py:1480-1547`) | **Missing**, device acceptance **unverified** | Read-only simulator first, then explicitly authorized owned-device firmware/EEPROM/radio/Wi-Fi/Bluetooth/display/trust operations; real flashing evidence cannot come from simulation |
| rnir/rnpkg (`Utilities/rnir.py:40-50`, `rnpkg.py:39-49`) | **Upstream placeholders**, initialize and exit; rnpkg has minimal example output | Track observed CLI-shell behavior, not an invented package manager |
| Vendored ConfigObj/MessagePack/platform/I2P/netinfo/transmit dependencies | **Not applicable as Python module copies**; equivalent configuration/wire/SAM/network/bounded-transmit contracts remain applicable | Use native facilities with differential external behavior and explicit ownership; do not port vendored threading/interpreter checks |
| LXMF/custom telemetry; absent audio/video extensions | Existing supported regressions retained; expansion **not applicable** as RNS parity | Preserve current evidence without implying these extensions fill missing RNS utilities or interfaces |

Missing interface anchors/options are explicitly inventoried:
`AutoInterface.py:43-70,105-113,633` (29716/42671, group reticulum, MTU 1196,
22-second expiry);
`BackboneInterface.py:42-69,133-142,843-870` (1 MiB MTU, watermarks,
fast-flap blocking and pinned egress recovery);
`LocalInterface.py:53,153,303,356` (TCP/Unix variants, 8-second reconnect);
`PipeInterface.py:64-82,113,193`;
`KISSInterface.py:85-97,178,292,381`;
`AX25KISSInterface.py:92-105,191,320,396`;
`RNodeInterface.py:150-169,428,738,1175,1257,1452`;
`RNodeMultiInterface.py:146,163-228,938` (up to 11 subinterfaces);
`I2PInterface.py:76,322-330,729-762` (SAM/persisted destination/15-second peer
reconnect);
`WeaveInterface.py:49,560,816-884,1008` (device WDCL, MTU 1024, 20-second
peer timeout);
and Android Serial/KISS/RNode USB/Bluetooth/runtime adapters.
These are implementation/acceptance slices, not evidence of attached devices.

Runtime configuration keys requiring explicit matrix closure include sharing/
instance/RPC fields; static_transport_identity/local_hops_delta/network_identity;
link_mtu_discovery/use_implicit_proof/default_gravity; management/probe/error
policy; discovery/source/stamp/autoconnect/blackhole fields; default_ar_*
and ic_*/ec_* controls; qlen_in_data/announce/pr/il; and logtimestamps/level 8.
Upstream I2P autoconnect and Yggdrasil/onion automatic-connect detection are
explicitly unimplemented (`Discovery.py:810-821`), not missing .NET features
to invent. Full I2P bearer functionality remains a real separate obligation.

#### Architecture ownership and review closure

Keep Core's codec/names/identity ownership, Crypto's secret-bearing primitive
ownership, Transport's endpoint/routing/link/resource/channel state, Interfaces'
bearer lifetimes, and Applications' configuration/storage/CLI boundaries.
Use native async/cancellation/channel/storage abstractions instead of Python
singletons, watchdog threads or reflection-based handlers. Concrete unresolved
ownership boundaries are link-manager stop versus disposal/restart; unbounded
outbound/callback/reader buffers and registries; owned handler/resource work;
ratchet key snapshots versus concurrent zeroing; diagnostic delivery; and stable
interface/original-wire-announce references in restart stores.

This is a **bounded expansion inventory, not completed Parity 1 or a parity
certification**. The review did not exhaust every Transport jobs/preprocessing
branch, every malformed advertisement combination, every CLI/config/manual
option/default combination, vendored crypto internals or every example/test
assertion. These remain explicit inventory-closure work, alongside the
feature-specific gaps now expanded in `plan.md`. No newly found correctness gap
is marked fixed by this documentation slice.

Windows-only execution cannot certify Unix local sockets/PTYs, Linux/macOS/
FreeBSD behavior, Android/iOS permissions, ARM/RISC-V native deployment, or
serial/TNC/RNode/Weave hardware. Appropriate OSes/devices and I2P/firmware
resources are needed for their own final acceptance, not a blanket blocker on
ready parser/RPC/persistence/Windows bearer/CLI/simulator work. No CI workflows
were created because earlier parity requirements remain open.

### Read-only Git helper contract and implementation (2026-10-19)

Inspected the existing verified hosted-source checkout, without downloading or
advancing the reference: `a29bfb51c468945c96a1ee22cae0114e993e2391`.
SHA-256 of `RNS/Utilities/rngit/client.py`:
`78358331ddac087860fb8a1f350632d3875917782dc000681d48597affd8936b`;
`server.py`:
`6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693`.
The focused source-backed contract/acceptance criteria are in `docs/rngit.md`.
Reviewed `main`, configuration application, `run`, `handle_git_list`,
`process_fetch_queue`, and the server's `handle_list`/`handle_fetch` before
implementing the helper. No new wire format or regenerated historical vector
was introduced.

`RngitSession` owns an identified link, validates remote listings, and imports
verified bundles without changing any refs. Both direct `rngit` operations and
Git's helper reuse it. Global have tips include remote-tracking refs, avoiding
the Python client's same-name-local-branch assumption for incremental fetches.
The direct client's ref transaction, HEAD/config ownership, bare-only interface
and staging behavior remain intact. The helper advertises fetch/option only,
starts networking lazily, accepts progress options, rejects unsupported
commands, and leaves repository/ref/checkout ownership to Git. Native
installation is a copy of the native application under `git-remote-rns[.exe]`
on PATH, with executable-name dispatch, not a wrapper or a Python dependency.

Intentional differences from the upstream helper: no push capability or write
requests; .NET configuration/identity storage rather than Python client config,
aliases or shared-instance attachment; lazy rather than eager connection;
consistent exit 1 for helper failures rather than upstream startup 1/abort 255;
explicit malformed-input/size validation and one overall deadline. These do
not imply complete Git or runtime/configuration parity. Remaining Git hosting,
write/signature behavior stays in Parity 2; Python configuration compatibility
stays in the runtime inventory.

Baseline `dotnet build DotReticulum.slnx -c Release` passed with zero warnings;
`dotnet test DotReticulum.slnx -c Release --no-build`: 335 passed, 40 opt-in
skipped, zero failed. Focused helper/direct-client tests initially passed
35 with one live skip. Running the direct-client test with the actual pinned
Python server passed all 11 cases, including missing-object incremental
retrieval, no-change fetch and failures.

An additional separate-process idle-stdin probe exposed that a Windows
anonymous-pipe read did not honor cancellation. The helper now bounds the wait
itself, not just the underlying read's cancellation token, so the overall
deadline can end the helper even with Git's input pipe still open.

The first ordinary-clone acceptance run also caught Git's intentionally invalid
temporary HEAD (`refs/heads/.invalid`) during object import. Instead of changing
Git's HEAD or weakening object checking, the importer supplies the requested
object IDs explicitly to `git fsck --full --strict --no-reflogs <ids>`.
An isolated reproduction verified this checks objects successfully without
requiring Git to have finalized its refs.

#### Ordinary Git, native hosted-source retrieval

Published win-x64 with ILLink/ILC warnings as errors, copied the actual native
executable to `git-remote-rns.exe` and placed that directory first on PATH.
Ordinary `git fetch --progress --no-tags origin` used the helper to retrieve
**11,502 bundle bytes** through `reticulum.thecryptosquid.com:4242`. The .NET
node observed the repositories destination at **six hops**, received the
146-ref listing, and Git updated `refs/remotes/origin/master` from
`7f2b3b9b524c9386316379af1313b43a5e4f7a5d` to
**`a29bfb51c468945c96a1ee22cae0114e993e2391`**.
Its tree is **`24d4c393de9fd57b933836cea68891fd953ab060`**.
The delta has **92 newly reachable objects**. `cat-file -e` proved the target
commit absent before and present afterward; strict fsck and the seed-ancestor
check passed. HEAD remained unborn in this deliberately seeded bare fixture,
demonstrating that the helper did not rewrite it.

This was **incremental retrieval**, not a full network clone. History through
the parent was seeded by local Git from the previously verified P0 bare
repository (whose historical seed originally came from GitHub). No Python
client performed any production networking. Object consistency and the
unchanged live pin are verified; publisher/release-signature authentication
is still not claimed.

Exact hosted-flow commands (local artifact root below, no secrets committed):

```powershell
$root = 'C:\Users\dennis\.copilot\session-state\60ac68ce-5c68-4055-abf1-ef8136a7a065\files'
$source = 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\reticulum-rns-seed.git'
$seed = Join-Path $root 'helper-hosted.git'
git init --bare $seed
git "--git-dir=$seed" fetch --no-tags $source 7f2b3b9b524c9386316379af1313b43a5e4f7a5d
git "--git-dir=$seed" update-ref refs/remotes/origin/master 7f2b3b9b524c9386316379af1313b43a5e4f7a5d
git "--git-dir=$seed" config remote.origin.url rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum
git "--git-dir=$seed" config remote.origin.fetch '+refs/heads/master:refs/remotes/origin/master'
git "--git-dir=$seed" cat-file -e a29bfb51c468945c96a1ee22cae0114e993e2391 # nonzero as required
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o (Join-Path $root 'git-native') --nologo -v minimal
Copy-Item (Join-Path $root 'git-native\DotReticulum.Applications.exe') (Join-Path $root 'git-native\git-remote-rns.exe')
$env:PATH = (Join-Path $root 'git-native') + ';' + $env:PATH
$env:DOTRETICULUM_RNS_CONFIG = Join-Path $root 'helper-public'
$env:DOTRETICULUM_RNGIT_TIMEOUT = '900'
git "--git-dir=$seed" fetch --progress --no-tags origin
git "--git-dir=$seed" fsck --full --strict
git "--git-dir=$seed" rev-parse refs/remotes/origin/master 'refs/remotes/origin/master^{tree}'
git "--git-dir=$seed" cat-file -e a29bfb51c468945c96a1ee22cae0114e993e2391
git "--git-dir=$seed" merge-base --is-ancestor 7f2b3b9b524c9386316379af1313b43a5e4f7a5d refs/remotes/origin/master
```

All commands exited zero except the intentionally missing-object precheck.
`helper-public\config` contains the TCP client gateway above; it is an observed
public route, not a guaranteed service. `helper-hosted-fetch.log` retains
stderr progress and positive received-byte evidence outside source control.
A subsequent `git "--git-dir=$seed" fetch --no-tags origin` also exited zero;
Git only needed the ref listing and did not ask the helper to fetch objects.
`helper-hosted-nochange.log` retains that observation.

The post-change full live-reference regression
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v minimal`,
with `DOTRETICULUM_LIVE=1`, the verified hosted reference/Python environment and
the new native application in `DOTRETICULUM_NATIVE_EXE`, passed **402/402,
zero skipped/failed**. This includes the existing native encrypted/compressed
multi-megabyte resource path and direct rngit regression, not just native help
or publication. A separate published-native idle helper with stdin still open,
`DOTRETICULUM_RNGIT_TIMEOUT=0.5`, exited 1 with empty stdout and the explicit
overall-timeout stderr diagnostic within the ten-second bound.

#### Managed/native local Git acceptance

The supporting fleet work owns only the Pester Git suite/runner and standalone
C# fixture tool. The coordinator owns production integration, shared contracts,
documentation, hosted retrieval and commits. Fixture content/history creation
and the incremental commit are C#; PowerShell launches real application/Git
processes, asserts their outputs/exit codes/persisted results, and owns bounded
cleanup. Python only starts the unchanged pinned `ReticulumGitNode` with the
previously disclosed test-environment allowances.

Pester passed **26/26, zero skipped/failed**, on both the managed apphost and the
published native executable. This includes the existing five tooling/three CLI
cases plus 18 Git cases: executable discovery/dispatch; exact offline
capabilities/options and clean stdout; `RNS_CONFIG` fallback; refs/HEAD; working,
bare and feature-branch clones; branches/tags/remote-tracking refs and fsck;
positive received-byte, previously-missing-commit/tree incremental retrieval;
diverged local-branch preservation; no-change fetch; denied repository,
response-size refusal, malformed commands/URL, push rejection, unreachable
destination and open-stdin timeout.

Bare incremental fetch is tested separately: without a fetch refspec, normal
Git updates FETCH_HEAD to the fetched commit/tree but preserves symbolic HEAD
and local branches. Both missing objects and nonzero received bundle bytes
are mandatory assertions, not optional diagnostics. Native cases exercise
the actual published feature, not just `--help`.

```powershell
$old = 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files'
$python = Join-Path $old 'refvenv\Scripts\python.exe'
$reference = Join-Path $old 'upstream-reticulum-rns'
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference -Application (Join-Path $root 'git-native\DotReticulum.Applications.exe')
```

Commands exited zero. Environment: .NET SDK **10.0.402**, Git
**2.52.0.windows.1**, PowerShell **7.6.6**, Pester **5.7.1**, Windows x64.
`run-tests.ps1 -Live -Pester` now forwards the verified Python/reference
environment and enables the Git suite rather than silently omitting it.
Response buffering, detached HEAD, SHA-256 helper negotiation, interrupted/
corrupt transfers, impaired/concurrent Git sessions, pruning/deletions,
shallow/filter operations and non-Windows native installation remain outside
this read-only gate's exercised evidence. Unsupported capabilities are not
advertised; subsequent parity phases retain the broader work.

#### Final full-suite coverage and integration

An initial default-parallel live Coverlet run in `coverage-git-gate` failed
five Applications cases (native resource, direct rngit, Python rnx and both
live link-transport cases), with **397 passed / 5 failed / zero skipped**;
it is retained as failed evidence, not presented as a successful coverage run.
The direct rngit trace spent the live-link interval on redundant per-ref Git
validation subprocesses before fetching. Shared source-backed ref validation
now replaces those launches, with differential unit cases against Git's
`check-ref-format`. Other time-sensitive live scenarios contended during
instrumentation. The coverage runner now serializes projects (`-m:1`) and xUnit
collections (`xUnit.ParallelizeTestCollections=false`) only for coverage runs;
no test timeout, assertion or production protocol timer was weakened.

Final commands and results:

```powershell
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe (Join-Path $root 'git-native\DotReticulum.Applications.exe') -Coverage -ResultsDirectory (Join-Path $root 'coverage-git-gate-serial')
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe (Join-Path $root 'git-native\DotReticulum.Applications.exe') -Pester -Filter FullyQualifiedName~GitRemoteRnsTests
```

All exited zero. Full live/native coverage: **402 passed, zero skipped/failed**.
Final managed Pester: **26/26**; final runner-integrated native Pester:
**26/26**, preceded by **25/25** helper unit/differential cases. The final native
application was republished with both warning-as-error flags before this run;
the actual native resource and Git paths ran successfully. Production coverage
merges six reports by source/line/branch identity, counting shared code once.
Pester/native external processes are not counted as managed Coverlet hits:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 913/1394 (65.49%) | 723/1577 (45.85%) |
| Core | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2438/2800 (87.07%) | 1310/1701 (77.01%) |
| **Overall** | **4907/5873 (83.55%)** | **2770/4145 (66.83%)** |

The focused read-only Git-first gate is complete within this recorded scope.
It does not complete the subsequent repository-wide inventory or remaining
Git/runtime/interface/endpoint/utility/platform/release phases.

### Git-first parity sequencing (2026-10-19)

Prioritized a focused read-only native `git-remote-rns` gate before the repository-wide contract inventory. Its scope includes the upstream helper contract, Git-discoverable packaging, ordinary clone/fetch, ref ownership, managed/native local-reference acceptance and actual hosted-upstream retrieval. Push, hosting and signing remain in the later Git phase; the initial gate must reject unsupported operations rather than imply full helper parity. This provides a normal Git workflow for ongoing upstream comparison without making an exhaustive audit a prerequisite. No helper implementation is claimed by this roadmap change.

### Roadmap realignment to upstream feature parity (2026-10-19)

Replaced the planned push-to-talk audio and adaptive-video phases with upstream RNS feature-parity phases. The target is high-fidelity observable behavior and interoperability implemented as modern, idiomatic native .NET, not literal translation or Python API identity. The new checklist starts with a source-backed contract inventory and then covers Git integration, runtime/configuration, transport controls, endpoint/transfers, upstream interfaces, utilities and acceptance evidence. That inventory remains to be completed: the roadmap is not an exhaustive source audit or a new compatibility claim.

Removed speculative WebSocket and separate C++/Rust interoperability milestones from the RNS parity roadmap; Python remains the authoritative reference. Existing LXMF and custom telemetry are retained without expanding them as RNS parity work. Preserved the completed P0 record as a bounded historical milestone. Updated the cohesive agent instructions and README to match the new scope. No production behavior changed.

### .NET-focused tooling, Pester and hosted reference adoption (2026-10-19)

Adopted the language boundary: C# implements the solution, fixtures, scenarios and protocol assertions; PowerShell 7 owns development orchestration/reporting; Python remains only at the actual upstream API boundary. Replaced the Python coverage merger and its unittest suite with `tests/coverage_summary.ps1` and Pester tooling tests. The PowerShell implementation preserves hit merging, line/branch deduplication, source-path normalization, test-assembly exclusion, complete-production-module checks and failure exit codes. It reproduced the earlier P0 report exactly when run against those saved reports. The rngit fixture's Git commands, payload creation, branch/tag setup, seed generation and expected object IDs moved into `RngitTests.cs`; its Python adapter still hosts the unmodified upstream server and emits reference observations. Other Python files were reviewed: upstream vector generators, acceptance verifiers, API/callback adapters, actual-import checks and the disclosed Windows reference shim remain legitimate boundary code, not disguised embedded Python or rewritten reference logic.

`tests/run-tests.ps1` runs the .NET suite with optional live reference, native peer, fresh coverage and Pester stages, propagates failures and restores environment overrides. An initial run exposed PowerShell's empty-versus-absent environment behavior selecting an empty external-daemon executable; the runner now removes the override explicitly. A filtered six-case replay confirmed caller environment restoration. Final coverage runs below were sequential, with no other builds/tests touching instrumented outputs.

Added **Pester 5.7.1+** under `tests/Pester` with `tests/run-pester.ps1`: five tooling cases (complementary hits/exclusions, missing modules, malformed XML, filtered-coverage refusal, existing-results refusal) and three real separate-process CLI flows (identity create/reload/overwrite refusal, rngit invalid-input failure, authenticated rncp send/fetch). The rncp flow uses an isolated TCP loopback server/client, a **2,400,024-byte** compressible file, actual exit codes and SHA-256 checks of both uploaded and fetched files. All processes have bounded waits, drained stdout/stderr and owned-process cleanup. These are application-boundary tests, not a replacement for xUnit protocol coverage. The suite passed **8/8 against both the Release DLL and the win-x64 NativeAOT executable**; no Python/public gateway is required for Pester. Module installation is explicit and documented, not automatic. No CI workflows were added.

The authoritative reference is now **`rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`**, not its GitHub mirror. Materialized a new working checkout locally from `reticulum-rns-seed.git`, whose missing objects were fetched by our .NET client during P0. Independent strict fsck passed; checkout HEAD **`a29bfb51c468945c96a1ee22cae0114e993e2391`**, tree **`24d4c393de9fd57b933836cea68891fd953ab060`**, source version **1.5.7**. `reference_environment.py` now enforces that SHA for live tests, and the output-verification adapters also use its actual-import checks. LXMF remains pinned to **1.2.1**. Existing stored vectors, generation scripts and their historical comments retain the older SHA that generated them; they have not been relabeled or regenerated. README, contribution guidance and durable instructions describe the hosted source as authoritative and GitHub as a comparison mirror. This does not add publisher-signature verification.

**At this milestone there was no `git-remote-rns` integration helper.** This limitation is superseded by the read-only helper implementation and verification record above. The original native `rngit clone`/`fetch` client was invoked directly, with installed Git handling local objects.

Final exact commands (all passed; artifacts outside source control):

```powershell
$artifacts = Join-Path $env:USERPROFILE '.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files'
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester -ResultsDirectory (Join-Path $artifacts 'coverage-pwsh-default-final')
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o (Join-Path $artifacts 'pwsh-native-final') --nologo -v minimal
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python (Join-Path $artifacts 'refvenv\Scripts\python.exe') -Upstream (Join-Path $artifacts 'upstream-reticulum-rns') -NativeExe (Join-Path $artifacts 'pwsh-native-final\DotReticulum.Applications.exe') -Coverage -Pester -ResultsDirectory (Join-Path $artifacts 'coverage-pwsh-rns-final')
```

Default .NET: **335 passed / 40 skipped / 0 failed**, then **8/8 Pester passed** on the managed CLI. Hosted-reference/native .NET: **377 passed / 0 skipped / 0 failed**, then **8/8 Pester passed** on NativeAOT. Publish passed with ILLink/ILC warnings as errors and no warnings. Production coverage below is .NET Coverlet coverage only; it does not count Pester scripts or claim combined native-process coverage. Each measurement merged six reports, counting shared production sources once:

| Project | Default lines | Default branches | Hosted live/native lines | Hosted live/native branches |
| --- | ---: | ---: | ---: | ---: |
| Applications | 648/1282 (50.55%) | 485/1438 (33.73%) | 839/1282 (65.44%) | 620/1438 (43.12%) |
| Core | 681/722 (94.32%) | 379/421 (90.02%) | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 387/429 (90.21%) | 192/270 (71.11%) | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2406/2800 (85.93%) | 1282/1701 (75.37%) | 2438/2800 (87.07%) | 1313/1701 (77.19%) |
| **Overall** | **4608/5761 (79.99%)** | **2502/4006 (62.46%)** | **4833/5761 (83.89%)** | **2670/4006 (66.65%)** |

A separate invocation of `reference_environment.verify_reference()` against the old GitHub checkout correctly failed with an explicit SHA mismatch. After verification, the inspected `pwsh-native-final` publish directory was removed; the hosted bare repository, new reference working checkout and coverage evidence remain outside source control. Other operating systems were not exercised by this change.

### P0 closeout (2026-10-19)

**P0 is complete within its recorded scope, not a claim of complete Reticulum feature parity.** This record supersedes the earlier limitations concerning swallowed announce/LXMF observer errors, the external-daemon false pass, missing Channel impairment tests, missing Python transport peers and unexecuted native compression. Remaining limitations below and in the historical records are not silently promoted to supported features.

#### Actual hosted-upstream retrieval

The community wiki at `https://reticulum.miraheze.org/wiki/Rngit` publishes `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`. Public bootstrap definitions came from `https://directory.rns.recipes/api/directory/submitted`, linked by the official connectivity guide. The **win-x64 NativeAOT executable**, using only our .NET Reticulum stack through `reticulum.thecryptosquid.com:4242`, resolved the destination at **6 hops**, established a link and received **11,502 bundle bytes**. Git performed only local object import/checking; no Python client or Git network transport fetched on our behalf.

This was an **incremental single-branch fetch**, not a complete history download from scratch. Local history was seeded from GitHub through `7f2b3b9b524c9386316379af1313b43a5e4f7a5d` (our pin's parent). Before fetching, `git cat-file -e` confirmed that parent existed and our pinned commit did not. A full native clone had reached the server and begun a bundle but was cancelled because retransmitting approximately 144 MiB of existing history was unnecessarily slow. The successful incremental operation genuinely supplied missing Git objects, not just remote refs.

The server advertised 146 refs; only default branch `refs/heads/master` was selected. Retrieved HEAD is **`a29bfb51c468945c96a1ee22cae0114e993e2391`**, with tree **`24d4c393de9fd57b933836cea68891fd953ab060`**. Independent `git fsck --full --strict` passed, both the seed and our pin were verified ancestors, and the commit/tree matched the native client's output. The locator and Git hashes establish reachability and object consistency, **not authenticated publisher provenance**; release/publisher signature verification is not implemented or claimed.

Local artifact variables and the successful command (the publish command below recreates the temporary executable):

```powershell
$artifacts = Join-Path $env:USERPROFILE '.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files'
$exe = Join-Path $artifacts 'p0-native-gate\DotReticulum.Applications.exe'
$upstream = Join-Path $artifacts 'reticulum-rns-seed.git'
& $exe rngit fetch 'rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum' $upstream --config (Join-Path $artifacts 'rngit-public-gate') --timeout 900 --single-branch
git "--git-dir=$upstream" fsck --full --strict
git "--git-dir=$upstream" rev-parse HEAD 'HEAD^{tree}'
git "--git-dir=$upstream" merge-base --is-ancestor 7f2b3b9b524c9386316379af1313b43a5e4f7a5d HEAD
git "--git-dir=$upstream" merge-base --is-ancestor e40191b3d193b46b7f2d8a44424a594cd758839b HEAD
```

All commands exited 0. The bootstrap is an observed public gateway, not a required or guaranteed service. The fetched bare repository is retained outside source control for inspection and later `.NET rngit fetch` updates.

#### Requested three-way comparison

`gh api repos/markqvist/Reticulum/commits/master --jq '.sha'` still returned our **`e40191b3d193b46b7f2d8a44424a594cd758839b`** pin (RNS **1.5.5**, tree `ed83d80cd2ced7b41528f8e3f873b3bb54b62ad9`). In the fetched repository, `git rev-list --left-right --count e40191b3d193b46b7f2d8a44424a594cd758839b...HEAD` returned **`0 10`**: the Reticulum-hosted branch is ten commits ahead, without divergence, and reports **1.5.7**. `git log --oneline <pin>..HEAD`, `git diff --stat <pin> HEAD` and the focused source diff showed 29 changed files:

- **Discovery:** require transport-enabled peers before autoconnect, and force a static transport identity when discoverable interfaces are configured on a non-transport instance.
- **Backbone:** reset transmit-buffer/egress-stall state on teardown so an initiator can reconnect; accompanying regression test added.
- **Other changes:** version/changelog and regenerated manual updates. `Identity.py`, `Packet.py`, `Destination.py`, `Transport.py`, `Link.py`, `Resource.py`, `Channel.py` and the `rngit` sources are unchanged from our pin.

For **DotReticulum**, `RnsdHost.Create` already persists the transport identity on both transport and non-transport nodes. Interface discovery/autoconnect and `BackboneInterface`/its egress controller are not implemented, so those fixes do not have equivalent active .NET paths to patch. The fetched changes introduce no new mismatch in the implemented wire formats or rngit protocol. This is a behavior/feature comparison, not a Python-versus-C# text diff or a claim that the existing gaps have disappeared. Keep the existing test pin until an explicit, separately verified reference update.

#### Lessons applied to the solution

| Requirement | Applied evidence |
| --- | --- |
| Follow the latest requested scope | Completed P0, including its added external gate; no P1/P2/CI work started |
| Bounded delegation with parent continuation | Separate rngit and resilience deliverables were integrated and verified together; neither handoff was treated as milestone completion |
| Observable completion criteria | Missing objects, positive bundle bytes, exact commit/tree, strict fsck and comparison required; optional extensions remain explicit |
| One current tracker | `plan.md` is the sole phase/subphase progress checklist; this document retains the supporting history and evidence, and current README capability claims and related docs match it |
| No false-positive tests | Unsupported external-daemon local registration explicitly skips; callback assertions execute outside exception-isolating callbacks |
| Meaningful coverage | Production line/branch hits merged without double-counting; missing modules rejected; default and live results below |
| Real native execution | Native rncp decrypts/decompresses and compresses/encrypts a 2,200,017-byte roundtrip; native rngit fetched actual public-network source objects |
| Lifecycle/backpressure/fault isolation | Existing manager startup/cancellation/disposal/blocked-channel/child-failure tests retained; shared link fixture now awaits startup before sending instead of racing a background task |
| Subscriber-independent processing and reported faults | No-subscriber regression retained; announce handlers, announce observers and LXMF state observers report faults and preserve other observers/continued processing; compression fallback reports its error |
| Impairments and Python transport peers | Four deterministic Channel cases cover 20 ms one-way latency, lost data/proofs, window exhaustion, retry exhaustion, teardown and recovery; real Python leaf -> Python transport -> .NET router -> Python leaf covers three-hop discovery, announces, encrypted datagrams/proofs and links/data/proofs/teardown |
| Reproducible reference selection | Harness helper verifies actual imported RNS path, exact SHA, clean RNS sources, and LXMF 1.2.1 import/version where used; pinned RNS source is 1.5.5, not the venv's separately installed 1.5.7; reference-side shims remain disclosed |
| Policy limits versus protocol capabilities | LXMF peer transfer-size settings are recorded; bidirectional Python LXMRouter delivery, .NET-to-lxmd delivery and unimplemented propagation sync remain distinct claims |

#### Final commands and results

On Windows with SDK 10.0.402, `dotnet build DotReticulum.slnx -c Release --no-restore --nologo -v quiet` passed with **0 warnings and 0 errors**. Default coverage collection used `DOTRETICULUM_LIVE=0` and no native-executable override:

```powershell
dotnet test DotReticulum.slnx -c Release --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory (Join-Path $artifacts 'coverage-p0-default-closeout') --nologo -v minimal
python tests\coverage_summary.py (Join-Path $artifacts 'coverage-p0-default-closeout')
```

**Passed: 335; skipped: 40; failed: 0.** Per-project passed/skipped: Crypto 47/0, Core 77/0, Interfaces 55/7, Transport 97/12, Applications 32/13, LXMF 27/8.

Native publish and full live/native verification:

```powershell
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o (Join-Path $artifacts 'p0-native-gate') --nologo -v minimal
& $exe --help
$env:DOTRETICULUM_LIVE = '1'
$env:DOTRETICULUM_UPSTREAM = Join-Path $artifacts 'upstream-reticulum'
$env:DOTRETICULUM_PYTHON = Join-Path $artifacts 'refvenv\Scripts\python.exe'
$env:DOTRETICULUM_NATIVE_EXE = $exe
# Leave DOTRETICULUM_RNSD_EXE unset so in-process-only scenarios execute.
dotnet test DotReticulum.slnx -c Release --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory (Join-Path $artifacts 'coverage-p0-live-closeout') --nologo -v minimal
python tests\coverage_summary.py (Join-Path $artifacts 'coverage-p0-live-closeout')
python -m unittest discover -s tests -p test_coverage_summary.py -v
```

Publish/help passed without linker/compiler warnings. **Full live/native: 377 passed, 0 failed, 0 skipped** (Crypto 47, Core 77, Interfaces 62, Transport 111, Applications 45, LXMF 35); coverage-tool regressions **3/3 passed**. Tests include real upstream rngit clone/fetch/no-change/error cases and native compressed-resource transfer. Initial verification exposed the shared fixture's start-before-send race, corrected above; one earlier native-flow failure was not reproduced by focused replay or three subsequent full live runs. Failed/incomplete measurement runs are not included in the following totals.

With `DOTRETICULUM_RNSD_EXE=$exe`, `dotnet test tests\DotReticulum.Applications.Tests -c Release --no-build --filter FullyQualifiedName~DotNetAnnounce_ReachesBothPythonNodes_AndPythonEncryptsToIt --nologo -v minimal` separately reported **0 passed, 1 skipped, 0 failed**, explicitly identifying the unsupported mode. The inspected temporary native-publish and path-probe directories were removed after verification; fetched upstream, bootstrap configuration and measurement evidence were retained.

Both final measurements contain all six production modules, use fresh result directories and had no concurrent build/test mutation of their output files. Shared source is counted once:

| Project | Default lines | Default branches | Live lines | Live branches |
| --- | ---: | ---: | ---: | ---: |
| Applications | 648/1282 (50.55%) | 485/1438 (33.73%) | 839/1282 (65.44%) | 620/1438 (43.12%) |
| Core | 681/722 (94.32%) | 379/421 (90.02%) | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 387/429 (90.21%) | 192/270 (71.11%) | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2408/2800 (86.00%) | 1285/1701 (75.54%) | 2439/2800 (87.11%) | 1311/1701 (77.07%) |
| **Overall** | **4610/5761 (80.02%)** | **2505/4006 (62.53%)** | **4834/5761 (83.91%)** | **2668/4006 (66.60%)** |

**Remaining scope/limits:** no publisher/release-signature verification, discovery/autoconnect, Backbone/egress controller, IFAC, shared instances, tunnels, ingress control/announce caps, TCP reconnect, MTU discovery, file-backed resources, LXMF propagation sync/tickets/delivery ratchets, C++/Rust interop, real serial hardware or other-platform validation. Native rnx/telemetry/LXMF were not individually exercised; deterministic .NET Channel impairments do not establish live Python impairment behavior. rngit is read-only and bare-repository-only, buffers resource bodies, and does not implement push, releases or shallow clones. P1/P2/additional interfaces and CI remain unchecked.

### Historical milestone evidence

- **P0-1** (Crypto.Tests 47, Core.Tests identity tests): RFC 8032/7748/5869 vectors; low-order key rejection; token tamper/length/key-size tests; upstream Token/Identity output opens in .NET and .NET output opens in upstream (`tests/Interop/ExportDotNetVectors.cs` + `VerifyDotNetOutput.py`: 14 tokens, 5 identities OK). rnid generate/show verified, and an upstream `Identity.from_file` loads a file written by the NativeAOT rnid.
- **P0-2** (Core.Tests total 32): all 128 flag combinations, 8 canonical upstream packets/hashes, every truncation, MTU/hop boundaries, reserved bit 7, destination hash vectors incl. plain.
- **P0-3** (Core.Tests +17 manager tests = 49 total; Interfaces.Tests 53 passing + 7 opt-in live tests skipped by default): `IPacketInterface`, `ReceivedPacket`, `PacketInterfaceManager` (single start, bounded channel 256 with wait-on-full backpressure, malformed packets dropped via `Packet.TryParse`, `SendAsync`/`SendExceptAsync`, aggregated dispose, failed/cancelled start disposes everything, receiver fault completes the channel with that exception); internal `HdlcPacketFraming`/`HdlcFrameDecoder`; `UdpPacketInterface`, `TcpClientPacketInterface`, `TcpServerPacketInterface` (max 64 clients, per-client decoder), `SerialPacketInterface` (System.IO.Ports or a caller-provided duplex stream). HDLC vectors in `tests/DotReticulum.Interfaces.Tests/Fixtures/hdlc-vectors.json` come from EXECUTING upstream `TCPInterface.process_outgoing` and `read_loop` (`GenerateHdlcVectors.py`; TCPInterface.py sha256 `d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d`), plus byte-by-byte/back-to-back/garbage/oversize/escape/fuzz tests. Loopback tests: UDP, TCP client<->server (both directions, 12 clients, max-client enforcement, garbage recovery), serial over an in-memory duplex pair.
  - **Live interop vs real Python RNS** (`tests/Interop/live_interfaces_harness.py` + `LiveInteropTests.cs`, opt-in): run with `$env:DOTRETICULUM_LIVE='1'; $env:DOTRETICULUM_PYTHON='<venv>\Scripts\python.exe'; $env:DOTRETICULUM_UPSTREAM='<pinned clone>'; dotnet test tests/DotReticulum.Interfaces.Tests -c Release --filter "FullyQualifiedName~LiveInterop"`. One Python RNS instance (pinned clone, fresh config dir, shared instance off, transport off) with a TCPServerInterface, a TCPClientInterface (to our TcpServerPacketInterface) and a UDPInterface (listen/forward ports paired with our UdpPacketInterface) announces a SINGLE destination every second and reports packets arriving on a PLAIN destination. Result on this machine (Windows, Python venv, 3 consecutive runs): 7/7 passed each time. (1) our `TcpClientPacketInterface` received real Python announces from the Python `TCPServerInterface`; (2) our `TcpServerPacketInterface` accepted the Python `TCPClientInterface` and received its announces; (3) our `UdpPacketInterface` received announces from the Python `UDPInterface`. Each announce parsed with our `Packet` codec (Header1, Single, announce, no context flag, 172-byte payload = 64+10+10+64+app_data), its destination hash equalled the Python destination hash and was re-derived from the announced public key + name hash. In the reverse direction, a .NET-built `Packet` (PLAIN data, valid header) sent over each of the three interfaces was delivered to the Python PLAIN destination callback with the correct payload and arrival interface (`Client on PyTCPServer`, `PyTCPClient`, `PyUDP`, hops=1), and Python's interface rx counters were non-zero on all three. Not exercised live: **serial** (no serial port/pty pair available on this Windows machine; it shares the HDLC code verified live over TCP and is tested over an in-memory duplex stream and against the upstream HDLC vectors only), TCP client reconnect (not implemented), IPv6, I2P/KISS TCP framing (not implemented).
  - NativeAOT: a throwaway app rooting `DotReticulum.Core` and `DotReticulum.Interfaces` published with `-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` for win-x64 and ran; the real Applications publish still succeeds and `--help` runs (artifacts deleted).
  - Deviations from upstream: decoder frame bound is the 500-byte MTU (upstream TCP uses HW_MTU 262144 so future link-MTU discovery must raise it via `HdlcFrameDecoder(maxFrameLength)`); invalid HDLC escapes discard the frame (upstream passes them through); frames that are not parseable packets are dropped at the interface; serial has no idle-timeout frame reset (upstream resets a partial frame after a timeout; we recover at the next flag) and uses the shared-flag TCP semantics (upstream serial toggles in/out of frame on each flag); TCP server rejects connections beyond 64 (upstream has no cap); no interface-level IFAC/ingress-control yet; TCP client has no reconnect (stream completes or faults).- NativeAOT publish (win-x64, warnings as errors) succeeded; needs `vswhere` on PATH in this environment.
- **P0-4** (Core.Tests 70 = +21 announce tests; Transport.Tests 20 passing + 6 opt-in live skipped by default; Crypto.Tests 47; Interfaces.Tests 53 + 7 skipped): `Announce.Create/Validate` (plain/app_data/ratchet/path-response, random_hash = 5 random bytes + 5-byte big-endian time, signature over dest_hash+public_key+name_hash+random_hash[+ratchet]+app_data, signature-then-destination-hash check order as upstream `validate_announce`), `DestinationRatchets` (generate, interval rotation, 512 retained, enforce flag, ratchet-aware decrypt; persistence-free), `KnownDestinations` (bounded, identity recall by destination or identity hash, app_data, announced ratchet with 30-day expiry, encrypt to ratchet), `AnnounceEngine` (inbound announce path of `Transport.inbound`: hops+1, known-destination key-conflict check, random_blob dedup, path freshness/expiry, upstream rate limiter, announce table with PATHFINDER_G/RW and neighbour-rebroadcast cancellation, handlers with aspect filter and path-response opt-in), `AnnounceProcessor` (glue to `PacketInterfaceManager`).
  - (a) Vectors from EXECUTING upstream (`tests/DotReticulum.Core.Tests/GenerateAnnounceVectors.py` -> `Fixtures/announce-vectors.json`): 6 real `Destination.announce` outputs (plain, app_data, ratchet, ratchet+app_data, path response x2) all accepted by our validator; re-creating them from the same identity/random hash/ratchet reproduces the upstream bytes exactly; 79 tampered/truncated variants plus a validly signed wrong-destination-hash case, all rejected by upstream `validate_announce` and by us.
  - (b) `tests/Interop/ExportDotNetAnnounces.cs` + `VerifyDotNetAnnounces.py`: 6 .NET-created announces (incl. max-size app_data) validated by upstream `Identity.validate_announce` (app_data, ratchet, recall checked), tampered copies rejected, tokens .NET encrypted to the announced identity/ratchet decrypted by upstream. OK.
  - (c) Live vs real Python RNS (`tests/Interop/live_announce_harness.py` + `LiveAnnounceTests.cs`; `$env:DOTRETICULUM_LIVE='1'; $env:DOTRETICULUM_PYTHON='<venv>\Scripts\python.exe'; $env:DOTRETICULUM_UPSTREAM='<pinned clone>'; dotnet test tests/DotReticulum.Transport.Tests -c Release --filter "FullyQualifiedName~LiveAnnounce"`): one Python instance (TCPServerInterface, transport off) with a ratcheted destination (ratchets enforced) and a plain one, both announcing every 2 s; our `TcpClientPacketInterface` + `AnnounceProcessor`. 6/6 passed in each of 4 runs on this machine: Python announces validated and recalled (app_data, ratchet; handler with and without aspect filter); we encrypted to the announced ratchet and Python (enforcing ratchets) decrypted via its destination callback; same for the non-ratchet destination; our ratchet and plain announces were accepted by Python (announce handler, `Identity.recall` identity hash/app_data match, `Identity.get_ratchet` equals our ratchet public key, `Transport.has_path` true); Python recalled our identity, encrypted to it (ratchet and plain) and our `DestinationRatchets.Decrypt`/`Identity.Decrypt` opened the packets. The live run found a real bug (`AnnounceProcessor` skipped `Process` when no callback was set); fixed with a regression test.
  - NativeAOT: throwaway app rooting Core/Transport/Crypto published win-x64 with ILLink/ILC warnings as errors, ran (artifacts deleted).
  - Not verified / deviations: no live multi-hop rebroadcast against Python (transport mode and header-2 rewriting belong to item 5; rebroadcast logic is unit-tested only); no gravity, unresponsive-path state, tunnels, local-client or discovery handling; rate limit is global rather than per interface; ratchet expiry is checked on read; all state in memory only; handlers run synchronously (upstream uses threads) and their exceptions are swallowed; HopLimit outcome cannot occur from the wire because `Packet.Parse` rejects hops >= 128; rebroadcast never echoes to the ingress interface; Python `app_data` None vs empty is mimicked.
- **P0-5** (Release 0 warnings; Crypto.Tests 47, Core.Tests 74, Transport.Tests 37 + 6 opt-in live skipped, Interfaces.Tests 55 + 7 skipped, Applications.Tests 12 + 4 opt-in live skipped): per-client `TcpServerPacketInterface` (each connection is its own interface via `ISpawningPacketInterface`; the manager adopts/removes children and a failing child no longer faults the manager); `TransportRouter` (path table with next hop/hops/expiry/interface, path requests on `rnstransport.path.request` incl. tag dedup, local-destination and cached-announce responses with grace delay, discovery forwarding by interface mode, announce rebroadcast with HEADER_2 rewriting and no echo to the source, DATA forwarding with transport_id handling and header-type conversion, reverse table for proofs, two-generation bounded packet hashlist, path/reverse/discovery expiry, persisted transport identity); `rnsd` (`rnsd --config <dir>`, ini-subset config with TCPServer/TCPClient/UDP/Serial, `--help`, `--exampleconfig`, Ctrl+C/SIGTERM shutdown, leveled logging; format in `docs/rnsd.md`). Upstream-derived evidence: the `rnstransport.path.request` destination hash vector comes from executing upstream (`6b9f66014d9853faab220fba47d02761`); router behaviour was implemented from reading upstream `Transport.py` and covered by unit tests with a fake clock, not by byte vectors.
  - **Live multi-hop vs real Python RNS** (`tests/Interop/live_transport_node.py` + `tests/DotReticulum.Applications.Tests/LiveTransportTests.cs`; `$env:DOTRETICULUM_LIVE='1'; $env:DOTRETICULUM_PYTHON='<venv>\Scripts\python.exe'; $env:DOTRETICULUM_UPSTREAM='<pinned clone>'; dotnet test tests/DotReticulum.Applications.Tests -c Release --filter "FullyQualifiedName~LiveTransport"`; optional `DOTRETICULUM_RNSD_EXE=<native rnsd>` runs the real binary instead of the in-process host): Python A <-TCP-> .NET rnsd (transport on) <-TCP-> Python B, both Python nodes without transport. 4/4 passed with the in-process host (18 s) and 4/4 with the NativeAOT win-x64 exe (the .NET-originated-announce test needs the in-process host and returns early in exe mode, so 3 were genuinely exercised there). Verified: (1) B announces, A's upstream accepts the rebroadcast (hops 2, `has_path` true, path hops 2; rnsd hops 1); (2) A sends an encrypted packet to B's destination, routed via .NET as HEADER_2, B decrypts it (hops 2) and A's delivery proof (PROVE_ALL) returns through the .NET reverse table and validates; (3) a third Python node joining late has no path, sends a path request and the .NET node answers from its path table (has_path true, hops 2); (4) a .NET-originated announce is accepted by both A and B (hops 1, app_data intact) and a Python-encrypted packet to it is decrypted by .NET. The live run found a real bug (a reset child connection faulted the whole manager and killed the daemon); fixed with a regression test.
  - NativeAOT: `DotReticulum.Applications` published win-x64 with ILLink/ILC warnings as errors; the native exe ran `rnsd --help` and served the live test above (artifacts deleted).
  - Not verified / deviations: no Python *transport* node in the topology (only leaf nodes), so Python-to-.NET transport-to-transport announce/path-request exchange is untested; links are not forwarded (link requests are logged and dropped; item 6) and there is no link table; no ingress control, announce bandwidth caps, tunnels, shared-instance mode, IFAC, held-announce table (a separate response table is used), packet cache (announce kept in path state), path-state responsive/unresponsive tracking; the .NET side does not generate proofs for local destinations (it only routes proofs); config is an ini subset only (AutoInterface/RNode/etc. unsupported); TCP client has no reconnect and any interface failing at startup aborts the daemon; UDP and serial multi-hop and graceful-shutdown of the native exe were not exercised live; path-lifetime/expiry timers are unit-tested only.
- **P0-6** (Release 0 warnings; Crypto.Tests 47, Core.Tests 77, Transport.Tests 89 + 12 opt-in live skipped, Interfaces.Tests 55 + 7 skipped, Applications.Tests 12 + 6 skipped): `MsgPack` (AOT-safe), `Bz2` (SharpZipLib wrapper), `Link` (ECDH establishment, LRPROOF, HKDF key, Token encryption, LRRTT, keep-alive/stale/close, identify, link data with proofs/receipts, request/response incl. via resources, callbacks), `LinkManager`, `TransportRouter` link table (multi-hop LINKREQUEST/LRPROOF/link-data forwarding), `Resource` (advertisement, part requests, sliding window, HASHMAP_UPDATE, bz2, checksums, proof, cancel/reject, progress, multi-segment), `Channel` + `StreamDataMessage`/`RawChannelReader`/`RawChannelWriter`.
  - Upstream vectors (executed upstream): link key derivation and LRPROOF byte-identical, LRRTT/keepalive/close layouts, resource advertisement msgpack, hashmap, compressed payload, StreamDataMessage bytes.
  - Live vs Python RNS (`DOTRETICULUM_LIVE=1`, `tests/Interop/live_link_node.py`): .NET->Python and Python->.NET links with data both ways, RTT, keep-alive, teardown by each side; 1 MB, 3.5 MB random and 2.2 MB text resources both ways byte-identical (sha256); request/response with resources and file responses; Channel (60 messages each way in order) and Buffer streams (350 kB / 200 kB sha256 identical); Python A <-> .NET rnsd <-> Python B: link, data with proof via link table, 2.6 MB and 1.8 MB resources, 300 kB file response, 14 s keep-alive, teardown, 80 channel messages and 400 kB buffer.
  - NativeAOT: publish of `DotReticulum.Applications` with ILLink/ILC warnings as errors succeeded; native exe ran (`--help`); artifacts deleted. The native exe did not execute Link/Resource/bz2 code paths (SharpZipLib bz2 is AOT-compiled but not run natively).
  - Not verified / deviations: fixed MTU 500, no MTU discovery; lenient LRPROOF hop check in the link table; no network-cache request for resource proofs; resources are in-memory `byte[]` (no file handles; file responses as bytes + metadata); Channel streams are .NET `Stream`s without BufferedWriter buffering, writer waits when window is full, Channel talks to Link directly, retransmit re-encrypts; Channel retry/teardown path not unit- or live-tested; no Python transport node in the multi-hop topology; resource vectors use a deterministic XOR in place of link encrypt.
- Deviations/hardening: MTU enforced on parse (default 500, optional `mtu` parameter for larger link MTUs; upstream only checks on pack); identity files must be exactly 64 bytes; Ed25519 public keys must be prime-order/canonical; Link destinations cannot be created directly.
- **P0-7** (commits 7db8987, a2cf3b2, c74384c, b9d6f14, 4fb53a7, d97de14, 7501b17 + docs): `ReticulumNode` shared client runtime (config, interfaces, transport, announce, links) in Applications; `src/DotReticulum.Lxmf` (LxMessage pack/unpack/hash/signature, stamps, LxmRouter-lite with opportunistic, direct and Resource delivery, proofs/states, retry queue); `rncp`, `rnx`, `telemetry` subcommands. Evidence: LXMF vectors from executing upstream LXMF 1.2.1 (unpack matches, upstream accepts our bytes). LIVE vs Python LXMRouter 6/6 (both directions; opportunistic 120 chars, direct 200, direct 600 000 chars via Resource; hash/signature/sha256/delivered checked). LIVE vs real lxmd 1/1 (.NET -> lxmd opportunistic, direct, 200 kB Resource; lxmd stored them; needs delivery_transfer_max_accepted_size raised). LIVE rncp 4/4 vs upstream rncp (send 5 000 011 B, fetch 3 000 037 B; upstream client send 4 000 019 B, fetch 2 500 003 B; all byte-identical) and rnx (.NET client -> Python listener; Python client -> .NET listener). Upstream rncp on Windows needed a shim (`tests/Interop/rncp_win_shim.py`: shutil.move of open file) and -s/-j path checks fail on Windows paths. Telemetry (DotReticulum-specific, docs/telemetry.md): 3 tests incl. over a .NET rnsd hop; no Python peer. NativeAOT publish: no warnings, 4.0 MB exe; native rncp transferred a 3 000 017 B file with identical sha256. Full run with DOTRETICULUM_LIVE=1: 357 passed, 0 failed, 0 skipped (Crypto 47, Core 77, Interfaces 62, Lxmf 34, Applications 32, Transport 105); live off: 318 passed. NOT verified/implemented: lxmd -> .NET (no propagation client/sync), ratchets on lxmf.delivery, tickets, shared-instance mode, native exe for rnx/telemetry/LXMF, files are held fully in memory.
