# DotReticulum development history

Historical reference only: this file records milestone outcomes and verification evidence; active work is tracked in [TODO.md](../TODO.md), the original roadmap snapshot is [docs/archive/plan.md](../docs/archive/plan.md), and current-state documentation lives in `README.md` and `docs/`. Historical completion does not imply full Reticulum parity.

## Reference pins and provenance

### Accepted complete extended live interoperability workflow

Exact-source **67eaa41**, **40961479281**, completed with **all 12 jobs
passed**: three Builds, four Compatibility candidates, three Tests and both
summaries. Downloaded all three actual `test-results-*-1` artifacts, separately
from the four already inspected Compatibility reports. Actual reports contain
the following **passed / failed / skipped** counts:

| Suite | Linux | macOS | Windows |
|---|---:|---:|---:|
| Unit | 1441 / 0 / 5 | 1441 / 0 / 5 | 1440 / 0 / 6 |
| Integration | 434 / 0 / 11 | 430 / 0 / 15 | 426 / 0 / 19 |
| Native executable/CLI/contract integration | 199 / 0 / 4 | 192 / 0 / 11 | 198 / 0 / 5 |
| Extended live integration | 59 / 0 / 6 | 58 / 0 / 7 | 57 / 0 / 8 |

These are separate suite executions, not a sum of distinct capability claims.
The extended reports preserve actual Applications/Interfaces/Transport assembly
rows and explicit hardware/platform/prerequisite skips. The actual Summarize
Test job **123767521328** rendered the extended rows, including
Applications **35/0/1**, **34/0/2**, **33/0/3** and Transport **17/0/0** on
each platform. All four Compatibility candidates independently passed
**42/42**, including the actual large **T059** request/response, as recorded
below. This checks off `interop-test.yml`; the separate three-repeat mesh
workflow was already accepted. Neither workflow is sustained soak or complete
impaired native/per-bearer/platform/hardware acceptance. The separately added
public Buffer SDK consumer is not present in this older source revision.

### Native public Buffer SDK consumer and four-candidate large-request acceptance

Added `tests/Fixtures/ChannelBufferConsumer`, a minimal separately published
public API consumer, without adding a production CLI/debug mode or changing
the protocol. `NativeBufferTests` drives actual pinned Python Buffer APIs over
loopback TCP: exactly **340,000 outbound bytes** (190,000 compressible and
150,000 random) and **200,000 returned bytes** in each of two cases, one
compressible and one random. Exact persisted length and SHA-256 must match
both ways. An ordinary upstream Channel observer records compressed/plain
nonempty received chunks before the original RawChannelReader; both must occur.
No reference protocol method or timer is replaced. Upstream must observe EOF,
and the native consumer requires incoming EOF while its link is still Active,
then awaits its actual teardown write.

The shared native process helper accepts an explicit executable and removes
inherited Actions command-file variables before applying any explicitly owned
environment. Missing live/native prerequisites explicitly skip locally; a
configured missing binary fails. CI publishes this SDK consumer in Build with
strict AOT/trimming warnings, its own commit/RID/executable manifest, and
reuses the same-run binary in Test. The artifact selector can export the
consumer's dedicated variable; four real child-process Pester cases exercise
both supported exports and reject a wrong commit or missing consumer binary.
Native resource and Buffer results use separate directories and parent-qualified
labels; summarizing actual TRX files verified two distinct assembly rows.
Normal CI requires the same-run verified reference for this new native flow.
This is bounded **Integration**, not shared Compatibility or loss/soak evidence.
Cross-platform hosted consumer acceptance remains pending.

Validation:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\native-buffer-final-baseline -- xUnit.MaxParallelThreads=1`:
  **1,864 passed / 90 explicit skips / zero failures**.
- In the same MSVC-configured process,
  `dotnet publish tests\Fixtures\ChannelBufferConsumer\ChannelBufferConsumer.csproj -c Release -r win-x64 -o artifacts\native-buffer-consumer -p:PublishAot=true -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v quiet`:
  strict publication passed; native `--help` passed. Executable SHA-256
  **9f7bbf3bdb3c18be6c51092b36684d270e9d970872d0f7637e4e507e92f10e66**.
- With `DOTRETICULUM_LIVE=1`, the existing import-verified RNS 1.5.7/LXMF 1.2.1
  environment and `DOTRETICULUM_BUFFER_CONSUMER` set to that native binary,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~NativeBufferTests' --logger trx --results-directory TestResults\native-buffer-both-modes -- xUnit.MaxParallelThreads=1`:
  **two passed / zero skips**.
- Before splitting return modes, the native consumer, existing published-native
  encrypted/compressed **2,200,017-byte** resource and terminal flow, plus the
  managed live bidirectional Buffer case passed together:
  `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~NativeBufferTests|FullyQualifiedName~NativeResourceTests|FullyQualifiedName~RnshNativeTests|FullyQualifiedName~Channel_MessagesAndBufferStreamsInteroperate_BothWays' --logger trx --results-directory TestResults\native-buffer-final-live -- xUnit.MaxParallelThreads=1`:
  **four passed / zero skips**.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **65 passed / two prerequisite skips / zero failures**.
- `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **66 tracked scripts / zero findings**.
- `Get-Help .\tests\Set-NativeBuild.ps1 -Full` discovers the added environment
  variable parameter description. No production source changed, and separately
  launched NativeAOT processes are not managed Coverlet coverage.

Separately, downloaded all four actual Compatibility reports from exact-source
**67eaa41**, **40961479281**: **42/42 passed on Upstream, Linux, macOS and
Windows**, including **T059**. The actual Summarize Compatibility job
**123759309158** renders T057/T058/T059 Passed for all four candidates with
commit-pinned test-code links. This resolves the earlier T059 hosted-pending
statement; the older b716cc6 external-reference failure remains recorded.
The full extended interop/Test workflow subsequently passed all 12 jobs; its
actual platform counts and checked workflow gate are recorded above.

### Accepted three-repeat mixed-mesh workflow on all desktop targets

Exact-source **86a9b72**, **40956389129**, completed successfully with all
**12 jobs passed**. Downloaded all three actual `test-results-*-1` artifacts;
each `summary-extended.json` has precisely three iteration rows, each
**seven passed / zero failed / zero skipped**: Linux **21/21**, macOS
**21/21**, Windows **21/21**, **63 executed extended cases** overall.
Actual TRX names, not only counts, confirm each iteration exercised:

- Mixed unknown-path discovery, announcements, encrypted datagrams and links.
- Published native daemon dormant-tunnel persistence and authenticated Python
  transport-path restoration after restart.
- Default endpoint watchdog TIMEOUT and fresh-session recovery in both roles.
- Bidirectional measured-window resource-loss recovery and actual router restart.
- Observation of an already authenticated Python delivery proof.
- The independently reproduced upstream cold-window cancellation negative control.
- Fresh-announcement route replacement through a surviving .NET backup, distinct
  links and bidirectional DATA/proofs in both initiator roles.

The actual Summarize Test job **123756558348** printed all three iteration
rows as **7 / 0 / 0** for each platform. Actual Summarize Compatibility job
**123742180383** printed T058 as **Passed** for all four candidates, with the
test-source hyperlink pinned to the executing commit. All three Builds, all
four Compatibility candidates, all three Tests and both summary jobs passed.
This closes the earlier Windows healthy-proof failure's repeated acceptance
gate after the queue/write-clock correction; failures in older runs remain
recorded rather than being relabeled.

`e2e-mesh-test.yml` is now checked off. Cross-platform alternate-route
acceptance is no longer Windows-only. This bounded three-repeat workflow is not
sustained soak, full native/impaired/per-bearer acceptance, automatic active-link
migration or passing upstream cold-window recovery. Those broader checklist
items remain open. No production changes or redundant build/coverage run were
needed for this evidence-only update.

### Transport-only route restoration and native I2P reused-configuration startup

Resolved the previously recorded Windows published-candidate startup failure
without changing I2P offline writes, probe retries or deadlines. Pinned
`Transport.py:340-355,404-485` loads replay hashes, routes and tunnels only when
transport is enabled. Our host restored them for leaves too, allowing an old
route to bypass discovery before an asynchronous bearer connected.
`HostStateProvider` now applies the upstream transport-mode guard while still
restoring known identities and cached resource proofs. Storage schemas and
transport-enabled restoration remain unchanged.

The existing stable-bearer restart test incorrectly expected leaf route
restoration. Its replacement exercises both restart modes from actual saved
transport state: transport mode must restore the route without a new announce;
leaf mode must retain the identity but require a new route, then complete a real
link request/response. Before the correction, the disabled-mode case failed and
the enabled-mode control passed.

The actual-router native case now starts two fresh isolated candidates and a
third reusing the first configuration/storage unchanged. Before that third
process, it requires the previous destination path to be present in storage.
Each process must decrypt exactly 257 bytes at the receiver on its own observed
accepted I2P child and validate the signed delivery proof. All three are single
probes with unchanged 90-second deadlines; no extra retries or cache deletion.

The unchanged receipt-clock executable reproduced the original failure:
fresh candidates passed, reused candidate three sent via its saved route without
a path request and timed out, **one failed / zero skipped**, **151 seconds**.
Artifacts:
`TestResults\i2p-topology-25e99ef9125242209bbfde4c31a0fd1f`.
The new strict Windows x64 executable passed the same three-process case:
**one passed / zero skipped**, **65 seconds**. Artifacts:
`TestResults\i2p-topology-54681e18c5f64d31be047cde2bcada62`.
Both used actual Java I2P **2.14.0 / SAM 3.1**, local-destination delivery,
unchanged stock client tunnel settings, transit disabled and no alternate bearer.
Owned router/test processes were stopped. Private router artifacts remain ignored
and were not uploaded.

Validation:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors.
- `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~ReticulumNodeTests|FullyQualifiedName~RuntimeStateStoreTests|FullyQualifiedName~LifecycleStateTests|FullyQualifiedName~SharedRuntimeTests' --logger trx --results-directory TestResults\leaf-path-restore-focused`:
  **71 passed / zero skips**.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --logger trx --results-directory TestResults\leaf-path-restore-coverage -- xUnit.MaxParallelThreads=1`:
  **1,864 passed / 89 explicit skips / zero failures**.
  `pwsh -NoProfile -File tests\Get-CoverageSummary.ps1 TestResults\leaf-path-restore-coverage`
  merged nine reports without double-counting production points:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 10654/13890 (76.70%) | 9191/14468 (63.53%) |
| Core | 1840/1899 (96.89%) | 1153/1268 (90.93%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4676/5689 (82.19%) | 2538/3477 (72.99%) |
| Transport | 5070/5546 (91.42%) | 3219/4052 (79.44%) |
| **Overall** | **22350/27140 (82.35%)** | **16148/23315 (69.26%)** |

- `. tests\Compatibility\Support\Preparation.ps1; Publish-CompatibilityApplication -Directory (Join-Path $PWD 'artifacts\native-leaf-route-policy')`:
  strict Windows NativeAOT/help passed; executable SHA-256
  **8a106f3bf7ca3c48231ee905bd14a089f7a52d113a9ce3b19496f1d7070f6e3e**.
- `$env:DOTRETICULUM_NATIVE_EXE = (Resolve-Path artifacts\native-leaf-route-policy\DotReticulum.Applications.exe).Path; & tests\Interop\Invoke-I2pTopology.ps1 -JavaExecutable (Join-Path $PWD 'TestResults\i2p-tools-java-win\jdk-21.0.12.1+1-jre\bin\java.exe') -JavaRouterDirectory (Join-Path $PWD 'TestResults\i2p-tools-java-win\router') -AllowPublicNetwork -JavaLocalDestinationRouting -PublishedFeatureOnly -NoBuild -SamEndpoint 127.0.0.1:17657 -TestTimeoutSeconds 360 -KeepArtifacts`:
  the actual three-process native I2P case passed as described above.
- With the verified reference and new native executable,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~NativeResourceTests|FullyQualifiedName~RnshNativeTests' --logger trx --results-directory TestResults\leaf-path-restore-live-native -- xUnit.MaxParallelThreads=1`:
  **two passed / zero skips**, including terminal initiation and exact encrypted/
  compressed **2,200,017-byte** resource receive/return.
- `pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application artifacts\native-leaf-route-policy\DotReticulum.Applications.exe -Python TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe -Upstream TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream -Implementation Both -OutputDirectory TestResults\leaf-path-restore-shared`:
  native **41 passed / one explicit skip**, actual upstream-to-upstream
  **40 passed / two explicit skips**, zero failures. T059 also requires exactly
  one output payload marker. External retrieval was not requested; upstream
  rnsh additionally requires POSIX. This is the complete canonical shared suite,
  not a managed replacement or internal API round trip.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **61 passed / two explicit prerequisite skips**.
  `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **66 tracked scripts / zero findings**.

The native I2P evidence is bounded startup/restart integration with a managed
receiver, not shared Compatibility, Python-role execution, public-tunnel recovery,
transport-enabled native startup or another OS. Native executable flows are not
included in managed coverage. The live reference pin remains unchanged.

Separately, exact-source full interop **40958443219**, source **b716cc6**,
failed candidate T001 retrieval on all three platforms:
`rngit: Could not establish link with <7649a50d84610232d1416b41d2896aff>`.
No canonical source bundle was produced; upstream and dependent reference cases
could not execute. This is not a T059 functional verdict or hosted acceptance.
The three Build jobs and both summary jobs passed; all four Compatibility and
all three Test jobs failed, including their explicit missing-reference dependency.
T059 therefore remains locally accepted only. Actual static artifacts for both
**86a9b72 / 40956391132** and **b716cc6 / 40958442860** contain zero
Actions/C#/PowerShell findings and the same two known Python fixture warnings;
all five static jobs passed in each run.

### Shared resource-sized application requests and responses

Added **T059 / C008**, keeping the same scenario, fixed command, seeded
ASCII-safe input and assertions for actual upstream and native candidates.
The actual **rnx CLI** authenticates to an explicitly allowlisted pinned
upstream execution listener. Its fixed PowerShell child persists and echoes
**21,874 exact input bytes**. Acceptance requires the persisted bytes and
returned output, authenticated identity, zero child exit/stderr, an observed
request-resource advertisement with the actual matching request ID, and
serialized request **and** response exceeding the actual **16,303-byte MDU**.
Observed final packed request sizes were **22,852 / 22,840 bytes**
(native/upstream); both response bodies were **21,923 bytes**. Command/path
encoding accounts for the small invocation-size difference.

The thin reference adapter invokes pinned `rnx.listen`, delegates to the
original `execute_received_command`, and adds normal application resource
callbacks. It neither replaces Link/Resource algorithms nor changes request
values, authorization, timers, serialization or responses. Source contracts:
`Link.py:473-510,804-854,885-897,1031-1040`, `Resource.py:168-230` and
`Utilities/rnx.py:63-138,155-249,376-396`, unchanged pin
**a29bfb51c468945c96a1ee22cae0114e993e2391** / RNS 1.5.7.
The existing import/revision verifier runs for both CLI and observer.
`reference_cli.py` now also exposes the unchanged rnx entry point.

Initial experiments remained failing evidence until resolved: the CLI adapter
had not yet exposed rnx; accepted incoming request Resources intentionally do
not populate `resource.request_id`, so observation now uses the original
advertisement's public `read_request_id`/`is_request` methods; and upstream
Windows console `print` translates LF to CRLF. The final **identical**
ASCII-safe input omits line breaks rather than normalizing mismatched bytes or
weakening assertions. This is bounded large request/response evidence, not
multiline console equivalence, arbitrary application values, concurrent
requests, denial/timeout/cancellation or comprehensive remote-execution parity.
Only the owned, explicitly authorized fixture command is executed.

Validation:

- `pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application artifacts\native-receipt-write-boundary\DotReticulum.Applications.exe -Python TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe -Upstream TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream -Implementation Both -OutputDirectory TestResults\large-request-shared-full`:
  native **41 passed / one explicit skip**, actual upstream-to-upstream
  **40 passed / two explicit skips**, zero failures. External reference retrieval
  was deliberately not requested; upstream rnsh additionally requires POSIX.
- Canonical Pester container for `Requests.Tests.ps1`, filtered to **T059**:
  final seeded-input and typed socket-error handling recheck passed for both
  candidates, **2 passed / zero skips**
  (`TestResults\large-request-deterministic-final`).
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **61 passed / two explicit missing-reference/candidate skips**.
- `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **66 tracked scripts / zero findings**.
  Read-only compatibility discovery now reports **42 distinct tests / 17
  unchanged quoted claims**, without starting nodes.

The reused strict Windows native executable is the queue/write-clock
milestone's actually exercised binary, SHA-256
**93c9523d8aa45c2dba2d23810e5287a909247debaf9ab3d219cb14b2684dcc81**.
No production code changed for this acceptance milestone. T059 subsequently
passed for all four hosted candidates in **40961479281**, source **67eaa41**.

Separately, all four hosted candidates passed **T053/T054/T058** in
**40956389129**, source **86a9b72**, establishing the earlier bounded small and
nested resource-response boundary on Linux/macOS/Windows/upstream. The
completed full-interop run **40953387442** at **b9777e1** passed all three
Build, native Compatibility and Test jobs; only the upstream helper's external
T001 hosted-link establishment failed, leaving its dependent baseline
unexecuted. Its extended Test results were Linux **59 passed / six skips**,
macOS **58 / seven**, Windows **57 / eight**, with no failures. Actual static
artifacts at b9777e1 contained zero Actions/C#/PowerShell findings and the two
existing Python fixture warnings, not zero findings overall.

### Start link receipt clocks at outbound send, not local enqueue

Completed repeated-mesh run **40947802917** at **b5d01f2** retained all three
iterations rather than hiding a first failure: Linux and macOS each passed
**21/21** extended cases; Windows passed **20/21**. Its first watchdog case
failed the initial managed DATA receipt at
`Parity8CompatibilityLiveTests.cs:398`, before any router crash. Windows
iterations two and three passed unchanged. The separate full interop run
**40947799428** passed all three Test jobs, including their extended cases,
but failed overall on the separately corrected native Windows T057 shutdown.
Neither run was a complete green gate; at that point both scheduled workflow
checklist items remained unchecked. The later passing repeated-mesh gate is
recorded above.

Two deterministic regressions then failed in both endpoint roles: hold an
earlier bearer write, enqueue DATA behind it, advance beyond the unchanged
proof deadline, and tick the link. The unsent packet was marked FAILED. Pinned
`Transport.py:1456-1458,1599-1602` creates its Link receipt after the synchronous
interface transmission, not in an additional local work queue.
The native bounded pump records send-start and arms the proof deadline when
its write attempt returns. Receipts stay registered during I/O so an actual
early proof can still authenticate; they do not expire behind prior writes or
inside their own write. Actual send
timestamp/RTT metadata exclude the queue wait. The original RTT multiplier,
five-millisecond minimum, strict expiry boundary, global receipt limit,
Channel timeout overrides and failure/cancellation settlement remain.
No sleeps, timer increases, reference patches or synthetic proofs were added.
The existing virtual-clock expiry test now observes the actual outbound
packet before advancing its post-send clock.

