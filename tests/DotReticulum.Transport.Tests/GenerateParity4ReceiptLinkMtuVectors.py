"""Execute pinned RNS receipt, MTU and forced resource proof/cache contracts.

Runtime-only registration, transmission, watchdogs and PHY observers are replaced.
Cryptography, packing, validation, Resource.prove and Transport.cache_request run
from the unmodified pinned source. Set DOTRETICULUM_UPSTREAM and PYTHONPATH to
the authoritative checkout; run with its isolated reference virtual environment.
"""
import ast
import hashlib
import inspect
import json
import os
from pathlib import Path
import sys
import textwrap
import threading
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Cryptography import X25519PrivateKey, Ed25519PrivateKey

verify_reference()
assert RNS.__version__ == "1.5.7"
ROOT = Path(os.environ["DOTRETICULUM_UPSTREAM"])
DEST_PRV = bytes(range(1, 33)) + bytes(range(101, 133))
INIT_X = bytes(range(201, 233))
INIT_SIG = bytes(range(33, 65))
RESP_X = bytes(range(65, 97))
captured = []
RNS.Transport.register_destination = lambda destination: None
RNS.Transport.register_link = lambda link: None
RNS.Transport.activate_link = lambda link: None
RNS.Link.start_watchdog = lambda self: None
RNS.Link._Link__update_phy_stats = lambda self, packet, force_update=False: None


def send(packet, *args, **kwargs):
    packet.pack()
    captured.append(packet)
    packet.sent = True


RNS.Packet.send = send
identity = RNS.Identity(create_keys=False)
identity.load_private_key(DEST_PRV)
dest = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE,
                       "dotreticulum", "parity4", "link")
remote = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE,
                         "dotreticulum", "parity4", "link")
pub = X25519PrivateKey.from_private_bytes(INIT_X).public_key().public_bytes()
sigpub = Ed25519PrivateKey.from_private_bytes(INIT_SIG).public_key().public_bytes()
result = {
    "pinned": PINNED_RNS_SHA,
    "rnsVersion": RNS.__version__,
    "sourceSha256": {name: hashlib.sha256((ROOT / "RNS" / name).read_bytes()).hexdigest()
                     for name in ("Packet.py", "Transport.py", "Link.py", "Resource.py", "Reticulum.py")},
    "destinationPrivateKey": DEST_PRV.hex(),
    "destinationHash": dest.hash.hex(), "initiatorX25519Private": INIT_X.hex(),
    "initiatorSigSeed": INIT_SIG.hex(), "responderX25519Private": RESP_X.hex(),
    "mtus": [], "proofs": [], "eligibility": [],
}
link_module = sys.modules["RNS.Link"]
original_key = link_module.X25519PrivateKey
link_module.X25519PrivateKey = SimpleNamespace(
    generate=lambda: X25519PrivateKey.from_private_bytes(RESP_X))
for mtu in (None, 0, 500, 1024, 16384, 262144, 524288, 2097151):
    signalling = b"" if mtu is None else RNS.Link.signalling_bytes(mtu, RNS.Link.MODE_AES256_CBC)
    packet = RNS.Packet(remote, pub + sigpub + signalling, packet_type=RNS.Packet.LINKREQUEST)
    packet.pack()
    inbound = RNS.Packet(None, packet.raw)
    inbound.unpack()
    inbound.destination, inbound.receiving_interface, inbound.hops = dest, None, 1
    captured.clear()
    link = RNS.Link.validate_request(dest, inbound.data, inbound)
    assert link is not None
    proof = captured[-1]
    result["mtus"].append({"advertised": -1 if mtu is None else mtu, "requestRaw": packet.raw.hex(),
                           "proofRaw": proof.raw.hex(), "mtu": link.mtu, "mdu": link.mdu,
                           "linkId": link.link_id.hex(), "derivedKey": link.derived_key.hex()})
link_module.X25519PrivateKey = original_key
link.status, link.rtt = RNS.Link.ACTIVE, 0.25
link.mtu = 500
link.update_mdu()
# Opposite endpoint's verifier: the initiator validates the destination's key.
# Use the actual Link.load_peer/validate implementation, not a signature shim.
receipt_peer = object.__new__(RNS.Link)
receipt_peer.type, receipt_peer.rtt = RNS.Destination.LINK, 0.25
receipt_peer.traffic_timeout_factor = link.traffic_timeout_factor
receipt_peer.load_peer(link.pub_bytes, identity.get_public_key()[32:])

# Evaluate the actual upstream predicate, not a manually copied eligibility rule.
tree = ast.parse(textwrap.dedent(inspect.getsource(RNS.Transport._outbound)))
condition = next(node.test for node in ast.walk(tree)
                 if isinstance(node, ast.If) and any(isinstance(item, ast.Assign)
                     and any(isinstance(target, ast.Name) and target.id == "generate_receipt"
                             for target in item.targets)
                     for item in node.body))
expression = compile(ast.Expression(condition), "<upstream eligibility>", "eval")
for ptype in range(4):
    for dtype in range(4):
        result["eligibility"].append([
            bool(eval(expression, {"RNS": RNS, "packet": SimpleNamespace(
                create_receipt=True, packet_type=ptype,
                destination=SimpleNamespace(type=dtype), context=context)}))
            for context in range(256)])

