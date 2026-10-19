"""Generates Fixtures/announce-vectors.json by EXECUTING the pinned upstream Reticulum announce implementation.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
          (RNS/Destination.py announce(), RNS/Identity.py validate_announce(), RNS/Transport.py)
Run:      PYTHONPATH=<upstream checkout> python GenerateAnnounceVectors.py > Fixtures/announce-vectors.json

Method: real RNS.Destination objects (fixed identity keys) create announces through Destination.announce(send=False)
(plain, with app_data, with ratchets, path response), packed with Packet.pack(). Every announce, and a set of tampered
and truncated variants, is then fed to upstream Identity.validate_announce() (full, and signature-only) and the
upstream verdict is what the vectors record. Nothing is computed by DotReticulum.
"""
import hashlib
import json
import os
import shutil
import sys

import RNS
from RNS.Identity import Identity

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"

RNS.Transport.register_destination = staticmethod(lambda destination: None)
# Ratchet remembering would persist under the (nonexistent) Reticulum storage path; keep it in memory only.
RNS.Identity._remember_ratchet = staticmethod(lambda destination_hash, ratchet: Identity.known_ratchets.__setitem__(destination_hash, ratchet))


def identity(i):
    prv = hashlib.sha256(f"ann-x-{i}".encode()).digest() + hashlib.sha256(f"ann-ed-{i}".encode()).digest()
    return Identity.from_bytes(prv)


def pack(packet):
    packet.pack()
    return bytes(packet.raw)


def validate(raw, only_signature=False):
    Identity.known_destinations = {}
    Identity.known_ratchets = {}
    packet = RNS.Packet(None, raw)
    if not packet.unpack():
        return {"valid": False, "known": False, "ratchet": None, "app_data": None}
    result = Identity.validate_announce(packet, only_validate_signature=only_signature)
    entry = Identity.known_destinations.get(packet.destination_hash)
    ratchet = Identity.known_ratchets.get(packet.destination_hash)
    return {
        "valid": bool(result),
        "known": entry is not None,
        "ratchet": ratchet.hex() if ratchet else None,
        "app_data": None if entry is None or entry[3] is None else entry[3].hex(),
    }


def flip(raw, index):
    b = bytearray(raw)
    b[index] ^= 0x01
    return bytes(b)


def main():
    sys.stderr.write(f"RNS from {RNS.__file__}; pinned {PINNED_SHA}\n")
    D = RNS.Destination
    scratch = os.path.join(os.getcwd(), ".announce-gen-ratchets")
    os.makedirs(scratch, exist_ok=True)
    out = {"upstream_sha": PINNED_SHA, "announces": [], "tampered": []}

    cases = [
        ("plain", 0, None, False, False),
        ("app_data", 1, b"hello reticulum", False, False),
        ("ratchet", 2, None, True, False),
        ("ratchet_app_data", 3, b"ratchet+app", True, False),
        ("path_response", 4, b"pr", False, True),
        ("ratchet_path_response", 5, b"", True, True),
    ]
    for label, i, app_data, ratchets, path_response in cases:
        ident = identity(i)
        d = D(ident, D.IN, D.SINGLE, "dotreticulum", "vectors", f"announce{i}")
        if ratchets:
            d.enable_ratchets(os.path.join(scratch, f"r{i}"))
        packet = d.announce(app_data=app_data, path_response=path_response, send=False)
        raw = pack(packet)
        result = validate(raw)
        sig_only = validate(raw, only_signature=True)
        entry = {
            "label": label,
            "identity_private": ident.get_private_key().hex(),
            "identity_public": ident.get_public_key().hex(),
            "app_name": "dotreticulum", "aspects": ["vectors", f"announce{i}"],
            "destination_hash": d.hash.hex(), "name_hash": d.name_hash.hex(),
            "app_data": None if app_data is None else app_data.hex(),
            "ratchet": result["ratchet"],
            "ratchet_private": d.ratchets[0].hex() if ratchets else None,
            "context_flag": bool(packet.context_flag), "context": packet.context,
            "raw": raw.hex(),
            "upstream_valid": result["valid"], "upstream_signature_valid": sig_only["valid"],
            "upstream_recalled_app_data": result["app_data"],
        }
        out["announces"].append(entry)

        # Tampered variants: the offsets are positions in the raw packet (header 1 = 19 bytes incl. context byte).
        payload = 19
        names = {
            "dest_hash": 2, "public_key_x": payload, "public_key_ed": payload + 40, "name_hash": payload + 64,
            "random_hash": payload + 74, "signature": len(raw) - 1 - (len(app_data) if app_data else 0),
        }
        if app_data:
            names["app_data"] = len(raw) - 1
        if ratchets:
            names["ratchet"] = payload + 84
        for tname, idx in names.items():
            tampered = flip(raw, idx)
            v = validate(tampered)
            s = validate(tampered, only_signature=True)
            out["tampered"].append({"base": label, "what": tname, "raw": tampered.hex(),
                                    "upstream_valid": v["valid"], "upstream_signature_valid": s["valid"]})
        flag = bytearray(raw)
        flag[0] ^= 0x20  # toggle the context flag: the payload is then split differently
        v = validate(bytes(flag))
        out["tampered"].append({"base": label, "what": "context_flag", "raw": bytes(flag).hex(),
                                "upstream_valid": v["valid"], "upstream_signature_valid": validate(bytes(flag), True)["valid"]})
        for cut in (len(raw) - 1, payload + 64 + 10 + 10 + 63, payload + 64 + 10, payload + 10, payload):
            if cut <= 0 or cut >= len(raw):
                continue
            trunc = raw[:cut]
            v = validate(trunc)
            out["tampered"].append({"base": label, "what": f"truncated_{cut}", "raw": trunc.hex(),
                                    "upstream_valid": v["valid"], "upstream_signature_valid": validate(trunc, True)["valid"]})

    # Destination hash mismatch with a VALID signature: a different identity signs for another identity's hash.
    ident = identity(9)
    other = identity(10)
    d = D(ident, D.IN, D.SINGLE, "dotreticulum", "vectors", "mismatch")
    wrong_hash = D(other, D.IN, D.SINGLE, "dotreticulum", "vectors", "mismatch").hash
    random_hash = os.urandom(5) + int(1700000000).to_bytes(5, "big")
    signed = wrong_hash + ident.get_public_key() + d.name_hash + random_hash
    payload = ident.get_public_key() + d.name_hash + random_hash + ident.sign(signed)
    pkt = RNS.Packet(d, payload, RNS.Packet.ANNOUNCE)
    pkt.destination.hash = wrong_hash
    pkt.pack()
    raw = bytes(pkt.raw)
    v = validate(raw)
    out["tampered"].append({"base": "mismatch", "what": "destination_hash_mismatch_valid_signature", "raw": raw.hex(),
                            "upstream_valid": v["valid"], "upstream_signature_valid": validate(raw, True)["valid"]})

    shutil.rmtree(scratch, ignore_errors=True)
    json.dump(out, real_stdout, indent=1)


real_stdout = sys.stdout
sys.stdout = sys.stderr  # RNS logs to stdout; keep it away from the JSON
main()