The four final regressions use an explicit clock in both endpoint roles.
Queued packets stay pending through a one-second queue delay, then require a
genuine validated proof and exact send timestamp/zero virtual elapsed RTT.
Packets inside their own blocked write cannot expire either; after release,
unproved packets must fail at the unchanged strict deadline. These establish the
queue-clock bug independently; missing historical send timestamps prevent
claiming that it explains every earlier loaded-runner receipt failure.
The live watchdog case now reports receipt timing/status, queue depth and
actual peer DATA event count if its healthy precondition fails again.

Validation:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: zero warnings/errors.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --logger trx --results-directory TestResults\receipt-write-boundary-coverage-final -- xUnit.MaxParallelThreads=1`:
  **1863 passed / 89 explicit skips / zero failures**.
  `pwsh -NoProfile -File tests\Get-CoverageSummary.ps1 TestResults\receipt-write-boundary-coverage-final`
  merged eight reports, counting production points once:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 10653/13888 (76.71%) | 9190/14466 (63.53%) |
| Core | 1840/1899 (96.89%) | 1153/1268 (90.93%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4675/5689 (82.18%) | 2541/3477 (73.08%) |
| Transport | 5056/5546 (91.16%) | 3209/4052 (79.20%) |
| **Overall** | **22334/27138 (82.30%)** | **16140/23313 (69.23%)** |

- `. tests\Compatibility\Support\Preparation.ps1; Publish-CompatibilityApplication -Directory (Join-Path $PWD 'artifacts\native-receipt-write-boundary')`:
  strict Windows x64 NativeAOT/help passed; SHA-256
  **93c9523d8aa45c2dba2d23810e5287a909247debaf9ab3d219cb14b2684dcc81**.
- With that actual native executable and verified live reference,
  `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~DataReceiptExcludesLocalQueueAndBearerWriteFromItsProofDeadline|FullyQualifiedName~LiveLinkTests|FullyQualifiedName~ManagedAndPinnedEndpointsAutomaticallyTimeOutAfterRouterCrashAndRecoverWithFreshLinks|FullyQualifiedName~NativeResourceTests|FullyQualifiedName~RnshNativeTests' --logger trx --results-directory TestResults\receipt-write-boundary-live-native-final -- xUnit.MaxParallelThreads=1`:
  **18 passed / zero skips**. All four final clock cases and verified pinned
  Python link roles ran. The unchanged default-watchdog case ran both
  initiator roles, actual router crashes/TIMEOUT and fresh proved sessions.
  Native terminal initiation and exact encrypted/compressed **2,200,017-byte**
  resource receive/return also ran. Canonical shared **T057** separately passed
  with this native binary and actual Python peer, including exact bytes/EOF
  and real peer shutdown (`TestResults\receipt-write-boundary-native-channel`).

Coverage remains managed ordinary-suite evidence; the separate live/native
flows are not included. Subsequent exact-source run **40956389129** passed all
three repetitions on all three platforms, as recorded above.

### Awaiting the actual link-close write before rnsh owner disposal

Exact-source full interop **40947799428**, source **b5d01f2**, failed Windows
shared **T057** because its upstream Channel application did not shut down after
the native candidate exited. Actual retained peer observations show **87,415
input bytes / 23 messages / EOF**, and **207 output messages / EOF**. The helper
previously threw before retaining candidate stdout/stderr; those streams are
now saved before checking peer shutdown. No stream, EOF or peer-close assertion
was removed or relaxed, and the existing deadlines are unchanged.

Source tracing found a real ownership race: synchronous `Link.Teardown`
enqueues its close packet, while immediate `ReticulumNode` disposal cancels
the outbound pump and discards queued packets. Pinned upstream
`Link.teardown`/`Packet.send` performs the outbound send synchronously.
The new cancellation-aware **Link.TeardownAsync** closes an open link and
settles only after its selected bearer write finishes. Concurrent asynchronous
callers share completion. The existing void API is retained. The bounded
256-entry queue now carries optional local-write completion, explicitly
settled on write error, manager shutdown or rejection; this is not a protocol
receipt, a peer acknowledgement, new retry policy or wire-format change.
`RnshSession.DisposeAsync` awaits it within five seconds before its caller
disposes the node. It does not delay arbitrarily or globally drain/rewrite
manager shutdown. Historical missing candidate diagnostics prevent proving
that every earlier peer-close failure had this exact timing.

Five focused cases gate the actual bearer write, require both asynchronous
callers to remain pending, release it and immediately dispose the owner before
requiring the remote closure, in both endpoint roles. They also cover caller
cancellation, manager shutdown and actual write failure without success-shaped
completion.

Validation:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: zero warnings/errors.
- `DOTRETICULUM_LIVE=1 DOTRETICULUM_NATIVE_EXE=artifacts\native-link-close-write-atomic\DotReticulum.Applications.exe dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~RnshTests|FullyQualifiedName~RnshLiveTests|FullyQualifiedName~LinkTests|FullyQualifiedName~LinkVectorTests|FullyQualifiedName~NativeResourceTests|FullyQualifiedName~RnshNativeTests' --logger trx --results-directory TestResults\rnsh-close-write-atomic-live-native-final -- xUnit.MaxParallelThreads=1`:
  **65 passed / one explicit POSIX-reference skip**; actual verified pinned Python
  link/rnsh peers, five strengthened close-ownership cases, native terminal
  initiation and native encrypted/compressed **2,200,017-byte** resource
  receive/return with exact persisted bytes.
- `. tests\Compatibility\Support\Preparation.ps1; Publish-CompatibilityApplication -Directory (Join-Path $PWD 'artifacts\native-link-close-write-atomic')`:
  strict Windows x64 NativeAOT publication and help passed; SHA-256
  **5dc035e2a3d148903429a748d899b79df8969f6e398657632389005caf34d34d**.
  **T057** then passed **ten separate native/Python executions / zero skips**
  through the canonical Pester discovery/configuration, each preserving exact
  stream bytes, EOF and actual peer shutdown
  (`TestResults\channel-close-write-atomic-native-repeat\iteration-1` through `iteration-10`).
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --logger trx --results-directory TestResults\rnsh-close-write-atomic-coverage-final -- xUnit.MaxParallelThreads=1`:
  **1859 passed / 89 explicit skips / zero failures**.
  `pwsh -NoProfile -File tests\Get-CoverageSummary.ps1 TestResults\rnsh-close-write-atomic-coverage-final`
  merged nine reports by production source point, not average percentages:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 10651/13888 (76.69%) | 9188/14466 (63.51%) |
| Core | 1840/1899 (96.89%) | 1153/1268 (90.93%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4673/5689 (82.14%) | 2538/3477 (72.99%) |
| Transport | 5045/5530 (91.23%) | 3195/4038 (79.12%) |
| **Overall** | **22319/27122 (82.29%)** | **16121/23299 (69.19%)** |

Coverage is managed ordinary-suite evidence with live opt-ins disabled; it does
not instrument the separately exercised native executable. The first coverage
attempt retained one unrelated Windows resolver-test executable-image sharing
violation at its exclusive-write assertion. Its isolated instrumented test
passed, then the fresh complete instrumented gate above passed unchanged.
No speculative resolver change, retry loop or weakened assertion was added.
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`: **65 files /
zero findings**. Fresh-source **T057** passed on all three native platforms in
**40953387442** at **b9777e1**. Its upstream baseline failed the external
**T001** prerequisite: the actual upstream Git helper
resolved a path but could not establish its hosted retrieval link. Dependent
upstream cases were not promoted to passing; this is not evidence of a native
Channel or management-response failure.

### Shared small-packet and resource-sized management responses

Added **T058 / C008**, using the same actual `rnstatus` invocation, inputs and
assertions for the upstream and .NET candidates against a fixed pinned upstream
transport. It keeps **24 owned loopback TCP connections** open, observes their
actual registered upstream child names before querying, and requires exact
agreement between every returned nested interface name and the original status
generator's response. Its packed response must exceed the **observed** negotiated
MDU; the unchanged pinned `Link.py:845-854` consequently uses the resource
response branch. Existing **T053** now explicitly requires a small response
fitting that MDU, and **T054** proves that the identified unauthorized peer
never invokes the protected response generator.

The thin observer registers through the normal application API, delegates to
`Transport.remote_status_handler` and preserves its existing identity allowlist.
It records actual packed length, link MDU, request/link IDs and interface names;
no protocol algorithm, timer, response value, serialization or resource policy
is replaced. Actual admission snapshots are atomically published after close;
the Windows reader permits replacement while reading. Owned children and
processes are bounded and closed. This is management request/response evidence,
not arbitrary application values, large requests, concurrency or complete
request failure/cancellation acceptance.

The initial 12-connection experiment correctly failed the intended boundary
assertion: actual responses were **12,640 / 12,793 bytes**, still below the
**16,303-byte MDU**. The final topology uses 24 real children rather than
shrinking the MDU, injecting padding or weakening the assertion. Final actual
responses were **23,274 bytes (.NET candidate) / 23,571 bytes (upstream
candidate)**, each with **26 interfaces** and the same **16,303-byte MDU**.
Both small responses were **2,008 bytes / two interfaces**.

Validation:

- `pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application TestResults\requests-b5d01f2-native\DotReticulum.Applications.exe -Python TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe -Upstream TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream -OutputDirectory TestResults\requests-boundary-shared-final -Implementation Both`:
  Windows published-native **40 passed / one explicit skip**; actual
  upstream-to-upstream **39 passed / two explicit skips**; zero failures.
  Hosted retrieval was deliberately not requested in either execution;
  upstream rnsh additionally requires POSIX. All three management cases
  passed for both candidates. Discovery now has **41 distinct tests /
  17 unchanged quoted claims**.
- The native executable is the exact **win-x64** Build artifact downloaded from
  repeated mesh **40947802917**, with manifest verified at source
  **b5d01f2bb2a458bdd0d087e9401749ecad100b2d**. No duplicate local publication
  or production coverage is claimed. Imported upstream remains verified at
  **a29bfb51c468945c96a1ee22cae0114e993e2391**, RNS 1.5.7 / LXMF 1.2.1.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **61 passed / two explicit missing-reference/candidate skips**, zero failures.
- `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **65 tracked PowerShell files / zero findings**.

Fresh-source hosted request-boundary acceptance subsequently passed in
**40956389129** and **40961479281**, as recorded above. Earlier-source
full interop **40947799428** and three-repeat mesh **40947802917** were dispatched
at **b5d01f2**; their outcomes are not inferred from local results.

### Isolating controlled bearer loss from router ingress saturation

The completed macOS repeat failure had no new deliberate bridge drops on its
failing resource, yet the receiver lacked initial parts. Source review confirms
the fixture's non-default two-slot ingress queues can independently discard a
resource burst: `TransportRouter.InboundScheduling.cs:77-80` increments the
traffic-class drop counter on overflow, matching pinned
`RNS/Transport.py:1894-1895`. That is a different impairment from the three
explicit bearer drops and can reintroduce the separately demonstrated upstream
cold-window limitation before a measured window completes. Historical ingress
counters were not retained, so this remains fault isolation rather than a claim
to have proven every earlier failure's cause.

The controlled-loss scenario now uses the **actual pinned bounded defaults**
**1,024/128/128/8** (`Transport.py:143-146`), rather than searching for an
arbitrary larger queue. It snapshots all four ingress-drop counters before and
after each resource and requires no increase outside the controlled bearer.
Failure diagnostics and both router shutdowns retain capacities and actual
drop counters. Intentional saturation is still independently checked by
`InboundSaturationHasIndependentBoundsAndReferencePriorityOrder`, with its
two-slot DATA queue and existing independent reference vectors unchanged.

This intentionally removes the old two-slot mesh setting, not boundedness or
the capacity-one bearer pressure test. The 20 ms/frame latency, three deliberate
distinct drops per resource, all 15 observed repairs, two router restarts,
actual pinned peers and exact resource hashes are unchanged. Simultaneous
live router-overflow/bearer-loss recovery remains unverified, not silently
claimed from the now-isolated scenario.

Validation:

- `DOTRETICULUM_LIVE=1 dotnet test DotReticulum.slnx -c Release --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests|FullyQualifiedName~InboundSaturationHasIndependentBoundsAndReferencePriorityOrder' --logger trx --results-directory TestResults\mesh-controlled-loss-isolation-final -- xUnit.MaxParallelThreads=1`:
  **9 passed / zero skips**: eight Applications cases and the independent
  Transport saturation case. Actual mesh output retained **20 distinct initial
  parts / 15 dropped / 15 retried / 224 capacity-one waits / four TCP sessions**.
  Both router lifetimes reported **[0,0,0,0] ingress drops** at the pinned
  default capacities.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\mesh-controlled-loss-full-final -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 89 explicit skips / zero failures**.

No production behavior changed; subsequent hosted controlled-loss acceptance
passed in all three repetitions/platforms in **40956389129**.
Static **40946356295**, exact source
**cb9aef1**, completed all five jobs. Its inspected rendered report has zero
Actions/C#/PowerShell findings and the same two Python warnings recorded below.

### Default watchdog bounds and already-validated receipt observation

Inspected the actual Linux and Windows artifacts from repeated mesh
**40938753343**, source
**3aacad63d2ed7c4d502ee37860c6b50c49790db8**. Linux passed all five selected
cases in all three iterations (**15 passed / zero failures/skips**).
The automatic outage case exercised both initiator roles in each repetition,
with actual source-derived deadlines **38/35, 37/34 and 37/34 seconds**,
matched TIMEOUT closures and distinct proved recovered links.

Windows iterations were **5/0/0, 3/2/0, 5/0/0**. Its second automatic-outage
case rejected a legitimate **423-second** budget before crashing the router:
the actual RTT was **0.981228099999953 seconds**, adaptive keepalive
**201.85263771427606 seconds**, and stale interval
**403.7052754285521 seconds**. The four-minute fixture ceiling was not an
upstream policy. Its replacement maximum derives from default maximum stale
time plus this case's 90-second handshake bound, timeout factor, grace and two
watchdog polling intervals: **1,095 seconds**. Actual waits still use each
session's observed budget; there is no fixed sleep or reference timer change.

The other Windows failure was the all-upstream cold control's initial DATA
proof assertion, **not path discovery**. Actual peer logs prove that exact
DATA arrived, while the adapter reported `delivered=false`. Source review
found that a valid proof can conclude before `set_delivery_callback`; that
setter does not replay a past delivery. The adapter now registers its callback
and also checks the actual public receipt status. No unproved delivery is
accepted. New metadata retains before-observer/final status, actual proof flag
and unchanged receipt timeout.

The deterministic live regression
`PinnedReceiptObserverRecognizesAnAlreadyValidatedDeliveryProof` deliberately
registers after the real upstream receipt concludes. Before the fix it failed
with **DELIVERED before and after registration, proved=true, delivered=false**
and actual timeout **0.010577201843261719 seconds**
(`TestResults\reference-late-proof-observer-regression`). After the fix,
both directions require actual DELIVERED/proved receipts and exact peer bytes.
This proves the observation race, not the unrecorded exact receipt state in
the earlier hosted failure. The zero-delay healthy control and all proof,
MTU, loss, repair and negative-cancellation assertions remain intact.

One-shot setup also now observes actual registered Python server children,
using a thin interface-name/online observation in the existing adapter.
Initial connection success alone is not server admission. This is additional
setup hardening, not a claimed explanation of the hosted DATA/proof failure.

Validation:

- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests' --logger trx --results-directory TestResults\mesh-proof-observer-and-budget-final -- xUnit.MaxParallelThreads=1`:
  **8 passed / zero skips / 4m05s**, verified actual pinned Python peers.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\mesh-proof-observer-full-final -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 89 explicit skips / zero failures**.
- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~PinnedReceiptObserverRecognizesAnAlreadyValidatedDeliveryProof' --logger trx --results-directory TestResults\reference-late-proof-observer-final -- xUnit.MaxParallelThreads=1`:
  **1 passed / zero skips / 1 second**, final observer guard requires actual
  DELIVERED and proved state and verifies exact bytes in both directions.

These are test/observation changes, not production changes or additional shared
Compatibility capabilities. No redundant native publish or production coverage
is claimed. Fresh-source hosted acceptance remains outstanding; the workflows
stay unchecked.

The completed macOS artifact and actual rendered summary subsequently showed
iterations **5/0/0, 4/1/0, 5/0/0**. Its automatic watchdog case passed both
roles in every repetition, with derived deadlines **77/92, 170/107 and
112/126 seconds**, matched TIMEOUT closures and distinct proved recovered
links. This extends bounded managed watchdog acceptance to macOS, not native
impaired endpoints.

The macOS failure was a different measured-window resource: its second
2,200,019-byte transfer, with four parts reported sent but only two received,
no completed throughput estimate, ACTIVE receiver link and sender cancellation.
Bridge totals were still **3 dropped / 3 retried**, belonging to the preceding
successful transfer. This does not establish that the current resource's
missing initial parts were deliberately dropped by the bridge. The configured
two-slot router ingress queues can independently drop burst traffic; those
counters were not retained, so that remains a hypothesis requiring separate
fault isolation, not a proven historical cause.

All three Builds, all four Compatibility candidates and both actual renderers
passed, but the workflow **failed overall** on these Windows/macOS extended
cases. Its rendered iteration rows preserve every failure. The macOS job
finished in about 42 minutes, not by exhausting its 45-minute budget; no blind
workflow-timeout increase was made.

### Hosted artifact transport failure, not a protocol result

Full interop **40943225354**, exact source
**1e56396b4d1af352d3d7a79357f6062030ded2f9**, failed because macOS's native
artifact finalization returned **ECONNRESET**. Its actual Publish NativeAOT
step passed, and Windows/Linux Build jobs passed; the macOS Build job still
failed. The missing canonical reference then correctly failed Upstream reuse
instead of substituting an old bundle. This is not passing hosted acceptance
or an observed protocol failure. A fresh exact-source run is still required.

Static **40943226294**, same exact source, completed all five jobs; its actual
rendered report retains zero Actions/C#/PowerShell findings and the same two
Python warnings recorded below.

### Distinct initial-window parts, not replayed frames

Investigating the completed macOS mesh failure revealed a real fixture defect:
the pre-loss quota counted every forwarded RESOURCE frame, including a replay
of an already forwarded packet. Four counted frames could therefore mean
fewer than four distinct parts, and an initial part's later replay could
incorrectly consume the subsequent distinct-loss budget. The original failed
run did not retain the initial frame hashes, so this defect is **not proven
to be the sole cause of that historical timeout**.

