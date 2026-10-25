# rncp and rnx

Both tools run on a `ReticulumNode` (the application runtime: loads an `rnsd`-style config and starts the shared-instance client or owned interfaces, transport router, announce engine and link manager). They are wire-compatible with upstream `rncp` and `rnx` (destinations `rncp.receive` / `rnx.execute`, request paths `fetch_file` / `command`, upstream msgpack layouts).

```sh
DotReticulum.Applications rncp -l -a <identity hash> -s <dir> -b 30   # receive files, announce every 30 s
DotReticulum.Applications rncp -p                                      # print identity + destination
DotReticulum.Applications rncp <file> <destination>                    # send
DotReticulum.Applications rncp -f <file> <destination> -s <dir>        # fetch (listener needs -F, optional -j jail)

DotReticulum.Applications rnx -l --allow-execution -a <identity hash>  # explicitly authorize allowed identities
DotReticulum.Applications rnx <destination> "<command line>" -w 15 -m  # run a command, -m mirrors the exit code
```

All options: `rncp --help`, `rnx --help`. Both use `--config <dir>` like `rnsd` and keep their identity in `<dir>/storage/identities/<tool>` unless `-i` is given.

## Behaviour and limits

- Allow-lists combine `-a` with **one** file: `/etc/<tool>/allowed_identities`, otherwise `~/.config/<tool>/allowed_identities`, otherwise `~/.<tool>/allowed_identities`. A higher-priority empty file masks lower-priority files. Lines must contain exactly 32 hexadecimal characters; malformed 32-character entries fail closed. Identity, local send/save and fetch-jail paths expand the current user's `~`. Remote fetch paths are expanded by the listener, not by the requesting client.
- `rnx -l` additionally requires `--allow-execution`: this is a deliberate safety difference from upstream. An identity allow-list authorizes arbitrary executable commands, not a command sandbox. `-n` / `--noauth` grants that authority to everyone who can reach the listener. No command is implicitly run through a shell. Supplying a shell executable explicitly grants shell access and must be covered by the operator's policy. The programmatic `RnxListener` API likewise requires the caller to deliberately configure its identity authorization policy.
- rncp sends files segment-by-segment from owned streams and saves received resources through `OpenRead()`, without materializing spilled multipart files. Receive storage spills above the configured resource memory threshold; the default logical size limit is 1 GiB. See [resource policy and ownership](resources.md). Received files never overwrite unless `-O`; collisions get `.1`, `.2` suffixes; names are reduced to their base name.
- rnx splits the command like Python's `shlex.split` and starts the process directly, with no shell, so shell syntax (pipes, `&&`) does not work remotely, exactly as upstream. stdin is UTF-8 text. stdout and stderr on the native CLI are byte-preserving; unlike upstream's UTF-8-decoding display, arbitrary binary output does not fail decoding. Zero output limits return no bytes; negative limits use Python slicing (for example `--stdout -1` removes the final byte), while total lengths still report all drained bytes. Failed spawn returns exit 248. `-m` mirrors the remote exit code; without it, a completed command returns zero even if its exit code overlaps rnx's 240–249 protocol-error codes.
- Processes have explicit ownership: stdin writing and both output drains run concurrently, timeout covers blocked stdin, and timeout, an authenticated client's link teardown, or listener disposal terminates the process tree. Already-produced output and its original lengths survive timeout, with a null conclusion timestamp. Child commands never inherit a shell. Platform process-tree termination is best effort for descendants that deliberately detach or escape the operating system's process hierarchy.
- Native safety limits intentionally differ from upstream's unlimited buffering: at most four concurrent commands, a ten-minute listener-side maximum execution time, and 16 MiB retained per output stream by default. `RnxListenerOptions` can configure these bounds. Excess output is still drained and counted, but only a bounded prefix is returned. These are not new wire fields; older Python clients can read the standard eight-element result.
- rncp validates save directories before opening a runtime: missing directories return 3, non-writable directories return 4. Received files are streamed into a uniquely owned staging file and atomically moved into place, so failed writes do not delete the previous destination. `-P` / `--phy-rates` reports an average rate from resource transfer bytes (compressed/encrypted resource data), not uncompressed file length or interface-wide traffic. Native progress formatting is intentionally different from upstream's terminal spinner and sliding-window rate display. `--version` identifies DotReticulum and the pinned RNS 1.5.7 reference rather than claiming to be upstream.
- Both parsers support short-option clusters, attached short values, long `--option=value` forms and `--` before positional arguments. Incomplete invocation prints help and returns zero, as upstream; malformed options return 2. The native aliases `-c`, `-?` and accepting an unquoted multi-token `rnx` command are retained extensions. Diagnostics are sent to stderr rather than copying upstream's mixed stdout/logging display conventions. Native interactive `rnx -x` accepts case-insensitive `exit`, `quit`, and `clear`; an initial command is executed before the prompt, and EOF/exit ends successfully.
- Both tools use shared-instance election by default and can attach to a native or pinned Python owner without opening that owner's physical interfaces. The configured endpoint and RPC key must match; separate configuration directories need an explicit common key. `share_instance=no` opens an independent runtime. See [runtime operations](runtime-operations.md) for ownership, fallback and reconnect behavior.
- Upstream quirk on Windows: upstream Python `rncp` cannot receive files on Windows (it moves a file that is still open, and its `-s`/`-j` checks compare against `/`). The live tests run upstream through `tests/Interop/rncp_win_shim.py`, which verifies actual pinned imports and replaces that move with a copy only on Windows. This does not establish unmodified-upstream Windows filesystem compatibility.

