# rnsh: remote shell

The `rnsh` utility opens a remote shell over Reticulum. It runs on Reticulum Channels and supports a persistent interactive terminal session as well as binary stdin, stdout and stderr streams. It is different from `rnx -x`, which sends repeated request/response commands and does not provide a PTY.

`rnsh` ships in the pinned RNS repository as `RNS/Utilities/rnsh` and is documented in the RNS manual. That version is included under the Reticulum License and is based on the original `rnsh` by Aaron Heise ([acehoss/rnsh](https://github.com/acehoss/rnsh), MIT License, Copyright (c) 2023 Aaron Heise), as its source headers state. The DotReticulum wire protocol follows RNS 1.5.7 `RNS/Utilities/rnsh/protocol.py`, protocol version 1.

## Usage

On the listening system, start a listener that allows one identity, announces once at startup and runs `/bin/sh`:

```sh
rnsh --config <shell-state> --rnsconfig <reticulum-config> -l -a <identity-hash> -b 0 -- /bin/sh
```

From another system, run a command on the listener and mirror its exit status:

```sh
rnsh --rnsconfig <reticulum-config> -m <destination-hash> -- command argument
```

Arguments after `--` stay discrete argv values; they are never joined into a shell command line.

## Command-line options

```text
Reticulum Remote Shell Utility (experimental)
rnsh [-c STATE] [--rnsconfig RNS] [-i IDENTITY] [-v|-q] [-p] [--version]
rnsh -l [-s SERVICE] [-b PERIOD] [-a HASH ...|-n] [-A|-C] -- [COMMAND ARGS...]
rnsh [-N] [-m] [-w SECONDS] DESTINATION -- [COMMAND ARGS...]
--config/-c is shell state; --rnsconfig is the Reticulum configuration.
-l/--listen explicitly authorizes remote process execution.
-n/--no-auth accepts anyone: dangerous. Default authorization is deny.
-A/--remote-command-as-args appends remote argv to the default command.
-C/--no-remote-command rejects supplied remote commands.
-N/--no-id suppresses identification; -m/--mirror mirrors the process exit.
-p/--print-identity prints identity (and destination in listener mode).
Native terminal backends use Linux/macOS PTY or Windows ConPTY; Windows terminal-attached outputs merge.
```

| Option | Meaning |
|---|---|
| `-c`, `--config STATE` | Shell identity and authorization state. Default `~/.config/rnsh` when present, otherwise `~/.rnsh`. |
| `--rnsconfig RNS` | Reticulum configuration. |
| `-i`, `--identity FILE` | Identity file. The listener defaults to `identity.default` or a sanitized service suffix; the initiator defaults to `identity`. |
| `-s`, `--service NAME` | Service identity suffix for a listener. |
| `-l`, `--listen` | Start a listener. |
| `-p`, `--print-identity` | Print the identity (and listener destination) without starting a listener. |
| `-b`, `--announce PERIOD` | Announce interval. `0` announces once; omitted means no announce. |
| `-a`, `--allowed HASH` | Allow an identity hash. Repeatable. |
| `-n`, `--no-auth` | Accept any identity. **Dangerous.** |
| `-A`, `--remote-command-as-args` | Append remote arguments to the configured default command. |
| `-C`, `--no-remote-command` | Reject remote arguments. |
| `-N`, `--no-id` | Do not identify to the listener. |
| `-m`, `--mirror` | Return the remote process status. |
| `-w`, `--timeout SECONDS` | Positive, finite connect and session deadline. Default 30 seconds. |
| `-v`, `--verbose` / `-q`, `--quiet` | Raise or lower logging verbosity. |
| `--version` | Show the version. |

## Authorization

Starting a listener explicitly authorizes child process execution. By default access is **denied** unless the initiator's identity is given with `-a` or listed in `<shell-state>/allowed_identities`. That file is re-read for each new session. `-n` bypasses identity authorization entirely.

The default listener command is the platform shell: `cmd.exe` on Windows, otherwise `$SHELL` or `/bin/sh`. Operator-supplied arguments are not concatenated into a shell command. `TERM` and `RNS_REMOTE_IDENTITY` are passed to the child. If you do not intend to grant shell access, use an allow-list and a constrained default command.

## Sessions and streams

Protocol stream IDs 0, 1 and 2 carry stdin, stdout and stderr. EOF flags delimit input, and window messages carry rows, columns and pixel dimensions. Output is read incrementally and channel queues are bounded. A listener accepts at most 16 concurrent sessions.

Child ownership and cancellation are explicit. Windows children run in a kill-on-close Job; Linux and macOS PTY children get a new session and process group. Process-tree termination is best effort for descendants that detach or escape OS ownership.

When an initiator session completes normally, disposal waits for the open link's close packet to be written before disposing the node that owns it. `Link.TeardownAsync` provides this local bearer-write completion; it is not a remote acknowledgement. Cancellation, manager shutdown and write failures settle the wait explicitly, and the session bounds it to five seconds rather than sleeping or assuming that an enqueued packet was sent.

## Terminal behaviour

The native terminal backends use ConPTY on Windows, and `posix_spawn` with a PTY, process groups and window ioctls on Linux and macOS.

The initiator saves and restores terminal state and enters raw mode. It polls the window size and rate-limits later updates using the reference's `RTT * 25` interval. The first changed size is compared with the command's actual initial dimensions, including changes made during the handshake. These escapes are supported: `~~`, `~.`, `~L` and `~?`.

Redirected pipe input bypasses escape processing so arbitrary bytes pass through. On Windows, ConPTY merges endpoints attached to its terminal surface. Independently redirected endpoints stay lossless, and explicit inherited handles support mixed modes. ConPTY does not reproduce POSIX termios behavior. All eight pipe-versus-terminal combinations of stdin, stdout and stderr are exercised on Windows and Linux, including stream separation and terminal merging.

## Platform notes

Native terminal backends are implemented for Windows, Linux and macOS, and redirected pipe processes work on Windows and Unix. Windows builds do not verify the Darwin native ABI.

The pinned Python shell depends on POSIX-only `fcntl`, `pty`, `termios` and `fork`, so its process and session implementation cannot run unmodified in Windows Python. Live sessions with Python peers use a Linux peer, including WSL where applicable.

### macOS

Actual macOS ARM64 execution has verified:

- controlling-terminal resize
- literal argv
- interrupt delivery
- process-group teardown
- terminal mode restoration, with the documented `PENDIN` allowance
- all endpoint combinations
- delayed first read
- the published native terminal initiator
- both pinned Python roles

Darwin needs its own native handling:

- `POSIX_SPAWN_SETSID`, open flags, window ioctls and the 72-byte LP64 termios layout, including the arm64 variadic `ioctl` calling convention.
- XNU rejects extended master ioctls until the PTY replica has opened, so the parent first opens an owned, non-controlling replica.
- XNU drains the controlling terminal before reporting a session leader's exit, so waiting for exit before reading can deadlock. An owned reader therefore drains terminal output immediately into a bounded 16-chunk queue, with backpressure when full. When only stdin uses the terminal, the unused primary output is drained without being reported as stdout or stderr. The reader releases the retained replica only when its input is idle and the child has exited. Disposal cancels and joins the pump and releases the replica. Initialization and read errors are fatal.
- Darwin applies spawn file actions before creating the new session. A fixed `/bin/sh` bootstrap therefore reopens one terminal endpoint after session creation to acquire the controlling terminal and foreground process group, then replaces itself with `exec -- "$@"`. The command stays discrete argv and is never interpolated as shell code, and no persistent helper process remains.
- All-pipe Darwin children use the same owned Unix spawn/wait backend as Linux.

The ABI and ordering references are Apple's [XNU headers](https://github.com/apple-oss-distributions/xnu/tree/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/sys), [`tty_dev.c`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/tty_dev.c#L989-L996), [`kern_exit.c:2286-2336`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/kern_exit.c#L2286-L2336), [`kern_exec.c`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/kern_exec.c), [`vfs_syscalls.c`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/vfs/vfs_syscalls.c) and [Apple's arm64 ABI documentation](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms).

## Exit status

The owned Unix spawn/wait backend, used on Linux and for all-pipe macOS children, returns the reference's raw `waitpid` status masked with `0xff`, not a normalized shell exit code. An ordinary `exit 7` therefore reports zero, as it does in pinned POSIX upstream, where `CallbackSubprocess.start.poll` applies the same mask. Windows uses native process exit codes.

## Differences from upstream

- The pinned server accepts `tcflags` but does not apply them; the native server does not apply them to its child either.
- The native CLI help and terminal behavior are not claims of Python UI parity.
- Pinned upstream has an EOF race, described below. DotReticulum owns child-input shutdown and teardown and does not reproduce the permanently hung listener.

### Upstream EOF race

In the pinned upstream, `close_stdin()` schedules termination while a waiter thread can reap the child before the regular polling callback sees termination. The listener's return code is then never set. The race does not happen on every run or platform.

The live EOF probe waits for the child to be ready before closing stdin, and requires the child's exact acknowledgement of a zero-byte read. A clean completion must return zero. A bounded cancellation is accepted only with that acknowledgement plus a fresh, link-correlated upstream observation that the child has exited while its listener is still RUNNING without a return code. An unexplained timeout fails. A session that keeps stdin open until the child exits is not evidence that EOF completion works.

## Verification

| Tests | What they show |
|---|---|
| `tests/DotReticulum.Applications.Tests/RnshTests.cs`, `RnshNativeTests.cs`, `RnshLiveTests.cs` | Protocol, process ownership, terminal behavior and live sessions with Python peers. |
| `tests/DotReticulum.Applications.Tests/RnshDarwinTests.cs` | Actual-macOS controlling-terminal resize, literal argv, interrupt delivery, process-group teardown and delayed first read. These cases explicitly skip on other systems. |

On Windows and Linux x64, native endpoint combinations and Python interoperability are exercised, including binary streams, resize, loss and retry, cancellation and recovery. Published NativeAOT terminal-hosted flows run on both systems. The terminal-mode restoration, endpoint-combination and published native terminal cases are also enabled for macOS, where a hosted run passed all 29 rnsh cases; that run's overall result failed for unrelated reasons. See the development history for run details.

On XNU, restoring canonical input with `TCSADRAIN` can add the transient `PENDIN` input-processing bit. The terminal probe permits only that documented bit, reports its before and after values if seen, and still compares every other flag, speed, control byte and raw-mode transition exactly. It does not flush input or change production restoration behavior. See [`tty.c:1358-1391`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/tty.c#L1358-L1391) and [`termios.h:259`](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/sys/termios.h#L259).

The Python live driver exercises protocol sessions, not upstream's interactive UI. The observed EOF race remains a bounded negative reference result, not successful EOF completion.
