# DotReticulum development history

Development chronology, verification evidence, upstream comparisons and recorded limitations. Phase and subphase completion is tracked only in [plan.md](../plan.md); this document is not a second progress checklist.

Current live interoperability uses the authoritative Reticulum-hosted upstream at `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7). Stored reference vectors and their reproduction generators retain their original `e40191b3d193b46b7f2d8a44424a594cd758839b` provenance. Earlier records describe the pins and tooling used at that time, including Python coverage scripts since replaced by PowerShell.

## Verification records

### Artwork lettering correction (2026-10-19)

Removed square-cap protrusions from the R/S strokes and replaced the N's
mitered stroke with a filled outline with flat top/bottom edges. The SVG and
Windows exporter now agree on flat polyline caps and bevel joins. Regenerated
the PNG and all ICO frames with
`pwsh -NoProfile -File assets\branding\Export-Icons.ps1`; visually inspected
the enlarged lettering crop. Native win-x64 publish with ILLink/ILC warnings
as errors passed; the extracted icon matched the source ICO pixel-for-pixel.
No protocol or runtime behavior changed.

### Revised Reticulum-derived artwork (2026-10-19)

Supersedes the original mesh D artwork below at user request. The SVG now
manually traces the official Reticulum logo's foreground network and RNS
lettering, replacing the circular backdrop with a two-tone purple hexagon
using the C# icon palette. This is an adaptation, not a pixel-identical copy:
the original raster shadows are omitted. Source attribution, Mark Qvist's
copyright and the applicable root Reticulum License are identified in
`assets/branding/NOTICE.txt` and the README; the modified mark remains
explicitly unofficial. The Windows exporter now renders the revised SVG's
polygons, polylines, lines and outlined circles to the same PNG/ICO sizes.

`pwsh -NoProfile -File assets\branding\Export-Icons.ps1` regenerated the
assets. `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`
passed without warnings; native win-x64 publish with ILLink/ILC warnings as
errors passed. The exported hero image was visually inspected, and the
published native icon matched the ICO pixel-for-pixel at 32 pixels.
Published `--help` passed. No protocol behavior changed or live-reference
interoperability was rerun for this artwork revision.

### Original application and README artwork (2026-10-19)

Added an original violet/blue connected-mesh D mark, distinct from upstream
Reticulum branding. `assets/branding/dotreticulum.svg` is the editable source;
the README displays it at 240 pixels. A 1024-pixel transparent PNG and a
Windows ICO containing 16/24/32/48/64/128/256-pixel frames are exported from
that source with `pwsh -NoProfile -File assets\branding\Export-Icons.ps1`.
The exporter requires Windows System.Drawing; consuming the checked-in assets
does not. Applications uses `ApplicationIcon`, including NativeAOT executables
and renamed copies such as `git-remote-rns.exe`. No parity claims changed.

Validation:

- `dotnet build DotReticulum.slnx -c Release --nologo -v minimal` passed
  without warnings.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  360 passed, 40 opt-in skipped, zero failed; live-reference tests not enabled.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v minimal`
  passed without warnings, with Visual Studio Installer on PATH.
- Published native `--help`, `--generate <owned-temporary-file>` and
  `--show <owned-temporary-file>` passed; the temporary private key was removed.
  This asset-only change did not rerun native resource-transfer interoperability.
- Visually inspected the PNG and extracted managed/native executable icons;
  the native 32-pixel icon matched the source ICO pixel-for-pixel.
  Repeated export produced identical PNG/ICO SHA-256 hashes.

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

This was a **bounded expansion inventory, not completed Parity 1 or a parity
certification**. The subsequent **Parity 1 public-contract inventory closure**
record below supersedes its inventory-coverage limitation, not its implementation
limitations. At this point the review did not exhaust every Transport jobs/preprocessing
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

### Parity 1 public-contract inventory closure (2026-10-19)

This closes the **inventory and architecture baseline**, not the implementation
of the classified gaps. It extends, and must be read together with, the three
source-backed family matrices above. Those rows retain their narrower evidence
boundaries. Every public family, configuration-reader family, installed command
parser and shipped example now has a classification, owner and acceptance
route. A row classified **unverified** is an accounted-for obligation, not a
passing test. `plan.md` alone records implementation progress.

