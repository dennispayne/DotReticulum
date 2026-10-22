# Git over Reticulum

`rngit clone` creates a **bare** local Git repository from an upstream
`rngit` server. `rngit fetch` updates an existing bare repository using
upstream's incremental bundle protocol. This experimental, AI-assisted
implementation is not an official Reticulum utility. Its network path is entirely the .NET stack:
`ReticulumNode` → `ClientLinks` → identified `Link` → request/response
`Resource` transfers. Python is only a test peer, never a client dependency.
Git on `PATH` performs local bundle validation/import and object checking;
the client never invokes Git's network transports.

The same application also implements a read/write **`git-remote-rns` helper**.
Git owns the working tree, HEAD, local branches, tags and remote-tracking refs;
the helper advertises remote refs, imports fetched objects and sends push
operations to the server. It never updates the caller's local refs.
The direct `rngit` commands below retain their bare-repository behavior.
The application also contains a native repository node, management client,
release/work stores, NomadNet page service and `rngcs` signer. These are
separate surfaces from the completed ordinary-Git helper; their evidence and
remaining acceptance boundaries are recorded in the development history.
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

### Repository node and management

The repository node uses a separate rngit configuration directory and the
existing .NET Reticulum interface configuration:

```powershell
DotReticulum.Applications.exe rngit node --config C:\RNGit\Server --rnsconfig C:\RNS\Server
DotReticulum.Applications.exe rngit node --config C:\RNGit\Server --print-identity
DotReticulum.Applications.exe rngit create rns://NODE/team/project.git --config C:\RNGit\Client --rnsconfig C:\RNS\Client
DotReticulum.Applications.exe rngit work rns://NODE/team/project.git create --title "Design" --content C:\Owned\design.md --config C:\RNGit\Client --rnsconfig C:\RNS\Client
DotReticulum.Applications.exe rngit release rns://NODE/team/project.git create v1:C:\Owned\artifacts --content C:\Owned\notes.md --config C:\RNGit\Client --rnsconfig C:\RNS\Client
DotReticulum.Applications.exe rngit release C:\Owned\manifest.rsm verify --offline --signer IDENTITY_HASH
```

`NODE` denotes the 32-character repositories destination hash, not the
identity hash. `--config` selects rngit's directory; `--rnsconfig` selects
our .NET network configuration. The older direct clone/fetch commands retain
their existing `--config` network meaning. Install copies of the published
executable as `rngit`, `rngcs` and `git-remote-rns` (with `.exe` on Windows);
their filenames select the corresponding utility.

The server's `config` follows the source-backed flat/list rngit settings:

```ini
[rngit]
node_name = Private development node
announce_interval = 360
mirror_interval = 24
record_stats = yes
[aliases]
maintainer = 0123456789abcdef0123456789abcdef
[repositories]
team = C:\Owned\Repositories
[access]
team = read:all, create:maintainer, write:maintainer, admin:maintainer
[pages]
serve_nomadnet = yes
unicode_icons = yes
media_conversion = yes
```

Announce intervals are minutes; mirror intervals are hours. Existing bare
repositories are discovered under configured group directories. Keys are
raw 64-byte `repositories_identity` and `client_identity`; `stats` uses the
reference MessagePack schema. Corrupt identities are never replaced.
Back up the configuration and keys together with repositories and their
adjacent `.allowed`, `.work` and `.releases` state. Protect Windows directory
ACLs; generated Unix private-key files are owner-only.

Group `.allowed` entries are combined with `[access]`; nonempty
repository permission entries override group entries. Explicit `none` takes
precedence over `all` and administrators. Identity aliases here identify
callers, unlike helper aliases which identify destination nodes.
Management distinguishes readable-but-denied from hidden repositories
without disclosing protected contents. NomadNet anonymous permissions are
resolved explicitly; an invalid all-zero cryptographic key is not accepted
to emulate upstream's anonymous sentinel.

