# Read-only Git over Reticulum

`rngit clone` creates a **bare** local Git repository from an upstream
`rngit` server. `rngit fetch` updates an existing bare repository using
upstream's incremental bundle protocol. This experimental, AI-assisted
implementation is not an official Reticulum utility. Its network path is entirely the .NET stack:
`ReticulumNode` → `ClientLinks` → identified `Link` → request/response
`Resource` transfers. Python is only a test peer, never a client dependency.
Git on `PATH` performs local bundle validation/import and object checking;
the client never invokes Git's network transports.

The same application also implements a read-only **`git-remote-rns` helper**.
Git owns the working tree, HEAD, local branches, tags and remote-tracking refs;
the helper advertises remote refs and imports objects, never updates Git's refs.
The direct `rngit` commands below retain their bare-repository behavior.
The Reticulum-hosted URL
`rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum` is this project's
authoritative reference upstream; GitHub is only a secondary comparison mirror.
Current live tests pin its retrieved commit
`a29bfb51c468945c96a1ee22cae0114e993e2391` (RNS 1.5.7).
Fetch updates into a bare repository with the .NET client, verify them, and
materialize a working checkout for `DOTRETICULUM_UPSTREAM` using local Git:

```powershell
git clone C:\Repos\reticulum.git C:\Reference\Reticulum
git -C C:\Reference\Reticulum checkout --detach a29bfb51c468945c96a1ee22cae0114e993e2391
```

The local clone does not contact a network remote. Do not change the live pin
merely because newer refs appear: compare and verify the snapshot first.
Stored vectors retain the SHA of the revision that generated them.

## Usage

### Ordinary Git (native helper)

Publish for your host, then install the **native executable**, not a Python
adapter or a shell wrapper, under Git's helper name in a directory on `PATH`.
For Windows (requires the NativeAOT MSVC toolchain):

```powershell
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts\git-native
New-Item -ItemType Directory -Force C:\Tools\DotReticulum
Copy-Item artifacts\git-native\DotReticulum.Applications.exe C:\Tools\DotReticulum\git-remote-rns.exe
$env:PATH = 'C:\Tools\DotReticulum;' + $env:PATH
$env:DOTRETICULUM_RNS_CONFIG = 'C:\RNS\Client'
$env:DOTRETICULUM_RNGIT_TIMEOUT = '900'
git clone rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository
git -C C:\Repos\repository fetch origin
git clone --bare rns://DESTINATION/GROUP/REPOSITORY C:\Repos\bare.git
```

On Unix publish for the host RID, copy the executable as `git-remote-rns`
(no extension), preserve its executable permission, and put its directory on
`PATH`. Windows is the currently exercised native platform; publishing alone
does not establish support on other systems. Do not install two different
helpers with that name earlier on `PATH`/Git's exec path.

`DOTRETICULUM_RNS_CONFIG` selects our Reticulum configuration; `RNS_CONFIG`
is the fallback environment alias. The default is `~/.dotreticulum`.
`DOTRETICULUM_RNGIT_TIMEOUT` sets a positive overall deadline in seconds
(default 7200, including Git protocol input). `DOTRETICULUM_RNGIT_MAX_RESPONSE`
sets the positive response byte cap (default 268435456). The identity remains
`storage\rngit` under that configuration. Python's `RNGIT_CONFIG`, aliases,
`client_config`, log file and shared-instance settings are not consumed:
this is an explicit .NET configuration/platform difference, not full
configuration parity.

Git discovers the executable by name. For managed development only, copy the
Release **apphost** (`DotReticulum.Applications.exe` on Windows) under the helper
name alongside the original DLL, deps and runtimeconfig files and dependencies.
The apphost still loads `DotReticulum.Applications.dll`; the renamed executable
selects helper mode. Alternatively invoke
`dotnet DotReticulum.Applications.dll git-remote-rns <remote> <url>` to inspect
the protocol manually; that command alone does not install a Git helper.

Supported Git commands are `capabilities`, `list`, `option progress true|false`
and batched `fetch <object-id> <ref>` terminated by a blank line. Capabilities
are **`fetch` and `option` only**; `list` is implicit in Git's protocol.
Unknown options return `unsupported`; malformed commands and unsupported
operations fail nonzero with diagnostics on stderr. Push, `list for-push`,
connect, shallow/depth and filter capabilities are not advertised.
Git can create working-tree/bare clones, choose branches (`clone -b feature`),
follow annotated tags, fetch incrementally, and retain local work while updating
remote-tracking refs through its normal refspecs. An unchanged fetch may require
only a ref listing because Git knows the objects already.