The bridge now retains a bounded per-direction set of the initial packet
hashes, cleared when that direction is rearmed. Replays neither advance the
four-part quota nor consume any of the three distinct drops. Existing
lost-part/retry accounting and the unchanged 20 ms delay, bounded queues,
15 losses/repairs and upstream defaults remain intact. Failure and success
diagnostics now include observed initial-window replay counts.

The existing focused fixture case now repeats each initial frame and replays
an initial part again after the window. It failed before the correction
(`TestResults\mesh-initial-replay-regression`: **1 passed / 1 failed**), then
passed with the same exact distinct quotas.

Final validation:

- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests' --logger trx --results-directory TestResults\mesh-distinct-initial-window-final -- xUnit.MaxParallelThreads=1`:
  **7 passed / zero skips / 3m59s**, actual verified pinned Python peers.
  The measured-window case retained **20 distinct initial parts / 15 dropped /
  15 retried / 219 capacity-one waits / four TCP sessions**. Its live run
  observed zero initial replays; replay handling is independently established
  by the failing-then-passing focused fixture, not invented live evidence.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\mesh-distinct-initial-full-final -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 88 explicit skips / zero failures**.

No production/reference timer, packet format or MTU changed. A distinct
forwarded-part quota alone is not callback-level proof that an upstream
throughput estimate completed; actual failure observations remain visible.
Subsequent corrected-source repeat acceptance passed in **40956389129**.

### Fresh links through a surviving alternate transport

Added Windows managed live integration case
`SurvivingDotNetAlternateRouteCarriesFreshBidirectionalLinksAfterPinnedPrimaryRouterCrash`.
Two actual pinned Python leaves initially discover two-hop paths through an
actual Python transport and prove exact DATA both ways. A separately connected
.NET backup starts without replacing those selected paths. The actual primary
process is then force-killed; both leaves observe its TCP interface offline.

Fresh announcements follow the pinned `RNS/Transport.py:2232-2240` rule:
strictly newer whole-second emission timestamps permit same-hop replacement.
Neither path cache is cleared and neither leaf restarts. The thin Python
adapter adds only the actual public `Transport.next_hop()` value to existing
path observations. Both leaves must select the surviving .NET transport's
identity, its own paths to them must be direct, and each initiator role then
opens a distinct link and proves exact DATA both ways. Endpoint teardown
callbacks must correlate with each fresh link.

An exploratory assertion incorrectly expected immediate transit-table removal
on encrypted endpoint teardown. The pinned transport instead retains validated
entries until its traffic timeout or interface removal
(`Transport.py:152,873-881`), matching our existing implementation. The final
case correlates actual endpoint closure and requires each routed new link to
add a forwarding entry; it does not invent an immediate transit cleanup policy.

Validation with the unchanged import-verified pin:

- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~SurvivingDotNetAlternateRouteCarriesFreshBidirectionalLinksAfterPinnedPrimaryRouterCrash' --logger trx --results-directory TestResults\mesh-alternate-route-final -- xUnit.MaxParallelThreads=1`:
  **1 passed / zero skips / 3 seconds**, both recovered initiator roles.
- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests' --logger trx --results-directory TestResults\mesh-alternate-and-recovery-final -- xUnit.MaxParallelThreads=1`:
  **7 passed / zero skips / 4m04s**.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\mesh-alternate-full-final -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 88 explicit skips / zero failures**.

No production behavior changed; no redundant production coverage or native
publish is claimed. This is bounded integration acceptance after fresh
announcements, not shared Compatibility, automatic switching without
announcements, cached-alternate guarantees, active-session migration or
native/cross-platform acceptance. Broader matrices remain unchecked.

### Completed full interop after the scheduling corrections

Run **40934315953**, source
**9de09d31bf51698be40e96afe667753a7846a411**, completed with all three Builds,
all four Compatibility candidates, Linux Test and both renderers passing.
The actual macOS TRX confirms controlled autoconnect discovery, both
busy-observer theory cases, native Git hosting and all **29 rnsh cases**
passed. The corresponding corrected Windows cases also passed.

The overall run **failed**: Windows exposed the owned PID-file publication
race fixed below. macOS extended integration had **30 passed / 1 failed**:
the measured-window mesh case failed on its final 220,019-byte resource after
the Python-router restart, with sender/receiver FAILED, the receiver link
still ACTIVE, **13 dropped / 12 retried** at failure and an actual sender
part-request timeout. Receiver observations included four received parts,
`req_data_rtt_rate=0` and two outstanding parts. Four forwarded frames therefore
did not establish a completed measured window in that execution. Initial-frame
replay counting is corrected above, without proving the cause of this historical
failure; this is not
reclassified as a passing recovery or explained by the separate cold control.
The subsequent three-repeat run **40938753343** remains a separate pending
result, not a retroactive repair of this failure.

### Atomic owned-descendant PID readiness

The actual Windows Integration TRX from full interop **40934315953**,
source **9de09d31bf51698be40e96afe667753a7846a411**, showed
`RngitProcessTests.TimeoutKillsAnOwnedDescendantTree` failing with a sharing
violation while reading the fixture's PID file. File existence could precede
the writer closing its handle. The owned backend now writes a sibling
`.pending` file, closes it, then renames it to publish readiness. Both child
modes retain the same bounded timeout and actual descendant-termination
assertions; no read retries, extra sleeps or production changes were added.

The earlier discovery, busy-observer and native blocked-peer corrections
passed in that actual Windows run. Linux Test passed. macOS and the overall
run were incomplete when this correction was first recorded; the completed
outcome and the separate macOS mesh failure are recorded above.

Local validation:

- `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~RngitProcessTests' --logger trx --results-directory TestResults\atomic-pid-fixture-process -- xUnit.MaxParallelThreads=1`:
  **12 passed / zero skips**.
- `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~RngitMediaTests|FullyQualifiedName~RngitReleaseTests|FullyQualifiedName~RngitTemplatesTests|FullyQualifiedName~RngitRepositoryStoreTests' --logger trx --results-directory TestResults\atomic-pid-fixture-consumers`:
  **66 passed / 3 explicit skips**.
- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  **zero warnings/errors**.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\atomic-pid-fixture-full -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 87 explicit skips / zero failures**; opt-in live cases
  were not enabled.

Static analysis **40938754611**, exact source
**3aacad63d2ed7c4d502ee37860c6b50c49790db8**, completed all five jobs.
Its actual rendered report has zero Actions/C#/PowerShell findings and the
same two Python warnings: `py/clear-text-storage-sensitive-data` in
`tests/Interop/live_auto_node.py:100` and `py/weak-crypto-key` in the
upstream-compatible 1024-bit vector generator at
`tests/DotReticulum.Applications.Tests/GenerateRnodeconfSigningVectors.py:21`.
Successful scan execution is not a zero-finding claim.

### Automatic endpoint watchdog expiry and fresh-session recovery

Added a bounded Windows managed integration case,
`ManagedAndPinnedEndpointsAutomaticallyTimeOutAfterRouterCrashAndRecoverWithFreshLinks`,
using a .NET endpoint, surviving .NET transport, the existing **20 ms/frame**
capacity-one bridge, an actual pinned Python transport and a Python endpoint.
Both initiator roles establish three-hop links and prove exact DATA receipt in
both directions before the actual Python router is force-killed.

The .NET endpoint's local router connection stays present. A first exploratory
topology placed the endpoint directly on the removed interface and correctly
triggered immediate local link teardown (`TransportRouter.cs:234-252`), not
watchdog expiry. The final routed topology tests the intended remote outage.
The failed post-crash send must obtain no proof or peer receipt. Without
issuing `close` at either endpoint, both must report **TIMEOUT**, the upstream
callback must identify that exact old link, the .NET link must leave its manager,
and its closure callback must occur once. The surviving router remains running.

The deadline derives from the actual endpoints' RTT/stale intervals plus the
unchanged timeout factor/grace and two default five-second upstream polling
intervals (`RNS/Link.py:745-777,795-797`, pin
**a29bfb51c468945c96a1ee22cae0114e993e2391**). The initial four-minute ceiling
was later shown to reject a valid loaded-runner adaptive keepalive; the
source-derived maximum correction is recorded separately. No reference
timer, production policy, MTU or protocol is modified. The thin Python callback
adapter adds only the actual `link_id` to its closure observation.

Recovery initially exposed fixture ordering: router-side readiness did not mean
the surviving leaf had reconnected, and cached paths did not establish new
route propagation. The final case observes leaf TCP online, new intermediate
route availability and a newly received announce before either initiator role
opens its distinct new link. It then proves exact DATA both ways. An exploratory
cleanup also deleted state before the separately owned endpoint had persisted;
the endpoint now explicitly disposes before its owned directory is removed.
These failures were not accepted as passing recovery.

Final local validation, verified actual upstream imports:

- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~ManagedAndPinnedEndpointsAutomaticallyTimeOutAfterRouterCrashAndRecoverWithFreshLinks' --logger trx --results-directory TestResults\mesh-automatic-timeout-discovered -- xUnit.MaxParallelThreads=1`:
  **1 passed / zero skips / 1m28s**, exercising both initiator roles.
- `DOTRETICULUM_LIVE=1 dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests' --logger trx --results-directory TestResults\mesh-automatic-timeout-and-reference-final -- xUnit.MaxParallelThreads=1`:
  **6 passed / zero skips / 3m57s**, including measured-window recovery and the
  separately negative all-upstream cold-window control. The final automatic
  case observed RTT **0.06476690014824271 / 0.06319093704223633** for the two
  .NET roles, source-derived close budgets **42 / 42 seconds**, matched TIMEOUT
  callbacks at both endpoints and distinct proved recovered links.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\mesh-automatic-timeout-full-final -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 87 explicit skips / zero failures**.

This adds acceptance tests and observation metadata, not production behavior.
No redundant production coverage or native publish is claimed. Cross-platform
execution of the new automatic case and impaired published-native endpoints
remain unverified. It is integration evidence, not a shared Compatibility claim
or active-link resumption.

### Completed three-iteration mesh evidence before the fixture fixes

Run **40929696634**, exact source **df2b80b444543d7e563b0c3a01081f9001a64e29**,
completed with **all four selected cases passing in all three iterations on
Linux, Windows and macOS**: twelve case executions per platform, no extended
failures/skips. Actual uploaded TRX and the rendered summary were inspected;
the renderer preserved each `iteration-1/2/3` row with **4 / 0 / 0** in all
three platform columns. These are four distinct integration cases, not 36
different capabilities or shared Compatibility cases.

All nine measured-window recovery executions retained **20 initial parts
passed / 15 dropped / 15 retried**, four TCP sessions, both router restarts and
the interrupted old/fresh-link assertions. Actual capacity-one pressure counts
were Linux **193/216/194**, Windows **213/202/215** and macOS **199/201/203**.
The all-upstream cold-window case independently retained its healthy zero-delay
control and its explicit negative 120 ms result in each iteration; it is not
passing cold-window recovery. Native persisted-tunnel recovery and the original
mixed-router path/datagram/link case also passed in every iteration.

The workflow **still failed overall** because its earlier-source macOS Unit
suite hit the controlled discovery announcement-before-acceptance race. Both
renderers passed while preserving that real Unit failure. The later
**9de09d3** fixture correction is under full interop **40934315953**; no passing
overall result is inferred from the green extended rows.
Static analysis **40934316808**, exact source 9de09d3, completed all five jobs;
its actual rendered report still has zero Actions/C#/PowerShell findings and
the same two Python warnings recorded below.

### Hosted Darwin acceptance and scheduling-independent fixture assertions

Inspected the actual uploaded macOS and Windows TRX from full interop
**40926579478**, exact source **f5688026ed42457af486114f41c328f35dc61746**.
All **29 rnsh cases passed on macOS ARM64**, including all pipe/terminal
combinations, the delayed-first-read regression, controlling TTY/resize/literal
argv, interrupt delivery and subsequent session, process-group teardown, mode
restoration, first-window change during handshake, the published-native terminal
initiator and both actual pinned Python roles. This accepts the bounded Darwin
milestone, not upstream interactive-UI or installation parity. The observed
upstream EOF race remains explicitly negative.

The overall run **failed**, not just a renderer: macOS had the old cold-window
resource failure and two unit fixture failures; Windows failed the native Git
host's blocked-peer assertion. All three Builds, all four Compatibility
candidates, Linux Test and both renderers passed. The resource diagnostics
reported an active receiver, one received / three outstanding parts, no measured
data rate and zero repaired drops; this run predates the explicit measured-window
scenario and independent negative reference control in **df2b80b**.

Corrected the three scheduling assumptions without changing production code:

- The controlled discovery fixture now waits for the server to accept its child
  before sending its one-shot announcement. A completed client connect does not
  imply server acceptance; the failed run counted the attempted announcement but
  sent zero bearer bytes to the eventual child.
- The busy-observer initialization test uses the existing fixed protocol clock,
  retaining real bounded waits and the delivered-proof/identity/payload/error
  assertions. It exercises initialization independent of saturated observers,
  not whether a sub-millisecond loopback RTT's receipt deadline survives runner
  scheduling. Production RTT-derived deadlines are unchanged.
- The native Git negative case still requires the actual blocked-peer diagnostic
  and continued service for another peer. It now also accepts the exact
  **status 3 / `Not found` / no metadata** denial when that response wins the race
  with the asynchronous disconnect observer. It does not accept a successful
  listing, arbitrary error status or unexplained timeout. Both
  `RngitRepositoryStore.Permissions.cs:144-151` and pinned
  `RNS/Utilities/rngit/server.py:2310-2312,2870-2888` enforce this repository
  denial independently of the notification. The failed historical assertion did
  not retain its response bytes, so their exact value is not retroactively
  claimed.

Validation:

- Clean resume baseline:
  `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  **zero warnings/errors**; ordinary serialized suite **1854 passed / 86 skips**,
  `TestResults\resume-f568-hosted-baseline`.
- Verified reference and unchanged strict native binary from the f568802
  milestone, `DOTRETICULUM_LIVE=1`,
  `dotnet test DotReticulum.slnx -c Release --nologo -v quiet --filter 'FullyQualifiedName~Link_ResponderInitializesBeforeFirstDataEvenWhenObserversAreBusy|FullyQualifiedName~ControlledHdlcAutoconnectAppliesIfacAndDiscoveredRoutingOverrides|FullyQualifiedName~PublishedHostServesRepositoryCollaborationTemplatesMediaAndDownloadsAcrossNativeLinks' --logger trx --results-directory TestResults\hosted-ordering-fixtures-focused -- xUnit.MaxParallelThreads=1`:
  **4 passed / zero skips**, including actual native hosting with the pinned
  Python client. Production source is unchanged; no redundant publish/coverage
  collection is claimed.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\hosted-ordering-fixtures-full -- xUnit.MaxParallelThreads=1`:
  **1854 passed / 86 explicit skips / zero failures**.
- Inspected actual rendered Static analysis summaries from **40926400969**
  (f568802) and **40929610785** (df2b80b): all five jobs passed, zero
  Actions/C#/PowerShell findings, and the same **two Python warnings**:
  `py/clear-text-storage-sensitive-data` at `tests/Interop/live_auto_node.py:100`
  and `py/weak-crypto-key` at
  `tests/DotReticulum.Applications.Tests/GenerateRnodeconfSigningVectors.py:21`.
  Neither warning was suppressed; scan success does not mean zero findings.

The exact three-iteration mesh run **40929696634**, source **df2b80b**, was
in progress at this milestone and subsequently completed as detailed above.
It is not evidence for the later fixture fixes.

### Independent cold-window reference failure and explicit measured-window recovery

Executed a real **all-upstream** control: two pinned Python transports, two
Python leaves and the existing owned C# impairment bridge, with no .NET
forwarder and no upstream algorithm/timer/MTU patch. The thin transport adapter
now supports a server-only root by omitting its optional client configuration;
existing server/client and client-only roles are unchanged. The source remains
**a29bfb51c468945c96a1ee22cae0114e993e2391**, with verified actual imports.
Actual imported Windows `RNS\Resource.py` SHA-256:
**9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699**.

Both control cases negotiate **16384-byte MTU**, prove an actual DATA receipt,
then drop three initial resource-window parts. Without added latency, the
**220019-byte** resource completes with the exact peer hash and **3/3**
loss/retry. At **120 ms/frame**, both resource callbacks report **FAILED (7)**:
receiver still **ACTIVE (2)**, **one received / three outstanding**, **16 retries
left**, **zero retries observed**, SDU **16348**, EIFR
**6524.248129251539 bit/s**, RTT **0.25014376640319824**. Actual upstream DEBUG
reports **Resource timed out waiting for part requests**. Receiver's earliest
first repair is about **120 seconds** after its last part, but the sender
cancels after about **102 seconds**, matching its actual **0.25481247901916504**
resource RTT and reference sender budget. This establishes a pinned reference
limitation independent of .NET forwarding; it does not prove that it is the
sole cause of earlier macOS link timeouts.

The initial exploratory **80 ms/frame** control actually **recovered** with
three retries, correctly failing the provisional negative assertion. The
120 ms control was chosen from the source timing inequality, not by increasing
a test wait or changing a reference timer. `Resource.py:561-571` derives cold
EIFR from establishment cost / RTT; `601-612` uses that estimate for receiver
repair, while `642-649` independently bounds sender wait. Throughput is only
measured after a complete initial window (`842-940`).

Added a clearly named **negative reference integration control**, not a shared
Compatibility claim or passing recovery result. It requires the healthy
zero-latency control, both actual failed callbacks, the sender's specific
diagnostic, an active receiver, exact cold-window state and the observed
repair/cancellation inequality. No unexplained timeout or alternate unexpected
outcome passes. Ordinary non-live execution explicitly skips it.

The strict mixed recovery case now explicitly injects loss **after each first
complete four-part window**. This changes its bounded acceptance to
**measured-window recovery**, not cold recovery; the latter remains unchecked
in TODO and explicitly negative in the mesh documentation. All five resources
still require exact bytes/hash/status, exactly one conclusion and **15/15**
distinct dropped/retried parts. It additionally requires exactly **20 passed
initial parts**, retaining capacity-one backpressure, **20 ms/frame** latency,
both router restarts and the interrupted active-link assertions. No loss count,
MTU, production protocol, retry policy or timeout is relaxed. A focused fixture
test checks that initial-window passes do not consume the distinct-loss budget.

Final validation:

- Verified-reference `DOTRETICULUM_LIVE=1`,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~Parity8ResourceLossFixtureTests|FullyQualifiedName~LiveMixedTransportTests' --results-directory TestResults\reference-resource-boundaries-final --logger trx --logger 'console;verbosity=minimal' -- xUnit.MaxParallelThreads=1`: **5 passed / zero skips**, **2m38s**, including the explicitly negative reference control.
- Actual measured-window recovery: **42.42s**, **15 dropped / 15 retried**,
  **20 initial parts passed**, **210 backpressure waits**, **four TCP sessions**;
  surviving leaves, both persisted-identity router restarts, forced Python crash,
  interrupted old session and distinct recovered session all exercised.
