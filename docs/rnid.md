# rnid: identities, encryption and signatures

The `rnid` utility lets you generate and manage Reticulum identities, compute destination hashes, look up and announce identities on the network, encrypt and decrypt files, and create and validate signatures. Its utility contract targets RNS 1.5.7 (`a29bfb51c468945c96a1ee22cae0114e993e2391`).

DotReticulum is experimental and AI-assisted, not an official or endorsed Reticulum implementation. A valid signature authenticates a key; it does not establish an independently trusted publisher.

## Usage

Generate a new identity and display it:

```sh
rnid --generate identity.rid
rnid --show identity.rid
rnid -i identity.rid -p
```

Compute a destination hash for an identity:

```sh
rnid -i identity.rid -H app.aspect
```

Sign a file and validate the signature:

```sh
rnid -i identity.rid -s document.txt
rnid -V document.txt
```

Sign a message with embedded content, then validate it and show its metadata:

```sh
rnid -i identity.rid -S "Signed message" -w message.rsm
rnid -V message.rsm --meta
```

Encrypt a file for an identity and decrypt it again:

```sh
rnid -i identity.rid -e document.txt
rnid -i identity.rid -d document.txt.rfe -w recovered.txt
```

From a source checkout, run `dotnet run --project src/DotReticulum.Applications -- rnid ...`.

## Command-line options

```text
Reticulum Identity & Encryption Utility (experimental DotReticulum)
usage: rnid [-i identity | -g path | -m public | -M private] [options]
  --show path                 Legacy identity hash/public-key display
  -g, --generate path         Generate a raw private identity (owner-only on Unix)
  -m, --import-pub data       Import raw file, hex, base32 or URL-safe base64 public keys
  -M, --import-prv data       Import raw file, hex, base32 or URL-safe base64 private keys
  -x, --export-pub            Export public keys
  -X, --export-prv            Export private keys (explicit disclosure)
  -p, --print-identity        Display identity; -P also displays private keys
  -H, --hash aspects         Display destination hash
  -a, --announce [aspects]    Announce destination (default rns.id)
  -R, --request              Request unknown identity from the network
  -N, --no-cache             Do not use cached/network identity information
  --config path              Reticulum configuration directory
  -t seconds                 Network timeout (default 15; maximum 3600)
  -s, --sign paths...         Sign files; --raw creates legacy raw signatures
  -V, --validate paths...     Verify RSG files or embedded RSM messages
  -S, --sign-message [text]   Sign embedded message; -r reads UTF-8 file
  -E, --embed-meta [path]     Embed ConfigObj metadata; --meta-spec validates it
  --meta                     Display verified RSM metadata
  -e, --encrypt paths...      Encrypt files to .rfe
  -d, --decrypt paths...      Decrypt .rfe files
  -w, --write path            Output path (public identities gain .pub)
  -f, --force                 Explicitly overwrite existing output
  -B, -b, -U, -F              Base32, URL-safe base64, base256, hex RSG output
  -I, --stdin                Read operation input from stdin (also accepts -)
  -O, --stdout               Write operation bytes to stdout; diagnostics to stderr
  --allow-editor             Explicitly authorize an editor for omitted message/metadata
  --editor executable        Editor path (otherwise EDITOR); no shell
  --editor-args arguments    Explicit editor arguments (POSIX-style quoted tokens)
  --editor-timeout seconds   Editor deadline (default 120; maximum 3600)
  -v, -q                     Compatibility verbosity flags
  --version                  Print reference compatibility version
Limits: 16 MiB raw/embedded message, 1 MiB signature/metadata, 32 metadata levels.
Current detached signatures and RFE files use bounded streaming for arbitrarily large files.
```

The help block omits a few accepted long forms: `-i`/`--identity`, `-P`/`--print-private`, `-r`/`--read`, `--raw`, `--meta-spec`, and `--base32`, `--base64`, `--base256` and `--hex` for `-B`, `-b`, `-U` and `-F`.

## Identities

`-g`/`--generate` creates a raw private identity. It **refuses to overwrite** an existing file unless you also pass `-f`. Private identity files use the upstream format: the raw X25519 private key followed by the Ed25519 seed. On Unix they are created owner-only. On Windows they inherit the directory's ACLs, so protect that directory.

`--show` displays an identity's hash and public key. `-i`/`--identity` selects an identity file or hash to operate on.

`-m`/`--import-pub` and `-M`/`--import-prv` import keys from a raw 64-byte file or from hexadecimal, uppercase padded base32 or padded URL-safe base64. `-x`/`--export-pub` and `-X`/`--export-prv` export keys; private export deliberately writes secret key material.

`-p`/`--print-identity` hides private keys unless `-P`/`--print-private` is also given. `-w`/`--write` selects the output path. Public-key output gets a `.pub` suffix unless private output is requested.

## Network lookup and announces

`--config` selects the Reticulum configuration. Network requests time out after `-t` seconds (default 15, range 0–3600).

