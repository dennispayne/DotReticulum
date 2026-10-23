"""Execute verified RNS 1.5.7 for IFAC and bitrate MTU vectors; no .NET output is consumed.

DOTRETICULUM_UPSTREAM and PYTHONPATH must select the authoritative checkout.
Uses actual Reticulum._add_interface for derivation, Transport IFAC codecs and Interface.optimise_mtu.
An in-memory Interface subclass and minimal Reticulum owner avoid opening hardware or starting a daemon.
Packet.MTU is explicitly raised for upgraded bearer-frame cases; this is not resource/channel interop.
"""
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference()
import RNS
from RNS.Interfaces.Interface import Interface

RNS.loglevel = RNS.LOG_CRITICAL


class MemoryInterface(Interface):
    AUTOCONFIGURE_MTU = True

    def __init__(self):
        # Interface's hardware-independent algorithms need only these fields. Base initialization
        # requires a running Reticulum singleton; this fixture intentionally starts no daemon.
        self.bitrate = 62500
        self.HW_MTU = None
        self.ifac_violations = 0

    def __str__(self):
        return "Parity4IfacVector"


def access_code(name, key, size):
    interface = MemoryInterface()
    owner = SimpleNamespace(
        is_connected_to_shared_instance=False,
        ifac_salt=RNS.Reticulum.IFAC_SALT,
        _default_gravity=lambda: 0,
    )
    RNS.Reticulum._add_interface(
        owner, interface, ifac_size=size, ifac_netname=name, ifac_netkey=key
    )
    return interface


def vector(name, key, size, length):
    interface = access_code(name, key, size)
    destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "parity4", "ifac")
    payload = bytes((i * 37 + 11) % 256 for i in range(length - 19))
    packet = RNS.Packet(destination, payload)
    packet.MTU = length
    packet.pack()
    raw = bytes(packet.raw)
    assert len(raw) == length
    frame = RNS.Transport.handle_outgoing_ifac(interface, raw)
    assert frame == RNS.Transport.handle_outgoing_ifac_legacy(interface, raw)
    valid, restored = RNS.Transport.handle_ifac(frame, interface)
    assert valid and restored == raw
    wrong = access_code(name, "different synthetic key", size)
    wrong_valid, _ = RNS.Transport.handle_ifac(frame, wrong)
    assert not wrong_valid
    wrong_network = access_code("different synthetic network", key, size)
    wrong_network_valid, _ = RNS.Transport.handle_ifac(frame, wrong_network)
    assert not wrong_network_valid
    tampered = bytearray(frame)
    tampered[-1] ^= 1
    tampered_valid, _ = RNS.Transport.handle_ifac(bytes(tampered), interface)
    assert not tampered_valid
    samples = [
        {"offset": offset, "hex": frame[offset:offset + 32].hex()}
        for offset in sorted({0, max(0, len(frame) - 32), *(
            x for x in [8128, 8159, 8160, 8191, 8192] if x < len(frame)
        )})
    ]
    return {
        "network_name": name, "passphrase": key, "size": size, "packet_length": length,
        "public_key": interface.ifac_identity.get_public_key().hex(),
        "signature": interface.ifac_signature.hex(), "raw_header": raw[:19].hex(),
        "frame": frame.hex() if length <= 500 else None,
        "frame_sha256": hashlib.sha256(frame).hexdigest(), "samples": samples,
        "wrong_key_valid": wrong_valid, "wrong_network_valid": wrong_network_valid, "tampered_valid": tampered_valid,
    }


root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
sources = [
    "RNS/Reticulum.py", "RNS/Transport.py", "RNS/Cryptography/HKDF.py",
    "RNS/Interfaces/Interface.py", "RNS/Interfaces/TCPInterface.py",
    "RNS/Interfaces/UDPInterface.py", "RNS/Interfaces/SerialInterface.py",
]
bitrates = [0, 5, 62499, 62500, 62501]
for threshold in [1000000, 2000000, 5000000, 10000000, 100000000, 200000000, 400000000, 750000000, 1000000000]:
    bitrates.extend([threshold - 1, threshold, threshold + 1])
mtus = []
for bitrate in bitrates:
    interface = MemoryInterface()
    interface.bitrate = bitrate
    interface.optimise_mtu()
    mtus.append({"bitrate": bitrate, "hardware_mtu": interface.HW_MTU})

result = {
    "upstream_sha": PINNED_RNS_SHA, "rns_version": RNS.__version__,
    "source_sha256": {path: hashlib.sha256((root / path).read_bytes()).hexdigest() for path in sources},
    "method": "Reticulum._add_interface + Transport.handle_outgoing_ifac/handle_ifac; Interface.optimise_mtu; real upstream Packet.pack with explicit upgraded Packet.MTU; in-memory Interface subclass initializes only bitrate/HW_MTU/ifac_violations to avoid singleton startup; no reference source changes",
    "vectors": [
        vector("parity4-test", None, 1, 20),
        vector(None, "synthetic test passphrase", 8, 500),
        vector("μ-network 🛰", "synthetic κλειδί", 16, 500),
        vector("parity4-test", "synthetic test passphrase", 64, 500),
        vector("parity4-test", "synthetic test passphrase", 16, 8144),
        vector("parity4-test", "synthetic test passphrase", 16, 8145),
        vector("parity4-test", "synthetic test passphrase", 16, 8177),
        vector("parity4-test", "synthetic test passphrase", 16, 16384),
    ],
    "bitrate_mtus": mtus,
}
output = Path(__file__).with_name("Fixtures") / "parity4-ifac-vectors.json"
output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {output}: {len(result['vectors'])} IFAC vectors, {len(mtus)} MTU thresholds, pin {PINNED_RNS_SHA}")
