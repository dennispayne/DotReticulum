# Git over Reticulum

DotReticulum includes a set of tools for hosting Git repositories and collaborating on them over Reticulum:

- `git-remote-rns` lets ordinary Git clone, fetch and push using `rns://` URLs.
- `rngit` hosts repositories as a repository node, manages them remotely, and can also transfer bare repositories directly.
- `rngcs` lets Git sign and verify commits and tags with Reticulum identities.

Git over Reticulum uses URLs in the format `rns://DESTINATION/GROUP/REPOSITORY`. Once the helper is on your `PATH`, you can use `git` with a Reticulum remote just like any other remote.

These tools are experimental and AI-assisted. They are not official or endorsed Reticulum utilities. All network operations use the .NET Reticulum stack. Git is used locally for repository operations, and Python is used only by the optional reference-test harness.

The compatibility target is Reticulum (RNS) 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`, with LXMF 1.2.1 in the reference environment. Stored vectors retain their source revision, including `e40191b3d193b46b7f2d8a44424a594cd758839b` where applicable. See [versioning](../README.md#versioning) for tool version numbers.

The Reticulum-hosted upstream repository is `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`, and GitHub is a comparison mirror. Do not treat this locator as authenticated publisher provenance. Git object consistency is not the same as publisher or release-signature authentication.

## Getting the tools

An installed package provides the individual commands `rngit`, `git-remote-rns` and `rngcs`. From source, you can build the shared application and pass the tool name after the DLL:

```powershell
dotnet build src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release
dotnet src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll rngit --help
```

The same application is the runtime behind the installed commands. A NativeAOT build selects the tool from its executable file name, so Git can find the helper when you install the native binary as `git-remote-rns`. Install copies named `rngit` and `rngcs` as well if you want those commands:

```powershell
dotnet publish src\DotReticulum.Applications\DotReticulum.Applications.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -o artifacts\git-native
New-Item -ItemType Directory -Force C:\Tools\DotReticulum
Copy-Item artifacts\git-native\DotReticulum.Applications.exe C:\Tools\DotReticulum\git-remote-rns.exe
$env:PATH = 'C:\Tools\DotReticulum;' + $env:PATH
```

On Unix, publish for the host runtime identifier, copy the executable as `git-remote-rns` without an extension, preserve its executable permission and put its directory on `PATH`.

Windows x64 and Linux x64 are exercised. macOS ARM64 builds, passes the managed suite and passes the NativeAOT resource test in hosted CI. Other platforms remain unverified, and native publishing alone does not establish feature support on a platform. See [TODO.md](../TODO.md).

## Using Git with Reticulum remotes

With `git-remote-rns` on your `PATH`, Git invokes it automatically for `rns://` URLs:

