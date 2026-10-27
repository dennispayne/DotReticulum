# Contributing

DotReticulum is an experimental project written predominantly by LLM coding agents under human direction. Discuss substantial protocol or architecture changes in an issue first. Keep pull requests focused and follow the PR checklist and Code of Conduct.

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

```powershell
pwsh -NoProfile -File tests\run-tests.ps1
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Python C:\Reference\venv\Scripts\python.exe -Upstream C:\Reference\Reticulum
pwsh -NoProfile -File tests\run-tests.ps1 -Pester
```

`-NativeExe <path>` additionally exercises the published NativeAOT peer. `-Filter <expression>` selects tests without coverage; `-Coverage` requires the full solution and uses a fresh results directory (optionally supplied with `-ResultsDirectory`). Live execution consumes an existing pinned reference checkout/venv; it does not install packages, change pins or fetch code automatically. The runner selects in-process `rnsd` for supported local-destination tests, fails on command errors and restores its environment overrides.

### Pester tooling and CLI end-to-end tests

Use PowerShell 7 and Pester 5.7.1 or newer. If Pester is missing, install it explicitly with `Install-Module Pester -MinimumVersion 5.7.1 -Scope CurrentUser`; test scripts never install dependencies automatically.

```powershell
dotnet build DotReticulum.slnx -c Release
pwsh -NoProfile -File tests\run-pester.ps1
pwsh -NoProfile -File tests\run-pester.ps1 -ToolingOnly
pwsh -NoProfile -File tests\run-pester.ps1 -Application C:\Publish\DotReticulum.Applications.exe
```

`run-pester.ps1` tests the built Release DLL by default, or the supplied published executable. Tooling cases cover coverage merging/exclusions, incomplete/malformed reports and runner input guards. CLI suites launch actual processes for identity persistence, runtime configuration/recovery, interface and resource behavior, Git errors and authenticated multi-megabyte rncp transfers. The suites also cover identity/signature/encryption, network utilities, shell, device-maintenance safety and transfer/execution cases. No public gateway is required; the default suite needs no Python. Supplying both `-Python` and `-Upstream` enables additional verified-reference process comparisons; unavailable reference modes explicitly skip. The suite uses isolated temporary directories, bounded process waits, captures stdout/stderr and terminates only processes it launched. Pester checks application boundaries; protocol-level interoperability assertions remain in xUnit. `run-tests.ps1 -Pester` runs these after the .NET suite, using `-NativeExe` as the Pester application when supplied.

Measure production coverage with the existing Coverlet collector and merge reports across test projects:

```sh
dotnet test DotReticulum.slnx -c Release --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/coverage-<unique-run>
pwsh -NoProfile -File tests/coverage_summary.ps1 TestResults/coverage-<unique-run>
```

Use a fresh results directory per run so stale reports cannot inflate coverage, and do not run other builds/tests against the same output directories while the collector is instrumenting assemblies. The summary counts shared lines and individual branch paths once across all test projects, excluding test assemblies, and rejects reports missing a production project. Record whether `DOTRETICULUM_LIVE=1` was enabled; the default suite skips Python interoperability tests. Coverage shows execution, not correctness. Prefer focused tests of uncovered failure behavior over coverage-only assertions.

Live Python harnesses require `DOTRETICULUM_UPSTREAM` to point at an unmodified working checkout of the Reticulum-hosted upstream, currently `a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7), and `DOTRETICULUM_PYTHON` to select the reference venv. They verify the imported source and Git revision; LXMF harnesses also verify the pinned package version (1.2.1). Obtain updates with our .NET `rngit fetch`, verify the resulting objects and materialize a working checkout locally; the GitHub repository is only a secondary comparison mirror. See [rngit.md](docs/rngit.md). Existing vectors and their reproduction generators still identify the older revision that actually generated them; reproducing those requires that historical checkout rather than the current live pin. Disclose reference-side shims and non-default peer limits. Unsupported test modes must explicitly skip rather than return a false pass.

## Moving to a new upstream release

CI's **Fetch upstream over Reticulum** workflow runs manually or weekly. It reuses
a successful Linux native Build artifact, seeds only history through the pin's
parent from the GitHub comparison mirror, and uses our native `rngit` to fetch
missing objects over `rns://`. Nonzero network bundle bytes, strict fsck and the
pin's absence before retrieval are required. The resulting source bundle and
provenance are Actions artifacts, not tracked source or a venv.

Compatibility jobs reuse that bundle, checking the recorded SHA-256 and pin,
then run `tests\prepare-reference.ps1` to create a fresh checkout and venv in
runner temporary storage. Dependency versions are recorded in
`tests\Interop\reference-requirements.txt`; actual imported modules are checked
before testing. If no unexpired source artifact exists, cases needing Python
remain explicitly unexecuted rather than passing. Object integrity does not
authenticate the publisher. Moving the compatibility pin remains a separate,
explicit change; fetching current head does not change it.

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
