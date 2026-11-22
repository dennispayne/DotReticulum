# rnir and rnpkg: upstream placeholders

RNS 1.5.7 contains entry points for an identity resolver (`rnir`) and a package manager (`rnpkg`), but neither performs any operation yet. DotReticulum preserves that boundary. It does not invent a resolver protocol, name service, package repository or package format.

Neither tool is a service manager.

## rnir

Upstream `Resolver.resolve_identity(full_name)` is a static placeholder that returns `None`. It defines no lookup behavior, naming scheme, cache, authentication, destination or wire format.

The `rnir` command parses its options, initializes Reticulum and exits. Its upstream service branch only selects file logging and default verbosity; it does not resolve an identity.

```sh
rnir --help
rnir --config <path> -v
```

```text
rnir: upstream placeholder; initializes Reticulum and exits. No resolution/package operations.
--config PATH -v/--verbose -q/--quiet --version
```

The native command is an observable startup shell, not identity resolution. It initializes and disposes the native Reticulum runtime, labels its help and version as a placeholder, and rejects unsupported operations.

## rnpkg

Upstream `rnpkg` has the same configuration and verbosity startup shell, plus an `--exampleconfig` option. The entire example is the comment `# This is an example package manager configuration file.` followed by a blank line. There are no package installation, repository, dependency, signature or fetch operations.

```sh
rnpkg --help
rnpkg --exampleconfig
rnpkg --config <path> -q
```

```text
rnpkg: upstream placeholder; initializes Reticulum and exits. No resolution/package operations.
--config PATH -v/--verbose -q/--quiet --version --exampleconfig
```

The native command preserves this shell behavior and the exact minimal example. An ordinary invocation initializes and disposes the native runtime, and unsupported operations are rejected.

## Options and behaviour

Both commands accept `--config <path>` and counted `-v`/`--verbose` and `-q`/`--quiet` switches. An ordinary invocation opens a Reticulum runtime using the configured platform runtime and logging providers. Help, version and `rnpkg --exampleconfig` return without starting network interfaces. No identity lookup or package operation is authorized or performed.

The contract has no platform-specific implementation or wire behavior.

## Differences from upstream

- The native help and version output label each tool as a placeholder.
- Unsupported operations are rejected.
- Diagnostics and the generic invalid-input exit status follow native conventions rather than Python argparse.

Neither tool adds a resolver or package service.

## Verification

The pinned contract is RNS 1.5.7 `RNS/Resolver.py`, `RNS/Utilities/rnir.py` and `RNS/Utilities/rnpkg.py`. `tests/DotReticulum.Applications.Tests` covers the source-backed placeholder classification and the native startup and parser behavior.

A disclosed constructor-capture adapter observes the `RNS.Reticulum` arguments without starting upstream interfaces or touching reference state. This is startup-shell evidence, not unmodified Python daemon startup or network capability. No resolver or package interoperability is claimed. See [remaining capabilities](../TODO.md).
