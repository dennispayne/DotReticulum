# DotReticulum development history

Historical reference only: this file records milestone outcomes and verification evidence; active work is tracked in [TODO.md](../TODO.md), the original roadmap snapshot is [docs/archive/plan.md](../docs/archive/plan.md), and current-state documentation lives in `README.md` and `docs/`. Historical completion does not imply full Reticulum parity.

## Reference pins and provenance

### Readable Pester compatibility cases and citation discovery

Compatibility is being reorganized around real Pester `It` cases under
`tests\Compatibility\Claims`, with shared operational helpers under `Support`.
The citation catalog inventories upstream claims without duplicating test
descriptions, scenario mappings or source paths. Stable test tags identify
individual cases; claim tags join them to citations. Family is removed.
`Run-CompatibilityTests.ps1` executes the cases, `List-CompatibilityTests.ps1`
discovers them without starting nodes or fetching reference objects, and
`Create-CompatibilitySummary.ps1` aligns the actual Pester outcomes across
Upstream/Linux/macOS/Windows. There is no calculated claim-level pass/fail.
Unimplemented advertised claims remain discovery coverage gaps, not synthetic
test results. Existing fixture provenance and the reference pin are unchanged.

The preceding candidate-owned retrieval implementation (`4672202`) passed all
**12 jobs** in Actions run **`40621550584`**, including all native retrieval
candidates, the subsequent upstream baseline, Test and both aggregation jobs.
This closes the earlier pending cross-platform acquisition acceptance; it does
not verify the subsequent Pester reorganization.

Local baseline before the reorganization:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet` completed with zero
warnings/errors, and
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet` passed
**1,831** cases with **79 skipped**, zero failed; opt-in live tests were disabled.
NativeAOT:
`dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o TestResults\compatibility-pester-native --nologo -v quiet`
passed with MSVC setup in the same process. The binary ran `--help`;
`dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~NativeResourceTests' --nologo -v quiet`
passed its native encrypted resource-transfer case.

Pester validation of the reorganized harness:
- `pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly`: **44 passed,
  2 explicit skips**, zero failed. Skips require actual candidate/reference
  arguments; they are exercised in the parameterized run below.
- `Invoke-Pester` with a `New-PesterContainer` for
  `tests\Compatibility\Support\Harness.Tests.ps1`, supplying the published
  Windows executable and candidate-fetched Python checkout/venv:
  **10 passed**, zero failed/skipped. Includes missing reference, failed
  retrieval, real per-case failure isolation for both candidates and
  upstream-only output.
- `pwsh -NoProfile -File tests\Compatibility\Run-CompatibilityTests.ps1 -Application TestResults\compatibility-pester-native\DotReticulum.Applications.exe -Implementation DotReticulum -FetchReference -ReferenceDirectory TestResults\pester-compatibility-reference -OutputDirectory TestResults\pester-compatibility-network -NoJobSummary`:
  **7 passed**, zero failed/skipped, including an actual **79,683-byte** native
  hosted fetch and the fixed-upstream-peer proof/no-proof cases. Retrieved head
  and pin/tree match the preceding candidate-owned evidence above; no pin move
  or publisher-authentication claim.
- `pwsh -NoProfile -File tests\Compatibility\List-CompatibilityTests.ps1 -IncludeUncoveredClaims`:
  seven real test declarations and five uncovered claims, including joined
  human-readable upstream citations, without test execution.

Following readability feedback, compatibility claim IDs are now `C001` through
`C009`, and the seven individual tests are `T001` through `T007`. These IDs are
stable identifiers, not dynamic row numbers; new tests append IDs rather than
renumbering old ones. Citation metadata retains the advertised claim text;
Pester `Describe` names the group and `It` names the concrete acceptance case.
The short-ID tooling run passed **45** cases with **2 explicit skips**, zero
failed, and the native suite passed all **7** cases using this executable's fresh
retrieval evidence. Four candidate executions do not multiply seven distinct
tests into twenty-eight distinct tests.

A local Windows upstream-only run of the new Pester harness passed the six
core cases but failed the real Python Git-helper case with upstream's
`Bundle verification failed for batch` after observing **79,669,382** received
bundle bytes. This is retained as a real failure, not converted to a skip or
inferred pass from the native candidate. No reference-side shim was applied.
The hosted upstream baseline runs on Linux; its results are separate evidence
and do not establish Windows support for the Python Git helper.

