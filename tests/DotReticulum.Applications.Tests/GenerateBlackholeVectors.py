"""Execute pinned blackhole methods; no replacement protocol implementation.

Only path-table invalidation is stubbed (no router is running), clocks are fixed,
and the updater link/receipt is a synchronous observation adapter. Actual local
management, /list provider, remote merge, source-file persistence and reload
methods are unmodified. This is differential fixture evidence, not live interop.
The request payload is captured from the actual Link.request packet constructor;
packet transmission returns False to stop before receipt timers are allocated.
"""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Discovery import BlackholeUpdater
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    RNS.loglevel = -1
    root = Path(args.work_directory).resolve()
    root.mkdir(parents=True, exist_ok=True)
    if any(root.iterdir()):
        raise RuntimeError("Reference work directory must be empty")
    local = bytes.fromhex("aa" * 16)
    remote = bytes.fromhex("bb" * 16)
    relay = bytes.fromhex("cc" * 16)
    hashes = [bytes.fromhex(f"{i:02x}" * 16) for i in range(1, 7)]
    transport = RNS.Transport
    RNS.Reticulum.blackholepath = str(root)
    transport.identity = SimpleNamespace(hash=local)
    transport.blackholed_identities = {}
    instance = RNS.Reticulum.__new__(RNS.Reticulum)
    instance.is_connected_to_shared_instance = False
    results = {}
    with patch.object(transport, "remove_blackholed_paths"), \
         patch.object(RNS.Reticulum, "blackhole_sources", return_value=[remote]), \
         patch("time.time", return_value=1000.0):
        results["add"] = instance.blackhole_identity(hashes[0], reason="local Δ")
        results["duplicate"] = instance.blackhole_identity(hashes[0], reason="ignored")
        results["invalid"] = instance.blackhole_identity(b"x")
        instance.blackhole_identity(hashes[1], until=2000.0, reason="temporary")
        results["local_publication"] = mp.packb(transport.blackhole_list_handler(None, None, None, None, None, None)).hex()
        results["local_file"] = (root / "local").read_bytes().hex()
        incoming = {
            hashes[0]: {"source": remote, "until": None, "reason": "conflict ignored"},
            hashes[2]: {"source": relay, "until": None, "reason": "relayed"},
            hashes[3]: {"source": remote, "until": 900, "reason": "expired"},
            hashes[4]: {"source": remote, "until": 0, "reason": "zero"},
            hashes[5]: {"source": remote, "until": 1000.0, "reason": "equal clock"},
        }
        results["remote_publication"] = mp.packb(incoming).hex()
        updater = BlackholeUpdater()
        requests = []
        link = SimpleNamespace(
            get_remote_identity=lambda: SimpleNamespace(hash=remote),
            request=lambda path: requests.append(path) or SimpleNamespace(concluded=lambda: True, get_response=lambda: incoming),
            teardown=lambda: None,
        )
        updater.update_link_established(link)
        results["merged_publication"] = mp.packb(transport.blackholed_identities).hex()
        results["source_file"] = (root / remote.hex()).read_bytes().hex()
        for empty in [None, {}]:
            link.request = lambda path: SimpleNamespace(concluded=lambda: True, get_response=lambda: empty)
            updater.update_link_established(link)
        results["after_failed_and_empty"] = mp.packb(transport.blackholed_identities).hex()
        incoming = {hashes[2]: {"source": remote, "until": None, "reason": "changed ignored"}}
        link.request = lambda path: SimpleNamespace(concluded=lambda: True, get_response=lambda: incoming)
        updater.update_link_established(link)
        results["after_conflict_file"] = (root / remote.hex()).read_bytes().hex()
        transport.blackholed_identities = {}
        transport.reload_blackhole()
        results["reloaded_publication"] = mp.packb(transport.blackholed_identities).hex()
        results["remove_remote"] = instance.unblackhole_identity(hashes[2])
        transport.blackholed_identities = {}
        transport.reload_blackhole()
        results["remote_restored_after_reload"] = instance.is_blackholed(hashes[2])
        results["absent_remove"] = instance.unblackhole_identity(b"\xff" * 16)
        results["request_paths"] = requests
        results["destination_hash"] = RNS.Destination.hash_from_name_and_identity("rnstransport.info.blackhole", remote).hex()
        captured = []
        with patch.object(RNS, "Packet", side_effect=lambda owner, payload, *args, **kwargs:
                          captured.append(payload) or SimpleNamespace(send=lambda: False)):
            RNS.Link.request(SimpleNamespace(mdu=431), "/list", timeout=25)
        results["request_payload"] = captured[0].hex()
    results["upstream"] = PINNED_RNS_SHA
    results["source_sha256"] = {
        name: hashlib.sha256(Path(importlib.import_module("RNS." + name).__file__).read_bytes()).hexdigest()
        for name in ["Reticulum", "Transport", "Discovery", "Identity", "Link"]
    }
    Path(args.output).write_text(json.dumps(results, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
