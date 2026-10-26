# DotReticulum development history

Historical reference only: this file records milestone outcomes and verification evidence; active work is tracked in [TODO.md](../TODO.md), the original roadmap snapshot is [docs/archive/plan.md](../docs/archive/plan.md), and current-state documentation lives in `README.md` and `docs/`. Historical completion does not imply full Reticulum parity.

## Reference pins and provenance

The authoritative upstream is the Reticulum-hosted repository at `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`; GitHub `markqvist/Reticulum` is a comparison mirror. New live tests and later reference executions use the verified, clean hosted checkout at **`a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7)**, tree **`24d4c393de9fd57b933836cea68891fd953ab060`**. The first upstream fetch was incremental: local history was seeded through parent `7f2b3b9b524c9386316379af1313b43a5e4f7a5d`; .NET `rngit` then transferred 11,502 bundle bytes, added 92 reachable objects, and passed strict Git fsck. This proves object consistency/reachability, not publisher authentication.

| Reference revision / artifact | Provenance and evidence |
| --- | --- |
| `a29bfb51c468945c96a1ee22cae0114e993e2391` | Current live RNS 1.5.7 pin; fetched from the Reticulum host, independently checked against the GitHub mirror and used for post-pin live interop, source-executed vectors and source audits. Tree `24d4c393de9fd57b933836cea68891fd953ab060`. |
| `e40191b3d193b46b7f2d8a44424a594cd758839b` | Original stored vectors and generators (GitHub mirror, RNS 1.5.5; tree `ed83d80cd2ced7b41528f8e3f873b3bb54b62ad9`). Vectors were executed from upstream Python; they were not relabeled or regenerated when the live pin moved. |
| LXMF `1.2.1` | Isolated Python distribution used where LXMF behavior is exercised; actual import path and version checked. LXMF delivery, `lxmd` interaction and propagation sync remain distinct claims. |
| Published maintainer release identity `bc7291552be7a58f361522990465165c` | Identity relevant to future release-signature authentication; object integrity and this identifier do not by themselves establish publisher authentication. |

The following SHA-256 values fingerprint pinned RNS 1.5.7 source files used in later vector generation, differential checks or contract review. Repeated use of a file has one entry. Windows raw and canonical-LF fingerprints can differ for identical pinned source; both Backbone values are retained.

| Pinned source path | SHA-256 |
| --- | --- |
| `RNS/Identity.py` | `ac336bb2b7217278d3a962b6945212313fed0ee9cc10d666e8ac17faca17ea2f` |
| `RNS/Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |
| `RNS/Packet.py` | `f1b12a7068c6036e20439f87f8bfb6ad2dd92689c03be387e2bb697d46bdb075` |
| `RNS/Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `RNS/Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `RNS/Link.py` | `4267ab1c5b11d9c415693298376f2a984aa9bca13a6245e9bc3dce3236365a7c` |
| `RNS/Resource.py` | `9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699` |
| `RNS/Channel.py` | `5ab5cf0bfd2c0fd3574d53b405f48e08756c2105bba72d9bc530b652f985bdd7` |
| `RNS/Buffer.py` | `7f2269b25ada8e81b20f01692517e790bd3264dfdb461295570bcfd7b39b8db6` |
| `RNS/Discovery.py` | `ae26445adf6325ed3e47f2b7c4b412060994d75ae583a58162849507ed7a9950` |
| `RNS/Cryptography/HKDF.py` | `543479e703b6946876a593f1c7179de49fedd689597766cee4a4aa385ace25fa` |
| `RNS/vendor/umsgpack.py` | `0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50` |
| `RNS/Interfaces/Interface.py` | `70bf362bc54872e24c124e503d8c52c487886aa141f62faed739e46c396c689c` |
| `RNS/Interfaces/TCPInterface.py` | `d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d` |
| `RNS/Interfaces/UDPInterface.py` | `575d1137716139b238649ae5b35a0f66a6a25a8661bc919f5226942df79f4fa5` |
| `RNS/Interfaces/SerialInterface.py` | `676f7b038c2950b92ac71f29653cbbe35ba6513a53ca41742d43e19966e1ef1f` |
| `RNS/Interfaces/LocalInterface.py` | `b241095c0a528f0c9a88817429a9ce54c7adb5aa850ae08e29c9b84dd8fa4371` |
| `RNS/Interfaces/BackboneInterface.py` (Windows raw bytes) | `c3445132d89af04998c35b12e5101c8975d73599f62fe71b501b3d734c150438` |
| `RNS/Interfaces/BackboneInterface.py` (canonical LF / Git blob) | `b83f226ff847f140dabf782eac173177ba74c6a99bbc067c2a971fecdcf5e369` |
| `RNS/Utilities/rnsd.py` | `e8920d1acde17e09b0aad57651348e86aabc84fbb29a531c340b7365d220e748` |
| `RNS/Utilities/rngit/client.py` | `78358331ddac087860fb8a1f350632d3875917782dc000681d48597affd8936b` |
| `RNS/Utilities/rngit/server.py` | `6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693` |
| `RNS/Utilities/rngit/pages.py` | `d165d8ac8d3cfdd1b42929a2512e9d5d03a1cef726124deca5218d7522a89c04` |
| `RNS/Utilities/rngit/media.py` | `55fadeaee76d0a79c30c54d1d79122174cb6a956f2278013e97c4fd015fd8e9c` |
| `RNS/Utilities/rngit/commitsigs.py` | `f4a05c0db1099d60a032f57e40b457edfdd60e30248ae5c4c25f0542bbbaa0fc` |
| `RNS/Utilities/rnid.py` | `faee0d8acb4263f10812fa378085f2653b2453f584b4a33f2018c7797885f8c8` |