The helper lazily starts one owned .NET node/identified link per invocation
when Git requests refs, and tears it down on exit/error/cancellation.
Protocol stdout contains only Git responses. Diagnostics, progress and received
bundle-byte counts go to stderr. Helper input/output is explicitly UTF-8
(without an output BOM), independent of the Windows console code page.
Cancellation/timeout is failure, not an empty
successful response. Input lines and queued refs are bounded. Fetch validates
that each requested ref/object pair matches the listing, verifies/imports
bundles and checks the object database before acknowledging success.
Failure may leave imported unreferenced objects; Git decides whether to update
refs. The helper requires Git's `GIT_DIR` when importing; it does not initialize
repositories, set HEAD, write FETCH_HEAD, or rewrite configuration.

### Direct bare-repository client

Build with the .NET 10 SDK, then run:

```powershell
dotnet build src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release
dotnet src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll rngit clone rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 7200
```

Replace `DESTINATION` with the server's **32-hex-character repositories
destination hash**, not its identity hash. The group and repository name are
case-sensitive and may contain ASCII letters, digits, `.`, `_`, and `-`.
URL escaping, aliases, queries and fragments are not supported. For `clone`,
the destination directory must not exist; even an empty existing directory is
refused. `fetch` requires an existing bare Git directory, not a working tree
or its parent.

`--config` accepts the same configuration directory or file as `rnsd`.
A directory contains a file named `config`. For example, use your configured
Reticulum transport gateway:

```ini
[reticulum]
  enable_transport = no
[interfaces]
  [[Gateway]]
    type = TCPClientInterface
    target_host = YOUR_GATEWAY
    target_port = 4242
```

The server must be reachable through that interface and permit reads from the
client identity (persisted as `<config directory>\storage\rngit`).
There is no Python shared-instance attachment. See [rnsd.md](rnsd.md) for
the supported configuration subset.

`--timeout <seconds>` is an overall deadline covering startup, path discovery,
link establishment, all requests/transfers, and local Git commands (default
7200 seconds). Ctrl+C cancels. Teardown/cleanup may add a short delay.
`--max-response <bytes>` limits each response resource's advertised total
uncompressed size (default 268435456 bytes / 256 MiB, including metadata).
The list response additionally has a 4 MiB cap. Transfers are buffered in
memory by the current Resource implementation; this is not a streaming
large-repository client. Progress goes to stderr.

`--single-branch` transfers only the remote default branch, without tags or
other branches. It is accepted by both commands. Without it all advertised
refs are fetched in batches of 25. Immediately after listing, stderr reports
the remote default branch, its object ID, and the advertised ref count.

On success, the CLI prints the bare repository path, HEAD commit and tree,
and the number of **bundle bytes received over Reticulum** (not including
request, listing or RNS framing bytes). Zero bundle bytes means the server
considered the local objects sufficient; it is not evidence of fetching new
source objects.
It runs `git fsck --full --strict` before publishing the result. To inspect:

```powershell
git --git-dir=C:\Repos\repository.git fsck --full --strict
git --git-dir=C:\Repos\repository.git rev-parse HEAD 'HEAD^{tree}'
git --git-dir=C:\Repos\repository.git for-each-ref
```

Using explicit `--git-dir` also works on hosts with
`safe.bareRepository=explicit`. A sibling staging directory is moved into
place only after successful verification; failures remove the client's staging
files and never overwrite an existing target. A concurrent server ref update
that changes a requested bundle's advertised refs is rejected; retry rather
than accepting mismatched list/bundle snapshots.

## Incremental fetch

```powershell
dotnet src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll rngit fetch rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 900 --single-branch
```

The existing repository's refs provide per-ref and global `have` object IDs,
so upstream excludes history already reachable locally. Seed objects can come
from a local repository; the client does not require that every historical
object was originally obtained over Reticulum. To demonstrate actual source
retrieval rather than refs-only discovery, seed only an ancestor, use
`git --git-dir=... cat-file -e TARGET_COMMIT` to prove the desired commit is
absent beforehand, require nonzero received bundle bytes, then verify the
commit/tree and strict fsck afterward.

