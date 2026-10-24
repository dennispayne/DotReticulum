"""Execute pinned RequestReceipt and small-response size contracts.

Only clock and packet receipt I/O are adapters. No node or live-wire claim.
Run using the isolated reference venv with DOTRETICULUM_UPSTREAM/PYTHONPATH set.
"""
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.vendor import umsgpack

verify_reference(require_lxmf=True)
module = sys.modules["RNS.Link"]
events = []


class PacketReceipt:
    def __init__(self):
        self.truncated_hash = bytes(range(16))
        self.status = RNS.PacketReceipt.SENT
        self.proved = False
        self.callbacks = SimpleNamespace(delivery=lambda _: events.append("delivered"))

    def set_timeout_callback(self, callback):
        self.timeout_callback = callback


packet = PacketReceipt()
link = SimpleNamespace(pending_requests=[])
receipt = module.RequestReceipt(
    link, packet_receipt=packet, timeout=30, request_size=23,
    max_response_size=100,
    progress_callback=lambda r: events.append(f"progress:{r.progress:g}"),
    response_callback=lambda _: events.append("response"),
    failed_callback=lambda _: events.append("failed"))
receipt.response_received(b"answer")
success = {"events": list(events), "status": receipt.status,
           "progress": receipt.progress, "packetStatus": packet.status,
           "proved": packet.proved, "response": receipt.response.hex()}
events.clear()
rejected = module.RequestReceipt(link, packet_receipt=PacketReceipt(), timeout=30,
                                failed_callback=lambda _: events.append("failed"))
rejected.response_rejected()
rejected.response_rejected()
clock = module.time.time
try:
    module.time.time = lambda: 1007.0
    timed_link = object.__new__(module.Link)
    timed_link.activated_at = 1000.0
    timed_link.last_inbound = 1000.0
    timed_link.last_outbound = 1005.0
    timed_link.last_data = 1003.0
    timed_link.establishment_rate = 204 / 0.25
    timing = {
        "age": timed_link.get_age(),
        "noInboundFor": timed_link.no_inbound_for(),
        "noOutboundFor": timed_link.no_outbound_for(),
        "noDataFor": timed_link.no_data_for(),
        "inactiveFor": timed_link.inactive_for(),
        "establishmentRate": timed_link.get_establishment_rate(),
    }
finally:
    module.time.time = clock
root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
result = {
    "pinned": PINNED_RNS_SHA,
    "rnsFile": RNS.__file__,
    "sourceSha256": {"Link.py": hashlib.sha256((root / "RNS" / "Link.py").read_bytes()).hexdigest()},
    "success": success,
    "rejection": {"events": list(events), "status": rejected.status},
    "timing": timing,
    "smallResponseSizes": [
        {"responseHex": umsgpack.packb(value).hex(), "size": len(umsgpack.packb(value)) - 2}
        for value in (None, 0, True, "", "a", "abc", b"", b"abc", list(range(100)))
    ],
}
output = Path(__file__).with_name("Fixtures") / "parity5-request-vectors.json"
output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(output)
