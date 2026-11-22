# rncp and rnx

The `rncp` utility is a simple file transfer tool: you can send files to, and fetch files from, another system over Reticulum. The `rnx` utility is a basic remote command execution program: you can run commands on a remote system and see their output. Its `-x` mode repeats commands interactively, but it is not a terminal session; for that, see [rnsh](rnsh.md).

Both are subcommands of the native `DotReticulum.Applications` executable and follow the pinned RNS 1.5.7 wire contracts (`rncp.receive` and `rnx.execute`).

## rncp

### Usage

On the receiving system, start a listener and say which identities may send files. `-b 30` announces the listener every 30 seconds:

```sh
rncp -l -a <identity-hash> -s <directory> -b 30
```

Print the identity and listening destination without starting a listener:

```sh
rncp -p
```

From another system, send a file to the listener, or fetch a file from a listener started with `-F`:

```sh
rncp <file> <destination-hash>
rncp -f <remote-file> <destination-hash> -s <directory>
```

From a source checkout, use `dotnet run --project src/DotReticulum.Applications -- rncp ...`.

### Command-line options

```text
DotReticulum rncp - Reticulum file transfer utility (experimental, wire-compatible with upstream rncp)

Usage:
  rncp <file> <destination>        Send a file to a listener
  rncp -f <file> <destination>     Fetch a file from a listener that allows it
  rncp -l [-s dir] [-a hash ...]   Listen for incoming transfers
  rncp -p                          Print this identity and listening destination, then exit

Options:
  --config <path>      Reticulum config directory or file (auto-discover; fallback ~/.reticulum)
  -l, --listen         Listen for incoming transfer requests
  -f, --fetch          Fetch <file> from the remote <destination> instead of sending
  -F, --allow-fetch    Listener: allow authenticated clients to fetch files
  -j, --jail <path>    Listener: restrict fetch requests to this directory
  -s, --save <path>    Save received files in this directory (must exist)
  -O, --overwrite      Overwrite existing received files instead of adding a postfix
  -a <hash>            Allow this identity hash (repeatable; also read from ~/.rncp/allowed_identities)
  -n, --no-auth        Accept requests from anyone
  -b <seconds>         Listener: announce interval, 0 to announce only at startup (default: no announce)
  -C, --no-compress    Disable automatic compression
  -P, --phy-rates      Display physical layer transfer rate
  -i <identity>        Path to the identity file to use (created when missing)
  -w <seconds>         Sender timeout before giving up (default 15)
  -S, --silent         Do not print transfer progress
  -v / -q              Increase / decrease logging verbosity
  -p, --print-identity Print identity and destination and exit
  -h, --help           Show this help
  --version            Show implementation and pinned reference versions
```

### Receiving files

A listener accepts no inbound transfers unless at least one allowed identity is configured. `-n` permits any requester. Fetching additionally requires the listener to be started with `-F`.

Received files are handled conservatively:

- Received names are reduced to their base name.
- Existing files are not overwritten. Name collisions get `.1`, `.2` and later suffixes. `-O` enables replacement.
- Save directories must already exist and be writable.
- Data is streamed through owned staging files and moved into place only after a successful transfer.
- Received resources spill to file-backed storage above the default 64 MiB memory threshold. The default logical resource limit is 1 GiB; see [resource policy and ownership](resources.md).

Per-process publication locks serialize finalization of files with the same name. External writers are not coordinated, and Unix no-replace rename races are not guaranteed safe. Do not use an output directory shared with untrusted writers as an atomic inbox.

`--jail` applies the pinned reference's lexical path restriction. It is not a filesystem or symlink sandbox.

`-P` reports resource-transfer bytes, which are compressed and encrypted. It does not report original file size or interface-wide traffic.

### Protocol and timing

`rncp` uses the destination `rncp.receive`, the request path `fetch_file`, and a Resource whose metadata contains the UTF-8 filename. Fetch replies are true, false, or `0xF0` when fetching is disallowed. All layouts are upstream MessagePack.

