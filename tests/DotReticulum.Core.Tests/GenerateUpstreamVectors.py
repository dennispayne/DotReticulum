"""Reproduce PacketTests/DestinationTests vectors with actual upstream methods.

Run: python tests/DotReticulum.Core.Tests/GenerateUpstreamVectors.py
Only Python's standard library is needed. Upstream methods are extracted with
AST (not reimplemented); minimal RNS stubs provide SHA-256 and protocol constants.
This tests wire packing/hashing only, not upstream encryption or networking.
"""

import ast
import hashlib
import struct
import types
import urllib.request

REVISION = "e40191b3d193b46b7f2d8a44424a594cd758839b"
BASE = f"https://raw.githubusercontent.com/markqvist/Reticulum/{REVISION}/RNS/"


class Identity:
    TRUNCATED_HASHLENGTH = 128
    NAME_HASH_LENGTH = 80

    @staticmethod
    def full_hash(data):
        return hashlib.sha256(data).digest()


rns = types.SimpleNamespace(
    Identity=Identity,
    Reticulum=types.SimpleNamespace(TRUNCATED_HASHLENGTH=128),
    Transport=types.SimpleNamespace(PATHFINDER_M=128),
    sl=lambda level: False,
    LOG_DEBUG=0,
)


def load_class(filename, class_name, methods, constants=()):
    with urllib.request.urlopen(BASE + filename) as response:
        source = response.read()
    print(f"# {BASE + filename} SHA256={hashlib.sha256(source).hexdigest()}")
    original = next(
        node for node in ast.parse(source).body
        if isinstance(node, ast.ClassDef) and node.name == class_name
    )
    original.body = [
        node for node in original.body
        if (isinstance(node, ast.FunctionDef) and node.name in methods)
        or (isinstance(node, ast.Assign)
            and isinstance(node.targets[0], ast.Name)
            and node.targets[0].id in constants)
    ]
    module = ast.fix_missing_locations(ast.Module(body=[original], type_ignores=[]))
    namespace = {"RNS": rns, "struct": struct}
    exec(compile(module, BASE + filename, "exec"), namespace)
    return namespace[class_name]


Packet = load_class(
    "Packet.py", "Packet",
    {"get_packed_flags", "pack", "unpack", "update_hash", "get_hash", "get_hashable_part"},
    {"HEADER_1", "HEADER_2", "DATA", "ANNOUNCE", "LINKREQUEST", "PROOF",
     "LRPROOF", "RESOURCE_PRF", "RESOURCE", "KEEPALIVE", "CACHE_REQUEST"},
)
Destination = load_class("Destination.py", "Destination", {"expand_name", "hash"},
                         {"SINGLE", "GROUP", "PLAIN", "LINK"})
rns.Packet = Packet
rns.Destination = Destination

# Verify constants against Transport.py rather than trusting the stub.
with urllib.request.urlopen(BASE + "Transport.py") as response:
    transport_source = response.read()
tree = ast.parse(transport_source)
transport = next(node for node in tree.body
                 if isinstance(node, ast.ClassDef) and node.name == "Transport")
hop_limit = next(node.value.value for node in transport.body
                 if isinstance(node, ast.Assign)
                 and isinstance(node.targets[0], ast.Name)
                 and node.targets[0].id == "PATHFINDER_M")
assert hop_limit == rns.Transport.PATHFINDER_M
print(f"# {BASE}Transport.py SHA256={hashlib.sha256(transport_source).hexdigest()}")

destination_hash = bytes(range(16))
transport_id = bytes(range(16, 32))
payload = bytes.fromhex("00ff807265746963756c756d")
for header in (0, 1):
    for kind in range(4):
        packet = Packet()
        packet.header_type = header
        packet.packet_type = kind
        packet.transport_type = header
        packet.context_flag = kind % 2
        packet.context = 0x0e
        packet.hops = 127
        packet.MTU = 500
        packet.destination = types.SimpleNamespace(
            type=kind, hash=destination_hash, encrypt=lambda data: data,
        )
        packet.transport_id = transport_id if header else None
        packet.data = payload
        packet.flags = packet.get_packed_flags()
        # Upstream pack only sets ciphertext for ANNOUNCE on header 2.
        # Other transported packets are already encrypted; supply their wire payload.
        packet.ciphertext = payload
        packet.pack()
        received = Packet()
        received.raw = packet.raw
        assert received.unpack()
        assert received.get_hash() == packet.get_hash()
        print(f'packet "{packet.raw.hex()}" "{packet.get_hash().hex()}"')

for name, identity_hash in (
    ("example.echo", None),
    ("example.echo", bytes(range(16))),
    ("应用.回声", None),
    ("应用.回声", bytes(range(16))),
):
    app, *aspects = name.split(".")
    result = Destination.hash(identity_hash, app, *aspects)
    print(f'destination "{name}" "{identity_hash.hex() if identity_hash else ""}" "{result.hex()}"')

# Upstream accepts reserved bit 7 and ignores it when calculating hashes.
received.raw = bytes([packet.raw[0] | 0x80]) + packet.raw[1:]
assert received.unpack()
assert received.get_hash() == packet.get_hash()
for hops in (128, 255):
    received.raw = packet.raw[:1] + bytes([hops]) + packet.raw[2:]
    assert not received.unpack()
print("# Verified reserved bit 7 accepted, hops 128/255 rejected.")
