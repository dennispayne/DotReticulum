# DotReticulum development history

Development chronology, verification evidence, upstream comparisons and recorded limitations. Phase and subphase completion is tracked only in [plan.md](../plan.md); this document is not a second progress checklist.

Current live interoperability uses the authoritative Reticulum-hosted upstream at `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7). Earlier protocol vectors and their reproduction generators retain their original `e40191b3d193b46b7f2d8a44424a594cd758839b` provenance; Parity 3 runtime and Parity 4 transport/interface fixtures explicitly record the hosted `a29bfb51...` revision and source hashes. Earlier records describe the pins and tooling used at that time, including Python coverage scripts since replaced by PowerShell.

## Verification records

### Parity 8 compatibility acceptance, in progress (2026-10-25)

Scope starts from clean `d4eac24`: source-backed classification, impaired
mixed transport recovery, native installation/discovery and available-platform
acceptance. CI/CD remains a separate phase. The user will provide macOS
execution through GitHub Actions later; no local Mac is available. That
deferral does not implement or verify the currently missing Darwin rnsh
terminal backend, and no macOS support claim is made.

The warning-free Release baseline initially had one intermittent failure in
`ConcurrentDefaultCreationPublishesOneWholeConfiguration` (**1819 passed,
86 skipped, one failed**). The configuration class selection then passed
**52**, and **30** separate focused repetitions passed. A fresh full serialized
run with TRX diagnostics passed **1820, skipped 86**, zero failed:
`dotnet test DotReticulum.slnx -c Release --no-build -m:1 --nologo -v quiet
--logger trx --results-directory TestResults\parity8-baseline-trx
-- xUnit.MaxParallelThreads=1`. The initial failure is not attributed to
Windows Firewall or claimed fixed without a reproducible cause; no
configuration production change was made on that evidence.

The source audit discovered applicable software gaps hidden by earlier broad
family checkboxes: non-TCP discovery publication, packet/link/shared PHY
attribution, desktop RNode BLE providers and Darwin rnsh terminal handling.
Android host/provider and physical acceptance limitations also remain.
`docs/compatibility-contracts.md` classifies these separately from intentional
.NET differences and actual upstream placeholders. Inventory completion is
not implementation completion.

The user conditionally authorized necessary COM8 RAK reflashing. The current
native `rnodeconf COM8 --info` successfully queried the physical RAK4631/SX1262,
firmware **1.86**, nRF52 platform/MCU/board **70/71/51**, Normal host-controlled
mode. EEPROM information decoded; device signature remains **Unverified**,
not an authenticated manufacturer claim. `rnodeconf COM8 --eeprom-backup
--backup-file <session-files>\rnode-com8-pre-parity8.eeprom` then saved the
actual **200-byte** EEPROM in the session artifact directory outside source
control. No EEPROM bytes, credentials or private identity were committed.
Both operations exited zero, without starting RF, resetting, flashing or
mutating EEPROM. The functioning board currently supplies no reason for a
gratuitous reflash. One radio cannot establish peer-to-peer RF acceptance.

The first verified software slice restores a statistics boundary accidentally
regressed in Parity 7: `rxb`/`txb` and sampled bit rates again use actual
bearer diagnostics, including HDLC framing/escaping on transmit. Extensions
without diagnostics stay null; separately named decoded totals remain
available. A real TCP socket observes the actual framed bytes and checks
both the status counters and sampled rate against those bytes.

Discovery publication now implements the pinned I2P/RNode/Weave/KISS and
KISS-framed TCP-client branches rather than only TCP/Backbone servers.
Factory/host validation, radio metadata fields, canonical KISS type and
non-TCP provider handling are covered. `GenerateDiscoveryVectors.py
--output TestResults\parity8-discovery-unused.json --bearer-output
tests\DotReticulum.Applications.Tests\Fixtures\DiscoveryBearerVectors.json`
executed the unchanged pinned upstream builder in the verified RNS/LXMF
environment. Existing `DiscoveryVectors.json` was not regenerated or
relabeled. The new fixture records the revision/source hash and actual
numeric-KISS-modulation `AttributeError`; native publication diagnoses this
reference defect instead of inventing a successful value.

`dotnet test
tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
-c Release --no-restore --filter
"FullyQualifiedName~DiscoveryTests|FullyQualifiedName~DiscoveryMetadataTests|FullyQualifiedName~RuntimeDiscoveryHostTests|FullyQualifiedName~RuntimeOperationsTests|FullyQualifiedName~SharedHostIntegrationTests"
--nologo --logger "console;verbosity=normal"` passed **70, skipped 1**, zero
failed and no build warnings. The skip is the Unix metadata executable case
on Windows; the actual Windows-native provider case passed. This verifies
software field mapping/lifecycle, not actual RF or I2P network discovery.

### Parity 7 utility software acceptance; physical acceptance outstanding (2026-10-24)

The intermediate checkpoints below are chronological investigation evidence.
The final software acceptance record at the end of this section supersedes
their pending software gates; physical-device limitations remain current.

Scope starts from `1984d92`: complete the utility contracts in Parity 7,
without starting compatibility/release acceptance or CI/CD. The user accepted
the previously recorded Parity 6 limitations; they remain unverified, not
silently converted to support claims.

Before utility changes, `dotnet build DotReticulum.slnx -c Release --nologo
-v quiet` passed with zero warnings/errors. `dotnet test DotReticulum.slnx
-c Release --no-build -m:1 --nologo -v quiet -- xUnit.MaxParallelThreads=1`
passed **1687, skipped 84**, zero failed. Live, NativeAOT executable and
physical-device opt-ins were disabled for this baseline. Serial collections
avoid the previously observed unrelated Windows executable-fixture races.
Executing `verify_reference(require_lxmf=True)` from
`tests\Interop\reference_environment.py` also passed against the existing
isolated environment, confirming the actual RNS 1.5.7 checkout import,
unchanged reference revision/source and LXMF 1.2.1 distribution import before
new utility fixture generation.

Independent workstreams cover identity operations, network inspection,
interactive shell, device configuration/placeholder classification and
transfer/execution CLI fidelity. Each must establish options/defaults,
successful and failing outcomes, persistence/process effects and applicable
wire behavior against the unchanged hosted
`a29bfb51c468945c96a1ee22cae0114e993e2391` reference before claiming completion.
Protocol fixtures must come from executed reference code. Process acceptance
uses separate applications and persisted/output/exit results; terminal,
firmware, RF and platform requirements must retain their actual evidence
boundaries. Shared dispatch and full gates are integrated centrally.
`pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly` passed **5,
skipped 0** after registering the utility process suites.

The first integrated utility checkpoint is not acceptance: focused xUnit
execution found a mixed-list metadata validator regression and a parser
expectation inconsistent with upstream dispatch. Both were corrected; the
next focused run passed **87, skipped 4**, with one outstanding Windows
ConPTY assertion failure. Compilation of newly integrated device work also
exposed accidentally nested methods, corrected before those cases ran.

Separate-process `Parity7Identity.Tests.ps1`, using the verified reference
checkout/venv, passed **11, skipped 0**. This includes independent detached
signatures, legacy/embedded/wrapped messages, authorized editor success,
failure and deadline, binary stdout, actual Python file encryption in both
directions across the 16 MiB boundary, network identity lookup, cache
retention across restart and an authenticated reverse announce. Explicit
UTF-8 console input/output fixes Windows redirected Unicode text without
altering binary stream handling.

At that checkpoint, device/placeholder and basic shell Pester cases passed
**13, skipped 0**. An initial actual Python `rncp` fetch timed out: a cached
identity was mistaken for an available route after client restart. Shared
`ClientLinks` now waits for a path before establishing the link and tears
down pending links on cancellation/error. A cached-identity/no-route
regression passes. The subsequent root CLI and transfer Pester run passed
**12, skipped 0**, including both actual Python listener roles for file
transfer and authorized execution, raw binary output, writable-directory
failure, renamed utility discovery and preserved legacy identity stderr.
Windows home expansion also normalizes its separator.

Neither these counts nor simulator results establish firmware flashing,
physical RF, interactive terminal recovery or completed Parity 7 support.
Full gates, coverage and native execution remain pending.

The subsequent rebuilt utility checkpoint passed **110, skipped 4** with
three outstanding assertions (ConPTY capture and two network-utility
checks). All rnid parser vectors, transfer timeout/concurrency/cancellation
cases and current device firmware/recovery simulations passed. A managed
Pester run of identity, device, transfer and network utility suites passed
**33, skipped 0**, using the verified Python environment where applicable.
The network suite at this point covers dispatch/invalid input, not yet core
statistics/path/probe acceptance.

Actual POSIX Python rnsh runs through WSL do not yet pass: the peer accepts
and authenticates the link, negotiates the protocol and executes the child,
but then remains running after its pipe-close observations until the test's
150-second deadline. Retained upstream diagnostics distinguish this from a
network-connectivity failure. This remains an investigation, not a waived
interop test or an undisclosed reference shim.

The next coherent focused command,
`dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
-c Release --filter "FullyQualifiedName~RnshTests|FullyQualifiedName~Parity7|FullyQualifiedName~Rnodeconf|FullyQualifiedName~RncpRnxTests|FullyQualifiedName~RnsdTests|FullyQualifiedName~ReticulumNodeTests"
--nologo -v quiet --logger trx --results-directory TestResults\parity7-ninth-focused
-- xUnit.MaxParallelThreads=1`, passed **119, skipped 4**. ConPTY now captures
the child through explicitly null startup standard handles instead of
leaking output to the parent console. Maintenance serial ownership tests
also passed **2, skipped 0**.

Windows NativeAOT publish with both `ILLinkTreatWarningsAsErrors=true` and
`IlcTreatWarningsAsErrors=true` passed, followed by native `--help`.
`pwsh -NoProfile -File tests\run-pester.ps1 -Application
artifacts\native-parity7\DotReticulum.Applications.exe -Python <verified-venv>
-Upstream <verified-checkout>` passed **70, skipped 2**, with three failing
new network-utility test setup assertions. Native rnid's **11** process
cases, transfer/execution's **6**, device/placeholder safety's **13** and
root CLI's **6** all passed, including renamed utility dispatch, actual
Python identity lookup/retention, both-direction >16 MiB RFE and actual
Python rncp/rnx in both listener roles. Existing native encrypted multi-MB
rncp and resource process flows also ran; this is not publish-only evidence.
Network setup failures and full gates remain open. The leaked daemon from
failed setup was explicitly stopped by its owned PID.

The rnsh investigation independently executes unmodified upstream
`CallbackSubprocess`: without EOF it reports the child exit; with EOF its
`close_stdin` termination waiter consumes the exit before the polling
callback, leaving a null return code. Successful self-terminating sessions
and bounded negative EOF/recovery acceptance are being separated, not
papered over with a reference-side shim.

`DOTRETICULUM_NATIVE_EXE=artifacts\native-parity7\DotReticulum.Applications.exe`
with `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
-c Release --no-build --filter "FullyQualifiedName~NativeResourceTests"
--nologo -v quiet -- xUnit.MaxParallelThreads=1` passed **1, skipped 0**,
executing the encrypted/compressed multi-megabyte native resource flow.
The network process rerun passes actual local daemon management/path/drop/
blackhole behavior and a probe path request, but remote setup still needs
correction. No remote utility completion claim is based on these results.

Actual terminal-mode restoration now passes inside ConPTY: the probe
captures original modes, enters raw mode, observes changed flags and
asserts exact restoration on disposal. The focused shell/network command
passed **20, skipped 0**. With verified Windows Python imports and the
unmodified Linux reference via the explicit WSL interpreter/checkout
overrides, `RnshLiveTests|RnshNativeTests` passed **2, skipped 0**.
This exercises both Python roles, binary streams, at least two dropped
Channel messages across the impaired bridge, resize, cancelled-child
cleanup and fresh sessions. It also observes the actual pinned EOF defect
under a bounded deadline before teardown/recovery. A published-native
initiator runs inside an actual terminal and completes a remote session.
This is not a claim that upstream EOF completion was repaired or shimmed.
Linux native-backend validation and the final source audit remain pending.

The full Windows regression run,
`dotnet test DotReticulum.slnx -c Release --no-build -m:1 --nologo -v quiet
-- xUnit.MaxParallelThreads=1`, passed **1800, skipped 86**, zero failed,
before the final expanded endpoint/statistics changes. Live/native opt-ins
were disabled for that run; the separate live/native evidence above remains
distinct. Later changes require fresh targeted and full milestone gates.

All eight Windows child pipe/terminal combinations pass through actual
child redirection probes; the expanded shell/network focused run passed
**28, skipped 0**. The corresponding Linux snapshot passed **20** shell
cases, with one terminal-mode restoration probe failure still under
investigation. Linux-x64 NativeAOT publish with both warning gates and
`--help` passed. Published Linux terminal-hosted rnsh and encrypted resource
tests passed **2**; the mixed-Python shell case reached failed-spawn
verification but failed a Windows-specific error-text assertion. These
remaining Linux assertions are not recorded as platform acceptance yet.
Native remote status/path requests now pass against both native and actual
Python nodes; the probe callback observation remains under investigation.

The Linux restoration failure was fixed by applying .NET's control-C
terminal state before the native termios snapshot, so exact native
restoration is last. The rebuilt Linux selection passed **23, skipped 0**:
all eight endpoint combinations, exact mode restoration, both Python roles
with impairment/recovery and the native terminal-hosted initiator. After
republishing the corrected Linux production code with both warning gates,
native terminal-hosted rnsh plus encrypted resource tests passed **2,
skipped 0**. Failed-spawn assertions now verify the appropriate Windows or
Linux error text rather than requiring a Windows API name on Linux.

Published Windows network utilities Pester passed **7, skipped 0** after
correcting the thin Python callback's use of a nonexistent Packet property.
Actual callbacks now verify both proof-producing and no-proof destinations.
Local management/path/drop/blackhole behavior, native remote status/path and
actual pinned Python remote status/path plus probe reply/loss all execute
through separate processes. Empty remote tables are valid results, not
authentication failures. Final measured-statistics tests and integrated
coverage are still pending.

Measured-statistics behavior now passes: the test drives actual path-request
ingress and outbound requests and verifies counters plus queued depth/
pressure. The final Windows network/terminal/runtime focused selection
passed **41, skipped 0**, covering no-argument usage, repeated verbosity,
blackhole-list output, announce sorting and table/rate precedence. One
test-analyzer argument-order warning is being corrected before the full
milestone coverage gate.

Device software acceptance is separated in `plan.md` from physical
flashing/reset/re-enumeration/EEPROM persistence and RF. Only read-only COM8
identification was authorized; no real firmware or EEPROM operation was
performed. The Parity 7 parent remains unchecked while physical acceptance
is unavailable, even after all scoped software gates pass.

Full serialized coverage checkpoint:
`dotnet test DotReticulum.slnx -c Release --settings tests\coverage.runsettings
--collect:"XPlat Code Coverage" --results-directory
TestResults\coverage-parity7-final-73d8d59a -m:1 --nologo -v quiet
-- xUnit.MaxParallelThreads=1` passed **1814, skipped 86**, zero failed.
Live/native opt-ins were disabled for this collection. The existing
`tests\coverage_summary.ps1` merged all six reports by production line/branch
identity without double-counting:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 75.80% | 62.47% |
| Core | 96.96% | 91.90% |
| Crypto | 94.83% | 94.00% |
| Interfaces | 85.39% | 78.13% |
| LXMF | 90.23% | 70.96% |
| Transport | 90.95% | 81.50% |
| Overall | 82.48% (21874/26521) | 69.24% (16675/24083) |

The complete published-native Pester entry point with the verified Python
environment then passed **73, skipped 2**, zero failed, including all seven
network-utility process cases. Conditional skips were Unix SIGTERM and
abstract-socket cases on Windows, not successful stand-ins.
The final source audit subsequently identified additional software-maintained
rnstatus totals/speed/burst/TX/filter fields required by accepted options;
they are not hardware-only omissions. Their implementation and regression
gate remain required, so this coverage checkpoint is not final acceptance
of complete rnstatus parity.

The same complete Pester entry point against the managed Release DLL also
passed **73, skipped 2**, zero failed, with the verified Python environment.
A bounded integration review identified a concurrent rncp same-name save
race: two completed resources could select the same non-overwriting target
before either move. The collision fix and a focused concurrent regression
are required before final transfer acceptance; earlier passing healthy
transfer cases did not cover this race.

The receive-save review also identified a .NET Unix publication limitation:
non-overwriting `File.Move` can use a check followed by rename. Concurrent
in-process publications therefore require bounded shared serialization in
addition to collision retry; a failure-free Windows test alone would not
establish Linux preservation.

The receive-save fix now coordinates final publication with a fixed bounded
set of locks, leaving payload copy/staging outside the lock. It rechecks
targets before publication and retries only real collision errors, preserving
staged data. A deterministic barrier forces both receives to select the same
target before either stages its payload; both original payloads survive with
distinct names. This and non-file-error/cancellation cleanup regressions
passed on Windows and Linux (**2** Linux targeted cases, zero skipped).
Coordination is process-local; the documented Unix race against unrelated
external writers is not claimed eliminated by these locks.

The next full collection correctly rejected the expanded statistics batch:
two existing shared-host integration tests failed because a newly exposed
boxed byte was unsupported by the management MessagePack boundary.
The separate shared-host reproduction fails both tests with the explicit
`Unsupported management MessagePack type Byte` diagnostic. This is a real
regression to fix, not firewall interference or an acceptable flaky gate.
The failed collection is not used as final coverage acceptance.
Child aggregate accounting and Local-interface reporting differences also
remain under source-backed review.

#### Final integrated software acceptance

The shared-management regression is resolved at the statistics boundary:
interface mode is emitted as a supported integer rather than a boxed byte.
Existing real shared-owner/reconnect tests and added row-serialization
assertions now pass. Aggregate announce/path observations include spawned
children exactly once; byte/packet totals use parent-rolled manager counters.
Local TCP reports measured pending write bytes, explicitly not invented
upstream buffer, drop or stall measurements. Status renders configured
gravity, announce-rate limits and IFAC information. The focused
`RuntimeOperationsTests|Parity7NetworkUtilitiesTests|SharedHostIntegrationTests`
selection passed **26**, zero failed/skipped.

Final `dotnet build DotReticulum.slnx -c Release --nologo -v quiet` passed
with **zero warnings/errors**.
`dotnet test DotReticulum.slnx -c Release --settings tests\coverage.runsettings
--collect:"XPlat Code Coverage" --results-directory
TestResults\coverage-parity7-complete-73d8d59a -m:1 --nologo -v quiet
-- xUnit.MaxParallelThreads=1` passed **1820, skipped 86**, zero failed.
Live/native opt-ins were disabled for this full collection, not silently
counted as live successes.
`pwsh -NoProfile -File tests\coverage_summary.ps1
TestResults\coverage-parity7-complete-73d8d59a` merged six reports:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 76.46% (10578/13834) | 63.17% (9924/15711) |
| Core | 96.58% (1725/1786) | 91.42% (1055/1154) |
| Crypto | 94.83% (110/116) | 94.00% (47/50) |
| Interfaces | 85.43% (4499/5266) | 78.25% (2533/3237) |
| LXMF | 90.23% (388/430) | 70.96% (193/272) |
| Transport | 91.04% (4920/5404) | 81.71% (3229/3952) |
| Overall | 82.80% (22220/26836) | 69.66% (16981/24376) |

Latest Windows native publication used the MSVC environment in the same
process as `dotnet publish
src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release
-r win-x64 -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity7 --nologo -v quiet`;
publish and native `--help` passed. The complete
`pwsh -NoProfile -File tests\run-pester.ps1 -Application
artifacts\native-parity7\DotReticulum.Applications.exe -Python <verified-venv>
-Upstream <verified-checkout>` passed **73, skipped 2**. The same entry point
without `-Application` passed **73, skipped 2** against the managed Release
DLL. Both skips are Windows-inapplicable Unix SIGTERM/abstract-socket cases.
These include actual pinned Python peers, not only native loopback.

With `DOTRETICULUM_LIVE=1`, the verified Python paths and explicit unmodified
WSL rnsh interpreter/checkout overrides, plus `DOTRETICULUM_NATIVE_EXE` set
to the latest publication, `dotnet test
tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
-c Release --no-build --filter
"FullyQualifiedName~RnshLiveTests|FullyQualifiedName~RnshNativeTests|FullyQualifiedName~NativeResourceTests"
--nologo -v quiet -- xUnit.MaxParallelThreads=1` passed **3**, zero skipped.
This reruns both actual Python shell roles with impairment/recovery, an
actual terminal-hosted native initiator and encrypted/compressed multi-MB
native resource transfer after the final production changes.

The refreshed owned Ubuntu snapshot also passed a warning-free Release
build, Linux-x64 publish with both warning gates and native `--help`.
With the verified Linux reference/venv and native executable opt-ins, the
same Applications test command selecting
`RnshTests|RnshLiveTests|RnshNativeTests|NativeResourceTests|RuntimeOperationsTests|SharedHostIntegrationTests|Parity7NetworkUtilitiesTests|FileSaver`
passed **50**, zero skipped. This includes the concurrent save regression,
all pipe/terminal combinations, actual Python sessions, exact terminal
restoration, native resource/session flows and shared-management regressions.

The Linux native `tests/run-pester.ps1` entry point initially lacked Pester;
the existing explicit Pester **5.7.1** module was supplied to that owned
snapshot. It then passed **74** with one failed jail test: the test sent a
literal Windows backslash on Unix and therefore requested a nonexistent
filename rather than an outside path. The test now constructs the traversal
with `Path.Combine`, preserving the actual jail rejection requirement.
The complete affected `Parity7Transfer.Tests.ps1` container subsequently
passed **6**, zero failed/skipped, on both Linux and Windows native binaries.
All other Linux process cases had already passed, including Unix-only
SIGTERM/abstract sockets; the whole entry point was not rerun after this
test-only portability correction.

All scoped Parity 7 software items are accepted with the documented safety,
platform and upstream-defect differences; this is not full Reticulum parity.
The Parity 7 parent remains unchecked solely for explicitly authorized real
rnodeconf flashing/reset/re-enumeration/EEPROM persistence and RF acceptance.
No such physical mutation was performed: only earlier read-only COM8
identification was authorized, and only one radio is available.
macOS/broader topology/release acceptance remains Parity 8. No Parity 8
implementation or CI/CD workflows were added.

### Parity 6 software recovery and authorized RNode identification, partial (2026-10-24)

This continues `9bc0b02`, not Parity 7 or CI/CD. The owner confirmed that only
one RNode is available and requested finishing software while retaining
unverified RF acceptance. Physical TNC/Multi/Weave and Android execution are
not established by that deferral. The Parity 6 parent checkbox remains open;
the earlier Auto and Local software limitations below are superseded here.

Auto maintenance now retires a disappeared device's socket and peers, remembers
eligible startup devices, retries failed opens and creates a fresh listener on
reappearance, including the same address/index. Two deterministic regressions
verify cleanup, receive completion, fresh peer/socket ownership and retry.

Local shared receive counters now count decoded HDLC payloads, excluding framing
and empty keepalives. An explicit native platform policy preserves desktop
keepalives and implements Android's three-second initiator keepalive and
12-second accepted-client sleep pause using monotonic elapsed time. Any incoming
socket activity refreshes it. Linux/Android abstract-socket selection is shared
by runtime/config parsing. Deterministic-clock and real loopback tests exercise
pause, resume and accounting; they are not Android device evidence. Packet
framing now allocates only after acquiring the existing write semaphore.

The owner's authorized COM8 RAK answered the production native serial factory's
read-only detection with firmware **1.86**, nRF52 platform **0x70**, MCU **0x71**.
An initial DTR-low probe received nothing; asserting DTR made identification
work. Upstream Serial/KISS/AX25/RNode/Weave constructors disable flow handshake
but retain pyserial's asserted DTR/RTS defaults. The shared `NativeSerialPort`
factory now preserves that distinction. The RNode generator was rerun against
the unchanged pinned reference to record actual pyserial defaults; existing
framing/configuration vectors did not change. An oracle-backed regression and
explicitly opt-in `DOTRETICULUM_RNODE_PORT` hardware test cover the correction.
Only detect/version/platform/MCU queries were sent: no RF DATA, radio
configuration, EEPROM writes or firmware flashing. USB/firmware responsiveness
does not establish full radio startup, unplug/replug or over-air acceptance.

I2P simulator restart acceptance now exchanges packets in both directions across
persisted current/legacy bearer restarts, checks new child ownership and verifies
unchanged private destination generation. An unproven stale-session refresh
experiment was removed after it failed actual recovery; the source-backed
healthy session lifetime remains unchanged. The owned topology runner
canonicalizes reference executable/source paths before changing
working directories. Actual-router results and non-default SAM configuration
are recorded separately below; SAM readiness alone is never successful delivery.

The Linux UDP test previously assumed delivery order across separately created
sender sockets. It now requires all five distinct datagrams without imposing an
ordering contract UDP does not provide; exact reverse payload comparison remains.
No packet loss, retry or missing result is accepted.

Stable Windows validation:

* `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors. The initial integration run exposed and corrected a
  new Local test's incorrect pre-send counter expectation. An existing concurrent
  default-config test also encountered one Windows sharing violation; its focused
  rerun and the final complete suite passed without changing that unrelated code.