The original `e40191...` vectors retain their commit provenance; this history did not preserve a complete per-file SHA-256 ledger for that older source snapshot. Some fixture fingerprints are also preserved in their owning tests (for example retry-crypto fixture `59e2486a903a85a566dd539e522e4e547d53b44f3949a7fb06cb14edca46f9bd` and `rngit-push.json` fixture `941ae1dc34ea3425a428c03cb096738db84145c9999a2b5f1e7382697927752c`); these hash generated outputs, not upstream source files.

## Chronological milestones

| Date | Milestone | Outcome |
| --- | --- | --- |
| 2026-10-19 | P0 bootstrap and stack | Created .NET 10 solution/projects, governance and reference-vector workflow. Implemented crypto/identity/token, packet codec, bounded interfaces/HDLC, announces, routing, links/resources, LXMF/utilities/telemetry slices. Milestone evidence is historical and narrower than full parity. |
| 2026-10-19 | P0 closeout / hosted upstream | Native .NET `rngit` fetched missing objects from the Reticulum host through a six-hop gateway; exact commit/tree, 11,502 received bytes and strict fsck verified. Compared hosted RNS 1.5.7 to the 1.5.5 mirror pin; no publisher-signature claim. |
| 2026-10-19 | Tooling, reference and roadmap realignment | Moved coverage merging/orchestration to PowerShell/Pester, kept Python as thin upstream adapter, adopted hosted RNS pin, and replaced speculative audio/video roadmap with upstream feature parity. |
| 2026-10-19 | Read-only `git-remote-rns` gate | Implemented ordinary Git clone/fetch through the native helper, retaining Git ownership of refs and repository state. Hosted fetch proved missing commit/tree objects and nonzero bundle bytes; seed history disclosed; push/hosting/signing deferred. |
| 2026-10-19 | Parity 1 contract inventory | Audited public APIs, configuration, CLI, persistence, platform requirements and failure paths against the pinned source. Recorded implemented/partial/missing/unverified/not-applicable/upstream-placeholder status; inventory itself did not close implementation gaps. |
| 2026-10-19 | Parity 2 Git client and hosting | Added native read/write Git client, then repository hosting, collaboration, permissions, releases, work, pages/media and signing contracts with actual Python/native process checks. No public repository was mutated; publisher trust is separate from signature/object validity. |
| 2026-10-19–20 | UTF-8, branding and push-readiness | Closed Unicode helper decoding using explicit UTF-8 child environment, revised unofficial artwork and corrected concurrent identity publication/relay readiness races. No reference source or protocol changed. |
| 2026-10-21 | Parity 3 runtime/configuration/operations | Implemented bounded runtime ownership, configuration, persistence, shared-instance RPC, discovery/blackholes, telemetry/statistics and lifecycle handling. Full live/native and process tests passed; several platform and source-family gaps remained. |
| 2026-10-22 | Discovery lifecycle follow-up | Fixed attach/detach/reload advertisement registration and a receiver/disposal race; deterministic regressions verified replacement metadata, ports, intervals and continued service. |
| 2026-10-23 | Parity 4 transport/interface controls | Implemented routing/proofs/path controls, rate limiting, IFAC and bearer recovery, tunnel persistence, receipts and staged MTU behavior. Mixed Python/.NET impairments and native multi-hop/resource flows passed; operational offers intentionally stayed at MTU 500 / MDU 431 pending endpoint support. |
| 2026-10-24 | Parity 4 evidence clarification | Reaffirmed that larger signed MTU fields did not prove operational MTU negotiation; README/docs and roadmap wording were corrected without changing runtime behavior. |
| 2026-10-24 | Parity 5 endpoints/transfers | Implemented destination/request/ratchet/channel/resource behavior, bounded streaming/file-backed transfers and expanded operational MTU acceptance. Mixed-node loss/retry/recovery and multi-megabyte tests passed; limits and ownership differences documented. |
| 2026-10-24 | Parity 6 interfaces (partial) | Integrated Auto, Backbone/Pipe, UDP/TCP/serial, KISS/RNode/Multi, Weave and I2P software slices, source-generated vectors and runtime configuration. Some actual-router restart, Android, Darwin and physical-device acceptance remained unverified. |
| 2026-10-24 | Parity 6 recovery investigation | Fixed Auto/local lifecycle, DTR/RTS defaults, Android keepalive policy and I2P session persistence behavior. Actual I2P stream/restart recovery still failed; simulator/healthy SAM sessions were not accepted as proof. RAK COM8 checks were read-only; no firmware/EEPROM/RF mutation. |
| 2026-10-24 | Parity 7 utilities | Completed software acceptance for rnid, rncp/rnx, rnsh, rnstatus/rnsd and device safety paths using actual processes and Python peers. Software milestone evidence does not establish physical firmware/RF acceptance; upstream rnsh EOF behavior remained disclosed. |
| 2026-10-25 | Parity 8 Windows/Linux acceptance | Audited uncovered contracts; fixed cross-platform file publication, strict fsck, native process, discovery and PHY/statistics issues. Mixed impaired Python transport topology recovered after router restart; NativeAOT and resource flows ran on Windows/Linux. Physical/mobile, Darwin-specific and broader topology acceptance remained open. |
| 2026-10-26 | Hosted build/test CI | User-authorized narrow CI exception: GitHub Actions run **40330280525** passed managed coverage/build, NativeAOT, native help, encrypted compressed multi-MB resource flow and local Pester containers on Windows, Linux and macOS. Initial hosted failures (40324430597, 40326928959, 40328376116) drove test portability fixes and then version/git-height and other fixes through the successful run. CI does not establish live Python, mesh or complete macOS support. |

