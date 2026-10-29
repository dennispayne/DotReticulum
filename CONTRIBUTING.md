# Contributing

DotReticulum is an experimental project written predominantly by LLM coding agents under human direction. Discuss substantial protocol or architecture changes in an issue first. Keep pull requests focused and follow the PR checklist and Code of Conduct.

## Inactivity reminders

The weekly inactivity workflow labels unassigned issues and non-draft pull
requests after 90 days without activity. It never closes work or deletes
branches. Assignments, milestones and the `pinned`, `security` or `never-stale`
labels exempt work; a new comment removes `stale`. Manual dispatch defaults to
a dry run so maintainers can preview reminders without modifying discussions.

## Development

Install the .NET 10 SDK and use C# 13, nullable reference types, file-scoped namespaces, and the repository `.editorconfig`. Nullable diagnostics are errors. The solution file is `DotReticulum.slnx`.

```sh
dotnet build DotReticulum.slnx -c Release
dotnet test DotReticulum.slnx -c Release
```

### Test categories

Every test class carries `[Trait("Category", "...")]`; new test classes must too.

| Category | Meaning |
|---|---|
| `Unit` | In-process and deterministic: no sockets, subprocesses, serial ports or external tools. |
| `Integration` | Uses real OS resources: loopback sockets, child processes, git, serial/pty, a native executable. Still runs by default. |
| `Live` | Needs the pinned Python reference; set on individual test methods and skipped unless `DOTRETICULUM_LIVE=1`. Live tests sit inside `Integration` classes. |

Run a subset with a trait filter:

```sh
dotnet test DotReticulum.slnx -c Release --filter "Category=Unit"
dotnet test DotReticulum.slnx -c Release --filter "Category=Integration"
```

A class that mixes both kinds is tagged by its heavier side; a single method that needs a native executable or the reference sets its own gate and skips explicitly.

Every behavior change needs tests, including malformed inputs, boundary lengths and failure paths. Protocol changes require independently generated reference vectors produced by executing the upstream Python implementation, with the exact upstream revision and generation method recorded in the test. Round-trip tests alone are not interoperability evidence. Do not commit real private keys, captured private traffic, or credentials.

## Language ownership

C# implements and verifies our solution. PowerShell 7 builds and operates the development workflow. Python only bridges to the upstream Python reference.

Keep production, test assertions, topology control and project-owned fixtures in C#; use PowerShell for process orchestration and general development/reporting tools. Retain small readable Python adapters for actual RNS/LXMF API calls, callback observations, independent vector generation and necessary reference-side shims. Import-path/version checks remain at the Python boundary so they validate what the peer actually loaded. Do not reimplement reference behavior in our languages, embed Python strings in PowerShell, or generate Python scripts merely to reduce the Python file count.

`tests/Interop/live_rngit_node.py`, for example, only hosts the actual upstream server; `RngitTests.cs` creates the Git fixture, controls the scenario and compares results. Existing reference-vector verifiers may assert upstream acceptance directly; new scenarios should expose observations to xUnit rather than grow general test orchestration in Python.

PowerShell entry points (no Python required for the default suite or coverage reporting):

Standalone scripts use approved `Verb-Noun.ps1` names and provide comment-based
help: `Get-Help .\tests\Invoke-Tests.ps1 -Full` shows usage without running tests.
Renamed entry points replace the previous lowercase/underscore names; update
local commands accordingly. Tooling tests guard entry-point naming and help discovery.

### Scheduled live integration and mixed meshes

The weekly `interop-test.yml` and `e2e-mesh-test.yml` workflows reuse Build and
Test, including fresh three-platform native artifacts and candidate-owned
reference retrieval. They do not download an old successful build/reference.
Normal pushes keep the bounded default suite; extended runs explicitly enable
the live xUnit integration cases or repeat the mixed-router/resource-loss,
reconnect and published-daemon tunnel-restoration cases.

```powershell
gh workflow run interop-test.yml
gh workflow run e2e-mesh-test.yml -f iterations=3
```