* `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj
  -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true
  -p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity6-recovery
  --nologo -v quiet`, with MSVC environment setup in the same process:
  passed; the current executable's `--help` passed.
* With `DOTRETICULUM_RNODE_PORT=COM8`,
  `pwsh -NoProfile -File tests\run-tests.ps1 -NativeExe
  artifacts\native-parity6-recovery\DotReticulum.Applications.exe -Coverage
  -ResultsDirectory TestResults\coverage-parity6-recovery-final-20261024-2231`:
  **1690 passed, 81 skipped, zero failed**. General live tests were disabled;
  the explicit hardware probe and published-native encrypted/compressed
  **2,200,017-byte** resource send/fetch ran. A prior live collection was stopped
  after an in-flight source update invalidated its mixed build graph; no results
  from that abandoned collection are used as acceptance or coverage.
* `dotnet test tests\DotReticulum.Interfaces.Tests\DotReticulum.Interfaces.Tests.csproj
  -c Release --no-build --filter FullyQualifiedName~LiveInteropTests
  --nologo -v quiet --logger trx --results-directory
  TestResults\parity6-recovery-live-desktop`, with the verified reference and
  `DOTRETICULUM_LIVE=1`: **7 passed**, actual Python UDP/TCP exchanges.
* `pwsh -NoProfile -File tests\run-pester.ps1 -Python <verified-python>
  -Upstream <verified-reference>` and the same command with `-Application
  artifacts\native-parity6-recovery\DotReticulum.Applications.exe`:
  **24 passed, 2 skipped each**. These execute separate UDP transfer, owned Pipe
  shutdown, shared election, Python RPC and daemon/client restart processes,
  plus explicit public-network permission and invalid RouterInfo seed rejection.
  The Linux-only abstract endpoint case explicitly skips on Windows.

Current Linux/WSL snapshot validation (same production changes):

