# Contributing

DotReticulum is an experimental, native .NET implementation of the Reticulum Network Stack. It is written predominantly by LLM coding agents under human direction, and it is not an official Reticulum implementation. Its goal is feature, behavioral and wire parity with a pinned revision of the upstream Python reference, and the project only claims what its recorded evidence shows.

Contributions are welcome. Before you start:

- Discuss substantial protocol or architecture changes in an issue first.
- Keep pull requests focused, and complete the [pull request checklist](PULL_REQUEST_TEMPLATE.md).
- Follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report vulnerabilities as described in the [security policy](SECURITY.md).

Remaining work is tracked in [TODO.md](../TODO.md). Development chronology, commands and verification evidence are recorded in the [development history](development-history.md).

## Ground rules

These rules apply to every change.

- **The pinned upstream is authoritative.** The reference is the Reticulum-hosted repository, currently pinned to `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7). The GitHub repository is only a secondary comparison mirror.
- **Never invent wire formats, primitives or fragmentation schemes.** Read the upstream source, and when you are unsure, execute it to produce evidence. Match it byte for byte.
- **Generate reference vectors by executing upstream.** Protocol changes need vectors produced by running the upstream Python implementation, never written by hand or taken from our own output. Record the exact upstream revision and generation method in the test.
- **Be honest about evidence.** Unit vectors and .NET-only round trips are not interoperability with a Python node. Keep README, `TODO.md` and interoperability claims exactly as strong as the evidence, and disclose any reference-side shim or non-default peer setting.
- **Test every behavior change**, including malformed input, boundary lengths and failure paths. An unsupported test mode must skip explicitly with a reason, or fail. It must never return early and appear to pass.
- **Keep secrets out.** Do not commit real private keys, captured private traffic or credentials.
- **Preserve the license.** Keep the Reticulum License and its copyright notices intact. When a change affects upstream wire compatibility or has license implications, explain this in the pull request.

## Code style

Use C# 13 with nullable reference types, file-scoped namespaces and the repository `.editorconfig`. Nullable diagnostics are errors, and the build must stay warning-free. The solution file is `DotReticulum.slnx`.

### Language ownership

Each language has one job:

- **C#** implements and verifies the stack. Production code, test assertions, topology control and project-owned fixtures belong here.
- **PowerShell 7** builds and operates the development workflow, including process orchestration and general development and reporting tools.
- **Python** only bridges to the upstream reference.

Keep Python adapters small and readable. They are for actual RNS and LXMF API calls, callback observations, independent vector generation and necessary reference-side shims. Import-path and version checks stay at the Python boundary, so they validate what the peer actually loaded.

Do not reimplement reference behavior in our own languages, embed Python strings in PowerShell, or generate Python scripts merely to reduce the Python file count.

For example, `tests/Interop/live_rngit_node.py` only hosts the actual upstream server, while `RngitTests.cs` creates the Git fixture, controls the scenario and compares the results. Existing reference-vector verifiers may assert upstream acceptance directly. New scenarios should expose observations to xUnit instead of growing general test orchestration in Python.

### PowerShell scripts

Standalone scripts use approved `Verb-Noun.ps1` names and provide comment-based help, so you can read usage without running anything:

```powershell
Get-Help .\tests\Invoke-Tests.ps1 -Full
```

Renamed entry points replace the previous lowercase and underscore names, so update local commands accordingly. Tooling tests guard entry-point naming and help discovery.

## Setting up

You will need:

- The .NET 10 SDK pinned in `global.json`.
- PowerShell 7.
- Git.
- The platform's NativeAOT toolchain if you publish native binaries: MSVC C++ build tools on Windows, `clang` and zlib development files on Linux, or Xcode tools on macOS.

Test and analysis scripts never install dependencies for you. Install the pinned tools explicitly:

```powershell
Install-Module Pester -MinimumVersion 5.7.1 -Scope CurrentUser
Install-Module PSScriptAnalyzer -RequiredVersion 1.24.0 -Scope CurrentUser -Repository PSGallery
```

`tests\Install-Pester.ps1` is also available. It installs an exact Pester version (5.7.1 by default) with bounded retries.

### The pinned Python reference

The default test suite and coverage reporting need no Python. Live interoperability tests need an unmodified working checkout of the pinned upstream and an isolated Python virtual environment with LXMF pinned to 1.2.1.

Obtain the reference with our own .NET `rngit`, not from the GitHub mirror. See [rngit.md](../docs/rngit.md). The scripted path is:

```powershell
pwsh -NoProfile -File tests\Receive-Reference.ps1 -Application <native-executable> -Directory TestResults\reference-fetch
pwsh -NoProfile -File tests\Initialize-Reference.ps1 -Bundle TestResults\reference-fetch\reference.bundle -Directory TestResults\reference-python
```

`Receive-Reference.ps1` fetches the pinned objects over Reticulum through a TCP gateway and records provenance. `Initialize-Reference.ps1` validates the bundle against that provenance, checks the Git objects, and creates an `upstream` checkout and a `venv` virtual environment in a fresh directory. Both scripts require fresh destinations. Run `Get-Help` on either script for its options.

Then point the live harnesses at the result:

- `DOTRETICULUM_UPSTREAM` selects the checkout.
- `DOTRETICULUM_PYTHON` selects the virtual environment's interpreter.

The harnesses verify the imported source and Git revision, and LXMF harnesses also verify the package version. Existing vectors and their reproduction generators still identify the older revision that actually generated them (`e40191b3d193b46b7f2d8a44424a594cd758839b`). Reproducing those requires that historical checkout, not the current live pin.

## Building and testing

Build and test the whole solution:

```sh
dotnet build DotReticulum.slnx -c Release
dotnet test DotReticulum.slnx -c Release
```

### Test categories

Every test class carries `[Trait("Category", "...")]`, and new test classes must too.

| Category | Meaning |
|---|---|
| `Unit` | In-process and deterministic: no sockets, subprocesses, serial ports or external tools. |
| `Integration` | Uses real OS resources: loopback sockets, child processes, git, serial/pty, a native executable. Still runs by default. |
| `Live` | Needs the pinned Python reference; set on individual test methods and skipped unless `DOTRETICULUM_LIVE=1`. Live tests sit inside `Integration` classes. |

A class that mixes both kinds is tagged by its heavier side. A single method that needs a native executable or the reference sets its own gate and skips explicitly.

You can run a subset with a trait filter:

```sh
dotnet test DotReticulum.slnx -c Release --filter "Category=Unit"
dotnet test DotReticulum.slnx -c Release --filter "Category=Integration"
```

### The test runner

`tests\Invoke-Tests.ps1` wraps the common combinations:

```powershell
pwsh -NoProfile -File tests\Invoke-Tests.ps1
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Coverage
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Live -Python C:\Reference\venv\Scripts\python.exe -Upstream C:\Reference\Reticulum
pwsh -NoProfile -File tests\Invoke-Tests.ps1 -Pester
```

Its options:

- `-NativeExe <path>` also exercises the published NativeAOT peer.
- `-Filter <expression>` passes a test filter to `dotnet test`. It cannot be combined with `-Coverage`.
- `-Coverage` requires the full solution and uses a fresh results directory, which you can supply with `-ResultsDirectory`.
- `-Live` consumes an existing pinned reference checkout and virtual environment. It does not install packages, change pins or fetch code. The runner selects in-process `rnsd` for supported local-destination tests, fails on command errors and restores its environment overrides.
- `-Pester` runs the Pester suites after the .NET suite, using `-NativeExe` as the Pester application when supplied.

### Pester tooling and CLI tests

Pester (5.7.1 or newer) tests the tooling and drives the command-line applications as real processes:

```powershell
dotnet build DotReticulum.slnx -c Release
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -ToolingOnly
pwsh -NoProfile -File tests\Invoke-PesterTests.ps1 -Application C:\Publish\DotReticulum.Applications.exe
```

By default it tests the built Release DLL. With `-Application` it tests the published executable you supply.

- Tooling cases cover coverage merging and exclusions, incomplete or malformed reports and runner input guards.
- CLI suites launch actual processes. They cover identity persistence, signatures and encryption, runtime configuration and recovery, interface and resource behavior, network utilities, shell, Git errors, device-maintenance safety, transfer and execution cases, and authenticated multi-megabyte `rncp` transfers.

No public gateway is needed, and the default suite needs no Python. Supplying both `-Python` and `-Upstream` enables extra process comparisons against the verified reference; unavailable reference modes skip explicitly.

The suites use isolated temporary directories and bounded process waits. They capture stdout and stderr, and terminate only processes they launched. Pester checks application boundaries; protocol-level interoperability assertions stay in xUnit.

### NativeAOT

Production projects enable AOT and trimming analysis. Avoid reflection-based serialization, dynamic code generation and platform-specific assumptions. Prefer spans and memory, cancellation-aware asynchronous operations and explicit wire formats. Use platform cryptography where it is supported, and never invent primitives. Curve25519 uses Bouncy Castle, because portable .NET APIs do not expose raw Ed25519 and X25519 operations.

Publish with trimming and AOT warnings as errors:

```sh
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