Actions run **`40626931637`** passed all four Compatibility candidate jobs and
the table renderer. The Linux/macOS **Test** jobs exposed a reporting regression:
PowerShell wraps a deliberate wrong-pin diagnostic at terminal width, breaking
a literal-space assertion; ANSI error text then prevented Pester from exporting
that failure as NUnit XML. The summary regression subprocess now requests plain
text, and the assertion tolerates diagnostic line wrapping without changing the
required rejection. A narrow-width (`COLUMNS=80`, `TERM=xterm-256color`) local
Pester run passed **43** cases with **4 explicit environment-dependent skips**;
its generated NUnit XML parsed successfully. Cross-platform revalidation remains
required for this reporting-only correction.

### Candidate-owned retrieval, reused baseline and table-only reporting

The user replaced the separate hosted fetch workflow with an advertised
Compatibility scenario: each native OS candidate now retrieves the reference
through its own `rngit`, then provisions the fetched pin for the shared tests.
`FUNC-GIT-REFERENCE` cites the upstream Git-over-Reticulum manual and requires
the pin to be missing before retrieval, positive received bundle bytes, strict
fsck and the exact pinned commit/tree. Native evidence includes the executable
SHA-256. A changing remote head is recorded, not silently adopted as the pin.

The native jobs run first; one Linux source bundle is retained as the same-run
`reference-source` artifact. Test and the subsequent **Compatibility / Upstream**
job reuse that bundle, rather than looking up historical fetch runs or retaining
three source archives. Other fetched repositories/venvs are runner-temporary.
The standalone `upstream-reference.yml` and historical artifact downloader are
removed. Retrieved-head comparisons remain explicitly available through
`compare-upstream.ps1`, not a separately scheduled acquisition pipeline.

The upstream baseline runs the same six core scenarios once after the native
candidates, plus the same hosted retrieval acceptance using the real Python
`git-remote-rns`. Its thin adapter only observes received bundle file sizes;
upstream still performs discovery, requests, resources and Git unbundling.
No reference wire/file-retention shim was added. The Python helper must run
without inherited `GIT_DIR`, otherwise the import verifier checks the seeded
fetch repository instead of the selected reference checkout. The adapter also
follows the advertised symbolic HEAD rather than assuming `main`; the actual
hosted upstream advertises `refs/heads/master`.

The compatibility summary now contains only the nine-row table, with a
commit-pinned linked Test ID, full behavior and Upstream/Linux/macOS/Windows
columns. Family, redundant title and before/after commentary are removed.
Unimplemented claims keep plain IDs; no nonexistent test link is fabricated.
Failure/boundary/provenance detail remains in logs, the catalog and JSON.

An intentionally failing tooling regression leaked its fake CLI diagnostics
into the real Test job summary because child scripts inherited
`GITHUB_STEP_SUMMARY`. `Start-TestProcess` now removes Actions summary/env/output/
path/state file variables by default, while allowing explicit owned-path
overrides. A regression verifies both isolation and deliberate overrides.
Only the two aggregation jobs write main-workflow summaries; per-platform
coverage output stays in logs/artifacts.

Local verification so far:
- `pwsh -NoProfile -File tests\run-compatibility.ps1 -Application TestResults\shared-claims-native\DotReticulum.Applications.exe -Implementation DotReticulum -FetchReference -ReferenceDirectory TestResults\rf-native -OutputDirectory TestResults\compatibility-rngit-native -NoJobSummary`:
  actual native Windows retrieval of **79,683** bundle bytes, strict fsck and
  source-import verification passed; all seven candidate scenarios passed
  (four claim rows Pass, five Not covered).
- Retrieved head `ca6451a02aead88734e47193dc228055d42c9854`, tree
  `a3b5484c6f56df70832deeaf725a42bda8abb63b`; unchanged pin
  `a29bfb51c468945c96a1ee22cae0114e993e2391`, pinned tree
  `24d4c393de9fd57b933836cea68891fd953ab060`. Mirror-parent seed remains
  `7f2b3b9b524c9386316379af1313b43a5e4f7a5d`; no publisher authentication.
- Pester `tests\Pester\Coverage.Tests.ps1`: **13 passed**, zero failed/skipped;
  includes linked IDs/table-only output, single-baseline reuse/duplicate and
  wrong-pin rejection, retrieval failure reporting, baseline-only execution
  and real Actions summary isolation.

