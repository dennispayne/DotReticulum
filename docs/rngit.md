# Git over Reticulum

DotReticulum provides three related tools: `rngit` for direct repository access and hosting management, `git-remote-rns` for ordinary Git remote operations, and `rngcs` for Git SSH-signature integration. The implementation is experimental and AI-assisted; it is not an official or endorsed Reticulum utility. Its network operations use the .NET Reticulum stack. Git is used locally for repository operations; Python is used only by the optional reference-test harness.

The compatibility target is Reticulum (RNS) 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`, with LXMF 1.2.1 in the reference environment. The Reticulum-hosted repository is `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`; GitHub is a comparison mirror. Do not treat this locator as authenticated publisher provenance. Stored vectors retain their source revision, including `e40191b3d193b46b7f2d8a44424a594cd758839b` where applicable. See [versioning](../README.md#versioning) for tool version numbers.

## Tools and usage

An installed package provides individual commands such as `rngit`, `git-remote-rns` and `rngcs`. From source, build the shared executable with:

```powershell
dotnet build src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release
dotnet src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll rngit --help
```

When running from source, pass the tool name after the application DLL. The DLL is also the runtime behind the installed commands.

### Direct clone and fetch

`rngit clone` creates a bare repository; `rngit fetch` updates an existing bare repository:

```powershell
rngit clone rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 7200
rngit fetch rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 900 --single-branch
```

`DESTINATION` is the server's 32-hex-character `git.repositories` destination hash, not its identity hash. Group and repository names are case-sensitive and use ASCII letters, digits, `.`, `_` or `-`. URL escaping, queries, fragments and aliases are not accepted by these direct commands. Clone refuses any existing destination directory, even an empty one. Fetch requires an existing bare Git directory; it does not accept a working tree or its parent.

`--single-branch` fetches only the default branch and no tags or other branches. Otherwise, advertised refs are fetched in batches of 25. `--timeout` is an overall deadline covering startup, path discovery, link setup, requests, transfers and local Git commands (default 7200 seconds). Ctrl+C cancels. `--max-response` limits each response resource's advertised uncompressed size (default 268435456 bytes, including metadata); listings have a separate 4 MiB limit. Resources are buffered in memory, so this is not a streaming large-repository client. Progress goes to stderr.

Clone verifies imported objects with `git fsck --full --strict` before publishing the local repository. The CLI reports its path, HEAD, tree and bytes received in Git bundles over Reticulum; this excludes requests, listings and Reticulum framing. Zero bundle bytes means the server found local objects sufficient and is not proof of source-object retrieval. Inspect a repository with:

```powershell
git --git-dir=C:\Repos\repository.git fsck --full --strict
git --git-dir=C:\Repos\repository.git rev-parse HEAD 'HEAD^{tree}'
git --git-dir=C:\Repos\repository.git for-each-ref
```

Clone uses a temporary sibling directory and moves it into place after verification. Errors remove owned temporary files and never overwrite an existing target. Fetch replaces selected same-named refs, including non-fast-forward updates, and sets HEAD to the advertised default branch. It does not prune refs absent from the server. Avoid fetching into a bare repository with unrelated local work to preserve. Ref updates use a compare-and-swap transaction against initial local refs; a concurrent local ref writer causes failure. The origin URL is preserved and `remote.rngit.url` records the fetch source. Clone records `remote.origin.url`.

Fetch verifies objects before updating refs. A failed fetch can leave imported but unreferenced objects; the ref transaction is atomic, but later HEAD and configuration changes are not. A server ref change between listing and bundle creation is rejected if the bundle no longer matches the advertised refs. To demonstrate actual retrieval from a seeded repository, prove the desired commit is absent with `git --git-dir=... cat-file -e TARGET_COMMIT`, fetch, require nonzero bundle bytes, and then verify the commit, tree and strict fsck. Seed history may originate locally; it need not have been downloaded over Reticulum.

Exit codes are 0 for success or help, 1 for transport/server/timeout/cancellation/local failures, and 2 for invalid arguments.

### Git remote helper

The native `git-remote-rns` helper lets Git operate on a working tree or bare repository. Git owns checkout, local refs, HEAD and remote-tracking refs; the helper advertises remote refs, imports fetched objects and sends pushes. It does not update the caller's local refs. Install the published native executable under Git's helper name in a directory on `PATH`:

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

On Unix, publish for the host runtime identifier, copy the executable as `git-remote-rns` without an extension, preserve its executable permission and put its directory on `PATH`. Windows x64 and Linux x64 are exercised. macOS ARM64 builds, passes the managed suite and passes the NativeAOT resource test in hosted CI, but the Darwin terminal backend required by `rnsh` is not implemented. Android, desktop RNode BLE and other unlisted platform/provider combinations are not established as supported. See [TODO.md](../TODO.md). Native publishing alone does not establish feature support on a platform.

Git commands supported by the helper are `capabilities`, `list`, `list for-push`, `option progress true|false`, batched `fetch <object-id> <ref>` and `push [+]<source>:<destination-ref>`, with blank-line batch termination. It advertises `fetch`, `push` and `option`; `list` is implicit. Unknown options return `unsupported`; malformed input and unsupported operations fail nonzero with stderr diagnostics. It does not advertise connect, shallow/depth, filter or atomic-push capabilities. Unsupported dry-run requests fail before remote mutation.

Git can clone working or bare repositories, select a branch with `clone -b`, follow annotated tags, fetch incrementally and preserve local work using normal refspecs. A no-change fetch can require only ref listing. The helper lazily loads configuration and creates one .NET node and identified link for each invocation requiring remote refs, then tears them down at exit, error or cancellation. Stdout carries only Git protocol messages; progress and diagnostics go to stderr. Input and output use UTF-8 without an output BOM, independent of the Windows console code page. Cancellation and timeout are failures, not empty successful responses. Input lines and queued refs are bounded.

Git must provide `GIT_DIR` when the helper imports objects. The helper does not initialize repositories, set HEAD, write FETCH_HEAD or rewrite Git configuration. Fetch validates requested ref/object pairs against the advertisement, verifies and imports bundles, and checks the object database before acknowledging success. A failure can leave imported unreferenced objects; Git controls ref updates.

#### Push behavior

Push listing checks upstream write permission. New branches, lightweight and annotated tags, updates, forced updates and deletion use upstream `/git/push` and `/git/delete` requests. Locally resolvable sources, including raw object IDs, are named in a temporary bare repository using a read-only object alternate; the caller's refs and HEAD are not changed. Objects already present remotely are excluded from bundles. If required objects are already on the server, the helper sends an `update_ref` request without a bundle.

For an existing target, the upstream existing-object operation requires explicit force whenever the object ID changes, even for a fast-forward. Bundle pushes use Git's fetch ancestry and tag decisions. Delete follows upstream behavior and does not change symbolic HEAD. Each ref receives its own success or error result; rejection of one ref does not conceal other results.

Pushes are sequential, not atomic. Upstream has no compare-and-swap or remote lease protocol, so concurrent writes to the same ref are not transactionally protected. Independent pushes to distinct refs are supported. If cancellation, deadline or connection loss occurs after transmission, the in-flight outcome may be uncertain; fetch and inspect before retrying. Normal cleanup removes owned temporary repositories. Force-killing a process can leave an unreferenced `rngit-push-*` directory in Git's directory.

The helper supports SHA-1 only. It rejects nonempty SHA-256 remote listings and local SHA-256 transfers and does not advertise Git object-format negotiation. An empty remote does not reveal its object format in the upstream protocol; the helper assumes SHA-1 and an incompatible server must reject the bundle. The direct bare client supports SHA-1 and SHA-256. Imported objects are checked with `fsck --full --strict --no-reflogs` in a temporary bare verification view borrowing the caller's object store through an alternate; the view is removed after verification. Local Git commands enable `core.longpaths` for Windows verification paths.

### Configuration

The direct bare client uses `--config` for the .NET Reticulum configuration directory or file. The directory form contains `config`. A gateway example is:

```ini
[reticulum]
  enable_transport = no