```powershell
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

`DESTINATION` is the server's 32-hex-character `git.repositories` destination hash, not its identity hash. You can also use a destination alias from the [client configuration](#client-configuration).

You can clone working or bare repositories, select a branch with `clone -b`, follow annotated tags, fetch incrementally and preserve local work using normal refspecs. A fetch with no changes can require only a ref listing.

Git owns checkout, local refs, HEAD and remote-tracking refs. The helper advertises remote refs, imports fetched objects and sends pushes. It does not update your local refs, initialize repositories, set HEAD, write `FETCH_HEAD` or rewrite Git configuration.

### Pushing

You can push new branches, lightweight and annotated tags, updates and forced updates, and delete remote refs. Listing for a push checks that you have write permission upstream.

- Locally resolvable sources, including raw object IDs, are named in a temporary bare repository that borrows your objects through a read-only alternate. Your refs and HEAD are not changed.
- Objects the server already has are left out of bundles. If the server already has every required object, the helper updates the ref without sending a bundle.
- When the target ref already exists, upstream's existing-object update requires explicit force whenever the object ID changes, even for a fast-forward. Bundle pushes use Git's own fetch ancestry and tag decisions.
- Deleting a ref follows upstream behavior and does not change the remote's symbolic HEAD.
- Each ref gets its own success or error result, so a rejected ref does not hide the results for the others.

Pushes are sequential, not atomic. Upstream has no compare-and-swap or remote lease protocol, so concurrent writes to the same ref are not transactionally protected. Independent pushes to different refs are supported.

If cancellation, a deadline or a lost connection interrupts a push after it was transmitted, its outcome may be uncertain. Fetch and inspect the remote before retrying. Normal cleanup removes the helper's temporary repositories, but force-killing the process can leave an unreferenced `rngit-push-*` directory inside the Git directory.

### Object formats

The helper supports SHA-1 repositories only. It rejects nonempty SHA-256 remote listings and local SHA-256 transfers, and does not advertise object-format negotiation. An empty remote does not reveal its object format in the upstream protocol, so the helper assumes SHA-1 and an incompatible server must reject the bundle. The direct `rngit clone` and `rngit fetch` commands support both SHA-1 and SHA-256.

### Helper behavior

The helper creates one .NET node and identified link for each invocation that needs remote refs, and tears them down when it exits, fails or is cancelled. Configuration is loaded only when needed.

Stdout carries only Git protocol messages. Progress and diagnostics go to stderr. Input and output use UTF-8 without a BOM, regardless of the Windows console code page. Cancellation and timeouts are reported as failures, never as empty successful responses. Input lines and queued refs are bounded.

On fetch, Git must provide `GIT_DIR`. The helper checks requested ref/object pairs against the advertisement, verifies and imports bundles, and checks the object database before reporting success. Imported objects are checked with `git fsck --full --strict --no-reflogs` in a temporary bare verification view that borrows your object store through an alternate. The view is removed afterwards. A failure can leave imported but unreferenced objects; Git controls the ref updates. Local Git commands enable `core.longpaths` for Windows verification paths.

The helper implements these Git remote-helper commands, with batches terminated by a blank line:

| Command | Notes |
| --- | --- |
| `capabilities` | Advertises `fetch`, `push` and `option`; `list` is implicit |
| `list`, `list for-push` | Lists remote refs; `for-push` checks write permission |
| `option progress true\|false` | Unknown options return `unsupported` |
| `fetch <object-id> <ref>` | Batched |
| `push [+]<source>:<destination-ref>` | Batched |

It does not advertise connect, shallow/depth, filter or atomic-push capabilities. Dry-run push requests fail before anything changes remotely. Malformed input and unsupported operations fail with a nonzero exit and a diagnostic on stderr.

### Client configuration

The helper reads two configurations, selected by environment variables:

| Variable | Purpose | Default |
| --- | --- | --- |
| `RNGIT_CONFIG` | rngit client directory | `~/.rngit`, or `~/.rngit/reticulum` if `~/.config/rngit/config` exists |
| `DOTRETICULUM_RNS_CONFIG` | .NET Reticulum configuration | `RNS_CONFIG`, then normal discovery |
| `RNS_CONFIG` | Fallback Reticulum configuration | Normal discovery |
| `DOTRETICULUM_RNGIT_TIMEOUT` | Positive overall deadline in seconds, including Git protocol input | 7200 |
| `DOTRETICULUM_RNGIT_MAX_RESPONSE` | Maximum response size in bytes | 268435456 |
| `DOTRETICULUM_RNGIT_MAX_PUSH` | Maximum size of each outgoing bundle, checked before it is read into memory | 268435456 |

The `RNGIT_CONFIG` default follows the reference's actual directory selection, not its comments. Without a Reticulum configuration variable, discovery checks `/etc/reticulum/config`, then `~/.config/reticulum/config`, then `~/.reticulum`, including the Windows system-directory mapping described in [rnsd](rnsd.md).

Transfers use buffered Reticulum resources, not streaming large-repository I/O. Git may also use more temporary disk while creating a bundle than the transfer limit allows.

The client directory contains:

- `client_config`, the client settings.
- `client_identity`, the raw 64-byte private identity the helper uses.
- `client_log`, filtered node and session diagnostics, rotated at 5 MiB. Protocol errors still reach stderr.

Missing configuration and identity files are created. The identity is published atomically, and concurrent creators use whichever identity was published. An invalid existing identity is not replaced.

```ini
[client]
ref_batch_size = 25
[aliases]
my_node = 0123456789abcdef0123456789abcdef
[logging]
loglevel = 4
```

Aliases name destination nodes, so you can write `rns://my_node/group/repository`. Aliases are case-sensitive, and a literal destination hash takes precedence over an alias. `ref_batch_size` defaults to 25 and is limited to 1024. Logging accepts the upstream levels -1 (off) through 8.

