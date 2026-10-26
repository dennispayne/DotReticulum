# rncp and rnx

`rncp` transfers files over Reticulum. `rnx` runs request/response commands on a remote node; its `-x` mode repeats commands interactively and is not a terminal session. Both use the native `DotReticulum.Applications` executable and the pinned RNS 1.5.7 wire contracts (`rncp.receive` / `rnx.execute`).

```sh
rncp -l -a <identity-hash> -s <directory> -b 30
rncp -p
rncp <file> <destination-hash>
rncp -f <remote-file> <destination-hash> -s <directory>
rnx -l --allow-execution -a <identity-hash>
rnx <destination-hash> "<command and arguments>" -w 15 -m
```

When running from a source checkout, use `dotnet run --project src/DotReticulum.Applications -- rncp ...` or replace `rncp` with `rnx`. Use `rncp --help` and `rnx --help` for complete option lists.

## Options and configuration

Both accept `--config <path>` for a Reticulum configuration directory or file, `-i <path>` for an identity file, and `-v`/`-q` to increase/decrease logging verbosity. By default, identities are stored below the configured `storage/identities` directory. They can attach to a shared native or pinned Python instance, or open their own runtime when configuration sets `share_instance=no`. See [runtime operations](runtime-operations.md).

`rncp` options include `-l/--listen`, `-f/--fetch`, `-F/--allow-fetch`, `-j/--jail <path>`, `-s/--save <path>`, `-O/--overwrite`, repeatable `-a <identity-hash>`, `-n/--no-auth`, `-b <seconds>` (zero announces once; omitted means no announce), `-C/--no-compress`, `-P/--phy-rates`, `-w <seconds>` (default 15), and `-S/--silent`. `-p` prints the local identity and destination without starting a listener.

`rnx` options include `-l/--listen`, the required listener gate `--allow-execution`, repeatable `-a <identity-hash>`, `-n/--noauth`, `-N/--noid`, `-b/--no-announce`, `-w <seconds>` (default 15), `-W <seconds>` (result-download deadline), `--stdin <text>`, `--stdout <bytes>`, `--stderr <bytes>`, `-m` (mirror the remote exit code), `-d/--detailed`, and `-x/--interactive`. `-p` prints identity and destination.

Allow-lists combine `-a` entries with the first existing file in this order: `/etc/<tool>/allowed_identities`, `~/.config/<tool>/allowed_identities`, then `~/.<tool>/allowed_identities`. A higher-priority file masks lower-priority files even when empty. Each accepted line is a 32-character hexadecimal identity hash; malformed entries of that length fail closed. Current-user `~` expands in local identity, send/save, and fetch-jail paths; remote fetch paths are expanded on the listener. `rncp --jail` applies the pinned reference's lexical path restriction, not a filesystem or symlink sandbox.

## Authorization and security

`rncp` accepts no inbound transfers by default unless an allowed identity is configured; `-n` permits any requester. A fetch listener must additionally be started with `-F`. `rnx -l` refuses to start unless `--allow-execution` is given and an identity allow-list is configured, or the operator explicitly chooses `-n`. An allow-list authorizes execution of arbitrary commands, not a sandbox. `-n/--noauth` gives command access to any reachable peer. Commands are started directly, without an implicit shell; explicitly supplying a shell executable grants shell access and must be part of the operator's policy.

`rncp` saves received files without overwriting by default, adds `.1`, `.2`, and later suffixes on name collisions, and reduces received names to their base name. `-O` enables replacement. Save directories must already exist and be writable. Data is streamed through owned staging files and moved into place only after a successful transfer. Per-process publication locks serialize same-name finalization; external writers are not coordinated, and Unix no-replace rename races are not guaranteed safe. Do not use an output directory shared with untrusted writers as an atomic inbox. Received resources spill to file-backed storage above the default 64 MiB memory threshold. The default logical resource limit is 1 GiB; see [resource policy and ownership](resources.md). `-P` reports resource-transfer bytes (compressed/encrypted), not original file size or interface-wide traffic.

