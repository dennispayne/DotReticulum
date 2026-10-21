"""Execute pinned RNS persistence APIs; only reference execution and observation live in Python."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import threading
import types

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference(require_lxmf=True)
import RNS
import RNS.vendor.umsgpack as mp
import time


def main():
    project = Path(__file__).resolve().parents[2]
    scratch = project / ".runtime-state-vector-work"
    scratch.mkdir(exist_ok=False)
    previous_time = time.time
    clock = [1800000000.125]
    time.time = lambda: clock[0]
    try:
        RNS.loglevel = RNS.LOG_CRITICAL
        RNS.Reticulum.storagepath = str(scratch)
        RNS.Reticulum.transport_enabled = staticmethod(lambda: True)
        RNS.Transport.owner = types.SimpleNamespace(is_connected_to_shared_instance=False)
        RNS.Identity.known_destinations = {}
        RNS.Identity.known_ratchets = {}
        identity = RNS.Identity(create_keys=False)
        private = bytes(range(64))
        identity.load_private_key(private)
        destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "persist", "test")
        destination_hash = destination.hash
        packet_hash = hashlib.sha256(b"upstream persisted announce").digest()
        RNS.Identity.remember(packet_hash, destination_hash, identity.get_public_key(), b"fixture")
        stages = []

        def observe(label):
            RNS.Identity.save_known_destinations()
            stages.append({"label": label, "hex": (scratch / "known_destinations").read_bytes().hex()})

        observe("remember")
        use_result = RNS.Identity._used_destination_data(destination_hash)
        observe("used")
        retain_result = RNS.Identity._retain_destination_data(destination_hash)
        observe("retained")
        retained_use_result = RNS.Identity._used_destination_data(destination_hash)
        clock[0] += 10.5
        RNS.Identity.remember(packet_hash, destination_hash, identity.get_public_key(), b"again")
        observe("retained_reannounce")
        RNS.Identity._unretain_destination_data(destination_hash)
        observe("unretained")

        interface_hash = hashlib.sha256(b"fixture interface").digest()
        interface = types.SimpleNamespace(get_hash=lambda: interface_hash)
        RNS.Transport.interfaces = [interface]
        next_hop = bytes(range(16, 32))
        random_blobs = [bytes(range(10)), bytes(range(10, 20))]
        RNS.Transport.path_table = {destination_hash: [
            clock[0], next_hop, 2, clock[0] + 604800, random_blobs, interface, packet_hash]}
        RNS.Transport.save_path_table()
        path_bytes = (scratch / "destination_table").read_bytes()
        tunnel_id = hashlib.sha256(b"fixture tunnel").digest()
        RNS.Transport.tunnels = {tunnel_id: [
            tunnel_id, interface, RNS.Transport.path_table.copy(), clock[0] + 604800]}
        RNS.Transport.save_tunnel_table()
        tunnel_bytes = (scratch / "tunnels").read_bytes()
        RNS.Transport.packet_hashlist = {packet_hash}
        RNS.Transport.save_packet_hashlist()

        destination.ratchets = [bytes(range(32)), bytes(range(32, 64))]
        destination.ratchets_path = str(scratch / "local_ratchets")
        destination.ratchet_file_lock = threading.Lock()
        destination._persist_ratchets()
        ratchet_bytes = (scratch / "local_ratchets").read_bytes()

        RNS.Transport.has_path = staticmethod(lambda _: False)
        RNS.Identity.known_destinations = {}
        RNS.Identity.remember(packet_hash, destination_hash, identity.get_public_key())
        RNS.Identity.clean_known_destinations()
        fresh_never_used_survives = destination_hash in RNS.Identity.known_destinations

        root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
        sources = {
            name: hashlib.sha256((root / "RNS" / name).read_bytes()).hexdigest()
            for name in ["Identity.py", "Destination.py", "Reticulum.py", "Transport.py"]
        }
        result = {
            "revision": PINNED_RNS_SHA, "version": RNS.__version__, "import": str(Path(RNS.__file__).resolve()),
            "sources_sha256": sources, "identity_private": private.hex(), "identity_public": identity.get_public_key().hex(),
            "destination_hash": destination_hash.hex(), "packet_hash": packet_hash.hex(),
            "use_result": use_result, "retain_result": retain_result, "retained_use_result": retained_use_result,
            "known_destinations": stages, "paths": path_bytes.hex(), "tunnels": tunnel_bytes.hex(),
            "packet_hashlist": (scratch / "packet_hashlist.raw").read_bytes().hex(),
            "local_ratchets": ratchet_bytes.hex(), "fresh_never_used_survives": fresh_never_used_survives,
            "harness": "Frozen clock, synthetic active interface and Transport.owner; no reference source modifications or network."
        }
        output = Path(__file__).with_name("Fixtures") / "runtime-state-vectors.json"
        output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(output)
    finally:
        time.time = previous_time
        shutil.rmtree(scratch)


if __name__ == "__main__":
    main()