The preceding shared-scenario/title change (`a1d05c9`) passed all 11 jobs in
Actions run **`40619781254`**, including all native OS candidates and both
summaries. The summary leak shown by the user came from intentionally failing
tooling cases, not those passing real candidate scenarios; the isolation change
above addresses it. That run predates candidate-owned retrieval and the single
post-candidate upstream baseline, which require new cross-platform acceptance.

### Shared advertised-claim compatibility and fixed upstream peers

The user clarified that Compatibility means the same advertised, testable claim
scenario independently exercised against upstream RNS and .NET Linux/macOS/Windows.
For multi-node cases, upstream remains the fixed peer; upstream-to-upstream is the
baseline and only the candidate node changes. The previous mixed 47-row reporting
above is historical integration evidence, not proof of four-target compatibility.

Preserved the 39 behavioral/native/mixed-peer contracts and all selected Pester
cases in `tests\Integration\catalog.json` and `tests\run-contract-integration.ps1`.
CI now runs them under Test and includes their NUnit results in the Native table.
Compatibility's eight advertised-core rows have exact pinned README citations,
shared scenario IDs, acceptance criteria and explicit boundaries. Six scenarios
cover three rows: identity import/address derivation, fixture signature/decryption,
and positive/absent packet delivery proofs. The other five rows remain Not covered
on both implementations; existing integration tests do not pass them by inference.

Both probe scenarios launch the same pinned upstream RNS server and use identical
16-byte probes, candidate configurations, timeouts, peer observations and exit-code
assertions. Upstream Python is itself a candidate, not merely the peer. The peer
adapter accepts an explicit revision for separate head-drift runs; existing callers
retain the default pin check. No upstream source shim or newly generated vector
was introduced. Existing `parity7-rnid.json` expectations retain their provenance.
An initial run exposed upstream's `0.0%`/`100.0%` formatting versus .NET's integer
percentages; the shared assertion accepts equivalent numeric formatting without
changing the required loss values or exit codes.

Each compatibility matrix job now executes its own upstream baseline rather than
copying prior upstream results from the source artifact. Consolidation preserves
all eight rows and four target columns; a failing upstream baseline takes priority,
missing baselines cannot produce Pass, and pin mismatches fail explicitly.
Three added tooling regressions cover baseline aggregation/pin enforcement,
missing-reference reporting and failure propagation through the entry point.

Local verification:
- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: zero warnings/errors.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  Crypto 47, Core 171, Interfaces 314/23 skipped, Transport 543/16 skipped,
  Applications 756/40 skipped; zero failures, opt-in xUnit live tests disabled.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o TestResults\shared-claims-native --nologo -v quiet`
  with MSVC environment setup in the same process: passed.
- `pwsh -NoProfile -File tests\run-compatibility.ps1 -Application TestResults\shared-claims-native\DotReticulum.Applications.exe -Python TestResults\reference-provision-check\venv\Scripts\python.exe -Upstream TestResults\reference-provision-check\upstream -OutputDirectory TestResults\shared-claims-native-results -NoJobSummary`:
  all six scenarios passed independently on Python and native Windows (12 executions);
  three rows Pass and five Not covered on each candidate.
- Pester `tests\Pester\Coverage.Tests.ps1`: 10 passed, zero failed/skipped.
- `pwsh -NoProfile -File tests\run-contract-integration.ps1 -Application TestResults\shared-claims-native\DotReticulum.Applications.exe -OutputDirectory TestResults\reclassified-native-integration -NoJobSummary`
  with the verified reference paths in the environment: 127 passed, two skipped,
  16 unselected; 39 integration contracts (35 pass, two skipped, two not covered).
  This also executes native authenticated multi-megabyte rncp transfers.

Requested job titles are now Build, Test, Compatibility, Summarize Compatibility
and Summarize Test. GitHub generates the graph's `Matrix:` prefix itself; changing
workflow job names does not remove that UI-owned decoration. Build jobs still
produce no summaries. No release/tag was created or dispatched.

### CI reference provisioning and hosted acceptance

Hosted end-to-end run **`40521067543` passed** after supplying .NET 10 and full
application Git history. Our native `rngit` again retrieved **69,228** bundle
bytes from the hosted upstream and passed object checks. Pinned native/Python
compatibility passed **129**, failed/skipped **0**, with **16** unselected
non-contract cases: **47 rows, 42 pass, 5 not covered**. Both the four pinned
CLI comparisons and the four current-head comparisons passed on the actual
Linux native binary and Python. The unchanged fetched head/tree are recorded
below; this remains a seeded incremental fetch, not a full network clone or
publisher-authentication result. Verified source and independent upstream
results are now available as Actions artifacts for the three-platform jobs.

The first hosted fetch in run `40519261522` **retrieved 69,228 Git bundle bytes
using our native .NET stack** via `reticulum.thecryptosquid.com:4242`, after the
disclosed mirror-parent seed. Strict fsck passed. Retrieved head:
`30c6428e0b59bfc05563167c72c2e0f21ef015c0`; tree:
`0ff68fb38620afee8acb0cf8868ae0f7940fbf4a`. The pinned checkout and dependency
environment were then provisioned and import-verified on the Linux runner.
This establishes actual CI `rns://` retrieval, not publisher authentication.
The run failed later because C# fixture/remote-command tests needed .NET 10 but
the runner's default SDK was 8; setup-dotnet is now explicit in both reference
and main compatibility jobs. A second hosted run (`40520520527`) successfully
executed all four matching pinned and all four retrieved-head CLI comparisons
on Python and the native .NET binary. Its Git-helper fixture still failed because
the application checkout was shallow and Nerdbank.GitVersioning requires history;
both compatibility checkouts now request full history. Hosted full-suite
acceptance was subsequently completed by run `40521067543` above.