* `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  zero warnings/errors.
* `dotnet test tests/DotReticulum.Interfaces.Tests/DotReticulum.Interfaces.Tests.csproj
  -c Release --no-build --nologo -v quiet --logger trx --results-directory
  TestResults/parity6-recovery-linux-bearers-final`:
  **316 passed, 14 skipped**, including Linux Backbone, Pipe and Local cases.
* `dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj
  -c Release -r linux-x64 -p:ILLinkTreatWarningsAsErrors=true
  -p:IlcTreatWarningsAsErrors=true -o artifacts/native-parity6-recovery
  --nologo -v quiet`, then current native `--help`: passed.
* `dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj
  -c Release --no-build --filter
  "FullyQualifiedName~NativeResourceTests|FullyQualifiedName~SharedLiveTests"
  --nologo -v quiet --logger trx --results-directory
  TestResults/parity6-recovery-linux-native`, with live reference and current
  native executable enabled: **7 passed**: the published-native resource flow
  and six managed actual Python shared/management-role cases over TCP.
* As root in the owned snapshot, `pwsh -NoProfile -File
  tests/Interop/invoke-auto-topology.ps1 -Dotnet <verified-dotnet>
  -Python .reference-venv/bin/python -Upstream .reference -MulticastSmoke`:
  **3 passed**, actual pinned Python raw/IFAC exchanges plus real IPv6 multicast.
  Owned veth devices/namespaces were cleaned up by the runner; evidence remains
  in `TestResults/auto-live-782dcdc5/auto-live.trx`.
* `pwsh -NoProfile -File tests/run-pester.ps1 -Application
  artifacts/native-parity6-recovery/DotReticulum.Applications
  -Python <verified-python> -Upstream <verified-reference>`:
  **26 passed, zero skipped/failed**. This additionally runs the current native
  executable as an abstract Unix owner and independent clients, checks that only
  the owner binds the physical listener, and confirms the interactive client's
  `LocalSharedPacketInterface` role. The existing portable Pester 5.7.1 module
  was explicitly made available through `PSModulePath`; no global Linux module
  installation or Android execution was implied.

Final restored-source production coverage (after removing the unsuccessful
I2P refresh experiment) merged six collections plus their six TRX attachment
copies by source line/branch identity, without double-counting:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 7848/9655 (81.28%) | 6764/10273 (65.84%) |
| Core | 1688/1741 (96.96%) | 1045/1136 (91.99%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4495/5220 (86.11%) | 2530/3225 (78.45%) |
| Lxmf | 388/430 (90.23%) | 193/272 (70.96%) |
| Transport | 4905/5374 (91.27%) | 3221/3940 (81.75%) |
| **Overall** | **19434/22536 (86.24%)** | **13800/18896 (73.03%)** |

#### Actual I2P recovery investigation remains open

The owned i2pd 2.59.0 reruns retain the original four-minute test deadline,
90-second setup limit and delivery assertions:

* `singlethread=false` excludes one source-observed delayed destination-deletion
  hazard, but the first actual rerun still failed native restart. Its two Python
  cases had invalid relative executable paths; they are not interoperability
  evidence. Artifacts: `i2p-topology-5fff97e37dd54df8bb867dbdd0c4acdc`.
* With canonicalized paths and the stale-session experiment, a fresh router had
  zero peer RouterInfos and reported no available inbound/outbound tunnel peers:
  **1 passed, 3 failed**, not a successful recovery run. Artifacts:
  `i2p-topology-ad76c16111504c57aee32163b58c1442`.
* The explicit public-only `-SeedRouterInfoDirectory` imported **172 signed
  public RouterInfos (189,707 bytes)** from the earlier owned packet-delivering
  run's `router/netDb`. No router keys, private destinations, lease sets, ready
  tunnels or fabricated peer state were copied. The router still validates the
  public files. With `-AllowPublicNetwork -RunStreamTests -NoBuild -KeepArtifacts`
  and this seed, persistence and both pinned Python roles passed:
  **3 passed, 1 failed**. Initial native bidirectional delivery passed, but
  replacement-server accept never arrived. The stale-session refresh ran and
  preserved its local identity without fixing delivery. Artifacts:
  `i2p-topology-01c8dbef554e443aaf83501347fc022f`.
* `-ReferenceRestartOnly` independently runs two actual pinned Python processes,
  not a Python proxy for .NET. The first diagnostic established callbacks in
  both directions but exposed an invalid persistence precondition: non-transport
  RNS uses an ephemeral active transport identity by default
  (`Transport.py:320-334`), selecting a different I2P key filename after restart.
  It failed before a valid same-address recovery comparison.
* The corrected diagnostic uses the supported, disclosed
  `static_transport_identity=Yes` on the reference server/replacement only.
  It checks active transport hash, persisted transport-file hash, I2P filename,
  private-file hash, single-file count and b32 equality after actual process
  death/restart. No reference source, key serialization or SAM options are
  patched. Initial callbacks passed both ways; all persistence assertions passed;
  the surviving reference initiator reported broken pipe/reconnect but no new
  accepted peer arrived before the unchanged deadline: **0 passed, 1 failed**.
  Artifacts: `i2p-topology-dc2d6f312a4d4edb96443ddb31280043`.

These failures do not establish a sole router cause or successful .NET recovery;
they do show that the pinned Python-to-Python topology also fails this recovery
criterion with the same owned backend. The user requested continuing with a
different I2P router implementation rather than closing the work as blocked.

The alternate backend is the official Java **I2P 2.14.0** distribution:

* The installer SHA-256 is
  `5593e5583a703cddbe738cff837026d9917b83491bd0cfbbeb2de0242b6c7990`.
  Its official detached PGP signature verified against download-page
  fingerprint `70D2060738BEF80523ACAFF7D75C03B39B5E14E1` in an isolated
  owned keyring. Matching package bytes alone were not treated as publisher
  authentication.
* The isolated Temurin **21.0.13+9** Linux x64 JRE archive's SHA-256 is
  `a535a1df9c6284d9618872984e026ae0352d1f7e9e0605425c0e6114f47351dd`,
  checked against official Adoptium release metadata.
* The authenticated IzPack 5.2.4 package's actual panel IDs supplied the
  automation XML documented in `docs/i2p-interface.md`. The unattended
  installer exited **0**, reported automated completion and installed all
  required assets in the isolated snapshot's `.i2p-tools/java-i2p-router`.
  Installer Java home/scratch and install target were owned directories;
  no service or global JRE/router installation was performed. An earlier
  unsupported console probe stopped on terminal job control and was killed
  by its specific owned PID; it is not installation evidence.
* The first foreground launch failed before SAM with a source-confirmed
  Java 2.14.0 Linux portable-mode context-initialization recursion. Removing
  only `i2p.dir.portableMode=true` uses the ordinary supported path; the
  already-created owned `router.config` prevents migration. The subsequent
  foreground process answered actual SAM 3.1 HELLO. OS inspection showed
  only owned loopback SAM and the chosen TCP/UDP transport port, not console,
  proxy or external I2CP listeners. Source citations and the prohibition on
  direct in-router delivery are in the interface documentation.

The Java runner reuses existing permission gates, explicit public-only
RouterInfo seeding, process ownership, deadlines and exact TRX assertions.
Eight early rejection cases additionally check public permission, every
required distribution asset and incompatible backend parameter sets.
`pwsh -NoProfile -File tests\run-pester.ps1 -Python <verified-python>
-Upstream <verified-reference>` and the same command with
`-Application artifacts\native-parity6-recovery\DotReticulum.Applications.exe`
each passed **32, skipped 2** on Windows. The corresponding Linux command
with `artifacts/native-parity6-recovery/DotReticulum.Applications` passed
**34, skipped 0**, including native abstract-socket election. These supersede
the earlier Pester counts after adding Java validation; they are not
actual Java stream/restart acceptance.

The alternate router's four-case run
`i2p-topology-ab6bdd4b82c84fe393e1ba0582315506` finished **2 passed,
2 failed, zero skipped** in 8m16s. SAM persistence and Python-as-initiator
bidirectional traffic passed. Python-as-server failed its initial mixed stream
before the four-minute deadline; no sole bootstrap cause was isolated.
The native pair subsequently delivered initial packets both ways at
03:15:57Z, reused the persisted server b32 after restart, and still failed
replacement acceptance at the original deadline. The surviving initiator
retained its one SAM session and repeatedly timed out during STREAM CONNECT.
The router's hard transit rejection therefore did not prevent its own client
tunnels or all packet delivery.

This run was compiled before removing the unsuccessful stale-session
experiment; its diagnostic counter remained zero on the failed native restart.
It is failed comparative evidence, not acceptance of the final restored source
or a successful recovery fix. The temporary refresh implementation and its
test were removed instead of committing an unverified divergence from source
session ownership. Bidirectional current/legacy restart simulator coverage
remains. The stopped Java router's private datadir was retired; bounded logs
and signed public RouterInfos were preserved separately as `router-logs`
and `public-netdb`, not a reusable private router identity.

The independent, unmodified pinned-Python comparison was then run on Java
using the **140 signed public RouterInfos** retained from that packet-delivering
Java run, not its private router state. With the same public permission,
`-JavaExecutable .i2p-tools/java-jre/bin/java -JavaRouterDirectory
.i2p-tools/java-i2p-router -ReferenceRestartOnly -NoBuild -KeepArtifacts`,
the restored current source and unchanged four-minute deadline, it finished
**0 passed, 1 failed**, zero skipped. Both initial callbacks passed at
03:24:02/03:24:03Z. The server was actually killed, and all supported static
transport identity, key hash, filename and b32 persistence assertions passed
by 03:24:17Z. The surviving Python initiator logged closed socket, broken
pipe and unresponsive/restart, but no replacement accepted peer arrived.
Artifacts: `i2p-topology-b722bc354d7b48f8912486ceed9b7a69`.
Thus the reference also fails this bounded restart criterion on **both**
backends. This does not identify a sole cause, authorize a guessed sleep or
larger acceptance deadline, or close the actual I2P recovery gate.

#### Final restored-source gates

Two resumed default-parallel Windows runs exposed different existing executable
fixture file-sharing races (`WindowsPeAllowedResolverRequiresAuthorizationAndOwnsFailureProcesses`
and `TimeoutKillsAnOwnedDescendantTree`); both focused cases passed without
unrelated production changes. The complete serial-collection run passed.
Final production coverage additionally used serial collections explicitly:

```powershell
$env:DOTRETICULUM_NATIVE_EXE = (Resolve-Path artifacts\native-parity6-recovery\DotReticulum.Applications.exe).Path
$env:DOTRETICULUM_RNODE_PORT = 'COM8'
dotnet test DotReticulum.slnx -c Release --no-build -m:1 --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults\coverage-parity6-final-restored-20261025 --nologo -v quiet --logger trx -- xUnit.MaxParallelThreads=1
pwsh -NoProfile -File tests\coverage_summary.ps1 TestResults\coverage-parity6-final-restored-20261025
```

Result: **1689 passed, 82 skipped, zero failed**, six production modules
present, overall/per-project coverage in the final table above. General live
tests were off; COM8 read-only identification and the current published-native
2,200,017-byte encrypted/compressed resource roundtrip were on. The count
decreased by one when the unsupported refresh experiment/test was removed.
No build or second collection overlapped these Windows output directories.

Both final `win-x64` and `linux-x64` NativeAOT publishes used
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true`;
both current executables ran `--help` and the representative resource flow.
Final Linux bearer execution exposed the same invalid cross-ephemeral-sender
UDP ordering assumption in the manager test, which was corrected to require
both distinct valid datagrams, irrespective of order, while still rejecting
the malformed datagram. This was test-only, not a production change.
`dotnet test tests/DotReticulum.Interfaces.Tests/DotReticulum.Interfaces.Tests.csproj
-c Release --nologo -v quiet` then passed **315, skipped 15** on Linux.
The final Linux published-resource selection passed **1, skipped 0**.
On Windows, `dotnet test tests\DotReticulum.Interfaces.Tests\DotReticulum.Interfaces.Tests.csproj
-c Release --filter FullyQualifiedName~UdpPacketInterfaceTests --nologo -v quiet`
passed **7, skipped 0**. Final builds were warning/error-free.
Published-native I2P execution, actual persisted-stream restart and the
explicitly retained hardware/Android acceptance gaps are still unverified;
Parity 6 is not marked complete.

### Parity 6 interface integration, partial (2026-10-24)

Parity 4 cleanup (`6da44c4`) and Parity 5 (`c992e74`) remain completed and
pushed; independent skill commit `3728dfd` is preserved. This is an intermediate
verified interface integration, **not completion of Parity 6**. Parity 7, 8
and repository CI/CD workflows have not been started. Required physical/mobile
acceptance cannot be supplied by this environment; additional software and
actual-router recovery gaps are explicitly retained below rather than called
hardware-only blockers.

The reference remains the authoritative .NET-fetched
`a29bfb51c468945c96a1ee22cae0114e993e2391`, RNS 1.5.7, isolated LXMF 1.2.1.
Actual Windows and Linux imports, clean tracked source and exact revision
were checked with `reference_environment.py`. Older `e40191...` provenance is
unchanged. New `GenerateParity6{Auto,BackbonePipe,I2p,Kiss,RNode,Stream,Udp,Weave}Vectors.py`
and `GenerateParity6OutgoingVectors.py` execute the pinned reference; their
fixtures retain imported paths, methods and raw source fingerprints.
Reference-side recording devices/default accessors and other bounded adapters
are disclosed in the individual documents, not described as physical devices.

Source maps, preimplementation observable criteria, intentional differences
and configuration/API boundaries:

| Contract | Source-backed documentation |
| --- | --- |
| IPv6 discovery/peers/expiry, duplicate handling | [Auto](../docs/auto-interface.md) |
| Linux/Android Backbone, scoped ingress/flap/egress state; subprocess ownership | [Backbone/Pipe](../docs/backbone-pipe.md) |
| KISS and AX.25 framing, TNC controls/flow/beacons | [KISS](../docs/kiss-interfaces.md) |
| RNode/Multi detection, nested radios, firmware/control/PHY/flow/reconnect | [RNode](../docs/rnode-interfaces.md) |
| SAM sessions, destination persistence, accepted/initiating peers, stream recovery | [I2P](../docs/i2p-interface.md) |
| Weave device/endpoint framing, queues and reconnect | [Weave](../docs/weave-interface.md) |
| UDP direction/address/port/device and truncation boundaries | [UDP](../docs/udp-interface.md) |
| TCP/serial configuration, framing, retry and ownership | [Streams](../docs/stream-interfaces.md) |
| OUT selection and actual DATA-queue pressure | [Interface policy](../docs/interface-policy.md) |
| Local sharing and Android USB/Bluetooth/service/platform gaps | [Platform audit](../docs/interface-platforms.md) |

All new bearer factories are wired through runtime configuration, including
RNodeMulti triple-nested radio sections. Backbone aliases follow the pinned
source, not the TCP-client parser. Discovery publishes real TCP/Backbone types
and actual bound ports; Linux/Android autoconnect uses native Backbone, other
platforms retain the explicitly compatible HDLC adapter. Default Auto creation
remains conservatively disabled pending explicit opt-in, no longer described
as unimplemented, and never rewrites existing user configuration.

Generic `outgoing=no` affects selection, not source-authorized known-path/raw
sends. Children inherit policy/IFAC/accounting except source-backed RNodeMulti
and I2P initiating-child exceptions. Backbone pressure feedback binds to the
router's actual bounded DATA queue, including rejected admission attempts;
state is scoped by manager identity, not process-global. The immediate
watermark preserves the source's 128-frame floor.

Concrete integration fixes include duplex TCP receive-enumeration ownership,
hostname address fallback within one configured deadline, and Unix UDP
`ReceiveMessageFromAsync` truncation detection. Actual Linux probes showed an
8192-byte datagram could disappear before delivery in this environment, so
the test now checks exact 1065/1129/1193-byte IFAC/truncation boundaries rather
than treating delivery of that oversized proxy as guaranteed.
Link-manager startup/stop now captures the token before worker scheduling and
keeps its CTS owned until workers join. A 32-iteration immediate-stop regression
passes. The 1024-receipt regression waits for the separate bounded outbound
queue without weakening successful-send/culling assertions. Coverage exposed
a telemetry test's mutable-list snapshot race and a multipart test polling
an intentionally 10-ms-lived status; snapshots are now locked copies and the
multipart test uses the existing controllable clock, retaining the exact
expiry/cancellation and exactly-once assertions.

I2P source tracing confirmed SAM sessions belong to tunnels, not individual
streams. Ready initiating sessions now survive normal stream retries while
control failure/`INVALID_ID` recreates them; independent simulator cases verify
stable session ID/public identity and genuine session recreation. Private
destination persistence was already stable; the correction preserves tunnel
readiness, not a previously regenerated identity. Upstream setup-error reset
differences and bounded native deadlines remain explicit.

**Windows verification.** Commands sharing outputs were serialized. An
intermediate opt-out suite passed **1675**, skipped **82**, failed zero.
The first full parallel-project live/coverage attempt failed five tests;
all six selected cases passed on focused retry. After the deterministic
test fixes above, the final full solution gate used `-m:1`, preserving protocol
assertions/deadlines while avoiding competing instrumented test projects.

```powershell
dotnet build DotReticulum.slnx -c Release --no-restore -m:1 -v:minimal
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 --no-restore -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity6
artifacts\native-parity6\DotReticulum.Applications.exe --help
dotnet test DotReticulum.slnx -c Release --no-restore -m:1 --settings tests\coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults\coverage-parity6-1376b1c553914ea4b7f6e985b7dc990d --logger trx -v:minimal
pwsh -NoProfile -File tests\coverage_summary.ps1 TestResults\coverage-parity6-1376b1c553914ea4b7f6e985b7dc990d
pwsh -NoProfile -File tests\run-pester.ps1 -GitHosting -GitRemote
pwsh -NoProfile -File tests\run-pester.ps1 -Application artifacts\native-parity6\DotReticulum.Applications.exe -GitHosting -GitRemote
```

Build: **zero warnings/errors**. Publication ran with MSVC setup and publish
in the same `cmd.exe` process; trimming/compiler warnings are errors. Help
executed. Full solution: **1742 passed, 24 explicitly skipped, zero failed**,
with `DOTRETICULUM_LIVE=1`, actual reference variables and
`DOTRETICULUM_NATIVE_EXE` set. Counts come from actual TRX outcomes, not an
absent skipped-counter attribute. Linux-only, physical/mobile and separately
opt-in router/multicast cases are not silently treated as passed. Managed
Pester: **110 passed, 2 skipped, zero failed**; published-native Pester:
**110 passed, 2 skipped, zero failed**, including authenticated file transfer
through independent UDP receive/forward ports, actual Pipe child startup and
graceful joined shutdown, and existing streamed/multipart/Git/runtime flows.