- Re-executed the final negative control, including its specific sender-log
  assertion: **1/1 passed / no skips**, **1m51s**,
  `TestResults\reference-resource-negative-assertions-final`.
- `DOTRETICULUM_LIVE=0 dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --logger trx --results-directory TestResults\reference-resource-boundaries-full-final -- xUnit.MaxParallelThreads=1`: **1854 passed / 86 explicit skips / zero failures**.

This slice changes tests/adapters and truthful acceptance boundaries, not
production code. The preceding strict native/coverage gates remain the evidence
for unchanged production source; no redundant native publication is claimed.
Full interop **40926579478** tested earlier **f568802**, without this later
measured-window/negative-control separation, and subsequently failed as detailed
above. Cross-platform acceptance of the measured-window cases, negative control
and repeated mesh reporting was subsequently verified in 40929696634, as detailed
above; its unrelated Unit failure prevents an overall passing-workflow claim.

### Darwin exit draining and the first rnsh window change

Full interop **40918584672**, exact source **291bc7d**, completed **failed**.
All Builds, all four Compatibility candidates, Windows/Linux Test and both
renderers passed. Actual macOS TRX passed the three original Darwin-specific
cases, mode restoration, redirected exit, the previously empty mixed stdout-pipe
/ terminal-stderr case, native terminal initiation and the deterministic Git
deadline regression. The delayed-first-read and stdin-only terminal cases
instead timed out awaiting child exit. The live shell also printed its initial
size twice, losing the requested resize.

Primary XNU **f6217f891ac0bb64f3d375211650a4c1ff8ca1ea**,
`bsd/kern/kern_exit.c:2286-2336`, explicitly calls `ttywait` on a session leader's
controlling terminal before revoking it. `tty.c:1745-1796` waits for queued output
to drain. Retaining a replica alone cannot support waiting for exit before the
first read. Darwin terminal output now has an immediately started owned reader
with a bounded **16 x 4096-byte** queue; full queues apply backpressure. When only
stdin uses the terminal, unused primary output is drained without misreporting
it as either redirected output stream. Cancellation joins the pump, and read
failures propagate through the output or owned-drain disposal. Linux and Windows
terminal backends are unchanged. The actual Darwin correction was pending at
this milestone and was subsequently verified by all 29 rnsh cases in macOS run
40926579478, as detailed above; it was not accepted from Windows evidence.

The lost resize has a separate source-grounded cause: after the handshake,
`PumpWindowAsync` sampled the *current* size as its baseline rather than the
dimensions actually sent in `RnshExecute`. A change during that handshake was
then invisible. Its first changed size now sends promptly against the original
command dimensions; subsequent updates retain upstream's **RTT x 25** rate
limit (`rnsh/initiator.py:437-444`, where the first `last_winch` starts at zero).
No wire codec or reference shim changes. A real linked-session regression
requires the first changed WindowSize message. Buffered-reader tests cover
exact unread bytes, bounded backpressure, cancellation while full or awaiting
input, and visible reader/unused-drain failures.

Final local gates:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: **zero warnings/errors**.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-darwin-buffer-window-final-20261029 --logger trx -- xUnit.MaxParallelThreads=1`: **1853 passed / 85 explicit skips / zero failures**, live disabled.
- `tests\Get-CoverageSummary.ps1 TestResults\coverage-darwin-buffer-window-final-20261029`: Applications **76.69% / 63.51%**, Core **96.89% / 90.85%**, Crypto **94.83% / 94.00%**, Interfaces **82.19% / 72.99%**, Transport **91.22% / 79.26%** line/branch coverage; deduplicated overall **82.29% / 69.20%**.
- Strict Windows NativeAOT publish with `ILLinkTreatWarningsAsErrors=true` and `IlcTreatWarningsAsErrors=true`, then published `--help`: passed. `artifacts\native-darwin-buffer-window\DotReticulum.Applications.exe` SHA-256 **bc2da1065b7a76fd80bd75c0fe4b4af6e665b477725a01abe91bc5f69b694576**.
- `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~RnshLiveTests|FullyQualifiedName~RnshNativeTests|FullyQualifiedName~NativeResourceTests' --logger 'console;verbosity=normal' --results-directory TestResults\darwin-buffer-window-live --logger trx -- xUnit.MaxParallelThreads=1`: **3/3 passed**, no skips; fresh published-native terminal initiation, encrypted/compressed multi-megabyte resource and actual pinned Linux/WSL Python shell roles, including resize. Reference imports verified; pin unchanged.

The initial parallel baseline had an unrelated concurrent Git-statistics failure;
its isolated rerun passed. The first parallel coverage run then hit an unrelated
Windows file-sharing cleanup failure in `RnodeconfFirmwareTests`. Its focused
rerun and both subsequent complete serialized suites passed, without altering
those tests or assertions. Final coverage above is from the final source.

The same hosted interop still failed an impaired resource after .NET-router
restart: **9 dropped / 6 retried** total, three unrepaired first-window parts,
one received part, receiver EIFR **6710.629817095229 bit/s**, SDU **16348**,
resource RTT **0.2431962490081787**, **16 retries left**, then actual upstream
link timeout. That implies roughly **117 seconds** before the receiver's first
repair under `Resource.py:601-612`; it is evidence for further upstream-only
investigation, not a confirmed root cause or resource fix.

Static analysis **40918584135** completed all five jobs successfully. Its actual
rendered report has zero Actions/C#/PowerShell findings and **two Python
warnings**: clear-text fixture configuration at `live_auto_node.py:100` and
the upstream-format 1024-bit RSA fixture generator at
`GenerateRnodeconfSigningVectors.py:21`. Successful scan execution is not
reported as zero findings.

### Repeated-mesh reporting preserves iteration identity and actual failures

Repeated mesh **40915647175**, exact source **52dc6f0**, completed **failed**.
All Builds, all four Compatibility candidates, the Compatibility renderer and
Windows / Linux Test passed. Actual uploaded extended TRX has **3/3 tests in
each of three iterations** on Windows and Linux; macOS has **2 passed / 1 failed
in every iteration**. The impaired resource failed before its first repair
in iterations 1/3 and after Python-router restart in iteration 2
(**12 dropped / 9 retried** at failure). No reference-side root cause or resource
fix is claimed by this reporting change.

The final Test renderer also rejected legitimate repeated execution as
**Duplicate extended row DotReticulum.Applications.Tests for linux-x64**:
the exporter labeled every TRX with only its assembly, discarding iteration
identity. Added explicit **-IncludeParentDirectory** to `Get-TestSummary.ps1`
and both extended workflow callers. Result labels and failed test names now
include their containing **iteration-N** directory. Existing callers, positional
parameters, suite counts and separate Unit/Integration coverage stay unchanged.
The renderer's rejection of genuinely duplicate rows is preserved, not relaxed.

`tests\Invoke-PesterTests.ps1 -ToolingOnly`:
**61 passed / 2 explicit optional-reference skips / zero failures**.
The new real-process case covers separate iteration counts, a retained failed
iteration, hardware skip details and rejection of a repeated row within one
iteration. `tools\Invoke-PowerShellAnalysis.ps1`: **65 tracked files / zero
findings**. No production build is needed for this tooling-only slice.

Replayed **actual downloaded Linux/macOS/Windows** artifacts through
`Get-TestSummary.ps1 -Path <downloaded-extended-directory> -IncludeParentDirectory`
and the unchanged strict `Get-TestPlatformSummary.ps1`. The latter now succeeds
and prints three independent rows, each retaining **3/0/0 Linux, 2/1/0 macOS,
3/0/0 Windows**, plus all three macOS failures with their iteration names.
Owned replay:
`TestResults\mesh-summary-replay-52dc6f0\rendered-summary.txt`.
This validates reporting, not a passing mesh: both workflow TODOs remain open.

Inspected the actual four completed Compatibility job logs from that run:
**T057 passed** on Linux (**703 ms**), macOS (**1.59 s**), Windows (**975 ms**)
and the actual Linux upstream-to-upstream baseline (**925 ms**).
Read-only discovery confirms **40 distinct tests / 17 distinct mapped claims**.
README and the bounded Channel ledger now reflect that hosted evidence.
Current full interop **40918584672** tests earlier **291bc7d**, not this later
report grouping correction.

### macOS residuals: retained PTY output, deterministic Git deadlines and resource diagnostics

Full interop **40911880526**, exact source **0598977**, completed **failed**.
All Builds, all four Compatibility candidates, both summary jobs and Windows /
Linux Test passed. Actual downloaded macOS TRX proves all three original
Darwin-specific cases, terminal-mode restoration with the documented PENDIN
allowance, all-pipe exit status, both actual Python process roles and the
previously failing narrow-MTU impaired resource case passed.
Remaining macOS failures were:

- Mixed pipe stdin/stdout with terminal stderr returned empty stderr rather
  than the actual child's marker.
- The nonexistent Git destination correctly failed but reported **Path not
  found** rather than the elapsed timeout; the full captured diagnostic now
  exposes that race.
- Initial impaired resource failed on both actual upstream endpoints with
  **3 dropped / 0 retried** parts; the receiver remained on an active link,
  RTT **0.2865941524505615**, **16 retries left**, **65 parts**. This failure's
  root cause is **not established or claimed fixed**.

Primary XNU **f6217f891ac0bb64f3d375211650a4c1ff8ca1ea**,
`bsd/kern/tty_dev.c:325-361`, closes the line discipline/backing TTY and flushes
on the final replica close. The parent previously released its initial replica
at the end of spawn setup; a fast child could already have exited before the
caller's first output read. The parent now retains that non-controlling replica.
Only after the child exits and the terminal reader observes an empty queue is
it released; disposal also releases it. Native poll/read still supplies EOF,
so surviving descendants retain their normal output lifetime. A new actual
Darwin regression deliberately waits for process exit **before its first read**,
for merged and independently redirected stdout. It explicitly skips elsewhere;
the corrected Darwin branch still needs actual macOS acceptance.

Git path/link establishment now measures its consumed stage budget with a
monotonic clock and reports an elapsed **timeout expired** explicitly when no
link is obtained after that budget. This does not depend on which outer
cancellation timer gets scheduled first. A deterministic no-interface reference-
free case consumes a 20-ms path budget while its independent 5-second guard
remains uncancelled; no file/clone success is fabricated.

The thin actual-reference adapter now records resource counters/timers, window,
request-response rate, EIFR, outstanding/received parts and watchdog state in
failure observations and sender conclusions. The Parity 8 leaf selects ordinary
upstream **DEBUG** logging to expose actual resource cancellation/retry reasons.
No reference algorithm, timer, deadline, loss budget or success assertion changes.
The source pin remains **a29bfb51c468945c96a1ee22cae0114e993e2391**.

Local focused Applications
`RnshTests|RnshDarwinTests|RngitTests|Parity8CompatibilityLiveTests`, verified
Windows Python and `DOTRETICULUM_LIVE=1`, serial execution:
**36 passed / 4 explicit Darwin skips / zero failures**, **3m25s**, including
the actual active-link router crash and unchanged **15 drops / 15 retries**.
Full warning-free Release build and fresh full managed coverage:
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-darwin-drain-timeout-20261029 --logger trx`:
**1,851 passed / 85 explicit skips**, live modes disabled.
`tests\Get-CoverageSummary.ps1 TestResults\coverage-darwin-drain-timeout-20261029`:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 76.71% | 63.59% |
| Core | 96.89% | 90.93% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 82.14% | 73.08% |
| Transport | 91.38% | 79.69% |
| **Overall** | **82.34%** | **69.35%** |

Fresh strict Windows NativeAOT publish with both ILLink/ILC warnings as errors,
native help, actual native terminal and encrypted compressed multi-megabyte
resource execution: **2/2 passed**, no skips.
`artifacts\native-darwin-drain-timeout\DotReticulum.Applications.exe` SHA-256:
**fb302ef4179142d0511d604ddf36dc7e1bde93c49b2c401962994199acf53663**.
These results do not prove the pending Darwin drain branch or resolve the
macOS impaired-resource failure. Repeated mesh **40915647175** tests earlier
**52dc6f0**, not these later production corrections/diagnostics.

### Interrupted active-link crash and explicit session recovery

Extended the existing mixed three-hop actual-upstream transport case to kill
the Python router while its endpoints retain their already proved link, instead
of closing it before the crash. Both directions first validate actual DATA
proofs and both endpoint APIs report **ACTIVE (2)**. After the force kill, the
surviving leaf observes its bearer offline, the .NET TCP child count falls to
one and the .NET transit link table becomes empty. A further attempted send on
the old link gets **no validated delivery proof** and the other leaf's actual
packet-callback count remains unchanged. This negative result follows an
observed real path and forced bearer outage, not an unexplained timeout.

Both surviving endpoint APIs then explicitly tear down their old links and
report **CLOSED (4)**. Pinned `Link.py:662-672` defines that teardown and fresh
keys for new links. After the real Python router restarts with its persisted
transport identity, the existing discovery/receipt/resource acceptance runs
over an asserted **different link ID**. The unchanged actual impairment still
requires **15 distinct resource-part drops / 15 first retries**, bounded
capacity-one backpressure, exact hashes and bidirectional payloads.
No reference timer, API or source shim is introduced. This is managed .NET
transport integration with actual upstream leaves/router, **not** automatic
endpoint timeout, cryptographic-session resume or native transport acceptance.

Initial exploration unnecessarily created another link immediately after
closing the previous session and encountered two retained forwarding entries.
The existing exact-one assertion was kept: the final case instead retains the
already exercised session through the crash, directly establishing the intended
active-link boundary without changing production retention policy.

With actual import-verified RNS **a29bfb51c468945c96a1ee22cae0114e993e2391**:
`DOTRETICULUM_LIVE=1` plus verified Windows reference variables and
`dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests' --logger trx --results-directory TestResults\active-link-crash-reuse-20261029 -- xUnit.MaxParallelThreads=1`:
**1/1 passed / zero skips / 3m05s**.
Final `dotnet build DotReticulum.slnx -c Release --nologo -v quiet` and
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
**zero warnings/errors; 1,850 passed / 84 explicit skips**.
Production code and its previously measured coverage/native artifact are
unchanged. Cross-platform repeated-mesh acceptance of this extension remains
pending; earlier hosted runs do not test it.

### Shared executable Channel stream acceptance

Added **T057 / C009** without changing its verbatim upstream claim or reference
pin. The exact same Pester scenario drives actual candidate **rnsh** and an
actual pinned upstream **Channel** peer, using the existing upstream
`rnsh/protocol.py:100-107` subclass of `Buffer.StreamDataMessage`
(`Buffer.py:44-110`). The peer uses reference message registration, Channel
readiness/sending, unpacking and stream EOF; it does not reimplement envelopes,
compression, retries or encryption. It records received bytes and callback
observations; PowerShell owns setup, bounded processes and assertions.
The peer executes **no commands** and does not substitute a subprocess backend
or claim actual rnsh listener/process acceptance.

Each direction sends a distinct **87,415-byte ASCII transcript**, including
fresh random base64 content and ordered endpoint markers. Acceptance compares
both persisted transcripts exactly, requires multiple actual Channel messages,
observes stdin EOF and actual command-message receipt, requires both processes
to exit successfully and waits for actual link closure. This is bounded
Channel/Buffer message-codec acceptance, **not** general Buffer reader/writer,
arbitrary binary stdout, impairment/recovery or sustained-soak acceptance.
The actual CLI wrapper lazily imports the upstream rnsh entry point only when
selected, preserving Windows support for the other reference utilities.
Windows Upstream mode explicitly skips this POSIX-only CLI; hosted Upstream
continues to execute on Linux. Native Windows does not require WSL or POSIX.

Local acceptance with unchanged import-verified RNS
**a29bfb51c468945c96a1ee22cae0114e993e2391**:

- Actual published Windows candidate
  `artifacts\native-hosted-corrections\DotReticulum.Applications.exe`
  (**f8816a7d417d08a9096732615f026d7a6f29dd0338a5c1a2f104eca7305f8907**):
  focused Pester **1/1 passed**, then the complete
  `tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application artifacts\native-hosted-corrections\DotReticulum.Applications.exe -Python <verified-Windows-python> -Upstream <verified-Windows-checkout> -Implementation DotReticulum -OutputDirectory <fresh-owned-directory>`:
  **39 passed / 1 explicit hosted-retrieval skip / zero failed**, **86 seconds**.
  Discovery now contains **40 distinct cases**; no added scenario switch or
  duplicate catalog of test definitions.
- Actual Linux upstream CLI and actual Linux upstream peer under WSL, with the
  existing verified LF checkout/venv, the same test file/inputs/assertions:
  **1/1 passed, zero skips**, **5 seconds**. PowerShell was absent in WSL,
  so provisioned an owned portable official **7.6.9** archive after the explicit
  missing-tool failure; SHA-256 verified against the publisher's downloaded
  `hashes.sha256` (**1da6d6d6035d651f20e70ddee7c4c15ac6ab8b9fb40ee92240d91263f28a884b**).
  Existing portable Pester **6.2.0** was reused; no global installation.
  The first upstream execution exposed an asynchronous ordinary configuration
  creation notice inside otherwise correct stdout. Both candidates now receive
  precreated owned shell state, matching normal configured use. No filtering,
  weakened byte comparison or upstream shim.