Added an explicit revision-scoped CLI adapter for drift comparisons; all existing
reference adapters retain the default pin check. Four identical CLI acceptance
cases execute on both implementations: key persistence, detached signature bytes,
authenticated fixture decryption and destination-address derivation. All eight
executions passed locally (managed .NET and Python). Pinned results are exported
separately from retrieved-head drift results; the latter cannot populate the
pinned Upstream column.

An initial `advertised-core` family maps the pinned README's Notable Features
to eight explicit rows. Five rows reuse actual acceptance cases for addressing,
crypto, proof/no-proof delivery, IFAC isolation and multi-megabyte resources.
Multi-hop routing, application requests and Channel/Buffer remain unmapped in
this new family rather than inheriting historical passes. Each row states its
bounded evidence; neither a few megabytes nor fixtures establish every advertised
scale/security claim. Upstream gets its own column populated only by directly
executed reference cases, not by using Python as a .NET test peer.

After adding these mappings, the locally provisioned pinned-peer run passed
**127**, skipped **2**, with **16** unselected cases: **47** catalog rows,
**40 pass, 2 skipped, 5 not covered**. No newly generated reference vectors or
changed compatibility pin is claimed.

Added a manual/weekly hosted fetch workflow using the existing native Linux
Build artifact and the previously used public TCP gateway. The reference fetch
seeds through parent `7f2b3b9b524c9386316379af1313b43a5e4f7a5d` from the
GitHub comparison mirror, asserts the pin is absent, then requires actual
nonzero bundle bytes from native `rngit` over Reticulum. Strict fsck, head/tree
and seed/network provenance are recorded without publishing identities or config.
Only the verified Git bundle and provenance are uploaded.

Main compatibility jobs can download that reference artifact and provision fresh
checkouts/venvs outside tracked source. An unavailable artifact remains a visible
reference prerequisite, not a pass. Existing pinned Python import/clean-tree checks
are retained; the versioned requirements record all six dependencies from the
existing verified environment, including the separately installed RNS package
needed by LXMF. Runtime imports must still resolve to the fetched source checkout,
not that installed package.

Local provisioning from a Git bundle of the existing verified checkout succeeded;
`verify_reference.py` reported RNS 1.5.7 from the new checkout. Running
`tests\run-compatibility.ps1` with those newly provisioned paths passed **127**,
skipped **2**, with **16** unselected cases; contract results were **35 pass,
2 skipped, 2 not covered**. This is local pinned-peer evidence, not hosted-network
acceptance or an upstream-head differential comparison; those were subsequently
completed separately in hosted run `40521067543` above.

### Consolidated compatibility results

Added `tests\compatibility_summary.ps1` and a final CI aggregation job that
downloads each platform's compatibility JSON. All catalog rows retain their IDs,
families and full behavior descriptions alongside Linux, macOS and Windows
columns. Missing platform reports or contract rows are explicitly unavailable.
Failure and skip details are retained below the table. The aggregation runs after
failed matrix jobs too; it does not replace their failing conclusions.

Contract aggregation no longer labels a partially skipped/not-run contract as
passed because one associated case passed. Unexpected incomplete execution and
Pester container failures fail the runner. This can make earlier broad contract
results appear more conservative without changing the tests themselves.

