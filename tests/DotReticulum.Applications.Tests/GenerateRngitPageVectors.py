"""Execute pinned pages.py, not a page reimplementation.

Only fixture timing is normalized: the default base template's GEN_TIME token
is replaced with Unknown generation time. This is not live-node evidence.
Optional Pygments is disabled to exercise its documented plain-literal fallback.
All generated Git repositories live in the explicitly supplied project directory.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
from importlib import import_module
from collections import deque
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference
import RNS
from RNS.Utilities.rngit import pages, server
from RNS.Utilities.rngit import util, highlight
from RNS.Utilities.rngit import commitsigs
from RNS.Utilities import rnid
from RNS.Utilities.rngit.highlight import SyntaxHighlighter
from RNS.Utilities.rngit.util import MarkdownToMicron
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    for key in list(os.environ):
        if key.startswith("GIT_"):
            os.environ.pop(key)
    os.environ["GIT_TERMINAL_PROMPT"] = "0"
    RNS.loglevel = -1
    root = Path(args.work_directory).resolve()
    root.mkdir(parents=True, exist_ok=True)
    work = root / "working"
    group_path = root / "group"
    work.mkdir(exist_ok=True)
    group_path.mkdir(exist_ok=True)

    def git(*arguments, cwd=work, input=None):
        return subprocess.run(["git", "--no-pager", *arguments], cwd=cwd,
                              input=input, capture_output=True, check=True).stdout.decode("utf-8").strip()

    git("init", "--initial-branch=main")
    git("config", "user.name", "Page Author")
    git("config", "user.email", "pages@example.invalid")
    files = {
        "hello.txt": b"Hello world\n",
        "empty.txt": b"",
        "README.md": b"# Demo\n\nA **native** repository.\n",
        "page.mu": b">Already Micron\n\n`!bold`!\n",
        "binary.dat": b"\x00\x01\x02\xff",
        "large.txt": b"x" * (256 * 1024 + 1),
        "directory/sp ace+\u00e9.txt": b"UTF-8 filename\n",
        "markup.md": b"# Heading\n\n- item **bold**\n\n[doc](hello.txt#x)\n",
        "image.webp": b"RIFF\x00\x00\x00\x00WEBP",
    }
    for name, content in files.items():
        destination = work / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
    many = work / "many"
    many.mkdir()
    for i in range(1001):
        (many / f"file-{i:04d}.txt").write_bytes(b"x\n")
    git("add", ".")
    os.environ["GIT_COMMITTER_DATE"] = "2023-11-14T22:13:20Z"
    git("commit", "-m", "Initial pages fixture", "--date=2023-11-14T22:13:20Z")
    git("tag", "v1")
    git("tag", "-a", "v-annotated", "-m", "An annotated tag\n\nDetails.")
    git("branch", "feature/\u00e9+")
    bare = group_path / "demo"
    git("clone", "--bare", str(work), str(bare))
    git("config", "repository.description", "Page fixture", cwd=bare)
    git("config", "core.quotePath", "false", cwd=bare)
    git("config", "user.name", "Page Author", cwd=bare)
    git("config", "user.email", "pages@example.invalid", cwd=bare)
    os.environ["GIT_AUTHOR_DATE"] = "2023-11-14T22:13:20Z"
    history = git("rev-parse", "HEAD", cwd=bare)
    tree = git("rev-parse", "HEAD^{tree}", cwd=bare)
    for i in range(100):
        history = git("commit-tree", tree, "-p", history, cwd=bare, input=f"History {i}\n".encode("utf-8"))
    git("update-ref", "refs/heads/history", history, cwd=bare)

    owner = server.ReticulumGitNode.__new__(server.ReticulumGitNode)
    owner.record_stats = False
    owner.stats_enabled = False
    owner.stats_ignored = {}
    owner.identity_aliases = {}
    owner.blocked_identities = {}
    owner.stats = {}
    owner.stats_lock = threading.Lock()
    owner.destination = SimpleNamespace(hash=bytes(16))
    owner.configdir = str(root)
    owner.node_name = "Test Node"
    permissions = ["read", "write", "create", "stats", "release", "interact", "propose", "admin"]
    group = {permission: [owner.TGT_ALL] if permission == "read" else [] for permission in permissions}
    repo = {permission: [] for permission in permissions}
    repo.update(path=str(bare), fork=None, mirror=None)
    group.update(path=str(group_path), repositories={"demo": repo})
    owner.groups = {"public": group}
    node = pages.NomadNetworkNode.__new__(pages.NomadNetworkNode)
    node.owner = owner
    node.node_name = owner.node_name
    node.null_ident = RNS.Identity.from_bytes(bytes(64))
    node.templatesdir = str(root / "templates")
    node.templates = {name: getattr(pages, "DEFAULT_" + ("FRONT" if name == "front" else name.upper()) + "_TEMPLATE")
                      for name in ["base", "front", "group", "repo", "tree", "blob", "commits", "commit", "refs",
                                   "stats", "releases", "release", "work", "work_doc", "no_ident"]}
    node.templates["base"] = node.templates["base"].replace("{GEN_TIME}", "Unknown generation time")
    node.use_nerdfonts = False
    node.media_conversion = False
    node.highlight_syntax = True
    node.highlighter = SyntaxHighlighter()
    node.highlighter.pygments_available = False
    node.mdc = MarkdownToMicron(max_width=100, syntax_highlighter=node.highlighter)
    node.thanks_deque = deque(maxlen=256)
    node.active_links = {}
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(range(64)))
    signed_commits = {}
    parent = git("rev-parse", "HEAD", cwd=bare)
    key_wire = commitsigs.ssh_string(b"ssh-ed25519") + commitsigs.ssh_string(identity.get_public_key()[32:])
    for variant in ["author", "mismatch", "tampered", "bad_wrapping", "bad_armor"]:
        author = identity.hash.hex() if variant != "mismatch" else "not-the-signer"
        headers = (f"tree {tree}\nparent {parent}\nauthor Page Author <{author}> 1700000000 +0000\n"
                   "committer Page Author <pages@example.invalid> 1700000000 +0000").encode("utf-8")
        message = b"Signed pages\n"
        unsigned = headers + b"\n\n" + message
        rsg = rnid.create_rsg(identity, unsigned)
        if variant == "tampered":
            rsg = bytes([rsg[0] ^ 1]) + rsg[1:]
        wrapper = commitsigs.create_ssh_signature(key_wire, b"git", b"", b"sha256", rsg)
        if variant == "bad_wrapping":
            wrapper = b"not an SSHSIG wrapper"
        armor = commitsigs.armor_ssh_signature(wrapper).strip()
        if variant == "bad_armor":
            armor = "-----BEGIN PGP SIGNATURE-----\ninvalid\n-----END PGP SIGNATURE-----"
        lines = armor.split("\n")
        raw = headers + b"\ngpgsig " + lines[0].encode("utf-8")
        raw += b"".join(b"\n " + line.encode("utf-8") for line in lines[1:])
        raw += b"\n\n" + message
        commit = git("hash-object", "-t", "commit", "-w", "--stdin", cwd=bare, input=raw)
        git("update-ref", "refs/heads/signed-" + variant, commit, cwd=bare)
        signed_commits[variant] = commit
    cases = []
    routes = {
        "/page/index.mu": node.serve_front_page, "/page/group.mu": node.serve_group_page,
        "/page/repo.mu": node.serve_repo_page, "/page/tree.mu": node.serve_tree_page,
        "/page/blob.mu": node.serve_blob_page, "/page/commits.mu": node.serve_commits_page,
        "/page/commit.mu": node.serve_commit_page, "/page/refs.mu": node.serve_refs_page,
        "/page/stats.mu": node.serve_stats_page, "/page/releases.mu": node.serve_releases_page,
        "/page/release.mu": node.serve_release_page, "/page/work.mu": node.serve_work_page,
        "/page/work_doc.mu": node.serve_work_doc_page, "/media": node.serve_media,
        "/file/artifact": node.serve_artifact, "/file/download": node.serve_download,
        "/file/workdoc": node.serve_wd_download,
    }

    def call(name, route, request=None, caller=None, **settings):
        vector = dict(name=name, route=route, request=request, identified=caller is not None, **settings)
        try:
            response = routes[route](route, request, bytes(16), bytes(16), caller, 0)
        except Exception as error:
            vector["upstream_exception"] = type(error).__name__ + ": " + str(error)
            cases.append(vector)
            return
        if isinstance(response, bytes):
            vector["response"] = response.hex()
        elif isinstance(response, list):
            if hasattr(response[0], "read"):
                vector["file"] = response[0].read().hex()
                response[0].close()
                vector["name"] = response[1]["name"].decode("utf-8")
            else:
                vector["file"] = response[1].hex()
                vector["name"] = response[0]
                vector["workdoc"] = True
        else:
            vector["value"] = response
        cases.append(vector)

    def request(**values):
        return {"var_g": "public", "var_r": "demo", **{"var_" + k: str(v) for k, v in values.items()}}

    call("front", "/page/index.mu")
    call("group", "/page/group.mu", request())
    call("missing_group", "/page/group.mu", {"var_g": "missing"})
    call("invalid_group", "/page/group.mu", {})
    for route in routes:
        if route.startswith("/page/") and route not in ["/page/index.mu", "/page/group.mu"]:
            call("missing_" + route.split("/")[-1], route, request(g="missing"))
    call("repository", "/page/repo.mu", request())
    call("repository_thanks", "/page/repo.mu", request(thanks="y"), thanks_link=bytes(16).hex())
    call("repository_thanks_duplicate", "/page/repo.mu", request(thanks="y"), thanks_link=bytes(16).hex())
    call("tree", "/page/tree.mu", request())
    call("directory", "/page/tree.mu", request(path="directory"))
    call("tree_bad_path", "/page/tree.mu", request(path="nonexistent"))
    call("tree_bad_ref", "/page/tree.mu", request(ref="absent"))
    call("tree_page_negative", "/page/tree.mu", request(page="-1"))
    call("tree_unicode_ref", "/page/tree.mu", request(ref="feature/\u00e9+"))
    call("tree1000", "/page/tree.mu", request(path="many"))
    call("tree1001", "/page/tree.mu", request(path="many", page="1"))
    call("text", "/page/blob.mu", request(path="hello.txt"))
    call("empty_text", "/page/blob.mu", request(path="empty.txt"))
    call("micron", "/page/blob.mu", request(path="page.mu"))
    call("micron_source", "/page/blob.mu", request(path="page.mu", raw="y"))
    call("markdown", "/page/blob.mu", request(path="markup.md"))
    call("binary", "/page/blob.mu", request(path="binary.dat"))
    call("large", "/page/blob.mu", request(path="large.txt"))
    call("unicode", "/page/blob.mu", request(path="directory%2Fsp+ace%2B%C3%A9.txt"))
    call("blob_directory", "/page/blob.mu", request(path="directory"))
    call("blob_bad_ref", "/page/blob.mu", request(path="hello.txt", ref="absent"))
    call("blob_missing_path", "/page/blob.mu", request())
    call("blob_not_found", "/page/blob.mu", request(path="absent.txt"))
    call("refs", "/page/refs.mu", request())
    call("refs_heads", "/page/refs.mu", request(type="heads"))
    call("refs_tags", "/page/refs.mu", request(type="tags"))
    call("empty_commits", "/page/commits.mu", request(page=20))
    call("commits", "/page/commits.mu", request())
    call("commits100", "/page/commits.mu", request(ref="history"))
    call("commits101", "/page/commits.mu", request(ref="history", page="1"))
    call("commit", "/page/commit.mu", request(h=git("rev-parse", "HEAD", cwd=bare)))
    for variant, commit in signed_commits.items():
        call("signed_commit_" + variant, "/page/commit.mu", request(h=commit))
    call("bad_commit", "/page/commit.mu", request(h="deadbee"))
    call("missing_commit", "/page/commit.mu", request())
    call("stats_denied", "/page/stats.mu", request())
    call("releases_empty", "/page/releases.mu", request())
    call("release_absent", "/page/release.mu", request(t="v1"))
    call("work_empty", "/page/work.mu", request())
    call("work_all", "/page/work.mu", request(scope="all"))
    call("work_bad_id", "/page/work_doc.mu", request(id="bad"))
    call("work_absent", "/page/work_doc.mu", request(id="99"))
    for file in ["hello.txt", "binary.dat", "directory%2Fsp+ace%2B%C3%A9.txt"]:
        call("download_" + file, "/file/download", request(path=file))
    call("download_absent", "/file/download", request(path="absent"))
    call("download_bad_format", "/file/download", request(path="hello.txt", fmt="mu"))
    call("artifact_absent", "/file/artifact", request(t="v1", a="asset"))
    call("work_download_absent", "/file/workdoc", request(id="99"))
    call("media_bad_request", "/media", {})
    call("media_absent", "/media", {"key": "k", "path": "/media/public/demo/HEAD/absent"})
    call("media_webp", "/media", {"key": "k", "path": "/media/public/demo/HEAD/image.webp"})
    repo["read"] = [owner.TGT_NONE]
    call("front_hidden", "/page/index.mu", repository_allowed="r:none\n")
    call("download_denied", "/file/download", request(path="hello.txt"), repository_allowed="r:none\n")
    repo["read"] = []
    owner.blocked_identities = {node.null_ident.hash: True}
    call("anonymous_blocked", "/page/index.mu", blocked_anonymous=True)
    owner.blocked_identities = {}

    group["stats"] = [owner.TGT_ALL]
    original_time = server.time.time
    server.time.time = lambda: 1700000100.0
    try:
        for title, metric in [
            ("stats_empty", {}),
            ("stats_activity", {"view": {"2023-11-14": 2}, "fetch": {"2023-11-14": 3},
                                "push": {"2023-11-14": 1}, "download": {"2023-11-14": 1},
                                "release_download": {"2023-11-14": 1}}),
        ]:
            owner.stats = {"pages": {"front": {}}, "groups": {"public": {"view": {}, "repositories": {"demo": metric}}}}
            call(title, "/page/stats.mu", request(), repository_allowed="stats:all\n",
                 statistics=mp.packb(owner.stats).hex(), stats_timestamp=1700000100)
    finally:
        server.time.time = original_time
        group["stats"] = []
        owner.stats = {}

    release = Path(str(bare) + ".releases") / "v1"
    (release / "artifacts").mkdir(parents=True)
    (release / "META").write_text("tag = v1\nstatus = published\ncreated = 1700000000\n", encoding="utf-8")
    (release / "RELEASE.md").write_text("# Release\n\nNotes **bold**.\n", encoding="utf-8")
    (release / "artifacts" / "sp ace+\u00e9.bin").write_bytes(b"artifact bytes")
    (release.parent / "latest").write_text("v1", encoding="utf-8")
    draft = release.parent / "draft"
    draft.mkdir()
    (draft / "META").write_text("tag = draft\nstatus = draft\ncreated = 1700000001\n", encoding="utf-8")
    doc = {"content": "# Work\n\nBody **bold**.", "meta": {
        "title": "Test Work", "format": "markdown", "author": identity.hash,
        "identity": identity.get_public_key(), "created": 1700000000.0, "edited": 1700000000.0,
        "signature": identity.sign(b"# Work\n\nBody **bold**.")}}
    doc_path = Path(str(bare) + ".work") / "active" / "1" / "root"
    assert owner._work_save_document(str(doc_path), doc)
    comment = {"content": "An update", "meta": {"author": identity.hash, "created": 1700000001.0},
               "format": "micron"}
    assert owner._work_save_document(str(doc_path.parent / "1"), comment)
    seed = {}
    for folder in [Path(str(bare) + ".releases"), Path(str(bare) + ".work")]:
        for file in folder.rglob("*"):
            if file.is_file():
                seed[str(file.relative_to(group_path)).replace("\\", "/")] = file.read_bytes().hex()
    for route, values in [
        ("/page/releases.mu", {}), ("/page/release.mu", {"t": "v1"}), ("/page/release.mu", {"t": "latest"}),
        ("/page/release.mu", {"t": "draft"}), ("/file/artifact", {"t": "latest", "a": "sp+ace%2B%C3%A9.bin"}),
        ("/file/artifact", {"t": "draft", "a": "asset"}), ("/page/work.mu", {}),
        ("/page/work_doc.mu", {"id": "1"}), ("/file/workdoc", {"id": "1"}),
        ("/page/work_doc.mu", {"id": "\u0661"}), ("/file/workdoc", {"id": "+0_1"}),
    ]:
        call("seeded_" + route.split("/")[-1] + "_" + str(values), route, request(**values), seeded=True)
    call("release_thanks", "/page/release.mu", request(t="v1", thanks="y"), seeded=True, thanks_link=bytes(16).hex())
    call("release_thanks_duplicate", "/page/release.mu", request(t="v1", thanks="y"), seeded=True, thanks_link=bytes(16).hex())

    provenance_now = 1700000100
    source_destination = bytes.fromhex("1234567890abcdef1234567890abcdef")
    public_source = "rns://" + source_destination.hex() + "/upstream/project"
    metadata_keys = ["repository.rngit.type", "repository.rngit.upstream.source", "repository.rngit.upstream.sync"]
    original_instance = RNS.Reticulum.get_instance()
    standalone = RNS.Reticulum.__new__(RNS.Reticulum)
    standalone.is_connected_to_shared_instance = False
    RNS.Reticulum._Reticulum__instance = standalone
    original_time = server.time.time
    server.time.time = lambda: float(provenance_now)

    def provenance(name, kind="fork", source=public_source, sync=None, route="/page/repo.mu",
                   values=None, known=False, head="main", permission=None, caller=None,
                   blocked=False, presentation=None, sensitive=None):
        metadata = dict(zip(metadata_keys, [kind, source, sync]))
        for key, value in metadata.items():
            result = subprocess.run(["git", "config", "--unset-all", key], cwd=bare, capture_output=True)
            assert result.returncode in [0, 5], result.stderr
            if value is not None:
                git("config", "--replace-all", "--", key, str(value), cwd=bare)
        git("symbolic-ref", "HEAD", "refs/heads/" + head, cwd=bare)
        repo["fork"] = owner._ReticulumGitNode__is_fork(str(bare))
        repo["mirror"] = owner._ReticulumGitNode__is_mirror(str(bare))
        repo["read"] = [owner.TGT_NONE] if permission == "none" else [identity.hash] if permission == "caller" else []
        owner.blocked_identities = {node.null_ident.hash: True} if blocked else {}
        RNS.Identity.known_destinations.pop(source_destination, None)
        settings = {"seeded": True, "upstream_config": metadata, "clock_timestamp": provenance_now, "head_ref": head}
        if known:
            RNS.Identity.remember(bytes(32), source_destination, identity.get_public_key())
            settings["known_source"] = source_destination.hex()
        if permission:
            settings["repository_allowed"] = "r:" + (identity.hash.hex() if permission == "caller" else "none") + "\n"
        if blocked:
            settings["blocked_anonymous"] = True
        if presentation is not None:
            settings["redact_source"] = {"from": source, "to": presentation}
            settings["sensitive_values"] = sensitive
        call(name, route, request(**(values or {})), caller=caller, **settings)

    try:
        provenance("fork_public_unknown", sync=str(provenance_now - 3661))
        provenance("fork_public_known", known=True, sync=str(provenance_now - 61))
        provenance("mirror_public_known", kind="mirror", known=True, sync=str(provenance_now - 59))
        provenance("fork_unicode_source", known=True, source="rns://" + source_destination.hex() + "/groupe+\u00e9/projet+\u00e9", sync=str(provenance_now))
        provenance("mirror_public_https", kind="mirror", source="https://example.invalid/upstream/project.git", sync=str(provenance_now - 90061))
        provenance("mirror_future_sync", kind="mirror", sync=str(provenance_now + 3600))
        provenance("fork_missing_sync")
        provenance("mirror_invalid_sync", kind="mirror", sync="not-a-timestamp")
        provenance("fork_negative_sync", sync="-10")
        provenance("fork_default_head_history", known=True, head="history", sync=str(provenance_now - 1))
        provenance("fork_explicit_ref", known=True, values={"ref": "main"}, sync=str(provenance_now - 1))
        provenance("unknown_repository_kind", kind="other", sync=str(provenance_now))
        provenance("missing_repository_kind", kind=None, sync=str(provenance_now))
        provenance("missing_upstream_source", source=None, sync=str(provenance_now))
        provenance("empty_upstream_source", source="", sync=str(provenance_now))
        provenance("invalid_rns_source_hash", source="rns://" + "z" * 32 + "/upstream/project", sync=str(provenance_now))
        for label, route, values in [
            ("index", "/page/index.mu", {}), ("group", "/page/group.mu", {}),
            ("tree", "/page/tree.mu", {}), ("refs", "/page/refs.mu", {}),
            ("releases", "/page/releases.mu", {}), ("release", "/page/release.mu", {"t": "v1"}),
            ("work", "/page/work.mu", {"scope": "all"}), ("work_doc", "/page/work_doc.mu", {"id": "1"}),
        ]:
            provenance("fork_" + label + "_no_annotation", known=True, route=route, values=values, sync=str(provenance_now))
        provenance("fork_denied_no_disclosure", known=True, permission="none", sync=str(provenance_now))
        provenance("fork_identity_only_anonymous", known=True, permission="caller", sync=str(provenance_now))
        provenance("fork_identity_only_allowed", known=True, permission="caller", caller=identity, sync=str(provenance_now))
        provenance("fork_blocked_anonymous", known=True, blocked=True, sync=str(provenance_now))
        provenance("fork_credentialed_https", source="https://fixture-user:fixture-password@example.invalid/project.git?access_token=fixture-query-token#fixture-fragment-token",
                   sync=str(provenance_now), presentation="https://example.invalid/project.git",
                   sensitive=["fixture-user", "fixture-password", "fixture-query-token", "fixture-fragment-token"])
        provenance("mirror_credentialed_ssh", kind="mirror", source="ssh://fixture-user:fixture-password@example.invalid/project.git?token=fixture-query-token",
                   sync=str(provenance_now), presentation="ssh://example.invalid/project.git",
                   sensitive=["fixture-user", "fixture-password", "fixture-query-token"])
        provenance("fork_credentialed_scp", source="fixture-user@example.invalid:project.git?token=fixture-query-token",
                   sync=str(provenance_now), presentation="example.invalid:project.git",
                   sensitive=["fixture-user", "fixture-query-token"])
        provenance("fork_malformed_credentialed_uri", source="https://fixture-user:fixture-password@/project.git?token=fixture-query-token",
                   sync=str(provenance_now), presentation="[upstream source]",
                   sensitive=["fixture-user", "fixture-password", "fixture-query-token"])
    finally:
        server.time.time = original_time
        RNS.Reticulum._Reticulum__instance = original_instance
        RNS.Identity.known_destinations.pop(source_destination, None)
        repo["read"] = []
        repo["fork"] = None
        repo["mirror"] = None
        owner.blocked_identities = {}
        git("symbolic-ref", "HEAD", "refs/heads/main", cwd=bare)
        for key in metadata_keys:
            subprocess.run(["git", "config", "--unset-all", key], cwd=bare, capture_output=True)
    call("work_global_invalid", "/page/work.mu", {})
    call("work_group_invalid", "/page/work.mu", {"var_g": "public"})
    bundle = root / "fixture.bundle"
    git("bundle", "create", str(bundle), "--all", cwd=bare)

    markdown = [
        "", "# Title\n\nParagraph **bold** and *italic*.", "- bullet\n  - nested\n\n---",
        "[local](doc.md#part) [external](https://example.invalid/a#b)", "`inline` and __bold__",
        "> quote\n> next line\n\ntext", "```\na ` b\n```", "```rawmu\n>Raw\n```",
        "\\path\n<test\n-dash", "| A | B |\n| --- | ---: |\n| x | yy |",
        "unclosed\n```\ncode", "## Header [link](file.md)", "![](image.webp)",
        "```csharp\nstring value = `input`;\n```",
        "# H1\n## H2\n### H3\n#### H4\n##### H5\n###### H6\n\n===\n***\n___",
        "| **Heading** | `Code` |\n| :---: | ---: |\n| centered | right |",
        "| Escaped | Value |\n| --- | --- |\n| a\\|b | c\\\\d |",
        "| Wide | Other |\n| --- | --- |\n| " + "x" * 140 + " | " + "y" * 70 + " |",
        "> " + "word" * 40 + "\n\nAfter quote",
        "```unknown-language\n\\path `literal`\n```",
        "[fragment](#anchor) ![alt](image.png) [code `name`](../file.md)",
        "| 漢字 | emoji 👩‍💻 |\n| --- | --- |\n| e\u0301 | 👩🏽‍💻 |",
        "| Astral |\n| --- |\n| " + "😀" * 140 + " |",
        "> " + "😀" * 110,
    ]
    vectors = [{"input": text, "output": node.mdc.format_block(text), "scope": ":/page/"} for text in markdown]
    charts = []
    for data in [[], [0, 0, 0], [1, 3, 0, 2], [100, 1, 50, 75, 0]]:
        charts.append({"data": data, "labels": ["Before", "Today"],
                       "output": node.render_chart_halfblock(data, ["Before", "Today"], color="10b981", secondary_color="1c5e71")})
    combined = {"views": [1, 0, 2, 4], "fetches": [0, 0, 3, 1],
                "pushes": [3, 0, 1, 0], "downloads": [1, 0, 0, 2], "labels": ["Before", "Today"]}
    combined["output"] = node.render_combined_chart(combined["views"], combined["fetches"],
                                                  combined["pushes"], combined["downloads"], combined["labels"])
    output = {
        "upstream": PINNED_RNS_SHA, "rns_version": RNS.__version__, "rns_module": str(Path(RNS.__file__).resolve()),
        "source_sha256": {name: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest()
                          for name, module in [("pages.py", pages), ("server.py", server), ("util.py", util),
                                               ("highlight.py", highlight), ("commitsigs.py", commitsigs),
                                               ("rnid.py", rnid), ("RNS.__init__.py", RNS),
                                               ("Identity.py", import_module("RNS.Identity")),
                                               ("Reticulum.py", import_module("RNS.Reticulum"))]},
        "shims": ["GEN_TIME token replaced in fixture base template", "Stats-only clock frozen at 1700000100",
                  "Optional Pygments disabled; literal fallback", "Process-local ambient GIT_* overrides removed",
                  "Repository core.quotePath=false for UTF-8 names", "Node __new__ avoids starting listeners/jobs; real source permissions/handlers run"],
        "provenance_settings": ["Provenance-only clock frozen at 1700000100",
                                "Metadata populated through real server private fork/mirror readers from persisted Git config",
                                "Standalone Reticulum __new__ singleton permits real Identity.recall and cache-use bookkeeping without listeners",
                                "Synthetic credentialed-source reference bytes retained; tests explicitly redact only presented source URL"],
        "files": {name: content.hex() for name, content in files.items()}, "head": git("rev-parse", "HEAD", cwd=bare),
        "private_key": bytes(range(64)).hex(), "cases": cases, "markdown": vectors,
        "bundle": bundle.read_bytes().hex(), "seed": seed, "charts": charts, "combined_chart": combined,
        "sizes": [{"bytes": size, "output": RNS.prettysize(size)}
                  for size in [-9223372036854775808, -2675, -1000, -1, 0, 1, 999, 1000, 1005, 1015, 2675, 262144, 262145, 999995, 999999,
                               1000000, 2675000000, 9223372036854775807]],
        "generation_times": [{"seconds": float(seconds), "output": "Generated in " + RNS.prettytime(float(seconds))}
                             for seconds in [0, 0.000001, 0.0049, 0.005, 0.0149, 0.015, 0.125, 0.375,
                                             1, 1.005, 2.675, 59.999, 60, 60.01, 61.5, 3600,
                                             3661.25, 86400, 90061.25, -0.0049, -0.005, -1, -90061.25]],
        "sync_ages": [{"seconds": float(seconds), "output": RNS.prettytime(float(max(0, seconds)), compact=True).split(" ")[0]}
                      for seconds in [-1, 0, 0.99, 1, 59.99, 60, 61, 3599.9, 3600, 86400, 90061, 1700000100]],
    }
    Path(args.output).write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(cases)} pages/downloads and {len(vectors)} markdown vectors from {RNS.__file__}")


if __name__ == "__main__":
    main()