The configuration parser is bounded. It accepts flat sections, scalar and quoted scalar values, and comments, and ignores unknown scalar settings as upstream does. Nested sections, lists, multiline or interpolated values, duplicate entries and oversized input are rejected. The file is limited to 1 MiB, 16,384 lines and 8,192 characters per line. It is not a full ConfigObj parser.

If you used the older .NET direct client, note that the helper uses upstream's `client_identity`, not the .NET `<Reticulum config>\storage\rngit` file. To keep an already-authorized identity, copy that file into the helper's client directory as `client_identity` before running the helper, without overwriting a different identity. The direct `rngit clone` and `rngit fetch` commands keep using their Reticulum storage identity.

## Direct bare clone and fetch

`rngit clone` and `rngit fetch` are a DotReticulum addition for transferring bare repositories without the helper. `clone` creates a new bare repository, and `fetch` updates an existing one:

```powershell
rngit clone rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 7200
rngit fetch rns://DESTINATION/GROUP/REPOSITORY C:\Repos\repository.git --config C:\RNS\Client --timeout 900 --single-branch
```

| Option | Meaning |
| --- | --- |
| `--config <path>` | .NET Reticulum configuration directory or file |
| `--timeout <seconds>` | Overall deadline covering startup, path discovery, link setup, requests, transfers and local Git commands (default 7200) |
| `--max-response <bytes>` | Maximum advertised uncompressed size of each response resource, including metadata (default 268435456) |
| `--single-branch` | Fetch only the default branch, with no tags or other branches |
| `-h`, `--help` | Show help |

Without `--single-branch`, advertised refs are fetched in batches of 25. Ref listings have a separate 4 MiB limit. Resources are buffered in memory, so this is not a streaming large-repository client. Progress goes to stderr, and Ctrl+C cancels.

Group and repository names are case-sensitive and use ASCII letters, digits, `.`, `_` or `-`. These commands do not accept URL escaping, queries, fragments or aliases.

Exit codes are 0 for success or help, 1 for transport, server, timeout, cancellation or local failures, and 2 for invalid arguments.

### How clone and fetch update repositories

Clone refuses any existing destination directory, even an empty one. It builds the repository in a temporary sibling directory, verifies the imported objects with `git fsck --full --strict`, and only then moves it into place. Errors remove owned temporary files and never overwrite an existing target. Clone records `remote.origin.url`.

Fetch requires an existing bare Git directory. It does not accept a working tree or its parent. Fetch verifies objects before updating refs, then:

- Replaces selected refs of the same name, including non-fast-forward updates.
- Sets HEAD to the advertised default branch.
- Does not prune refs that are absent from the server.
- Preserves the origin URL and records the fetch source in `remote.rngit.url`.

Avoid fetching into a bare repository that holds unrelated local work you want to keep. Ref updates use a compare-and-swap transaction against the initial local refs, so a concurrent local ref writer causes the fetch to fail. The ref transaction is atomic, but the later HEAD and configuration changes are not, and a failed fetch can leave imported but unreferenced objects. If a server ref changes between listing and bundle creation, the fetch is rejected when the bundle no longer matches the advertised refs.

### Checking what was received

The CLI reports the repository path, HEAD, tree and the number of Git bundle bytes received over Reticulum. That count excludes requests, listings and Reticulum framing. Zero bundle bytes means the server found your local objects sufficient; it is not proof that any source objects were retrieved. You can inspect a repository with:

```powershell
git --git-dir=C:\Repos\repository.git fsck --full --strict
git --git-dir=C:\Repos\repository.git rev-parse HEAD 'HEAD^{tree}'
git --git-dir=C:\Repos\repository.git for-each-ref
```

A fetch into a seeded repository is an incremental fetch, not a full network clone. To show that objects were actually retrieved, first prove the desired commit is absent with `git --git-dir=... cat-file -e TARGET_COMMIT`. Then fetch, require nonzero bundle bytes, and verify the commit, its tree and a strict fsck. Seed history may come from anywhere locally; it need not have been downloaded over Reticulum.

