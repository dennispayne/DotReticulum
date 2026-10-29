"""Execute the pinned maintenance reader for a device's empty config-sector reply."""
import hashlib
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference()
from RNS.Utilities import rnodeconf as reference


class Serial:
    def __init__(self):
        self.request = None
        self.inbound = b""

    def write(self, data):
        self.request = data
        return len(data)

    @property
    def is_open(self):
        return bool(self.inbound)

    @property
    def in_waiting(self):
        return len(self.inbound)

    def read(self, size):
        result, self.inbound = self.inbound[:size], self.inbound[size:]
        return result


serial = Serial()
node = reference.RNode(serial)
reference.sleep = lambda _: None  # Only hardware pacing; reader/serializer are unchanged.
node.download_cfg_sector()
frame = serial.request[:2] + serial.request[-1:]
serial.inbound = frame
node.readLoop()
assert node.cfg_sector == b""
source = Path(reference.__file__)
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
    "adapter": "Capture download_cfg_sector request; remove request payload for an empty reply; execute original readLoop with owned serial bytes; suppress hardware pacing only.",
    "empty_sector": {"frame": frame.hex(), "decoded": node.cfg_sector.hex()},
}
target = Path(__file__).parent / "Fixtures" / "rnodeconf-configuration.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(f"Verified RNS import: {reference.__file__}")
print(f"Wrote {target}")
