"""Generates Fixtures/resource-vectors.json by EXECUTING the pinned upstream RNS.Resource (sender side) on a real upstream Link.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
Run:      PYTHONPATH=<upstream checkout> python GenerateResourceVectors.py > Fixtures/resource-vectors.json

The link is created through the real Link.validate_request with fixed keys (as in GenerateLinkVectors.py). Two things are made deterministic:
RNS.Identity.get_random_hash (a counter-derived value, so random_hash is fixed) and link.encrypt (a reversible XOR, because the real Token
uses a random IV; the Token itself is covered by the Crypto vectors). Everything else - compression, hash, expected proof, part split,
map hashes, part packets and the advertisement - is upstream code running unchanged.
"""
import hashlib
import json
import sys

import RNS
from RNS.Cryptography import X25519PrivateKey
from RNS.vendor import umsgpack

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"
DEST_PRV = bytes(range(1, 33)) + bytes(range(101, 133))
INIT_X_PRV = bytes(range(201, 233))
INIT_SIG_SEED = bytes(range(33, 65))
RESP_X_PRV = bytes(range(65, 97))

counter = [0]


def fixed_random_hash():
    counter[0] += 1
    return hashlib.sha256(b"fixed-random" + bytes([counter[0]])).digest()[:16]


class FixedKey:
    def __init__(self, cls, value):
        self.cls, self.value = cls, value

    def generate(self):
        return self.cls.from_private_bytes(self.value)


def make_link():
    RNS.Transport.register_destination = lambda destination: None
    RNS.Transport.register_link = lambda link: None
    RNS.Transport.activate_link = lambda link: None
    RNS.Link.start_watchdog = lambda self: None
    RNS.Link._Link__update_phy_stats = lambda self, packet, force_update=False: None
    def fake_send(self, *a, **k):
        self.pack()
        self.sent = True
        return None
    RNS.Packet.send = fake_send
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(DEST_PRV)
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "vectors", "resource")
    out_destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "dotreticulum", "vectors", "resource")
    init_pub = X25519PrivateKey.from_private_bytes(INIT_X_PRV).public_key().public_bytes()
    from RNS.Cryptography import Ed25519PrivateKey
    init_sig_pub = Ed25519PrivateKey.from_private_bytes(INIT_SIG_SEED).public_key().public_bytes()
    request = RNS.Packet(out_destination, init_pub + init_sig_pub + RNS.Link.signalling_bytes(500, RNS.Link.MODE_AES256_CBC),
                         packet_type=RNS.Packet.LINKREQUEST)
    request.pack()
    parsed = RNS.Packet(None, request.raw)
    parsed.unpack()
    parsed.destination = destination
    parsed.receiving_interface = None
    parsed.hops = 1
    link_mod = sys.modules["RNS.Link"]
    original = link_mod.X25519PrivateKey
    link_mod.X25519PrivateKey = FixedKey(X25519PrivateKey, RESP_X_PRV)
    try:
        link = RNS.Link.validate_request(destination, parsed.data, parsed)
    finally:
        link_mod.X25519PrivateKey = original
    link.status = RNS.Link.ACTIVE
    link.rtt = 0.25
    return link


def xor(data):
    return bytes(b ^ 0x5A for b in data)


def main():
    link = make_link()
    RNS.Identity.get_random_hash = staticmethod(fixed_random_hash)
    link.encrypt = xor

    text = (b"The quick brown fox jumps over the lazy dog. " * 200)
    noise = hashlib.shake_256(b"resource-vector-noise").digest(40000)
    cases = [
        ("compressible_metadata", text, {"name": "fox.txt", "n": 7}, True, None),
        ("incompressible_multipart", noise[:3000], None, True, None),
        ("incompressible_hashmap_segments", noise, None, True, b"\x10" * 16),
        ("compression_disabled", text, None, False, None),
        ("request", umsgpack.packb([1700000000.5, RNS.Identity.truncated_hash(b"/x"), "payload " * 200]), None, True, "request"),
    ]
    out = {"pinned": PINNED_SHA, "mtu": link.mtu, "sdu": link.mtu - RNS.Reticulum.HEADER_MAXSIZE - RNS.Reticulum.IFAC_MIN_SIZE,
           "hashmapMaxLen": RNS.ResourceAdvertisement.HASHMAP_MAX_LEN, "collisionGuardSize": RNS.ResourceAdvertisement.COLLISION_GUARD_SIZE,
           "linkId": link.link_id.hex(), "cases": []}
    for label, data, metadata, compress, kind in cases:
        counter[0] = 0
        request_id = None
        if kind == "request":
            request_id = RNS.Identity.truncated_hash(data)
        r = RNS.Resource(data, link, metadata=metadata, advertise=False, auto_compress=compress, request_id=request_id, is_response=False)
        adv = RNS.ResourceAdvertisement(r, request_id=request_id, is_response=False)
        packed = adv.pack(0)
        full_data = (r.metadata + data) if r.has_metadata else data
        stream_plain = xor(b"".join(p.data for p in r.parts))
        entry = {
            "label": label,
            "data": data.hex(),
            "metadataPacked": umsgpack.packb(metadata).hex() if metadata is not None else None,
            "hashedData": full_data.hex(),
            "autoCompress": compress,
            "compressed": r.compressed,
            "randomHash": r.random_hash.hex(),
            "hash": r.hash.hex(),
            "expectedProof": r.expected_proof.hex(),
            "originalHash": r.original_hash.hex(),
            "streamPlain": stream_plain.hex(),
            "parts": [p.data.hex() for p in r.parts],
            "partPacketRaw": [p.raw.hex() for p in r.parts],
            "hashmap": r.hashmap.hex(),
            "totalParts": r.total_parts,
            "transferSize": r.size,
            "advertisement": packed.hex(),
            "requestId": request_id.hex() if request_id else None,
            "hashmapUpdateSegment1": (r.hash + umsgpack.packb([1, r.hashmap[74 * 4:2 * 74 * 4]])).hex() if r.total_parts > 74 else None,
        }
        out["cases"].append(entry)
    print(json.dumps(out, indent=1))


if __name__ == "__main__":
    main()