[interfaces]
  [[Gateway]]
    type = TCPClientInterface
    target_host = YOUR_GATEWAY
    target_port = 4242
```

The gateway must have a route to the destination and the host must grant access to the client's identity. The direct client stores that identity at `<Reticulum config directory>\storage\rngit`. Shared-instance election can attach to a native or pinned Python owner instead of opening interfaces. Separate configuration directories need a common explicit RPC key; use `share_instance=no` for independent interface ownership. See [rnsd](rnsd.md) and [runtime operations](runtime-operations.md).

The helper has a separate client configuration directory, selected by `RNGIT_CONFIG`. It contains `client_config`, `client_identity` (raw 64-byte private identity) and `client_log`. Without the environment variable, the reference-compatible default is `~/.rngit`, or `~/.rngit/reticulum` if `~/.config/rngit/config` exists. This follows the reference's actual directory selection, not its comments. Missing client configuration and identity files are created. Identity publication is atomic; concurrent creators use the published identity. Invalid existing identities are not replaced.

```ini
[client]
ref_batch_size = 25
[aliases]
my_node = 0123456789abcdef0123456789abcdef
[logging]
loglevel = 4
```

Aliases are case-sensitive and identify destination nodes: `rns://my_node/group/repository`. Literal destination hashes take precedence. `ref_batch_size` defaults to 25 and is limited to 1024; nonpositive values fail instead of entering the reference's non-advancing zero-batch loop. Logging accepts upstream levels -1 (off) through 8. Filtered node/session diagnostics go to `client_log`, rotated at 5 MiB; protocol errors still reach stderr. The bounded parser accepts flat sections, scalar values, quoted scalar values and comments. Nested sections, lists, multiline/interpolated values, duplicate entries and oversized input are rejected. Unknown scalar settings are ignored as upstream does. It is not a full ConfigObj parser.