Use `linux-x64`, `win-x64` or `osx-arm64` for `<rid>`. These are the platforms the GitHub Actions matrix exercises. Physical-hardware acceptance and platforms beyond these three are outside the current roadmap and remain unverified, not passed. Do not claim support for a platform merely because managed code compiles there.

A successful publish or help screen does not show that the native code paths work. Run `--help` and a representative native transfer that exercises links, encryption and compression, and record which features actually ran. For the compressed multi-megabyte round trip, set `DOTRETICULUM_NATIVE_EXE` to the published executable and run:

```sh
dotnet test tests/DotReticulum.Applications.Tests -c Release --filter FullyQualifiedName~NativeResourceTests
```

Without that variable the test skips explicitly.

To package and install the published utilities, see [Installing native DotReticulum utilities](../docs/installation.md).

## Compatibility tests

The shared Compatibility suite checks claims that upstream itself advertises. It runs the **same scenario, inputs and assertions** against the upstream Python reference and against the native .NET candidates. Only the invocation adapters differ.

### Running compatibility locally

From the repository root, with no parameters or preconfigured reference paths:

```powershell
.\tests\Compatibility\Invoke-CompatibilityTests.ps1
```

This needs the prerequisites from [Setting up](#setting-up), plus Python with venv and pip support, Internet access and a reachable Reticulum gateway. The automatic path supports Windows x64, Linux x64 and macOS ARM64.

The script:

1. Publishes a fresh native candidate.
2. Uses that candidate to retrieve the pinned reference over Reticulum. Retrieval uses the existing disclosed mirror-parent seed, so it is not publisher authentication or a full network clone.
3. Provisions an isolated Python environment. Reference dependencies are installed only inside that run's virtual environment; no global tools are installed.
4. Runs both the .NET and upstream candidates.

Each invocation prints and keeps a unique `TestResults\compatibility-local-*` directory with the native executable, reference artifacts, NUnit XML and per-test JSON reports. Existing results are never overwritten. Failed setup or retrieval is never treated as passing compatibility.

On Windows, the upstream candidate's hosted Git helper has a recorded bundle verification failure. The runner reports it as a failed case and exits nonzero, even when the .NET candidate passes. It does not hide the failure or apply a reference-side workaround. Use `-Prepare -Implementation DotReticulum` when you only intend to run the .NET candidate.

`-Prepare` keeps the automatic setup while overriding selected values, for example `-Prepare -Gateway my.gateway.example`, or `-Prepare -Application <native-executable>` to avoid republishing. Explicit invocations without `-Prepare` keep the manual and CI behavior, including environment-sourced reference paths and the fixed default output directory. `Get-Help .\tests\Compatibility\Invoke-CompatibilityTests.ps1 -Full` documents every override.

### How the suite is organized

- `tests\Compatibility\catalog.json` maps stable claim IDs such as `C004` to upstream citations. Claim text is a verbatim excerpt of the pinned upstream, not our interpretation, and each citation gives its file and exact lines.
- `tests\Compatibility\Claims\*.Tests.ps1` holds the readable acceptance cases. `Describe` names the group, and each `It` names the concrete acceptance behavior and carries a stable test ID such as `T006`. Test descriptions and scope comments explain what is actually exercised, without narrowing the quoted claim.
- Helpers under `tests\Compatibility\Support` handle invocation, reference nodes and lifecycle. Python adapters under `tests\Interop` execute upstream APIs, not acceptance assertions.

Append new numeric IDs; never renumber existing ones. Candidate initialization checks every quoted excerpt at its cited lines against the fetched, import-verified pin, and stale quotations fail explicitly.

The catalog's optional `citation.uri` (shown as `CitationUri` in discovery) is navigation metadata only. Use the original website URL for website claims, or a verified NomadNet source link when one is available. It stays blank for current source claims until those links are verified, and no GitHub mirror URL is substituted. Tests always use the Reticulum-fetched pinned checkout, whether or not a browsing link exists.

NomadNet browsing remains unverified. The Git destination resolved, but the derived page destination could not establish a link through our .NET stack. The browser accepts destination and page addresses and pinned blob fields, but neither an external clickable URI scheme nor source-line anchors were established. The development history records the exact reproduction.

You can list tests with their upstream citations without fetching, starting peers or running anything:

```powershell
pwsh -NoProfile -File tests\Compatibility\Get-CompatibilityTests.ps1
pwsh -NoProfile -File tests\Compatibility\Get-CompatibilityTests.ps1 -IncludeUncoveredClaims
```

### What the cases cover

At the time of writing the inventory has 42 distinct tests and 17 quoted claims; the discovery command above is authoritative. The cases cover:

- Identity generation, persistence, import and export, and public-only restrictions.
- Valid, corrupted and modified signatures, and authenticated decryption failures.
- Delivery proofs: confirmed, missing, forged and truncated.
- Authenticated management requests, including authorization denial and responses larger than the packet payload.
- Application requests and responses through an authorized executable consumer.
- Bidirectional multi-megabyte `rncp` transfers, automatic compression, authorized fetches, and authorization or missing-file failure followed by recovery.
- TCP IFAC mismatches.
- Channel stream delivery in both directions with the upstream Channel and Buffer message codecs.
- Bidirectional exact file delivery across a two-hop UDP/TCP path through a fixed upstream router, with no static routes and the upstream link's observed hop count.

Assertions check persisted bytes and actual peer observations, not only exit status. This is bounded evidence:

- The Channel case covers Buffer message encoding, not the general reader and writer APIs, loss or soak.
- The routing cases do not establish alternate routes, RF or impaired meshes.
- TCP IFAC does not establish authentication on every bearer.
- Direct transfers do not establish impaired-link or sustained-soak acceptance.

Windows `rncp` cases use a disclosed reference-side workaround; see [Moving to a new upstream release](#moving-to-a-new-upstream-release).

### Upstream baseline and the results table

In multi-node cases, upstream RNS is the fixed reference peer. Upstream-to-upstream is the baseline, and the .NET candidate then replaces only the candidate node on each OS. Using Python merely as a peer never passes the Upstream column, and a missing baseline result never implies an Upstream pass.

The consolidated table has one row per Pester `It` case, with Upstream, .NET Linux, .NET macOS and .NET Windows side by side. Each cell is Pester's own outcome. There is no Family classification and no calculated claim-level verdict. Test IDs link to the test source line at the executing commit. The summary contains only the table; diagnostics and provenance stay in logs, NUnit XML and per-test JSON artifacts.

`tests\Integration\catalog.json` and `tests\Invoke-ContractIntegration.ps1` hold the behavioral, .NET-only and mixed-peer process checks. CI runs those under **Test** and reports them in the Native table, not under Compatibility. That broader contract ledger is implementation and integration evidence, not a four-target compatibility report.

### Reference retrieval in CI

CI's **Compatibility** jobs exercise Git over Reticulum as an advertised feature. Each native candidate retrieves its own reference with its own `rngit`, then uses the fetched checkout for the remaining scenarios. Each fetch:

- Seeds only history up to the pin's parent from the GitHub comparison mirror.
- Requires the pinned commit to be initially absent.
- Receives nonzero Git bundle bytes over `rns://`.
- Verifies strict fsck and the exact pinned commit and tree.

This is a seeded incremental fetch, not a full network clone. Object integrity does not authenticate the publisher. The retrieved head and tree are recorded separately and can advance between candidates, but they never move the pin.

Only one source bundle, from the Linux candidate, is kept for the run, while each candidate's provenance and results are kept separately. A single upstream baseline runs after the three native candidates. **Compatibility / Upstream** and **Test** reuse that same-run bundle, checking its SHA-256, network evidence and pinned commit and tree before provisioning their temporary checkouts and virtual environments.

The upstream baseline runs the same retrieval scenario with the actual Python Git helper, plus the same core scenarios. Its adapter only observes bundle sizes and delegates request and fetch processing to upstream, without a wire shim. Dependency versions are recorded in `tests\Interop\reference-requirements.txt`, and the actually imported modules are checked before testing.

When something is missing:

- A failed native retrieval fails its Pester Git test and leaves dependent tests explicitly unexecuted.
- Without the canonical source bundle, the upstream baseline cannot pass and Python-peer integration cases skip explicitly. Non-reference Test cases can still run.

Compatibility always uses the canonical pin in `tests\Interop\reference_environment.py` and never treats a retrieved remote head as an alternate reference. Fetching the current head does not move the pin; that is a separate, explicit change. There is no standalone fetch workflow or historical-artifact lookup. The former scheduled fetch/drift workflow and switch-based comparison runner were removed rather than kept as a second acquisition or acceptance path.

## Coverage

Measure production coverage with the Coverlet collector, then merge the reports across test projects:

```sh
dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/coverage-<unique-run>
pwsh -NoProfile -File tests/Get-CoverageSummary.ps1 TestResults/coverage-<unique-run>
```

- Use a fresh results directory for every run, so stale reports cannot inflate coverage.
- Do not run other builds or tests against the same output directories while the collector is instrumenting assemblies.
- The summary counts shared lines and individual branch paths once across all test projects. It excludes test assemblies and rejects reports that are missing a production project.
- Record whether `DOTRETICULUM_LIVE=1` was enabled. The default suite skips Python interoperability tests.

Coverage shows execution, not correctness. Prefer focused tests of uncovered failure behavior over coverage-only assertions.

In CI, production line and branch coverage is reported beneath the Unit and Integration tables, per project and overall, for Linux, macOS and Windows. Each suite is measured independently, and percentages are never averaged or added. The merged report counts each line and branch covered by either suite once. All of these measure managed test processes, not separately launched native executables.

The coverage badges use the same merged Unit and Integration source-point counts. After a successful automatic main Test run, the **Coverage badges** workflow updates only `assets/coverage/*.json` on `main`. Those paths do not trigger Build or Static analysis, and publishing does not rebuild or execute downloaded artifacts. `assets/coverage/source.json` records the Test run, attempt and tested commit, and older runs cannot overwrite newer results. Shields endpoint badges read the public raw JSON with a five-minute cache. To bootstrap manually:

```powershell
gh workflow run coverage-badges.yml -f test_run_id=<successful-automatic-main-Test-id>
```

## Static analysis

The [Static analysis workflow](workflows/codeql.yml) uses CodeQL advanced setup for Actions, C# and Python, plus PSScriptAnalyzer for every tracked PowerShell script, module and data file, tests included. It runs on relevant pull request and push changes, weekly on Monday at 05:00 UTC, and on demand. Do not re-enable GitHub-managed default setup alongside it.

CodeQL keeps its default query suite, and C# is built with the pinned SDK for extraction. You can run the PowerShell checks locally:

```powershell
.\tools\Invoke-PowerShellAnalysis.ps1
```

`tools/PSScriptAnalyzerSettings.psd1` selects focused correctness and security rules and approved function verbs, not a blanket formatting policy. Enabled warnings and errors fail the job after the SARIF is written. Suppress genuine exceptions narrowly, with a justification. Public IFAC test fixture values are not production secrets. Pester still covers basic entry-point help.

The workflow summary shows each analyzer and language, its job status and finding counts, with bounded finding details. A completed CodeQL job does not mean there were no findings; results are handled through Code Scanning rather than an arbitrary zero-alert gate. Missing or failed analyses stay explicitly incomplete. SARIF artifacts are kept for 14 days. Fork pull requests run analysis and reporting without uploading SARIF to Code Scanning.

## Continuous integration

### Build

`build.yml` compiles the solution and publishes NativeAOT binaries on Linux, macOS and Windows. It uploads portable managed test outputs and the exact native binaries. A separate, non-executing attestation job signs trusted main-branch artifacts. Pull request builds keep read-only permissions and are not attested.

### Test

After a successful Build, `test.yml` runs separate **Unit**, **Integration** and **Compatibility** platform matrices against that Build's exact commit and artifacts, without rebuilding. Integration restores the application's MSBuild metadata only for the versioning-tool check; it does not compile another binary.

The security boundaries are:

- Trusted main artifacts require verified signed provenance, matching commit and runtime manifests, and SHA-256 digests.
- Pull requests (including forks) and feature-branch Builds get read-only `workflow_run` feedback without attestation.
- Scheduled and manual Test runs accept only trusted main Builds.
- No test job receives secrets or write permissions, and downstream jobs have no persisted checkout credentials.

A normal Build completion runs bounded checks. Longer live checks, mesh and recovery, native CLI and installation, and repeated traffic all belong to **Integration** rather than further test categories. Hardware and I2P prerequisites still skip explicitly, so these checks do not establish acceptance for every bearer or device.

### Scheduled and manual runs

The trusted **Test request** workflow owns the schedule and manual inputs. It selects and pins the newest successful main Build:

- Tuesday 06:00 UTC runs live Integration once.
- Saturday 06:00 UTC repeats the mixed-mesh/recovery and constrained native Buffer cases three times.
- A manual full Integration run includes the constrained native case too.

Manual runs can select an explicit Build:

```powershell
gh workflow run build.yml
gh workflow run test-request.yml -f build_run_id=123456789
gh workflow run test-request.yml -f full_integration=true -f iterations=3
```

Test request runs only on main with read permissions. It executes no dynamic producing source or Build artifacts, and uploads only the exact Build ID and cadence as JSON. Test runs only on completion of Build or Test request. It validates the request producer's repository, workflow, branch, event, success and request fields before accepting that exact main Build. Ordinary Build feedback and each requested cadence use separate concurrency groups.

The selection is recorded before downloading. Missing or expired artifacts fail rather than falling back to another run or rebuilding. Repetitions are bounded to 1–10 and keep every failure, even if a later round passes. Integration results keep component and iteration labels in one platform table.

### Retries

Selective job retries keep the source gate's report group, so unchanged platform reports and the canonical reference stay available. A retried job replaces only its own report artifact; rerunning the whole workflow creates a new group. Retries never select a different Build or silently rebuild.

### Inactivity reminders

The weekly inactivity workflow labels unassigned issues and non-draft pull requests after 90 days without activity. It never closes work or deletes branches. Assignments, milestones and the `pinned`, `security` or `never-stale` labels exempt work, and a new comment removes `stale`. Manual dispatch defaults to a dry run, so maintainers can preview reminders without changing anything.

## Submitting changes

Before you open a pull request:

1. Run the checks that cover your change: the relevant tests, `Invoke-PowerShellAnalysis.ps1` for PowerShell changes, and a NativeAOT publish plus an exercised feature flow for production changes. Documentation-only changes need no build.
2. Work through the [pull request checklist](PULL_REQUEST_TEMPLATE.md): tests, reference vectors, interoperability evidence, the NativeAOT check, secrets and license notices.
3. Explain any upstream wire-compatibility or license implications.
4. Keep the README interoperability table, `TODO.md` checkboxes and the [development history](development-history.md) evidence honest. Unit vectors are not live-node interop.

## Moving to a new upstream release

DotReticulum's version is the targeted upstream RNS version plus a git-height build number (see the README's [Versioning](../README.md#versioning) section). Anyone may take the project to the next upstream release, for example 1.5.8:

1. Fetch the new upstream with our own `rngit fetch` from `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`, verify the Git objects, and compare them against the [GitHub mirror](https://github.com/markqvist/Reticulum). The mirror is for comparison, never the origin of a pin.
2. Update `PINNED_RNS_SHA` (and the LXMF version the harness checks, if needed) in `tests/Interop/reference_environment.py`, and the commit text in this file and the README. [DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem) keeps its own copy of that file and its own submodule pin; update it separately.
3. Regenerate affected vectors by executing the new upstream, recording the new revision. Do not relabel older vectors.
4. Review upstream changes since the old pin, update the code and `docs/compatibility-contracts.md`, and run the live suites.
5. Re-check the reference-side shims described below against the new upstream.
6. Edit `version.json` to the new upstream number (the build height resets) and update the "reference RNS x.y.z" strings in the applications' `--version`/help output.
7. Record the change in `.github/development-history.md`.

### Reference-side shims

On Windows, the `rncp` reference adapter uses a disclosed workaround that replaces `shutil.move` with `copyfile` to retain received files. Those Windows cases are therefore not evidence of unmodified-upstream Windows file-retention behavior. Unix uses no retention shim.
