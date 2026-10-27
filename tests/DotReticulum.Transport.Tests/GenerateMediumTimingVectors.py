"""Execute pinned RNS timing APIs with fake interface metadata, without networking."""
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference()
import RNS

root = Path(RNS.__file__).resolve().parent
cases = []
for name, interfaces in [
    ("empty", []),
    ("unknown", [(0, True)]),
    ("offline", [(1200, False)]),
    ("minimum-clamp", [(1, True)]),
    ("minimum", [(5, True)]),
    ("radio", [(1200, True)]),
    ("mixed", [(10000000, True), (1200, True), (5, False), (0, True)]),
]:
    RNS.Transport.interfaces = [SimpleNamespace(bitrate=rate, online=online) for rate, online in interfaces]
    RNS.Transport.lowest_interface_bitrate = None
    RNS.Transport.prioritize_interfaces()
    cases.append({
        "name": name,
        "interfaces": [{"bitrate": rate, "online": online} for rate, online in interfaces],
        "lowest": RNS.Transport.lowest_interface_bitrate,
        "medium": RNS.Transport.medium_path_timeout(),
    })

first_hops = []
original = RNS.Transport.next_hop_per_byte_latency
try:
    for bitrate in [None, 5, 1200, 10000000]:
        RNS.Transport.next_hop_per_byte_latency = staticmethod(
            lambda destination, rate=bitrate: None if rate is None else 8 / rate)
        first_hops.append({"bitrate": bitrate, "timeout": RNS.Transport.first_hop_timeout(bytes(16))})
finally:
    RNS.Transport.next_hop_per_byte_latency = original

output = {
    "upstream": PINNED_RNS_SHA,
    "sourceSha256": {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                     for name in ["Reticulum.py", "Transport.py"]},
    "cases": cases,
    "firstHops": first_hops,
}
Path(__file__).with_name("Fixtures").joinpath("medium-timing-vectors.json").write_text(
    json.dumps(output, indent=2) + "\n", encoding="utf-8")
