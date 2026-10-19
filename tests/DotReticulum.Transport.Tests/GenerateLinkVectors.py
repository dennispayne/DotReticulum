"""Generates Fixtures/link-vectors.json by EXECUTING the pinned upstream RNS.Link (responder side) with fixed key material.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
Run:      PYTHONPATH=<upstream checkout> python GenerateLinkVectors.py > Fixtures/link-vectors.json

Network-facing parts of upstream (Transport registration, watchdog, packet sending) are stubbed so the real Link.validate_request / prove /
prove_packet / packet pack and encrypt code runs unchanged, and every packet it would have sent is captured.
"""
import json
import os
import time

import RNS
from RNS.Cryptography import X25519PrivateKey, Ed25519PrivateKey
from RNS.vendor import umsgpack

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"

DEST_PRV = bytes(range(1, 33)) + bytes(range(101, 133))        # identity private key: x25519(32) + ed25519 seed(32)
INIT_X_PRV = bytes(range(201, 233))
INIT_SIG_SEED = bytes(range(33, 65))
RESP_X_PRV = bytes(range(65, 97))

captured = []


class FixedKey:
    """Replaces X25519PrivateKey/Ed25519PrivateKey.generate inside RNS.Link so the exchange is deterministic."""
    def __init__(self, cls, value):
        self.cls, self.value = cls, value

    def generate(self):
        return self.cls.from_private_bytes(self.value)


def stub_runtime():
    RNS.Transport.register_destination = lambda destination: None
    RNS.Transport.register_link = lambda link: None
    RNS.Transport.activate_link = lambda link: None
    RNS.Link.start_watchdog = lambda self: None
    RNS.Link._Link__update_phy_stats = lambda self, packet, force_update=False: None

    def fake_send(self, *a, **k):
        self.pack()
        captured.append(self)
        self.sent = True
        return None
    RNS.Packet.send = fake_send


def main():
    stub_runtime()
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(DEST_PRV)
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "vectors", "link")
    out_destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "dotreticulum", "vectors", "link")

    init_pub = X25519PrivateKey.from_private_bytes(INIT_X_PRV).public_key().public_bytes()
    init_sig_pub = Ed25519PrivateKey.from_private_bytes(INIT_SIG_SEED).public_key().public_bytes()
    out = {"pinned": PINNED_SHA, "destinationPrivateKey": DEST_PRV.hex(), "destinationHash": destination.hash.hex(),
           "initiatorX25519Private": INIT_X_PRV.hex(), "initiatorSigSeed": INIT_SIG_SEED.hex(),
           "responderX25519Private": RESP_X_PRV.hex(), "requests": []}

    for label, signalling in (("mtu500", RNS.Link.signalling_bytes(500, RNS.Link.MODE_AES256_CBC)),
                              ("mtu262144", RNS.Link.signalling_bytes(262144, RNS.Link.MODE_AES256_CBC)),
                              ("nosignalling", b"")):
        request = RNS.Packet(out_destination, init_pub + init_sig_pub + signalling, packet_type=RNS.Packet.LINKREQUEST)
        request.pack()
        link_id = RNS.Link.link_id_from_lr_packet(request)
        entry = {"label": label, "requestRaw": request.raw.hex(), "linkId": link_id.hex(), "signalling": signalling.hex()}

        # Responder: run the unmodified upstream validate_request with fixed responder ephemeral key
        captured.clear()
        parsed = RNS.Packet(None, request.raw)
        parsed.unpack()
        parsed.destination = destination
        parsed.receiving_interface = None
        parsed.hops = 1
        import sys
        link_mod = sys.modules["RNS.Link"]
        original = link_mod.X25519PrivateKey
        link_mod.X25519PrivateKey = FixedKey(X25519PrivateKey, RESP_X_PRV)
        try:
            link = RNS.Link.validate_request(destination, parsed.data, parsed)
        finally:
            link_mod.X25519PrivateKey = original
        assert link is not None, "upstream rejected the request"
        proof = captured[-1]
        entry["proofRaw"] = proof.raw.hex()
        entry["mtu"] = link.mtu
        entry["mdu"] = link.mdu
        entry["derivedKey"] = link.derived_key.hex()
        entry["responderPublic"] = link.pub_bytes.hex()
        out["requests"].append(entry)
        if label != "mtu500":
            continue

        # Link established upstream-side: capture every packet type, encrypted with the real link token
        link.status = RNS.Link.ACTIVE
        link.rtt = 0.25
        samples = {}

        def pack(context, data, ptype=RNS.Packet.DATA, encrypt=True):
            p = RNS.Packet(link, data, packet_type=ptype, context=context, create_receipt=False)
            p.pack()
            return p

        samples["data"] = {"plaintext": b"hello from upstream".hex(), "raw": pack(RNS.Packet.NONE, b"hello from upstream").raw.hex()}
        samples["keepalive_ping"] = {"plaintext": b"\xff".hex(), "raw": pack(RNS.Packet.KEEPALIVE, b"\xff").raw.hex()}
        samples["keepalive_pong"] = {"plaintext": b"\xfe".hex(), "raw": pack(RNS.Packet.KEEPALIVE, b"\xfe").raw.hex()}
        samples["lrrtt"] = {"plaintext": umsgpack.packb(0.25).hex(), "raw": pack(RNS.Packet.LRRTT, umsgpack.packb(0.25)).raw.hex()}
        samples["linkclose"] = {"plaintext": link.link_id.hex(), "raw": pack(RNS.Packet.LINKCLOSE, link.link_id).raw.hex()}

        peer_identity = RNS.Identity()
        identify_data = peer_identity.get_public_key() + peer_identity.sign(link.link_id + peer_identity.get_public_key())
        samples["linkidentify"] = {"plaintext": identify_data.hex(), "raw": pack(RNS.Packet.LINKIDENTIFY, identify_data).raw.hex(),
                                   "identityHash": peer_identity.hash.hex()}

        request_payload = umsgpack.packb([1700000000.5, RNS.Identity.truncated_hash(b"/echo"), "hi"])
        req_packet = pack(RNS.Packet.REQUEST, request_payload)
        samples["request"] = {"plaintext": request_payload.hex(), "raw": req_packet.raw.hex(),
                              "pathHash": RNS.Identity.truncated_hash(b"/echo").hex(), "requestId": req_packet.getTruncatedHash().hex()}

        # Link packet proof: what upstream emits for a data packet received over the link (responder signs with the destination key)
        captured.clear()
        inbound = RNS.Packet(None, bytes.fromhex(samples["data"]["raw"]))
        inbound.unpack()
        inbound.link = link
        link.prove_packet(inbound)
        samples["packet_proof"] = {"dataPacketHash": inbound.get_hash().hex(), "raw": captured[-1].raw.hex()}
        entry["samples"] = samples
        out["linkIdForSamples"] = link_id.hex()

    print(json.dumps(out, indent=1))


if __name__ == "__main__":
    main()