Fresh production coverage merged 12 reports without double-counting shared code:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 8026/9651 (83.16%) | 6897/10273 (67.14%) |
| Core | 1688/1741 (96.96%) | 1045/1136 (91.99%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 4396/5122 (85.83%) | 2492/3179 (78.39%) |
| Lxmf | 389/430 (90.47%) | 194/272 (71.32%) |
| Transport | 4935/5374 (91.83%) | 3247/3940 (82.41%) |
| Overall | 19544/22434 (87.12%) | 13922/18850 (73.86%) |

**Linux execution.** Used an isolated source snapshot under WSL Ubuntu
26.04.1, kernel 6.18.54.1, real SDK 10.0.402, PowerShell 7.6.8, isolated
Python 3.14.4/reference and real ICU/clang/zlib/bzip2 support. Windows outputs
were not copied. Missing application test imports initially caused a
`--no-restore` invocation to discover no tests: that no-op is **not evidence**.
`dotnet restore DotReticulum.slnx` supplied the missing Linux test dependencies,
and subsequent commands required actual test results.

```powershell
dotnet test tests/DotReticulum.Interfaces.Tests/DotReticulum.Interfaces.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Parity6|FullyQualifiedName~AutoInterfaceTests" --logger trx --results-directory TestResults/interfaces-final -v:minimal
dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ActualPythonBackboneAdvertisementAutoconnectsAndCarriesEncryptedLinkAfterBootstrapDetaches --logger trx --results-directory TestResults/backbone-live-canonical -v:minimal
pwsh -NoProfile -File tests/Interop/invoke-auto-topology.ps1 -Dotnet /home/dennis/.dotreticulum-dotnet/dotnet -Python .reference-venv/bin/python -Upstream .reference -Repository . -Build -TimeoutSeconds 300
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r linux-x64 --no-restore -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts/native-parity6
artifacts/native-parity6/DotReticulum.Applications --help
dotnet test tests/DotReticulum.Applications.Tests/DotReticulum.Applications.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NativeResourceTests|FullyQualifiedName~SharedLiveTests" --logger trx --results-directory TestResults/native-local-live-final -v:minimal
```

Commands ran through `wsl --exec env` with actual SDK/Python/PATH variables;
the Auto runner used root only for its owned namespaces/veth and scoped Git
safe-directory configuration. Linux bearer selection: **236 passed,
6 explicitly skipped, zero failed**. Actual pinned Python Backbone discovery,
bootstrap detachment and encrypted link flow: **1 passed, zero skipped/failed**.
Owned Auto namespace/veth topology: **2 passed, zero skipped/failed**,
`TestResults/auto-live-09ac0c56/auto-live.trx`; actual bidirectional raw/IFAC
packets, discovery, wrong-key rejection and expiry, not physical LAN evidence.
No existing NIC/routes were modified and owned namespaces were cleaned.
Linux warnings-as-errors NativeAOT publish/help passed. Final native/resource
and shared-reference selection: **7 passed, zero skipped/failed**; the published
native listener decrypts/decompresses and returns the exact compressed
**2,200,017-byte** resource. The six shared-reference cases use TCP explicitly:
they do not establish Linux abstract-Unix or Android interoperability.

Windows CRLF and Linux LF checkouts have different raw source hashes despite
the same clean pin. Auto, I2P and actual Backbone adapters now independently
compare normalized imported bytes with the pinned Git blob and report actual
raw, canonical-LF and Git-blob hashes separately. Native tests independently
hash the reported imported file. Original Windows raw-vector provenance is
not relabeled. For Backbone the original raw SHA-256 is `c3445132...150438`;
canonical/pinned-blob SHA-256 is
`b83f226ff847f140dabf782eac173177ba74c6a99bbc067c2a971fecdcf5e369`.

**Actual I2P results, not accepted recovery.** An owned extracted official
Ubuntu i2pd 2.59.0 process used private state, loopback SAM and bounded lifetime.
Isolated real SAM persistence passed **1** case; initial no-transport startup
failed explicitly and was corrected to an owned loopback NTCP2 transport,
private net ID 222, no seeds/SSU2/NTP/subscriptions. This is not overlay traffic
or independently verified physical isolation. A public-bootstrap run retained
certificate verification and used:

```powershell
pwsh -NoProfile -File tests/Interop/invoke-i2p-topology.ps1 -RouterBinary .i2p-tools/unpacked/usr/bin/i2pd -CertificatesDirectory .i2p-tools/unpacked/usr/share/i2pd/certificates -AllowPublicNetwork -RunStreamTests -PythonExecutable .reference-venv/bin/python -UpstreamPath .reference -NoBuild -KeepArtifacts -TestTimeoutSeconds 900
```

Latest result: **3 passed, 1 failed, zero skipped** in 7m51s,
`TestResults/i2p-topology-a5ab16658d8b490396c218fd8bd8cca3/results/i2p-live.trx`.
Real SAM persistence and both pinned Python roles pass. Initial native
bidirectional packets also pass, but after persisted server-bearer restart the
replacement accepted stream does not arrive within the unchanged four-minute
case deadline. Diagnostics show one retained initiating SAM session, eventual
disconnect and a second SAM CONNECT; SAM OK is not remote-handshake proof.
Source review of i2pd rules out a first-payload accept deadlock and found no
concrete native accept re-arm defect. Router logs include publication/tunnel
failures, but **do not prove an environmental root cause**. Actual recovery
remains failed/unresolved, with safe SYN/lease/accept-transition diagnosis
needed; simulator recovery is not a substitute. Earlier failed raw-hash
assertions were checkout-normalization mistakes, not moved pins.
The runner stopped its owned router/test processes; known PIDs and namespace
cleanup were checked. Private router/destination state remains ignored local
artifacts, never committed.

**Remaining acceptance and external prerequisites.** The Local/Android audit
finds native Local RX counts raw socket bytes versus upstream decoded bytes,
missing Android sleep/pause policy, missing explicit Android abstract-socket
selection, and no Android-targeted USB/RFCOMM/GATT/permission/service host.
Desktop shared TCP, simulated devices and Android-permitting socket guards
do not resolve those gaps. Required hardware tests need identified, owned
serial loopback/TNC, RNode/Multi/Weave devices and peers, firmware/chip details,
driver/port access, explicit configuration/transmission authority and lawful
frequency/bandwidth/power/SF/CR; Android acceptance additionally needs an
authorized Android host and USB/Bluetooth devices. The only enumerated
COM4 (`VID_239A`, `PID_8029`) is unidentified and was **not opened**.
No firmware, EEPROM, bonding or RF action was attempted. These physical/mobile
prerequisites cannot be manufactured from software tests; affected checkboxes
remain open. They are not presented as the cause of the separate I2P failure
or as completion of remaining software contracts. CI remains strictly last.

### Parity 5 endpoint and transfer implementation (2026-10-24)

Resumed the requested remaining-roadmap scope from `07c977d`; Parity 4's
documentation clarification was committed/pushed as `6da44c4`. The independent
repository-skill commit `3728dfd` was preserved. The resumed Release baseline
was warning-free with **1,350 passed, 65 explicitly skipped, zero failed**
before final integration tests were added. Builds, tests, coverage and native
publication sharing output directories were coordinator-serialized. Four
bounded implementation workstreams owned application values, endpoint/ratchet
state, resources, and Channel/Buffer; the coordinator integrated request/link/
runtime ownership and actual mixed-node/process acceptance.

The reference remains the authoritative .NET-fetched checkout at
`a29bfb51c468945c96a1ee22cae0114e993e2391`, RNS 1.5.7, with isolated LXMF
1.2.1. `reference_environment.py` verifies the actual imported paths, versions,
revision and clean tracked reference source. New `GenerateMsgPackApplicationVectors.py`,
`GenerateParity5EndpointVectors.py`, `GenerateParity5RequestVectors.py`,
`GenerateParity5ResourceVectors.py`, `GenerateParity5ChannelVectors.py` and
`GenerateParity5ChannelRetryCryptoVectors.py` execute that reference and retain
method/source provenance beside their tests. Older `e40191...` fixtures were
not relabeled. The existing explicitly `a29bfb51...` link-MTU generator/fixture
was extended for operational negotiation, not presented as older evidence.

| Pinned source | SHA-256 |
| --- | --- |
| `Identity.py` | `ac336bb2b7217278d3a962b6945212313fed0ee9cc10d666e8ac17faca17ea2f` |
| `Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |
| `vendor/umsgpack.py` | `0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50` |
| `Link.py` | `4267ab1c5b11d9c415693298376f2a984aa9bca13a6245e9bc3dce3236365a7c` |
| `Resource.py` | `9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699` |
| `Channel.py` | `5ab5cf0bfd2c0fd3574d53b405f48e08756c2105bba72d9bc530b652f985bdd7` |
| `Buffer.py` | `7f2269b25ada8e81b20f01692517e790bd3264dfdb461295570bcfd7b39b8db6` |

Acceptance was established from the endpoint/identity import, encryption,
announce, proof and ratchet branches; the vendor MessagePack numeric, extension,
timestamp, string/map and malformed-input branches; `Link.py` request/response,
identification, timing and MTU paths; `Resource.py` advertisement, metadata,
segmentation, hash-map, compression, proof, retry and cancellation paths; and
`Channel.py:113-148,175-245,303-544,547-608` /
`Buffer.py:44-98,101-198,201-294`. Detailed API boundaries, source references,
limits, acceptance mappings and intentional restrictions are in
[application values](../docs/application-values.md),
[endpoints](../docs/endpoints.md), [resources](../docs/resources.md) and
[Channel/Buffer](../docs/channel-buffer.md).

Implementation adds lossless bounded AOT-safe application values, signed
ratchet reload and detached selected-key metadata, endpoint direction/type/key
ownership, local recall and subscriber-independent authenticated delivery/proofs.
Dynamic announce defaults and tagged signed-payload caching retain context
flags; duplicate registration is explicit rather than replacing owned endpoint
state. Plain/group state and secrets have explicit lifecycle. Requests install
response limits before transmission, couple proof/response completion correctly
and settle once under cancellation, resource rejection and callback failure.
Lossless application-value requests/handlers are opt-in; legacy tool views keep
binary-key hexadecimal projection and diagnose projection collisions.

Link managers bound links, request/callback/outbound queues and worker slots.
Protocol state advances independently of optional observers. Noncooperative
synchronous application code cannot be forcibly stopped: shutdown diagnoses
remaining optional invocations after one second and retains their ownership/
occupied slot. Required resource cleanup is separate and fully joined, not
abandoned after that observer timeout. Owned stream cancellation callbacks run
off protocol locks; failures are diagnosed and cleanup joins their execution.
Caller streams and callbacks must cooperate; no bounded-shutdown claim is made
for arbitrarily blocked caller code.

Resources support seekable input streams, bounded forward-only disk proxies,
file-backed multipart reception and streamed file responses. rncp now sends
segment-by-segment and saves through `OpenRead()` rather than materializing a
spilled transfer. Ownership covers queued, rejected, cancelled, failed and
successful work; `LeaveOpen` remains caller ownership. Validation rejects
malformed metadata/advertisements/HMU/segments and excessive decompression.
Defaults include a 1-GiB logical limit, 64-MiB receive-memory threshold and
bounded parts/segments/queues/results. Upstream does not support arbitrary-offset
cross-link/restart resume; active-part retry and multipart continuation are the
supported contract, not an invented restart protocol.

Runtime resource storage is `<config>/storage/resources`, with startup and
15-minute bounded scavenging of strictly older-than-24-hour, exact owned
resource/input names in marked directories. Active native files hold sharing
leases. Unrelated names, other configurations, reparse targets and fresh files
are preserved. Library-only default storage remains `.reticulum/resources`.
Unix creation requests owner-only access; Windows inherits directory ACLs.
No Unix filesystem execution is claimed by Windows tests.

Operational link offers now follow the discovery-capable path bottleneck,
up to the native 524,288-byte frame ceiling. Non-discovery paths remain at
500-byte MTU / 431-byte MDU; larger signed-field vectors alone do not prove
operational acceptance. Actual mixed Python transport + .NET transport tests
exercise MTUs 1,024 and 16,384, 40-ms bridge RTT, selective Channel loss in
both directions, Buffer traffic, multi-megabyte file-backed multipart transfers,
retry exhaustion, teardown and fresh-link recovery. Direct pinned-peer tests
exercise both upgraded link roles and a streamed 2,200,037-byte response.
Other bottleneck placements, sequence wrap, RTT threshold windows, slow
consumers, callback races and malformed-input limits have deterministic tests;
this is not an all-MTU/bearer live sweep or prolonged production soak.

A preliminary synthetic-outlet retry assumption was corrected before commit.
Actual `Channel.py:561-564` calls `Packet.resend()`, `Packet.py:319-337`
repacks, and `Packet.py:221-224` encrypts Channel data again. Thus retries keep
the plaintext envelope/sequence but obtain fresh IV/ciphertext/hash/receipt.
The new retry-crypto generator executes the unmodified Channel/outlet/Packet/
Link/Token/Transport path with a synthetic installed session key and a capture
interface only; it does not establish handshake/network interoperability.
Its three-attempt fixture SHA-256 is
`59e2486a903a85a566dd539e522e4e547d53b44f3949a7fb06cb14edca46f9bd`.
Genuine RNG means regeneration changes sampled ciphertext, not the asserted
semantics. Native tests verify fresh hashes, sequence 65535-to-zero, latest
proof acceptance and stale-proof rejection. Establishment-rate accounting was
also corrected to count LR request/proof bytes only, not later packet proofs.

Strict malformed-key, authenticated-padding and RFC-HKDF boundaries remain
intentional differences, not compatibility regressions for valid traffic.
The signed upstream-format ratchet-file API leases cooperating native writers;
simultaneous Python/.NET writers are not supported. Versioned runtime ratchets
reload authenticated keys without replacing live policy or borrowing a disposed
caller identity/opening token. Missing/corrupt files fail explicitly and preserve
usable in-memory keys instead of upstream's missing-file reinitialization.

Focused acceptance completed before the final all-suite gate: Core **155 passed**;
Transport endpoint/request/resource/channel/lifecycle selection **171 passed,
4 skipped**; final request/retry selection **14 passed**; direct upgraded and
streamed-response live cases **3 passed**; mixed-transfer/rncp live selection
**11 passed**. Full managed Pester initially passed **107**, skipped **2**.
The last two broad runs preceded final retry-crypto integration and therefore
are not substitutes for the final gate.

`dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj
-c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity5 --nologo -v minimal`
completed warning-free using MSVC in the publishing process. The published
binary ran `--help` and the separate-process resource acceptance passed
**1 test, zero skipped/failed**: two isolated configurations each received
**73,400,357 bytes**, spilled above 64 MiB, saved identical hashes, removed
expired owned files and preserved unrelated/fresh/CWD files.

The first full live/native collection passed **1,462**, skipped **10** and failed
**3**; the runner correctly refused to call it a completed gate or proceed to
Pester. Discovery's live test registered the same destination again for its
encrypted-advertisement pass; it now reuses one owned endpoint with per-announce
data, preserving both directions and plain/encrypted assertions. Both mixed
transfer cases asserted an empty spool directory after disposal; the ownership
marker now correctly remains, and the assertion requires exactly that marker
and no other file. The thin Python observation callback now filters its text
message type before accessing `.text`, avoiding misleading callback exceptions
on valid Buffer messages without modifying upstream code.

`dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
-c Release --filter "FullyQualifiedName~DiscoveryLiveTests.PlainAndEncryptedAdvertisementsCrossRealTcpBothDirections|FullyQualifiedName~Parity5TransferLiveTests"
--nologo -v minimal` then passed **3**, skipped/failed **zero**, including
both complete impaired mixed-transfer cases with final retry behavior.
`pwsh -NoProfile -File tests\run-pester.ps1 -GitHosting -GitRemote` with the
verified reference environment passed **108**, skipped **2**, failed **zero**
in 127.56 seconds, including the new managed large-file storage case.

A second full collection passed **1,464**, skipped **10** and failed **1**:
the native initiator could finish before the Python responder's application
callback installed its current link, so an immediate harness `stat` command
observed `None`. Tests now await the actual Python `link_up` event as well as
native establishment in both roles, not a sleep, retry or weakened assertion.
`dotnet test tests\DotReticulum.Transport.Tests\DotReticulum.Transport.Tests.csproj
-c Release --filter "FullyQualifiedName~LiveLinkTests" --nologo -v minimal`
then passed **11**, skipped/failed **zero**, in 57 seconds.

Final full Release live/native collection:
`pwsh -NoProfile -File tests\run-tests.ps1 -Live -NativeExe
C:\Git\dennispayne\DotReticulum\artifacts\native-parity5\DotReticulum.Applications.exe
-Coverage -Pester -ResultsDirectory
C:\Git\dennispayne\DotReticulum\TestResults\coverage-parity5-accepted-37181771113d46f8a1f4430870b10106`.
All six projects built without warnings/errors and passed **1,465**,
explicitly skipped **10**, failed **zero**: Applications **612/8**, Core
**155/0**, Crypto **47/0**, Interfaces **81/0**, LXMF **35/0**, Transport
**535/2** (passed/skipped). Live and native opt-ins were enabled, including
the published encrypted/compressed 2,200,017-byte resource roundtrip.
Skips are explicit Windows/Unix/Backbone platform cases and the separately
opt-in real FFmpeg case, not hidden successful returns.

Fresh production-only OpenCover reports were merged by source line/branch
identity across projects, without averaging or double-counting assemblies:

| Project | Lines | Branches |
| --- | --- | --- |
| Applications | 7,838/9,441 (83.02%) | 6,736/10,061 (66.95%) |
| Core | 1,641/1,692 (96.99%) | 1,001/1,090 (91.83%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 794/903 (87.93%) | 329/420 (78.33%) |
| LXMF | 389/430 (90.47%) | 194/272 (71.32%) |
| Transport | 4,904/5,364 (91.42%) | 3,222/3,924 (82.11%) |
| **Overall** | **15,676/17,946 (87.35%)** | **11,529/15,817 (72.89%)** |

That combined command's native Pester tests all passed, but its container
correctly failed cleanup because a file-based C# fixture had launched a
persistent compiler holding an analyzer DLL under its owned scratch directory.
`GitRemote.Tests.ps1` now passes `dotnet run --disable-build-servers` for that
fixture rather than killing shared build servers or suppressing cleanup errors.
After this test-tooling-only correction, complete process suites were rerun:

* `pwsh -NoProfile -File tests\run-pester.ps1 -Application
  C:\Git\dennispayne\DotReticulum\artifacts\native-parity5\DotReticulum.Applications.exe
  -GitHosting -GitRemote`: **108 passed, 2 skipped, zero failed**, including
  container cleanup, in **97.89 seconds**.
* `pwsh -NoProfile -File tests\run-pester.ps1 -GitHosting -GitRemote`:
  **108 passed, 2 skipped, zero failed**, including cleanup, in **129.02 seconds**.

Pester skips are Unix SIGTERM execution and separately opt-in real FFmpeg.
Managed/native process coverage includes the new per-configuration 70-MiB
resource flow and existing Git/runtime/discovery/receiver-disposal regressions.
The source code was unchanged after native publication and successful full
coverage; later corrections were observation synchronization and fixture process
ownership, not production fallback behavior. Parity 5's defined acceptance is
complete on Windows x64. Linux/macOS/other platform execution, real interfaces/
hardware, utility gaps and repository workflows remain later roadmap work;
this milestone does not claim complete upstream or platform parity.

### Parity 4 status clarification and remaining-roadmap resume (2026-10-24)

Resumed clean `main` at `07c977d4f5f3376d824b209143de561f8a94ef36`,
also verified as `origin/main` using `git ls-remote origin refs/heads/main`.
`dotnet build DotReticulum.slnx -c Release` passed with zero warnings/errors;
`dotnet test DotReticulum.slnx -c Release --no-build` passed **1,196**,
explicitly skipped **62**, failed **zero** with live/native opt-ins disabled
(Applications 579/35, Core 107/0, Crypto 47/0, Interfaces 74/7, LXMF 27/8,
Transport 362/12, passed/skipped).

README now summarizes the completed Parity 4 scope alongside Parity 3, and the
plan explicitly labels link MTU discovery as staged. Neither clarification
changes behavior: operational offers remain 500 bytes, live MDU 431, and larger
signed-field vectors are not evidence of upgraded link/resource/channel payload
acceptance. That acceptance remains in Parity 5. No cleanup defect or unrelated
refactoring was introduced.

### Parity 4 transport and interface-control implementation (2026-10-23)

Resumed clean pushed `main` at `be71bb8`; no reference-pin change, push or
GitHub Actions work is authorized by this milestone. Fresh
`dotnet build DotReticulum.slnx -c Release` completed with **zero warnings/errors**;
`dotnet test DotReticulum.slnx -c Release --no-build` established **868 passed,
60 skipped, zero failed**. The discovery-reload and cancelled-receiver disposal
regressions remain part of the regression suite.

Implementation ownership separates the tightly coupled routing/proof/path/
announce state machine, IFAC/bearer controls, and receipt/resource-proof/link-MTU
contracts. The coordinator owns authenticated tunnels, runtime/persistence
integration, mixed-transport impairment and native-process acceptance.
All .NET builds/tests/coverage/publish commands are coordinator-serialized.

The reference is the previously verified .NET-fetched checkout, still
`a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7), with LXMF 1.2.1
in the isolated reference environment. New generators verify actual imports
through `tests\Interop\reference_environment.py`; older fixture provenance
is not changed.