Native mutation commands never invoke an editor implicitly. Supply
`--content <owned-file>` or explicitly authorize bounded `EDITOR` execution
with `--allow-exec`. Deletion and latest-release prompts retain the default
refusal; `--yes` explicitly confirms the requested mutation. Python's interactive runtime
shell is not provided by the native node. Executable templates and permission
resolvers require operator authorization; static `.mu` templates do not.
External media backends are optional executables, not Python client
dependencies. Backend absence/failure is diagnosed and does not prove image
conversion support.

`rngcs -Y sign|find-principals|check-novalidate|verify` implements Git's signer
invocation surface. SSHSIG is a transport envelope around Reticulum RSG:
structural checking does not verify a signature. Cryptographic validity does
not establish publisher trust. Release verification with an independently
specified signer checks that identity; an embedded artifact key alone is
never described as a trusted publisher. Commit/tag verification separately
checks author/tagger identity.

### Collaboration, presentation and safety boundaries

`fork` and `mirror` take a source URL followed by a target repository URL.
Both persist their source next to the bare repository. Manual `sync` updates
either kind; periodic synchronization selects mirrors whose interval has
elapsed. Mirror synchronization restores the source's default HEAD, while
fork synchronization preserves the fork's HEAD. RNS sources always use the
native stack. HTTP/HTTPS/SSH sources invoke local Git only when the node
operator passes `--allow-external`; filesystem and other sources are not
implicitly authorized. A failed or cancelled initialization is diagnosed and
its owned staging directory is removed; imported objects and interrupted
multi-step release drafts are not described as a transactional rollback.

Release operations are `list`, `view <tag>`, `create <tag>:<artifacts-directory>`,
`latest <tag>`, `delete <tag>`, and `fetch <tag>:<artifact-pattern>`. Use
`latest:all` explicitly to fetch every artifact of the selected latest
release. Creation writes `manifest.rsm` and artifact `.rsg` sidecars into
the owned artifacts directory. Fetch validates the selected artifacts and
saves `<package>_<version>.rsm` beside them. `release <manifest> verify`
performs offline verification; `--signer` can require a particular identity
hash or load an explicitly supplied raw private identity file. It does not
modify a personal trust store. Git's allowed-signers file is not a trust
database for `rngcs`: the pinned signer ignores that option, while principal,
author/tagger and cryptographic checks remain distinct.

Work operations are `list`, `view`, `create`, `propose`, `edit`, `delete`,
`update` (also `comment`), `complete`, `activate` and `perms`. Select
`active`, `completed`, `proposed`, or `all` for listing; mutations and views
use an explicit document ID where applicable. Content is trimmed before
signing, matching server validation. Document permissions inherit from the
repository unless overridden; mutation authorization and hidden-versus-denied
responses are enforced before protected content is returned.

NomadNet serves index/group/repository/tree/blob/history/commit/ref/statistics,
release and work pages, plus blob/artifact/document downloads and `/media`.
Place static overrides under `templates`; `base.mu` recognizes
`{NODE_NAME}`, `{VERSION}`, `{NAVIGATION}`, `{PAGE_CONTENT}` and `{GEN_TIME}`.
Generation timing is measured per request. Native Markdown implements the
reference's default rendering path, including its code-point width fallback
when optional `wcwidth` is absent. Optional Python Pygments highlighting and
wcwidth-enhanced layout are not provided. Byte-differential fixtures cover
the exercised default rendering, not every conceivable Markdown document or
terminal font. Unsafe filesystem links/download paths are rejected and
media refs are encoded rather than preserving unsafe upstream URL behavior.
Static `.mu` files are not treated as executable merely because Windows
reports `X_OK`; executable permission/template resolution requires explicit
`--allow-exec` and bounded process execution.