Iterations are bounded to 1-10 and every failure remains a failure, even if a
later round passes. Extended results have their own cross-platform Test table,
not Compatibility rows or instrumented coverage. Hardware/I2P prerequisites
still skip explicitly. Existing mixed-mesh cases do not establish Python-router
crash recovery, alternate routes, sustained Channel soak or every native bearer.

### Static analysis

The [Static analysis workflow](.github/workflows/codeql.yml) uses CodeQL advanced
setup for Actions, C# and Python, plus PSScriptAnalyzer for all tracked PowerShell
scripts, modules and data files (including tests). It runs on relevant PR/push
changes, weekly Monday at 05:00 UTC, or manually. CodeQL retains its default query
suite; C# is built with the pinned SDK for extraction.

Install the pinned analyzer explicitly, then run the same checks locally:

```powershell
Install-Module PSScriptAnalyzer -RequiredVersion 1.24.0 -Scope CurrentUser -Repository PSGallery
.\tools\Invoke-PowerShellAnalysis.ps1
```

`PSScriptAnalyzerSettings.psd1` selects focused correctness/security rules and
approved function verbs, not a blanket formatting policy. Enabled warnings and
errors fail the PowerShell job after writing SARIF. Use narrow suppressions with
a justification for genuine exceptions; public IFAC test fixture values are not
production secrets. Basic entry-point help remains covered by Pester.

The final workflow summary shows each analyzer/language, job status and finding
counts, plus bounded finding details. CodeQL completion does not mean no findings;
its results are handled through Code Scanning rather than an arbitrary zero-alert
gate. Missing/failed analyses stay explicitly incomplete. SARIF artifacts are
retained for 14 days. Fork PRs run analysis and reporting without uploading SARIF
to Code Scanning. Do not re-enable GitHub-managed default setup alongside this
advanced workflow.

```powershell
pwsh -NoProfile -File tests\Invoke-Tests.ps1
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Coverage
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Live -Python C:\Reference\venv\Scripts\python.exe -Upstream C:\Reference\Reticulum
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Pester
```

`-NativeExe <path>` additionally exercises the published NativeAOT peer. `-Filter <expression>` selects tests without coverage; `-Coverage` requires the full solution and uses a fresh results directory (optionally supplied with `-ResultsDirectory`). Live execution consumes an existing pinned reference checkout/venv; it does not install packages, change pins or fetch code automatically. The runner selects in-process `rnsd` for supported local-destination tests, fails on command errors and restores its environment overrides.

### Pester tooling and CLI end-to-end tests

Use PowerShell 7 and Pester 5.7.1 or newer. If Pester is missing, install it explicitly with `Install-Module Pester -MinimumVersion 5.7.1 -Scope CurrentUser`; test scripts never install dependencies automatically.

```powershell
dotnet build DotReticulum.slnx -c Release
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -Application C:\Publish\DotReticulum.Applications.exe
```

`Invoke-PesterTests.ps1` tests the built Release DLL by default, or the supplied published executable. Tooling cases cover coverage merging/exclusions, incomplete/malformed reports and runner input guards. CLI suites launch actual processes for identity persistence, runtime configuration/recovery, interface and resource behavior, Git errors and authenticated multi-megabyte rncp transfers. The suites also cover identity/signature/encryption, network utilities, shell, device-maintenance safety and transfer/execution cases. No public gateway is required; the default suite needs no Python. Supplying both `-Python` and `-Upstream` enables additional verified-reference process comparisons; unavailable reference modes explicitly skip. The suite uses isolated temporary directories, bounded process waits, captures stdout/stderr and terminates only processes it launched. Pester checks application boundaries; protocol-level interoperability assertions remain in xUnit. `Invoke-Tests.ps1 -Pester` runs these after the .NET suite, using `-NativeExe` as the Pester application when supplied.

Measure production coverage with the existing Coverlet collector and merge reports across test projects:

```sh
dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/coverage-<unique-run>
pwsh -NoProfile -File tests/Get-CoverageSummary.ps1 TestResults/coverage-<unique-run>
```