Tunnel completion criteria were established from `Transport.py:2764-2874`
(176-byte signed synthesis and replacement decisions), `2467-2476`
(announcement-backed path association), `1015-1060` (expiry/timebase culling),
`462-500` (dormant restart) and `3879-3950` (existing serialization).
`GenerateParity4TunnelVectors.py` executes the pinned synthesis and restoration
methods; its fixture records source SHA-256
`333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c`.
The vector adapter freezes clock/random values, captures `Packet.send`, and
provides synthetic interface/path tables. It does not modify reference source
or establish live interoperability. Acceptance requires malformed/blocked
synthesis rejection, detached-path retention, authenticated rebind after
restart, path/timebase competition, expiry and bounded concurrent state.

The other acceptance contracts were established against the same pin:
`Transport.py:690-725,875-952,1450-1595,2222-2350,2610-2715,3278-3599`
and `Interface.py:391-432` for mode/path competition, failed-link recovery,
authenticated LRPROOF effects, scheduling, requests and announce controls;
`Packet.py:399-588` and `Transport.py:745-759,1358-1369,2576-2582,2748-2761,3053-3123,3194-3197`
for eligibility, receipt completion/timeouts and forced resource-proof cache
queries; `Resource.py:652-670,765-774` for proof-loss recovery;
`Link.py:148-212,306-310,366-432,512-514` for signed MTU fields and discovery;
`Reticulum.py:884-902,1221-1269`, `Transport.py:1272-1318,1689-1794`,
`Cryptography\HKDF.py:43-111`, `Interface.py:251-265` and
`TCPInterface.py:169-180,271-309,346-435` for IFAC, aliases, raw frame sizing
and owned TCP recovery.

| Pinned source | SHA-256 |
| --- | --- |
| `Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `Packet.py` | `f1b12a7068c6036e20439f87f8bfb6ad2dd92689c03be387e2bb697d46bdb075` |
| `Link.py` | `4267ab1c5b11d9c415693298376f2a984aa9bca13a6245e9bc3dce3236365a7c` |
| `Resource.py` | `9e4543ee19255ab7269d8861a2d977587319968b7986f4846d75ee7dba453699` |
| `Cryptography\HKDF.py` | `543479e703b6946876a593f1c7179de49fedd689597766cee4a4aa385ace25fa` |
| `Interfaces\Interface.py` | `70bf362bc54872e24c124e503d8c52c487886aa141f62faed739e46c396c689c` |
| `Interfaces\TCPInterface.py` | `d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d` |
| `Interfaces\UDPInterface.py` | `575d1137716139b238649ae5b35a0f66a6a25a8661bc919f5226942df79f4fa5` |
| `Interfaces\SerialInterface.py` | `676f7b038c2950b92ac71f29653cbbe35ba6513a53ca41742d43e19966e1ef1f` |

New independent generators are `GenerateParity4RoutingVectors.py`,
`GenerateParity4RoutingTrafficVectors.py`,
`GenerateParity4RoutingConfiguration.py`, `GenerateParity4IfacVectors.py`,
`GenerateParity4ReceiptLinkMtuVectors.py` and `GenerateParity4TunnelVectors.py`
beside their owning tests. They execute the verified Python implementation;
their fixtures retain the actual revision, imports and source hashes.
Evidence includes 17 path cases, 98 mode cases, eight rate cases, 15 routing
configuration cases, nine announce/nine path-request/five egress/five held-release
traffic steps, eight IFAC cases, 32 bitrate/MTU thresholds, 4096 receipt-eligibility
combinations, seven MTUs and twelve signed proofs. Fake clocks, random inputs,
synthetic interface/path tables and captured sends make vectors deterministic;
they are not claims of unmodified full-node execution. No tracked reference
source was changed. No new live reference-side shim was introduced.
The traffic generator makes the held-release thread synchronous and substitutes
the counter/burst/hold method sequence for `Transport.inbound`, so its evidence
is isolated controller behavior, not full inbound dispatch. IFAC long-frame
generation explicitly raises the reference packet MTU; it does not claim those
frames are accepted by the unconfigured 500-byte codec. Existing discovery
adapters retain deterministic stamp selection using real LXMF workblocks and
validation, not the CPU-count-sized multiprocessing scheduler. Existing
`rncp_win_shim.py` retains the Windows open-file move workaround; passing
rncp tests do not establish unmodified upstream Windows file retention.

`Parity4RoutingTests` and `Parity4RoutingTrafficTests` exercise gravity,
responsiveness, expiry/use refresh, the ingress/origin/egress mode matrix,
failed pending/forwarded link branches, authenticated and malformed LRPROOF,
detached validated links, throttled recovery, concurrent requester delivery,
engaged/inflight scheduling, slow-medium limits, held release, bandwidth caps,
penalties, queue replacement and all four saturated priority classes.
Controllers own their receive/transmit counters and bounded 48-sample windows;
configuration does not require an application delegate to become effective.
The shared-owner regression found during integration was fixed by retaining
physical requesters while searching local clients.

`Parity4ReceiptTests` verifies source eligibility, terminal metadata,
duplicate-packet receipts, the 1024-entry limit and source timeout calculations.
The old bad-signature regression assumed 60 seconds; the source's unknown-path
timeout is 774 seconds, so it now advances the actual timeout plus one second,
asserts failure and repeated-cull metadata, and bounds its completion wait.
`Parity4ReceiptCacheTests` verifies forced-only cache membership, malformed and
cross-link rejection, bounded expiry, real lost-proof query/replay, cancellation,
bearer binding and unchanged fractional cache age across restarts. Legacy
timestamp-less entries receive an import time once, not on every restart.
The existing atomic format accepts an optional fourth cached-packet timestamp.

`Parity4IfacTests`, `Parity4IfacConfigurationTests` and `Parity4BearerTests`
verify aliases/size units, derivation, authenticated/rejected frames,
long-mask legacy counter rollover, hardened key rejection, bitrate thresholds,
raw-versus-decoded MTU boundaries, reconnect limits/cancellation, disconnect
recovery and accepted-peer isolation. General HKDF remains RFC-length-limited;
the legacy expansion exception is isolated to IFAC. Packet parsing still rejects
oversized fixed-MTU decoded packets even when the raw frame permits IFAC overhead.

`Parity4TunnelTests` verifies the signed synthesis vector, malformed/blocked
input, correct bearer, cancellation, six competing-route restoration branches,
dormant restart, expired/invalid persisted state, concurrency and 1025-tunnel
saturation with explicit eviction diagnostics. The source's expired-current-route
restoration quirk is preserved and disclosed in `docs\transport-controls.md`.
`Parity4SnapshotCapacityTests` verifies the exact 65,536 combined cached-packet
bound, active-before-tunnel-before-proof snapshot priority and removal of dangling
tunnel references without modifying live state.
`Parity4DiscoveryControlsTests` additionally carries an authenticated announce
over the controlled compatible HDLC adapter, asserting actual discovered-bearer
mode, gravity, internal-announcement override and inherited ingress defaults.
This deliberately synthetic Backbone-compatible advertisement is not labeled
actual Python Backbone evidence.
Explicit management attachment/reload of disabled configuration entries also
resolves global and per-interface routing controls at attachment time, rather
than retaining inactive placeholder defaults. Two real TCP attach/reload cases
assert gravity, cap, rate and ingress overrides; disabled unsupported interfaces
remain parseable until explicitly activated.

The first full integration run exposed an operational MTU regression: advertising
upgraded frames caused Python to exceed the still-500-byte packet codec at a
resource-response boundary. Operational link offers/confirmations now cap at
**500 bytes**, live MDU **431**, while signed-field vectors retain larger values.
This stages discovery honestly; upgraded resource/channel payload acceptance
remains the explicit Parity 5 item, not a hidden claim or weakened assertion.
The existing blackhole resource-response regression then passed.

Live/native focused command (with the verified reference environment,
`DOTRETICULUM_LIVE=1`, `DOTRETICULUM_NATIVE_EXE` pointing at the published binary):

```powershell
dotnet test DotReticulum.slnx -c Release --filter "FullyQualifiedName~Parity4|FullyQualifiedName~NativeResourceTests|FullyQualifiedName~LiveMixedTransportTests|FullyQualifiedName~LiveLinkTransportTests|FullyQualifiedName~LiveLinkTests|FullyQualifiedName~LiveRncpRnxTests|FullyQualifiedName~LiveLxmdTests|FullyQualifiedName~LiveLxmfTests" --nologo -v quiet -m:1 --logger trx --results-directory TestResults\parity4-live-native -- 'xUnit.ParallelizeTestCollections=false'
```

Result: **349 passed, zero failed/skipped** (Applications 74, Core 15,
Interfaces 18, LXMF 7, Transport 235). This predates the final additional
discovery integration test, which separately passed all five cases.
`Parity4TransportLiveTests` uses actual Python and .NET transports, IFAC on the
inter-transport connection, a C# bounded HDLC proxy with **64 kbit/s and 20 ms
one-way latency**, one dropped announce, authenticated bidirectional encrypted
datagrams/proofs, tunnel synthesis and forced TCP disconnect/reconnect.
The reverse direction explicitly requests/recalls the originating identity before
encryption; assuming that recall without an announce was a test precondition bug.
`Parity4NativeTransportTests` runs the published daemon, persists real Python
transport tunnel paths, restarts the daemon while that transport reconnects,
and restores the two-hop leaf route without a new leaf announcement.
`NativeResourceTests.NativeListener_DecryptsDecompressesAndReturnsCompressedMultiMegabyteResource`
roundtrips **2,200,017 bytes**, asserting byte equality and compression in both
directions. Existing direct/multi-hop Python link/resource/channel, rncp/rnx,
LXMF and `lxmd` regressions also passed in that run.

Native publish and execution used one MSVC-initialized process, with the Visual
Studio Installer directory available to `vswhere`:

```powershell
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul && dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity4 --nologo -v minimal'
.\artifacts\native-parity4\DotReticulum.Applications.exe --help
pwsh -NoProfile -File tests\run-pester.ps1
pwsh -NoProfile -File tests\run-pester.ps1 -Application artifacts\native-parity4\DotReticulum.Applications.exe
```

Publish and help succeeded with no warnings. Each managed/native Pester run
passed **19**, failed **zero**, explicitly skipped **one** Windows-inapplicable
Unix SIGTERM test. Tests execute separate processes, authenticated multi-megabyte
rncp send/fetch, wrong IFAC credential rejection followed by corrected-alias
resource transfer, malformed/disconnected peer survival, shared-owner election,
actual Python RPC authentication and existing-client recovery after daemon restart.
Both Pester runs supplied the verified `DOTRETICULUM_PYTHON` and
`DOTRETICULUM_UPSTREAM` environment values.

After the disabled-entry integration correction, the focused two-case
`ExplicitAttachOfDisabledEntryResolvesGlobalAndConfiguredOperationalRouting`
selector passed, `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`
again completed with **zero warnings/errors**, and the same warnings-as-errors
NativeAOT publish/help commands were rerun successfully.

Final full Release verification and fresh merged production coverage used:

```powershell
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Live -NativeExe artifacts\native-parity4\DotReticulum.Applications.exe -ResultsDirectory TestResults\coverage-parity4-accepted-20261023
```

The verified reference checkout/venv were supplied through
`DOTRETICULUM_UPSTREAM` / `DOTRETICULUM_PYTHON`; `PYTHONPATH` pointed at that
checkout and `PYTHONUTF8=1`. The runner enabled live interop and native execution,
serialized projects with `-m:1`, disabled parallel test collections and collected
the complete suite with `tests\coverage.runsettings`. Result: **1252 passed,
8 explicitly skipped, zero failed**: Applications **606/8**, Core **107/0**,
Crypto **47/0**, Interfaces **81/0**, LXMF **35/0**, Transport **376/0**
(passed/skipped). No build, test, coverage or publish shared output directories
concurrently. Earlier integration failures were resolved before this run; the
one transient Git-release fixture setup failure did not recur in either full
serialized live run and no unrelated Git implementation change was made.

| Production project | Merged lines | Merged branches |
| --- | ---: | ---: |
| Applications | 7832/9420 (83.14%) | 6740/10061 (66.99%) |
| Core | 1168/1213 (96.29%) | 611/657 (93.00%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 794/903 (87.93%) | 326/420 (77.62%) |
| LXMF | 389/430 (90.47%) | 194/272 (71.32%) |
| Transport | 3848/4257 (90.39%) | 2567/3138 (81.80%) |
| **Overall** | **14141/16339 (86.55%)** | **10485/14598 (71.82%)** |

Six OpenCover reports were merged by source line/branch identity, counting shared
production code once, not averaging test-project percentages. Reports remain
ignored generated artifacts. Inspection of uncovered integration paths retains
explicit limitations rather than coverage-only assertions: exceptional discovery
startup/timeout/cleanup branches, synthesis queue saturation/interrupted-send
diagnostics and aggregate tunnel-path eviction are not all exercised by the live
topology. Deterministic tests cover cancellation, malformed imports and the exact
65,536 cached-snapshot bound; the live loss/reconnect/native restart flows are
separate evidence, not a claim of exhaustive impairment combinations.

The eight full-suite skips are existing Windows-inapplicable executable/link/
Unix-permission cases, actual Python Backbone's upstream OS restriction, and
the explicitly unavailable pinned FFmpeg conversion mode. They are diagnosed
test skips, not unsupported modes returning early as successes.
The published-native `tests\run-pester.ps1 -Application
artifacts\native-parity4\DotReticulum.Applications.exe` run was repeated after
that final coverage command: **19 passed, one explicit Unix-only skip, zero failed**.
All Parity 4 checklist items are supported by the evidence above; no Parity 5
implementation, reference-pin move, workflow creation or push was performed.

Final platform scope remains **Windows x64**. No serial/radio hardware, Unix
signal execution, Linux/macOS/ARM/RISC-V/FreeBSD/mobile validation, or impaired
UDP/serial multi-hop is inferred from loopback/HDLC vectors. Actual Python
Backbone is upstream-platform-restricted and remains explicitly skipped on
Windows; the compatible adapter is separate evidence. The new impairment test
does not establish arbitrary loss/latency distributions or impaired live Python
channel exhaustion. Broader endpoint/resource/channel acceptance and physical
interface/platform completeness remain in their existing later parity phases.

### Discovery advertisement lifecycle regression follow-up (2026-10-22)

Follow-up to Parity 3 commit `c4b0ba2`: reconcile named interface
attach/detach/reload with discovery advertisement registration, including the
current listener port, replacement metadata/name/endpoint, interval, location
provider, enablement and publication validation. Runtime documentation now
distinguishes per-interface mutation from global reconfiguration, replacement
validation from later initialization failure, and registration from scheduled
publication. The original milestone evidence below remains historical; it does
not verify this follow-up.

Removed stale shared-instance limitations from the rncp/rnx and Git utility
guides: these tools now use the common native runtime's election/attachment
contract, including its explicit cross-directory RPC-key requirement. Corrected
the Git helper's stale default network-config directory to the runtime's
`rnsd` configuration discovery order.

Four deterministic discovery regression cases decode signed outgoing
advertisements, verify replacement metadata and the actual bound port, and
exercise replacement intervals, disabled discovery/interfaces, the first dynamic
publisher and detach/reattach. An injected discovery clock avoids scheduler sleeps.
Publication requirements are validated before a reload removes the old interface.

Full-suite integration exposed a closely related detach race: a queued receiver
could enter an already-disposed TCP interface and incorrectly terminate the
manager. Receivers now check cancellation before entering the bearer and treat
disposal after owned cancellation as normal shutdown. A fifth new regression
case deterministically overlaps receive/disposal and verifies unrelated traffic
and later attachments. An investigation run also reproduced the previously
observed intermittent Windows statistics-file access-denied failure; no unrelated
statistics implementation was changed. The final full run below passed.

Validation on Windows x64:

- Baseline `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`
  and `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  **zero build warnings/errors; 863 passed, 60 skipped, zero failed**.
