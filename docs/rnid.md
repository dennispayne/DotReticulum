# rnid identity, encryption, and signatures

`rnid` manages Reticulum identities, destination hashes, network identity lookup and announces, file encryption, and signatures. Its utility contract targets RNS 1.5.7 (`a29bfb51c468945c96a1ee22cae0114e993e2391`). DotReticulum is experimental and AI-assisted, not an official or endorsed Reticulum implementation. A valid signature authenticates a key; it does not establish an independently trusted publisher.

```sh
rnid --generate identity.rid
rnid --show identity.rid
rnid -i identity.rid -p
rnid -i identity.rid -H app.aspect
rnid -i identity.rid -s document.txt
rnid -V document.txt
rnid -i identity.rid -S "Signed message" -w message.rsm
rnid -V message.rsm --meta
rnid -i identity.rid -e document.txt
rnid -i identity.rid -d document.txt.rfe -w recovered.txt
```

Use `rnid --help` for all options, including `-v`/`-q` verbosity controls and `--version`. From a source checkout, invoke the subcommand with `dotnet run --project src/DotReticulum.Applications -- rnid ...`.

## Identity and configuration

`-g/--generate` creates a raw private identity and refuses to overwrite unless `-f` is supplied. `--show` displays the identity hash and public key. `-i/--identity` selects an identity or hash; `-m/--import-pub` and `-M/--import-prv` import raw 64-byte files or hexadecimal, uppercase padded base32, or padded URL-safe base64. `-x/--export-pub` and `-X/--export-prv` export keys; private export explicitly writes secret key material. `-p/--print-identity` hides private keys unless `-P/--print-private` is also present. `-w/--write` selects output; public-key output receives a `.pub` suffix unless private output is requested.

Private files use the upstream raw X25519 private-key and Ed25519 seed format. Files are created owner-only on Unix; Windows files inherit directory ACLs, which must be protected. `--config` selects Reticulum configuration. Network requests use `-t` (default 15 seconds, range 0–3600). `-N/--no-cache` prevents cache/network identity resolution. `-i HASH` recalls a cached identity by destination or identity hash. `-R/--request` requests an unknown identity, including its `rns.id` destination, and waits up to the configured timeout. A hash-only `-H` operation does not initialize networking; `-a/--announce [app.aspect]` announces an owned identity, defaulting to `rns.id`.

## Encryption and signatures

RFE files concatenate independently identity-encrypted chunks without additional framing. Encryption processes 16 MiB plaintext chunks; corresponding ciphertext chunks are 16 MiB plus 96 bytes. Large RFE encryption/decryption and current detached signatures stream with bounded memory. A failed decryption can leave partial plaintext output; do not use that output when the command fails.

Current detached RSG signatures sign a MessagePack envelope containing the SHA-256 digest, not the original bytes. `--raw` creates a legacy 64-byte direct Ed25519 signature, which requires an explicit identity during verification. `-S/--sign-message` creates embedded RSM messages; `-r/--read` reads UTF-8 message text from a file. `-E/--embed-meta` embeds ConfigObj-style metadata; `--meta-spec` or an adjacent `.spec` validates and converts it. Metadata is displayed only after authentication. Supported built-in validators include string, integer, float, boolean, option, IPv4, list, and mixed-list types, with defaults, bounds, nested sections, and bounded interpolation. Arbitrary Python or custom validator functions are not executed. Quote spec expressions containing commas, for example `count = "integer(1, 5)"`.

`-B`, `-b`, `-U`, and `-F` select wrapped base32, URL-safe base64, Reticulum base256, and hexadecimal signatures. Wrapping uses 64-codepoint lines. Base256 uses upstream's 256-symbol alphabet, including supplementary Unicode code points. These encoding options do not change identity public/private key display, which remains hexadecimal.

Raw signing and embedded messages are limited to 16 MiB; signatures and metadata to 1 MiB; metadata nesting to 32 levels. Multiple file paths are processed in order and stop on first failure; this sequence behavior is not directory traversal. Existing output is not overwritten unless `-f/--force` is used.

## Security and native extensions

`-I/--stdin` reads operation input from standard input (also path `-`); `-O/--stdout` writes operation bytes to standard output and moves diagnostics to stderr. The pinned utility parses these hidden switches but does not wire them into file operations; native stdin/stdout support is an extension. Use binary-safe redirection.

If message text or metadata is omitted, an editor starts only with explicit `--allow-editor`. Set `--editor <executable>` or `EDITOR`, and optionally `--editor-args` for POSIX-style quoted arguments. No shell or fallback editor is invoked. Editor input files are created beneath the current directory and cleaned afterward; symlink output is rejected, input/output is bounded, and the owned process tree has a 120-second default deadline (maximum 3600 seconds) and a 1 MiB combined output limit. Cancellation terminates owned work. Extra files created by the editor are not recursively removed.

## Platform notes and differences from upstream

Identity and file formats are platform-independent. On Unix, private identity files are created owner-only; on Windows, they inherit directory ACLs. The native CLI adds binary-safe stdin/stdout operations, bounded streaming, and an explicitly authorized editor workflow. It does not execute arbitrary ConfigObj validators; its diagnostics, editor ownership, and progress output are native rather than Python CLI behavior. See [remaining capabilities](../TODO.md) for platform acceptance boundaries.

## Exit statuses

Identity and operation errors follow the pinned utility mapping: missing identity 2, missing private key 4, missing input 6, invalid encrypted filename 7, invalid identity 8, invalid aspects 9, invalid signature 10, existing output 11, and decryption failure 12. Invalid operation input returns 250, bounded/read failure 252, write failure 253, other errors 254, and cancellation 255. Parser errors return 2; conflicting operation, identity, or encoding choices return 1.

## Verification

`tests/Pester/Parity7Identity.Tests.ps1` exercises separate CLI processes, key persistence, editor ownership, network identity lookup, announces, and pinned Python encryption/decryption in both directions. `tests/DotReticulum.Applications.Tests/Parity7RnidTests.cs` covers reference vectors and identity utility behavior. These local TCP and vector results are distinct from routed or impaired-topology acceptance. The pinned reference is RNS 1.5.7 and LXMF 1.2.1; older stored vectors retain their recorded source revision. Version numbering is described in [README versioning](../README.md#versioning).