Use a fresh results directory per run so stale reports cannot inflate coverage, and do not run other builds/tests against the same output directories while the collector is instrumenting assemblies. The summary counts shared lines and individual branch paths once across all test projects, excluding test assemblies, and rejects reports missing a production project. Record whether `DOTRETICULUM_LIVE=1` was enabled; the default suite skips Python interoperability tests. Coverage shows execution, not correctness. Prefer focused tests of uncovered failure behavior over coverage-only assertions.

Build and Test reports production line and branch coverage separately beneath
the Unit and Integration tables, with per-project and overall counts for Linux,
macOS and Windows. Each suite is measured independently; percentages are not
averaged or added. The existing merged coverage report still counts each line
and branch covered by either suite once. These are managed test-process coverage
measurements, not coverage of separately launched native executables.

Live Python harnesses require `DOTRETICULUM_UPSTREAM` to point at an unmodified working checkout of the Reticulum-hosted upstream, currently `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7), and `DOTRETICULUM_PYTHON` to select the reference venv. They verify the imported source and Git revision; LXMF harnesses also verify the pinned package version (1.2.1). Obtain updates with our .NET `rngit fetch`, verify the resulting objects and materialize a working checkout locally; the GitHub repository is only a secondary comparison mirror. See [rngit.md](docs/rngit.md). Existing vectors and their reproduction generators still identify the older revision that actually generated them; reproducing those requires that historical checkout rather than the current live pin. Disclose reference-side shims and non-default peer limits. Unsupported test modes must explicitly skip rather than return a false pass.

### Run shared compatibility locally

From the repository root, no parameters or preconfigured reference paths are needed:

```powershell
.\tests\Compatibility\Invoke-CompatibilityTests.ps1
```

Install the prerequisites first: PowerShell 7, Pester 5.7.1+, Git, Python with
venv/pip support, the pinned .NET SDK and the platform's NativeAOT build tools
(MSVC C++ tools on Windows, clang/zlib development files on Linux, Xcode tools
on macOS). The automatic path supports Windows x64, Linux x64 and macOS ARM64.
It publishes a fresh native candidate, uses that candidate to retrieve the
pinned reference over Reticulum, provisions an isolated Python environment,
and runs both .NET and upstream candidates. The existing disclosed mirror-parent
seed is used for retrieval; this is not publisher authentication or a full
network clone. Internet access and a reachable Reticulum gateway are required.

Each invocation prints and retains a unique `TestResults\compatibility-local-*`
directory containing the native executable, reference artifacts, NUnit XML and
per-test JSON reports. Existing results are never overwritten, and failed setup
or retrieval is not treated as passing compatibility. Dependencies for the
Python reference are installed only inside that run's virtual environment;
the script does not install global tools.

On Windows, the upstream candidate's hosted Git helper has a recorded bundle
verification failure. The runner reports this as a failed case and exits nonzero,
even when the .NET candidate passes; it does not hide the failure or apply a
reference-side workaround. `-Prepare -Implementation DotReticulum` runs just
the .NET candidate when that is the intended scope.

Use `-Prepare` to keep automatic setup while overriding selected values, for
example `-Prepare -Gateway my.gateway.example`, or `-Prepare -Application <native-executable>`
to avoid republishing. Explicit invocations without `-Prepare` retain the existing
manual/CI behavior, including environment-sourced reference paths and the fixed
default output directory. `Get-Help .\tests\Compatibility\Invoke-CompatibilityTests.ps1 -Full`
documents all overrides.

## Moving to a new upstream release

The consolidated compatibility table has one row per Pester `It` case and
displays Upstream, .NET Linux, .NET macOS and .NET Windows side by side.
`tests\Compatibility\catalog.json` maps claim IDs to upstream citations;
`tests\Compatibility\Claims\*.Tests.ps1` contains the readable acceptance cases,
assertions and coverage boundaries. There is no Family classification or
calculated claim-level verdict: each cell preserves Pester's test outcome.
Claim text is a verbatim excerpt of the pinned upstream, not our interpretation;
citations contain its file and exact line reference. Test descriptions and scope
comments explain what is actually exercised without narrowing the quoted claim.
Candidate initialization verifies every quoted source excerpt at those lines
against the fetched, import-verified pin; stale quotations fail explicitly.
Discovery's separate `CitationUri` is optional navigation metadata (`citation.uri`
in the catalog): use the original website URL for website claims, or a verified
NomadNet source link when available. It remains blank for current source claims
until those links are verified; no GitHub mirror URL is substituted. Tests use
the Reticulum-fetched pinned checkout regardless of browsing-link availability.
Claims use stable IDs such as `C004`; individual tests use `T006`. Append new
numeric IDs without renumbering existing cases. The catalog spells out the
advertised claim, `Describe` names its group, and each `It` names the concrete
acceptance behavior.
`tests\Compatibility\Invoke-CompatibilityTests.ps1` runs the **same scenario, inputs and assertions**
against both candidates; only invocation adapters differ. For multi-node cases,
upstream RNS is the fixed reference peer: upstream-to-upstream is the baseline,
then .NET replaces only the candidate node on each OS. Using Python only as a
peer does not pass the Upstream column. A single upstream-to-upstream baseline
runs after the three native candidates, reusing one verified candidate-fetched
source bundle. Missing baseline results never imply an Upstream pass.
The table links stable Test IDs to the actual test source line at the executing
commit. Descriptions and assertions belong to Pester, not duplicated catalog
entries. Helpers under `Support` handle invocation, reference nodes and lifecycle;
Python adapters under `Interop` execute upstream APIs, not acceptance assertions.
The summary contains only the table; diagnostics and provenance remain in logs,
NUnit XML and per-test JSON artifacts.

Discover tests and join their metadata with upstream citations without fetching,
starting peers or running tests:

```powershell
pwsh -NoProfile -File tests\Compatibility\Get-CompatibilityTests.ps1
pwsh -NoProfile -File tests\Compatibility\Get-CompatibilityTests.ps1 -IncludeUncoveredClaims
```

The shared inventory contains 39 distinct tests and 17 catalogued quoted claims. Cases
cover identity generation/persistence/import/export, valid and corrupted
signatures, authenticated decryption failures, public-only restrictions,
proof/no-proof/forged-proof delivery, authenticated management requests,
bidirectional multi-megabyte rncp transfers, automatic compression, authorized
fetches, authorization failure/recovery and TCP IFAC mismatches. Assertions
check persisted bytes and actual peer observations, not only exit success.
Two routing cases add bidirectional exact file delivery across a two-hop
UDP/TCP path through a fixed pinned upstream router, with no static routes and
the upstream link's observed hop count. This is not alternate-route, RF or
impaired-mesh acceptance. Channel/Buffer remains uncovered in this shared
inventory; historical integration results do not silently pass it.
TCP IFAC does not establish authentication on every
bearer; direct transfers do not establish impaired-link or sustained-soak
acceptance. On Windows the rncp reference adapter uses the existing disclosed
`shutil.move` to `copyfile` retention workaround; those cases are not evidence
of unmodified-upstream Windows file-retention behavior. Unix uses no retention shim.

NomadNet browsing remains unverified: the Git destination resolved, but the
derived page destination could not establish a link through our .NET stack.
The browser accepts destination/page addresses and pinned blob fields; neither
an external clickable URI scheme nor source-line anchors were established.
`CitationUri` therefore remains blank rather than inventing links or falling
back to the GitHub mirror. See the exact reproduction and limitation in the
development history.
`tests\Integration\catalog.json` and `tests\Invoke-ContractIntegration.ps1` retain
the behavioral, .NET-only and mixed-peer process checks. CI runs those under
**Test**, including their results in the consolidated Native table, not
Compatibility. The broader contract ledger remains implementation/integration
evidence rather than a four-target compatibility report.

CI's **Compatibility** jobs exercise advertised Git-over-Reticulum functionality:
each native candidate retrieves its own reference using its own `rngit`, then
uses the fetched checkout for the remaining shared scenarios. Each fetch seeds
only history through the pin's parent from the GitHub comparison mirror, requires
the pin to be initially absent, receives nonzero Git bundle bytes over `rns://`,
and verifies strict fsck and the exact pinned commit/tree. This is a seeded
incremental fetch, not a full network clone. Retrieved head/tree are recorded
independently and can advance between candidates; they never move the pin.