## Verification evidence summary

- **Most recent accepted complete local solution runs before hosted CI:** Windows x64 **1,847 passed / 87 skipped / 0 failed**; Ubuntu WSL Linux x64 **1,856 passed / 78 skipped / 0 failed**. A prior full Windows run at Parity 7 was 1,820/86. Hosted run 40330280525 passed all three OS jobs, but this history does not record exact per-run hosted test counts; do not infer them from the local counts.
- **Latest accepted merged production coverage (fresh Parity 8 Windows collection):** overall lines **22,452/27,064 (82.96%)**, branches **17,208/24,594 (69.97%)**. Per project: Applications 76.63% / 63.59%; Core 96.70% / 91.38%; Crypto 94.83% / 94.00%; Interfaces 85.58% / 78.21%; LXMF 90.23% / 70.96%; Transport 91.08% / 81.67% (line / branch). Six production reports were merged by source identity without double-counting. This is local Windows coverage; it is not an OS-averaged metric.
- **NativeAOT and Pester:** Windows and Linux publishes passed with linker/compiler warnings as errors, and native `--help` executed. The native application also ran encrypted/compressed multi-megabyte resources and terminal/session flows. Focused native Git-hosting/rngcs/git-remote Pester selections passed **82/0 skipped on each of Windows and Linux**. Earlier full Pester entry-point runs passed **73/2 skipped** on managed and native Windows; those counts are not the later focused selection. Hosted CI selected three local Pester containers; hosted per-container counts are not recorded here.
- **Live interoperability highlights:** P0 ran Python RNS interface/announce and bidirectional crypto cases; later direct/multi-hop links, resource transfer, `rncp`/`rnx`, LXMF/`lxmd`, shared runtime and routing tests exercised actual pinned Python nodes. Parity 4 mixed transport used 64 kbit/s and 20 ms one-way latency, selective loss, proof/reconnect and native daemon restart. Parity 5 exercised 1,024/16,384 MTUs, 40 ms bridge RTT, loss, multi-megabyte resources and fresh-link recovery. Parity 8 Python leaf → Python transport → impaired TCP → .NET transport → Python leaf transferred 2.2 MB incompressible payloads both ways, with 9/9 selected retransmissions across operation/restart; Windows capacity-one bearer observed 196–208 waits. Native resource flow transferred 2,200,017 bytes with byte equality/compression checks. These are selected topologies, not exhaustive bearer/loss/platform coverage.
- **Hosted CI run 40330280525:** Windows/Linux/macOS matrix green, run on commit `dbc91a7`; local per-platform counts above are not CI counts. Managed suite/coverage, NativeAOT warning-as-error publish, native help, encrypted compressed multi-MB resource flow and Pester containers ran. Run was build/test automation only: live Python opt-in disabled; no interop, mesh, CodeQL or stale workflow was authorized. Darwin rnsh PTY cases explicitly skip; pipe cases still execute. Workflow actions are SHA-pinned, read-only by default, filtered, and use cancel-in-progress.