- Resume baseline `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`
  and `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  **zero warnings/errors; 1,850 passed / 84 explicit skips**.
  Production code is unchanged; the existing exercised native artifact is reused.
- Final `tools\Invoke-PowerShellAnalysis.ps1`: **65 tracked files / zero findings**.
  `tests\Invoke-PesterTests.ps1 -ToolingOnly`: **60 passed / 2 explicit
  optional-reference skips / zero failures**. Final focused Windows native
  execution with precreated state also passed **1/1**, no skips.

Subsequent hosted run **40915647175**, exact source **52dc6f0**, verified this
case on all four candidates. Its separate impaired-mesh/reporting failures
remain recorded above; the aggregate workflow did not pass.

### Hosted residual failures: bounded impairment model and Darwin pipe status

Completed full interop **40902040408**, source **68c7b39**, had all three Builds,
all four Compatibility candidates, both summary jobs and Linux Test pass.
Downloaded and validated actual reports using
`tests\Compatibility\New-CompatibilitySummary.ps1 -ReportsDirectory TestResults\hosted-interop-68c7b39-compatibility -Commit 68c7b39967c1ec9e6be31b1c2e9fce1f0eaa7bc1`:
**39 distinct rows, all four candidates passed**, with exact tested-commit links.
The aggregate workflow nevertheless **failed**, not full hosted acceptance.

Actual macOS ARM64 TRX now proves the initial PTY correction: all **three**
`RnshDarwinTests` passed (controlling TTY/resize/literal argv, foreground interrupt,
owned process-group cancellation), along with seven mixed terminal/pipe
combinations, the ordinary TTY resize case and the actual published
`RnshNativeTests` initiator. The remaining managed failure was the terminal-mode
probe aborting with code **6**; the old assertion discarded its captured
diagnostic. Extended macOS failures were the crash-recovery resource returning
**7** instead of **6**, narrow-MTU forward resource timing out at **29.35%**
with its link still active, the all-pipe rnsh child returning **7** instead of
reference **0**, and a Git timeout assertion without its full error detail.
Windows failed one upgraded-MTU case before observing the initial announce.
No deadline, loss count, payload-size or successful-resource assertion was relaxed.

Corrections and bounded diagnostics:

- The impairment bridge now pipelines fixed one-way latency through a
  **64-frame bounded queue**, while separately serializing bytes at the
  configured bitrate. It formerly charged the full latency serially for every
  frame, unintentionally capping throughput irrespective of configured bitrate.
  The new real-socket burst regression requires **64 exact ordered frames**
  inside **3 seconds** at 100-ms one-way latency; the old model requires at least
  **6.4 seconds**. Both impairment bridges now disable Nagle on their owned TCP
  legs, as the real pinned TCP bearer does (`TCPInterface.py:149,242`), rather than
  adding uncontrolled platform-specific buffering to their explicit impairment.
  The crash bridge retains its **capacity-one** queue and **20-ms/frame** shaping,
  exact **15 distinct drops / 15 first retries**, actual router crash and
  unchanged resource/proof/identity assertions.
- The live-link fixture waits for the actual pinned server's registered online
  **Client on PyTCPServer** before issuing its first announce. It observes the
  existing upstream interface API, not a sleep or substituted path. The original
  **45-second** readiness budget remains bounded; failed setup disposes its peer.
- Darwin all-pipe commands now use the same owned Unix spawn/wait backend as
  Linux. Pinned `rnsh/process.py:639-645` masks raw `waitpid` status with **0xff**;
  ordinary `exit 7` therefore returns **0**. The previous Darwin-only
  `System.Diagnostics.Process` fallback returned normalized **7**.
- The terminal probe reports full captured output on failure. Primary XNU
  **f6217f891ac0bb64f3d375211650a4c1ff8ca1ea**,
  `bsd/kern/tty.c:1358-1391`, adds **PENDIN** when restoring canonical input and
  ORs that kernel state into restored flags; `bsd/sys/termios.h:259` defines it
  as **0x20000000**. The probe allows only this documented added state bit and
  prints actual values if encountered; every other mode byte remains exact.
  Production restoration does not change or flush input. The prior abort's
  actual byte difference remains unknown until this revised hosted probe runs.
- Resource callback diagnostics retain the actual upstream failure status even
  when a failed resource has no readable `data`; they expose actual link/resource
  RTT and retry state, not synthetic success. The Git timeout test retains the
  original strict condition but now prints the whole error if it fails.

Local validation, same unchanged RNS **a29bfb51c468945c96a1ee22cae0114e993e2391**
and verified Windows/WSL peers:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
**zero warnings/errors**.
Applications selected `RnshTests|RnshLiveTests|Parity4TransportLiveTests|Parity5TransferLiveTests|Parity8CompatibilityLiveTests|RngitTests.LiveRngit_Clone`
with `DOTRETICULUM_LIVE=1`, verified Python/WSL variables and
`-- xUnit.MaxParallelThreads=1`: **28/28 passed, no skips, 4m06s**.
Both narrow/upgraded-MTU impaired flows passed in **25s / 14s**; actual
Python-router crash/restart passed in **2m42s**. Results:
`TestResults\hosted-failure-corrections-20261029`.
Transport selected `Parity5UpgradedMtu_DataResourceAndChannel`:
**2/2 passed**, both initiator directions, **18s**; results:
`TestResults\hosted-failure-corrections-transport-20261029`.

Final full managed coverage command:
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-hosted-corrections-20261029 --logger trx`:
**1,850 passed / 84 explicit skips**, live modes disabled.
`tests\Get-CoverageSummary.ps1 TestResults\coverage-hosted-corrections-20261029`
deduplicated collectors/TRX copies:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 76.79% | 63.66% |
| Core | 96.89% | 90.93% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 82.18% | 73.02% |
| Transport | 91.36% | 79.49% |
| **Overall** | **82.39%** | **69.35%** |

Fresh strict Windows NativeAOT:
`dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -o artifacts\native-hosted-corrections -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v quiet`
with discoverable **vswhere** and **vcvars64.bat** in the same process:
**passed**, followed by native help and actual native terminal /
encrypted compressed multi-megabyte resource cases **2/2 passed**, no skips.
Binary SHA-256:
**f8816a7d417d08a9096732615f026d7a6f29dd0338a5c1a2f104eca7305f8907**.
These Windows results do not establish the new Darwin paths or resolve the
remaining hosted Git/resource failures. Those remain explicit pending rerun.

### Actual-router published-native I2P cold-start DATA and proofs

Added opt-in Applications integration case
`I2pNativeTests.PublishedProbeExchangesEncryptedDataAndValidatedProofsAcrossFreshProcesses`
and the owned topology runner's documented **-PublishedFeatureOnly** selector.
It requires an existing **DOTRETICULUM_NATIVE_EXE**, explicit network permission
and an owned IPv4 SAM endpoint; conflicting selections and missing candidates
fail before router launch. The runner explicitly supplies **I2P_SAM_ADDRESS**
to the test/native children. No Python implementation or alternate bearer is
used by this case.

Actual **Windows x64 / Java I2P 2.14.0 / SAM 3.1** execution passed **1/1,
zero skips, 63 seconds**. The unchanged native artifact
`artifacts\native-darwin-startup\DotReticulum.Applications.exe` has SHA-256
**7d2bfbd78e440aa0b31a584b73248302c3a3abd0351d0a5e7d5da95534b8efe2**.
Two separate native **rnprobe** processes with independently initialized
configuration/storage each encrypted **257-byte** DATA. The managed receiver
decrypted both on their respective real accepted **I2pPacketPeer** instances;
both native senders validated signed delivery proofs. Sole-I2P configurations
disable shared instances; no TCP/UDP fallback. The receiver emits a normal
signed announce after each actual stream connects, not a preseeded identity/path.
Both child processes have **110-second** deadlines and **64-KiB** combined output
bounds inside the unchanged **four-minute** test budget.

This is bounded **cold-start NativeAOT integration**, not a shared Compatibility
claim, Python interoperation, persisted candidate recovery, router restart,
public-tunnel or separate-router acceptance. Java default local-destination
delivery was explicitly enabled; three-hop client defaults, certificate
verification and transit-disabled router ownership remain unchanged.

Failed exploratory executions are retained as limitations, not relabeled passes:
an unannounced receiver timed out on the first cold path request; after adding
connected-peer announcements, candidate one passed but candidate two, reusing
candidate one's state, sent its single probe via a restored path and timed out
awaiting proof. The latter run did not print a new path request; sending before
asynchronous stream readiness is a source-consistent hypothesis, not a directly
observed timestamped cause. `TransportRouter.HasPath` checks its path
table and `I2pPacketPeer.SendAsync` discards offline writes, as documented in its
existing upstream-derived behavior. Pinned upstream
`RNS/Transport.py:407-441` restores matching cached paths and
`RNS/Utilities/rnprobe.py:45-155` performs discovery/probing; no new
upstream executable comparison established equivalent persisted-candidate
failure. No production readiness fix or changed retry policy is claimed.
The accepted case explicitly uses fresh storage for each process; persisted
published-candidate readiness stays unchecked in **TODO.md**.

Exact successful command:
`$env:DOTRETICULUM_NATIVE_EXE = (Resolve-Path artifacts\native-darwin-startup\DotReticulum.Applications.exe).Path; & tests\Interop\Invoke-I2pTopology.ps1 -JavaExecutable (Join-Path $PWD 'TestResults\i2p-tools-java-win\jdk-21.0.12.1+1-jre\bin\java.exe') -JavaRouterDirectory (Join-Path $PWD 'TestResults\i2p-tools-java-win\router') -AllowPublicNetwork -JavaLocalDestinationRouting -PublishedFeatureOnly -NoBuild -SamEndpoint 127.0.0.1:17657 -TestTimeoutSeconds 360 -KeepArtifacts`.
TRX: ignored owned directory
`TestResults\i2p-topology-25648b0f67cc4c04812aa77db8886efc\results\i2p-live.trx`.
Router/test processes were stopped by the coordinator. Retained raw router
state/logs remain private and are neither committed nor uploaded.

Validation:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
**zero warnings/errors**.
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --results-directory TestResults\published-i2p-managed-20261029 --logger trx`:
**1,849 passed, 84 explicit skips** without opt-in live/native prerequisites.
Repeated after the final cold-start implementation with
`--results-directory TestResults\published-i2p-managed-final-20261029`:
the same **1,849 passed / 84 skipped**, with a warning-free full build.
Focused Pester configuration combining `Coverage.Tests.ps1` and
`Parity6Interfaces.Tests.ps1`, filtering
`*rejects published I2P selection*` and `*keeps standalone script verbs*`:
**5 passed, zero failures/skips** (Pester **6.2.0**). Four separate script
processes rejected unauthorized, missing-candidate, nonexistent-candidate and
conflicting-selection inputs; the fifth verified discoverable parameter help.
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
**63 tracked PowerShell files, zero findings**.
No production changes: the previously exercised strict native publication was
reused; no redundant production coverage or native publish was performed.

### Source-grounded Darwin PTY initialization and stricter hosted-regression probes

The completed corrected mesh run **40894852888** was inspected rather than
assuming every previous failure persisted: Windows failed only the duplicate
resource-part fixture selection corrected by **72d066e**; macOS retained the
**13** PTY startup failures. The previous interop run **40890841836** additionally
had the PNG/WebP assumption, a nondeterministic EOF-race expectation and one
narrow-MTU forward-resource timeout.

The Darwin failure is grounded in Apple's XNU revision
**f6217f891ac0bb64f3d375211650a4c1ff8ca1ea**,
`bsd/kern/tty_dev.c` Git blob **9deb01fa73be0256dcd749008cc319f3c25516e4**:
`ptcopen` sets **TS_IOCTL_NOT_OK** at line **516**; `ptsopen` clears it at
**308**; `ptyioctl` disables extended master ioctls at **989-996** and returns
**ENOTTY** at **1156-1158** until a replica has opened. Our initial window ioctl
ran before any replica open. The parent now opens an owned **O_RDWR|O_NOCTTY**
replica first, holds it through child spawn, and closes it through existing
ownership cleanup. Child endpoints still open before acquiring their own
controlling terminal via the existing literal-argv bootstrap. Linux behavior,
the Darwin arm64 ioctl ABI, deadlines and fatal initialization errors remain
unchanged. Existing real-macOS tests fail without this correction; Windows
execution cannot establish the corrected Darwin behavior.

Native hosting now explicitly configures **[pages] media_conversion=no** for
its strict byte-exact PNG branch, instead of assuming no automatically available
encoder when `DOTRETICULUM_MEDIA_BACKEND` is absent. Its explicitly configured
FFmpeg branch retains **media_conversion=yes** and strict WebP validation.
Pin/source: **a29bfb51c468945c96a1ee22cae0114e993e2391**,
`RNS/Utilities/rngit/pages.py:161,176-177,1861`. No production conversion default
or format assertion was weakened.

The rnsh probe no longer mandates the reference's intermittent EOF defect on
every OS. It waits for real child readiness, then closes stdin and requires
exact child output **stdin-ready\neof:0\n**, proving a zero-byte read.
A normal response must return **0**. Its original **8-second** cancellation is
accepted only with a fresh observation correlated to the actual link:
child **not running**, listener **LSSTATE_RUNNING**, return code **null**.
Unexplained timeout, live child, missing bytes or another session's observation
fails. The adapter only exposes actual reference state; no upstream patch.
Reference race source: `rnsh/process.py:542-550,639-653`,
`rnsh/session.py:281-289`. Subsequent fresh-session recovery remains required.
The separate narrow-MTU failure is **not claimed fixed**: its unchanged
90-second resource wait now reports status, progress, segment/part/window,
RTT, link state and loss count on timeout.

Prepared an isolated **WSL Ubuntu / Python 3.14.4** environment using
`tests/Interop/reference-requirements.txt` and the existing verified fetched
objects. Linux Git correctly rejected the Windows CRLF checkout as modified;
an owned LF-only checkout was materialized at the same exact pin and passed
`git fsck --full --strict`, tracked-source and actual-import verification.
Missing LXMF was then installed explicitly from the pinned requirements.
Verified interpreter/source remain under the owned ignored
`TestResults\wsl-rnsh-reference-20261029`; original checkout was not changed and
the guard was not relaxed. Imported RNS **1.5.7**, LXMF **1.2.1**.

Local validation:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
**zero warnings/errors**.
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-darwin-startup-20261029 --logger trx`:
**1,849 passed, 83 explicit skips**, live modes disabled.
`pwsh -NoProfile -File tests\Get-CoverageSummary.ps1 TestResults\coverage-darwin-startup-20261029`
merged collected reports and their TRX copies by production source point:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 76.78% | 63.67% |
| Core | 96.89% | 90.93% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 82.32% | 73.05% |
| Transport | 91.40% | 79.59% |
| **Overall** | **82.42%** | **69.38%** |

`dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -o artifacts\native-darwin-startup -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v quiet`
passed under MSVC; native `--help` passed. Exercised executable SHA-256:
**7d2bfbd78e440aa0b31a584b73248302c3a3abd0351d0a5e7d5da95534b8efe2**.
With that exact binary, verified Windows Python and the isolated WSL rnsh peer:

```powershell
dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v normal --filter 'FullyQualifiedName~RnshLiveTests|FullyQualifiedName~RnshNativeTests|FullyQualifiedName~NativeResourceTests|FullyQualifiedName~RngitNativeHostingTests' --results-directory TestResults\darwin-regression-native-live-20261029 --logger trx -- xUnit.MaxParallelThreads=1
```

**4 passed, zero failed/skipped**, **38 seconds**: fresh native ConPTY shell,
encrypted/compressed multi-megabyte native resource, native Git-host
collaboration/pages/byte-exact media, and both actual POSIX reference rnsh roles
with controlled EOF, loss/retry, resize, identification/spawn/cancellation and
new-session recovery. This WSL execution positively observed the missing-return
callback race after exact EOF acknowledgement, not clean upstream EOF completion.
An earlier focused `RngitNativeHostingTests|Parity5TransferLiveTests` selection
passed **3/3**, zero skips, **3m9s**, with the previous unchanged Windows native
artifact; both negotiated-MTU mixed transfer modes passed locally.
macOS execution acceptance and the unexplained earlier macOS narrow-MTU
timeout remain pending hosted follow-through; no broader box is checked here.

### Forced actual Python-router crash/restart with surviving leaves and distinct-part loss

Extended the existing bounded mesh case rather than inflating shared claim/test
counts: **A(Python leaf) -- Python transport -- impaired TCP -- managed .NET
transport -- B(Python leaf)**. After the existing reconnect and persisted .NET
restart phases, C# forcibly kills only the owned Python-router process, awaits
its exit, observes A's real upstream interface go offline and the .NET server's
peer count fall **2 -> 1**, then starts a new Python process with the same owned
configuration/storage. Its actual persisted transport identity must match.
Surviving A/B processes are not restarted. Online/peer-count **1 -> 2** recovery,
fresh upstream-router path discovery and three-hop leaf paths precede a new
link, proved DATA and impaired resources in both directions.

Thin reference adapters only expose actual `interface.name/online` and
`RNS.Transport.identity.hash`; C# owns crash/restart, waits and assertions.
`PyLinkNode` retains its generated storage across the explicit crash/restart
but still owns final cleanup, bounded process/pump joins and idempotent disposal.
Its children no longer inherit Actions command/summary output paths.
Pin remains **a29bfb51c468945c96a1ee22cae0114e993e2391**:
`RNS/Transport.py:319-327`, `RNS/Interfaces/TCPInterface.py:271-300,410-435`.
No new reference shim or timer change; the existing both-endpoint **PROVE_ALL**
policy remains explicit. Links were closed before crashing: active-link
failure/resumption and alternate routes are not established.

An initial attempt reached real offline/online recovery but tried a link while
the restarted Python router still lacked B's path: the surviving leaves' cached
paths were not a readiness proxy. The case now explicitly requests and verifies
the fresh router's actual **two-hop** path to B before establishing that link.
No blind sleep or deadline increase was used.

Hosted macOS run **40890841836** additionally exposed a loss-fixture bug:
an early legitimate retransmission could arrive before three distinct parts
had been dropped. The fixture incorrectly consumed another loss credit and
threw `Loss schedule selected a duplicate resource part`. It now drops each
selected hash once, forwards retransmissions even while unused loss credits
remain, and counts the first retry per distinct lost part. Assertions are not
relaxed: five exercised transfers must drop/retry exactly **15 distinct parts**,
deliver matching sizes/SHA-256 once, and exercise capacity-one backpressure.
A focused non-live fixture regression is outside the scheduled live-class
selection and does not masquerade as Python or RF evidence.