Only one source bundle (from the Linux candidate) is retained for the run, with
each candidate's provenance/results kept separately. **Compatibility / Upstream**
and **Test** reuse that same-run bundle, checking SHA-256, network evidence and
the pinned commit/tree before provisioning their temporary checkouts/venvs.
The upstream baseline uses the actual Python Git helper for the same retrieval
scenario, plus the same six core scenarios; its adapter only observes bundle
sizes and delegates request/fetch processing to upstream, without a wire shim.
The other fetched repositories are temporary; no standalone fetch workflow or
historical artifact lookup remains. Dependency versions are recorded in
`tests\Interop\reference-requirements.txt`; actual imported modules are checked
before testing. A failed native retrieval fails its Pester Git test and leaves dependent
tests explicitly unexecuted. If the canonical source bundle is unavailable,
the upstream baseline cannot pass and Python-peer integration cases explicitly
skip; non-reference Test cases can still run. Object integrity does not
authenticate the publisher. Moving the compatibility pin remains a separate,
explicit change; fetching current head does not change it.

Compatibility always uses the canonical pin in
`tests\Interop\reference_environment.py`; it does not treat the retrieved remote
head as an alternate reference. The former separately scheduled fetch/drift
workflow and switch-based comparison runner have been removed rather than
keeping a second reference acquisition or acceptance path.