Media backend discovery follows `magick`, `convert`, `gm`, `ffmpeg`, `avconv`;
`RNGIT_MEDIA_BACKEND` can select a supported backend. Conversion bounds input,
output, runtime and owned subprocess lifetime. Missing/failed conversion is
diagnosed; page delivery may retain the original image as upstream does.
Real FFmpeg/WebP conversion was exercised on Windows; other backend families
have argument-selection/failure coverage, not installed-encoder evidence.

CLI syntax failures exit 2, client-operation failures exit 1, and fatal node
initialization failures exit 255. Errors use stderr rather than upstream's
console-progress output; help identifies this native experimental utility.
Non-interactive content files, explicit process/network authorization,
resource limits and rejection of Python's interactive runtime are intentional
.NET safety/API differences, not claims of Python runtime compatibility.

The live reference-client adapter uses the actual pinned
`server.ReticulumGitClient`. On Windows it closes the received resource file
immediately before that client's original retention callback moves it:
upstream otherwise attempts to unlink an open file and raises WinError 32.
The original callback, request handlers and wire/resource implementation
remain in use; the adapter reads/closes the returned file and emits observations.
This proves interoperation with that disclosed Windows file-lifetime shim,
not an unmodified upstream Windows resource-retention path.

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
$env:RNGIT_CONFIG = 'C:\RNGit\Client'
$env:DOTRETICULUM_RNGIT_TIMEOUT = '900'
git clone rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository
git -C C:\Repos\repository fetch origin
git -C C:\Repos\repository push origin main
git -C C:\Repos\repository push origin refs/tags/v1
git -C C:\Repos\repository push origin --delete old-branch
git clone --bare rns://DESTINATION/GROUP/REPOSITORY C:\Repos\bare.git
```

On Unix publish for the host RID, copy the executable as `git-remote-rns`
(no extension), preserve its executable permission, and put its directory on
`PATH`. Windows is the currently exercised native platform; publishing alone
does not establish support on other systems. Do not install two different
helpers with that name earlier on `PATH`/Git's exec path.

`DOTRETICULUM_RNS_CONFIG` selects our Reticulum configuration; `RNS_CONFIG`
is the fallback environment alias. With neither set, use the same configuration
discovery as `rnsd`: an existing system `/etc/reticulum/config`, then
`~/.config/reticulum/config`, otherwise `~/.reticulum` (with the Windows
system-directory mapping described in [rnsd.md](rnsd.md)).
`DOTRETICULUM_RNGIT_TIMEOUT` sets a positive overall deadline in seconds
(default 7200, including Git protocol input). `DOTRETICULUM_RNGIT_MAX_RESPONSE`
sets the positive response byte cap (default 268435456).
`DOTRETICULUM_RNGIT_MAX_PUSH` limits each outgoing bundle before it is loaded
into memory (default 268435456 bytes). Local Git prepares bundles on disk;
that temporary disk use can exceed the transfer cap before Git finishes.
Transfers still use buffered RNS resources, not a streaming large-repository
implementation.

`RNGIT_CONFIG` independently selects the helper client directory. It contains
`client_config`, `client_identity` (64 raw private-key bytes) and `client_log`.
Without it, the pinned upstream default is preserved: `~/.rngit`, or
`~/.rngit/reticulum` when `~/.config/rngit/config` exists. This surprising
default is taken from the actual reference, not its comments. Missing client
configuration/identity files are created; identity publication is atomic and
concurrent creators load the same winner. Corrupt keys are not replaced.
Unix keys are owner-only; Windows users must protect the directory ACL.

```ini
[client]
ref_batch_size = 25
[aliases]
my_node = 0123456789abcdef0123456789abcdef
[logging]
loglevel = 4
```

Aliases are case-sensitive: `rns://my_node/group/repository`. Literal destination
hashes take precedence. Fetch batches default to 25 and clamp to 1024. Unlike
upstream's non-progressing zero-batch loop, nonpositive values fail explicitly.
Logging clamps to the actual upstream constants -1 (off) through 8; filtered
node/session diagnostics go to `client_log`, rotated at 5 MiB. Protocol errors
always reach stderr. Flat sections, quoted scalar values and comments are
supported; malformed aliases, duplicate entries, nested/list/multiline/
interpolated values and oversized input fail with a diagnostic instead of
being ignored or misparsed. Unknown scalar settings retain upstream's
ignore behavior. This bounded parser is not full ConfigObj.