**Provenance and method.** Read the existing verified hosted working tree at
`C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files\upstream-reticulum-rns`,
pin `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7).
Read-only syntax inspection used that environment's
`refvenv\Scripts\python.exe` to enumerate reference declarations and argument/
configuration readers; it did not import/start RNS, generate vectors or execute
the reference's operations. Source bodies were inspected for the previously
uncovered jobs, preprocessing, advertisement/HMU, Channel/Buffer, CLI dispatch,
configuration and ancillary contracts. Existing .NET implementations and named
tests below were inspected rather than inferring support from project names.
No pin, stored vector, production code or test changed.

The initiating session supplied a clean `main` baseline: warning-free Release
build and **360 passed / 40 skipped**. These are **inherited results, not commands
run by this documentation audit**. The earlier full live/native/coverage and
Pester records remain historical evidence only; this audit did not repeat them.
The integrating session completed current full tests, native execution and fresh
coverage concurrently; its separate validation record below is not execution by
the source auditor. No new hardware, platform, publisher-authentication or
malformed-input execution evidence is implied by that regression run.

#### Coverage boundary and supplementary public API ledger

Source anchors here are relative to the pinned repository; `RNS/` is omitted
from module names unless ambiguous. The original family tables cover
Identity/Destination/Packet/Link/Resource/Channel/Buffer, Reticulum/Transport/
Discovery, all bearer families, utilities and Git routes. This supplement closes
the remaining observable families rather than cataloguing private helpers.

| Public family and exact source anchors | Status / .NET ownership and audited evidence | Remaining measurable contract |
| --- | --- | --- |
| Identity hashes/randomness, raw key import/export, public files, sign/validate/prove, recall/app-data/ratchet access (`Identity.py:101-239,352-393,485-508,625-687,707-957`) | **Partial** family; bounded valid crypto/identity-file slices **implemented** by Core `Identity`, `IdentityFile`, Crypto; `IdentityTests`, `IdentityFileTests`, `RfcVectorTests`, `TokenTests`. Memory recall in Transport `KnownDestinations` is not persistent recall | Include public-only file roundtrip, local-destination fallback, retain/use/expiry, absent/corrupt storage, invalid signatures and private-key-required failure. Primitive byte compatibility does not imply Python object/API identity |
| Destination key lifecycle, callbacks and request registration (`Destination.py:320-435,477-692`); Transport register/deregister destination/link/announce handler (`Transport.py:2877-2980`) | **Partial**: Core owns names/ratchets; Transport `LocalDestination`, router and `LinkManager` own registrations and policies. `DestinationTests` covers names, not full plain/group operation | Duplicate and invalid direction/type registration; removal during receive; replace/deregister request handler; allow NONE/ALL/LIST with identified/unidentified peers; APP proof returning true/false/throwing; default link acceptance, dynamic app-data and group key import/export |
| Packet send/resend/prove/hash and PHY accessors (`Packet.py:123-389`), receipt state/RTT/deadline/callback setters (`Packet.py:417-587`) | Codec slices **implemented** in Core `Packet`/`PacketTests`; orchestration **partial** in Transport router/`PacketReceipt`; PHY **missing** as an upstream-compatible surface | Preserve codec separation; test send twice/resend without prior send, receipt eligibility and conclusion timestamps, optional RSSI/SNR/Q absent/present per ingress, callback changes and late/invalid proofs |
| Link mode negotiation, metrics/time accessors, lifecycle, resource policies (`Link.py:73-183,186-227,512-704,920-935,1198-1306`) | **Partial** in Transport `Link`, `LinkTypes`, `LinkManager`; `LinkVectorTests`/`LinkTests` cover AES-256-CBC/normal RTT and teardown. Only AES-256-CBC is enabled upstream (`Link.py:121-144`) | Reserved/disabled AES-128/GCM/OTP/PQ modes are **upstream placeholders/disabled contracts**, not encryption features to invent. Differentially reject disabled/unknown modes, malformed 64/67-byte requests and signalling; verify age/inactivity/rate/PHY metrics, stale recovery and exactly-once close reasons |
| RequestReceipt/status/progress/response/time (`Link.py:1324-1498`); Resource inspection/progress/segment/metadata and advertisement accessors (`Resource.py:1138-1247,1263-1376`) | **Partial** in Transport `Link.Requests`, `Resource`, `Link.Resources`; normal large/small request/resource tests exist | Compare all intermediate and terminal observations, final progress, timeout/rejection/cancel/close and metadata policy; an idiomatic result object is acceptable but cannot omit observable failure/progress |
| Transport path/next-hop/bitrate/MTU/latency lookup, `await_path`, responsive states and queue/interface management (`Transport.py:2983-2992,3126-3322,3602-3722,3971-4116`) | **Partial** router `HasPath`/`HopsTo`/mode and bounded tables; **missing** upstream management, tunnels/cache/blackhole/persisted health. `TransportRouterTests` covers ordinary forwarding and expiry | Absent/detached path results, cancellation/deadline, fresh-vs-unresponsive selection, shared-client disappear/reappear, void queues versus orderly detach, authorized remote/local management |
| Runtime statistics and management API (`Reticulum.py:1366-1437,1498-2041`); traffic/profiling (`Transport.py:591-676`; `RNS/__init__.py:357-615`) | **Missing** full upstream surface. Applications custom telemetry is **not applicable as a substitute**; Transport/Interfaces own counters, Applications owns snapshots/RPC/rendering | Every returned field: parent/child interface identity, rates/totals, queues/drops/pressure, IFAC/protocol/filter violations, burst/flap/PHY/device/discovery, paths/rates/links, RSS and optional profiler data. Compare absent/unsupported values, snapshot concurrency, remote ACLs and monitor cancellation; do not synthesize device measurements |
| Logging/time/size/frequency/distance/PHY formatting, hex/base256 and exit (`RNS/__init__.py:91-349,641-664`) | **Partial** Applications `CliSupport` and utility-specific output; complete reference formatting/base256 **missing/unverified**. Python global log destination, decorator/context-manager syntax and abrupt interpreter exit are **not applicable as architecture** | Exact output where consumed by CLI/protocol; values at zero/unit transitions, negative/fractional/large values, Unicode/base256 roundtrip and invalid point rejection. Preserve observable levels/destination/errors while using native structured diagnostics and graceful ownership |
| Generic request/response MessagePack domain (`vendor/umsgpack.py:66-181,370-415,685-750,1030-1042`; `Link.py:473-510,804-918`) | **Partial** Core `MsgPack` supports scalar/bin/string/array/map, depth 32; `MsgPackTests` verifies that subset. Extension/timestamp values **missing**, complete numeric/invalid UTF-8/map-key differential **unverified** | Reference-generated extension/timestamp and numeric boundary corpus plus malformed nesting/length/map/UTF-8 cases. Define AOT-safe explicit value types and limits, or explicitly diagnose unsupported application values; Python extension registration/reflection machinery itself is **not applicable** |
| Dependency/platform and optional compiled backend (`Cryptography/__init__.py:34-68`, `Provider.py`; `vendor/platformutils.py:31-83`; `CRNS/__init__.py:1-100`) | **Not applicable** to reproduce Python imports, vendored AES/pure25519/SHA/ConfigObj/validator/I2P libraries, netinfo implementation, Cython loader/cache/CRNS_JOBS or Python version checks. Their resulting crypto, config, network/address, SAM and packaging contracts remain in the applicable rows | Native crypto providers and OS APIs must meet wire/observable contracts; AOT uses static code. Internal-provider malformed-input differences remain **unverified**, not excused by this architectural exclusion |
| Extension scope (`Resolver.py:31-34`; `Utilities/rnir.py:40-72`; `rnpkg.py:39-83`; `Discovery.py:810-821`) | Resolver body, resolver/package functionality and automatic I2P/Yggdrasil/onion connection detection are **upstream placeholders**. CLI parser/config-init/example-output shells remain observable. Existing LXMF/telemetry are supported regressions; new absent audio/video features **not applicable** | Execute placeholder shell help/version/init/error/exit behavior without claiming resolver/package functionality. Full I2P interface is still required, independently of unimplemented autodetection |

The Python `__all__` import surface is not a requirement to duplicate modules or
names. All exported endpoint/transfer behavior, metrics, callbacks and errors
map to the rows here/above; private crypto provider internals and private Git
rendering helpers are not independent parity features. Upstream tests
`tests/{channel,coalesced_transmit,egress,hashes,hdlc,hkdf,identity,ifac,link,throughput}.py`
are additional acceptance input, not evidence that .NET passes those tests.

#### Configuration schema ledger

Every key below is a contract even when its status is **missing**. Source
anchors identify the actual parser/default, not just a documentation example.
For each listed key, acceptance requires reference observations for omission,
one valid non-default, malformed type, boundary and conflicting alias where
applicable; assert normalized effective settings, resulting behavior and
diagnostic/exit, not merely parse success. Numeric clamp/ignore rules are part
of that comparison. Whole-format parsing includes comments, quoting, lists,
duplicate sections/keys, case, nested `[[...]]`/`[[[...]]]`, unknown keys, path/
home expansion, encoding, default-file creation and CLI/environment precedence.

| Section / complete key family | Reference parser and defaults | Current classification / owner |
| --- | --- | --- |
| Runtime sharing: `share_instance`, `instance_name`, `shared_instance_type`, `shared_instance_port`, `instance_control_port`, `rpc_key` | `Reticulum.py:215-327,478-507`; share true, ports 37428/37429; UNIX where supported, explicit tcp/unix; invalid hex RPC key logs and falls back | **Missing** Applications host/config + Interfaces local bearer; `.dotreticulum` versus reference search `/etc/reticulum`, `~/.config/reticulum`, `~/.reticulum` is an explicit path difference, not shared-instance support |
| Node/policy: `enable_transport`, `static_transport_identity`, `local_hops_delta`, `network_identity`, `link_mtu_discovery`, `enable_remote_management`, `enable_interface_management`, `remote_management_allowed`, `respond_to_probes`, `force_shared_instance_bitrate`, `panic_on_interface_error`, `use_implicit_proof`, `default_gravity` | `Reticulum.py:263-297,509-588`; transport/static/delta/remote/probes/panic false; MTU discovery/implicit proof/interface management true; identity path create/load and validated 16-byte ACL entries | **Partial** only enable_transport/basic implicit proof behavior; remaining config **missing**. Applications policy/storage, Core/Crypto identities, Transport behavior |
| Discovery/blackholes: `discover_interfaces`, `required_discovery_value`, `publish_blackhole`, `blackhole_sources`, `blackhole_update_interval`, `interface_discovery_sources`, `autoconnect_discovered_interfaces`, `autoconnect_unverified_implementations`, `autoconnect_interface_mode`, `autoconnect_interface_gravity`, `autoconnect_announces_to_internal` | `Reticulum.py:590-657`; disabled/empty unless configured, positive stamp/capacity, blackhole interval clamped to >=2 minutes; validated source hashes; mode aliases match interface modes | **Missing** Applications config/store + Transport discovery/blackhole coordination + Interfaces attach/detach |
| Global rate/queues: `default_ar_target`, `default_ar_penalty`, `default_ar_grace`, `ic_max_held_announces`, `ic_burst_hold`, `ic_burst_freq_new`, `ic_burst_freq`, `ic_pr_burst_freq_new`, `ic_pr_burst_freq`, `ec_pr_freq`, `egress_control`, `ic_new_time`, `ic_burst_penalty`, `ic_held_release_interval`, `qlen_in_data`, `qlen_in_announce`, `qlen_in_pr`, `qlen_in_il` | `Reticulum.py:659-730`; `Interfaces/Interface.py:72-96`; `Transport.py:134-162`: default inbound 1024/128/128/8; held 256, gravity 0; AR 3600/0/5; IC new 7200 s, announce frequencies 3/10, PR 3/8, burst hold/penalty 15 s, release 5 s; EC PR 5 | **Missing** full configuration/control; **partial** bounded .NET manager=256 and global announce limiter are intentionally different architecture, not equivalent prioritized queues. Applications validates, Transport/Interfaces enforce |
| Logging: `loglevel`, `logtimestamps`; constructor level/verbosity/logdest precedence | `Reticulum.py:232-245,306-315,460-472`; `RNS/__init__.py:58-156`; default level 4, range 0-8 | **Partial** Applications level 0-7 parser; timestamp/destination/rotation/level 8 **missing** |
| Common bearer: `type`, `interface_enabled`, `enabled`, `interface_mode`, `mode`, `gravity`, `ifac_size`, `networkname`/`network_name`, `passphrase`/`pass_phrase`, `ingress_control`, `egress_control`, all per-interface `ic_*`/`ec_pr_freq` above, `bitrate`, `announce_rate_target`, `announce_rate_grace`, `announce_rate_penalty`, `announce_cap`, `bootstrap_only`, `recursive_prs`, `announces_from_internal`, `announces_to_internal`, `ignore_config_warnings` | `Reticulum.py:843-984,1029-1182`; explicit enables ORed; mode aliases full/access_point/accesspoint/ap/pointtopoint/ptp/roaming/boundary/gateway/gw/internal; announce cap 2%; derived IFAC identity/signature and per-bearer size | **Partial** Applications parser implements enabled/type/mode subset with differing enable/alias behavior; controls/IFAC **missing**. Disabled unknown types and enabled unsupported types require deliberate diagnostics |
| Discoverable bearer: `discoverable`, `announce_interval`, `discovery_stamp_value`, `discovery_name`, `discovery_encrypt`, `reachable_on`, `publish_ifac`, `location_cmd`, `latitude`, `longitude`, `height`, `discovery_frequency`, `discovery_bandwidth`, `discovery_modulation`, `discovery_lxmf_address` | `Reticulum.py:988-1026`; `Discovery.py:136-258`; only eligible transport/discoverable interfaces; static identity implication and location command | **Missing**. Location command must be an owned, bounded child; no unattended arbitrary command execution during config inspection |
| UDP: `name`, `device`, `port`, `listen_ip`, `listen_port`, `forward_ip`, `forward_port` | `Interfaces/UDPInterface.py:40-115`; independent listen/forward sides and port override; bitrate guess 10 Mbit/s | **Partial** Applications host + Interfaces UDP. Literal-address bidirectional slice implemented; independent sides/device/DNS/address-family behavior **missing/unverified** |
| TCP server: `name`, `device`, `port`, `listen_ip`, `listen_port`, `prefer_ipv6`; client: `name`, `target_host`, `target_port`, `kiss_framing`, `i2p_tunneled`, `connect_timeout`, `max_reconnect_tries`, `fixed_mtu` | `Interfaces/TCPInterface.py:75-110,515-551`; default timeout/reconnect wait 5 s, unlimited tries; HW MTU 262144 | **Partial** Applications/Interfaces TCP; defaults/custom connect and HDLC peer-isolation slice exists, remaining settings/reconnect/large MTU **missing/unverified**. `i2p_tunneled` is construction metadata as well as a reader key, not a new standalone network protocol |
| Serial: `name`, `port`, `speed`, `databits`, `parity`, `stopbits` | `Interfaces/SerialInterface.py:75-90,144-217`; 9600/8/N/1 | **Partial** port/speed and injected-stream framing; other settings/recovery **missing**, hardware **unverified** |
| Auto: `name`, `group_id`, `discovery_scope`, `discovery_port`, `multicast_address_type`, `data_port`, `devices`, `ignored_devices`, `configured_bitrate` | `Interfaces/AutoInterface.py:43-113`; group reticulum, ports 29716/42671, MTU 1196, peer expiry 22 s; OS ignore lists/scope/address type | **Missing** Interfaces; simulate discovery and duplicate multicast first, then actual multi-device IPv6/OS evidence |
| Backbone server/client: TCP address/port/device/prefer_ipv6 and `block_fast_flapping`, `fast_flapping_threshold`, `fast_flapping_grace`, `fast_flapping_block_time`; client `target_host`, `target_port`, `i2p_tunneled`, `connect_timeout`, `max_reconnect_tries` | `Interfaces/BackboneInterface.py:42-69,133-142,843-870`; MTU 1 MiB; flap true/20/5/12 h; ingress/egress watermarks, stall and hysteresis constants | **Missing** Interfaces; preserve peer-specific blocking, frame/byte accounting and egress recovery, not just TCP subclass API |
| Local TCP/UNIX platform constructors; Pipe `name`, `command`, `respawn_delay` | `Interfaces/LocalInterface.py:53-85,153-183,303-325,356-390`; `PipeInterface.py:64-82,113-204`; local reconnect 8 s, pipe respawn 5 s | **Missing** Interfaces. Local endpoints are runtime-created, not invented `Reticulum` config keys; UNIX credentials/authentication and Windows TCP limits need distinct evidence |
| KISS: serial keys + `preamble`, `txtail`, `persistence`, `slottime`, `flow_control`, `id_interval`, `id_callsign`; AX25KISS substitutes `callsign`, `ssid` for ID settings | `Interfaces/KISSInterface.py:85-115,178-202,292-381`; `AX25KISSInterface.py:92-125,191-215,320-396` | **Missing** Interfaces; KISS commands/escaping, AX.25 addressing, timing/beacon/flow control and unplug/reconnect must be tested on simulator and owned device separately |
| RNode: `name`, `port`, `frequency`, `bandwidth`, `txpower`, `spreadingfactor`, `codingrate`, `flow_control`, `id_interval`, `id_callsign`, `airtime_limit_short`, `airtime_limit_long` | `Interfaces/RNodeInterface.py:150-169,428-483,738-781,1175-1257,1452-1477` | **Missing** Interfaces. Radio ranges/commands/firmware capabilities/status/errors/airtime and serial/Bluetooth transport; hardware **unverified** |
| RNodeMulti parent `name`, `port`, `id_interval`, `id_callsign`; nested `interface_enabled`/`enabled`, `vport`, `frequency`, `bandwidth`, `txpower`, `spreadingfactor`, `codingrate`, `flow_control`, `airtime_limit_short`, `airtime_limit_long`, `outgoing` | `Interfaces/RNodeMultiInterface.py:146-228,938-1013`; up to 11 virtual interfaces and shared parent radio transport | **Missing** Interfaces + nested Applications parser; test duplicate/invalid vports, per-child state and shared queues without one child stopping siblings |
| I2P: `name`, `storagepath`, `peers`, `connectable`, `ifac_size`, `ifac_netname`, `ifac_netkey`; Weave: `name`, `port`, `configured_bitrate` | `Interfaces/I2PInterface.py:76-113,322-330,729-762`; `WeaveInterface.py:49-83,560-622,816-884,1008-1041` | **Missing** Interfaces; I2P SAM/destination persistence/tunnel readiness/reconnect; Weave WDCL peers/switch/endpoint/telemetry/MTU. SAM/device failures and unavailable backend are distinct from unsupported config |
| Android Serial/KISS: corresponding serial/KISS keys; Android RNode adds `allow_bluetooth`, `target_device_name`, `target_device_address`, `ble_name`, `ble_addr`, `tcp_host`, `force_ble`, `force_tcp` and radio/airtime settings | `Interfaces/Android/SerialInterface.py:87-92`; `KISSInterface.py:97-109`; `RNodeInterface.py:359-378` | **Missing** platform adapters; **unverified** actual Android permissions/USB/Bluetooth/BLE lifecycle. Desktop SerialPort presence is not Android evidence |
| Git client `[client] ref_batch_size`; `[aliases]` destination hashes; `[logging] loglevel`; RNGIT_CONFIG/RNS_CONFIG, client_config/client_identity | `Utilities/rngit/client.py:55-76,130-213,679-718`; batch 25, clamp 0..1024, log -1..8 | **Partial** Applications helper; RNS_CONFIG and fixed 25 exist; aliases/client config **missing**. Zero batch is an upstream non-progress edge (`464-481`), not permission to hang .NET |
| Git node `[rngit] node_name`, `announce_interval`, `mirror_interval`, `record_stats`, `stats_ignore_identities`, `stats_push_ignore_identities`, `blocked_identities`; `[repositories]` groups, `[aliases]`, `[access]`, `[pages] serve_nomadnet`, `[logging] loglevel` | `Utilities/rngit/server.py:1979-2250,4912-5074`; announce 360 minutes; mirror hours clamped >=0; repository descriptions, .allowed inheritance/admin and null identity | **Missing** Applications hosting/store; aliases used for identities are distinct from helper destination aliases. Group paths, templates and stats survive restart or fail diagnostically |
| Git page `[pages] unicode_icons`, `media_conversion`; static/executable `templates/*.mu`; `RNGIT_MEDIA_BACKEND`; interactive `EDITOR` | `Utilities/rngit/pages.py:132-177,269-312`; `media.py:53-215`; `server.py:720,1844,1889`; page media default true, Unicode icons disable Nerd Fonts, custom template fallback/placeholders | **Missing** Applications. Compare icon/rendering/media policy, Markdown/Micron/syntax-highlighting and pagination/downloads; static template read/error, explicitly authorized executable template/editor timeout/output bounds/cleanup and absent backend. Defaults are not separate interface settings |

The reference's generated default and verbose example configuration are
observable outputs, not proof that all defaults occur in those examples.
Body parser anchors above take precedence when comments/examples differ.

#### Complete installed CLI option ledger

`setup.py:176-190` installs fourteen commands: rnsd, rnstatus, rnprobe, rnpath,
rnid, rncp, rnx, rnir, rnpkg, rnsh, rngit, rngcs, git-remote-rns and rnodeconf.
The helper is a stdin/stdout protocol, not argparse. All argparse commands
inherit `-h/--help`; common `--version`, verbosity and config switches below
still require real process/exit/output tests even where omitted for brevity
from earlier family headings. “Missing” includes packaging/dispatch:
Applications `Program.cs` currently dispatches only rnid, rnsd, rncp, rnx,
telemetry, rngit and git-remote-rns. No support is inferred for other names.

| Parser anchor | Complete argument family (aliases are part of the contract) | Classification / required process acceptance |
| --- | --- | --- |
| `Utilities/rnsd.py:65-71` | `--config`, `-v/--verbose`, `-q/--quiet`, `-s/--service`, `-i/--interactive`, `--exampleconfig`, `--version` | **Partial** Applications RnsdApp; capture help/example/version, file logging versus stdout, interactive stdin/EOF, signals, startup failure and exit/restart |
| `Utilities/rnid.py:109-154` | `--config`; `-i/--identity`, `-g/--generate`, `-m/--import-pub`, `-M/--import-prv`, `-x/--export-pub`, `-X/--export-prv`; `-v/--verbose`, `-q/--quiet`; `-a/--announce`, `-H/--hash`, `-d/--decrypt`, `-e/--encrypt`, `-V/--validate`, `-s/--sign`, `-S/--sign-message`, `-E/--embed-meta`, `--meta-spec`, `--raw`; `-w/--write`, `-r/--read`, `-f/--force`, `-I/--stdin`, `-O/--stdout`; `-R/--request`, `-N/--no-cache`, `-t`, `-p/--print-identity`, `-P/--print-private`; `-B/--base32`, `-b/--base64`, `-U/--base256`, `-F/--hex`, `--meta`, `--version` | **Partial** local generate/show only. Audit operation combinations via `validate_args:86-102`; all encoding/key-source/stdin/file/force/network/timeout paths, recursive multi-file crypto, RSG/RSM legacy/current/wrapped/embedded metadata (`397-1032`), EDITOR behavior (`1034-1058`). Secret output requires explicit request, not logs |
| `Utilities/rncp.py:799-820` | positional `file destination`; `--config`, `-v/--verbose`, `-q/--quiet`, `-S/--silent`, `-l/--listen`, `-C/--no-compress`, `-F/--allow-fetch`, `-f/--fetch`, `-j/--jail`, `-s/--save`, `-O/--overwrite`, `-b`, repeated `-a`, `-n/--no-auth`, `-p/--print-identity`, `-i`, `-w`, `-P/--phy-rates`, `--version` | **Partial** RncpApp/Service; send/fetch both directions already evidenced. Add all switch combinations/policies, periodic announce -1/0/positive, jail/symlink/traversal, allow-file precedence, missing/readonly paths, interruption, output naming/collision and exit 4 |
| `Utilities/rnx.py:556-576` | positional `destination command`; `--config`, `-v/--verbose`, `-q/--quiet`, `-p/--print-identity`, `-l/--listen`, `-i`, `-x/--interactive`, `-b/--no-announce`, repeated `-a`, `-n/--noauth`, `-N/--noid`, `-d/--detailed`, `-m`, `-w`, `-W`, `--stdin`, `--stdout`, `--stderr`, `--version` | **Partial** RnxApp/Service; authorized basic bidirectional execution evidenced. Require exact binary stdin/out/err, empty/negative/zero/large limits, failed spawn, timeout result, mirror return code, interactive EOF and bounded cancellation/child termination |
| `Utilities/rnstatus.py:876-906` | `--config`, `--version`, `--attach`, `--detach`, `--reload`; `-a/--all`, `-A/--announce-stats`, `-P/--pr-stats`, `-l/--link-stats`, `-B/--burst`, `-b/--blocked-ips`, `-t/--totals`, `-p/--pps`, `-q/--queues`, `-z/--profiling`, `-s/--sort`, `-r/--reverse`, `-j/--json`, `-R`, `-i`, `-w`, `-d/--discovered`, `-D`, `--show-stale`, `--show-unknown`, `-m/--monitor`, `-I/--monitor-interval`, `-v/--verbose`, positional `filter` | **Missing** Applications + management API; every column/JSON key/filter/sort/remote-auth/status/view combination, monitor EOF/signal and attach/detach/reload failure; absent device/profiler values must remain honest |
| `Utilities/rnpath.py:484-505` | `--config`, `--version`, `-t/--table`, `-m/--max`, `-r/--rates`, `-d/--drop`, `-D/--drop-announces`, `-x/--drop-via`, `-w`, `-R`, `-i`, `-W`, `-b/--blackholed`, `-B/--blackhole`, `-U/--unblackhole`, `--duration`, `--reason`, `-p/--blackholed-list`, `-j/--json`, positional `destination list_filter`, `-v/--verbose` | **Missing** Applications; local/remote table/rate/drop/lookup/blackhole results and auth, invalid hash/filter/max/duration, unreachable node, persisted and expired blackholes |
| `Utilities/rnprobe.py:213-222` | `--config`, `-s/--size`, `-n/--probes`, `-t/--timeout`, `-w/--wait`, `--version`, positional `full_name destination_hash`, `-v/--verbose` | **Missing** Applications; proof-based payload size/count/interval, success/loss/timeout statistics, zero/negative/oversized inputs, probe responder disabled/denied and unreachable routes |
| `Utilities/rnsh/args.py:47-104`; `rnsh.py:57-184` | `-c/--config`, `--rnsconfig`, `-i/--identity`, `-v/--verbose`, `-q/--quiet`, `-p/--print-identity`, `--version`; `-l/--listen`, `-s/--service`, `-b/--announce`, repeated `-a/--allowed`, `-n/--no-auth`, `-A/--remote-command-as-args`, `-C/--no-remote-command`; `-N/--no-id`, `-m/--mirror`, `-w/--timeout`, positional `destination`, `--` separates arbitrary command argv | **Missing**, not rnx interactive. Cover service-name identity sanitization/persistence, allowed-file reload, command restrictions/concatenation, mirrored exits, PTY/non-TTY, EOF/signal/window resize/retry/protocol mismatch and terminal restoration. Process TERM and RNS_REMOTE_IDENTITY environment (`session.py:335-337`) are observable too |
| `Utilities/rnodeconf.py:1480-1547` | `-i/--info`, `-a/--autoinstall`, `-u/--update`, `-U/--force-update`, `--fw-version`, `--fw-url`, `--nocheck`, `-e/--extract`, `-E/--use-extracted`, `-C/--clear-cache`, `--baud-flash`; `-N/--normal`, `-T/--tnc`; `-b/--bluetooth-on`, `-B/--bluetooth-off`, `-p/--bluetooth-pair`; `-w/--wifi`, `--channel`, `--ssid`, `--psk`, `--show-psk`, `--ip`, `--nm`; `-D/--display`, `-t/--timeout`, `-R/--rotation`, `--display-addr`, `--recondition-display`, `--np`; `--freq`, `--bw`, `--txp`, `--sf`, `--cr`; `-x/--ia-enable`, `-X/--ia-disable`, `-c/--config`; `--eeprom-backup`, `--eeprom-dump`, `--eeprom-wipe`; `-P/--public`, `--trust-key`, `--version`; `-f/--flash`, `-r/--rom`, `-k/--key`, `-S/--sign`, `-H/--firmware-hash`, `-K/--get-target-firmware-hash`, `-L/--get-firmware-hash`, `--platform`, `--product`, `--model`, `--hwrev`, positional `port` | **Missing** Applications/device tooling; parser/info and simulated command byte/default/failure tests first. Firmware acquisition/cache/hash/signature/trust/EEPROM/manufacturing and actual flashing need separately authorized owned hardware; unavailable flash tool/network/device must fail clearly. Never log PSKs/private keys |
| `Utilities/rnir.py:55-58`; `rnpkg.py:54-58` | both `--config`, `-v/--verbose`, `-q/--quiet`, `--version`; rnpkg also `--exampleconfig` | Functionality **upstream placeholder**; .NET shell **missing**. Compare initialization/exit and minimal example rather than invent resolution or package operations |
| `Utilities/rngit/server.py:139-285` | common `--config`, `--rnsconfig`, `-v/--verbose`, `-q/--quiet`, `--version`; default/node: `-p/--print-identity`, `-s/--service`, `-i/--interactive`; create: `-i/--identity`, repository; release: identity, `-s/--signer`, `-n/--name`, `-L/--local`, `-o/--offline`, repository/operation/target; perms: identity, remote; work: identity, `--scope`, `-t/--title`, `-d/--id`, repository/operation; fork: identity, source/target; sync: identity, repository; mirror: identity, `--scope`, source/target | **Missing** upstream node/admin CLI. Direct .NET clone/fetch commands are additions, not substitutes. Release operations list/view/fetch/verify/create/latest/delete; work help lists list/view/create/propose/edit/delete/update/complete/activate/perms, while handlers also expose comments. Execute dispatch, not help text alone |
| `Utilities/rngit/client.py:55-76,335-447,545-676`; `commitsigs.py:301-307` | helper invocation `name URL`; capability/list/list-for-push/option/fetch batch/push batch/blank/EOF; rngcs `-Y sign/find-principals/check-novalidate/verify`, `-n`, `-f`, `-I`, `-s`, optional file, repeated `-O` | Helper read-only subset **implemented**, full helper **partial**; rngcs **missing**. Exact stdout protocol, stderr-only diagnostics, partial per-ref status, option arity/value/EOF and offline negotiation. Signature checks distinguish structure, principal/author identity and actual verification |

Existing process evidence is specifically `tests/Pester/Cli.Tests.ps1` identity,
invalid rngit and authenticated rncp; `GitRemote.Tests.ps1` local-reference
ordinary Git clone/fetch/errors; `Coverage.Tests.ps1` tooling. It is not coverage
for the unimplemented installed commands. CLI completion requires independent
processes, stdout/stderr bytes, exact exit statuses and persisted effects;
Pester must not substitute calls to internal C# handlers.

#### Housekeeping, preprocessing and persistent-state branch matrix

These branches were the explicit boundary of the earlier inventory. They are
now classified, not claimed implemented.

| Observable branch / source | Current status and audited .NET evidence | Acceptance obligation |
| --- | --- | --- |
| Startup readiness/IFAC/HW-MTU/filter-before-queue (`Transport.py:1753-1813`; IFAC `1689-1750`) | **Partial** Core manager parses at 500 and drops malformed frames; no IFAC or traffic-class preprocessing; source counter/diagnostic equivalence **missing** | Before-ready bounded wait/drop; <=2-byte frames; missing/unexpected IFAC flag, wrong/short MAC, post-auth frame too large, parse/filter rejection. Preserve raw-codec reserved-bit roundtrip while enforcing IFAC only at bearer boundary |
| Announce prevalidation and ingress hold; tag parsing/dedup/inflight batching (`Transport.py:1815-1896`) | **Partial** AnnounceEngine validates and router parses/truncates tags; class queues, source counters/holds/inflight semantics **missing** | Known/unknown/explicitly requested/blackholed announces; signature/MTU reject; request payload below/at/above 16/32 bytes, tagless/oversized/duplicate tags, simultaneous requesting interfaces, limited traffic versus ordinary batching; queues full must produce deliberate bounded behavior |
| Closed pending/active links and failure-triggered rediscovery (`Transport.py:695-737,875-964,1242-1264`) | **Partial** LinkManager ticks/removes closed links; router culls link entries, but automated path invalidation/rediscovery/blocked-ingress state machine **missing** | Pending endpoint failure with transport on/off, shared client versus standalone; invalidated/local-client/one-hop-destination/one-hop-initiator branches; boundary mode exception; 20 s minimum rediscovery interval, queue max 32 and 0.5 s dispatch throttle; fail then recover route without restart |
| Receipt cull/timeout/collection (`Transport.py:739-762`), retransmission limits/held announce restoration (`764-837`) | **Partial** receipt expiry, rebroadcast timers and neighbor cancellation tested; 1024 cull and held restoration incomplete | 1025+ outstanding receipts, oldest CULLED versus timeout, one terminal callback; local rebroadcast/retry limits, elapsed retransmission, lost identity/path during wait, restore held original after targeted path response |
| Hash/tag generations; detached pending local requests and path states (`Transport.py:840-874,1150-1158`) | **Partial** router two-generation bounded hash/tag sets; full stale local/shared request and health-state cleanup **missing/unverified** | At/exceed half hash capacity and tag capacity, duplicate survives one generation then expires; detach before response, no orphan states and bounded growth across repeated attach/remove |
| Reverse/link/path/request/discovery cleanup (`Transport.py:875-1022,1077-1148`) | **Partial** `TransportRouter.Cull:743-786` removes ordinary reverse/discovery/path/unproved entries; validated-link missing-interface handling and rediscovery coupling **partial** | Timeout equality/just-after, every absent ingress/egress, validated versus pending links; AP/roaming/full path lifetimes; pending/inflight 45 s gates, discovery timeout; failed round retries later and unrelated tables remain usable |
| Tunnel cleanup/replacement (`Transport.py:1024-1075,2764-2874`) | **Missing** | Excessively future expiry (>2x timeout), expired tunnel, vanished interface, stale tunnel path and newer active timebase. Reappearance restores only eligible paths; corrupted persisted tunnel cannot poison others |
| Interface order/ingress/PR/held-announces/PHY keepalive jobs (`Transport.py:1160-1170`; `Interfaces/Interface.py:184-430`) | **Missing** complete control plane; limited announce queue exists | Five-second controlled-clock rounds; gravity priority, held capacity/release cadence, latest queue replacement and hop-prioritized bandwidth cap; one interface failure logged and later round/other traffic continues |
| Packet/announce/resource caches, known destinations, management announces, blackhole expiry (`Transport.py:1172-1238,2995-3123`) | **Missing** disk cache/proof recovery/management/blackhole; known destination bounded in-memory expiry **partial** | Cache cleanup every 5 min; forced proof cache retained/read/replayed after proof loss; valid/corrupt/truncated cache, retention and clock skew; two-hour management announces (initial scheduling at start), one-minute blackhole expiry; slow/faulting jobs owned/cancelled, not untracked background work |
| Runtime jobs/persist/resource-cache cleanup and signals (`Reticulum.py:157-206,351-397,1316-1357`); Identity ratchet/known stores (`Identity.py:177-349,395-479`) | **Partial** stable transport/utility identities and graceful app cancellation exist; other stores/job policies **missing** | Reference intervals (5 min jobs, 15 min clean, 12 h persist, 5 min gracious persist, 24 h resource cache) under controlled clocks; atomic replace, failed/corrupt/readonly stores, process restart; concurrent persist and shutdown cannot truncate keys or deadlock |
| Path/hash/tunnel/blackhole save/reload and stable interface lookup (`Transport.py:397-509,2983-2992,3745-3968,4030-4116`) | **Missing**, separate from identity persistence | Clean and abrupt restart, wrong/absent interface identity, stale/corrupt entry, cached original announce unavailable, bad signed blackhole input and ACL; recover valid independent entries with diagnostics, never silently trust arbitrary serialized objects |
| Per-bearer transmit accounting, partial writes and egress gating (`Interfaces/util/TransmitBuffer.py:35-173`; `BackboneInterface.py:55-69`; `Interface.py:301-383`) | **Partial** basic async writes; coalescing/accounting/Backbone egress **missing** | Frame order/whole-frame counts under partial/zero/interrupted/would-block writes; byte/frame high-low watermarks, stall/dead-time/hysteresis, bounded backlog, cancellation while blocked. Python zero-copy deque layout itself is **not applicable** |

#### Malformed-input and failure-path acceptance ledger

This ledger is exhaustive by **external ingress/schema/state-machine family**,
not an assertion that every cross-product of malformed fields was executed.
For each family, the differential suite must record reference return/exception,
network reply/drop, state mutation, diagnostic and continued service on the
next valid input. A reference exception is evidence, not a reason to terminate
the .NET daemon or copy an unbounded allocation. Any deliberate hardening
divergence must be explicit.

| Boundary and source anchors | Classification / existing test boundary | Required corpus and recovery evidence |
| --- | --- | --- |
| Crypto/key/token (`Cryptography/{Token,HKDF,PKCS7,X25519,Ed25519}.py`; `Identity.py:738-944`) | Valid RFC/reference and tamper/short/low-order slices **implemented**; provider/padding/malformed-key equivalence **partial/unverified** | Key lengths, low-order/noncanonical/invalid Ed25519, no private key, empty/oversized HKDF, token length/alignment, valid-HMAC invalid-padding and bad-HMAC no-decrypt; no secret log; disposed/zeroed key use and concurrent ratchet rotation |
| Packet/frame/IFAC (`Packet.py:180-283`; `Transport.py:1624-1680,1689-1896`; bearer framing) | `PacketTests` all 128 flags/every truncation/hop/MTU; HDLC fuzz/recovery tests **implemented bounded slices**; IFAC/bearer MTU controls **missing** | Both headers and each context/type, unknown/duplicate/hop filter, wrong transport ID, malformed escape/truncated frame, IFAC above; valid successor on same peer. Explicitly retain/document stricter malformed HDLC rejection |
| Announces/discovery/blackhole (`Identity.py:511-612`; `Discovery.py:290-469,525-650,905-967`) | `AnnounceTests` tamper/truncation/wrong signed destination and `AnnounceEngineTests` key conflicts **implemented**; discovery/blackhole **missing** | Ratchet flag/key/name/random/signature/hash/app-data permutations, older/replayed announce, disallowed source/stamp/encryption/version/schema, invalid IP/name/port/location, stale/unknown discovery states, failed/partial blackhole update, bad expiry/reason; no poisoning remembered identity/path |
| Link control/proofs/requests (`Link.py:148-227,391-451,804-918,929-1187`; `Packet.py:449-560`; `Transport.py:2610-2718`) | Valid vectors/basic wrong-proof timeout **implemented**; all mode/hop/context/state/schema/failure combinations **unverified/partial** | Truncated/extra signalling/proofs, disabled mode, bad keys/signature/terminus/interface; repeated/wrong-side identify, keepalive/RTT/close payload/state; malformed request timestamp/path/data/response IDs, unknown/duplicate/late response, invalid policy/oversized small or resource body; only authenticated observations rebalance paths |
| Resource advertisement (`Resource.py:156-244,1289-1376`) | Valid advertisement vectors **implemented**; hostile schema/limit policy **partial/unverified** | Missing/extra/wrong-typed t/d/n/h/r/o/i/l/q/f/m, negative/zero/overflow/inconsistent sizes and parts, flags request+response/metadata/split/encryption, hash widths, segment bounds/order and duplicate accepted advertisement. Test `t` above `MAX_EFFICIENT_SIZE*3` and policies below/at/above allowed allocations before allocation |
| Resource parts/request/HMU/proof/assembly (`Resource.py:488-515,576-676,685-839,842-1123`) | Valid vectors and send/reject/cancel/segmentation **implemented bounded slices**; malformed/terminal cleanup **partial** | HMU truncated hash/invalid MessagePack/negative or excessive segment, empty/non-divisible/oversized map, unmatched part hashes/duplicate/out-of-window requests, bad proof/hash, decompression corruption/expansion, metadata length/schema with otherwise valid resource hash, partial multipart file and I/O failure. Reject/cancel releases next queued resource; exactly one terminal outcome and no accumulator/task/file leak |
| Channel envelope/messages/Buffer (`Channel.py:113-148,253-273,303-397,399-545`; `Buffer.py:64-98,151-191,232-280`) | Vector/order/full-window/reserved/large-message/retry-exhaustion recovery slices **implemented**; malicious wrap/concurrency/slow reader **unverified** | Envelope type/sequence/declared length versus bytes, unknown/reserved/unregistered message/factory failure, duplicate/reorder/window at sequence wrap after >65536 sends, RTT thresholds; stream header/id/EOF/compression expansion <=16 KiB, partial reads/zero-length writes, blocked read then dispose/close, callback throw and concurrent EOF/write |
| Configuration/local management (`Reticulum.py:460-730,843-1207,1369-1435`; vendor config syntax) | Existing `RnsdTests` subset **implemented**; full parser/auth RPC **missing** | All schema ledger cases plus authenticated connection challenge/framing, invalid key, short/unknown get/manage/drop/identity/destination operations, arbitrary payload/depth limits, stalled local peer and detach/reload race; isolated peer failure and deterministic authorization errors |
| Git remote/admin/pages/media/signatures (`Utilities/rngit/client.py:335-676`; `server.py:2869-4642`; `pages.py:250-312,366-3007`; `media.py:57-215`; `commitsigs.py:48-331`) | Read-only helper valid/error/process subset **implemented**; remaining families **missing** | Invalid URL/alias/ref/object-format/bundle/status/metadata and partial EOF; path/ref traversal, denied/hidden/blocked/null identities, corrupt .allowed/release/work files, request field/type/size/ID/scope, concurrent ref/doc/release updates; escaped/paginated/binary pages and download paths; absent/crashed/timed-out media/template/Git/editor process; wrong principal/namespace/author/tagger, truncated/legacy/tampered RSG/SSHSIG/RSM and artifact mismatch |
| Utility/file/process/device boundaries (all CLI parser anchors above; `rncp.py:254-324`; `rnx.py:155-251`; `rnsh/protocol.py:46-148`) | Basic rncp/rnx/identity errors **partial**; remaining utilities/device behavior **missing/unverified** | Wrong argument arity/type/combination, malformed hash, unauthenticated peer, missing/readonly/jail escapes, binary output, failed spawn/timeout/EOF/signal, protocol-version/PTY restore; device unexpected KISS/firmware/EEPROM reply, invalid image/signature/flash backend. Simulated negative cases precede authorized real hardware |

#### Shipped examples as observable acceptance scenarios

All fourteen files in `Examples/` were inventoried, including the ones not
listed in `docs/source/examples.rst`. Examples require equivalent scenarios,
not copied Python executables, terminal menus or module layout.

| Example and source anchors | Status / .NET owner and evidence | Scenario required for acceptance |
| --- | --- | --- |
| `Minimal.py:18-67` | **Partial**, Applications node/Transport announce | Fresh config/identity, initialize/start, announce and orderly cancellation; verify discovery from a Python peer |
| `Announce.py:23-138` | **Implemented bounded announce/handler slice**, Core/Transport `AnnounceTests`, `LiveAnnounceTests` | Preserve app-data/aspect-filter/ratchet/path-response and callback-fault regressions; dynamic default app data remains **partial** |
| `Broadcast.py:17-72` | **Partial**, Core plain codec and router test, not full plain endpoint API | Two real mixed peers exchange plain broadcast, no identity encryption, hop limits and observer-independent receive |
| `Echo.py:25-109,118-260` | **Partial**, Transport local DATA/receipts | Both-direction encrypted single datagram, APP/ALL/NONE proof policies, RTT and timeout, no subscriber and tampered input |
| `Ratchets.py:24-117,126-269` | **Partial**, Core ratchets + Transport recall; live ratchet exchange exists | Repeat Echo with enforced ratchet, rotation/restart/signed persistence/old key expiry and rejected identity fallback |
| `Link.py:27-97,109-234` | **Implemented bounded healthy link slice**, Transport `LinkTests`/`LiveLinkTests` | Both initiators, encrypted data/RTT/keepalive/each-side close; impaired/lost establishment and no-observer flow remain **unverified/partial** |
| `Identify.py:27-107,122-257` | **Implemented bounded identification slice**, Transport Link/live tests | Correct identity callback and signed link binding both ways; repeated/wrong/malformed identify and callback failure acceptance remains required |
| `Request.py:26-97,109-230` | **Partial**, Transport `Link.Requests` tests | Registered path and random response, received/progress/fail callbacks; denied/absent handler, timeout, cancel, size/resource policies and live both directions |
| `Resource.py:28-100,117-238` | **Partial**, Transport resources; normal segmented/metadata/live transfer exists | Sending and receiving callbacks, compression/random bytes, reject/cancel; queue advancement, corrupt metadata, bounded assembly and cleanup |
| `Filetransfer.py:44-188,214-521` | **Partial**, Applications rncp and Transport file responses | Listing then chosen streamed file, both directions, missing/changed/unreadable file, path containment and final checksum; rncp roundtrip alone is not this example's file-server contract |
| `Channel.py:60-86,98-190,202-333` | **Implemented bounded message/order/retry slice**, Transport Channel/vector/resilience/live tests | Custom AOT-safe message factories, full window/retries, both directions; unknown type, wrap and slow/malicious peer remain **unverified** |
| `Buffer.py:35-128,147-266` | **Partial**, Transport streams/live tests | Stream callbacks, interactive bidirectional EOF; cancellation/disposal/slow-reader bounds and lost proof recovery without callback/task leaks |
| `Speedtest.py:36-151,164-282` | **Unverified** scenario, Transport metrics + Applications harness | Sustained both-direction transfer with independent byte/rate/loss observations, negotiated MTU, proof/timeout and clean close; no invented throughput threshold or Python performance identity requirement |
| `ExampleInterface.py:62-211,218-293` | **Missing** equivalent configured extension, Interfaces/Core/Applications | Static native factory or owned process adapter exercising lifecycle, framing/config/IFAC/modes/reconnect/error and reload; Python dynamic import/exec is intentionally not reproduced |

#### Ownership and architecture decision matrix

These boundaries are requirements for subsequent implementation, not a claim
that existing classes already satisfy them. Preserve the existing projects;
no speculative singleton/module/threading translation or whole-stack rewrite
is implied.

| Owner / existing implementation | Async lifecycle, cancellation and bounds | Persistence and errors |
| --- | --- | --- |
| Crypto (`Ed25519`, `X25519`, `KeyDerivation`, `Token`) | Own secret buffers and zeroing, synchronous bounded primitive operations; disposable identities use safe key snapshots for concurrent ratchet/decrypt work; no background crypto worker per packet | No persistence/logging of secrets; caller-owned files. Stable failure verdict/exception at primitive boundary, authenticate before decrypt; malformed key/padding hardening explicit |
| Core (`Packet`, `Destination`, `Announce`, `IdentityFile`, `MsgPack`, `PacketInterfaceManager`) | Codec has no routing/encryption side effects; caller-owned input lifetime explicit. Manager owns top-level and spawned bearer start/read/send/dispose, bounded 256 wait-on-full ingress, cancellable enumeration; child fault isolated, top-level fault reported | Raw identity file contract and owner-only/nonoverwrite policy; explicit MessagePack limits. Aggregate disposal failures without abandoning siblings; callback-independent processing. New prioritized queue policy must remain bounded and preserve source/traffic class |
| Interfaces (UDP/TCP/serial/HDLC; future bearers) | Own sockets/serial/child process/peer, decoder and send/reconnect tasks; cancellation interrupts DNS/connect/read/backpressure/retry. Per-peer/aggregate frame+byte limits and actual negotiated MTU, no child failure kills parent listener | Stable bearer identity needed for restored paths/tunnels. Stats/errors include peer/context, distinguish optional unavailable device from fatal owned interface; no silent unsupported settings or leaked processes/reconnect timers |
| Transport (`TransportRouter`, AnnounceEngine, KnownDestinations, LinkManager/Link, Resource, Channel/Buffer) | Own protocol registries, timers, requests, callbacks and queues; a single cancellation tree settles pending operations. Existing LinkManager outbound/callback channels are **unbounded**, RawChannelReader memory grows, callbacks can stall, preparation/assembly Task.Run work is not fully owned: these are **partial**, not accepted architecture | Serialize versioned protocol state via Applications storage boundary; preserve original announce wire and stable interface identity. Isolate/report optional observers, exactly-once outcomes; timeouts/cancellation versus protocol rejection distinct; shutdown joins workers and clears secrets/accumulators/receipts |
| Applications (`ReticulumNode`, RnsdHost/config, ClientLinks, utilities, rngit helper/session) | Own configuration, policies, shared-instance election/RPC, OS signals and host lifetime. Scope Git/editor/template/media/remote-command processes with explicit authorization, bounded stdout/stderr/time and owned cleanup. Read-only RngitSession must not own Git refs; host services need per-repository mutation serialization | Own atomic/versioned stores, permissions/reload, private identity ACLs, caches and stats restart. Errors map to CLI stdout/stderr/exact exits or authenticated RPC status; failure must not expose hidden repositories. `RnsdHost.DisposeAsync` currently disposes manager only and ReticulumNode can miss host disposal on unexpected run failure: terminal/restartable ownership remains required work |
| LXMF and telemetry (existing supported extensions) | Preserve current cancellable native delivery/telemetry boundaries and existing regression suites; do not fold missing upstream management into telemetry or promise LXMF propagation parity | Existing stores/policy limits and interop disclosures retained; no extension expansion needed to close RNS parity inventory |

Intentional .NET differences: explicit start/dispose/cancellation rather than a
process singleton; Core packet codec versus upstream packet orchestration;
static AOT-safe handlers/factories/serialization instead of reflection/dynamic
imports; .NET `Stream.ReadAsync` waiting rather than Python RawIOBase returning
None; configurable bounded backpressure and 64 TCP-client default cap; strict
key/padding/HDLC/identity-file validation and explicit secret zeroing.
These architectural choices are acceptable only with documented observable
effects and recovery tests. `.dotreticulum`, rnid `--show`, direct rngit
clone/fetch and custom telemetry are native additions/differences, not proof of
the upstream CLI/configuration surface.

Upstream quirks need explicit decisions, not accidental translations:
`rngit/client.py:173,479-481` permits batch zero but does not advance a nonempty
fetch queue; `server.py:277-280` routes mirror CLI tasks through fork;
`server.py:4912-4970` comments place mirror_interval near repositories although
the reader is `[rngit]`; `pages.py:269-312` permits executable `.mu` templates;
`media.py:53,118-180` selects backends using RNGIT_MEDIA_BACKEND and controls
child processes. Bound/no-progress errors and explicitly authorized template/
editor execution should be intentional safety differences, recorded and tested
instead of copying hangs, arbitrary Python execution or unbounded output.
The coordinator additionally confirmed the existing-object push branch
(`server.py:3078-3106`) requires force for **any changed SHA**, including a
fast-forward, unlike ordinary bundle-push ancestry handling. Per-ref operations
are sequential, not atomic/CAS/lease transactions; deletion preserves the
reference's HEAD behavior. Differential cases must distinguish these branches
rather than asserting normal Git fast-forward semantics for all requests.
Any helper SHA-1-only decision must explicitly reject local/remote SHA-256
without narrowing the existing direct rngit client's separate behavior.

#### Differential/live/Pester/NativeAOT acceptance routing

The earlier feature-table criteria and the schema/failure/example rows above
are cumulative. Each row below assigns all four verification modes; N/A means
that particular layer has no CLI/network boundary, not that feature verification
can be skipped. New fixtures must execute the pinned reference, record actual
imports/SHA and source hashes, and preserve old `e40191b3...` vector provenance.
“Native” always means executing the relevant published feature, not only
successful publish or `--help`.

| Feature families / owner | Differential xUnit (D) | Actual reference/live (L) | Separate-process Pester (P) | Published native feature (N) |
| --- | --- | --- | --- | --- |
| Crypto/identity/ratchets/announce/codec/serialization; Core/Crypto | Independent provider/key/token/announce/packet/MessagePack corpus including malformed cases above; byte/verdict/state assertions | Mixed peers accept .NET bytes and .NET accepts Python bytes, both ratchet/plain identity directions | rnid complete CLI, encoding/file/stdin/no-overwrite/ACL/reload plus failure exits; codec-only internals have no separate CLI | Generate/load/export/sign/verify/encrypt/decrypt; announce and exchange from published utility with independently checked result |
| Config/logging/shared management/stores; Applications/Core/Interfaces | Every schema key/default/alias/invalid case; clocked cleanup and authentication-framing vectors | Real Python shared server/.NET client and reverse where applicable; RPC auth/ACL/manage/retention, discovered/blackhole peer updates | Clean home/start/election, required-shared fallback, attach/reload/detach, signal/restart/corrupt/readonly stores, version/example/monitor outputs | Repeat management/persistence/configured daemon feature and shutdown after live traffic, no leaked listener or child |
| Transport/routing/control/tunnels/cache; Transport/Interfaces | Mode/gravity/filter/IFAC/PR/receipts/cache/tunnel and every housekeeping branch above with controlled clocks | Python leaf AND transport peers, direct/multi-hop, both initiators, simultaneous requests, loss/latency/proof-loss/reconnect/route change; cached proof avoids full transfer | rnpath/rnstatus/rnprobe with daemon processes, authorized/denied/unknown and persisted state | Native router and management utilities perform representative impaired mixed-mesh flow with asserted paths/proofs/cleanup |
| Links/requests/resources; Transport | Wire/proof/metadata/limits/compression/segment/state corpus, no-subscriber/faulting observer, bounded queued cancellation and exact terminal outcome | Both link initiators; random/compressible multi-megabyte files each direction direct and Python-transport mixed multi-hop; file larger than memory budget; lost proof/request/close/recovery | rncp/rnx real client/listener/allow/jail/timeout/exit/output matrix; test process resource consumption, not only handlers | Encrypted/compressed multi-megabyte file and request response both directions, cancellation/rejection queue advancement and settled shutdown |
| Channel/Buffer; Transport | Full-window/retry and >65536-message wrap/RTT/malformed envelope/stream/slow-reader bounds | Both-direction Python Channel/Buffer over impaired direct and mixed transport path, exhaust then recover a new link | rnsh protocol/PTY/pipe/stdin/output/resize/EOF/signal and terminal restoration; internal Channel lacks standalone CLI until used by that feature | Native rnsh/stream flow and retry/close recovery; verify pending readers/writers settle within configured deadline |
| Existing UDP/TCP/serial + missing Auto/Backbone/Local/Pipe/KISS/AX25/RNode/Multi/I2P/Weave/Android; Interfaces | Every config family, framing/device response, partial write/queue/accounting, retry/disposal/fault isolation; owned simulators where physical device absent | Each implemented bearer with real Python counterpart; actual serial/radio/Weave hardware, I2P SAM and actual mobile permissions separately identified | Config/start/optional error/reload/reconnect/child exit/respawn with owned processes/devices and output/exit evidence | Execute actual bearer on each claimed OS/RID; desktop simulation cannot close Android/iOS/radio claims |
| Git helper/config/push/hosting/permissions/create/fork/mirror/sync; Applications | Reference-generated request/ref/status/permissions/default/alias/object-format and malformed corpus, concurrent mutation/stores | Actual ordinary Git clone/fetch/push/delete/force and reference node/admin operations both client/server directions; hidden/denied/partial/failing/restarted peers | Git-discoverable installed executable, exact protocol stdout, real repositories/refs/fsck, command/version/config/allow/restart/timeout/failure exits | Native Git operations and hosted-node flow, nonzero missing-object bundle evidence, local ref preservation, SHA-1/SHA-256 advertised support tested per platform |
| Git releases/work/pages/media/templates/signatures; Applications | Every operation/scope/permission, RSG/RSM/SSHSIG corpus, escaping/routes/pagination/binary/artifact checksums and backend failure cases | Bidirectional authorized reference exchanges, missing/tampered artifacts, current/legacy signatures, principal mismatch and actual NomadNet-style request routes | Real CLI operation output/exit and disk changes/restart/concurrency; owned executable template/media/editor failure/timeout/no-leak | Native create/fetch/verify release + work/page/download/signature operations, not merely fetching unrelated Git objects |
| rnid/rnsd/rnstatus/rnpath/rnprobe/rncp/rnx/rnsh/rnodeconf; Applications | Parser/domain/protocol fixtures for every CLI ledger row and conflict branch | Corresponding real reference daemon/client/device boundary; explicit authorization for remote exec and device writes | Every option's success/failure/default effect, stdin/EOF/signal/exit/output/persisted results; mock device vs real hardware distinguished | Executable discovery and representative substantive success/failure for each utility on supported platforms, including binary process I/O and OS terminal behavior |
| Resolver/rnir/rnpkg/disabled crypto/autoconnect placeholders | Assert source-backed no-function/disabled behavior, not invented implementations | No live capability claim for absent functionality; placeholder startup may initialize RNS | Help/version/init/example and failure shell behavior where installed | Shell/version/init only, explicitly labeled; not “native resolver/package manager support” |
| RNS formatting/profiling/platform and retained LXMF/telemetry regressions | Format/base256 boundaries, statistics optionality and AOT-safe profiling snapshots; existing extension tests | Match actual management observations; existing LXMF/lxmd evidence remains separately scoped | Exact utility text/JSON/monitor output and native extension regressions where promised | OS-specific formatting/device metrics/feature exercised; no Python module/decorator/CRNS loader reproduction required |

Platform evidence remains **unverified** beyond recorded Windows slices:
Linux/macOS managed/native, ARM/RISC-V, FreeBSD, Android/iOS permissions,
UNIX sockets/PTYs, serial/radio hardware and additional network backends must
each supply actual feature evidence before a support claim. Absence of such
resources does not prevent parser/simulator/Windows implementation. Coverage
collection must use a fresh directory, serialize instrumentation against other
builds, merge six production modules without double counting, and report
passed/skipped plus live enablement; coverage percentage is not an acceptance
criterion by itself.

**Closure interpretation.** The earlier “unreviewed jobs/preprocessing,
CLI/config/manual options and examples” inventory limitation is resolved at
the public-family/reader/branch level by this supplement. Exhaustive public
classification does **not** mean exhaustive private helper review, every
malformed combination tested, Python API identity, all manual prose ported,
or any newly implemented parity feature. Unexecuted differential verdicts,
platforms, hardware and future features are explicitly classified above and
tracked by the corresponding unchecked Parity 2-8 work in `plan.md`. No
unclassified external family is intentionally deferred to implementation;
future source discoveries or an explicit pin update must extend that tracker.
The additional concrete gaps (MessagePack application values, metrics/
formatting/profiling, failed-link rediscovery, transmit accounting and Git
templates) are now explicit required work, not implicit completions.

#### Coordinator execution evidence for the inventory baseline

The parent session reported the current-main checks below while this audit
was being completed. The auditor inspected the recorded test/coverage and
Pester log output, but did not rerun the commands. The reproducible invocation
sequence uses the supplied reference and output locations:

```powershell
$referenceFiles = 'C:\Users\dennis\.copilot\session-state\c075724f-f262-4902-9809-a6358920915f\files'
$sessionFiles = 'C:\Users\dennis\.copilot\session-state\f94e85c1-752c-4ad1-84c9-9df20727787d\files'
$python = Join-Path $referenceFiles 'refvenv\Scripts\python.exe'
$reference = Join-Path $referenceFiles 'upstream-reticulum-rns'
$nativeDirectory = Join-Path $sessionFiles 'native-parity1'
$nativeExe = Join-Path $nativeDirectory 'DotReticulum.Applications.exe'
dotnet build DotReticulum.slnx -c Release --nologo -v quiet
dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o $nativeDirectory --nologo -v minimal
& $nativeExe --help
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe $nativeExe -Coverage -ResultsDirectory (Join-Path $sessionFiles 'coverage-parity1')
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference -Application $nativeExe
```

Coordinator results: build **0 warnings / 0 errors**; default suite **360 passed,
40 opt-in skipped, 0 failed**; win-x64 NativeAOT publish with both warning-as-error
flags and native `--help` **passed**. Full live/native coverage **402 passed,
0 skipped / 0 failed**, including actual native multi-megabyte encrypted/
compressed resource paths. Native Pester against the actual pinned Python
server **26 passed, 0 skipped / 0 failed**; the same managed Pester suite also
**26 passed, 0 skipped / 0 failed** (coordinator-reported separately). Logs retained by the coordinator:
`files\parity1-validation.log` and `files\parity1-pester.log` under the current
session directory above. Publishing alone is not the feature evidence.

The full live run merged six managed production reports, counting shared
sources once. Native/Pester child processes are not additional Coverlet hits:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 913/1395 (65.45%) | 723/1579 (45.79%) |
| Core | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2430/2800 (86.79%) | 1308/1701 (76.90%) |
| **Overall** | **4899/5874 (83.40%)** | **2768/4147 (66.75%)** |

This execution preserves the tested bounded slices while establishing a
measured baseline for subsequent phases; it does not change the missing/
partial/unverified classifications or certify all inventoried failure paths.
Parity 1 is complete as an inventory/architecture/acceptance baseline.
Implementation begins with Parity 2 only in the integrating session's scope.

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