### Reticulum configuration for the direct client

The direct client takes its .NET Reticulum configuration with `--config`. The directory form contains a `config` file. Without this option it uses the usual Reticulum configuration discovery order, falling back to `~/.reticulum`; see [rncp and rnx](rncp-rnx.md#configuration-shared-by-both-tools). To reach a remote node through a gateway:

```ini
[reticulum]
  enable_transport = no
[interfaces]
  [[Gateway]]
    type = TCPClientInterface
    target_host = YOUR_GATEWAY
    target_port = 4242
```

The gateway must have a route to the destination, and the host must grant access to the client's identity. The direct client stores that identity at `<Reticulum config directory>\storage\rngit`.

Shared-instance election can attach to a native or pinned Python owner instead of opening interfaces. Separate configuration directories need a common explicit RPC key. Use `share_instance = no` when each instance should own its own interfaces. See [rnsd](rnsd.md) and [runtime operations](runtime-operations.md).

## Hosting a repository node

The repository node serves the `git.repositories` destination and scans configured group directories for bare repositories. Running `rngit` with no command, or `rngit node`, starts the node. On first run it creates a default configuration file, which you then edit to point at your repositories and set permissions:

```powershell
rngit node --config C:\RNGit\Server --rnsconfig C:\RNS\Server
rngit node --config C:\RNGit\Server --print-identity
```

`--config` selects the rngit configuration and identity directory, and `--rnsconfig` selects the .NET Reticulum configuration. `-p`, `--print-identity` prints the peer identity, repository-node identity and repositories destination, plus the Nomad Network destination when page serving is enabled. `-s`, `--service` logs to a `server_log` file in the configuration directory. `-v` and `-q` adjust verbosity.

Other clients reach the node through its repositories destination hash, which `--print-identity` prints. This is not the node identity hash. Upstream's `-i`, `--interactive` runtime shell is not provided, and the native node rejects that option.

### Node configuration

The node's `config` file looks like this:

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

| Section | Setting | Meaning | Default |
| --- | --- | --- | --- |
| `[rngit]` | `node_name` | Name shown on pages and in the page destination's app data | `Anonymous Git Node` |
| `[rngit]` | `announce_interval` | Minutes between announces; zero disables periodic announces | 360 in a new config; 0 if omitted |
| `[rngit]` | `mirror_interval` | Hours between mirror synchronizations; zero disables them | 24 |
| `[rngit]` | `record_stats` | Record repository statistics | false |
| `[rngit]` | `blocked_identities` | Identities disconnected after authentication | |
| `[rngit]` | `stats_ignore_identities` | Identities excluded from recorded non-push statistics | |
| `[rngit]` | `stats_push_ignore_identities` | Parsed but unused by the pinned server; does not filter pushes | |
| `[aliases]` | `<name>` | Names a caller identity by its 32-hex-character identity hash | |
| `[repositories]` | `<group>` | Directory of bare repositories for a group | |
| `[access]` | `<group>` | Group permission entries | |
| `[pages]` | `serve_nomadnet` | Serve Nomad Network pages | false |
| `[pages]` | `unicode_icons` | Use Unicode icons on pages | false |
| `[pages]` | `media_conversion` | Convert page media | true |
| `[logging]` | `loglevel` | Log level, clamped to -1 through 8 | 4 |

The identity lists accept identity hashes or identity aliases. Node aliases name caller identities, unlike helper aliases, which name destination nodes. Identity hashes are normalized; aliases and group and repository names are case-sensitive.

Repository paths must exist, and `~` and `~/` are expanded. The parser accepts flat sections with scalar and list values. It does not accept nested sections, interpolation or multiline values. Input is limited to 1 MiB, 16,384 lines and 8,192 characters per line.

The node stores its identity as `repositories_identity`. The same directory can also hold a `client_identity` for management and client commands; the two identities have separate roles.

### Identity files and backups

Private identity files are created owner-only on Unix. On Windows, files inherit their directory's ACLs, so restrict access to the configuration directory. Back up configuration and keys together with repositories and their adjacent `.allowed`, `.work` and `.releases` data. Never publish private identity files.

### Creating and managing repositories

The management commands talk to a node over Reticulum. `--rnsconfig` selects the .NET Reticulum configuration and `--config` the rngit configuration and identity directory. `-i`, `--identity <file>` selects a raw private identity file:

```powershell
rngit create rns://NODE/team/project.git --config C:\RNGit\Client --rnsconfig C:\RNS\Client
```

| Command | Purpose |
| --- | --- |
| `rngit create <url>` | Create a repository in a configured group |
| `rngit fork <source-url> <target-url>` | Create a repository from a source and record it as a fork |
| `rngit mirror <source-url> <target-url>` | Create a repository from a source and record it as a mirror |
| `rngit sync <url>` | Update a fork or mirror from its source |
| `rngit perms <group-or-repository-url>` | Read or update permissions |
| `rngit work <repository-url> <operation>` | Manage [work documents](#work-documents) |
| `rngit release <repository-url> <operation> [target]` | Manage [releases](#releases) |

Management commands also accept `--timeout <seconds>` as an overall deadline (default 7200).

The node periodically synchronizes mirrors whose configured interval has elapsed. Mirror synchronization restores the source's default HEAD, while fork synchronization keeps the fork's HEAD.

RNS sources are fetched with this .NET implementation. HTTP, HTTPS and SSH sources are only permitted when the node operator starts the node with `--allow-external`; no other source schemes are implicitly authorized. If initialization fails or is cancelled, its owned temporary directory is removed. This does not promise rollback of every imported object or of an interrupted multi-step release draft.

## Permissions

Permissions are written as `<permission>:<target>`, one per line. Comments start with `#`.

| Permission | Short form | Grants |
| --- | --- | --- |
| `read` | `r` | List or read repository data |
| `write` | `w` | Write Git refs |
| `create` | `c` | Create repositories |
| `stats` | `s` | Read protected statistics |
| `release` | `rel` | Create, publish, update latest, or delete releases |
| `interact` | `i` | Comment on work documents |
| `propose` | `p` | Create proposed work documents |
| `admin` | `adm` | Manage permissions and administer a repository or group |
| `readwrite` | `rw` | Both `read` and `write` |

A target is a 32-hex-character identity hash, an identity alias, `all` (also `everyone` or `a`), or `none` (also `nobody` or `n`). For example:

```text
r:all
rw:0123456789abcdef0123456789abcdef
rel:maintainer
```

### How permissions are resolved

- `none` takes precedence over `all`, a matching identity, and administrator grants for that permission.
- `all` or a matching identity grants the named permission. `admin` also grants permissions unless that permission has an explicit `none`.
- Group permissions come from the `[access]` entries and an optional `<group-path>.allowed` file, combined.
- A repository's adjacent `<repository-path>.allowed` file overrides the group for each permission it lists. If the repository's list for a permission is empty, the group applies. If it is nonempty, only matching identities are granted and unmatched callers are denied rather than falling back.
- A blocked identity is disconnected at the link boundary and denied in repository permission resolution.

Group permission input is limited to 1 MiB and 16,384 lines.

### Work document permissions

Work-document permission files are stored under `.work`. An explicit `none` on a document denies access. A matching identity, `all` or a document administrator entry grants the permission. If nothing on the document matches, its permissions fall back to the repository, which can in turn fall back to the group. Document permissions do not grant `stats` or `release` on their own. A proposed document grants its proposer document-level `interact` and `write` entries.

### Managing permissions remotely

`rngit perms <group-or-repository-url>` reads and updates an `.allowed` file. Managing permissions requires read and admin access, and setting repository permissions also requires group read and admin access.

Permission resolvers are plain text by default. An executable resolver runs only when the node operator explicitly starts the node with `--allow-exec`. Execution is limited to eight seconds and 1 MiB of output, and a malformed resolver fails closed. Static `.allowed` files are not treated as executable just because Windows reports `X_OK`. Executable resolvers cannot be modified remotely; update them on the host.

### Content input and confirmation

Management commands never launch an editor implicitly:

- `--content <file>` supplies work, release notes or permission content.
- `--allow-exec` explicitly authorizes bounded `EDITOR` or template execution.
- `--yes` confirms release deletion, work deletion or latest-release changes. Without it, prompts default to refusal.

## Serving pages over Nomad Network

Set `serve_nomadnet = yes` to serve the node's Nomad Network destination. The destination advertises the configured `node_name` as app data.

Pages include node, group and repository listings, tree and blob views, commit history and details, refs, statistics, releases and work documents. The node also serves artifact, file and work-document downloads and `/media`. Page requests follow repository and document read permissions. Anonymous access is resolved explicitly; an invalid all-zero cryptographic key is not used as an anonymous sentinel.

### Templates and formatting

You can place static `.mu` template overrides in the node's `templates` directory. `base.mu` recognizes `{NODE_NAME}`, `{VERSION}`, `{NAVIGATION}`, `{PAGE_CONTENT}` and `{GEN_TIME}`. Generation time is measured per request.

Native Markdown rendering implements the reference's default path, including its code-point-width fallback for when the optional `wcwidth` package is unavailable. Python Pygments highlighting and `wcwidth`-enhanced layout are not implemented. Byte-differential fixtures cover the exercised default rendering, not every Markdown input or terminal font.

Unsafe filesystem links and download paths are rejected. Media references are encoded instead of preserving upstream's unsafe URL behavior.

### Media conversion

Media backend discovery checks `magick`, `convert`, `gm`, `ffmpeg` and `avconv`. You can select a supported backend with `RNGIT_MEDIA_BACKEND`, or disable conversion with `media_conversion = no`. Conversion bounds input, output, runtime and subprocess lifetime. A missing or failed conversion is diagnosed, and page delivery may keep the original image, as upstream does.

Real FFmpeg/WebP conversion has Windows execution coverage. Other backend families have argument-selection and failure coverage only, not evidence with an installed encoder.

## Releases

`rngit release <repository-url> <operation>` supports these operations:

| Operation | Purpose |
| --- | --- |
| `list` | List releases |
| `view <tag>` | Show a release |
| `create <tag>:<artifacts-directory>` | Create and publish a release |
| `latest <tag>` | Mark a release as latest |
| `delete <tag>` | Delete a release |
| `fetch <tag>:<artifact-pattern>` | Download and validate artifacts; `latest:all` fetches every artifact from the latest release |
| `verify` | Verify a manifest |

Release options are `-s`, `--signer`, `-n`, `--name`, `-L`, `--local` and `-o`, `--offline`. To verify a local manifest without networking:

```powershell
rngit release C:\Owned\manifest.rsm verify --offline --signer IDENTITY_HASH
```

`--signer` can require a signer identity hash or load a raw private identity file. Offline verification does not add the signer to any trust store.

### Creating releases

Creating a release requires a repository tag. The client creates a draft in the repository's adjacent `.releases` directory, uploads the artifacts and finalizes the release. `--local` creates a local package instead. Manifests are named `manifest.rsm`, and artifact signatures use `.rsg` sidecar files. Fetch validates the selected artifacts and saves `<package>_<version>.rsm` beside them.

Release names and duplicate artifact names are validated more strictly than upstream's basename handling. Interrupted drafts may remain for the operator to clean up.

| Limit | Value |
| --- | --- |
| Releases per repository | 4,096 |
| Artifacts per release | 1,024 |
| Size per artifact | 256 MiB |
| Release notes | 512 KiB |
| Aggregate release packaging (client) | 256 MiB |

### Release trust

A release signature establishes that the signing identity signed the manifest and artifact hashes. A valid key embedded in an artifact does not establish publisher trust. When verifying a release, supply an independently trusted `--signer`.

## Work documents

Work documents are signed Markdown documents attached to a repository. `rngit work <repository-url> <operation>` supports `list`, `view`, `create`, `propose`, `edit`, `delete`, `update` (an alias for `comment`), `complete`, `activate` and `perms`.

- `list` takes `--scope active|completed|proposed|all`; the default is `active`.
- Views and changes take a document ID with `-d`, `--id`.
- `create` and `propose` require `-t`, `--title`. Supply content with `--content` or an explicitly authorized editor.
- `edit` loads the existing content first when you use an editor.

Content is trimmed before signing and limited to 256 KiB. Document signatures are checked against the author identity, and a view reports whether the signature is valid. Author text in metadata alone is not treated as a verified identity.

Authorization distinguishes read, write, interact, propose and admin operations, and checks document access before returning protected content.

## Commit signing with rngcs

`rngcs` implements Git's SSH-signing operations using Reticulum RSG signatures inside an SSHSIG envelope. To have Git sign with a Reticulum identity:

```powershell
git config --global gpg.format ssh
git config --global gpg.ssh.program rngcs
git config --global user.signingkey C:\Keys\reticulum-identity
git config --global commit.gpgsign true
```

The key file is a raw 64-byte Reticulum private identity. `rngcs` accepts `-Y {sign,find-principals,check-novalidate,verify}`, `-n namespace`, `-f keyfile`, `-I principal`, `-s signature-file`, `-O option` and an optional file.

- `sign` signs the message.
- `verify` checks the RSG cryptography and that the signer identity matches the commit author or tagger. It can also check the `-I` principal.
- `find-principals` extracts the signer identity metadata.
- `check-novalidate` performs structural checks only.

To match the pinned signer, the namespace is always `git`, and `-n`, `-O` and verification's `-f` allowed-signers file are ignored. Git's `allowedSignersFile` is therefore not a trust database for `rngcs`.

Neither `find-principals` nor `check-novalidate` establishes publisher trust. A valid signature proves control of its signing key, not that the verifier trusts the signer. Principal, author or tagger, and cryptographic signature checks are separate.

Messages are limited to 16 MiB, decoded signatures to 1 MiB and armored signatures to 2 MiB.

## Protocol details

The node uses the destination name `git.repositories`. After establishing a link, the client identifies itself. Requests use MessagePack maps. Integer keys `0` and `1` are reserved; other keys and ref and SHA values are strings.

1. **List.** `/git/list` sends `{0: "group/repository", "for_push": false}`. The response is binary: a status byte followed by UTF-8 `<object-id> <ref-name>\n` lines and `@refs/heads/<branch> HEAD\n`. A large list is an ordinary response Resource carrying MessagePack `[request-id, response-bytes]`, not a file response.
2. **Fetch.** `/git/fetch` sends `{0: "group/repository", "refs": [{"sha": "<object-id>", "ref": "refs/..."}]}`. Clone requests full history. The Python helper places an existing ref's `have` ID inside that ref entry, while the .NET importer sends a top-level `"have": ["<local-ref-id>", ...]` list that includes remote-tracking tips. Upstream checks that each `have` object exists on the server before passing it as an exclusion to `git bundle create`.
3. **Bundles.** A successful bundle is raw file-response Resource bytes with metadata `{1: 0}`. It is not a status byte prepended to a pack, nor a MessagePack-wrapped bundle. Bundles are verified, and their refs are compared with the list response before local import. Thin bundles may omit already-known ref tips from their headers only when those objects were reachable locally before the operation. A non-file success response `b"\x00"` means there is no bundle; local ref creation and fsck must still succeed.
4. **Errors.** Error responses carry a status byte and diagnostic text: `1` disallowed, `2` invalid request, `3` not found or not allowed, and `255` remote failure. Unknown or malformed metadata is an error.
5. **Push.** Push listing uses `/git/list` with `"for_push": true`. Bundle pushes use `/git/push` with `{0: "group/repository", "local_ref": "refs/rngit/push", "remote_ref": "refs/...", "force": false, "bundle": <binary Git bundle>}`. Existing-object updates send `/git/push` with an `operations` entry containing `action: "update_ref"`, `ref`, `sha` and `force`. Deletion uses `/git/delete` with the repository and ref. Successful push and delete responses are exactly `b"\x00"` with no file metadata. Requests larger than the link MDU are sent as request Resources, including binary bundles inside maps.

Reticulum may compress, encrypt and segment Resources. Channel is not part of the rngit protocol, and the tools do not use Channel messages for list or fetch requests.

### Runtime bounds

The node keeps at most 256 links and uses four request workers with a bounded queue of 256 requests by default. A full queue is reported as server busy. Repository requests and bundle transfers are limited to 256 MiB, and page requests to 1 MiB. These bounds cannot be changed through the configuration file.

## Differences from upstream

- **Direct transfer client.** `rngit clone` and `rngit fetch`, with their `--timeout`, `--max-response` and `--single-branch` options, are DotReticulum additions. The `DOTRETICULUM_*` helper variables are also additions.
- **No interactive shell.** Python's interactive runtime shell (`-i`, `--interactive`) is not provided.
- **Explicit authorization.** Executable permission resolvers, templates, editors and external repository sources each require explicit operator authorization with `--allow-exec` or `--allow-external`. Management content input is non-interactive by default.
- **Fetch exclusions.** The .NET importer sends a top-level `have` list, where the Python helper places `have` IDs inside each ref entry.
- **Batch size.** A nonpositive `ref_batch_size` fails instead of entering the reference's non-advancing zero-batch loop.
- **Release names.** Release and artifact names are validated more strictly than upstream's basename handling.
- **Hardened pages.** Filesystem link, download and media behavior is hardened instead of matching upstream's unsafe URL and path behavior.
- **Output.** Errors go to stderr instead of upstream's console progress output.
- **Pages.** Optional Pygments highlighting and enhanced `wcwidth` layout are absent.

## Limitations

- **Direct client.** Clone and fetch handle bare Git repositories and are read-only. Repository hosting, release download and publishing, shallow clone, submodule and LFS download, and signature verification are not part of the direct transfer client. Git owns working-tree checkout. Upstream fetch has no depth option. A detached remote HEAD that is not represented as a valid symbolic branch is unsupported.
- **Git helper.** Ordinary fetch and push work for SHA-1 repositories. Atomic push, object-format negotiation, shallow or depth fetch, filtering, submodules, LFS and remote lease guarantees are not implemented. Pushes are sequential, and changing an existing target requires force, including fast-forwards.
- **Configuration.** Client and node parsers implement bounded flat/list subsets, not full ConfigObj. Nested sections, interpolation and multiline values are unsupported.
- **Large repositories.** Transfers are buffered in memory and capped at 256 MiB by default; they are not streamed.
- **Media.** Conversion is limited to installed supported backends. Testing backend selection does not prove that its encoder is installed.
- **Release verification.** RSG cryptographic validity and a matching embedded signer are not publisher trust. The verifier must specify the signer identity independently, and Git's allowed-signers file is not consulted.
- **Platforms.** Windows x64 and Linux x64 are exercised, and macOS ARM64 is covered as described in [Getting the tools](#getting-the-tools). Other platforms remain unverified. See [TODO.md](../TODO.md).

## Verification

- `RngitTests` in `tests/DotReticulum.Applications.Tests/RngitTests.cs` exercises clone and fetch against the pinned Python `ReticulumGitNode`. It covers exact refs, HEAD and tree, strict fsck, denied reads, response limits, cancellation, incremental missing-object retrieval and no-change fetches. Live cases are opt-in.
- `tests/Pester/GitRemote.Tests.ps1` exercises the helper through real Git processes against the pinned Python peer when enabled. It covers working and bare clones, branches and tags, missing-object and unchanged fetches, push behavior and failure paths.

The harness verifies the imported RNS revision and tracked source files. Hosted platform evidence is distinct from live-node or mixed-topology evidence, and loopback tests do not establish public-network compatibility.

The Compatibility suite's `tests/Receive-Reference.ps1` uses the native candidate's `rngit fetch` to retrieve the pinned reference from the `rns://` locator through public TCP gateways. It seeds history through the parent of the pin from the GitHub comparison mirror, so it is an incremental fetch, not a full network clone. It requires the target objects to be initially absent, nonzero bundle bytes, strict Git checks and the exact pinned commit and tree. See the [development history](../.github/development-history.md) for gateway details and results.

### Windows reference adapter

On Windows, the Python management-client test adapter closes the received resource file just before the original retention callback moves it. The unmodified upstream callback would otherwise try to unlink an open file and raise `WinError 32`. The original callback, request handlers and wire and resource implementation stay in use; the adapter reads and closes the returned file and reports observations. This verifies interoperability with the disclosed Windows file-retention shim, not the unmodified upstream Windows retention path.