The fetch timeout bounds connection and request setup. The resource transfer itself relies on transport retry and stall behavior rather than an arbitrary whole-file deadline. A fetch that is accepted but never advertised fails after a bounded start allowance.

### Exit statuses

| Status | Meaning |
|---|---|
| 0 | Success; help, identity or version output; or the upstream-compatible refused or missing fetch response |
| 1 | Send, resource or configuration failure |
| 2 | Parser failure |
| 3 | Output directory missing |
| 4 | Output directory not writable |

## rnx

### Usage

On the listening system, start a listener. You must pass `--allow-execution` and name the identities that may run commands:

```sh
rnx -l --allow-execution -a <identity-hash>
```

From another system, run a command on the remote, wait up to 15 seconds, and mirror its exit code:

```sh
rnx <destination-hash> "<command and arguments>" -w 15 -m
```

Or enter the interactive mode pseudo-shell:

```sh
rnx <destination-hash> -x
```

### Command-line options

```text
DotReticulum rnx - Reticulum remote command execution (experimental, wire-compatible with upstream rnx)

Usage:
  rnx -l [-a hash ...]                 Listen for commands
  rnx <destination> <command>          Run a command on a remote listener
  rnx <destination> -x                 Interactive remote shell loop
  rnx -p                               Print this identity and listening destination, then exit

Options:
  --config <path>      Reticulum config directory or file (auto-discover; fallback ~/.reticulum)
  -l, --listen         Listen for incoming commands
  --allow-execution    Explicitly authorize the listener to start allowed peers' commands
  -p, --print-identity Print identity and destination and exit
  -a <hash>            Allow this identity hash (repeatable; also read from ~/.rnx/allowed_identities)
  -n, --noauth         Listener: accept commands from anyone (dangerous)
  -N, --noid           Client: do not identify to the listener
  -b, --no-announce    Listener: do not announce at startup
  -i <identity>        Path to the identity file to use
  -w <seconds>         Connect and execution timeout (default 15)
  -W <seconds>         Max result download time
  --stdin <text>       Pass this text on the remote command's stdin
  --stdout <bytes>     Max size of returned stdout
  --stderr <bytes>     Max size of returned stderr
  -m                   Mirror the remote exit code
  -d, --detailed       Show detailed result output
  -x, --interactive    Interactive mode
  -v / -q              Increase / decrease logging verbosity
  -h, --help           Show this help
  --version            Show implementation and pinned reference versions
```

### Authorization and security

`rnx -l` **refuses to start** unless `--allow-execution` is given and either an identity allow-list is configured or you explicitly choose `-n`.

- An allow-list authorizes execution of arbitrary commands. It is not a sandbox.
- `-n`/`--noauth` gives command access to any peer that can reach the listener.
- Commands are started directly, without an implicit shell. Explicitly running a shell executable grants shell access and must be part of your policy.

### Running commands

Commands are split with `shlex.split`-style quoting and the resulting executable is started directly. Pipes and shell operators therefore only work if you explicitly invoke a shell. Stdin is UTF-8 text. The native CLI preserves binary stdout and stderr bytes, unlike upstream's UTF-8 decoding.

Listener limits:

- at most four concurrent commands
- 16 MiB retained per output stream by default; output beyond the limit is still drained and counted
- at most ten minutes of execution time

Timeout, authenticated link teardown and listener disposal terminate the owned process tree on a best-effort basis. Detached descendants may escape OS process ownership.

Zero output limits return no bytes, and negative limits follow Python slicing semantics. `-W` starts its result-download deadline when the response resource is observed.

`rnx` uses the destination `rnx.execute` and request path `command`. The request is `[command bytes, timeout, stdout limit, stderr limit, stdin bytes]` and the response is `[executed, return code, stdout, stderr, stdout length, stderr length, started, concluded]`, in upstream MessagePack layouts.