`rnx` splits commands using `shlex.split`-style quoting and starts the resulting executable directly. Pipes and shell operators therefore do not work unless a shell is explicitly invoked. Stdin is UTF-8 text. Native CLI stdout/stderr preserve binary output bytes, unlike upstream's UTF-8 decoding. Output limits bound retained bytes per stream but continue draining and counting output; default listener limits are 16 MiB per stream and at most four concurrent commands. Listener execution time is capped at ten minutes. Timeout, authenticated link teardown, and listener disposal terminate the owned process tree on a best-effort basis; detached descendants may escape OS process ownership. Zero output limits return no bytes; negative limits follow Python slicing semantics. Failed spawn returns 248. `-m` returns the remote exit code; otherwise a completed command returns zero even if that code overlaps a protocol-error status.

## Behaviour and platform notes

`rncp` uses destination `rncp.receive`, request path `fetch_file`, and a Resource whose metadata contains the UTF-8 filename. Fetch replies are true/false or `0xF0` when fetch is disallowed. `rnx` uses destination `rnx.execute`, request path `command`, request data `[command bytes, timeout, stdout limit, stderr limit, stdin bytes]`, and response `[executed, return code, stdout, stderr, stdout length, stderr length, started, concluded]`, all in upstream MessagePack layouts. `rncp`'s fetch timeout bounds connection/request setup; resource transfer uses transport retry and stall behavior rather than an arbitrary whole-file deadline. `-W` in `rnx` starts its result-download deadline when the response resource is observed. A fetch accepted but never advertised fails after a bounded start allowance.

Windows upstream Python `rncp` cannot receive files using ordinary Windows paths: it attempts to move a still-open file and compares `-s` / `-j` paths against `/`. The live interoperability harness uses `tests/Interop/rncp_win_shim.py`, verifies actual pinned imports, and replaces that move with a copy only on Windows. The shim is a standing test-harness adaptation; it does not demonstrate unmodified Python `rncp` Windows filesystem compatibility.

Both native parsers accept short-option clusters, attached short values, long `--option=value`, and `--` before positional arguments. Incomplete invocations print help and return zero; malformed options return 2. Native extensions include `-c`, `-?`, and unquoted multi-token command input for `rnx`. Diagnostics go to stderr. Native `rnx -x` supports case-insensitive `exit`, `quit`, and `clear`; an initial command runs before the prompt, and EOF/exit succeeds. This loop is not `rnsh`, a PTY, or a full terminal emulator. `rncp --version` and `rnx --version` identify DotReticulum and reference RNS 1.5.7 rather than claiming to be upstream executables.

## Differences from upstream

The explicit `rnx --allow-execution` startup gate, bounded command concurrency/runtime/output, binary-safe CLI output, and stricter staging ownership are native safety and behavior differences. Native progress output is not upstream's terminal spinner or sliding-window rate display. Unix `~user` expansion is not implemented. Sub-second timeout rounding differs from upstream's polling loop.

## Exit statuses

`rncp` returns 0 for success, help/identity/version, and the upstream-compatible refused or missing fetch response; 1 for send/resource/configuration failure; 2 for parser failure; 3 for a missing output directory; and 4 for an unwritable output directory. `rnx` returns 0 for completed commands unless `-m` mirrors the remote status. Its protocol errors use 240 (missing remote status with `-m`), 241 (invalid destination), 242 (no path), 243 (link establishment), 244 (closed link/request unavailable), 245 (no result), 246 (result download failed/timed out), 247 (malformed result), 248 (remote spawn failure), and 249 (null response). Runtime/configuration failure is 1; parser or missing explicit execution authorization is 2.

## Verification

`tests/DotReticulum.Applications.Tests/RncpRnxTests.cs` covers parser, authorization, file, and process behavior; `LiveRncpRnxTests.cs` exercises separate native and pinned Python processes. Pinned-reference vectors and live interoperability are separate evidence. Windows upstream file receive uses the disclosed `rncp_win_shim.py`; no claim is made for unmodified-upstream Windows filesystem behavior.