Final validation:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
**zero warnings/errors**.
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --results-directory TestResults\mesh-loss-managed-final-20261029 --logger trx`:
**1,849 passed, 83 explicit skips**, live modes disabled.
With verified local Python/reference and `DOTRETICULUM_LIVE=1`:

```powershell
dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v quiet --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests' --results-directory TestResults\mesh-loss-crash-final-20261029 --logger trx -- xUnit.MaxParallelThreads=1
```

**1 passed, zero failed/skipped**, **2m44s**. Payloads remain incompressible
**2,200,017/2,200,019 bytes** initially, **220,019 bytes** after .NET restart,
and **220,017/220,019 bytes** after Python crash, with exact size/hash/proof
assertions and **20 ms/frame** latency. An earlier four-case regression selection
(`Parity8CompatibilityLiveTests|LiveMixedTransportTests|Parity4NativeTransportTests|NativeResourceTests`)
passed **4/4**, zero skips, **3m27s**, using
`artifacts\native-rnode-radio\DotReticulum.Applications.exe`.
That separately executes the freshly published native encrypted resource and
tunnel/path feature flows; it does not turn the managed Python-crash phase into
native bearer acceptance. No production code changed in this test milestone,
so no redundant coverage collection/native publication was performed.

An earlier full run had one unrelated `RngitRepositoryStoreTests.ActualUpstreamPrecedenceAndBlocklistDecisionsMatch`
failure. Its focused rerun and two subsequent full runs passed without changing
that test or implementation; the intermittent cause is not established.

### Actual receive-only RAK startup exposed and fixed an acknowledgement race

The authorized Windows RAK4631/SX1262 (nRF52, firmware **1.86**) initially
passed startup/live SF/CR changes/radio-off but failed its second serial open:
the reported parameters were correct and **state=0** was observed before the
final radio-on acknowledgement. `RNodeDevice` incorrectly completed configuration
on the first snapshot containing every field, including an intermediate off
state. Startup now clears detection-time echoes; both startup and live
configuration wait for **state=1** within the existing response deadline.
Missing acknowledgement still fails, and mismatched parameters are not accepted.
Failure diagnostics now include reported radio settings (no secrets).

Source contract: unchanged pin **a29bfb51c468945c96a1ee22cae0114e993e2391**,
`RNS/Interfaces/RNodeInterface.py:418-487,643-647,667-695,1190-1208`.
The reference validates the requested state after its serial settling interval;
.NET instead observes its asynchronous acknowledgement with a bounded wait.
No wire format changed and existing independently generated vectors still pass.
A focused simulator regression emits intermediate off followed by delayed on
for both startup and live reconfiguration; it does not simulate RF acceptance.

`Parity6RNodeHardwareTests` is now Integration rather than Unit. Port-only opt-in
still performs only identification; configuration needs the additional
`DOTRETICULUM_RNODE_RECEIVE_ONLY=1` and explicit
`DOTRETICULUM_RNODE_FREQUENCY=868100000`. With authorized port `COM8`:

```powershell
dotnet test tests\DotReticulum.Interfaces.Tests\DotReticulum.Interfaces.Tests.csproj -c Release --no-build --nologo -v normal --filter 'FullyQualifiedName~Parity6RNodeHardwareTests' --results-directory TestResults\rnode-radio-d10253636fa24ab396fae76fd4660277 --logger trx -- xUnit.MaxParallelThreads=1
```

**2 passed, zero failed/skipped**. Two serial sessions each verified device
frequency/BW/power/SF/CR/on echoes, changed SF **8 -> 9** and CR **6 -> 7**,
and awaited off acknowledgement before disposal/reopen. No packet send was
requested; 0 dBm is not zero RF power. Native `rnodeconf --eeprom-backup`
before/after confirmed all **200 bytes unchanged**; backups stay local.

Full `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
**zero warnings/errors**.
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-rnode-radio-20261029`:
**1,848 passed, 83 explicit skips**, live modes disabled.
`pwsh -NoProfile -File tests\Get-CoverageSummary.ps1 TestResults\coverage-rnode-radio-20261029`
merged five reports by production source point, without double counting:

| Project | Lines | Branches |
|---|---:|---:|
| Applications | 76.78% | 63.69% |
| Core | 96.89% | 90.93% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 82.21% | 73.05% |
| Transport | 91.36% | 79.49% |
| **Overall** | **82.39%** | **69.38%** |

`dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -o artifacts\native-rnode-radio -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v quiet`
passed under MSVC with `vswhere` on PATH, then native `--help` passed.
An initial publish without discoverable `vswhere` failed explicitly; fixing
the toolchain PATH resolved it, without source changes or warning suppression.
The exact new binary ran two separate `rnsd --config <owned-dir> --interactive`
processes against the real radio, with **share_instance=no, enable_transport=no,
outgoing=no** and the same radio settings. Both reached interface/status commands,
persisted actual runtime state and exited gracefully; post-native EEPROM
also remained byte-identical. Owned logs/configuration/results are under
`TestResults\rnode-radio-d10253636fa24ab396fae76fd4660277`.
This is native hardware-control execution, **not Python RF interoperability**.
Other frequencies, power settings, DATA/over-air delivery, unplug/replug,
Multi, BLE and other hardware/platforms remain unverified.

### Correct dispatch input conversion and explicit Windows POSIX-reference prerequisite

Initial mesh dispatch **40890846669** failed before creating jobs. Its caller
passed the manually supplied iteration value directly into a reusable numeric
input, unlike the working interop caller's literal number. The mesh caller
now uses `fromJSON` to convert both scheduled/default and dispatch values
explicitly; zero is retained for existing 1-10 validation, not silently replaced.
GitHub's dispatch API rejected a numeric JSON input directly (HTTP 422), and
`gh workflow run --json` requires string-valued inputs, so coercion belongs
at the caller boundary. Actionlint **1.7.12** accepts the corrected workflow;
corrective dispatch **40894852888** at **99f4470** created and ran all reusable
jobs, confirming the input-boundary fix. All three native Build jobs, all four
candidate Compatibility jobs and both summary jobs passed; Test/Linux passed.
Test/Windows and Test/macOS still failed, so workflow TODOs remain unchecked.

The full local scheduled live selection with actual pinned Python and native
Windows binary completed **55 passed, 6 explicit skips, 1 failed**:

```powershell
dotnet test DotReticulum.slnx -c Release --no-build -m:1 --nologo -v quiet --filter 'Category=Live' --results-directory TestResults\extended-local-interop-20261029 --logger trx -- xUnit.MaxParallelThreads=1
```

The failure was specifically upstream `rnsh/process.py:40` importing POSIX
`fcntl` in Windows Python (`ModuleNotFoundError`), not a wire failure.
`RnshLiveFact` now explicitly skips this one case on Windows when the required
`DOTRETICULUM_RNSH_WSL_PYTHON` environment is absent. Configured peers still
execute normally; invalid configured environments are not caught or defaulted.
The focused `FullyQualifiedName~RnshLiveTests` run with live enabled and the
verified Windows reference produced **one explicit POSIX-prerequisite skip**.
The remaining live cases were not needlessly rerun; this is not a full-green
Windows rnsh/Python-process claim. Linux/macOS still execute that reference case.
Updated stale CLI terminal help to include the implemented Darwin backend and
the actual Windows terminal-output merging boundary. Full Release/build/coverage
and native publication in the preceding hardware gate also included these changes.

Hosted **3078e5d** Static analysis **40890716711** passed. Normal Build and Test
**40890716875** built all three native artifacts, but macOS candidate retrieval
timed out after **900 seconds** with a discovered **11-hop** path and `/git/fetch`
at **9.1%**; dependent compatibility containers failed for unavailable reference.
This is retained as a failed external-network retrieval, not a macOS feature
or compatibility pass. The independent interop run **40890841836** passed all
three native Build and candidate Compatibility jobs at the same commit.
The interop run finished with all four **39-case** Compatibility candidates and
their renderer passing; actual rendered T055/T056 rows show **Passed** in all
four columns with test links pinned to **3078e5d**. Test/Linux passed, but
Test/Windows failed its POSIX-reference prerequisite and a media-test assumption
(expected PNG despite an automatically discovered encoder returning WebP).
Test/macOS exposed **13** terminal integration failures at `TIOCSWINSZ: ENOTTY`,
plus live loss-fixture duplication (fixed above), an rnsh EOF-outcome assumption
and a narrow-MTU transfer timeout. Those are retained failures, not hosted
acceptance; the remaining root causes require investigation. Both summaries
completed. No timeout increase, fake retrieval, fallback artifact or weakened
assertion was introduced.

### Scheduled opt-in live integration and repeated mixed meshes

Implemented `interop-test.yml` (Tuesday **06:00 UTC**, or manual) and
`e2e-mesh-test.yml` (Saturday **06:00 UTC**, or manual). Both call the same
Build and Test workflow through `workflow_call`; they do not duplicate native
publish/retrieval code, substitute a historical artifact or use Python to fetch
on the candidate's behalf. Each invocation builds its own three native artifacts,
validates their manifests and provisions the canonical same-run Linux
candidate-fetched reference bundle exactly as normal CI does.

Optional inputs leave normal pushes/PRs unchanged. Extended `interop` enables
the actual xUnit `Category=Live` selection. Extended `mesh` selects the existing
`Parity8CompatibilityLiveTests`, `LiveMixedTransportTests` and
`Parity4NativeTransportTests`: mixed real Python transports, bidirectional
multi-megabyte resource-part loss/retry, surviving-leaf reconnect/.NET restart,
and published-native tunnel/path restoration. These cases are **integration**,
not shared advertised-claim Compatibility. They do not establish Python-router
crash recovery, alternate routes, physical bearers or sustained Channel soak.

Mesh defaults to three repetitions, with integer **1-10** validation; interop
runs once. Each repetition must execute passing tests, and an earlier failure
stays failed even if a later iteration passes. Inputs enter PowerShell via
environment variables, not interpolated code. Extended jobs have a 60-minute
timeout versus normal Test's unchanged 30 minutes. Test children do not inherit
Actions command/summary output paths.

Extended raw results and their own `summary-extended.json` are retained in the
existing platform artifacts. The consolidated renderer adds an **Extended**
table only when those reports exist; Unit/Integration/Native counts and their
independent production coverage stay separate. Missing platforms remain
unavailable, not zero or passed. A focused reporting regression exercises
extended pass/skip counts alongside an unchanged failing Native report.

Local validation:
`TestResults\workflow-tools\actionlint-1.7.12\actionlint.exe -color .github\workflows\ci-build-test.yml .github\workflows\interop-test.yml .github\workflows\e2e-mesh-test.yml .github\workflows\stale.yml`
passed; `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
**60 passed, 2 explicit missing-reference skips**;
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
**63 tracked files, zero findings**.
With `DOTRETICULUM_LIVE=1`, the verified local Python/reference paths and
`DOTRETICULUM_NATIVE_EXE=artifacts\native-device-ble-darwin\DotReticulum.Applications.exe`:

```powershell
dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --nologo -v normal --filter 'FullyQualifiedName~Parity8CompatibilityLiveTests|FullyQualifiedName~LiveMixedTransportTests|FullyQualifiedName~Parity4NativeTransportTests' --results-directory TestResults\extended-local-mesh-20261029 --logger trx -- xUnit.MaxParallelThreads=1
```

**3 passed, zero failed/skipped**, **2.24 minutes**, with actual pinned Python
transport processes and the native Windows daemon for the tunnel case.
This is one local iteration, not the scheduled three-iteration soak.
Subsequent corrected-source workflow acceptance passed: full interop
**40961479281** and three-repeat mesh **40956389129**. Their workflow TODOs
are now checked, without relabeling these earlier local-only results.

### Shared heterogeneous UDP/TCP routing acceptance (T055/T056)

Added two genuine shared advertised-claim cases for existing quoted **C007**:
candidate **UDP** leaf, fixed pinned-upstream transport with UDP/TCP interfaces,
fixed pinned-upstream **TCP** rncp peer. The exact same scenarios, random
**3,211,282-byte** payload size, authorization and assertions run independently
with upstream and the published Windows native candidate. Configurations disable
shared instances and contain no static routes or direct candidate/reference
bearer. They assert exact persisted bytes, surviving peers/router and the
upstream link's observed **two-hop** distance for every resource.

`RoutingPeer.ps1` owns bounded local processes/configuration and reuses the
existing rncp helpers. The thin import-verified `reference_cli.py` now invokes
the actual upstream `rnsd.main`; the existing transfer observer additionally
exports `resource.link.expected_hops`. It does not implement routing or assert
the scenario in Python. Source: unchanged reference pin
**a29bfb51c468945c96a1ee22cae0114e993e2391**, `README.md:40`,
`RNS/Interfaces/UDPInterface.py:59-110`, and `RNS/Link.py:267-281,515-525`.
The existing disclosed Windows rncp copy-instead-of-move retention shim remains;
these cases do **not** establish unmodified Windows retention behavior.

A first **32,771-byte** upstream send exposed its existing Windows fast-transfer
metrics failure: `rncp.py:773-775` can observe zero elapsed time and divide by zero
after a completed transfer. The native sender did not show that failure.
The final bounded multi-megabyte cases retain strict exit, byte and hop
assertions and use no timing shim or added sleeps. They do **not** establish
small-file metric correctness, arbitrary carriers, alternate routes, loss,
radio interoperability or sustained mesh soak.

Direct Pester **5.7.1** execution of both cases for both candidates:
**4 passed, zero failed/skipped**; evidence
`TestResults\compatibility-routing-6f1bcf8a3e074c668262cee27ac9ea48\routing.xml`.
The normal complete runner then passed independently for each candidate:
**38 passed, 1 explicit hosted-retrieval skip, zero failures** apiece.
The inventory is now **39 distinct It cases**, with **17 quoted claims**
catalogued; not every catalogued claim has shared acceptance.

```powershell
pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application artifacts\native-device-ble-darwin\DotReticulum.Applications.exe -Python TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe -Upstream TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream -Implementation Both -OutputDirectory TestResults\compatibility-routing-full-20261029 -NoJobSummary
```

No new reference retrieval or Git baseline is claimed by this local reuse.
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1` analyzed
**63 tracked files, zero findings**; tooling discovery/reporting gates passed
**60 tests with 2 explicit missing-reference skips** in the adjacent build-fix
gate. Hosted Linux/macOS/native acceptance remains pending.

### Fix concurrent GitHub Actions version-variable exports

Hosted build **40887675136**, macOS job **123495856574**, compiled successfully
with **zero warnings/errors** but Actions then rejected its command file:
`Unable to process file command 'env' successfully`, `Invalid format '.7'`.
This was **not a Darwin C# compilation failure**. Parallel solution projects
each invoke Nerdbank.GitVersioning's default cloud-variable exporter against
the same Actions environment file; those values are unused by our workflows.

Disabled only `NBGV_SetCloudBuildVersionVars` in `Directory.Build.props`.
The pinned **3.10.94** package's `SetCloudBuildVersionVars` target explicitly
supports this switch. Assembly/package/version-height calculation remains
enabled, as do the existing artifact manifests. No version base, pin, tag
policy or `version.json` content changed.

An owned `GITHUB_ACTIONS=true`/`GITHUB_ENV`/`GITHUB_OUTPUT` reproduction previously
exported `GitAssemblyInformationalVersion`, `GitBuildVersion` and
`GitBuildVersionSimple`. After the fix, calculated assembly/file/informational
versions matched the pre-change values **byte-for-byte**, and the complete
parallel Release build left both sentinel command files untouched:
**zero warnings/errors**. This verifies the actual shared-output boundary
without writing any real runner command files.

Added a focused Pester regression using real `dotnet msbuild -t:GetBuildVersion`
with owned sentinel files, requiring valid matching assembly versions and no
Actions-file mutation. `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
**60 passed, 2 explicit missing-reference skips**.
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
**61 tracked files, zero findings**. Hosted rerun acceptance remains pending.

### Conservative inactivity-reminder automation

Added the roadmap's `stale.yml` as weekly Monday **05:30 UTC** reminders plus
manual dispatch. It marks inactive unassigned issues/non-draft PRs after
**90 days**, removes the label after activity and exempts assignments,
milestones, `pinned`, `security` and `never-stale`. It **never auto-closes work
or deletes branches**; manual runs default to preview-only. This avoids
discarding unresolved parity reports merely because hardware/platform access
has delayed them. CONTRIBUTING documents the policy.

The job has no checkout or untrusted code execution, ten-minute timeout,
30-operation budget, non-cancelling concurrency and only job-local
issues/pull-requests write permission. `actions/stale` **v11.0.0** is pinned to
verified release commit **4391f3da665fdf50b6810c1a66712fb9ba21aa93**.
Its published input definitions were checked, including the `-1` no-close and
debug-only behavior.

Validation:
`TestResults\workflow-tools\actionlint-1.7.12\actionlint.exe -color .github\workflows\stale.yml`
passed with no findings. The missing local linter was provisioned from the
Windows **actionlint v1.7.12** release; downloaded zip SHA-256
**6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9**
matched GitHub's release-asset digest. Tool/artifacts remain ignored.
Hosted manual preview **40887769855** on **8bbffcc** succeeded, with the log
confirming debug mode, no issue/PR mutation and no persisted debug state.
There were **no eligible issues**; this validates deployment/permissions and
non-mutating preview, not actual stale-label removal or reminder delivery.

### Actual Java I2P local-destination persisted-restart acceptance

The actual Java **I2P 2.14.0** router, not a SAM simulator, now demonstrates
bidirectional DATA/callbacks before and after a persisted accepting-bearer restart:
the destination key/address and transport identity remain unchanged and the
initiator survives. The production .NET bearer passed **1/1 in 65.8 seconds**;
an independent pinned-upstream-only control passed **1/1 in 95.7 seconds**.
These are managed contract-integration executions, **not published-NativeAOT or
shared advertised-claim Compatibility results**.

The distinction was an existing topology policy: Java's normal local-destination
delivery uses `i2cp.disableLoopback=false`; the previous forced-public-tunnel
topology sets it `true`. Three-hop client defaults, disabled external I2CP and
disabled transit remain unchanged. No zero-hop option, relaxed assertion,
extended four-minute acceptance deadline or reference-side shim was introduced.
Java 2.14.0 `ClientManager.java:85-86,462-501` establishes this local-versus-tunnel
boundary; `OutboundClientMessageOneShotJob.java:293-346,474-497` locates lease/cache
handling but does not prove a specific router bug.

Both forced-public controls exchanged initial DATA but **failed** after restart:
.NET **1 failed in 240.1 seconds**, pinned Python **1 failed in 240.9 seconds**,
at the same missing replacement-accepted-peer boundary. This remains a shared
router/topology recovery blocker, **not a demonstrated native-only defect**.
No speculative production workaround was added. Separate-router overlay,
public-tunnel recovery and published-native feature execution remain unverified.

`tests\Interop\Invoke-I2pTopology.ps1` now supports isolated
`-TestAssemblyPath`, `-NativeRestartOnly` and explicit
`-JavaLocalDestinationRouting`; child processes do not inherit Actions summary
paths. It rejects conflicting selectors, missing isolated assemblies and invalid
Java-only options before router startup. `docs\i2p-interface.md` records exact
reproduction commands. The four measured invocations used:

```powershell
$common = @{
    JavaExecutable = 'TestResults\i2p-tools-java-win\jdk-21.0.12.1+1-jre\bin\java.exe'
    JavaRouterDirectory = 'TestResults\i2p-tools-java-win\router'
    TestAssemblyPath = 'TestResults\i2p-isolated-20261029\DotReticulum.Interfaces.Tests.dll'
    AllowPublicNetwork = $true
    KeepArtifacts = $true
    TestTimeoutSeconds = 360
}
$reference = @{
    PythonExecutable = 'TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe'
    UpstreamPath = 'TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream'
}
& tests\Interop\Invoke-I2pTopology.ps1 @common -NativeRestartOnly -SamEndpoint 127.0.0.1:17656
& tests\Interop\Invoke-I2pTopology.ps1 @common -NativeRestartOnly -JavaLocalDestinationRouting -SamEndpoint 127.0.0.1:17657
& tests\Interop\Invoke-I2pTopology.ps1 @common @reference -ReferenceRestartOnly -JavaLocalDestinationRouting -SamEndpoint 127.0.0.1:17657
& tests\Interop\Invoke-I2pTopology.ps1 @common @reference -ReferenceRestartOnly -SamEndpoint 127.0.0.1:17656
```