Fetch replaces the selected same-named refs, including non-fast-forward
updates, and sets HEAD to the advertised default branch. It does not prune
unadvertised refs. Do not use it on a bare repository with unrelated local
work you wish to preserve. Ref updates use one compare-and-swap transaction
against the initial local refs; a concurrent writer causes failure rather
than being overwritten. The origin URL is preserved; `remote.rngit.url` records
the fetch source. Clone records `remote.origin.url`. Neither direct command installs the helper.

Unlike clone's staging-and-rename, a failed fetch can leave newly imported
unreferenced objects in the existing repository. Object verification happens
before updating refs. The ref transaction is atomic, but HEAD/config changes
are subsequent local operations, not one transaction with it.

Exit codes: **0** success/help; **1** transport, server refusal, timeout,
cancellation or local Git/filesystem failure; **2** invalid arguments.

## Exact upstream protocol

The read-only implementation follows these files at
`e40191b3d193b46b7f2d8a44424a594cd758839b`; their rngit protocol is unchanged
at the current hosted live-reference pin `a29bfb51c468945c96a1ee22cae0114e993e2391`:

* `RNS\Utilities\rngit\client.py`: `handle_git_list`, `process_fetch_queue`.
* `RNS\Utilities\rngit\server.py`: `ReticulumGitNode.handle_list`,
  `handle_fetch`, `remote_identified`.
* `RNS\Utilities\rngit\util.py`: Git ref/SHA validation.
* `RNS\Link.py` / `RNS\Resource.py`: file-response metadata, resources and
  request IDs.

The focused helper audit at the live pin covers `client.py:main` (Git invokes
`git-remote-rns <remote-name> <url>`), `run` (negotiation, batching, options,
stdout flushing), `handle_git_list` (symbolic HEAD and refs), and
`process_fetch_queue` (local object discovery, have exclusions, bundle
verification/unbundle without ref updates). Upstream advertises `list`, `fetch`,
`push`, `option`, recognizes progress, reports unsupported options, and aborts
unknown commands with a stderr diagnostic and code 255 (invalid startup uses 1).
Our read-only helper deliberately omits push and uses 1 consistently for
failures. Upstream eagerly connects before negotiation; ours connects lazily
so offline capability discovery does not require a network.

Focused acceptance is ordinary Git discovery/clone/fetch on working and bare
repositories; default/non-default branches, tags, HEAD, remote-tracking refs;
actual missing-object and no-change fetches; clean protocol, denied reads,
malformed input, deadlines/cancellation and explicit unsupported-operation
failure; managed and published-native local reference processes; and a native
ordinary-Git fetch of provably missing hosted-source objects. Remaining Git
write/hosting/signing work is not a prerequisite for this read-only gate.

The destination name is `git.repositories`. After link establishment the client
identifies itself. All maps below are MessagePack maps; `0` and `1` are integer
keys, other keys and ref/SHA values are strings.

1. `/git/list`: `{0: "group/repository", "for_push": false}`.
   The response is a binary string: status byte followed by UTF-8 lines
   `<object-id> <ref-name>\n`, ending with `@refs/heads/<branch> HEAD\n`.
   Status zero means success. A large list is an ordinary response Resource
   carrying MessagePack `[request-id, response-bytes]`, not a file response.
2. `/git/fetch`: `{0: "group/repository", "refs":
   [{"sha": "<object-id>", "ref": "refs/..."}]}`.
   Requests are batched at 25 refs, matching upstream's default. The full
   history is requested for clone. Fetch additionally sends `"have": "<old-id>"`
   inside each ref entry when that local ref exists in the Python helper.
   Our shared importer sends a top-level
   `"have": ["<local-ref-id>", ...]` list (including remote-tracking tips), following upstream's implemented
   exclusions. Upstream validates that each have object exists on the server
   before passing `^<id>` to `git bundle create`.
3. Successful bundles are **raw file-response Resource bytes** with metadata
   `{1: 0}`, not a status byte prepended to a pack and not a MessagePack-wrapped
   bundle. Each bundle is verified, its refs compared with the list response,
   and its objects unbundled locally. Thin bundles may omit already-known
   ref tips from their headers; these are accepted only when that object
   was in the local reachable object set before the operation.
   A non-file success response `b"\x00"`
   means no bundle; local ref creation/fsck still must succeed.
4. Error responses are a status byte plus diagnostic text:
   `1` disallowed, `2` invalid request, `3` not found/not allowed, `255` remote
   failure. Unknown/malformed metadata is an error.

Resources may be compressed, encrypted, and segmented by RNS. Requests larger
than the link MDU also use Resources. **Channel is not part of rngit's
list/fetch protocol**, so the client does not invent Channel messages.