## Pinned utility contract audit

Reference: RNS 1.5.7, `a29bfb51c468945c96a1ee22cae0114e993e2391`,
`RNS/Utilities/rncp.py` (`listen`, file receive/fetch callbacks, `send`, `fetch`, `main`)
and `RNS/Utilities/rnx.py` (`listen`, `execute_received_command`, `execute`, `main`).
`GenerateParity7TransferVectors.py` records source SHA-256 values and independently
executes binary output, positive/zero/negative slicing, failed spawn, parser failures
and version handling. Timing values are represented by their presence rather than
being fabricated as deterministic timestamps.

Acceptance covers all pinned option spellings, exact default allow-file precedence,
configuration/identity persistence, both mixed Python/.NET listener roles, real
separate CLI processes, file collision/overwrite/jail behavior, binary results,
failure exit codes, bounded concurrent I/O and owned-process termination.
Reference vectors, managed tests, mixed-node live tests and published-native
execution are distinct evidence levels; consult the development history for the
commands actually executed. Native interactive `rnx -x` is a line-at-a-time
command loop, not a PTY or the separate `rnsh` protocol.

| Utility | Exit contracts |
|---|---|
| rncp | 0 success, help/identity/version, or a reported refused/missing/failed fetch request (upstream quirk); 1 send/resource/configuration failure; 2 parser failure; 3 absent output directory; 4 unwritable output directory. |
| rnx | 0 completed execution unless `-m`; 240 missing remote return code with `-m`; 241 invalid destination; 242 no path; 243 link establishment failure; 244 closed link/request unavailable; 245 no execution result; 246 failed/result-time-limited download; 247 malformed result; 248 failed remote spawn; 249 null response. Runtime/configuration failure is 1; parser or missing execution authorization is 2. |

`-W` starts its download deadline when a response resource is observed, rather
than reducing the command execution allowance. A rncp peer that accepts a fetch
but never advertises its file fails after the bounded start allowance. Actual
resource transfer still uses the transport's resource retry/stall policy, not
an arbitrary whole-file deadline. A fetch jail follows the pinned reference's
lexical path restriction; it is not a filesystem/symlink sandbox.
Concurrent native receives with the same filename serialize only their final
publication through a fixed, bounded set of locks, rechecking suffixes after
staging. This preserves both completed payloads without buffering the file or
locking network input. Existing-file move collisions retry the next suffix.
These locks coordinate this process only: external writers are not covered,
and .NET's Unix `File.Move(overwrite:false)` implementation does not guarantee
a race-free no-replace rename against another process. Do not treat output
directories shared with untrusted concurrent writers as an atomic inbox.
The native timeout is enforced while writing stdin and waiting for process
exit, including zero/negative execution allowances. Upstream polls
`communicate(timeout=1)` before checking its deadline, so sub-second timeout
rounding is intentionally not reproduced. Unix `~user` account lookup is not
implemented; current-user `~` is supported on every platform.
