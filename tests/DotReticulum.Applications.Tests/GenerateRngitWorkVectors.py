"""Execute pinned rngit work handlers; Python is only the upstream API adapter.

The deterministic clock is an explicit fixture-only shim. Windows os.rename
cannot replace existing files: persisted edits/perms are observed on platforms
where upstream supports replacement; no filesystem primitive is patched.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities.rngit import server
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    root = Path(args.work_directory).resolve()
    if root.exists() and any(root.iterdir()):
        raise RuntimeError("Reference work directory must be empty.")
    root.mkdir(parents=True, exist_ok=True)
    RNS.loglevel = -1
    node = server.ReticulumGitNode.__new__(server.ReticulumGitNode)
    node.record_stats = False
    node.identity_aliases = {}
    node.blocked_identities = {}
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(range(64)))
    permissions = ["read", "write", "create", "stats", "release", "interact", "propose", "admin"]
    group = {p: [node.TGT_ALL] for p in permissions}
    repo = {p: [] for p in permissions}
    repo["path"] = str(root / "repo")
    group["repositories"] = {"repo": repo}
    group["path"] = str(root)
    node.groups = {"group": group}
    original_clock = server.time.time
    server.time.time = lambda: 1700000000.25
    cases = []

    def call(name, data, caller=identity):
        response = node.handle_work("/mgmt/work", data, b"", caller, 0)
        cases.append({"name": name, "request": mp.packb(data).hex(),
                      "identified": caller is not None, "response": response.hex()})
        if name in ["create", "comment_unsigned", "propose", "complete", "activate"]:
            work = root / "repo.work"
            cases[-1]["files"] = {str(path.relative_to(work)).replace("\\", "/"): path.read_bytes().hex()
                                 for path in work.rglob("*") if path.is_file() and (path.name == "root" or path.name.isdigit())}

    def request(operation, **fields):
        return {0: "group/repo", "operation": operation, **fields}

    try:
        call("unidentified", None, None)
        call("invalid_type", [])
        call("missing_repository", {"operation": "list"})
        call("missing_operation", {0: "group/repo"})
        call("unknown_operation", request("update"))
        call("empty_list", request("list"))
        call("invalid_scope_list", request("list", scope="global"))
        call("view_missing_id", request("view"))
        call("invalid_id", request("view", doc_id="no"))
        call("unknown_document", request("view", doc_id=42))
        call("missing_signature", request("create", title="Title", content="Body"))
        call("short_signature", request("create", title="Title", content="Body", signature=b"a"))
        call("invalid_signature", request("create", title="Title", content="Body", signature=b"a" * 64))
        call("wrong_signature_type", request("create", title="Title", content="Body", signature=True))
        call("wrong_title_type", request("create", title=None, content="Body", signature=identity.sign(b"Body")))
        call("create", request("create", title="  Title  ", content=" Body ", format="bad",
                               signature=identity.sign(b"Body")))
        call("view", request("view", doc_id=1))
        call("list", request("list"))
        call("comment_unsigned", request("comment", doc_id=1, content=" update ", format="micron"))
        call("view_comment", request("view", doc_id="1", scope="completed"))
        call("propose", request("propose", title="Proposal", content="Next",
                                signature=identity.sign(b"Next")))
        call("complete", request("complete", doc_id=1))
        call("activate", request("activate", doc_id=2))
        call("all_list", request("list", scope="all"))
        call("perms_get", request("perms", doc_id=2, step="get"))
        call("perms_invalid", request("perms", doc_id=2, step="set", content="bad"))
        call("delete_missing_allowed", request("delete", doc_id=1))
        call("delete_proposed_owner", request("delete", doc_id=2))
        call("final_list", request("list", scope="all"))
        call("null_scope_list", request("list", scope=None))
        call("invalid_scope_view", request("view", doc_id=1, scope=None))
        call("empty_array_operation", {0: "group/repo", "operation": []})
        call("empty_array_doc_id", request("view", doc_id=[]))
        call("zero_doc_id", request("view", doc_id=0))
        call("unicode_doc_id", request("view", doc_id="\u0661"))
        call("decimal_underscore_id", request("view", doc_id="0_1"))
        call("invalid_bytes_id", request("view", doc_id=b"\xff"))
        call("unknown_comment", request("comment", doc_id=999, content="update"))
        call("unknown_delete", request("delete", doc_id=999))
        call("unknown_complete", request("complete", doc_id=999))
        call("unknown_activate", request("activate", doc_id=999))
        call("perms_missing_step", request("perms", doc_id=1))
        call("perms_unknown_step", request("perms", doc_id=1, step="unknown"))
        call("empty_content", request("create", title="T", content="", signature=identity.sign(b"")))
        call("empty_title", request("create", title="", content="C", signature=identity.sign(b"C")))
        call("edit_no_changes", request("edit", doc_id=1, signature=identity.sign(b"")))
        repo["read"] = [node.TGT_NONE]
        call("hidden_repository", request("view", doc_id=1))
        cases[-1]["repository_allowed"] = "r:none\n"
        repo["read"] = []
        node.blocked_identities = {identity.hash: True}
        call("blocked_identity", request("view", doc_id=1))
        cases[-1]["blocked"] = True
        node.blocked_identities = {}
        (root / "repo.work" / "1.allowed").write_text("r:none\n", encoding="utf-8")
        group["admin"] = []
        call("hidden_document", request("view", doc_id=1))
        cases[-1]["document_allowed"] = "r:none\n"
        cases[-1]["group_allowed"] = "r:all, w:all, i:all, p:all"
        group["admin"] = [node.TGT_ALL]
        (root / "repo.work" / "1.allowed").unlink()
    finally:
        server.time.time = original_clock
    source = Path(server.__file__).resolve()
    if not source.is_relative_to(Path(RNS.__file__).resolve().parent):
        raise RuntimeError(f"rngit server imported outside verified RNS source: {source}")
    result = {"upstream_sha": PINNED_RNS_SHA, "rns_version": RNS.__version__,
              "rns_module": str(Path(RNS.__file__).resolve()),
              "module": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "clock_shim": "1700000000.25", "private_key": bytes(range(64)).hex(),
              "cases": cases}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
