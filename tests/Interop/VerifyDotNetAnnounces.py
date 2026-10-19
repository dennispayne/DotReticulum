"""Validates DotReticulum-created announces with the pinned upstream Reticulum implementation.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Usage:    python VerifyDotNetAnnounces.py dotnet-announces.json   (produced by ExportDotNetAnnounces.cs)
Exits non-zero on the first mismatch. Checks, per announce: upstream Identity.validate_announce accepts it and remembers the
identity, app_data and ratchet; a tampered copy is rejected; Identity.recall works; a token that .NET encrypted to the announced
identity/ratchet decrypts with the upstream Destination (ratchet-aware) decrypt path.
"""
import json
import sys

import RNS
from RNS.Identity import Identity
from reference_environment import verify_reference

verify_reference()

RNS.Transport.register_destination = staticmethod(lambda destination: None)
RNS.Identity._remember_ratchet = staticmethod(lambda destination_hash, ratchet: Identity.known_ratchets.__setitem__(destination_hash, ratchet))
real_stdout = sys.stdout
sys.stdout = sys.stderr  # RNS logs to stdout

data = json.load(open(sys.argv[1]))
sys.stderr.write(f"RNS from {RNS.__file__}\n")

for a in data["announces"]:
    label = a["label"]
    Identity.known_destinations = {}
    Identity.known_ratchets = {}
    dest_hash = bytes.fromhex(a["destination_hash"])
    app_data = bytes.fromhex(a["app_data"])

    packet = RNS.Packet(None, bytes.fromhex(a["raw"]))
    assert packet.unpack(), f"{label}: upstream could not unpack"
    assert packet.destination_hash == dest_hash, f"{label}: destination hash"
    assert packet.packet_type == RNS.Packet.ANNOUNCE
    assert packet.context_flag == (RNS.Packet.FLAG_SET if a["ratchet_private"] else RNS.Packet.FLAG_UNSET), f"{label}: context flag"
    assert Identity.validate_announce(packet), f"{label}: upstream rejected the .NET announce"

    entry = Identity.known_destinations[dest_hash]
    remembered = entry[3]
    if a["ratchet_private"] is None:
        assert (remembered or b"") == app_data, f"{label}: app_data"
        assert dest_hash not in Identity.known_ratchets
    else:
        prv = bytes.fromhex(a["ratchet_private"])
        assert (remembered or b"") == app_data, f"{label}: app_data (ratchet)"
        assert Identity.known_ratchets[dest_hash] == Identity._ratchet_public_bytes(prv), f"{label}: ratchet"

    recalled = Identity.recall(dest_hash, _no_use=True)
    assert recalled is not None and recalled.get_public_key() is not None, f"{label}: recall"

    bad = RNS.Packet(None, bytes.fromhex(a["tampered"]))
    assert bad.unpack()
    assert not Identity.validate_announce(bad), f"{label}: upstream accepted a tampered announce"

    # Decrypt what .NET encrypted to the announced (ratchet) key, through the upstream Destination decrypt path.
    owner = Identity.from_bytes(bytes.fromhex(a["identity_private"]))
    destination = RNS.Destination(owner, RNS.Destination.IN, RNS.Destination.SINGLE, a["app_name"], *a["aspects"])
    assert destination.hash == dest_hash, f"{label}: destination hash from name"
    if a["ratchet_private"]:
        destination.ratchets = [bytes.fromhex(a["ratchet_private"])]
        destination.enforce_ratchets = None
        plaintext = owner.decrypt(bytes.fromhex(a["token"]), ratchets=destination.ratchets, enforce_ratchets=True)
    else:
        plaintext = destination.decrypt(bytes.fromhex(a["token"]))
    assert plaintext == b"dotnet to python", f"{label}: token did not decrypt"

print(f"OK: upstream validated {len(data['announces'])} .NET announces, rejected their tampered copies, and decrypted tokens to the announced keys", file=real_stdout)
