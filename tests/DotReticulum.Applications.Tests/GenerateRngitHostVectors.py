"""Thin adapter executing pinned rngit config/statistics methods, without a network.

An uninitialised node supplies only the constructor's state; repository discovery
is a no-op callback (no Git repositories are scanned). Statistics _get_day is
injected with a fixed date; worker threads are joined before observations. Config,
recording and persistence methods are unmodified. These vectors do not establish
actual wall-clock/timezone or group-scan behavior.
The initial empty stats file is removed before persistence
because upstream os.rename cannot replace an existing destination on Windows;
this fixture does not prove upstream Windows restart/persistence behavior.
Single-repository vectors deliberately avoid the upstream
STATS_INIT_GROUP/STATS_INIT_REPO shared-mutable-default aliasing bug.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import threading
import time
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities.rngit import server
from RNS.vendor.configobj import ConfigObj
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    root = Path(args.work_directory).resolve()
    root.mkdir(parents=True, exist_ok=True)
    if any(root.iterdir()):
        raise RuntimeError("Reference work directory must be empty")
    RNS.loglevel = -1
    ignored = "0123456789abcdef0123456789abcdef"
    config_cases = [
        ("default", "\n".join(server.__default_rngit_config__)),
        ("empty", ""),
        ("configured", f"""[rngit]
node_name = 'A # node'
announce_interval = -2
mirror_interval = -5
record_stats = yes
stats_ignore_identities = alice, invalid
stats_push_ignore_identities = {ignored}
blocked_identities = alice,
[aliases]
alice = {ignored.upper()}
all = {ignored}
broken = invalid
[access]
public = r:all, w:alice
[pages]
serve_nomadnet = true
unicode_icons = yes
media_conversion = off
[logging]
loglevel = 99
"""),
    ]
    configurations = []
    for name, text in config_cases:
        node = server.ReticulumGitNode.__new__(server.ReticulumGitNode)
        node.config = ConfigObj(text.splitlines())
        node.identitypath = str(root / (name + "_identity"))
        node.identity_aliases = {}
        node.blocked_identities = {}
        node.stats_ignored = {}
        node.stats_push_ignored = {}
        node.node_name = "Anonymous Git Node"
        node.announce_interval = 0
        node.mirror_interval = 86400
        node.stats_enabled = False
        node._serve_nomadnet = False
        node.verbosity = 0
        node.load_repository_group = lambda *_: None
        RNS.loglevel = 4
        node._ReticulumGitNode__apply_config()
        configurations.append({
            "name": name, "config": text,
            "node_name": node.node_name, "announce_seconds": node.announce_interval,
            "mirror_seconds": node.mirror_interval, "record_stats": node.stats_enabled,
            "serve_nomadnet": node._serve_nomadnet, "loglevel": RNS.loglevel,
            "aliases": node.identity_aliases,
            "ignored": [k.hex() for k in node.stats_ignored],
            "push_ignored": [k.hex() for k in node.stats_push_ignored],
            "blocked": [k.hex() for k in node.blocked_identities],
        })

    node = server.ReticulumGitNode.__new__(server.ReticulumGitNode)
    node.stats_lock = threading.Lock()
    node.statspath = str(root / "stats")
    node.stats_enabled = True
    node.stats_ignored = {bytes.fromhex(ignored): True}
    node.stats_push_ignored = {bytes.fromhex(ignored): True}
    node._get_day = lambda: "2026-10-19"
    node._ReticulumGitNode__load_stats()
    peer = SimpleNamespace(hash=bytes.fromhex(ignored))
    operations = [
        ("view_succeeded", [None, None, None]),
        ("view_succeeded", ["group", None, None]),
        ("view_succeeded", ["group", "repo", None]),
        ("fetch_succeeded", ["group", "repo", None]),
        ("push_succeeded", ["group", "repo", None]),
        ("download_succeeded", ["group", "repo", None]),
        ("release_download_succeeded", ["group", "repo", None]),
    ]
    for method, arguments in operations:
        getattr(node, method)(*arguments)
    for method, _ in operations:
        getattr(node, method)("group", "repo", peer)
    for thread in threading.enumerate():
        if thread is not threading.current_thread() and thread.daemon:
            thread.join(timeout=5)
            if thread.is_alive():
                raise RuntimeError("Reference statistics worker did not finish")
    Path(node.statspath).unlink()
    node._ReticulumGitNode__persist_stats()
    if mp.unpackb(Path(node.statspath).read_bytes()) != node.stats:
        raise RuntimeError("Persisted reference statistics do not match")
    persisted = node.stats
    node._ReticulumGitNode__load_stats()
    if node.stats != persisted:
        raise RuntimeError("Reference statistics did not survive reload")
    # Only the permission boundary and clock are supplied by this harness.
    # This verifies aggregation, not authorization or real-timezone behavior.
    node.resolve_permission = lambda *_: True
    timestamp = time.mktime(time.strptime("2026-10-19 12:00:00", "%Y-%m-%d %H:%M:%S"))
    with patch.object(server.time, "time", return_value=timestamp):
        summary = node.repository_stats(None, "group", "repo", lookback_days=3)
        empty_summary = node.repository_stats(None, "unknown", "unknown", lookback_days=3)
    result = {
        "upstream": PINNED_RNS_SHA,
        "server_sha256": hashlib.sha256(Path(server.__file__).read_bytes()).hexdigest(),
        "configurations": configurations,
        "statistics": node.stats,
        "statistics_msgpack": Path(node.statspath).read_bytes().hex(),
        "summary_msgpack": mp.packb(summary).hex(),
        "empty_summary_msgpack": mp.packb(empty_summary).hex(),
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