`DOTRETICULUM_RNS_CONFIG` selects the helper's Reticulum configuration; `RNS_CONFIG` is its fallback. Without either, configuration discovery checks `/etc/reticulum/config`, then `~/.config/reticulum/config`, then `~/.reticulum` (including the Windows system-directory mapping described in [rnsd](rnsd.md)). `DOTRETICULUM_RNGIT_TIMEOUT` is the positive overall deadline in seconds (default 7200, including Git protocol input). `DOTRETICULUM_RNGIT_MAX_RESPONSE` limits response bytes (default 268435456); `DOTRETICULUM_RNGIT_MAX_PUSH` limits each outgoing bundle before it is read into memory (default 268435456 bytes). Git may consume more temporary disk while creating a bundle than the transfer cap. Transfers use buffered Reticulum resources, not streaming large-repository I/O.

For identity migration, the helper uses upstream's `client_identity`, not the older .NET `<Reticulum config>\storage\rngit` file. To preserve an existing authorized identity, copy that file into the helper client directory as `client_identity` before running the helper, without overwriting a different identity. The direct `rngit clone` and `fetch` commands continue to use their existing Reticulum storage identity.

Unix private identity files are created owner-only. On Windows, files inherit directory ACLs; restrict access to the configuration directory. The hosting node stores `repositories_identity`. The same directory can also hold `client_identity` for management/client commands; these identities have separate roles. Back up configuration and keys together with repositories and adjacent `.allowed`, `.work` and `.releases` data. Never publish private identity files.

### Hosting node

The repository node serves the `git.repositories` destination and scans configured group directories for bare repositories. `--config` selects rngit's configuration and identity directory; `--rnsconfig` selects the .NET Reticulum configuration:

```powershell
rngit node --config C:\RNGit\Server --rnsconfig C:\RNS\Server
rngit node --config C:\RNGit\Server --print-identity
rngit create rns://NODE/team/project.git --config C:\RNGit\Client --rnsconfig C:\RNS\Client
```