Test results also export structured per-assembly counts and failure/skip details,
then combine Unit, Integration and Native tables across the same three platforms.
Build jobs no longer write summaries, and per-platform test/compatibility tables
remain in artifacts/logs rather than duplicating the consolidated tables.

`pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly` passed 7/7,
including ID-based alignment with reordered input, missing OS/contract reports,
Markdown escaping and failure/skip details. The consolidated renderer also ran
against all three real artifacts from Actions run `40463533001`.
One full managed compatibility invocation failed waiting for the default-created
daemon's ready log (`UTIL-RNSD-002`); this was not hidden as a summary pass.
Actions run **`40521069184` passed all 11 jobs**, including the three Build,
three Test, three Upstream Compatibility jobs and both consolidated summaries.
Downloaded real platform reports were rendered again locally: all five mapped
advertised-core cases passed on Linux, macOS and Windows. The directly executed
Upstream addressing/crypto cells passed; the other advertised-core Upstream
cells remain **Not run**, not inferred from peer evidence. All 47 rows are kept;
the three newly catalogued routing/request/Channel cases remain **Not covered**.
No upstream source or Python environment is vendored; separate hosted
provisioning/retrieval acceptance is recorded above.

### Public medium timing estimates for ecosystem scheduling

A subsequent Linux ecosystem idle-link reconnect failure exposed a separate
initialization gap: `LinkManager.Connect` queued LINKREQUEST but never recorded
outbound activity. A pending link's zero `LastData` looked epoch-old to application
idle maintenance. Pinned `Link.py:318-321` calls `had_outbound()` after sending the
request. Native now records activity only after successful queue admission.
A deterministic fake-clock test verifies pending status and `LastOutbound`,
`LastData`, `NoDataFor` and elapsed activity before any handshake.
Focused timing tests passed **4/4 on Windows and Linux**. The full stack passed
**1,831**, skipped **79**, failed zero; one preceding run had an unrelated
refused-port interface test failure whose targeted rerun passed, so no socket
fix is claimed. Strict Windows NativeAOT and actual encrypted multi-megabyte CLI
send/fetch acceptance passed (**1/1**, 6.89 seconds). The test bearer is explicitly
started, preventing an asynchronous send-before-start error from changing pending
status during the regression itself.

Exposed the existing `TransportRouter.FirstHopTimeout(ReadOnlySpan<byte>)` and
added `GetLowestInterfaceBitrate()` / `GetMediumPathTimeout()`. Read-only daemon
management now reuses the router calculations instead of maintaining a second
implementation. Rates are bits/second; timeout values are seconds, with explicit
unknown-rate results and the source minimum-rate clamp. This unblocks LXMF
slow-medium scheduling without making the application invent transport values.

`GenerateMediumTimingVectors.py` executes pinned RNS
`a29bfb51c468945c96a1ee22cae0114e993e2391` interface prioritization and timing
methods. Its fixture records canonical source SHA-256 values, seven medium
cases and four first-hop cases. Windows and WSL/Linux focused timing/receipt
commands passed **14/14**, zero skips. Source metadata is fake and path latency
is supplied to the actual Python method: this is reference calculation evidence,
not RF timing measurement. Dynamic native online metadata refresh is immediate;
Python caches its lowest rate during interface prioritization.

`dotnet build DotReticulum.slnx -c Release` passed with zero warnings;
`dotnet test DotReticulum.slnx -c Release --no-build` passed **1,830**, skipped
**79** opt-in cases, failed zero. Strict Windows x64 NativeAOT publication with
`ILLinkTreatWarningsAsErrors=true` and `IlcTreatWarningsAsErrors=true` passed.
The published binary ran `--help` and the real CLI encrypted multi-megabyte
send/fetch acceptance case (**1/1**, zero skips, 6.37 seconds).

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

## Opt-in extended application map keys

LXST's arbitrary signalling surface exposed the remaining native map-key domain:
pinned RNS `a29bfb51c468945c96a1ee22cae0114e993e2391`, `RNS/vendor/umsgpack.py:772-805`,
accepts nil, recursively tuple-converted arrays, infinities and distinct NaNs.
The existing application-value generator now executes eight additional cases
against that exact source and independently records packing and unpack/repacking.
No reference shim or source change is used.