Isolated assembly execution avoided concurrent writes to shared build outputs.
Reference imports verified **RNS 1.5.7/LXMF 1.2.1** at the unchanged pin.
Sanitized TRX result locations under `TestResults`:
`i2p-topology-7153a825416449848e4ca81c307d3576\results\i2p-live.trx`
(.NET local pass),
`i2p-topology-4845de2435dd45709f358f540845608f\results\i2p-live.trx`
(upstream local pass),
`i2p-topology-a63d153cdce94a6fb0b61c889c261ca1\results\i2p-live.trx`
(.NET public failure), and
`i2p-topology-f6227fe7ba2d4d82baaf7f3b8e8a84b3\results\i2p-live.trx`
(upstream public failure). Raw router state/logs may contain keys and are
**private local artifacts, not safe upload evidence**. Owned router/test
processes and both SAM listeners were stopped. PSScriptAnalyzer **1.24.0**
reported zero findings; parser/help, three executable rejection guards and
diff checks passed. The earlier unconditional failed-Java-restart statement is
superseded only for the measured local-destination topology.

Parent integration rechecked `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
**59 passed, 2 explicit missing-reference skips**, and
`pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
**61 tracked files, zero findings**. No production source changed in this slice.

### Windows RNode BLE software slice (not full desktop BLE acceptance)

Added static-native Windows SetupAPI/Bluetooth GATT discovery of paired devices,
Nordic UART service/characteristic selection, notification subscription and
write-without-response operations. `ble://`, name and address selectors dispatch
from the real single-radio constructor, not a Python process or a caller-owned
stream substitution. The bounded byte stream serializes/chunks writes, rejects
notification overflow, cancels pending operations, joins ownership on disposal,
and faults ambiguous partial writes rather than replaying bytes. Existing radio
ownership reopens/redetects/reconfigures after simulated GATT disconnect.
BLE partial-frame expiry is **1250 ms**, distinct from serial/TCP.

Pinned source: `RNS/Interfaces/RNodeInterface.py:174-186,385-400,1257-1450`.
`GenerateRNodeBleVectors.py` executes original selector code and
`BLEConnection.find_target_device`/`device_bonded`, with a recording scanner and
paired-address query, at **a29bfb51c468945c96a1ee22cae0114e993e2391**.
`Fixtures/rnode-ble.json` records that revision and actual source SHA-256.
No actual Bluetooth is involved in vector generation. Microsoft documents
`GUID_BLUETOOTHLE_DEVICE_INTERFACE` as paired-device enumeration at
https://learn.microsoft.com/windows/win32/api/_bltooth/#guids ; optional device
flags further reject known unpaired entries, rather than establish bonding
from an absent property.

Intentional/remaining boundaries:
- Windows uses **cached paired PnP/GATT inventory, not live advertisement
  scanning**. An offline first match may fail rather than select another
  advertising device. This is a partial discovery contract.
- Windows desktop GATT exposes no negotiated-MTU getter here: writes use
  conservative **20-byte** ATT payloads, not upstream's maximum-chunk optimization.
- In-progress synchronous Windows GATT transactions cannot be cancelled by this
  API. Cancellation is checked before/after; owned operations are joined and may
  last until the OS ATT timeout. The five-second option is not a hard in-flight
  native deadline. Missing driver connection flags leave idle disappearance
  detection dependent on a later failed operation.
- Linux/macOS BLE providers remain **not implemented**. Android is deferred.
  The local Windows machine reports **no present Bluetooth adapter**, so actual
  discovery, physical notifications/writes, pairing and device reconnect remain
  **unverified**. No RF evidence follows from stream tests.

The **11** software/lifecycle cases passed in the full integrated Release suite:
selector/bond oracles, bounded ring wrap/overflow, chunk serialization,
disconnect wakeup, cancellation/read recovery, notification-start cleanup,
ambiguous-write failure, platform rejection, and owner redetection including
fragments separated beyond serial's timeout. The integrated gate recorded in the
RAK section passed **1,847 tests, 82 explicit skips**, zero warnings/errors;
Interfaces contributed **325 passed, 23 skipped**. NativeAOT publication remained
warning-free; real native resource/crypto/file flows against pinned Python passed
**36 cases, 1 explicit hosted-retrieval skip**. Those resource flows do not exercise
physical BLE. TODO retains full RNode/desktop BLE acceptance as unchecked.

### Darwin rnsh terminal backend implementation

Implemented macOS PTY creation with Darwin-specific `O_NOCTTY`,
`POSIX_SPAWN_SETSID`/close-on-exec flags, pointer-sized opaque spawn objects,
72-byte LP64 termios observations/restoration, and window ioctls. The arm64
variadic ioctl binding places its pointer argument on the stack, as Apple's ABI
requires. XNU applies spawn file actions before creating the session; a fixed
shell bootstrap reopens one terminal descriptor after `setsid` and `exec`s the
literal argv to acquire a controlling tty and foreground process group.
It does not interpolate remote command text. Linux's existing spawn path and
Windows ConPTY remain separate. Unix disposal now joins the same teardown task
on repeat calls and releases streams even if bounded child termination fails.

Sources: pinned `RNS/Utilities/rnsh/process.py` plus Apple XNU revision
`f6217f891ac0bb64f3d375211650a4c1ff8ca1ea`
`bsd/sys/{termios,ttycom,ioccom,fcntl,spawn}.h`,
`bsd/kern/kern_exec.c`, `bsd/vfs/vfs_syscalls.c`, and Apple's arm64 ABI
documentation linked in `docs\rnsh.md`. No wire protocol or reference pin changed.

Added actual-Darwin tests for controlling tty/resize/literal argv, foreground
interrupt delivery followed by a fresh session, and cancellation/process-group
cleanup. Existing terminal-mode restoration, eight endpoint combinations and
published-native terminal flow tests now run on macOS instead of skipping.
They still require that actual OS; Windows results do not establish Darwin ABI
acceptance.

Local `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter FullyQualifiedName~Rnsh`:
**21 passed, 5 explicitly skipped** on Windows. The integrated full build,
coverage, native publication and multi-megabyte feature evidence is recorded in
the adjacent RAK milestone (**1,847 passed, 82 skipped**, no warnings/errors).
**Actual macOS and Linux regression execution awaits hosted CI**; the TODO and
current platform claims deliberately remain partial until that evidence exists.

### RAK4631 USB DFU and physical maintenance acceptance

The user authorized DFU, firmware reflashing and device settings without requiring
their presence at a reset button; Android work is deferred. Executed against the
single Windows-attached RAK4631/SX1262, application firmware **1.86**, without
starting the LoRa radio. Fresh EEPROM backups and the exact installed release
were preserved before mutation. The initial `--normal` diagnostic normalized
the previously unset operating-mode byte to Normal; no RF mode was selected.
The subsequent physical acceptance case restores every byte of its own starting
EEPROM, including the reversible display-brightness setting.

Found and fixed three actual-device issues:
- Windows serial discovery returned disconnected registry entries and could not
  resolve composite USB interfaces to their physical serial. Filter against
  available/configured ports and walk bounded Configuration Manager parents.
- Upstream's `adafruit-nrfutil -t 1200` successfully entered DFU but reopened
  obsolete **COM8**, while this board's bootloader enumerated as **COM10**.
  Update now performs the touch in native .NET, resolves the bootloader by the
  same USB identity, runs the installed flasher without another touch, and
  resolves the application afterward. Application PID `8029` and bootloader
  PID `002A` were observed. Windows sometimes reports the expected invalidated
  serial handle during the touch; the error is printed and only corroborated
  same-identity/different-port enumeration permits continuation.
- RAK firmware returns a valid **empty** `6d` configuration sector, which the
  decoder dropped and the maintenance client treated as a timeout. Accept that
  command's empty reply, report EEPROM settings and explicitly disclose the
  absent Wi-Fi sector; empty packet DATA and truncated nonempty sectors remain
  rejected. Actual pinned Python `rnodeconf COM8 --config` independently
  succeeded with the same EEPROM settings. New
  `GenerateRnodeconfConfigurationVectors.py` executes the original pinned
  maintenance `readLoop`, recording SHA/source hash and its empty-sector result.

Source: pinned `RNS/Utilities/rnodeconf.py:470-482,1022-1030,3361-3364,3619-3649,3700-3800`;
official firmware **1.86** `RNode_Firmware.ino` command handlers and
`Utilities.h` `eeprom_config_dump_all`/`kiss_dump_config`; independently installed
Adafruit nrfutil **0.5.3.post16**, whose USB touch uses 100 ms open and 1.5 s
re-enumeration pacing. No reference pin or upstream source was changed.
Official `rnode_firmware_rak4631.zip` was **243,109 bytes**, SHA-256
`99cae226cca349a1f567f0a6c0c9a0fd4cc9500ec079a631b18b8d4c29e7aa9c`;
the selected/application/target partition SHA-256 was
`25820129029e453de2614b100bfb002ac9ba1162cd63ac75d9d804bbc4b40fdd`.
These are integrity observations, not publisher/signature authentication.
The external flasher returned exit zero even for its original missing-COM8
failure; actual DFU programming, application recovery and firmware/EEPROM checks,
not that exit status, established this device result.

Validation:
- Focused `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --nologo -v quiet --filter FullyQualifiedName~Rnodeconf`:
  **45 passed**, including USB parent traversal, bounded/cyclic/missing identity,
  changed DFU/application ports, interrupted touch corroboration/failure,
  empty/truncated config sectors and existing maintenance behavior.
  Focused `RNodeMaintenanceConnectionTests`: **2 passed**.
- `Publish-CompatibilityApplication` from
  `tests\Compatibility\Support\Preparation.ps1` published
  `artifacts\native-device-ble-darwin` with trimming/AOT warnings as errors and
  executed native help. Publication was warning-free.
- Actual Pester **5.7.1**, container
  `tests\Pester\Parity7Device.Tests.ps1`, native application above,
  `AuthorizeRNodeFirmware=$true`, explicit owned USB serial, firmware **1.86**
  and installed `adafruit-nrfutil.exe`: **14 passed**, zero failed/skipped.
  Native `--config` matched EEPROM brightness; one brightness byte changed;
  DFU/reflash preserved all **200** EEPROM bytes; restoring brightness reproduced
  the entire starting EEPROM. No button press was needed.
  NUnit evidence: `TestResults\rnode-device-2b24b6d711454895928f408929fee243\native-final-pester.xml`.
  The normal runner now exposes explicit physical opt-in parameters; ordinary
  CI skips this hardware case rather than claiming acceptance.
- `pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1 -Application artifacts\native-device-ble-darwin\DotReticulum.Applications.exe -Python TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\venv\Scripts\python.exe -Upstream TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5\reference\python\upstream -Implementation DotReticulum -OutputDirectory TestResults\compatibility-desktop-final-20261029`:
  **36 passed, 1 explicit hosted-retrieval skip**, zero failed, including
  both-direction compressible/incompressible multi-megabyte native/Python rncp.
  The already fetched/import-verified pin was reused; this run makes no new
  hosted retrieval or separate upstream-candidate claim.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **59 passed, 2 explicit reference-dependent skips**; pinned
  `tools\Invoke-PowerShellAnalysis.ps1`: **61 tracked files, zero findings**.
  Full integration build/coverage, also including the concurrently developed
  BLE/Darwin slices below, passed: **1,847 passed, 82 skipped**, no failures,
  zero build warnings. Opt-in live xUnit tests were disabled.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults\coverage-device-ble-darwin-20261029`
  and `tests\Get-CoverageSummary.ps1` merged five production reports:
  overall **22,260/27,010 lines (82.41%)**, **16,112/23,203 branches (69.44%)**.
  Applications: **76.81%/63.71%** line/branch; Core **96.89%/90.93%**;
  Crypto **94.83%/94.00%**; Interfaces **82.21%/73.02%**;
  Transport **91.40%/79.79%**. Native subprocess execution is not measured by
  managed Coverlet instrumentation.

Physical evidence remains limited to this board/OS/application firmware:
bootloader replacement, power-loss flashing recovery, actual unplug/replug,
RF/over-air delivery, Multi, TNC and Weave devices remain unverified. EEPROM
signer is still unverified; no keys, firmware trust roots or manufacturer
signatures were replaced. The old read-only-only statements later in this
history describe their historical boundary, not the current physical status.

### Zero-argument local compatibility execution

`tests\Compatibility\Invoke-CompatibilityTests.ps1` now runs without parameters.
It chooses an owned unique `TestResults\compatibility-local-*` directory,
publishes a fresh native application for the host, enables the existing
candidate-owned hosted reference retrieval, provisions the verified pinned
checkout/venv, and executes both candidates. `-Prepare` enables the same setup
with explicit overrides (gateway, native candidate, output/reference paths or
candidate selection). An explicitly supplied native application is not rebuilt.
Existing explicit manual/CI invocations without `-Prepare` are unchanged;
read-only discovery still does not prepare, download or start nodes.

Automatic publishing supports the existing Windows x64, Linux x64 and macOS
ARM64 RIDs; only Windows was executed locally in this pass. It requires installed
Git, Python, the pinned SDK/native toolchain and Pester, and does not install
global tools. Reference packages are installed into the fresh run-owned venv.
Publication and smoke-test processes use the existing bounded process helper,
with MSVC setup isolated to the same child as publication. Added a narrow raw
command-line option for `cmd.exe /s /c`: .NET's argv escaping otherwise breaks
quoted batch-file paths. Ordinary subprocess calls still use ArgumentList.
Executable lookup selects the first PATH match, not a joined list of all
installed Python interpreters. Both failures were encountered and corrected
while exercising real no-argument invocations, not bypassed with caller flags.

Validation:
- Baseline `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors; `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  **1,831 passed, 79 skipped**, zero failed; opt-in live xUnit tests disabled.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **59 passed, 2 explicit reference-dependent skips**, zero failed with local
  Pester **6.2.0**. New tooling cases cover unique output/reference locations,
  explicit overrides/no overwrite/no republish, multiple PATH matches, invalid
  preparation combinations, publication failure propagation and actual bounded
  Windows batch setup with quoted paths. Tooling preparation tests mock external
  tool discovery/publication and do not require Python or native build tools.
- Focused `Invoke-Pester` with **5.7.1**, `Run.Path` set to
  `tests\Pester\Coverage.Tests.ps1` and `Filter.FullName` set to
  `*Local compatibility preparation*`, `*preserves cmd environment*`,
  `*help discoverable for every declared parameter`: **6 passed**, zero failed.
- `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **61 tracked PowerShell files, zero findings** with **1.24.0**.
- Actual `pwsh -NoProfile -File tests\Compatibility\Invoke-CompatibilityTests.ps1`
  with **no parameters or DOTRETICULUM reference environment variables** retained
  artifacts in `TestResults\compatibility-local-bc8780635d9341858663c96c7438bcd5`.
  The new Windows NativeAOT binary ran help and all **37/37 .NET cases passed**,
  including both-direction multi-megabyte rncp transfers and failure/recovery
  cases against the fixed Python peer. The .NET client retrieved **87,057**
  network bundle bytes via `reticulum.thecryptosquid.com` (11-hop route observed).
  Strict object checks and actual imports verified pin
  `a29bfb51c468945c96a1ee22cae0114e993e2391`, tree
  `24d4c393de9fd57b933836cea68891fd953ab060`, RNS **1.5.7** and the isolated
  pinned dependency installation. The existing disclosed mirror-parent seed was
  used; this is not a full network clone or publisher authentication.
  Fetched development HEAD was separately
  `c0fe08c08d75ffbcc8ef34bd7ad90636954b4738` (tree
  `14ba7e4b639f2d8892f556d01ffd112e741fbdaf`), not a new reference pin.
- The same command's independent **Upstream candidate passed 36/37 cases**.
  `T001` failed with `Bundle verification failed for batch` after observing
  **79,718,410 bytes** in the actual Python Git helper. This matches the already
  recorded local Windows upstream-only limitation below; it is not an automatic
  setup failure or a .NET retrieval failure. Both per-test JSON and NUnit reports
  were retained and the command correctly exited **1**. No assertion was weakened,
  no case skipped to obtain success, no reference shim added, and no upstream
  source or pin changed. Hosted Linux upstream acceptance is separate evidence.

Updated discoverable parameter help and CONTRIBUTING with the one-command path,
prerequisites, overrides, artifact ownership and this Windows baseline limitation.
No production behavior or CI workflow changed; no new compatibility claim or
cross-platform/hardware support is inferred. Coverage collection was unnecessary
for this tooling change. Hosted CI was not run or pushed in this pass.

### Standalone PowerShell parameter help

Added `.PARAMETER` descriptions for all **100 declared parameters** across the
**20 standalone scripts** covered by the existing help-discovery check (the
branding exporter has no declared parameters). Help explains purpose, defaults,
environment-variable sources, prerequisites and relevant parameter-set boundaries.
Extended the existing tooling regression to inspect each script's declared
parameter AST and require a nonempty description from actual `Get-Help -Full`
output; PowerShell common parameters are not script-owned documentation.

Reviewed Build and test, Release and Static analysis workflow invocations.
CI intentionally relies on several defaults: Pester version/retry count,
compatibility gateway/output path, artifact commit from `GITHUB_SHA`,
reference provenance beside the bundle, reference interpreter/checkout from
`DOTRETICULUM_*`, report truncation limits and static-analysis paths/settings.
These invocations and all script execution behavior remain unchanged.
Corrected the compatibility runner's stale summary-help claim: its `Label`
and `NoJobSummary` parameters are retained no-ops, since rendering is separate.
Contract integration's similarly named parameters remain functional.

Verification:
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **54 passed, 2 explicit reference-dependent skips**, zero failed using the
  locally selected Pester **6.2.0**.
- `pwsh -NoProfile -Command 'Import-Module Pester -RequiredVersion 5.7.1; $configuration = New-PesterConfiguration; $configuration.Run.Path = ".\tests\Pester\Coverage.Tests.ps1"; $configuration.Run.PassThru = $true; $configuration.Filter.FullName = "*help discoverable for every declared parameter"; $result = Invoke-Pester -Configuration $configuration; if ($result.FailedCount -gt 0 -or $result.PassedCount -ne 1) { exit 1 }'`:
  the parameter-help regression passed with CI-pinned Pester **5.7.1**.
  Running the entry point after preloading 5.7.1 locally encounters the existing
  side-by-side 6.2.0 assembly conflict when its minimum-version import selects
  the newer module; no claim of a full local 5.7.1 tooling run is made.
- `pwsh -NoProfile -File tools\Invoke-PowerShellAnalysis.ps1`:
  **60 tracked files, zero findings** with pinned PSScriptAnalyzer **1.24.0**.
- Actual `Get-Help` inspection confirmed descriptions for all 100 declared
  parameters, including `NoJobSummary`, `Implementation` and `FetchReference`.
- `git diff --check`: passed. No production code, protocol behavior or workflow
  changed; .NET builds, coverage collection and NativeAOT publication were not
  needed. Hosted CI was not run in this pass.

### Agent instructions aligned with executable compatibility and active CI