`NODE` is the 32-character repositories destination hash printed by `--print-identity`, not the node identity. `--print-identity` prints the peer identity, repository-node identity and repository destination; it also prints the Nomad Network destination if page serving is enabled.

The host's flat/list `config` supports these settings:

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
[logging]
loglevel = 4
```

`announce_interval` is in minutes; zero disables periodic announcements. `mirror_interval` is in hours; zero disables periodic mirror synchronization. `record_stats` defaults to false. `node_name` defaults to `Anonymous Git Node`; `serve_nomadnet` defaults to false, `unicode_icons` to false, and `media_conversion` to true. Log level is clamped to -1 through 8. Repository paths must exist; `~` and `~/` are expanded. The server parser accepts flat sections and scalar/list values but not nested sections, interpolation or multiline values. Input is limited to 1 MiB, 16,384 lines and 8,192 characters per line.

`[aliases]` entries name caller identities by their 32-hex-character identity hash. These differ from helper aliases, which name destination nodes. `[repositories]` maps group names to directories of bare repositories. An optional `<group-path>.allowed` file is combined with `[access]` entries for the group. A repository's adjacent `<repository-path>.allowed` file overrides group permissions for each permission it lists. Identity hashes are normalized; aliases and group/repository names are case-sensitive.

### Permissions and authorization

Permission entries use `<permission>:<target>`, one per line; comments start with `#`.

| Permission | Abbreviations | Use |
| --- | --- | --- |
| `read` | `r` | List or read repository data |
| `write` | `w` | Write Git refs |
| `create` | `c` | Create repositories |
| `stats` | `s` | Read protected statistics |
| `release` | `rel` | Create, publish, update latest, or delete releases |
| `interact` | `i` | Comment on work documents |
| `propose` | `p` | Create proposed work documents |
| `admin` | `adm` | Manage permissions and administer a repository/group |
| `readwrite` | `rw` | Grant both `read` and `write` |

Targets are a 32-hex-character identity hash, an identity alias, `all`/`everyone`/`a`, or `none`/`nobody`/`n`. For example:

```text
r:all
rw:0123456789abcdef0123456789abcdef
rel:maintainer
```

`none` takes precedence over `all`, a matching identity and administrator grants for that permission. `all` or an explicit identity grants the named permission; `admin` also grants permissions unless the relevant permission has an explicit `none`. Group `.allowed` and `[access]` entries are combined. For a repository, an empty permission list falls back to the group; a nonempty repository list grants only matching identities and denies unmatched callers instead of falling back. Group permission input is limited to 1 MiB and 16,384 lines.

Work-document permission files are stored under `.work`. An explicit document `none` denies access; a matching identity, `all`, or document administrator entry grants the permission. An unmatched document-level list falls back to repository permissions, which can fall back to group permissions. Document permissions do not independently grant `stats` or `release`. Proposed documents grant their proposer document-level `interact` and `write` entries. Permission management requires read and admin access; setting repository permissions also requires group read and admin access. A blocked authenticated identity is disconnected at the link boundary and denied in repository permission resolution.

`rngit perms <group-or-repository-url>` reads and updates an `.allowed` file. Permission resolvers are plain text by default. An executable resolver runs only when the node operator explicitly starts the host with `--allow-exec`; execution is bounded to eight seconds and 1 MiB of output. Static `.allowed` files are not treated as executable merely because Windows reports `X_OK`. Executable resolvers cannot be modified remotely; update them on the host. A malformed resolver fails closed. NomadNet anonymous permissions are resolved explicitly; an invalid all-zero cryptographic key is not used as an anonymous sentinel.

Management accepts `--content <file>` for work, release notes and permission content. It does not launch an editor implicitly. `--allow-exec` explicitly authorizes bounded `EDITOR` or template execution. `--yes` confirms release deletion, work deletion or latest-release changes; prompts otherwise default to refusal. Node operators must explicitly pass `--allow-external` to permit HTTP, HTTPS or SSH repository sources. RNS sources use the native stack; other source schemes are not implicitly authorized. Python's interactive runtime shell is not provided.

