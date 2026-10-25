# Pinned upstream resolver/package placeholders

Audit pin: RNS 1.5.7,
`a29bfb51c468945c96a1ee22cae0114e993e2391`, from the verified Reticulum-hosted
checkout. These are upstream placeholders, **not** unimplemented .NET features
that should be invented.

| Source | Actual contract |
| --- | --- |
| `RNS/Resolver.py:31–34` | `Resolver.resolve_identity(full_name)` is a static `pass` method and returns `None`. No lookup, naming scheme, cache, authentication, destination or wire format exists in this module. No native resolver protocol is invented. |
| `RNS/Utilities/rnir.py:40–72` | Parses `--config`, counted `-v/--verbose`, counted `-q/--quiet`, help and `--version`; `program_setup` creates `RNS.Reticulum` with verbosity `verbose - quiet`, stdout logging, then exits 0. The unused `service` branch chooses file logging and default verbosity. No identity resolution occurs. |
| `RNS/Utilities/rnpkg.py:39–83` | Same startup/exit shell plus `--exampleconfig`. The complete example is one comment, `# This is an example package manager configuration file.`, followed by a blank line. There are no install, repository, dependency, signing, fetch or package operations. |

`GenerateUpstreamPlaceholderVectors.py` independently executes both real CLI
entry points for help/version/unknown-command/init/config/counting/example
cases, verifies actual RNS/LXMF imports, and records each source SHA-256 in
`Fixtures/upstream-placeholder-vectors.json`. A disclosed capturing constructor
replaces `RNS.Reticulum` **only** to observe constructor arguments without
starting network interfaces or touching reference state. Eleven captured cases
are shell evidence, not real upstream daemon startup or runtime parity.
The actual `Resolver.resolve_identity("example.invalid")` returns `None`.

Native `rnir` and `rnpkg` preserve the bounded shell intent: help/version,
configuration/verbosity parsing, real native Reticulum runtime initialization
followed by owned disposal/exit, and the exact minimal package example.
They clearly label themselves placeholders and reject fabricated operations.
Help/version text is native-branded, generic invalid input returns 1 rather
than argparse's 2, and runtime logs use the project's logger on stderr.
No separate service flag is exposed because the upstream parsers expose none.
PowerShell process tests use no-interface configurations to verify persisted
native runtime state and bounded exit; these do not establish resolver/package
capability or Python runtime equivalence.

Acceptance is source-backed placeholder classification plus tested observable
shell behavior. No hardware, network name service, package repository, P8
platform work or CI workflow is a prerequisite for this audit.