## Reference-side shims and adapters

- Python `Generate*.py` programs execute pinned upstream methods; C#/PowerShell own test setup and assertions. Recording interfaces, frozen clocks/random values, synthetic interface/path tables, captured sends, initialized handlers and deterministic workers isolate a method/contract; they do not prove full node behavior or live interop.
- The discovery vector adapter selects stamps deterministically using actual LXMF workblocks/validation rather than upstream's CPU-count-sized multiprocessing scheduler. It proves stamp/payload acceptance, not scheduler equivalence.
- `rncp_win_shim.py` / the hosted Rngit callback adapters close a received file before upstream's original move/retention callback on Windows, avoiding WinError 32. Network, request, crypto and authorization handlers remain upstream; this does not prove unmodified Python Windows file retention.
- RNS import verification checks actual module paths, versions, pinned commit and clean tracked source. Windows Unicode subprocess cases use process-scoped `PYTHONUTF8=1` / `PYTHONIOENCODING=utf-8`; no reference source is patched. Backbone source comparison separately records raw Windows bytes and canonical LF/Git blob hashes.
- Git page comparison adapts captured local date fields to the executing host timezone while preserving UTC strings, precision and remaining bytes; captured local clock statistics remain. No vector was regenerated. These adaptations do not assert identical local-calendar output across timezones.

## Limitations at the last entry

See [TODO.md](../TODO.md) for active scope. At the 2026-10-26 history boundary the roadmap remained incomplete: unresolved actual I2P stream/restart recovery; missing desktop RNode BLE, Darwin rnsh terminal backend and Android host/providers; no physical serial/TNC/RNode/Multi/Weave/RF acceptance; and remaining Python-router/crash restart, alternate-route, prolonged Channel/resource soak and impaired published-native/per-bearer matrices. Broader OS/device support is not implied by managed builds or macOS CI. The hosted workflow does not test live Python interoperability or mixed mesh. No publisher/release-signature authentication is claimed; RNS object consistency is not publisher provenance. Full RNS parity remains incomplete.

## Lessons and decisions worth retaining

- **Create-only publication:** .NET `File.Move(overwrite:false)` can be check-then-rename on Unix. Publish a closed same-filesystem staging file with hard-link creation (`link`/`CreateHardLinkW`), then remove staging; never replace existing identity/configuration. Unsupported hard-link filesystems fail explicitly. This was verified on Windows NTFS and Linux ext4.
- **Git fsck path lengths:** strict verification's owned Git view must use a short path on Windows; nested `commit-graph-chain` paths can exceed limits despite `core.longpaths`. The verifier borrows the object store through alternates, checks all objects and does not touch caller refs.
- **Socket keepalive:** assert a nonzero enabled value, not integer `1`; Darwin reports valid flag value `8`.
- **Timezone-aware fixtures:** compare the captured local date fields in the execution timezone, retaining UTC values, precision and other exact bytes. Verify day rollover (including `TZ=Pacific/Kiritimati`) rather than freezing machine-local expectations.
- **Evidence boundaries:** a successful publish/`--help`, loopback, vector, simulator, healthy SAM session or leaf-only topology is not broader acceptance. Require missing-object/nonzero-byte evidence for incremental Git fetch; distinguish reference configuration limits from wire failures; keep live, native, physical and hosted evidence separately labeled.
- **Ownership and diagnosis:** bounded queues/backpressure, explicit owner cleanup and process lifetimes prevent one peer/observer failure from taking down unrelated work. Callback exceptions must be diagnosed; optional observers must not gate protocol processing.