### Forks, mirrors and repository management

`rngit create <url>` creates a repository in a configured group. `rngit fork <source-url> <target-url>` and `rngit mirror <source-url> <target-url>` initialize a repository and record its source. `rngit sync <url>` updates a fork or mirror. Periodic synchronization selects mirrors whose configured interval has elapsed. Mirror synchronization restores the source default HEAD; fork synchronization preserves the fork's HEAD. RNS sources use this .NET implementation. HTTP, HTTPS and SSH sources require the node operator's `--allow-external` authorization. Failed or cancelled initialization removes its owned temporary directory; it does not promise rollback of every imported object or interrupted multi-step release draft.

### Releases

Release operations are `list`, `view <tag>`, `create <tag>:<artifacts-directory>`, `latest <tag>`, `delete <tag>` and `fetch <tag>:<artifact-pattern>`. Use `latest:all` to fetch every artifact from the selected latest release. Verify a local manifest without networking:

```powershell
rngit release C:\Owned\manifest.rsm verify --offline --signer IDENTITY_HASH
```

`--signer` can require a signer identity hash or load a raw private identity file. Offline verification does not add a signer to a trust store. Release creation requires a repository tag. It creates a draft under the repository's adjacent `.releases` directory, uploads artifacts and finalizes the release; `--local` creates a local package. Manifests are named `manifest.rsm`; artifact signatures use `.rsg` sidecars. Fetch validates selected artifacts and saves `<package>_<version>.rsm` beside the files. The server supports at most 4,096 releases per repository, 1,024 artifacts per release, 256 MiB per artifact and 512 KiB of release notes. The client also limits aggregate release packaging to 256 MiB. Interrupted drafts may remain for operator cleanup. Names and duplicate artifact names are validated, stricter than upstream's basename handling.

Release signatures establish that the signing identity signed the manifest and artifact hashes. A valid key embedded in an artifact does not establish publisher trust. Supply an independently trusted `--signer` when verifying a release. Git's `allowedSignersFile` is not a trust database for `rngcs`; the reference signer ignores that option. Principal, author/tagger and cryptographic signature checks are separate.

### Work documents

Work operations are `list`, `view`, `create`, `propose`, `edit`, `delete`, `update` (an alias for `comment`), `complete`, `activate` and `perms`. Lists use `active`, `completed`, `proposed` or `all` scopes; the default is `active`. Views and mutations use a document ID. `create` and `propose` require `--title`; supply content with `--content` or an explicitly authorized editor. Content is trimmed before signing. `edit` loads existing content first when editor input is used. Work documents and updates use Markdown, have a 256 KiB content limit, and document signatures are checked against the author identity. A view reports whether a document signature is valid; metadata author text alone is not treated as verified identity. Authorization distinguishes read, write, interact, propose and admin operations and checks document access before returning protected content.

### Nomad Network pages and media

Set `serve_nomadnet = yes` to serve the node's Nomad Network destination. The page service includes node/group/repository listings, tree and blob views, commit history and details, refs, statistics, releases and work documents, plus artifact, file and work-document downloads and `/media`. Page requests follow repository and document read permissions; anonymous access is evaluated explicitly. The page destination advertises the configured `node_name` as app data.

Static `.mu` template overrides may be placed under the host's `templates` directory. `base.mu` recognizes `{NODE_NAME}`, `{VERSION}`, `{NAVIGATION}`, `{PAGE_CONTENT}` and `{GEN_TIME}`; generation time is measured per request. Native Markdown implements the reference's default rendering path, including a code-point-width fallback when optional `wcwidth` is unavailable. Python Pygments highlighting and `wcwidth`-enhanced layout are not implemented. Byte-differential fixtures cover exercised default rendering, not every Markdown input or terminal font. Unsafe filesystem links and download paths are rejected; media references are encoded rather than preserving unsafe upstream URL behavior.

