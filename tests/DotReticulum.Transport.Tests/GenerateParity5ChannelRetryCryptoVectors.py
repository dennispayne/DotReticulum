"""Run the actual pinned Channel/Packet resend/Link/Token/Transport path.

Only the outbound interface is an in-memory capture sink. No upstream methods,
entropy source, packet packing, receipt creation or cryptography are replaced.
The Link starts with an installed synthetic session key: handshake and network
transport are not evidence supplied by this fixture. Random IVs are genuine, so
regeneration intentionally produces different ciphertext/hash samples.
"""
import hashlib
import importlib
import inspect
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference()
import RNS
from RNS.Cryptography import Ed25519PrivateKey

channel_module = importlib.import_module("RNS.Channel")
token_module = importlib.import_module("RNS.Cryptography.Token")
root = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve()


class CaptureInterface:
    OUT = True
    online = True

    def __init__(self):
        self.frames = []

    def process_outgoing(self, raw):
        self.frames.append(bytes(raw))


class Message(channel_module.MessageBase):
    MSGTYPE = 0x0101

    def __init__(self, data=b"channel retry ciphertext oracle"):
        self.data = data

    def pack(self):
        return self.data

    def unpack(self, raw):
        self.data = raw


def main():
    interface = CaptureInterface()
    # Runtime configuration only: the actual Transport.outbound/_outbound,
    # transmit, receipt construction and packet hash registration all execute.
    RNS.Transport.owner = SimpleNamespace(is_connected_to_shared_instance=False)
    RNS.Transport.interfaces = [interface]
    key = bytes(range(64))
    link = object.__new__(RNS.Link)
    link.type = RNS.Destination.LINK
    link.hash = link.link_id = bytes(range(16))
    link.mtu = 500
    link.mdu = 431
    link.rtt = 0.18
    link.traffic_timeout_factor = RNS.Link.TRAFFIC_TIMEOUT_FACTOR
    link.status = RNS.Link.ACTIVE
    link.attached_interface = interface
    link.derived_key = key
    link.token = None
    link.tx = link.txbytes = 0
    signing_key = Ed25519PrivateKey.from_private_bytes(bytes(range(32, 64)))
    link.peer_sig_pub = signing_key.public_key()
    outlet = channel_module.LinkChannelOutlet(link)
    channel = channel_module.Channel(outlet)
    channel._next_sequence = 65535

    methods = {
        "Channel.send": channel_module.Channel.send,
        "Channel._packet_timeout": channel_module.Channel._packet_timeout,
        "Channel._packet_delivered": channel_module.Channel._packet_delivered,
        "LinkChannelOutlet.send": channel_module.LinkChannelOutlet.send,
        "LinkChannelOutlet.resend": channel_module.LinkChannelOutlet.resend,
        "LinkChannelOutlet.get_packet_id": channel_module.LinkChannelOutlet.get_packet_id,
        "Packet.send": RNS.Packet.send,
        "Packet.resend": RNS.Packet.resend,
        "Packet.pack": RNS.Packet.pack,
        "PacketReceipt.__init__": RNS.PacketReceipt.__init__,
        "PacketReceipt.validate_link_proof": RNS.PacketReceipt.validate_link_proof,
        "Link.encrypt": RNS.Link.encrypt,
        "Link.decrypt": RNS.Link.decrypt,
        "Token.encrypt": token_module.Token.encrypt,
        "Token.decrypt": token_module.Token.decrypt,
        "Transport.outbound": RNS.Transport.outbound,
        "Transport._outbound": RNS.Transport._outbound,
        "Transport.transmit": RNS.Transport.transmit,
    }
    executed = {name: 0 for name in methods}
    by_code = {method.__code__: name for name, method in methods.items()}

    def profile(frame, event, arg):
        if event == "call" and frame.f_code in by_code:
            executed[by_code[frame.f_code]] += 1

    snapshots = []
    receipts = []
    first_packet = None

    def capture(envelope):
        nonlocal first_packet
        packet = envelope.packet
        first_packet = packet if first_packet is None else first_packet
        receipts.append(packet.receipt)
        plaintext = link.decrypt(packet.ciphertext)
        decoded = channel_module.Envelope(outlet, raw=plaintext)
        decoded.unpack({Message.MSGTYPE: Message})
        assert packet.receipt.hash == packet.get_hash() == outlet.get_packet_id(packet)
        assert interface.frames[-1] == packet.raw
        assert plaintext == envelope.raw
        snapshots.append({
            "tries": envelope.tries,
            "samePacketObjectAsInitial": packet is first_packet,
            "raw": packet.raw.hex(),
            "ciphertext": packet.ciphertext.hex(),
            "iv": packet.ciphertext[:16].hex(),
            "packetHash": packet.get_hash().hex(),
            "receiptHash": packet.receipt.hash.hex(),
            "outletPacketId": outlet.get_packet_id(packet).hex(),
            "envelope": plaintext.hex(),
            "sequence": decoded.sequence,
            "receiptTimeout": packet.receipt.timeout,
            "receiptIsNew": all(packet.receipt is not receipt for receipt in receipts[:-1]),
        })

    sys.setprofile(profile)
    try:
        envelope = channel.send(Message())
        capture(envelope)
        for _ in range(2):
            # Model timeout status/notification only; the installed reference
            # outlet callback and all resend/receipt/crypto methods are real.
            receipt = envelope.packet.receipt
            receipt.status = RNS.PacketReceipt.FAILED
            receipt.callbacks.timeout(receipt)
            capture(envelope)
        current_hash = envelope.packet.get_hash()
        proof = current_hash + signing_key.sign(current_hash)
        accepted = envelope.packet.receipt.validate_link_proof(proof, link)
        assert accepted and len(channel._tx_ring) == 0
        next_envelope = channel.send(Message(b"after retry and wrap"))
        next_sequence = next_envelope.sequence
        assert next_sequence == 0
        channel._shutdown()
    finally:
        sys.setprofile(None)

    assert len({item["iv"] for item in snapshots}) == len(snapshots)
    assert len({item["ciphertext"] for item in snapshots}) == len(snapshots)
    assert len({item["packetHash"] for item in snapshots}) == len(snapshots)
    assert len({item["envelope"] for item in snapshots}) == 1
    assert all(count > 0 for count in executed.values()), executed
    result = {
        "revision": PINNED_RNS_SHA,
        "rnsVersion": RNS.__version__,
        "sourceHashes": {
            name: hashlib.sha256((root / "RNS" / name).read_bytes()).hexdigest()
            for name in ["Channel.py", "Packet.py", "Link.py", "Transport.py", "Cryptography\\Token.py"]
        },
        "methodProvenance": {
            name: {
                "file": str(Path(inspect.getsourcefile(method)).resolve().relative_to(root)),
                "line": inspect.getsourcelines(method)[1],
                "calls": executed[name],
            } for name, method in methods.items()
        },
        "disclosures": [
            "Actual imported RNS path, clean tracked source and exact revision verified before execution.",
            "No upstream callable or entropy source was patched.",
            "Outbound network I/O is a CaptureInterface.process_outgoing sink.",
            "Link object has an installed synthetic AES-256 session key; no handshake or live node is claimed.",
            "Timeout stimulus marks the prior receipt FAILED and invokes its actual installed timeout callback.",
            "IVs come from unmodified os.urandom; ciphertext vectors intentionally change on regeneration.",
            "Delivery uses actual PacketReceipt.validate_link_proof and Link.validate with a synthetic peer signing key.",
        ],
        "syntheticSessionKey": key.hex(),
        "syntheticPeerSigningPublicKey": signing_key.public_key().public_bytes().hex(),
        "attempts": snapshots,
        "finalProof": proof.hex(),
        "finalProofAccepted": accepted,
        "nextSequenceAfterDelivery": next_sequence,
    }
    destination = Path(__file__).parent / "Fixtures" / "parity5-channel-retry-crypto-vectors.json"
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {destination}")
    print("Fresh ciphertext/hash per retry, unchanged envelope/sequence, fresh receipt, same Python Packet object:")
    for snapshot in snapshots:
        print(f"  try={snapshot['tries']} sequence={snapshot['sequence']} hash={snapshot['packetHash']}")
    print(f"Real proof accepted: {accepted}; next sequence: {next_sequence}")


if __name__ == "__main__":
    main()