- `-i HASH` recalls a cached identity by destination or identity hash.
- `-R`/`--request` requests an unknown identity, including its `rns.id` destination, and waits up to the timeout.
- `-N`/`--no-cache` prevents both cache and network identity resolution.
- `-a`/`--announce [app.aspect]` announces an identity you own; the default aspect is `rns.id`.

A hash-only `-H` operation does not start networking.

## Encryption

Encrypted RFE files are a plain concatenation of independently identity-encrypted chunks with no extra framing. Plaintext is processed in 16 MiB chunks, so each ciphertext chunk is 16 MiB plus 96 bytes. Encryption and decryption of large files stream with bounded memory.

A failed decryption can leave partial plaintext output. Do not use that output when the command fails.

## Signatures

Current detached RSG signatures sign a MessagePack envelope containing the file's SHA-256 digest, not the original bytes. They are streamed with bounded memory. `--raw` instead creates a legacy 64-byte direct Ed25519 signature; validating one requires an explicit identity.

`-S`/`--sign-message` creates an RSM message with embedded content. `-r`/`--read` reads the UTF-8 message text from a file.

`-B`, `-b`, `-U` and `-F` select wrapped base32, URL-safe base64, Reticulum base256 or hexadecimal signature output. Wrapped output uses 64-codepoint lines. Base256 uses upstream's 256-symbol alphabet, including supplementary Unicode code points. These options do not change how identity keys are displayed, which is always hexadecimal.

### Metadata

`-E`/`--embed-meta` embeds ConfigObj-style metadata in a signed message. `--meta-spec`, or an adjacent `.spec` file, validates and converts it. Metadata is displayed only after the message has been authenticated.

The built-in validators cover string, integer, float, boolean, option, IPv4, list and mixed-list types, with defaults, bounds, nested sections and bounded interpolation. Arbitrary Python or custom validator functions are not executed. Quote spec expressions that contain commas, for example `count = "integer(1, 5)"`.

## Limits and output

| Item | Limit |
|---|---|
| Raw signing and embedded messages | 16 MiB |
| Signatures and metadata | 1 MiB |
| Metadata nesting | 32 levels |

When you pass several file paths, they are processed in order and processing stops at the first failure. This is not directory traversal. Existing output is not overwritten unless you use `-f`/`--force`.

## Standard input and output

`-I`/`--stdin` reads operation input from standard input; the path `-` does the same. `-O`/`--stdout` writes operation bytes to standard output and moves diagnostics to stderr. Use binary-safe redirection.

The pinned utility parses these hidden switches but does not wire them into file operations, so working stdin/stdout support is a native extension.

## Editor

If message text or metadata is omitted, an editor starts only when you pass `--allow-editor`. Set the editor with `--editor <executable>` or `EDITOR`, and optionally pass POSIX-style quoted arguments with `--editor-args`. No shell or fallback editor is ever invoked.

- Editor input files are created beneath the current directory and cleaned up afterwards.
- Symlinked output is rejected, and input and output are bounded.
- The owned process tree has a 120-second default deadline (maximum 3600 seconds, set with `--editor-timeout`) and a 1 MiB combined output limit.
- Cancellation terminates the owned work.
- Extra files created by the editor are not recursively removed.

## Exit statuses

| Status | Meaning |
|---|---|
| 1 | Conflicting operation, identity or encoding choices |
| 2 | Parser error, or missing identity |
| 4 | Missing private key |
| 6 | Missing input |
| 7 | Invalid encrypted filename |
| 8 | Invalid identity |
| 9 | Invalid aspects |
| 10 | Invalid signature |
| 11 | Output already exists |
| 12 | Decryption failure |
| 250 | Invalid operation input |
| 252 | Bounded read failure |
| 253 | Write failure |
| 254 | Other errors |
| 255 | Cancellation |

The identity and operation statuses follow the pinned utility's mapping.

## Differences from upstream

Identity and file formats are platform-independent. The native CLI adds:

- binary-safe stdin/stdout operations
- bounded streaming for large files
- an explicitly authorized, bounded editor workflow

It does not execute arbitrary ConfigObj validators. Its diagnostics, editor ownership and progress output are native rather than the Python CLI's. See [remaining capabilities](../TODO.md) for platform acceptance boundaries.

## Verification

| Tests | What they show |
|---|---|
| `tests/Pester/Parity7Identity.Tests.ps1` | Separate CLI processes, key persistence, editor ownership, network identity lookup, announces, and encryption/decryption with the pinned Python reference in both directions. |
| `tests/DotReticulum.Applications.Tests/Parity7RnidTests.cs` | Reference vectors and identity utility behavior. |

These local TCP and vector results are separate from routed or impaired-topology acceptance. The pinned reference is RNS 1.5.7 with LXMF 1.2.1; older stored vectors keep their recorded source revision. Version numbering is described in [README versioning](../README.md#versioning).