`MsgPackMap` represents these keys as ordered nullable-key entries.
`UnpackApplicationValue` and its Try counterpart require the explicit
`allowExtendedMapKeys: true` opt-in. Ordinary decoded maps remain dictionaries;
legacy projection, request-handler defaults and existing restricted readers are
unchanged. Structural tuple/binary and exact numeric equality reject duplicates.
NaN-containing keys remain distinct and bypass same-hash duplicate checking.
Unhashable map-containing keys fail; all ordinary bounds still apply before
recursive key comparison. Unlike Python's tuple decoder branch, duplicate
non-NaN tuple keys are diagnosed rather than silently overwritten.

Validation on Windows:

- `dotnet test tests\DotReticulum.Core.Tests\DotReticulum.Core.Tests.csproj -c Release --filter "FullyQualifiedName~MsgPack"`: **33 passed**, zero skipped. Cases cover executed bytes, default-reader rejection, nested and numeric structural equality, repeated NaNs, cyclic keys, depth bounds and truncated input.
- `dotnet test DotReticulum.slnx -c Release`: **1,827 passed, 79 opt-in skipped**.
- `dotnet test DotReticulum.slnx -c Release --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --logger trx --results-directory TestResults\coverage-extended-map-acfd`: **1,827 passed, 79 skipped**.
- `pwsh -NoProfile -File tests\coverage_summary.ps1 TestResults\coverage-extended-map-acfd`: overall **22,063/26,532 lines (83.16%)**, **15,998/22,891 branches (69.89%)**; Core **1,840/1,899 lines (96.89%)**, **1,153/1,268 branches (90.93%)**. Shared production code counted once. Other project line/branch coverage: Applications 76.92%/63.78%, Crypto 94.83%/94.00%, Interfaces 85.60%/76.41%, Transport 91.38%/79.53%. Live mode disabled.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 --artifacts-path artifacts\parent-map-aot-build -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts\native-extended-map`: passed with MSVC environment initialized; published `--help` passed.
- With `DOTRETICULUM_NATIVE_EXE` selecting that binary, `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeResourceTests -- xUnit.MaxParallelThreads=1`: **1 passed**, zero skipped; actual encrypted multi-megabyte Resource feature flow, not new extended-map live evidence.

WSL Ubuntu/Linux x64 `dotnet test tests/DotReticulum.Core.Tests/DotReticulum.Core.Tests.csproj -c Release --artifacts-path <isolated-native-output> --filter "FullyQualifiedName~MsgPack"`: **33 passed**, zero skipped. Extended-map live LXST and native application traffic require separate ecosystem acceptance; vectors and AOT publication do not establish them.

## Incoming-link initialization ordering (2026-10-26)

Ecosystem `lxmd` acceptance exposed a first-packet race: the responder marked a link active and asynchronously queued its destination initializer behind optional observers. Identification/data could then be proved before handlers were installed. Pinned RNS `a29bfb51c468945c96a1ee22cae0114e993e2391`, `RNS/Link.py:515-535`, invokes the owner callback inside RTT processing. The responder now runs this short initializer in ordered core-after-receive work, outside the link lock but before subsequent ingress. General link-established observers remain asynchronous. Initializer failures are reported through the protected diagnostic mechanism and do not stop later packets.

Two deterministic regressions saturate every responder observer slot, establish a link, identify the initiator and immediately send/prove its first packet. Both failed before the fix and pass afterward, including an initializer that installs handlers and then throws. No protocol format, reference pin or transport interface changed.

Windows validation:

- `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --filter "FullyQualifiedName~LinkTests|FullyQualifiedName~Parity5ResourceTests"`: 85 passed, 9 opt-in cases skipped.
- `dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --logger trx --results-directory TestResults/coverage-ecosystem-link-acfd`: 1,819 passed, 79 skipped, zero failures; live mode disabled.
- With the verified isolated Python/RNS environment and `DOTRETICULUM_LIVE=1`, `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --filter "FullyQualifiedName~LiveLinkTests.PythonInitiator_DotNetResponder_DataIdentifyRequestAndTeardown|FullyQualifiedName~LiveLinkTests.Resource_TransfersOverPythonInitiatedLink_AndFileResponseRequest"`: 2 passed, zero skipped.
- `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts/native-ecosystem-link`: succeeded; published `--help` succeeded.
- With `DOTRETICULUM_NATIVE_EXE` pointing to that binary, `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --filter FullyQualifiedName~NativeResourceTests -- xUnit.MaxParallelThreads=1`: 1 passed, zero skipped. The native listener decrypted/decompressed a 2,200,017-byte Resource and returned a compressed multi-megabyte response. This is native/managed feature execution, not a new native/Python or RF claim.

`pwsh -NoProfile -File tests/coverage_summary.ps1 TestResults/coverage-ecosystem-link-acfd` merged production coverage without double-counting shared assemblies:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 10551/13726 (76.87%) | 9140/14338 (63.75%) |
| Core | 1784/1846 (96.64%) | 1082/1206 (89.72%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4531/5291 (85.64%) | 2474/3243 (76.29%) |
| Transport | 4978/5464 (91.11%) | 3150/3982 (79.11%) |
| **Overall** | 21954/26443 (83.02%) | 15893/22819 (69.65%) |

## Ordered protocol ingress and receiptless enqueue feedback (2026-10-26)

Native ecosystem telephone stress exposed adjacent asynchronous observer callbacks executing in reverse order. Pinned `RNS/Link.py:957` itself launches one thread per packet callback, so observer ordering is not claimed as an upstream guarantee. `SetPacketReceiver` is an explicit .NET protocol-adapter API: it receives decrypted data in ordered core ingress, outside the link lock, before optional asynchronous observers; it must enqueue briefly rather than block. Existing `PacketReceived` scheduling is preserved. Receiver exceptions use protected contextual logging without preventing later packets. Separately, `TrySend` exposes bounded receiptless enqueue success, eliminating the ambiguity of `Send(..., createReceipt: false)` returning null for success and failure. Neither API adds wire reliability or delivery confirmation.

Focused in-process cases exercise ordered reception without observers, recovery after a throwing receiver, closed-link rejection and a deliberately blocked bearer saturating the outbound queue. `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --filter FullyQualifiedName~LinkTests --nologo -v quiet`: 15 passed, 8 live cases skipped.

Windows validation:

- `dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/coverage-ordered-ingress-acfd --nologo -v quiet`: 1,821 passed, 79 skipped, zero failures.
- With verified RNS `a29bfb51c468945c96a1ee22cae0114e993e2391` and `DOTRETICULUM_LIVE=1`, `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --filter FullyQualifiedName~LiveLinkTests --nologo -v quiet`: 11 passed, zero skipped; bidirectional data, identification, requests, channel and multi-megabyte Resource cases.
- `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts/native-ordered-ingress`: succeeded. Published `--help` and `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeResourceTests --nologo -v quiet -- xUnit.MaxParallelThreads=1` with that binary: 1 passed, zero skipped; encrypted/compressed multi-megabyte Resource flow.

`tests/coverage_summary.ps1 TestResults/coverage-ordered-ingress-acfd` reported production line/branch coverage: Applications 76.88%/63.75%, Core 96.64%/89.72%, Crypto 94.83%/94.00%, Interfaces 85.56%/76.32%, Transport 91.27%/79.22%; merged overall 21,972/26,456 lines (83.05%) and 15,900/22,821 branches (69.67%). Ecosystem native telephone acceptance follows separately after its adapter opts into the APIs; this stack validation is not physical audio or RF evidence.

## Completed resource-work drain race (2026-10-26)

An ecosystem propagation stress capture showed a resource-work removal continuation waiting for the link lock while `CloseLocked` synchronously entered `DrainResourceWorkAsync` holding that same lock. A completed `Task.WhenAll` did not yield, so repeatedly snapshotting the still-registered completed task could spin indefinitely and prevent the removal continuation from acquiring the lock. The drain now prunes completed tasks under the lock before taking its pending-work snapshot; the original continuation still observes and reports failures.

The deterministic regression completes tracked resource work on another thread while the send preparation callback holds the link lock, then tears down and joins both work and cleanup within a deadline. Targeted `LinkTests|Parity5ResourceTests`: 88 passed, 9 live cases skipped.

Windows gates: `dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/coverage-resource-drain-acfd --nologo -v quiet` passed 1,822, skipped 79. With verified pinned RNS and live mode, `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --filter FullyQualifiedName~LiveLinkTests --nologo -v quiet` passed all 11. Strict `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts/native-resource-drain`, native `--help`, and `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeResourceTests --nologo -v quiet -- xUnit.MaxParallelThreads=1` with that native executable all passed (native flow 1/1).

Production merged coverage from `tests/coverage_summary.ps1 TestResults/coverage-resource-drain-acfd`: Applications lines/branches 76.87%/63.75%, Core 96.64%/89.80%, Crypto 94.83%/94.00%, Interfaces 85.60%/76.16%, Transport 91.42%/79.52%; overall 21,984/26,459 lines (83.09%) and 15,908/22,821 branches (69.71%). No wire behavior or reference pin changed.

WSL Ubuntu/Linux x64 follow-up at source commit `36dafd2`, using `/home/dennis/.dotreticulum-dotnet/dotnet`:

- `dotnet test tests/DotReticulum.Transport.Tests/DotReticulum.Transport.Tests.csproj -c Release --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/parent-stack-linux --filter "FullyQualifiedName~LinkTests|FullyQualifiedName~Parity5ResourceTests" --nologo -v quiet`: 89 passed, 8 skipped.
- `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r linux-x64 --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/parent-stack-native-linux-build -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o /home/dennis/dotreticulum-ecosystem-acfd3d43/native/stack-final`: succeeded.
- Published `--help` and `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/parent-stack-linux --filter FullyQualifiedName~NativeResourceTests --nologo -v quiet -- xUnit.MaxParallelThreads=1` with `DOTRETICULUM_NATIVE_EXE` selecting that native output: 1 passed, zero skipped.

This verifies the actual Linux runtime/native encrypted multi-megabyte flow, not physical hardware.

## Exclusive packet-receiver ownership (2026-10-26)

`RegisterPacketReceiver` adds an atomic exclusive lease for the ordered protocol receiver. It rejects a closed link or existing receiver; legacy setter operations cannot replace or remove an actively leased receiver. Disposal uses registration identity, so repeated disposal cannot remove a replacement owner. The existing setter remains available for explicitly caller-managed ownership. Removal does not cancel an in-flight callback; adapters still own bounded enqueue/disposal coordination. An in-process eight-way registration race proves one winner, foreign setter rejection and stale-lease safety.

`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet` passed 1,823, skipped 79. The preceding full run had one transient `SignedDiscoveryPreservesTcpPolicyAndBackboneMetadataAttachesNativeHdlcAdapter` timing failure; its detailed isolated rerun passed, then the full rerun passed without changes to discovery code. `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts/native-receiver-lease` succeeded; published `--help` and `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeResourceTests --nologo -v quiet -- xUnit.MaxParallelThreads=1` with that native executable passed (1/1). These checks preserve existing packet observers and wire behavior; ecosystem adapter ownership acceptance follows after opting into the lease.

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

See [TODO.md](../TODO.md) for active scope. At the 2026-10-26 history boundary the roadmap remained incomplete: unresolved actual I2P stream/restart recovery; missing desktop RNode BLE, Darwin rnsh terminal backend and Android host/providers; no physical serial/TNC/RNode/Multi/Weave/RF acceptance; and remaining Python-router/crash restart, alternate-route, prolonged Channel/resource soak and impaired published-native/per-bearer matrices. Broader OS/device support is not implied by managed builds or macOS CI. Hosted pinned-Python process interoperability is now exercised as recorded above; broader mixed-mesh acceptance remains separate. No publisher/release-signature authentication is claimed; RNS object consistency is not publisher provenance. Full RNS parity remains incomplete.

## Lessons and decisions worth retaining

- **Create-only publication:** .NET `File.Move(overwrite:false)` can be check-then-rename on Unix. Publish a closed same-filesystem staging file with hard-link creation (`link`/`CreateHardLinkW`), then remove staging; never replace existing identity/configuration. Unsupported hard-link filesystems fail explicitly. This was verified on Windows NTFS and Linux ext4.
- **Git fsck path lengths:** strict verification's owned Git view must use a short path on Windows; nested `commit-graph-chain` paths can exceed limits despite `core.longpaths`. The verifier borrows the object store through alternates, checks all objects and does not touch caller refs.
- **Socket keepalive:** assert a nonzero enabled value, not integer `1`; Darwin reports valid flag value `8`.
- **Timezone-aware fixtures:** compare the captured local date fields in the execution timezone, retaining UTC values, precision and other exact bytes. Verify day rollover (including `TZ=Pacific/Kiritimati`) rather than freezing machine-local expectations.
- **Evidence boundaries:** a successful publish/`--help`, loopback, vector, simulator, healthy SAM session or leaf-only topology is not broader acceptance. Require missing-object/nonzero-byte evidence for incremental Git fetch; distinguish reference configuration limits from wire failures; keep live, native, physical and hosted evidence separately labeled.
- **Ownership and diagnosis:** bounded queues/backpressure, explicit owner cleanup and process lifetimes prevent one peer/observer failure from taking down unrelated work. Callback exceptions must be diagnosed; optional observers must not gate protocol processing.
