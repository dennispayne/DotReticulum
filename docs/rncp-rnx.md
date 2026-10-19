# rncp and rnx

Both tools run on a `ReticulumNode` (the shared client runtime: loads an `rnsd`-style config, starts the interfaces, transport router, announce engine and link manager). They are wire-compatible with upstream `rncp` and `rnx` (destinations `rncp.receive` / `rnx.execute`, request paths `fetch_file` / `command`, upstream msgpack layouts).

```sh
DotReticulum.Applications rncp -l -a <identity hash> -s <dir> -b 30   # receive files, announce every 30 s
DotReticulum.Applications rncp -p                                      # print identity + destination
DotReticulum.Applications rncp <file> <destination>                    # send
DotReticulum.Applications rncp -f <file> <destination> -s <dir>        # fetch (listener needs -F, optional -j jail)

DotReticulum.Applications rnx -l -a <identity hash>                    # execute commands from allowed identities
DotReticulum.Applications rnx <destination> "<command line>" -w 15 -m  # run a command, -m mirrors the exit code
```

All options: `rncp --help`, `rnx --help`. Both use `--config <dir>` like `rnsd` and keep their identity in `<dir>/storage/identities/<tool>` unless `-i` is given.

## Behaviour and limits

- Allow-lists come from `-a`, `~/.rncp/allowed_identities` / `~/.rnx/allowed_identities` (and the `~/.config` and `/etc` equivalents). `-n` accepts everyone: for `rnx` that is remote code execution for anyone who can reach you.
- rncp keeps a whole file in memory (Resources are `byte[]`), so very large files need that much RAM. Received files never overwrite unless `-O`; collisions get `.1`, `.2` suffixes; names are reduced to their base name.
- rnx splits the command like Python's `shlex.split` and starts the process directly, with no shell, so shell syntax (pipes, `&&`) does not work remotely, exactly as upstream. Output limits, stdin, timeouts and the result array follow upstream.
- Neither tool can attach to a shared instance; each opens its own interfaces from its config.
- Upstream quirk on Windows: upstream Python `rncp` cannot receive files on Windows (it moves a file that is still open, and its `-s`/`-j` checks compare against `/`). The live tests run upstream through `tests/Interop/rncp_win_shim.py`, which only replaces that move with a copy.