**Identity migration:** the helper now uses upstream's `client_identity`, not
the older .NET `<RNS config>\storage\rngit`. To preserve an existing authorized
identity, explicitly copy that file into the selected client directory as
`client_identity` **before first use**, without overwriting another identity.
The direct `rngit clone/fetch` CLI keeps its existing RNS storage identity.
The helper uses the same shared-instance election as the other native applications,
including attachment to a native or pinned Python owner. Endpoint and RPC
authentication settings must match; see [runtime operations](runtime-operations.md).

Git discovers the executable by name. For managed development only, copy the
Release **apphost** (`DotReticulum.Applications.exe` on Windows) under the helper
name alongside the original DLL, deps and runtimeconfig files and dependencies.
The apphost still loads `DotReticulum.Applications.dll`; the renamed executable
selects helper mode. Alternatively invoke
`dotnet DotReticulum.Applications.dll git-remote-rns <remote> <url>` to inspect
the protocol manually; that command alone does not install a Git helper.

Supported Git commands are `capabilities`, `list`, `list for-push`,
`option progress true|false`, batched `fetch <object-id> <ref>` and
`push [+]<source>:<destination-ref>`, each batch terminated by a blank line.
Capabilities are **`fetch`, `push` and `option`**; `list` is implicit.
Unknown options return `unsupported`; malformed commands and unsupported
operations fail nonzero with diagnostics on stderr. Connect, shallow/depth,
filter and atomic-push capabilities are not advertised. Unsupported dry-run
requests must fail before any remote mutation.
Git can create working-tree/bare clones, choose branches (`clone -b feature`),
follow annotated tags, fetch incrementally, and retain local work while updating
remote-tracking refs through its normal refspecs. An unchanged fetch may require
only a ref listing because Git knows the objects already.

The helper lazily reads client configuration and starts one owned .NET
node/identified link per invocation
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

### Push behavior and limits

Push listing uses upstream's write-permission check. New branches, lightweight
and annotated tags, updates, forced updates and deletion use the pinned
`/git/push` and `/git/delete` contracts. Arbitrary locally resolvable sources
(including raw object IDs) are named in an isolated temporary bare repository
with a read-only object alternate; the caller's refs and HEAD are not changed.
Remote tips present locally are excluded from bundles. If all required objects
already exist remotely, the helper sends an `update_ref` operation without a
bundle. This is a real zero-byte operation, not a failed-bundle fallback.

**Important upstream difference:** existing-object operations require explicit
force whenever an existing target changes SHA, **even for fast-forward
changes**. Bundle pushes instead use Git fetch's ancestry/tag decisions.
The helper preserves both behaviors; it does not silently upgrade a push to
force. Delete uses upstream's behavior, including leaving symbolic HEAD alone.
Every ref gets `ok <ref>` or a correctly quoted `error <ref> <reason>`; one
rejected ref does not hide success or prevent later independent refs.

Pushes are sequential, **not atomic**. Upstream has no compare-and-swap or
remote lease protocol: concurrent writes to the same ref are not transactionally
protected. Git may apply local advertisement checks, but this is not a remote
lease guarantee. Separate sessions pushing distinct refs are supported.
Cancellation/deadline or loss after transmission cannot prove whether the
server applied the in-flight operation; the helper fails explicitly and the
caller should fetch/inspect before retrying. Cleanup removes owned temporary
repositories on normal error/cancellation; forcibly killing a process can leave
an unreferenced `rngit-push-*` directory in its Git directory.