Media backend discovery checks `magick`, `convert`, `gm`, `ffmpeg` and `avconv`; `RNGIT_MEDIA_BACKEND` selects a supported backend. Conversion bounds input, output, runtime and subprocess lifetime. Missing or failed conversion is diagnosed, and page delivery may retain the original image as upstream does. Real FFmpeg/WebP conversion has Windows execution coverage; other backend families have argument-selection and failure coverage, not installed-encoder evidence. Disable conversion with `media_conversion = no`.

### Git signing with rngcs

`rngcs` implements Git SSH-signing operations `sign`, `find-principals`, `check-novalidate` and `verify`, using Reticulum RSG signatures in an SSHSIG envelope. Configure Git to use SSH signatures and `rngcs`:

```powershell
git config --global gpg.format ssh
git config --global gpg.ssh.program rngcs
git config --global user.signingkey C:\Keys\reticulum-identity
git config --global commit.gpgsign true
```

The key file is a raw 64-byte Reticulum private identity. `rngcs` accepts `-Y {sign,find-principals,check-novalidate,verify}`, `-n namespace`, `-f keyfile`, `-I principal`, `-s signature-file`, `-O option` and an optional file. The namespace is always `git`; `-n`, `-O` and verification's `-f` allowed-signers file are ignored to match the pinned signer behavior. `verify` checks RSG cryptography and that the signer identity matches the commit author or tagger, and can check the `-I` principal. `find-principals` extracts signer identity metadata; `check-novalidate` performs structural checks only. Neither operation establishes publisher trust. A valid signature proves control of its signing key, not whether that signer is trusted by the verifier. Messages are limited to 16 MiB, decoded signatures to 1 MiB and armored signatures to 2 MiB.

### Protocol behavior

The helper uses destination name `git.repositories`. After establishing a link, the client identifies itself. Requests use MessagePack maps; integer keys `0` and `1` are reserved, while other keys and ref/SHA values are strings.

1. `/git/list` sends `{0: "group/repository", "for_push": false}`. The response is binary: a status byte followed by UTF-8 `<object-id> <ref-name>\n` lines and `@refs/heads/<branch> HEAD\n`. A large list is an ordinary response Resource carrying MessagePack `[request-id, response-bytes]`, not a file response.
2. `/git/fetch` sends `{0: "group/repository", "refs": [{"sha": "<object-id>", "ref": "refs/..."}]}`. Clone requests full history. The Python helper places an existing ref's `have` ID inside that ref entry. The .NET importer sends a top-level `"have": ["<local-ref-id>", ...]` list, including remote-tracking tips. Upstream verifies each `have` object exists on the server before passing its exclusion to `git bundle create`.
3. Successful bundles are raw file-response Resource bytes with metadata `{1: 0}`, not a status byte prepended to a pack or a MessagePack-wrapped bundle. Bundles are verified; their refs are compared with the list response before local import. Thin bundles may omit already-known ref tips from their headers only when those objects were reachable locally before the operation. A non-file success response `b"\x00"` means there is no bundle; local ref creation and fsck must still succeed.
4. Error responses contain a status byte and diagnostic text: `1` disallowed, `2` invalid request, `3` not found/not allowed, and `255` remote failure. Unknown or malformed metadata is an error.
5. Push listing uses `/git/list` with `"for_push": true`. Bundle pushes use `/git/push` with `{0: "group/repository", "local_ref": "refs/rngit/push", "remote_ref": "refs/...", "force": false, "bundle": <binary Git bundle>}`. Existing-object updates send `/git/push` with an `operations` entry containing `action: "update_ref"`, `ref`, `sha` and `force`. Deletion uses `/git/delete` with repository and ref. Successful push/delete responses are exactly `b"\x00"` and have no file metadata. Requests larger than the link MDU use request Resources, including binary bundles inside maps.

