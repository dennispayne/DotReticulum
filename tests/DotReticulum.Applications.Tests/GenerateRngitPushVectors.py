"""Execute pinned upstream push/delete handlers on a disposable Git fixture.

C#/PowerShell prepares the repositories and configs. This adapter only calls
the actual reference APIs; it does not emulate permissions or Git operations.
These handler vectors are not live-network interoperability evidence.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities.rngit import client, server


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rns-config", required=True)
    parser.add_argument("--server-config", required=True)
    parser.add_argument("--object", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    RNS.Reticulum(configdir=args.rns_config, loglevel=0)
    node = server.ReticulumGitNode(configdir=args.server_config)
    if not node.ready:
        raise RuntimeError("Reference server is not ready")
    identity = RNS.Identity()
    repo = "fixtures/fixture.git"
    target = "refs/heads/vector-disposable"
    operation = {"action": "update_ref", "ref": target, "sha": args.object, "force": False}
    cases = [
        ("unidentified", "push", {0: repo}, None),
        ("non-map", "push", None, identity),
        ("no-repository", "push", {}, identity),
        ("no-data", "push", {0: repo}, identity),
        ("operations-type", "push", {0: repo, "operations": "bad"}, identity),
        ("unknown-operation", "push", {0: repo, "operations": [{"action": "bad"}]}, identity),
        ("invalid-ref", "push", {0: repo, "operations": [{**operation, "ref": "bad"}]}, identity),
        ("invalid-sha", "push", {0: repo, "operations": [{**operation, "sha": "bad"}]}, identity),
        ("missing-object", "push", {0: repo, "operations": [{**operation, "sha": "f" * 40}]}, identity),
        ("invalid-bundle", "push", {0: repo, "local_ref": target, "remote_ref": target, "bundle": b"not a bundle"}, identity),
        ("read-only", "push", {0: "readonly/fixture.git", "operations": [operation]}, identity),
        ("hidden", "push", {0: "hidden/fixture.git", "operations": [operation]}, identity),
        ("create-existing-object", "push", {0: repo, "operations": [operation]}, identity),
        ("delete", "delete", {0: repo, "ref": target}, identity),
        ("delete-absent", "delete", {0: repo, "ref": target}, identity),
        ("delete-invalid-ref", "delete", {0: repo, "ref": "bad"}, identity),
    ]
    vectors = []
    for name, verb, data, peer in cases:
        handler = node.handle_push if verb == "push" else node.handle_delete
        response = handler("/git/" + verb, data, bytes(16), peer, 0)
        vectors.append({"name": name, "response": response.hex(), "success": response == b"\x00"})
    result = {
        "upstream": PINNED_RNS_SHA,
        "client_sha256": hashlib.sha256(Path(client.__file__).read_bytes()).hexdigest(),
        "server_sha256": hashlib.sha256(Path(server.__file__).read_bytes()).hexdigest(),
        "cases": vectors,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
