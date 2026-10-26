# Upstream resolver and package-manager placeholders

RNS 1.5.7 contains resolver and package-manager entry points but no corresponding operations. DotReticulum preserves that boundary and does not invent a resolver protocol, name service, package repository, or package format.

## Resolver (`rnir`)

Upstream `Resolver.resolve_identity(full_name)` is a static placeholder that returns `None`. It defines no lookup behavior, naming scheme, cache, authentication, destination, or wire format. The `rnir` command parses `--config`, repeatable verbosity (`-v`/`--verbose`) and quiet (`-q`/`--quiet`) switches, help, and version, then initializes Reticulum and exits. Its upstream service branch only selects file logging/default verbosity; it does not resolve an identity.

```sh
rnir --help
rnir --config <path> -v
```

The native command is an observable startup shell, not identity resolution. It initializes and disposes the native Reticulum runtime, labels its help/version as a placeholder, and rejects unsupported operations.

## Package manager (`rnpkg`)

Upstream `rnpkg` has the same configuration/verbosity startup shell and an `--exampleconfig` option. The entire example is the comment `# This is an example package manager configuration file.` followed by a blank line. There are no package installation, repository, dependency, signature, or fetch operations.

```sh
rnpkg --help
rnpkg --exampleconfig
rnpkg --config <path> -q
```

The native command preserves this shell behavior and exact minimal example, initializes and disposes the native runtime for ordinary invocation, and rejects unsupported operations. Native diagnostics and generic invalid-input exit status differ from Python argparse conventions. Neither tool is a service manager.

## Platform notes and differences from upstream

The resolver and package-manager contract has no platform-specific implementation or wire behavior. Native startup uses the configured platform runtime and logging providers. The native shell labels itself as a placeholder, rejects unsupported operations, and uses native diagnostic/exit conventions rather than Python argparse behavior; it does not add a resolver or package service.

## Configuration and security

Both commands accept `--config <path>` and counted `-v`/`--verbose`, `-q`/`--quiet`. They open a Reticulum runtime for ordinary startup invocation; help, version, and `rnpkg --exampleconfig` return without starting network interfaces. No identity lookup or package operation is authorized or performed.

## Verification

The pinned contract is RNS 1.5.7, `RNS/Resolver.py` and `RNS/Utilities/rnir.py` / `rnpkg.py`. Source-backed placeholder classification and native startup/parser behavior are covered by `tests/DotReticulum.Applications.Tests`. A disclosed constructor-capture adapter observes `RNS.Reticulum` arguments without starting upstream interfaces or touching reference state; this is shell evidence, not unmodified Python daemon startup or network capability. No resolver or package interoperability is claimed. See [remaining capabilities](../TODO.md).