receipt_packet = RNS.Packet(remote, b"parity4 packet receipt")
receipt_packet.pack()
result["receiptPacketRaw"] = receipt_packet.raw.hex()
packet_hash = receipt_packet.packet_hash
result["packetHash"] = packet_hash.hex()
link_receipt_packet = RNS.Packet(link, b"parity4 packet receipt")
link_receipt_packet.pack()
result["linkReceiptPacketRaw"] = link_receipt_packet.raw.hex()
original_instance = RNS.Reticulum.get_instance
RNS.Reticulum.get_instance = staticmethod(lambda: SimpleNamespace(get_first_hop_timeout=lambda _: 10))
original_hops = RNS.Transport.hops_to
RNS.Transport.hops_to = staticmethod(lambda _: 2)
inbound_tree = ast.parse(textwrap.dedent(inspect.getsource(RNS.Transport._inbound)))
settlement_loop = next(node for node in ast.walk(inbound_tree)
    if isinstance(node, ast.For) and isinstance(node.target, ast.Name)
    and node.target.id == "receipt" and isinstance(node.iter, ast.Name)
    and node.iter.id == "candidate_receipts")
settlement_code = compile(ast.Module(body=[settlement_loop], type_ignores=[]),
                          "<upstream duplicate receipt settlement>", "exec")
result["duplicates"] = []
for link_case in (False, True):
    destination = receipt_peer if link_case else remote
    packet_hash = link_receipt_packet.packet_hash if link_case else receipt_packet.packet_hash
    packet = SimpleNamespace(packet_hash=packet_hash, truncated_packet_hash=packet_hash[:16],
                             destination=destination)
    signature = identity.sign(packet_hash)
    for label, proof in (("implicit", signature), ("explicit", packet_hash + signature),
                          ("short", (packet_hash + signature)[:-1]),
                          ("trailing", packet_hash + signature + b"x"),
                          ("wronghash", bytes(32) + signature),
                          ("invalid", packet_hash + bytes(64))):
        receipt = RNS.PacketReceipt(packet)
        valid = (receipt.validate_link_proof(proof, receipt_peer) if link_case
                 else receipt.validate_proof(proof))
        result["proofs"].append({"link": link_case, "label": label, "proof": proof.hex(),
                                "accepted": valid, "timeout": receipt.timeout,
                                "status": receipt.status, "proved": receipt.proved})
    duplicates = [RNS.PacketReceipt(packet), RNS.PacketReceipt(packet)]
    proof_packet = RNS.Packet(remote, packet_hash + signature, packet_type=RNS.Packet.PROOF)
    if link_case:
        proof_packet.link = receipt_peer
    original_receipts, original_lock = RNS.Transport.receipts, RNS.Transport.receipts_lock
    RNS.Transport.receipts, RNS.Transport.receipts_lock = duplicates.copy(), threading.Lock()
    try:
        exec(settlement_code, {"RNS": RNS, "Transport": RNS.Transport,
             "candidate_receipts": duplicates, "packet": proof_packet})
        result["duplicates"].append({"link": link_case,
            "statuses": [receipt.status for receipt in duplicates],
            "remaining": len(RNS.Transport.receipts)})
    finally:
        RNS.Transport.receipts, RNS.Transport.receipts_lock = original_receipts, original_lock
RNS.Reticulum.get_instance = original_instance
RNS.Transport.hops_to = original_hops

packet_module = sys.modules["RNS.Packet"]
original_time = packet_module.time
clock = [1000.0]
packet_module.time = SimpleNamespace(time=lambda: clock[0])
result["timeoutBoundaries"] = []
for timeout, elapsed in ((2.0, 2.0), (2.0, 2.001), (-1.0, 0.0)):
    clock[0] = 1000.0
    receipt = RNS.PacketReceipt(SimpleNamespace(packet_hash=packet_hash,
        truncated_packet_hash=packet_hash[:16], destination=link))
    receipt.set_timeout(timeout)
    clock[0] += elapsed
    receipt.check_timeout()
    result["timeoutBoundaries"].append({"timeout": timeout, "elapsed": elapsed,
        "status": receipt.status, "concludedAt": receipt.concluded_at})
packet_module.time = original_time
original_latency = RNS.Transport.next_hop_per_byte_latency
result["firstHopTimeouts"] = []
for bitrate in (0, 5, 1000, 10000000):
    RNS.Transport.next_hop_per_byte_latency = staticmethod(
        lambda _, rate=bitrate: 8 / rate if rate else None)
    result["firstHopTimeouts"].append({"bitrate": bitrate,
        "timeout": RNS.Transport.first_hop_timeout(remote.hash)})
RNS.Transport.next_hop_per_byte_latency = original_latency

resource_data = b"parity4 forced resource proof"
resource_hash = RNS.Identity.full_hash(b"resource hash")
resource = object.__new__(RNS.Resource)
resource.status, resource.data, resource.hash = RNS.Resource.COMPLETE, resource_data, resource_hash
resource.link = link
resource.ensure_link = lambda: True
cache_observations = []
original_cache = RNS.Transport.cache
RNS.Transport.cache = staticmethod(lambda packet, force_cache=False, **kwargs:
                                   cache_observations.append((packet, force_cache)))
captured.clear()
resource.prove()
proof, forced = cache_observations[-1]
assert forced
RNS.Transport.cache = original_cache
RNS.Transport.get_cached_packet = staticmethod(lambda _: None)
captured.clear()
RNS.Transport.cache_request(proof.packet_hash, link)
query = captured[-1]
result["resource"] = {"data": resource_data.hex(), "hash": resource_hash.hex(),
                       "proofRaw": proof.raw.hex(), "proofPacketHash": proof.packet_hash.hex(),
                       "queryRaw": query.raw.hex(), "forced": forced}
print(json.dumps(result, indent=1))
