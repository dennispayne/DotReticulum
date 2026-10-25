"""Capture CLI help from the pinned upstream utilities for parity tests."""
import hashlib
import io
import json
import os
from pathlib import Path
from types import SimpleNamespace
import subprocess
import sys
from contextlib import redirect_stdout

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
from reference_environment import verify_reference

verify_reference(require_lxmf=True)
upstream = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve(strict=True)
expected_revision = "a29bfb51c468945c96a1ee22cae0114e993e2391"
tools = {}
for name in ("rnstatus", "rnpath", "rnprobe"):
    source = upstream / "RNS" / "Utilities" / f"{name}.py"
    result = subprocess.run(
        [sys.executable, str(source), "--help"],
        cwd=upstream,
        env={**os.environ, "PYTHONPATH": str(upstream)},
        check=True,
        capture_output=True,
        text=True,
        timeout=20,
    )
    tools[name] = {
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "help": result.stdout,
    }

probe_source = upstream / "RNS" / "Utilities" / "rnprobe.py"
probe_without_hash = subprocess.run(
    [sys.executable, str(probe_source)],
    cwd=upstream,
    env={**os.environ, "PYTHONPATH": str(upstream)},
    check=False,
    capture_output=True,
    text=True,
    timeout=20,
)
if probe_without_hash.returncode != 0:
    raise RuntimeError(f"Pinned rnprobe without arguments failed: {probe_without_hash.stderr}")

from RNS.Utilities import rnpath

path_rows = [{
    "hash": bytes(range(16)),
    "timestamp": 1_700_000_000.0,
    "via": bytes(range(16, 32)),
    "hops": 2,
    "expires": 1_700_003_600.0,
    "interface": "test-interface",
}]
rate_rows = [{
    "hash": bytes(range(16, 32)),
    "last": 1_700_000_000.0,
    "rate_violations": 2,
    "blocked_until": 1_700_000_123.0,
    "timestamps": [1_699_996_400.0, 1_700_000_000.0],
}]


class FixtureReticulum:
    def __init__(self, configdir, loglevel):
        pass

    def get_path_table(self, max_hops=None):
        return path_rows

    def get_rate_table(self):
        return rate_rows


rnpath.RNS.Reticulum = FixtureReticulum


def reference_json(table, rates):
    captured = io.StringIO()
    try:
        with redirect_stdout(captured):
            rnpath.program_setup(
                configdir=None, table=table, rates=rates, drop=False,
                destination_hexhash=None, verbosity=0, timeout=15,
                drop_queues=False, drop_via=False, max_hops=None,
                no_output=True, json=True,
            )
    except SystemExit as exit_code:
        if exit_code.code not in (None, 0):
            raise
    return json.loads(captured.getvalue())

blackhole_hash = bytes(range(16))
blackhole_source = bytes([0xA0] * 16)
blackhole_reason = "pinned blackhole output"
blackhole_values = {
    blackhole_hash: {"until": None, "reason": blackhole_reason, "source": blackhole_source},
}
FixtureReticulum.get_blackholed_identities = lambda self: blackhole_values
original_transport_identity = rnpath.RNS.Transport.identity
rnpath.RNS.Transport.identity = SimpleNamespace(hash=bytes([0xFF] * 16))
blackhole_capture = io.StringIO()
try:
    with redirect_stdout(blackhole_capture):
        rnpath.program_setup(
            configdir=None, table=False, rates=False, drop=False, destination_hexhash=None,
            verbosity=0, timeout=15, drop_queues=False, drop_via=False, max_hops=None,
            blackholed=True, no_output=True, json=True,
        )
finally:
    rnpath.RNS.Transport.identity = original_transport_identity

target = Path(__file__).parent / "Fixtures" / "parity7-network-utilities.json"
target.write_text(json.dumps({
    "revision": expected_revision,
    "tools": tools,
    "rnpath_reference_json": {
        "path_rows": [{
            "hash": row["hash"].hex(),
            "timestamp": row["timestamp"],
            "via": row["via"].hex(),
            "hops": row["hops"],
            "expires": row["expires"],
            "interface": row["interface"],
        } for row in path_rows],
        "path_output": reference_json(table=True, rates=False),
        "rate_rows": [{
            "hash": row["hash"].hex(),
            "last": row["last"],
            "rate_violations": row["rate_violations"],
            "blocked_until": row["blocked_until"],
            "timestamps": row["timestamps"],
        } for row in rate_rows],
        "rate_output": reference_json(table=False, rates=True),
    },
    "rnpath_blackhole_json_branch": {
        "hash": blackhole_hash.hex(),
        "source": blackhole_source.hex(),
        "reason": blackhole_reason,
        "output": blackhole_capture.getvalue().rstrip("\r\n"),
    },
    "rnprobe_no_hash_contract": {
        "exit_code": probe_without_hash.returncode,
        "has_usage": "usage: rnprobe.py" in probe_without_hash.stdout,
        "has_full_name": "full_name" in probe_without_hash.stdout,
        "has_destination_hash": "destination_hash" in probe_without_hash.stdout,
    },
}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Captured pinned RNS 1.5.7 CLI/help, rnpath JSON/blackhole output, and rnprobe no-hash contract for {', '.join(tools)}")
