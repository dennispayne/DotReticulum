"""Execute hosted pinned RNS discovery and LXMF stamp APIs, with no wire shims.

The deterministic search chooses inputs only; stamp validation/workblock generation
and receiver observations execute the actual reference implementation.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference
import RNS
import RNS.Discovery as discovery
from LXMF import LXStamper
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    RNS.loglevel = -1
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(range(64)))
    info = {0: "BackboneInterface", 1: True, 254: identity.hash,
            253: "RNS", 252: RNS.__version__, 255: "Reference TCP",
            3: 51.5, 4: -0.1, 5: None, 2: "reference.example", 6: 4242,
            7: "public", 8: "test-only", 240: bytes(range(16))}
    packed = mp.packb(info)
    block = LXStamper.stamp_workblock(RNS.Identity.full_hash(packed), expand_rounds=20)
    n = 0
    while True:
        stamp = n.to_bytes(32, "big")
        if LXStamper.stamp_valid(stamp, 8, block):
            break
        n += 1
    plain = bytes([0]) + packed + stamp
    encrypted = bytes([2]) + identity.encrypt(packed + stamp)
    observations = []
    original_sources = RNS.Reticulum.interface_discovery_sources
    original_hops = RNS.Transport.hops_to
    RNS.Reticulum.interface_discovery_sources = staticmethod(lambda: [])
    RNS.Transport.hops_to = staticmethod(lambda _: 2)
    try:
        handler = discovery.InterfaceAnnounceHandler(required_value=8, callback=observations.append)
        handler.received_announce(bytes(16), identity, plain)
    finally:
        RNS.Reticulum.interface_discovery_sources = original_sources
        RNS.Transport.hops_to = original_hops
    assert len(observations) == 1
    receiver_cases = []
    old_network_identity = RNS.Transport.network_identity
    RNS.Transport.hops_to = staticmethod(lambda _: 2)
    RNS.Transport.network_identity = identity
    try:
        for label, payload, required, sources in [
            ("plain", plain, 8, []),
            ("encrypted", encrypted, 8, []),
            ("insufficient", plain, LXStamper.stamp_value(block, stamp) + 1, []),
            ("authorized", plain, 8, [identity.hash]),
            ("unauthorized", plain, 8, [bytes(16)]),
            ("reserved_signed_placeholder", bytes([0x81]) + plain[1:], 8, []),
            ("truncated", bytes(33), 8, []),
        ]:
            RNS.Reticulum.interface_discovery_sources = staticmethod(lambda: sources)
            observed = []
            receiver = discovery.InterfaceAnnounceHandler(required_value=required, callback=observed.append)
            receiver.received_announce(bytes(16), identity, payload)
            receiver_cases.append({"name": label, "payload": payload.hex(), "required": required,
                                   "sources": [source.hex() for source in sources], "accepted": len(observed) == 1})
    finally:
        RNS.Reticulum.interface_discovery_sources = original_sources
        RNS.Transport.hops_to = original_hops
        RNS.Transport.network_identity = old_network_identity
    # Exercise the actual advertisement builder too. Only input configuration and
    # deterministic candidate selection are supplied; serialization is upstream.
    advertiser = discovery.InterfaceAnnouncer.__new__(discovery.InterfaceAnnouncer)
    advertiser.owner = SimpleNamespace(has_network_identity=lambda: True, network_identity=identity)
    advertiser.stamp_cache = {}

    def select_stamp(material, stamp_cost, expand_rounds):
        workblock = LXStamper.stamp_workblock(material, expand_rounds=expand_rounds)
        candidate = 0
        while True:
            result = candidate.to_bytes(32, "big")
            if LXStamper.stamp_valid(result, stamp_cost, workblock):
                return result, LXStamper.stamp_value(workblock, result)
            candidate += 1

    advertiser.stamper = SimpleNamespace(generate_stamp=select_stamp)
    interface = type("BackboneInterface", (), {})()
    for key, value in {
        "discovery_stamp_value": 8, "discovery_location": None,
        "discovery_name": "Reference TCP", "discovery_latitude": 51.5,
        "discovery_longitude": -0.1, "discovery_height": None,
        "discovery_lxmf_address": bytes(range(16)), "reachable_on": "reference.example",
        "bind_port": 4242, "discovery_publish_ifac": True, "ifac_netname": "public",
        "ifac_netkey": "test-only", "discovery_encrypt": False,
    }.items():
        setattr(interface, key, value)
    transport_identity = RNS.Transport.identity
    RNS.Transport.identity = identity
    RNS.Reticulum._Reticulum__transport_enabled = True
    try:
        advertised = advertiser.get_interface_announce_data(interface)
    finally:
        RNS.Transport.identity = transport_identity
    assert advertised
    result = {"revision": PINNED_RNS_SHA,
              "discovery_source_sha256": hashlib.sha256(Path(discovery.__file__).read_bytes()).hexdigest(),
              "stamper_source_sha256": hashlib.sha256(Path(LXStamper.__file__).read_bytes()).hexdigest(),
              "private_key": bytes(range(64)).hex(), "identity_hash": identity.hash.hex(),
              "packed": packed.hex(), "workblock_sha256": hashlib.sha256(block).hexdigest(),
              "stamp": stamp.hex(), "value": LXStamper.stamp_value(block, stamp),
              "plain": plain.hex(), "encrypted": encrypted.hex(),
              "advertised_packed": advertised[1:-32].hex(),
              "advertised_plain": advertised.hex(),
              "receiver_cases": receiver_cases,
              "discovery_hash": observations[0]["discovery_hash"].hex(),
              "sanitized": {s: discovery.InterfaceAnnounceHandler.sanitize_name(s)
                            for s in [" !!Reference  TCP!! ", "é x   y )", "***", "A     B"]},
              "versions": {s: discovery.version_tuple(s)
                           for s in ["1.5.2", "1.5.2-beta", "1.5", "2", "bad"]}}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
