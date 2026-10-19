"""Generates Fixtures/packet-vectors.json by EXECUTING the pinned upstream Reticulum Packet implementation.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b (RNS/Packet.py)
Run:      PYTHONPATH=<upstream checkout> python GeneratePacketVectors.py > Fixtures/packet-vectors.json

Method: wire bytes are produced by upstream Packet.pack() through a minimal duck-typed destination (identity
encryption) wherever upstream can pack them (header 1 packets, header 2 announces); the remaining
header 2 shapes are assembled from the documented layout, because upstream pack() only supports header 2 for
announces. Every entry is then run through upstream Packet.unpack()/get_hash()/getTruncatedHash() and the
upstream results are what the vectors record. Nothing is computed by DotReticulum.
"""
import hashlib
import json
import sys

import RNS
from RNS.Packet import Packet

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"


class StubDestination:
    # Packet.pack() only needs these attributes; encrypt() is the identity function so the payload is on the wire verbatim.
    def __init__(self, dest_hash, dest_type):
        self.hash = dest_hash
        self.type = dest_type
        self.mtu = RNS.Reticulum.MTU
        self.link_id = dest_hash

    def encrypt(self, data):
        return data


def det(label, n):
    out = b""
    i = 0
    while len(out) < n:
        out += hashlib.sha256(f"{label}-{i}".encode()).digest()
        i += 1
    return out[:n]


def upstream_pack(dest_hash, dest_type, payload, packet_type, context, header_type, transport_id, context_flag, transport_type, hops):
    try:
        p = Packet(StubDestination(dest_hash, dest_type), payload, packet_type=packet_type, context=context,
                   transport_type=transport_type, header_type=header_type, transport_id=transport_id, context_flag=context_flag)
        p.hops = hops
        p.pack()
        return p.raw
    except Exception:
        return None


def upstream_parse(raw):
    p = Packet(None, raw)
    ok = p.unpack()
    entry = {"raw": raw.hex(), "upstream_valid": ok}
    if ok:
        entry.update({
            "flags": p.flags, "hops": p.hops, "header_type": p.header_type, "context_flag": p.context_flag,
            "transport_type": p.transport_type, "destination_type": p.destination_type, "packet_type": p.packet_type,
            "transport_id": p.transport_id.hex() if p.transport_id else "",
            "destination_hash": p.destination_hash.hex(), "context": p.context, "payload": p.data.hex(),
            "hash": p.get_hash().hex(), "truncated_hash": p.getTruncatedHash().hex(),
            "hashable_part": p.get_hashable_part().hex(),
        })
    return entry


def build(flags, hops, transport_id, dest_hash, context, payload):
    raw = bytes([flags, hops])
    if (flags >> 6) & 1:
        raw += transport_id
    return raw + dest_hash + bytes([context]) + payload


def make(name, flags, hops, payload, context, label=None):
    label = label or name
    dest_hash = det(label + "-dest", 16)
    transport_id = det(label + "-tid", 16)
    header_type = (flags >> 6) & 1
    raw = build(flags, hops, transport_id, dest_hash, context, payload)
    packed = upstream_pack(dest_hash, (flags >> 2) & 3, payload, flags & 3, context, header_type,
                           transport_id if header_type else None, (flags >> 5) & 1, (flags >> 4) & 1, hops)
    entry = {"name": name}
    if packed is not None and not (flags & 0x80):
        assert packed == raw, f"upstream pack disagrees with documented layout for {name}"
    entry["upstream_packed"] = packed is not None and packed == raw
    entry.update(upstream_parse(raw))
    return entry


def main():
    sys.stderr.write(f"RNS from {RNS.__file__}; pinned {PINNED_SHA}\n")
    out = {"upstream_sha": PINNED_SHA}

    # All 128 flag combinations of bits 6..0 (header 1 and header 2).
    all_flags = []
    for f in range(128):
        payload = det(f"payload-{f}", 1 + (f * 7) % 40)
        all_flags.append(make(f"flags-{f:#04x}", f, f, payload, f))
    out["all_flags"] = all_flags

    # Reserved bit 7: upstream's unpack accepts it and the hash ignores it.
    out["reserved_bit"] = [make(f"reserved-{f:#04x}", f | 0x80, 3, det("res", 20), 0x11) for f in (0x00, 0x01, 0x2A, 0x41, 0x5F, 0x7F)]

    # Canonical packets: (flags, hops, payload, context)
    canon = [
        ("data-h1-single", 0x00, 0, det("c1", 48), 0x00),
        ("announce-h1-single", 0x01, 0, det("c2", 167), 0x00),
        ("linkrequest-h1-single", 0x02, 0, det("c3", 67), 0x00),
        ("proof-h1-link", 0x0F, 0, det("c4", 64), 0xFF),
        ("data-h2-transport", 0x50, 5, det("c5", 80), 0x00),
        ("announce-h2-transport", 0x51, 2, det("c6", 167), 0x00),
        ("data-h1-plain-ctx-flag", 0x28, 1, det("c7", 30), 0x0E),
        ("data-h1-group-resource", 0x04, 127, det("c8", 100), 0x01),
    ]
    out["canonical"] = [make(n, f, h, p, c) for n, f, h, p, c in canon]

    # Hop boundaries.
    out["hops"] = [make(f"hops-{h}", 0x00, h, det("hop", 10), 0) for h in (0, 1, 127, 128, 129, 255)]

    # MTU boundaries (payload sizes giving 499 / 500 / 501 byte packets).
    mtu = []
    for hdr_name, flags, hdr in (("h1", 0x00, 19), ("h2", 0x50, 35)):
        for total in (499, 500, 501):
            e = make(f"mtu-{hdr_name}-{total}", flags, 0, det("mtu", total - hdr), 0)
            e["total_length"] = total
            mtu.append(e)
    out["mtu"] = mtu

    # Truncation of every prefix length for one header 1 and one header 2 packet.
    trunc = []
    for name, flags in (("h1", 0x01), ("h2", 0x51)):
        full = bytes.fromhex(make(f"trunc-{name}", flags, 4, det("trunc", 12), 0x07)["raw"])
        for n in range(len(full) + 1):
            trunc.append({"name": f"trunc-{name}-{n}", **upstream_parse(full[:n])})
    out["truncation"] = trunc

    json.dump(out, sys.stdout, indent=1)
    print()


if __name__ == "__main__":
    main()