DotReticulum's version is the targeted upstream RNS version plus a git-height build number (see the README's Versioning section). Anyone may take the project to the next upstream release (for example 1.5.8):

1. Fetch the new upstream with our own `rngit fetch` from `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`, verify the Git objects, and compare against the [GitHub mirror](https://github.com/markqvist/Reticulum). The mirror is for comparison, never the origin of a pin.
2. Update `PINNED_RNS_SHA` (and the LXMF version the harness checks, if needed) in `tests/Interop/reference_environment.py`, and the commit text in this file and the README. [DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem) keeps its own copy of that file and its own submodule pin; update it separately.
3. Regenerate affected vectors by executing the new upstream, recording the new revision. Do not relabel older vectors.
4. Review upstream changes since the old pin, update the code and `docs/compatibility-contracts.md`, and run the live suites.
5. Edit `version.json` to the new upstream number (the build height resets) and update the "reference RNS x.y.z" strings in the applications' `--version`/help output.
6. Record the change in `.github/development-history.md`.

## NativeAOT and portability

Production projects enable AOT/trimming analysis. Avoid reflection-based serialization, dynamic code generation, and platform-specific assumptions. Prefer spans/memory, cancellation-aware asynchronous operations, and explicit wire formats. Use platform cryptography where supported; never invent primitives. Curve25519 uses Bouncy Castle because portable .NET APIs do not expose raw Ed25519/X25519 operations.

```sh
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

Use `linux-x64`, `win-x64` or `osx-arm64` for `<rid>`; NativeAOT needs the platform native toolchain. Other platforms must be validated separately; do not claim support merely because managed code compiles.

Run both `--help` and a representative native transfer exercising links, encryption and compression. A successful publish or help screen is not evidence that those code paths work. Record which features actually executed.

Set `DOTRETICULUM_NATIVE_EXE` to the published NativeAOT Applications executable, then run `dotnet test tests/DotReticulum.Applications.Tests -c Release --filter FullyQualifiedName~NativeResourceTests` for the compressed multi-megabyte roundtrip. Without that variable the test explicitly skips.

## Review

Explain upstream wire compatibility and license implications. Keep the interoperability table honest: unit vectors are not live-node interop. Preserve the Reticulum License and copyright notices.