- `dotnet test DotReticulum.slnx -c Release --filter
  "FullyQualifiedName~PacketInterfaceManagerTests|FullyQualifiedName~RuntimeDiscoveryHostTests"
  --nologo -v quiet`: **34 passed, zero skipped/failed** after the detach fix.
- Final `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`
  and `dotnet test DotReticulum.slnx -c Release --no-build -m:1 --nologo
  -v quiet --logger trx --results-directory TestResults\discovery-reload-verified`:
  **zero build warnings/errors; 868 passed, 60 skipped, zero failed**.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj
  -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true
  -p:IlcTreatWarningsAsErrors=true -o artifacts\native-discovery-reload
  --no-restore --nologo -v quiet`: **passed**, with MSVC setup and publish in
  the same process. Published `--help` and `rnsd --version` both exited zero.
- With `DOTRETICULUM_NATIVE_EXE` set to that binary,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
  -c Release --no-build --filter "FullyQualifiedName~NativeResourceTests"
  --nologo -v quiet`: **one passed**, a **2,200,017-byte** encrypted,
  compressed resource roundtrip.
- `pwsh -NoProfile -File tests\run-pester.ps1 -Application
  artifacts\native-discovery-reload\DotReticulum.Applications.exe`:
  **16 passed, three skipped, zero failed**. This executes native daemon,
  configuration, persistence, shared-instance election and multi-megabyte rncp
  processes. Skips are POSIX SIGTERM and two Python-environment-dependent flows;
  the reference environment was not enabled for this follow-up.

No new wire/live interoperability, native discovery-reload process observation,
platform coverage or upstream-pin change is claimed. Discovery mutation is
verified through the managed host and real signed packet output; native feature
execution is limited to the flows listed above. The Parity 3 evidence remains
Windows x64-only, with later-phase interface/transport/platform limitations
unchanged. No phase checkbox or interop claim changed.

### Parity 3 runtime, configuration and operations (2026-10-21)

Started on clean `main` at `c5ac441`. The initial Release build was warning-free;
the initial full suite passed **613 tests, 50 skipped, zero failed**. Six bounded,
nonoverlapping implementation workstreams supplied configuration/logging,
shared-instance/RPC, persistence/ratchets, discovery, blackholes and lifecycle
settlement; the coordinator integrated host ownership, real router state,
interface mutation/statistics/extensions and all acceptance gates. No workflow,
Parity 4 implementation, reference-pin change or push was performed.

#### Reference and measurable contracts

The reference remains the previously .NET-fetched authoritative checkout
`a29bfb51c468945c96a1ee22cae0114e993e2391`, RNS 1.5.7, with LXMF 1.2.1 in
the isolated Python environment. Live adapters validate the imported module
paths and versions using `tests\Interop\reference_environment.py`. New
`GenerateRnsdConfiguration.py`, `GenerateSharedRuntimeVectors.py`,
`GenerateRuntimeStateVectors.py`, `GenerateDiscoveryVectors.py` and
`GenerateBlackholeVectors.py` execute that reference; they are not C#-derived
expected values. Their fixtures retain actual source-file provenance.

| Pinned source under `RNS` | SHA-256 |
| --- | --- |
| `Reticulum.py` | `c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7` |
| `Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `Identity.py` | `ac336bb2b7217278d3a962b6945212313fed0ee9cc10d666e8ac17faca17ea2f` |
| `Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |
| `Discovery.py` | `ae26445adf6325ed3e47f2b7c4b412060994d75ae583a58162849507ed7a9950` |
| `Link.py` | `4267ab1c5b11d9c415693298376f2a984aa9bca13a6245e9bc3dce3236365a7c` |
| `Interfaces\LocalInterface.py` | `b241095c0a528f0c9a88817429a9ce54c7adb5aa850ae08e29c9b84dd8fa4371` |
| `Interfaces\BackboneInterface.py` | `c3445132d89af04998c35b12e5101c8975d73599f62fe71b501b3d734c150438` |
| `Interfaces\TCPInterface.py` | `d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d` |
| `Utilities\rnsd.py` | `e8920d1acde17e09b0aad57651348e86aabc84fbb29a531c340b7365d220e748` |

Acceptance was based on externally observable contracts, not module names:

| Area and source | Implemented and exercised contract |
| --- | --- |
| Configuration: `Reticulum.py`, vendored ConfigObj, `Utilities\rnsd.py`, TCP/UDP constructors | Discovery/default creation, explicit directory precedence, nested/list/quoted settings, omitted enable=false and alias OR, source mode/port precedence, static/network identities, all logging levels/timestamps/rotation, version/service and bounded interactive operations. Unsupported settings fail explicitly. |
| Shared owner/client: `LocalInterface.py`, Reticulum RPC handlers, Python multiprocessing connection framing | Election occurs before physical factories; required-shared fails rather than silently becoming standalone. Reference HDLC and bounded mutual-HMAC MessagePack RPC interoperate in both roles. Wrong keys, malformed framing, competing owners, remote identity allowlisting, owner restart/reconnect, automatic path-response reannouncements and encrypted requests are exercised. |
| Use/retention and persistence: `Identity.py:101-335,410-513`, `Destination.py:211-241,437-500`, `Transport.py:3788-3950` | Exact binary interchange schemas and fractional timestamps; retained use returns false, unretain records current use. Atomic identities/public-state generations, signed opt-in private ratchets, expiry/corruption/cancellation/concurrency and actual configured-path restart are tested. Nonempty unsupported runtime tunnels are rejected rather than fabricated. |
| Ownership/operations: Reticulum attach/detach/reload, cleanup/persistence loops, Transport and Link teardown | Serialized interface mutation, child isolation, bounded queue/backpressure, peer/global counters, observer diagnostics, scoped cache cleanup and supervised maintenance. Startup failures release reserved listeners without overwriting corruption. Terminal nodes automatically clean up faulted routers and settle links, requests, resources, channels and blocked streams; directly owned routers remain explicitly restartable. |
| Discovery: `Discovery.py` and Backbone source | Integer-key metadata/encryption/stamps, allowed sources, transport/implementation eligibility, duplicate/capacity rules, persisted available/unknown/stale/removal policy and bounded background work. Actual TCP advertisements retain their TCP type; only eligible Backbone advertisements autoconnect. Native HDLC bootstrap detach/restore and encrypted request recovery are exercised. |
| Blackholes: Reticulum/Transport publication, update and rejection handlers | Local precedence, expiry/reason/claimed source/authenticated provenance, source authorization, failed-source preservation, identified `/list` publication with binary map keys and persistence. Updates remove paths and reject/close identified blocked endpoints/links. Actual Python publication/update works in both roles. |
| Extensions/statistics: Reticulum external-interface loader and source bearer counters | Statically registered AOT-safe factories declare their accepted settings. Actual peer/parent totals avoid double-counting and retain departed-peer traffic; HDLC RX is decoded and TX is framed. Unknown PHY/device/IFAC/priority metrics are null/absent. Nominal bitrates are labeled estimates; opt-in .NET process profiling is not represented as Python profiling. |

`docs\runtime-operations.md`, `docs\runtime-state.md` and `docs\rnsd.md`
describe ownership, formats, defaults and intentional native differences.
The default-generated AutoInterface remains disabled with a diagnostic until
Parity 6 supplies that bearer. IFAC, gravity/internal-announcement controls,
full Backbone behavior and tunnel synthesis/restoration remain explicitly
unsupported later-phase work, not hidden runtime functionality. Tunnel
**serialization, validation and expiry** are covered here; actual tunnel
protocol integration remains Parity 4.

Private ratchet persistence is destination opt-in, like upstream's
`enable_ratchets(path)`, and saves signed candidate state before committing
rotation or policy changes. A failed save preserves the old ring and zeros
the candidate. Its stronger signed metadata and the single checksummed public
state generation are documented .NET formats, not claims of direct upstream
file compatibility. Windows ACLs remain operator-owned; Unix owner-only modes
are implemented but not newly exercised on this Windows host.

#### Integration failure investigations

Earlier broad concurrent runs exposed intermittent existing Git initialization/
publication and statistics-flush failures. Exact response assertions were kept
and enhanced with credential-safe diagnostic context. A temporary first-chance
I/O observer then reproduced the HTTP-mirror failure under coverage:
`System.IO.FileSystem.MoveDirectory`, HRESULT **0x80070005**, after successful
Git work. This was not evidence of the hypothesized process/job-assignment
failure; the process runner was not changed. The temporary observer was removed.

The runtime store's existing bounded Win32 sharing/access/lock retry policy was
extracted into `FileSystemSharingRetry` and reused for owned Git-directory
publication. Only Windows errors 5/32/33 are retried, at most eight times with
cancellable delays capped at 200 ms; persistent failures retain the original
exception, cancellation does not publish and destination overwrite is not
introduced. Regression tests exercise a real exclusive Windows file lease,
finite attempts, unchanged nonsharing failures and pre-cancellation. The
instrumented affected state/Git/statistics selector then passed **81 tests,
2 platform skips**. Statistics diagnostics remain explicit; no speculative
statistics or process-runner behavior was changed.

#### Final acceptance commands and results

All commands below ran locally; generated reports remain ignored artifacts.
The reference variables used the same checkout and isolated venv as the preceding
milestone: `DOTRETICULUM_UPSTREAM=<verified hosted checkout>`,
`DOTRETICULUM_PYTHON=<refvenv>\Scripts\python.exe`,
`PYTHONPATH=$env:DOTRETICULUM_UPSTREAM` and `PYTHONUTF8=1`.

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`:
  **passed, zero warnings/errors**.
- `dotnet test DotReticulum.slnx -c Release --no-build -m:1 --nologo -v quiet
  --logger trx --results-directory TestResults\parity3-release-verified`:
  **863 passed, 60 skipped, zero failed**. One MSBuild project worker avoids
  overlapping project hosts; normal xUnit collection concurrency remains enabled.
  Applications 507/33 skipped, Core 91, Crypto 47, Interfaces 56/7 skipped,
  LXMF 27/8 skipped and Transport 135/12 skipped.
- `pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -ResultsDirectory
  TestResults\coverage-parity3-release-20261021`: **863 passed, 60 skipped,
  zero failed**; live/native opt-in tests disabled in this collection. The
  runner invoked the production-only Coverlet settings and
  `tests\coverage_summary.ps1`, merging all six reports by source-line/branch
  identity without double-counting or averaging. No overlapping build/test
  wrote these outputs.
- With `DOTRETICULUM_LIVE=1`,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
  -c Release --filter "FullyQualifiedName~SharedLiveTests|FullyQualifiedName~BlackholeLiveTests|FullyQualifiedName~DiscoveryLiveTests|FullyQualifiedName~LiveTransportTests|FullyQualifiedName~LiveMixedTransportTests|FullyQualifiedName~LiveLinkTransportTests|FullyQualifiedName~LiveRncpRnxTests"`:
  **19 passed, one explicit Windows Backbone skip**. This includes actual
  Python shared clients/owners and authentication, remote management in both
  roles, plain/encrypted discovery, blackhole publication/update and existing
  mixed Python transport, link, rncp and authorized rnx regression flows.
- With the same reference/live environment,
  `dotnet test tests\DotReticulum.Transport.Tests\DotReticulum.Transport.Tests.csproj
  -c Release --filter "FullyQualifiedName~LiveLinkTests|FullyQualifiedName~LiveAnnounceTests"`:
  **14 passed, zero skipped/failed**, including direct live announces,
  links/resources and request/response. These live runs preceded only the
  bounded filesystem-publication extraction, not any further network change.
