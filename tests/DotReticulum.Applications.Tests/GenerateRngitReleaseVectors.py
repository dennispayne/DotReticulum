"""Thin adapter: execute pinned release handlers and rnid RSM signing APIs.

C# prepares disposable bare repositories, permissions, configs and the request
sequence. No reference handler, clock or permission resolver is replaced.
These observations are handler/vector evidence, not live-network interoperability.
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
from RNS.Utilities import rnid
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rns-config", required=True)
    parser.add_argument("--server-config", required=True)
    parser.add_argument("--requests", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    RNS.Reticulum(configdir=args.rns_config, loglevel=0)
    node = server.ReticulumGitNode(configdir=args.server_config)
    if not node.ready:
        raise RuntimeError("Reference server is not ready")
    inputs = json.loads(Path(args.requests).read_text(encoding="utf-8"))
    identity = RNS.Identity.from_bytes(bytes(range(64)))
    observations = []
    for case in inputs["cases"]:
        data = mp.unpackb(bytes.fromhex(case["request"])) if case["request"] else None
        response = node.handle_release("/mgmt/release", data, bytes(16),
                                       identity if case["identified"] else None, 0)
        observation = {"name": case["name"]}
        if isinstance(response, list):
            with response[0] as artifact:
                observation["file"] = artifact.read().hex()
            observation["metadata"] = mp.packb(response[1]).hex()
        else:
            observation["response"] = response.hex()
        observations.append(observation)
    vector = inputs["manifest"]
    artifact_bytes = bytes.fromhex(vector["artifact"])
    signature = rnid.create_rsg(identity, artifact_bytes, meta={"timestamp": vector["timestamp"]})
    metadata = {"name": "package", "version": "v1", "released": vector["released"],
                "timestamp": vector["timestamp"], "origin": bytes(range(16)),
                "path": "fixtures/repo.git", "commit": vector["commit"],
                "artifacts": [{"name": "payload.bin", "rsg": signature}]}
    manifest = rnid.create_rsg(identity, vector["notes"], embed=True, meta=metadata)
    result = {"upstream": PINNED_RNS_SHA, "rns_version": RNS.__version__,
              "rns_module": str(Path(RNS.__file__).resolve()),
              "git_process_policy": "C# adapter sets process-local safe.bareRepository=all and checkout-appropriate core.autocrlf; no persisted Git config or reference-source changes",
              "server_sha256": hashlib.sha256(Path(server.__file__).read_bytes()).hexdigest(),
              "rnid_sha256": hashlib.sha256(Path(rnid.__file__).read_bytes()).hexdigest(),
              "anchors": ["rngit/server.py:3367-3790", "rngit/server.py:878-1148",
                          "rnid.py:451-510", "rnid.py:588-601", "rngit/pages.py:2711-2734"],
              "cases": observations, "manifest": manifest.hex(), "artifact_signature": signature.hex()}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