## Verification and limits

The opt-in `RngitTests.LiveRngit_*` C# test creates a local two-commit Git fixture with
random binary content, multiple branches and an annotated tag, then runs the
.NET CLI against the actual pinned upstream `ReticulumGitNode` on loopback.
The Python adapter only hosts the reference server; fixture creation, expected
Git IDs, process orchestration and result assertions are owned by C#.
More than 25 refs exercise request batching, resource-sized list and
fetch requests, and metadata-bearing bundle responses. It asserts exact HEAD,
tree and every ref, a clean strict fsck, denied-read handling, existing-target
preservation, response-size refusal, deadline handling and staging cleanup.
An ancestor-only seed then exercises incremental single-branch fetch:
the new commit and tree are proven absent with `git cat-file -e` before fetch
and present afterward; received bundle bytes must be positive; exact HEAD/tree,
ancestor relationship and strict fsck must match. A subsequent no-change
fetch must succeed and explicitly report zero bundle bytes.
It never uses fabricated network payloads or a successful early return.

```powershell
$env:DOTRETICULUM_LIVE = '1'
$env:DOTRETICULUM_UPSTREAM = 'C:\Path\To\Pinned\Reticulum'
$env:DOTRETICULUM_PYTHON = 'C:\Path\To\refvenv\Scripts\python.exe'
dotnet test tests\DotReticulum.Applications.Tests\DotReticulum.Applications.Tests.csproj -c Release --filter FullyQualifiedName~RngitTests
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $env:DOTRETICULUM_PYTHON -Upstream $env:DOTRETICULUM_UPSTREAM
pwsh -NoProfile -File tests\run-pester.ps1 -GitRemote -Python $env:DOTRETICULUM_PYTHON -Upstream $env:DOTRETICULUM_UPSTREAM -Application artifacts\git-native\DotReticulum.Applications.exe
```

The opt-in Pester Git suite launches real Git processes and installs the selected
managed apphost/native executable under the helper name on an isolated PATH.
A standalone C# fixture tool creates the source repository and incremental
commit; PowerShell owns orchestration and CLI acceptance, while the existing
thin Python adapter executes the pinned server. This exercises working-tree and
bare clones, feature-branch selection, tags/HEAD/remote-tracking refs, missing
commit/tree retrieval, local-branch preservation, unchanged fetches and explicit
protocol/server/input/deadline failures. Without `-GitRemote`, the runner does
not claim to have exercised the Python-backed Git helper suite.
Unicode ref acceptance runs the reference Python child in UTF-8 mode
(`PYTHONUTF8=1`, `PYTHONIOENCODING=utf-8`). Its Git subprocess text decoding
otherwise uses the Windows legacy locale and can misread Git's UTF-8 ref names.
This is disclosed reference runtime configuration, not a patched protocol or
evidence that upstream works under every default Windows code page.

The harness uses `reference_environment.verify_reference()` to verify imported
RNS module location, exact revision, and unmodified tracked RNS sources.
It redirects upstream temporary bundle storage into its owned fixture
directory and scopes `safe.bareRepository=all` to the Python test process:
upstream discovers bare repositories using cwd-based Git commands. Neither
changes production source or replaces protocol handlers.
The harness reads its control stdin nonblocking: a pending synchronous
Windows pipe read otherwise prevents Git's inherited-stdin detection in
upstream's request worker from completing. No subprocess or wire-protocol
shim is installed.

The direct client creates bare repositories; ordinary Git uses the helper for
working-tree or bare repositories. No push,
repository creation, release download/publishing, shallow clone,
submodule/LFS download or signature verification is implemented by the client.
Git itself owns working-tree checkout. Upstream's fetch handler has no depth option. Detached remote HEAD
is unsupported by upstream's list format and rejected when not represented as
a valid symbolic branch. The direct client recognizes SHA-1/SHA-256 formats.
The helper does not advertise Git's object-format negotiation; its ordinary-Git
acceptance uses SHA-1, and a local/remote format mismatch fails explicitly.
Arbitrary remote refs are checked by
equivalent ref-name validation before protocol output/import. The direct client
uses one local ref transaction; it no longer launches a redundant Git process
per ref after the shared validator has already checked the listing.

The loopback fixture alone does not establish public-network or mixed-topology
compatibility. Separate public-network retrieval evidence and limitations are
recorded in the [development history](../.github/development-history.md).