- `pwsh -NoProfile -File tests\run-pester.ps1` with the reference environment:
  **18 passed, one Windows SIGTERM skip, zero failed**, separate managed CLI
  processes. Election, RPC authentication, ownership, daemon restart, interactive
  EOF, configuration, identity/state persistence and multi-megabyte rncp ran.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj
  -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true
  -p:IlcTreatWarningsAsErrors=true -o artifacts\native-parity3 --no-restore
  --nologo -v quiet`: **passed, zero warnings/errors**, after the final
  publication fix. MSVC `vcvars64.bat` and publish ran in the same `cmd` process,
  with `vswhere` discoverable.
- `artifacts\native-parity3\DotReticulum.Applications.exe --help` and
  `artifacts\native-parity3\DotReticulum.Applications.exe rnsd --version`:
  **both exited zero**. Version identifies the experimental native build
  separately from reference RNS 1.5.7. An initial root `--version` invocation
  exited 2 because the root dispatches to `rnid`; the applicable `rnsd` option,
  not a changed root-utility contract, is the verified version command.
- With `DOTRETICULUM_NATIVE_EXE` pointing to that final binary,
  `dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj
  -c Release --no-build --filter "FullyQualifiedName~NativeResourceTests"
  --nologo -v quiet`: **one passed**, a **2,200,017-byte** encrypted resource
  roundtrip with compression asserted in both directions.
- `pwsh -NoProfile -File tests\run-pester.ps1 -Application
  artifacts\native-parity3\DotReticulum.Applications.exe -GitHosting` with
  the reference environment: **55 passed, two explicit skips, zero failed**.
  In addition to the complete runtime process suite, this exercises native
  repository creation/publication, restart, ordinary Git clone/push/fetch,
  work/releases, signing and bounded presentation helpers. Skips are POSIX
  SIGTERM and the separately provisioned optional FFmpeg backend.

The earlier coverage runs named `coverage-parity3-acceptance-20261021` and
`coverage-parity3-verified-20261021` failed on the diagnosed directory rename
and are **not** acceptance passes. The final fresh all-green generation above
supersedes them and the earlier 857-test coverage generation.

| Production project | Covered lines | Covered branches |
| --- | ---: | ---: |
| Applications | 7384/9147 (80.73%) | 6406/9865 (64.94%) |
| Core | 968/1011 (95.75%) | 488/529 (92.25%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 653/747 (87.42%) | 220/284 (77.46%) |
| LXMF | 387/429 (90.21%) | 192/270 (71.11%) |
| Transport | 2888/3281 (88.02%) | 1711/2205 (77.60%) |
| **Overall** | **12390/14731 (84.11%)** | **9064/13203 (68.65%)** |

#### Platform and evidence boundaries

All new platform execution here is **Windows x64**. Actual Python Backbone
construction is explicitly skipped because the pinned source supports only
Linux/Android; native HDLC-adapter integration does not substitute for that
live claim. Unix abstract sockets, POSIX SIGTERM persistence, Linux/macOS,
serial/radio hardware and other upstream platforms remain unverified here.
Anonymous datagrams cannot reveal a sender identity and are not claimed to
support identity-based blackhole sender rejection.

Discovery fixtures/live adapters disclose a deterministic stamp-selection
shim that uses actual LXMF workblocks and validation instead of the upstream
CPU-count-sized multiprocessing scheduler. This verifies payload/stamp
acceptance, not that scheduler. The existing rncp Windows reference-side move
shim remains disclosed; no new pin or old-vector relabeling was performed.

### Parity 2 push-readiness regression fixes (2026-10-20)

Resolved two nondeterministic Windows test failures found during push-readiness
verification. Concurrent `RngitClientConfiguration.LoadOrCreateIdentity`
callers could contend while opening the newly atomically published identity;
the `File.OpenRead` sharing violation occurred at that read, not while writing.
Client and host identity loading now share one process-wide gate around
first-publication and validation, while retaining unique staging files and
no-overwrite atomic publication. Existing corrupt-file rejection and Unix
owner-only creation behavior are unchanged.

The `rnsd` relay test no longer assumes a fixed 300 ms is enough for both
accepted TCP children to be adopted by the router. It waits for an actual
announce to cross from the second client to the first, using fresh probe
destinations until that round-trip readiness evidence exists, then verifies
the original announce relay and graceful shutdown.

Verification after the fixes:

- `dotnet build DotReticulum.slnx -c Release --nologo -v quiet`: passed, zero
  warnings/errors.
- `dotnet test DotReticulum.slnx -c Release --no-build --nologo -v quiet`:
  **613 passed, 50 skipped, 0 failed** (Applications 310/23 skipped;
  Core 77; Crypto 47; Interfaces 55/7 skipped; LXMF 27/8 skipped;
  Transport 97/12 skipped).
- `ConcurrentCreatorsConvergeOnOneCompleteIdentity`: **70/70** repeated
  passes, including 20 concurrent repetitions alongside the relay regression.
- `Run_RelaysAnnouncesBetweenClients_ShutsDownGracefully_AndPersistsIdentity`:
  **50/50** repeated passes, including 20 concurrent repetitions alongside
  the identity regression.
- `dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj
  -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true
  -p:IlcTreatWarningsAsErrors=true`: NativeAOT succeeded with zero
  warnings/errors; the published executable's `--help` smoke test passed.
- `pwsh -NoProfile -File tests\run-pester.ps1 -Application <published exe>
  -GitHosting`: **29 passed, 18 skipped, 0 failed**. Skips require the
  separately provisioned pinned Python reference or optional FFmpeg backend.

The recorded Parity 2 live Python/native interop, full hosting Pester,
coverage and feature-flow evidence above remains unchanged; no new live
reference run was needed for these synchronization and test-readiness changes.

### Parity 2 repository hosting, collaboration, presentation and signing (2026-10-19)

This milestone supersedes the **missing hosting, administration, lifecycle,
release, work-document, page/media/template and signing** classifications in
the historical Parity 1 inventory below. The completed read-only gate,
Parity 1 inventory and `7c81563` read/write helper milestone remain intact.
Implementation is confined to Parity 2: no reference-pin update, public
repository mutation, personal key/trust-store modification, policy change,
Parity 3 work or GitHub Actions workflow was performed.

#### Source, acceptance and ownership

The authoritative, previously .NET-fetched checkout remains
`a29bfb51c468945c96a1ee22cae0114e993e2391`, tree
`24d4c393de9fd57b933836cea68891fd953ab060`. Its tracked worktree is clean.
`reference_environment.verify_reference(require_lxmf=True)` confirmed RNS
1.5.7 imported from that checkout's `RNS\__init__.py` and LXMF 1.2.1 from the
isolated `refvenv\Lib\site-packages\LXMF\__init__.py`. The checkout and venv are
the session-owned paths recorded in the preceding milestone, not installed
system substitutes. Original `e40191b3...` fixtures retain their provenance.

| Source file under `RNS\Utilities` | SHA-256 |
| --- | --- |
| `rngit\server.py` | `6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693` |
| `rngit\client.py` | `78358331ddac087860fb8a1f350632d3875917782dc000681d48597affd8936b` |
| `rngit\pages.py` | `d165d8ac8d3cfdd1b42929a2512e9d5d03a1cef726124deca5218d7522a89c04` |
| `rngit\media.py` | `55fadeaee76d0a79c30c54d1d79122174cb6a956f2278013e97c4fd015fd8e9c` |
| `rngit\commitsigs.py` | `f4a05c0db1099d60a032f57e40b457edfdd60e30248ae5c4c25f0542bbbaa0fc` |
| `rnid.py` | `faee0d8acb4263f10812fa378085f2653b2453f584b4a33f2018c7797885f8c8` |

Seven bounded fleet deliverables owned configuration/statistics,
repository storage/permissions/lifecycle, releases, work documents, pages,
process/templates/media and signing. The coordinator retained host
composition, CLI dispatch, authorization integration, native RNS transfers,
lifecycle ownership, shared-output validation and commits. Agents did not
launch nested fleets or commit independent changes.

| Pinned contract | Observable acceptance and implementation boundary |
| --- | --- |
| `server.py:1990-2260,4641-5081`: defaults, identity, configuration, statistics | `RngitHostConfiguration` and `RngitHostStatistics`: raw stable identity, aliases, blocked sets, malformed/unwritable/corrupt state, detached counters, ignore rules, persisted byte-format statistics, periodic flush/restart. Reference summary vectors do not substitute for live authorization tests |
| `server.py:2262-2371,2869-3140`: permission inheritance, discovery and Git handlers | `RngitRepositoryStore` plus owned `RngitHost`: static/authorized executable `.allowed`, admin/get/set, hidden-versus-denied responses, ordinary Git clone/new-object push/Unicode refs, shared repository leases and bounds. Authorization precedes protected state disclosure |
| Repository lifecycle handlers and inventory source anchors | Create/fork/mirror/manual and periodic sync, persisted source metadata, failed/cancelled clone cleanup, mirror default-HEAD restoration versus fork HEAD preservation. Actual native RNS transfers against the pinned Python node and owned dumb-HTTP Git synchronization; no Python fetching on the product's behalf |
| `server.py:878-1148,3367-3790`; `rnid.py:413-510,588-601` | Draft/artifact/finalize/latest/delete, persisted partial steps, permissions, local packaging, independent manifests/signatures, fetch/offline verification, signer mismatch and tampering. Imported Git object consistency, signature validity and trusted publisher identity remain separate |
| `server.py:1346-1841,3789-4440`: work clients/handlers | All scopes, signed create/propose/edit/comment/update, complete/activate/delete/perms, comments/IDs, inheritance, corruption/concurrency/restart. Client trims content before signing, matching `_edit_work_content` at 1843-1881 |
| `pages.py:250-314,366-3007`, `util.py:130-818`, `highlight.py` | Page/download byte differentials, trees/blobs/refs/history/commits/stats/releases/work, pagination, binary/Unicode/escaping, anonymous/identified/denied access, templates and request-local generation timing. Native process acceptance separately exercises real page/download resources |
| `media.py:53-215`; templates/editors at `pages.py:269-314`, `server.py:1888+` | Backend discovery/arguments/absence/failure/timeout, actual FFmpeg/WebP conversion, bounded output/input/process lifetimes, explicit execution authorization and owned temporary cleanup |
| `commitsigs.py:48-331`; `rnid.py:413-488` | `rngcs`: binary stdin/file signing, SSHSIG/RSG parsing, principal discovery, structure versus crypto, author/tagger identity, wrong principal/tamper/malformed inputs. Real Git commit/tag signing and actual upstream verification in both directions |
| `server.py:139-285`; helper executable integration | Actual utility filename discovery, applicable option dispatch/arity, version, source CLI exit differential, service logging, confirmation, explicit editor execution. Syntax exit 2, client failure 1, fatal node initialization 255; errors deliberately use stderr |

`RngitHost` owns a bounded request queue, workers, active links, destinations,
services and shutdown. Deferred `Link.Respond` permits async bounded processing
without blocking the transport dispatcher. A deterministic test fills the
running/queued slots, verifies explicit busy rejection and settles both
pending requests on disposal without leaked clone state. Optional diagnostics
cannot disable processing. Blocked peers are disconnected without disabling
unrelated service; a closed-link client error explicitly warns that an
in-flight mutation's remote outcome can be uncertain.

#### Independent reference generation and disclosed adaptations

All new generators execute the actual pinned Python implementation and retain
source provenance. C#/PowerShell prepare owned inputs and assert outputs.
Handler snapshots, loopback tests and live mixed-node evidence are distinct.

Reproduction uses `$python`/`$reference` from the preceding record, with
`DOTRETICULUM_UPSTREAM` and `PYTHONPATH` set to `$reference` (and
`PYTHONUTF8=1` for Unicode vectors). Each work directory must be fresh/owned:

```powershell
& $python tests\DotReticulum.Applications.Tests\GenerateRngitHostVectors.py --work-directory .rngit-host-vector-work --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-host.json
& $python tests\DotReticulum.Applications.Tests\GenerateRngitRepositoryStoreVectors.py --directory TestResults\store-vectors --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-repository-store.json
& $python tests\DotReticulum.Applications.Tests\GenerateRngitWorkVectors.py --work-directory TestResults\rngit-work-vectors --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-work.json
& $python tests\DotReticulum.Applications.Tests\GenerateRngitPageVectors.py --work-directory tests\.page-reference-timing --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-pages.json
& $python tests\DotReticulum.Applications.Tests\GenerateRngcsVectors.py --output tests\DotReticulum.Applications.Tests\Fixtures\rngcs.json
dotnet build tests\Fixtures\RngitPresentationBackend\RngitPresentationBackend.csproj -c Release -v quiet
& $python tests\DotReticulum.Applications.Tests\GenerateRngitMediaTemplatesVectors.py --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-media-templates.json --work-directory TestResults\media-template-vectors --encoder TestResults\media-backend\ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe --source-image assets\branding\dotreticulum.png --template-executable tests\Fixtures\RngitPresentationBackend\bin\Release\net10.0\RngitPresentationBackend.exe --editor C:\Windows\System32\sort.exe
```

Store generation uses the owned public/readonly/hidden bare fixture directories.
Release generation is driven by the C# test that creates requests/repositories:
set `DOTRETICULUM_GENERATE_RELEASE=1`, `DOTRETICULUM_PYTHON=$python`, then run
`dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --filter FullyQualifiedName~RngitReleaseTests.PinnedHandlerVectors`.
Unset that opt-in and rebuild afterward to copy the retained generated fixture.

Reference adaptations are explicit, not claims of unmodified Windows behavior:

- Host vectors supply constructor state without listeners/discovery, force
  summary authorization true, fix day/clock and join recording threads.
  They remove the initial stats file before upstream's Windows-failing
  replacement rename and use one repository to avoid upstream shared defaults.
- Store vectors use original handlers on explicitly initialized nodes.
  Windows static `.allowed` vectors override only `X_OK`; replacement vectors
  remove an existing file before upstream `os.rename`. Native overwrite and
  actual authorization are covered separately.
- Work vectors fix `server.time.time` to `1700000000.25`, restoring it afterward.
  Forty-nine responses and five binary snapshots use original permission and
  signature logic. Textual allowed-file newline identity is not claimed.
- Page vectors initialize handlers without listeners, fix the stats clock,
  disable ambient Git overrides and use `core.quotePath=false`.
  Nondeterministic timing footers are normalized only after native timing-shape
  assertions. Optional Pygments and wcwidth are absent in the verified venv.
- Media static-template vectors override Windows `X_OK`; argument/discovery
  vectors mock executable discovery only. Actual FFmpeg conversion uses the
  owned encoder and temp directory. Executable templates invoke the owned C#
  fixture; the upstream editor vector uses native `sort.exe`.
- Signing has no crypto/source shim. Generation fixes CLI argv[0] and
  normalizes owned-root paths; signature-text comparison alone normalizes
  Windows stdout CRLF. Git subprocess configuration is process-local,
  with empty owned global/system configuration and explicit autocrlf policy.
- The actual `server.ReticulumGitClient` live adapter closes the received file
  immediately before its original retention/move callback on Windows.
  Unmodified upstream otherwise attempts to unlink an open file (WinError 32).
  Requests, handlers, signatures and network/resource code remain actual
  upstream; unmodified Windows file retention is **not** established.

#### Integration evidence and bounded differences

Actual native clients exercise work/releases and RNS fork/mirror/sync against
the pinned Python server. The actual pinned Python management client exercises
listing/fetch, create, permissions and signed work/releases against managed and
published native hosts. Ordinary Git exercises the helper against both server
implementations, including genuinely new pushed objects. Native-host
acceptance additionally requests thirteen page routes, three downloads,
media conversion, templates/timing, concurrent mutations and blocked peers.
Pester uses separate real CLI/Git/editor/backend processes, stable persisted
identity and restart state; no public upstream is mutated.

The first integrated gate (before the final provenance/credential/Windows
resolver refinements) ran `tests\run-tests.ps1 -Live -Python $python -Upstream
$reference -NativeExe $native -Coverage -Pester` into the fresh
`coverage-parity2-closure-1` directory: **654 xUnit passed / 5 Windows-specific
skips**, **95 native Pester passed**, merged production coverage
**83.57% lines / 67.13% branches**. This is intermediate evidence, not the
final acceptance of subsequent refinements.

The final source gate includes credential-safe lifecycle diagnostics,
credential-safe fork/mirror provenance, native known-destination wiring,
populated work-page whitespace correction, actual Windows PE `.allowed`
execution, root/direct help and explicit subprocess-fixture build dependency.
An invalid PE header never selects executable permission resolution; a valid
PE requires explicit authorization. Diagnostics retain operation, repository,
exception type and Git exit code without emitting raw credential-bearing
arguments/stderr. Persisted source URLs are unchanged; displayed userinfo,
query and fragment are omitted, and malformed sources are redacted.

Final commands (all successful), with `$sessionFiles` naming this session's
`files` directory and `$native` its `native-parity2\DotReticulum.Applications.exe`:

```powershell
dotnet build DotReticulum.slnx -c Release --nologo -v quiet
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o (Join-Path $sessionFiles 'native-parity2') --nologo -v minimal
& $native --help
$env:DOTRETICULUM_MEDIA_BACKEND = (Resolve-Path TestResults\media-backend\ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe).Path
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe $native -Coverage -Pester -ResultsDirectory (Join-Path $sessionFiles 'coverage-parity2-closure-2')
pwsh -NoProfile -File tests\run-pester.ps1 -GitHosting -GitRemote -Python $python -Upstream $reference
```

Release build and win-x64 NativeAOT publish: **zero warnings/errors**;
published executable SHA-256
`f42daa105e39a886771f2c9f551a63f0b4a2a48a74dfcd10ff74e4566427c689`.
Full live/native xUnit: **660 passed / 5 skipped / 0 failed**
(Applications 328, Core 77, Crypto 47, Interfaces 62, LXMF 35, Transport 111).
Published-native Pester: **98 passed / 0 skipped / 0 failed**.
The independent complete managed Pester invocation also passed **98 / 98**.
Native acceptance ran actual hosted feature flows and multi-megabyte transfer,
not just publish/help; the native-host test included the actual pinned Python
client with the disclosed retention shim and real FFmpeg.

Fresh merged production coverage, six reports, source line/branch identities
deduplicated across suites (no overlapping builds/collection):

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 4860/6113 (79.50%) | 4561/7229 (63.09%) |
| Core | 686/722 (95.01%) | 383/421 (90.97%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2451/2806 (87.35%) | 1319/1705 (77.36%) |
| **Overall** | **8871/10598 (83.70%)** | **6620/9801 (67.54%)** |

Retained fixture SHA-256, all from actual pinned-reference execution:

| Fixture under Applications.Tests `Fixtures` | SHA-256 |
| --- | --- |
| `rngit-host.json` | `d428392dc1be2cd979920fd192af460e2371e0ae0efdade6a5625bb50f7ca79a` |
| `rngit-repository-store.json` | `6f6df2795db1437357948d34e4f570f4f374bee7e2d4017ad704609e230422e0` |
| `rngit-release-vectors.json` | `5c60638fd617bf33b6fbca83f9f4265e57f0a1711d586f4cf1fe8e6da295422d` |
| `rngit-work.json` | `6de59dcc435b90561d9b5ecfb15d199c1d547dc8db201271fb0fdd4f6c99adae` |
| `rngit-pages.json` | `9d84961c8f782d94d676221dfb3946ebbd15fe3d46835530d364de5b60b659f1` |
| `rngit-media-templates.json` | `df4f6e91e8ba3232d71dbc227fc1683fbfcf7021abdcce6babe49b77309f2bd5` |
| `rngcs.json` | `d68867a8e17ae71faf6f85a298a75f6fb5664530257e0353c1f18122a7696f84` |

The page corpus now contains **122 response/download cases**, 24 Markdown
cases, 23 generation-time cases and 12 sync-age cases. It includes source
annotations (`pages.py:474-500`, `server.py:2733-2782`) and explicitly unsupported
global/group work requests (`pages.py:1471-1475` requires a repository).
The release corpus contains 31 handler cases; work has 49 responses/five binary
snapshots; store has 19 responses/eight decisions; rngcs has 136 CLI,
54 validation and ten exact signing vectors.

Native safety/API differences include explicit executable/external-source
authorization; no Python interactive runtime; bounded parsers, queues, links,
responses, signatures and processes; diagnosed corrupt state without identity
replacement/ID reset; atomic cross-platform file overwrite; independent
statistics counters; filesystem-link/path restrictions and encoded media refs.
No cross-process statistics merge, transaction/rollback/remote lease, durable
resume or streaming-resource guarantee is invented.

Windows x64 is the exercised managed/native platform. Unix permission/symlink
cases explicitly skip on Windows; Linux/macOS/hardware support is not inferred.
Actual encoder execution covers FFmpeg 9.0.2 (SHA-256
`3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`),
not the other backend families' installations. Windows jobs own exited-root
descendants; equivalent Unix orphan ownership remains unverified.
Rare DST equivalence and cross-process statistics merging are not established.
Native rendering deliberately uses deterministic literal code blocks and
code-point widths rather than discovering optional Python packages on the
operator's machine. Default reference rendering, native diff/commit coloring
and Micron formatting are implemented. Pygments language-token coloring and
wcwidth-enhanced layout are **not implemented equivalents**; this is the
documented dependency-free native rendering boundary, not evidence of those
optional integrations. Neither the milestone nor its checked phase claims
every optional Python-package enhancement or complete repository-wide parity.
An embedded signer key is not a trusted publisher, and rngcs intentionally
retains upstream's ignored allowed-signers/namespace options rather than
claiming a trust policy it does not enforce.

### Parity 2 native read/write Git client milestone (2026-10-19)

This record supersedes the **missing helper push/client configuration** entries
in the earlier inventory snapshots, not the remaining hosting/signing gaps.
Parity 1 was first integrated as `2d5fcff`; subsequent work is confined to
Parity 2's client milestone. The branding and introductory README edits were
preserved. No public repository was written, no reference pin advanced, and
no GitHub workflow, origin push, merge or repository-protection change was made.

#### Source-backed contracts and implemented boundary

Authoritative source remains the previously .NET-fetched hosted checkout
`a29bfb51c468945c96a1ee22cae0114e993e2391`, tree
`24d4c393de9fd57b933836cea68891fd953ab060`, RNS 1.5.7.
`reference_environment.verify_reference(require_lxmf=True)` confirmed actual
RNS import under that checkout, a clean tracked RNS tree and LXMF 1.2.1 from
the isolated venv. Source SHA-256:
`client.py` = `78358331ddac087860fb8a1f350632d3875917782dc000681d48597affd8936b`;
`server.py` = `6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693`.
Existing `e40191b3...` vectors were not regenerated or relabeled.

| Pinned source contract | Implementation and measurable acceptance |
| --- | --- |
| `Utilities/rngit/client.py:55-76,130-213,679-718`: client directory/defaults, client_config, client_identity, aliases, logging, ref_batch_size | Applications `RngitClientConfiguration` reads the upstream flat settings and persists raw identities atomically without overwrite. Default/custom/batch-one configuration, quoted/comments, case/literal precedence, invalid/bounded input, concurrent creators, corrupt-key preservation and restart identity checks; Unix permissions explicitly skipped on Windows. Native/helper network configuration remains the documented .NET subset |
| `client.py:335-447`; `server.py:2869-2935`: capabilities, for-push listing and batch dispatch | Offline protocol-clean negotiation advertises fetch/push/option; list is implicit. Write listing uses for_push=true. Bounded lines/ref queues, no mixed/duplicate-target batch, explicit malformed/EOF/timeout diagnostics and no fabricated success; real Git process acceptance |
| `client.py:545-655`; `server.py:3005-3069`: bundle push | Resolve source to its exact object ID, enumerate genuinely new reachable objects, exclude locally available advertised tips, prepare a bundle under an owned isolated bare repository using read-only alternates, send the reference binary-map request, require a valid status. Named refs/raw OIDs, initial unborn repository, branches/annotated/lightweight/Unicode tags, updates and bundle rejection/force; positive bytes and previously absent objects are mandatory checks |
| `client.py:630-675`; `server.py:3070-3111`: existing-object operations | Zero new objects uses action=update_ref with ref/sha/force, not an error-string-based fallback from failed bundle creation. Existing target changes require force even for fast-forward, matching the actual reference. Zero-byte success, unchanged success, changed-SHA rejection and explicit force are exercised independently |
| `client.py:551-577`; `server.py:3115-3140`: deletion | Reference /git/delete request and individual status, including absent-ref deletion and deletion of the advertised default branch without rewriting symbolic HEAD; persisted object/ref integrity asserted |
| `client.py:445-462,545-676`: per-ref stdout/error escaping | Git-compatible C-style UTF-8 byte quoting prevents diagnostic line injection. Local source failure and remote rejection each produce error for that ref while later independent refs can succeed. Git porcelain exit/output and direct helper exact statuses checked separately |
| `server.py:2262-2371,2869-2890,3005-3019`: authorization/disclosure | Disposable identity-scoped writer, different identity, readable read-only and hidden groups; rejected writes preserve refs and hidden listings expose none. Python adapter supplies configuration only; actual upstream permission/handler logic is unchanged |
| Git object format and local ownership | Helper explicitly supports SHA-1 only and rejects genuine SHA-256 remote/local transfers; direct rngit SHA-256 behavior remains unchanged. Push preparation never creates even temporary refs in the caller's repository. Revision/exclusion input uses stdin to avoid Windows command-line limits. Existing clone/fetch/Unicode/HEAD/remote-tracking and diverged-local-branch regressions retained |

One owner kept request/bundle/status/helper behavior together. Independent fleet
deliverables were the configuration/identity implementation and the disposable
C#/PowerShell/reference-adapter acceptance fixtures. The coordinator integrated
them and owns final full-suite/native/coverage evidence and commits.

#### Reference-generated corpus and live fixtures

`tests\DotReticulum.Applications.Tests\GenerateRngitPushVectors.py` executes
the actual pinned `ReticulumGitNode.handle_push/handle_delete` methods, using
C#/PowerShell-prepared disposable repositories and node configuration, not a
simulated server or fabricated success replies. Sixteen captured cases cover
unidentified/malformed requests, missing repository/data, operation/ref/SHA
validation, nonexistent object, corrupt bundle, readable denied/hidden access,
existing-object creation and present/absent/invalid deletion.
`Fixtures\rngit-push.json` records the exact pin/source hashes and response
bytes. Repeating generation produced identical SHA-256
`941ae1dc34ea3425a428c03cb096738db84145c9999a2b5f1e7382697927752c`.
Handler vectors alone are **not live-network evidence**.

The generator's fixture was created by
`dotnet run --file tests\Interop\CreateRngitFixture.cs --verbosity quiet -- $fixtureRoot`.
Its owned RNS config sets share_instance/enable_transport=no; server config
maps fixtures/readonly/hidden to the generated repositories with respectively
read:all+write:all, read:all+write:none, and read:none+write:none, disables
NomadNet/stats and periodic mirrors. With the verified source on PYTHONPATH,
PYTHONUTF8=1 and a process-only safe.bareRepository=all Git override:

```powershell
$fixtureRoot = Join-Path $sessionFiles 'push-research'
$sha = git -C (Join-Path $fixtureRoot 'source') rev-parse refs/heads/main
& $python tests\DotReticulum.Applications.Tests\GenerateRngitPushVectors.py --rns-config (Join-Path $fixtureRoot 'rns') --server-config (Join-Path $fixtureRoot 'server') --object $sha --output tests\DotReticulum.Applications.Tests\Fixtures\rngit-push.json
```

Live xUnit sends real .NET requests over an identified encrypted RNS link to
the unchanged reference handlers, including bundle/operations force decisions,
annotated tags, actual denied identities, outbound cap, cancellation and
staging cleanup. Pester uses separate actual Git/helper processes, C#-generated
histories and a Python host with identity-scoped --writer. It additionally
exercises partial per-ref Git exits, simultaneous distinct-ref writers,
configured one-ref fetch batches, isolated log rotation, unsupported dry-run/
atomic no-mutation, queued-batch deadlines and actual server restart with
stable identities, persisted refs and fresh clone/push afterward.

The Python host retains the disclosed UTF-8 runtime, process-only bare-repo
discovery override, owned temporary directory and nonblocking stdin allowances.
The added --writer and read-only/hidden groups are test configuration, not a
permission shim. No public Reticulum upstream is a push test target.

#### Coordinator validation and production coverage

Using `$python`, `$reference` and `$sessionFiles` from the Parity 1 record:

```powershell
$env:DOTRETICULUM_LIVE = '1'
$env:DOTRETICULUM_PYTHON = $python
$env:DOTRETICULUM_UPSTREAM = $reference
dotnet build DotReticulum.slnx -c Release --nologo -v quiet
dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~GitRemoteRnsTests|FullyQualifiedName~RngitClientConfigurationTests|FullyQualifiedName~RngitPushTests' --nologo -v quiet
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
$nativeDirectory = Join-Path $sessionFiles 'native-git-write'
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o $nativeDirectory --nologo -v minimal
$nativeExe = Join-Path $nativeDirectory 'DotReticulum.Applications.exe'
& $nativeExe --help
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python $python -Upstream $reference -NativeExe $nativeExe -Coverage -ResultsDirectory (Join-Path $sessionFiles 'coverage-git-write')
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $python -Upstream $reference -Application $nativeExe
```

Build passed with **zero warnings/errors**. Focused live configuration/helper/
push tests **87 passed, 1 skipped, zero failed**. NativeAOT publish with both
warning-as-error gates and published `--help` passed. Full live/native coverage
**464 passed, 1 skipped, zero failed**: Applications 132+1, Core 77, Crypto 47,
Interfaces 62, LXMF 35, Transport 111. The only skip is Unix file permissions on
Windows, with an explicit reason; all opt-in live cases were enabled.
The full suite includes the published native encrypted/compressed
multi-megabyte resource flow, not only native helper negotiation.
Managed and published-native Pester each **59 passed, zero skipped/failed**.
These include the existing eight tooling/CLI cases and 51 live Git cases.
Native acceptance executes actual pushes, clone/fetch, denial, force, deletion,
restart and cleanup through the installed git-remote-rns executable, not
merely its help/capability output.

An initial focused live fixture clone failed because the production local-Git
wrapper correctly denies network/file transports. Only the disposable test
clone opts into protocol.file.allow=always; production restrictions were not
weakened. Final commands above passed after that test setup correction.
The prior 49-case delegated Git run predated the additional unborn-remote and
missing-local-source cases and is not substituted for the final runner result.

Fresh coverage merged six production reports by source/line/branch identity;
Pester and native external child processes do not count as managed hits:

| Project | Lines | Branches |
| --- | ---: | ---: |
| Applications | 1136/1664 (68.27%) | 924/1829 (50.52%) |
| Core | 682/722 (94.46%) | 380/421 (90.26%) |
| Crypto | 110/116 (94.83%) | 47/50 (94.00%) |
| Interfaces | 376/412 (91.26%) | 117/126 (92.86%) |
| LXMF | 388/429 (90.44%) | 193/270 (71.48%) |
| Transport | 2433/2800 (86.89%) | 1312/1701 (77.13%) |
| **Overall** | **5125/6143 (83.43%)** | **2973/4397 (67.61%)** |

Logs/reports remain outside source control under `$sessionFiles`:
`git-write-validation.log`, `git-write-pester-managed.log`,
`git-write-pester-native.log` and `coverage-git-write`.
Environment remains Windows x64, .NET SDK 10.0.402, Git 2.52.0.windows.1,
PowerShell 7.6.6 and Pester 5.7.1.

#### Intentional differences and remaining boundary

Nonpositive batch sizes fail rather than copy upstream's non-progress loop;
invalid aliases/unsupported ConfigObj structures, duplicate entries and
oversized input fail explicitly. Config integers are signed 32-bit. Unknown
scalar settings retain upstream's ignore behavior. Actual logging constants
are -1..8, not the default sample's stale comment; .NET timestamp/diagnostic
formatting and lazy offline startup are intentional differences.
The client directory default preserves upstream's surprising
~/.config/rngit/config detection selecting ~/.rngit/reticulum.
Helper identity now uses client_identity: preserving an older .NET
storage/rngit identity requires explicit operator copying before first use;
there is no silent migration or key replacement. Direct CLI storage is unchanged.

Each request/resource is buffered. Outgoing bundles default to a 256 MiB cap
before reading into memory; local Git can consume more temporary disk before
the resulting bundle is size-checked. Source object directories containing
newlines are unsupported. Ordinary-Git helper SHA-256 negotiation is not
implemented; an empty remote does not disclose its format and is assumed
SHA-1 until the server accepts/rejects the push. Native installation/execution
is verified only on Windows x64, not Linux/macOS/mobile or hardware.

Ref operations are sequential, not atomic/CAS: no remote lease guarantee can
be claimed. Same-ref races remain the reference's behavior; concurrent
distinct refs are tested. Atomic and dry-run pushes are explicitly rejected
without mutation. Cancellation/loss after transmission leaves the in-flight
remote outcome uncertain, reported as failure, never invented rollback or
success. Normal failure/cancellation cleans owned local staging; a forced OS
kill can leave an unreferenced owned staging directory. Impaired network/
mid-upload disconnect and same-ref race stress are not newly verified here.
Object hashes/fsck establish consistency, **not authenticated publisher or
release-signature verification**.

This is the user's preferred coherent fallback milestone, **not complete
Parity 2**. Remaining hosting/configuration/permissions/persistence, create/
fork/mirror/sync, releases/work documents, pages/templates/media, signing and
full native utility CLI integration remain unchecked. The exact next item is
**Repository node configuration, identity/aliases, announcements, blocked
identities, statistics and restart persistence**. No Parity 3 implementation
has begun.

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
