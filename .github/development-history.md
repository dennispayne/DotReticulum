# DotReticulum development history

Historical reference only: this file records milestone outcomes and verification evidence; active work is tracked in [TODO.md](../TODO.md), the original roadmap snapshot is [docs/archive/plan.md](../docs/archive/plan.md), and current-state documentation lives in `README.md` and `docs/`. Historical completion does not imply full Reticulum parity.

## Reference pins and provenance

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
