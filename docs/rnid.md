# rnid identity, encryption and signatures

`rnid` implements the pinned RNS 1.5.7 identity utility contracts
(`a29bfb51c468945c96a1ee22cae0114e993e2391`,
`RNS/Utilities/rnid.py`). DotReticulum is experimental and AI-assisted, not
an official Reticulum implementation. Signature validity authenticates a key,
not an independently trusted publisher.

```powershell
dotnet run --project src\DotReticulum.Applications -- rnid --generate identity.rid
dotnet run --project src\DotReticulum.Applications -- rnid --show identity.rid
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -p
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -x -B
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -s document.txt
dotnet run --project src\DotReticulum.Applications -- rnid -V document.txt
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -S "Signed message" -w message.rsm
dotnet run --project src\DotReticulum.Applications -- rnid -V message.rsm --meta
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -e document.txt
dotnet run --project src\DotReticulum.Applications -- rnid -i identity.rid -d document.txt.rfe -w recovered.txt
```

## Keys and information

`-m` imports public keys and `-M` imports private keys from a raw 64-byte file,
hex, uppercase padded base32, or padded URL-safe base64. `-x`/`-X` export
public/private text. `-w` writes public keys with a `.pub` suffix, unless
`-X` explicitly selects private output. `-p` hides private keys unless `-P`
is also supplied. Explicit private exports disclose secrets; do not send them
to other people or record them in logs.

Private files use the upstream raw X25519/private Ed25519 seed format.
They are created owner-only on Unix; Windows files inherit directory ACLs.
Protect those ACLs. Existing output is refused without `-f`. Legacy exact
`--generate path` and `--show path` retain their earlier output and error
status; ordinary upstream-style output uses the reference utility's statuses.

`-H app.aspect` calculates the destination hash; `-i HASH -N -H app.aspect`
does not initialize networking. `-i HASH` recalls identities from persistent
known destinations, by either destination or identity hash. `-R` requests
both the supplied hash and its `rns.id` destination and waits up to `-t`
seconds. `-N` prevents network/cache resolution. `-a [app.aspect]` announces
an owned identity (default `rns.id`). `--config` uses normal Reticulum
configuration, including configured shared-instance operation.

## Signatures and file encryption

Detached current RSG signatures sign the SHA-256-bearing MessagePack envelope,
not the original bytes. `--raw` creates the legacy 64-byte direct Ed25519
signature, which requires an explicit identity during verification. Multiple
paths are processed in order, stopping at the first failure. This is upstream's
“recursive” sequence operation, not directory traversal.

`-S` creates an embedded RSM; `-r` reads its UTF-8 message from a file.
`-E metadata.conf` embeds ConfigObj-style metadata; an adjacent `.spec`, or
`--meta-spec path`, validates/converts it. Reserved signer/public-key fields
cannot replace the true signing identity. Metadata is displayed only after
authentication. Native metadata supports nested sections, quoted/scalar/list
and multiline values, and built-in string/integer/float/boolean/option/IPv4,
list and mixed-list checks, defaults and bounded ConfigObj interpolation.
As in the pinned utility, quote spec expressions containing commas (for
example `count = "integer(1, 5)"`). Arbitrary Python/custom Validator functions
are not executed: the reference rnid also installs only the built-in Validator.
Unknown validators produce explicit errors, not silently accepted values.

`-B`, `-b`, `-U`, `-F` emit 64-codepoint wrapped base32, base64, Reticulum
base256, or hex signatures. The native verifier additionally accepts these
wrappers. Identity information follows upstream: `-U` does not change public
or private key display, which remains hex. Base256 is the exact upstream
256-symbol alphabet, including supplementary Unicode codepoints.

RFE files concatenate independent identity-encrypted chunks, with the pinned
16 MiB plaintext / 16 MiB + 96 ciphertext chunk sizes. No new framing is added.
Current detached signing/verification and RFE operations stream arbitrarily
large files with bounded memory. Raw signing and embedded messages are limited
to 16 MiB; signatures/metadata to 1 MiB and metadata nesting to 32 levels.
As upstream, a failed decryption can leave partial plaintext output. Do not
consume that output when the command fails.

## Explicit native safety extensions

`-I` or path `-` reads stdin; `-O` writes binary operation bytes to stdout,
moving status messages to stderr. The pinned utility parses these hidden flags
but does not wire stdin/stdout into file operations; native support is an
intentional extension. Use binary-safe redirection, not text recoding.

Omitted `-S` text or `-E` path opens an editor **only** with `--allow-editor`.
Choose `--editor executable` (or `EDITOR`) and optionally
`--editor-args "-NoProfile -File 'owned-editor.ps1'"`. No implicit shell or
editor fallback executes. Editor input files are created beneath the current
directory, cleaned after use, reject symlink output, and have bounded size.
The owned process tree has a 120-second deadline (override
`--editor-timeout`, maximum 3600 seconds) and 1 MiB combined output limit.
Cancellation terminates owned work. Editor-created extra files are not
recursively deleted.

Exit statuses follow the pinned constants: missing identity 2, missing private
key 4, missing input 6, invalid encrypted filename 7, invalid identity 8,
invalid aspects 9, invalid signature 10, existing output 11, decryption failure
12, invalid operation input 250, bounded/read failure 252, write failure 253,
other errors 254, cancellation 255; argument-parser errors use 2 and conflicting
operation/identity/encoding options use 1. Network timeouts are explicitly
bounded to 0–3600 seconds. `--help` describes all options.
Short flags may be clustered (`-xp`, `-pPB`), short option values may be
attached (`-mHEX`, `-Smessage`), and unambiguous long prefixes are accepted
(`--print-i`). Ambiguous prefixes fail with status 2. The native help text,
bounded-policy diagnostics and network progress presentation identify native
behavior rather than reproducing Python argparse usage text or spinner timing.

Reference fixture generation verifies actual RNS and LXMF imports, exact
revision and source digest. Unit vectors are not live network interoperability.
`Parity7Identity.Tests.ps1` exercises real processes, editor ownership, file
persistence and pinned Python encryption/decryption in both directions.
On Windows x64, both the managed and published NativeAOT acceptance suites
passed all 11 process tests with no skips: independent Python encrypted/decrypted files larger than 16 MiB,
Python TCP identity lookup and retained-cache recall after restart, and Python
validation of the native `rns.id` announce. These are direct local TCP topology
results, not claims of all routed/impaired/platform topologies. Editor success,
nonzero exit and forced deadline termination were executed against owned
PowerShell processes. The NativeAOT binary was published with linker/compiler
warnings treated as errors; exact commands and aggregate verification are
recorded in the repository development history.