### Interactive mode

`rnx -x` runs any initial command first, then prompts for more. `exit`, `quit` and `clear` are accepted case-insensitively, and EOF or exit ends the session successfully. This loop is not `rnsh`, a PTY or a full terminal emulator.

### Exit statuses

A completed command returns 0 unless `-m` mirrors the remote status. Without `-m`, a completed command returns 0 even if its own exit code overlaps a protocol-error status.

| Status | Meaning |
|---|---|
| 1 | Runtime or configuration failure |
| 2 | Parser failure, or missing explicit execution authorization |
| 240 | Remote status missing with `-m` |
| 241 | Invalid destination |
| 242 | No path |
| 243 | Link establishment failed |
| 244 | Link closed or request unavailable |
| 245 | No result |
| 246 | Result download failed or timed out |
| 247 | Malformed result |
| 248 | Remote spawn failure |
| 249 | Null response |

## Configuration shared by both tools

Both tools accept `--config <path>` for a Reticulum configuration directory or file, `-i <path>` for an identity file, and `-v`/`-q` to raise or lower logging verbosity.

Without `--config`, they search for an existing config in the system `/etc/reticulum` directory (on Windows, `\etc\reticulum` on the profile drive), then `~/.config/reticulum`. If neither exists, they use `~/.reticulum` and create a config there if needed. The default identity files are `<config directory>/storage/identities/rncp` and `<config directory>/storage/identities/rnx`; these are separate from the allowed-identities files below.

The tools attach to a shared native or pinned Python instance, or open their own runtime when the configuration sets `share_instance=no`; see [runtime operations](runtime-operations.md).

Allowed identities combine `-a` entries with the first existing file in this order:

1. `/etc/<tool>/allowed_identities`
2. `~/.config/<tool>/allowed_identities`
3. `~/.<tool>/allowed_identities`

A higher-priority file masks lower-priority files, even when it is empty. Each accepted line is a 32-character hexadecimal identity hash; malformed entries of that length fail closed.

The current user's `~` expands in local identity, send, save and fetch-jail paths. Remote fetch paths are expanded on the listener.

Both parsers accept short-option clusters, attached short values, long `--option=value` and `--` before positional arguments. Incomplete invocations print help and return 0; malformed options return 2. Diagnostics go to stderr. `--version` identifies DotReticulum and reference RNS 1.5.7 rather than claiming to be the upstream executable.

## Differences from upstream

- `rnx --allow-execution` is a native startup gate.
- `rnx` bounds command concurrency, runtime and output, and preserves binary output.
- `rncp` uses stricter staging ownership.
- `-c`, `-?` and unquoted multi-token commands for `rnx` are native extensions.
- Progress output is native, not upstream's terminal spinner or sliding-window rate display.
- Unix `~user` expansion is not implemented.
- Sub-second timeout rounding differs from upstream's polling loop.

### Windows upstream `rncp`

Upstream Python `rncp` on Windows cannot receive files with ordinary Windows paths: it tries to move a still-open file, and it compares `-s` and `-j` paths against `/`. The live interoperability harness therefore uses `tests/Interop/rncp_win_shim.py`. The shim verifies the actual pinned imports and replaces that move with a copy, only on Windows. It is a standing test-harness adaptation and does not demonstrate unmodified Python `rncp` Windows filesystem compatibility.

## Verification

| Tests | What they show |
|---|---|
| `tests/DotReticulum.Applications.Tests/RncpRnxTests.cs` | Parser, authorization, file and process behavior. |
| `tests/DotReticulum.Applications.Tests/LiveRncpRnxTests.cs` | Live tests with separate native and pinned Python processes. |

Pinned-reference vectors and live interoperability are separate evidence. Windows upstream file receive uses the disclosed `rncp_win_shim.py`, so no claim is made for unmodified upstream Windows filesystem behavior.
