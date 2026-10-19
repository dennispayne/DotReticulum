"""Generates Fixtures/destination-vectors.json by EXECUTING the pinned upstream Reticulum Destination implementation.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b (RNS/Destination.py)
Run:      PYTHONPATH=<upstream checkout> python GenerateDestinationVectors.py > Fixtures/destination-vectors.json

Method: real RNS.Destination objects are constructed (Transport registration is stubbed out because no Reticulum
instance is running) and Destination.expand_name / hash / hash_from_name_and_identity plus the instance's name,
name_hash and hash attributes are recorded. Identities are fixed private keys so the vectors are deterministic.
"""
import hashlib
import json
import sys

import RNS
from RNS.Identity import Identity

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"

RNS.Transport.register_destination = staticmethod(lambda destination: None)


def identity(i):
    prv = hashlib.sha256(f"dest-x-{i}".encode()).digest() + hashlib.sha256(f"dest-ed-{i}".encode()).digest()
    return Identity.from_bytes(prv)


NAMES = [
    ("lxmf", ["delivery"]),
    ("lxmf", ["propagation"]),
    ("nomadnetwork", ["node"]),
    ("example_utilities", ["announcesample", "fruits"]),
    ("a", []),
    ("app", ["a", "b", "c", "d"]),
    ("ünïcode", ["æspect"]),
    ("", ["x"]),
]


def main():
    sys.stderr.write(f"RNS from {RNS.__file__}; pinned {PINNED_SHA}\n")
    out = {"upstream_sha": PINNED_SHA, "single": [], "plain": [], "group": [], "hash_from_name": [], "invalid": []}
    D = RNS.Destination

    for i, (app, aspects) in enumerate(NAMES):
        ident = identity(i)
        d = D(ident, D.IN, D.SINGLE, app, *aspects)
        out["single"].append({
            "app_name": app, "aspects": aspects, "identity_private": ident.get_private_key().hex(),
            "identity_hash": ident.hash.hex(), "full_name": d.name, "name_hash": d.name_hash.hex(),
            "hash": d.hash.hex(), "type": d.type,
            "expand_name_no_identity": D.expand_name(None, app, *aspects),
            "static_hash_identity_object": D.hash(ident, app, *aspects).hex(),
            "static_hash_identity_bytes": D.hash(ident.hash, app, *aspects).hex(),
            "static_hash_no_identity": D.hash(None, app, *aspects).hex(),
        })
        p = D(None, D.IN, D.PLAIN, app, *aspects)
        out["plain"].append({
            "app_name": app, "aspects": aspects, "full_name": p.name, "name_hash": p.name_hash.hex(),
            "hash": p.hash.hex(), "type": p.type,
        })
        full = ".".join([app] + aspects)
        out["hash_from_name"].append({
            "full_name": full, "identity_hash": ident.hash.hex(),
            "hash": D.hash_from_name_and_identity(full, ident).hex(),
            "hash_bytes": D.hash_from_name_and_identity(full, ident.hash).hex(),
        })

    # GROUP destinations with an explicit identity-less OUT direction are rejected upstream; record the explicit-identity form.
    ident = identity(100)
    g = D(ident, D.IN, D.GROUP, "grp", "chat")
    out["group"].append({
        "app_name": "grp", "aspects": ["chat"], "identity_hash": ident.hash.hex(), "full_name": g.name,
        "name_hash": g.name_hash.hex(), "hash": g.hash.hex(), "type": g.type,
    })

    def rejected(label, fn):
        try:
            fn()
            out["invalid"].append({"case": label, "upstream_rejects": False})
        except Exception as e:
            out["invalid"].append({"case": label, "upstream_rejects": True, "error": type(e).__name__})

    rejected("dot_in_app_name", lambda: D(None, D.IN, D.PLAIN, "a.b", "c"))
    rejected("dot_in_aspect", lambda: D(None, D.IN, D.PLAIN, "a", "b.c"))
    rejected("expand_name_dot_in_aspect", lambda: D.expand_name(None, "a", "b.c"))
    rejected("plain_with_identity", lambda: D(identity(0), D.IN, D.PLAIN, "a"))
    rejected("outbound_single_without_identity", lambda: D(None, D.OUT, D.SINGLE, "a"))

    json.dump(out, sys.stdout, indent=1)
    print()


if __name__ == "__main__":
    main()
