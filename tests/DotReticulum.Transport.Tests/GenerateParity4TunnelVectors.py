"""Observe the pinned reference tunnel wire and restoration decisions."""
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import types

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference(require_lxmf=True)
import RNS


def main():
    RNS.loglevel = RNS.LOG_CRITICAL
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(range(64)))
    RNS.Transport.identity = identity
    interface_hash = hashlib.sha256(b"Parity4 tunnel interface").digest()
    interface = types.SimpleNamespace(get_hash=lambda: interface_hash, wants_tunnel=True)
    packets = []
    old_send = RNS.Packet.send
    old_random = RNS.Identity.get_random_hash
    old_time = time.time
    try:
        def capture(packet):
            packet.pack()
            packets.append(packet.raw.hex())

        RNS.Packet.send = capture
        RNS.Identity.get_random_hash = staticmethod(lambda: bytes(range(16)))
        time.time = lambda: 1800000000.25
        RNS.Transport.synthesize_tunnel(interface)
        tunnel_id = hashlib.sha256(identity.get_public_key() + interface_hash).digest()
        destination = bytes(range(16))
        next_hop = bytes(range(16, 32))

        def path(hops, expires, emitted):
            blob = b"\x01\x02\x03\x04\x05" + emitted.to_bytes(5, "big")
            return [1800000000.0, next_hop, hops, expires, [blob], None, bytes(32)]

        cases = []
        for label, existing, saved in [
            ("absent_valid", None, path(3, 1800001000, 100)),
            ("absent_expired", None, path(3, 1799999999, 100)),
            ("shorter_current", path(2, 1800001000, 90), path(3, 1800001000, 100)),
            ("same_hops_newer_tunnel", path(3, 1800001000, 90), path(3, 1800001000, 100)),
            ("same_hops_older_tunnel", path(3, 1800001000, 110), path(3, 1800001000, 100)),
            ("expired_current", path(2, 1799999999, 90), path(3, 1800001000, 100)),
        ]:
            RNS.Transport.path_table = {} if existing is None else {destination: existing}
            RNS.Transport.tunnels = {tunnel_id: [tunnel_id, None, {destination: saved}, 1800000100]}
            RNS.Transport.handle_tunnel(tunnel_id, interface)
            restored = RNS.Transport.path_table.get(destination)
            cases.append({
                "name": label, "restored": restored is not None and restored[5] is interface,
                "retained": destination in RNS.Transport.tunnels[tunnel_id][2],
                "tunnel_expires": RNS.Transport.tunnels[tunnel_id][3],
            })
        root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
        result = {
            "revision": PINNED_RNS_SHA, "version": RNS.__version__,
            "sources_sha256": {"Transport.py": hashlib.sha256((root / "RNS" / "Transport.py").read_bytes()).hexdigest()},
            "private_key": bytes(range(64)).hex(), "interface_hash": interface_hash.hex(),
            "random_hash": bytes(range(16)).hex(), "tunnel_id": tunnel_id.hex(),
            "synthesis_packet": packets[0], "wants_tunnel_after_send": interface.wants_tunnel,
            "timeout": RNS.Transport.TUNNEL_TIMEOUT, "path_timeout": RNS.Transport.TUNNEL_PATH_TIMEOUT,
            "restoration": cases,
            "harness": "Frozen clock/random and Packet.send capture; synthetic interface/path tables, unmodified reference methods.",
        }
        output = Path(__file__).with_name("Fixtures") / "parity4-tunnels.json"
        output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(output)
    finally:
        RNS.Packet.send = old_send
        RNS.Identity.get_random_hash = old_random
        time.time = old_time


if __name__ == "__main__":
    main()