Updated `.github/copilot-instructions.md` to make the current shared Pester
acceptance model and authorized workflow architecture durable requirements.
It distinguishes customer-facing capability claims from bugfix coverage,
requires common executable scenarios with a fixed upstream peer and a separately
executed upstream baseline, preserves exact quoted citations/stable IDs/actual
Pester outcomes, and excludes hardware-dependent acceptance from runner-only CI.
Local executable RNS topologies are explicitly valid live-node evidence; vectors
and .NET-only round trips are not substitutes.

The instructions now cover candidate-owned hosted reference retrieval, same-run
artifact provenance/reuse, independent suite coverage and merged union counts,
Build/Test/Compatibility separation, advanced CodeQL and pinned PowerShell
analysis, warning-free full C# extraction, honest SARIF/summary failure handling,
release/versioning boundaries, PowerShell help/naming, process isolation and
cross-platform temporary-path/toolchain lessons. Removed stale "CI last/only
Build authorized/no linter" guidance and current LXMF/telemetry obligations;
historical scope stays labeled as such. Explicitly preserve concurrent edits
and scope validation to the change rather than rebuilding for prose.

Documentation-only: checked the instructions against current workflow files,
claim discovery/catalog/reporting and reference acquisition scripts, reviewed
the diff and ran `git diff --check`. No executable behavior changed; no build,
test run, coverage collection or NativeAOT publication was needed.

### Advanced static analysis, suite coverage summaries and accessibility

The user authorized replacing GitHub-managed CodeQL default setup with an
editable Static analysis workflow containing CodeQL and PSScriptAnalyzer jobs
and a consolidated summary. `.github/workflows/codeql.yml` preserves Actions,
C# and Python with the default CodeQL query suite; C# extraction uses a real
Release build with the pinned SDK. Relevant PR/push changes, Monday 05:00 UTC
and manual dispatch run analysis. Action references use full commit SHAs.
Read-only permissions are the default, with code-scanning upload permissions
scoped to analysis jobs. Fork PRs retain reports without code-scanning uploads.

PSScriptAnalyzer 1.24.0 is pinned and scans all tracked `.ps1`, `.psm1`, `.psd1`
files, including tests and helpers, without executing them. Shared settings
select twelve security/correctness/approved-verb rules, not a bulk style rewrite.
Warnings and errors fail its job after writing SARIF. Two Markdown helpers now
use an approved conversion verb. Two narrow, justified suppressions identify
public IFAC test fixture values deliberately written to owned upstream configs.
The new report converter handles parser errors as errors, relative URI paths,
empty results, inherited CodeQL severity, missing/failed analyses, duplicate
categories and malformed input. Analysis completion is separate from findings;
existing CodeQL results are not hidden or treated as a zero-findings success.

Build and Test exports separate deduplicated unit/integration coverage JSON and
displays per-project and overall line/branch hit counts and percentages beneath
each suite's cross-platform table. Missing platforms remain unavailable; the
existing merged union report is retained. Independently measured percentages
are not averaged or summed, and native subprocess execution is not claimed as
instrumented managed coverage.

Added a basic `ACCESSIBILITY.md`, linked from the README, describing CLI goals,
known lack of formal/screen-reader verification and issue-reporting guidance
without asserting compliance.

Local evidence before hosted validation:
- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: zero warnings/errors.
- `dotnet test DotReticulum.slnx -c Release --no-build -m:1 --filter 'Category=Unit' --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-analysis-ae5ee7713e614f768fef3d4291013e4a\unit --logger trx -- xUnit.MaxParallelThreads=1`:
  **1,416 passed, 7 skipped**, zero failed.
- The same command with `--filter 'Category!=Unit&Category!=Live'` and results
  subdirectory `integration`: **415 passed, 15 skipped**, zero failed.
  Opt-in live xUnit tests were not enabled; their exclusion is not a pass.
- `tests\Get-CoverageSummary.ps1` on each subdirectory with `-JsonPath`, on the
  merged root, and `tests\Get-TestPlatformSummary.ps1` on their exported platform
  artifacts verified the actual data path. Unit **60.71% lines / 48.84% branches**;
  integration **52.82% / 38.87%**; merged **83.11% / 69.84%**.

| Production project | Unit lines / branches | Integration lines / branches | Merged lines / branches |
|---|---|---|---|
| Applications | 47.32% / 38.84% | 46.07% / 33.76% | 76.83% / 63.76% |
| Core | 96.68% / 90.46% | 62.82% / 48.66% | 96.89% / 90.93% |
| Crypto | 94.83% / 94.00% | 87.93% / 48.00% | 94.83% / 94.00% |
| Interfaces | 50.10% / 38.61% | 58.72% / 53.93% | 85.58% / 76.23% |
| Transport | 91.15% / 79.04% | 59.78% / 41.70% | 91.36% / 79.39% |

`tools\Invoke-PowerShellAnalysis.ps1` analyzed **60 tracked PowerShell files**,
zero enabled-rule findings. The summary was also exercised on actual SARIF
downloaded from preceding CodeQL run `40637008141`, preserving its two Python
findings rather than inventing a clean scan. `actionlint` 1.7.12 (downloaded
official binary with verified release checksum) accepted both modified workflows.
No production implementation changed and NativeAOT was not republished locally.
`pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly` passed
**54 tests**, with **2 explicit reference-dependent skips**, zero failed.
Includes actual PSScriptAnalyzer execution against tracked clean, warning and
syntax-error fixtures, absence/failure reporting and coverage JSON/Markdown
round trips with complementary suite coverage that must not be added together.
Hosted workflow/migration results are recorded below after execution.

Static analysis run `40643125133` passed all five jobs, including actual CodeQL
and PowerShell SARIF uploads and the consolidated summary. Default setup is now
`not-configured`; advanced setup replaces it rather than running in parallel.
The hosted summary preserves two existing Python warnings, with no C#/Actions
or enabled-rule PowerShell findings. The user requested removing the C# overlay
fallback warning without weakening analysis: the pinned action's
`src/config-utils.ts` explicitly accepts `CODEQL_OVERLAY_DATABASE_MODE=none`.
Set this for C# initialization only, retaining the manual real build and full
database while allowing automatic overlay selection for other languages.
PowerShell SARIF automation IDs now end with `/`, retaining the complete
`/language:powershell` category in Code Scanning rather than an empty category.
The actual-analyzer regression verifies that ID.

Final hosted acceptance:
- Static analysis run **`40644131301`** passed all **five jobs**. C# initialization
  logs explicit full-database selection from `CODEQL_OVERLAY_DATABASE_MODE`;
  its check has no overlay warning annotation (only GitHub's runner-image
  migration notice). Code Scanning records the full `/language:powershell`
  category with zero findings. Existing Python findings remain visible.
- Build and Test run **`40644131484`** passed all **12 jobs**, including all three
  NativeAOT builds, native feature checks, shared candidate acceptance, upstream
  baseline and both summary jobs. Downloaded all platform test artifacts and
  executed `tests\Get-TestPlatformSummary.ps1` on them; verified all six
  unit/integration coverage JSON files and populated platform coverage cells.
  The preceding run `40643125165` was superseded/cancelled by this push, not a
  test failure.

| Hosted suite | Linux lines / branches | macOS lines / branches | Windows lines / branches |
|---|---|---|---|
| Unit | 61.92% / 49.24% | 60.64% / 48.69% | 60.75% / 48.81% |
| Integration | 54.15% / 39.47% | 52.87% / 38.77% | 53.54% / 39.46% |

These are independent production-code measurements from the recorded run,
not additive percentages or proof of native subprocess coverage.

### PowerShell entry-point names, help and CodeQL configuration correction

Standardized all 18 standalone PowerShell entry points on approved Verb-Noun
names and added discoverable synopsis, description and examples. Existing
approved names retain their verbs; the Pester installer filename is now
`Install-Pester.ps1`. Updated current documentation, workflow steps, Pester
callers and the AutoInterface C# harness. Historical commands below and the
archived plan retain their original filenames. Old entry-point names are
replaced, not retained as wrappers. Script parameters and operational behavior
are unchanged. A tooling regression checks approved verbs, consistent casing
and actual `Get-Help -Full` discovery. A blank line between `#Requires` and
comment-based help is necessary for that discovery.

PSScriptAnalyzer is not installed/configured in this checkout and no analysis
workflow was added in this pass. GitHub API verification corrected the earlier
assumption that a missing repository workflow meant CodeQL was disabled:
`gh api repos/dennispayne/DotReticulum/code-scanning/default-setup` reports
configured default setup for Actions, C# and Python, with weekly scanning.
The dynamically generated CodeQL run `40637008141` succeeded. Its success
indicates completed analysis, not absence of findings. No migration to advanced
setup or change to repository security settings was performed.

Verification:
- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors.
- `dotnet test DotReticulum.slnx -c Release --nologo -v quiet`:
  **1,831 passed, 79 skipped**, zero failed; opt-in live tests disabled.
- `pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly`:
  **49 passed, 2 explicit reference-dependent skips**, zero failed.
- `Invoke-Pester` with `New-PesterContainer` for
  `tests\Pester\Parity6Interfaces.Tests.ps1` and
  `tests\Pester\Parity8Packaging.Tests.ps1`, supplying the existing published
  `TestResults\compatibility-expanded-native\DotReticulum.Applications.exe`:
  **18 passed**, zero failed/skipped. Exercises renamed I2P runner guards and
  packaging/install/discovery/uninstall processes, including installed utilities.
- All PowerShell scripts parsed without errors; renamed compatibility discovery
  returned **37 cases**. No obsolete references remain in current tracked source,
  configuration or documentation; historical records are intentionally excluded.
- No production implementation changed; NativeAOT was not republished. The
  existing native binary above was used for the relevant process flows.

### Expanded advertised acceptance and primary-source citation navigation

The user requested substantially broader shared acceptance, not more copies of
the same test per OS. Work extends the readable Pester cases with independent
identity/cryptographic failures, malicious delivery-proof inputs and transfers
against fixed upstream peers. Tests retain direct Pester outcomes; a test count
is a count of distinct `It` cases, not the sum of candidate executions.
Claim text remains a verbatim pinned-source quotation. The harness now checks
those quotations at the stated source lines against the verified fetched
checkout, rejecting stale citations rather than accepting metadata on trust.
Discovery remains read-only and does not fetch files or start nodes.

Actions run **`40630370168`** passed all **12 jobs**, clearing the previously
recorded macOS timeout on that rerun. This is preceding-harness evidence, not
acceptance of the new expanded cases.

Baseline for this expansion:
`dotnet build DotReticulum.slnx -c Release --nologo -v quiet` completed with
zero warnings/errors;
`dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet` passed
**1,831** cases with **79 skipped**, zero failed, opt-in live tests disabled.

The expanded inventory has **37 distinct tests** (previously 7) and **17 quoted
claims** (previously 9). Fifteen claims have at least one case; heterogeneous
multi-hop routing and Channel/Buffer still have no shared acceptance cases.
Having a case does not establish a broad claim in full. New cases cover:
identity generation/reload/overwrite refusal and encoded import/export;
canonical/raw signature validation and corrupted message/signature rejection;
IV/ciphertext/tag tampering, wrong keys and truncation without plaintext;
public-only key restrictions; forged/truncated/wrong-hash delivery proofs;
authenticated management requests and denied identities; multi-megabyte rncp
upload/receive/fetch, actual compression and segmentation observations; missing
file and authorization recovery; and absent/wrong-name/wrong-passphrase TCP IFAC.
All existing IDs remain stable. New quotes identify narrower advertised
contracts where appropriate; no synthetic claim verdicts or platform-multiplied
test totals were added. Existing vector provenance and the live pin are unchanged.

The new failure cases exposed and fixed two production defects. Remote
management's private deadline raised an uncaught `TaskCanceledException`;
`WaitAsync(timeout, callerToken)` now distinguishes timeout from caller
cancellation, and rnstatus reports unsuccessful remote status with exit 2,
matching the actual upstream CLI. An unauthorized rncp peer can close the link
between identification and resource creation; the client now installs its close
observer before identifying and converts only closed-link
`InvalidOperationException` failures to the existing failed-transfer result.
Fetch handles the same close race. The tests retain failed exit codes,
no-output-file checks and subsequent authorized recovery rather than accepting
the previous native process crashes. Candidate invocation also preserves an
array prefix when running a managed DLL through `dotnet`.

Local acceptance:
- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: zero warnings/errors.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -o TestResults\compatibility-expanded-native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --nologo -v quiet`:
  passed with MSVC setup and `vswhere` discovery in the same process;
  the published executable ran `--help` and all native scenarios below.
- `tests\Compatibility\Run-CompatibilityTests.ps1 -Application TestResults\compatibility-expanded-native\DotReticulum.Applications.exe -Python TestResults\pester-compatibility-reference\python\venv\Scripts\python.exe -Upstream TestResults\pester-compatibility-reference\python\upstream -Implementation Both -OutputDirectory TestResults\compatibility-expanded-8bab640b43c5467ea0adb7700db66c17 -NoJobSummary`:
  **36 passed, 1 explicitly skipped per candidate**, zero failed. The skipped
  Git case did not request another hosted fetch; preceding fresh retrieval
  evidence is separate. This exercised the published native crypto, links,
  requests, compression, multi-segment resources and persisted transfer bytes,
  not merely publication or help.
- `pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly`:
  **48 passed, 2 explicit reference-dependent skips**, zero failed.
- `Invoke-Pester` with `New-PesterContainer` for
  `tests\Compatibility\Support\Harness.Tests.ps1`, supplying the new native
  executable and verified Python checkout/venv: **13 passed**, zero failed/skipped.
  Includes exact/stale source quotation handling, rejection of multiple
  citations, missing/failed references, actual failure isolation and the
  expanded upstream-only baseline after final claim/tag integration.
- `Assert-CompatibilitySourceCitations` against the fetched checkout and
  `tests\Compatibility\List-CompatibilityTests.ps1 -IncludeUncoveredClaims`:
  all 17 exact quotations verified; 37 tests, two uncovered claims; no network
  retrieval or test execution during discovery.
- `dotnet test DotReticulum.slnx -c Release --no-build -m:1 --settings tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults\coverage-compat-final-a3c9a2cea8a34cfb80c9f7c1de2cebf7 --nologo -v quiet`
  and `tests\coverage_summary.ps1 TestResults\coverage-compat-final-a3c9a2cea8a34cfb80c9f7c1de2cebf7`:
  **1,831 passed, 79 skipped**, zero failed, opt-in live xUnit tests disabled.
  An earlier uninstrumented full run hit the unchanged host-statistics
  concurrency case (its isolated and Applications-suite reruns passed); the
  first instrumented run hit the unchanged descendant-process timeout.
  Final project-serialized coverage passed. A concurrent diagnostic run
  accidentally overlapped instrumentation and reported a missing assembly;
  that invalid diagnostic is not acceptance evidence.

| Production project | Line coverage | Branch coverage |
|---|---:|---:|
| Applications | 76.88% | 63.80% |
| Core | 96.89% | 90.85% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 85.64% | 76.29% |
| Transport | 91.27% | 79.26% |
| **Merged overall** | **83.13%** | **69.84%** |

Five production reports were merged by source identity, without averaging
project percentages or double-counting shared assemblies. These managed-suite
figures do not incorporate the separately executed native/Python acceptance.

Cross-platform acceptance: Actions run **`40634153580`** at **`3edc8eb`**
passed all **12 jobs**. Native Linux x64, macOS ARM64, Windows x64 and the actual
upstream Python baseline each passed **37/37 cases**, zero failed/skipped.
These are 37 distinct cases executed four ways, not 148 distinct tests.
Unlike the local no-fetch run, each candidate's hosted Git retrieval case ran;
the pipeline retained candidate-owned missing-object acquisition and verified
source provisioning before the remaining cases. Both summary jobs passed,
as did the three full platform Test jobs. This accepts the bounded expanded
shared suite across the four candidates, not the remaining broad claim scopes.

The Windows rncp reference adapter retains the existing visible
`shutil.move` to `copyfile` workaround so Python can preserve received files
while its temporary resource remains open. Compression/segment observations
wrap upstream callbacks and invoke the originals; acceptance assertions remain
in Pester. Unix has no retention shim. Windows results therefore do not
establish unmodified-upstream Windows retention behavior. Direct TCP tests do
not cover every bearer, packet loss, sustained soak, alternate-route recovery,
or all request size/concurrency/cancellation boundaries.

Primary-source navigation investigation:
`dotnet run --file tests\Interop\CheckNomadCitation.cs -- TestResults\nomad-citation-page-check-20261028 TestResults\pester-compatibility-reference\python\upstream`
built successfully and resolved the actual hosted Git destination through the
.NET stack. The derived NomadNet page destination
`a8d24177d946de4f1f0a0fe1af9a1338` could not establish a link
(`IOException: Could not establish link`), so no blob page was retrieved.
Owned runtime state was cleaned up. The attempted browser address was:

```text
a8d24177d946de4f1f0a0fe1af9a1338:/page/blob.mu`g=reticulum|r=reticulum|ref=a29bfb51c468945c96a1ee22cae0114e993e2391|path=README.md|raw=y
```

Pinned upstream `RNS/Utilities/rngit/pages.py:137,179,709-720,787-836` and
`server.py:2052-2057` distinguish the optional page service from Git and support
the pinned blob fields. NomadNet `Browser.py:1459-1495` at
`b2f852c9e0efc68d89d9185939f42ec0c2fee125` accepts bare destination/page
addresses with backtick-separated fields. No external clickable URI scheme or
source-line anchors were established. All current `CitationUri` values remain
null, with no GitHub fallback. Git resolution does not prove that the optional
page service is enabled, reachable or serves the selected pin; this failed
navigation check is not a compatibility pass or publisher authentication.

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

Claim catalog wording now quotes the pinned source verbatim instead of
paraphrasing the bounded tests. All nine excerpts were checked against their
exact cited source lines in the candidate-fetched pinned checkout. Citations
are file/line references without redundant headings. Read-only discovery now
exposes `Claim` separately from Pester's `Name`; uncovered claims have no test
name. The joined-metadata regression and tooling suite passed **46** cases
with **2 explicit reference-dependent skips**, zero failed. Quoted claims do
not imply the current tests fully verify their scope.

Each claim now stores one `citation` object, not an array. Discovery exposes
only `Citation` (`file:line`) rather than duplicate readable/structured columns.
`pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly` passed **46** cases
with **2 explicit reference-dependent skips** after that simplification.

Actions run **`40628676024`** confirmed the portable reporting correction:
all four Compatibility jobs, both summary jobs, Windows Test and Linux Test
passed. That run's macOS Test failure was a discovery announcement timeout in
the unchanged `Parity4DiscoveryControlsTests.ControlledHdlcAutoconnectAppliesIfacAndDiscoveredRoutingOverrides`;
this was not a Compatibility case or reporting failure. Subsequent run
**`40630370168`** passed all 12 jobs, resolving that pending run-level acceptance.
The Pester harness itself is cross-platform accepted;
discovery-only claim/citation metadata cleanup was subsequently verified locally.

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
