"""Thin adapter executing the pinned server's real policy and hosting handlers.

No network server is started: object.__new__ initializes only the runtime fields
required by these actual methods; load_repository_group performs real discovery.
PowerShell owns the disposable fixture directory and Git setup.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from threading import Lock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities.rngit import server
from RNS.vendor.configobj import ConfigObj


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()
    verify_reference()
    # The CLI harness injects safe.bareRepository=explicit; upstream discovers
    # owned bare repositories via cwd, so use an explicit clean fixture policy.
    for name in list(os.environ):
        if name.startswith("GIT_CONFIG_"):
            os.environ.pop(name)
    RNS.loglevel = 7 if args.verbose else 0
    # Windows os.access(X_OK) marks all existing .allowed files executable, so
    # unmodified upstream tries to launch static text as Win32 executables.
    # This narrowly disclosed adapter supplies Unix static-file classification.
    access = os.access
    if os.name == "nt":
        server.os.access = lambda path, mode: False if mode == os.X_OK and str(path).endswith(".allowed") else access(path, mode)
    root = Path(args.directory).resolve(strict=True)
    identity = RNS.Identity.from_bytes(bytes(range(1, 65)))
    other = RNS.Identity.from_bytes(bytes(range(65, 129)))
    node = object.__new__(server.ReticulumGitNode)
    node.groups = {}
    node.identity_aliases = {"owner": identity.hash.hex()}
    node.blocked_identities = {}
    node.perms_lock = Lock()
    node.config = ConfigObj([
        "[access]", "public = r:all, c:all, adm:owner",
        "readonly = r:all", "hidden = r:none",
    ])
    for group in ("public", "readonly", "hidden"):
        node.load_repository_group(group, str(root / group))
    vectors = []

    def call(name, method, data, peer=identity):
        result = getattr(node, method)("/git/" + method[7:], data, bytes(16), peer, 0)
        vectors.append({"name": name, "response": result.hex()})

    call("unidentified", "handle_create", {0: "public/new.git"}, None)
    call("invalid-map", "handle_create", None)
    call("no-repository", "handle_create", {})
    call("invalid-path", "handle_create", {0: "bad"})
    call("missing-group", "handle_create", {0: "absent/new.git"})
    call("read-only-create", "handle_create", {0: "readonly/new.git"})
    call("hidden-create", "handle_create", {0: "hidden/new.git"})
    call("create", "handle_create", {0: "public/new.git"})
    call("existing", "handle_create", {0: "public/new.git"})
    call("ordinary-sync", "handle_sync", {0: "public/new.git"})
    call("perms-invalid", "handle_perms", {})
    call("group-get", "handle_perms", {2: "public", "operation": "gperms", "step": "get"})
    call("repo-get", "handle_perms", {0: "public/new.git", "operation": "rperms", "step": "get"})
    call("repo-admin-cannot-set", "handle_perms",
         {0: "public/new.git", "operation": "rperms", "step": "set", "content": "r:all"}, other)
    call("invalid-permission", "handle_perms",
         {2: "public", "operation": "gperms", "step": "set", "content": "# note\nbad:all"})
    call("group-set", "handle_perms",
         {2: "public", "operation": "gperms", "step": "set", "content": "w:owner\n"})
    call("group-reloaded-get", "handle_perms", {2: "public", "operation": "gperms", "step": "get"})
    # Remove the prior static file as fixture setup: Windows os.rename cannot
    # replace it. Separate .NET tests cover portable overwrite persistence.
    (root / "public" / "new.git.allowed").unlink()
    call("repo-set", "handle_perms",
         {0: "public/new.git", "operation": "rperms", "step": "set", "content": "r:all\nw:none\n"})
    call("repo-reloaded-get", "handle_perms", {0: "public/new.git", "operation": "rperms", "step": "get"})

    decisions = []
    repo = root / "public" / "new.git"
    doc_path = Path(str(repo) + ".work")
    doc_path.mkdir()
    for name, allowed, document, blocked in [
        ("inherit", "", "", False),
        ("repo-none-beats-admin", "w:none", "", False),
        ("repo-unmatched-overrides", "w:" + "f" * 32, "", False),
        ("admin-all-not-universal", "adm:all", "", False),
        ("document-unmatched-falls-back", "", "w:" + "f" * 32, False),
        ("document-none-denies", "", "w:none", False),
        ("document-all-beats-repo-none", "w:none", "w:all", False),
        ("blocked-only-repository", "", "", True),
    ]:
        Path(str(repo) + ".allowed").write_text(allowed, encoding="utf-8")
        (doc_path / "1.allowed").write_text(document, encoding="utf-8")
        node.update_repository_permissions("public", "new.git")
        node.blocked_identities = {identity.hash: True} if blocked else {}
        decisions.append({
            "name": name, "allowed": allowed, "document": document, "blocked": blocked,
            "repository": node.resolve_permission(identity, "public", "new.git", node.PERM_WRITE),
            "group": node.resolve_group_permission(identity, "public", node.PERM_WRITE),
            "doc": node.resolve_doc_permission(identity, "public", "new.git", 1, node.PERM_WRITE),
        })
    result = {
        "upstream": PINNED_RNS_SHA,
        "server_sha256": hashlib.sha256(Path(server.__file__).read_bytes()).hexdigest(),
        "reference_shim": "Windows only: classify .allowed files as non-executable for os.access(X_OK); does not verify unmodified upstream Windows hosting.",
        "fixture_setup": "Clear inherited GIT_CONFIG_*; remove repository .allowed before repo-set to avoid Windows os.rename overwrite failure; .NET overwrite persistence tested separately.",
        "identity_private": bytes(range(1, 65)).hex(),
        "identity_hash": identity.hash.hex(),
        "other_public": other.get_public_key().hex(),
        "cases": vectors,
        "decisions": decisions,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