The ordinary-Git helper explicitly supports **SHA-1 only**. Nonempty remote
SHA-256 listings and local SHA-256 transfers fail with a diagnostic; no Git
object-format negotiation is advertised. An empty remote does not expose its
object format in the upstream protocol: it is assumed SHA-1 and an incompatible
server must reject the bundle. The separate direct bare client retains its
SHA-1/SHA-256 behavior. Native feature evidence is Windows x64 only; other
platforms remain unverified.

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
Shared-instance election can attach to a native or pinned Python owner rather than
opening physical interfaces; a separate configuration directory requires a common
explicit RPC key. Set `share_instance=no` for independent interface ownership.
See [rnsd.md](rnsd.md) and [runtime operations](runtime-operations.md) for configuration
and ownership boundaries.

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

The original read-only implementation follows these files at
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
The historical read-only gate omitted push; the current helper adds it and
uses 1 for protocol/transport failures, with per-ref rejections returned through
Git's status protocol. Upstream eagerly connects before negotiation; ours connects lazily
so offline capability discovery does not require a network.

The historical Git-first gate acceptance is ordinary Git discovery/clone/fetch on working and bare
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
   Requests default to batches of 25 refs, matching upstream; the helper's
   client_config can override this, while the direct CLI keeps 25. The full
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
5. Push listing is `/git/list` with `"for_push": true`. A bundle push is
   `/git/push` with `{0: "group/repository", "local_ref": "refs/rngit/push",
   "remote_ref": "refs/...", "force": false, "bundle": <binary Git bundle>}`.
   The local bundle ref is implementation-owned, not an on-wire branch naming
   convention. Existing-object push instead sends
   `{0: "group/repository", "operations": [{"action": "update_ref",
   "ref": "refs/...", "sha": "<object-id>", "force": false}]}`.
   Delete is `/git/delete` with `{0: "group/repository", "ref": "refs/..."}`.
   Each response must be ordinary binary status bytes with no file metadata;
   successful status is exactly `b"\x00"`. Requests that exceed the link MDU
   use ordinary request Resources, including the binary bundle inside the map.

Resources may be compressed, encrypted, and segmented by RNS. Requests larger
than the link MDU also use Resources. **Channel is not part of rngit's
list/fetch protocol**, so the client does not invent Channel messages.

## Verification and limits

The Parity 2 read/write milestone additionally exercises ordinary Git push
against a disposable, actual pinned Python server with an identity-scoped
writer: new/updated/forced/deleted branches and tags, annotated/Unicode refs,
existing-object-only operations, per-ref partial rejection, concurrent distinct
refs, read-only/hidden/unapproved identities, restart persistence and explicit
SHA-256 rejection. Managed xUnit tests cover live wire decisions and a separate
reference-generated status corpus (`GenerateRngitPushVectors.py`); Pester owns
actual Git processes. The generator executes real upstream handlers on owned
fixtures, not a replacement server. Those handler vectors alone are not
live-network evidence. Exact final commands/results and remaining failure/
platform gaps are recorded in the development history.

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
working-tree or bare repositories. Push is available through the helper only;
the direct CLI remains read-only. No repository creation/hosting, release
download/publishing, shallow clone,
submodule/LFS download or signature verification is implemented by the client.
Git itself owns working-tree checkout. Upstream's fetch handler has no depth option. Detached remote HEAD
is unsupported by upstream's list format and rejected when not represented as
a valid symbolic branch. The direct client recognizes SHA-1/SHA-256 formats.
The helper does not advertise Git's object-format negotiation and explicitly
rejects SHA-256; ordinary-Git acceptance uses SHA-1.
Arbitrary remote refs are checked by
equivalent ref-name validation before protocol output/import. The direct client
uses one local ref transaction; it no longer launches a redundant Git process
per ref after the shared validator has already checked the listing.

The loopback fixture alone does not establish public-network or mixed-topology
compatibility. Separate public-network retrieval evidence and limitations are
recorded in the [development history](../.github/development-history.md).
