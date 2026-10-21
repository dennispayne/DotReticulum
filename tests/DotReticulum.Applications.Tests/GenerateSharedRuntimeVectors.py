"""Execute pinned upstream MessagePack/HDLC and Python multiprocessing framing/authentication.

No protocol is reconstructed here. Python stdlib authentication provenance is
recorded separately because it is determined by the running Python version.
"""
import argparse
import hashlib
import inspect
import json
from pathlib import Path
import sys
import multiprocessing.connection as connection

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.vendor import umsgpack as mp
from RNS.Interfaces.util.HDLC import HDLC


class Capture:
    def __init__(self):
        self.data = bytearray()

    def _send(self, data):
        self.data.extend(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    root = Path(RNS.__file__).parent
    key = bytes(range(32))
    calls = [
        {"get": "path_table", "max_hops": None},
        {"get": "interface_stats"},
        {"drop": "path", "destination_hash": bytes(range(16))},
        {"manage": "detach_interface", "name": "Local"},
        {"destination_data": "used", "destination_hash": bytes(range(16))},
        {"identity_data": "retain", "identity_hash": bytes(range(16))},
        {"blackhole_identity": bytes(range(16)), "until": 1900000000.5, "reason": "test"},
    ]
    values = calls + [
        {bytes(range(16)): [None, True, False, -1, 255, 65536, 1.25, "μ"]},
        {"interfaces": [], "transport_id": bytes(range(16))},
        2**64 - 1, -2**63,
    ]
    vectors = []
    for value in values:
        packed = mp.packb(value)
        capture = Capture()
        connection.Connection._send_bytes(capture, packed)
        vectors.append({"packed": packed.hex(), "framed": bytes(capture.data).hex()})
    challenge = bytes(range(20))
    prefixed = b"{sha256}" + bytes(range(40))
    output = {
        "upstream": PINNED_RNS_SHA,
        "version": RNS.__version__,
        "rns_module": str(Path(RNS.__file__).resolve()),
        "source_sha256": {
            str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in [root / "Reticulum.py", root / "Interfaces" / "LocalInterface.py",
                         root / "Interfaces" / "util" / "HDLC.py", root / "vendor" / "umsgpack.py"]
        },
        "connection_sha256": hashlib.sha256(Path(connection.__file__).read_bytes()).hexdigest(),
        "python": sys.version,
        "key": key.hex(),
        "challenge": challenge.hex(),
        "legacy_response": connection._create_response(key, challenge).hex(),
        "modern_challenge": prefixed.hex(),
        "modern_response": connection._create_response(key, prefixed).hex(),
        "values": vectors,
        "hdlc_packet": (b"\x00\x00" + bytes(range(16)) + b"\x00\x7e\x7d").hex(),
    }
    packet = bytes.fromhex(output["hdlc_packet"])
    output["hdlc_framed"] = (bytes([HDLC.FLAG]) + HDLC.escape(packet) + bytes([HDLC.FLAG])).hex()
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