Reticulum may compress, encrypt and segment Resources. Channel is not part of this rngit protocol; the tools do not substitute Channel messages for list/fetch requests.

## Differences and limitations

- **Direct client:** clone/fetch handles bare Git repositories and is read-only. Repository hosting, release download/publishing, shallow clone, submodule/LFS download and signature verification are not implemented by the direct transfer client. Git owns working-tree checkout. Upstream fetch has no depth option. A detached remote HEAD that is not represented as a valid symbolic branch is unsupported.
- **Git helper:** ordinary Git fetch and push are implemented for SHA-1 repositories. Atomic push, object-format negotiation, shallow/depth fetch, filtering, submodules, LFS and remote lease guarantees are not implemented. Per-ref pushes are sequential. Existing-object updates require force when changing an existing target, including fast-forwards.
- **Configuration:** client and host parsers implement bounded flat/list subsets, not full ConfigObj. Nested sections, interpolation and multiline values are unsupported. Host config input has 1 MiB, 16,384-line and 8,192-character line limits; permission input has 1 MiB and 16,384-line limits.
- **Runtime:** Python's interactive shell and implicit editor execution are not provided. Executable permission resolvers, templates, editors and external repository sources require explicit operator authorization. Errors use stderr rather than upstream's console progress output. Management content input is non-interactive by default.
- **Pages and media:** optional Pygments and enhanced `wcwidth` behavior are absent. Conversion is limited to installed supported backends; testing backend selection does not prove its encoder is installed. Filesystem link/download behavior is hardened rather than matching unsafe upstream URL/path behavior.
- **Release verification:** RSG cryptographic validity and a matching embedded signer are not publisher trust. The verifier must independently specify the signer identity. Git's allowed-signers file is not consulted by this signer.
- **Reference adapter:** on Windows the Python management-client test adapter closes the received resource file immediately before the original retention callback moves it. The unmodified upstream callback otherwise attempts to unlink an open file and raises `WinError 32`. The original callback, request handlers and wire/resource implementation remain in use; the adapter reads and closes the returned file and emits observations. This verifies interoperability with the disclosed Windows file-retention shim, not the unmodified upstream Windows retention path.
- **Platform support:** Windows x64 and Linux x64 are exercised. macOS ARM64 builds, passes the managed suite and passes the NativeAOT resource test in hosted CI, but `rnsh` lacks its Darwin terminal backend. Android, desktop RNode BLE and other platform/provider combinations are not implemented or verified. See [TODO.md](../TODO.md).

## Verification

`RngitTests` in `tests/DotReticulum.Applications.Tests/RngitTests.cs` exercises clone/fetch against the pinned Python `ReticulumGitNode`, including exact refs, HEAD/tree, strict fsck, denied reads, response limits, cancellation, incremental missing-object retrieval and no-change fetches; live cases are opt-in. `tests/Pester/GitRemote.Tests.ps1` exercises the helper through real Git processes against the pinned Python peer, including working and bare clones, branches/tags, missing-object and unchanged fetches, push behavior and failure paths when enabled. The harness verifies the imported RNS revision and tracked source files. Hosted platform evidence is distinct from live-node or mixed-topology evidence; loopback tests do not establish public-network compatibility. The Windows Python reference run uses the disclosed file-retention shim above.

The helper's config file is limited to 1 MiB, 16,384 lines and 8,192 characters per line. The host can keep at most 256 links, uses four request workers and a bounded request queue of 256 entries by default. Repository requests and bundle transfers are limited to 256 MiB; page requests are limited to 1 MiB. A full request queue is reported as server busy. These runtime bounds are not adjustable through the flat configuration file.

The host also accepts `blocked_identities`, `stats_ignore_identities` and `stats_push_ignore_identities` in `[rngit]`; values may be identity hashes or identity aliases. Blocked peers are disconnected after identity authentication. `stats_ignore_identities` excludes identities from recorded non-push statistics. `stats_push_ignore_identities` is parsed but unused by the pinned server; it does not filter pushes.
