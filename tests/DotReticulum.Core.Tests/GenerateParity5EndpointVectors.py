"""Independent endpoint/provider observations from verified RNS a29bfb51.

No daemon is started. Transport registration is isolated to an empty destination list;
only _used_destination_data is replaced because these observations have no runtime owner.
Announce time/randomness and AES IVs are fixed only for reproducible vectors.
"""
import hashlib
import hmac
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference()
import RNS
from RNS.Cryptography import Token, hkdf, backend
from RNS.Cryptography import X25519PrivateKey
from RNS.Cryptography.X25519 import X25519PrivateKey as InternalPrivate, X25519PublicKey as InternalPublic
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey as PycaPrivate, X25519PublicKey as PycaPublic
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from RNS.vendor import umsgpack

root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
RNS.Transport.destinations = []
RNS.Transport.owner = SimpleNamespace(is_connected_to_shared_instance=False)
identity = RNS.Identity(create_keys=False)
private_key = bytes(range(64))
assert identity.load_private_key(private_key)
destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "parity5", "endpoint")
calls = [0]


def default_data():
    calls[0] += 1
    return b"default-" + str(calls[0]).encode()


destination.set_default_app_data(default_data)
announces = []
with patch("RNS.Destination.time.time", return_value=1700000000), patch.object(
    RNS.Identity, "get_random_hash", return_value=bytes(range(16))
):
    for data, response, tag in [
        (None, False, b"tag"),
        (b"ignored-on-cache-hit", True, b"tag"),
        (b"", False, b"empty"),
        (None, False, b"new"),
    ]:
        packet = destination.announce(app_data=data, path_response=response, tag=tag, send=False)
        packet.pack()
        announces.append({"payload": packet.data.hex(), "context": packet.context, "flag": packet.context_flag})

groups = []
for size in [32, 64]:
    key = bytes(range(size))
    group = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.GROUP, "parity5", f"group{size}")
    group.load_private_key(key)
    with patch("RNS.Cryptography.Token.os.urandom", return_value=bytes(range(16))):
        encrypted = group.encrypt(b"group payload")
    groups.append({"key": key.hex(), "plaintext": b"group payload".hex(), "token": encrypted.hex()})

packed_ratchets = umsgpack.packb([bytes(range(32)), bytes(range(32, 64))])
ratchets_file = umsgpack.packb({"signature": identity.sign(packed_ratchets), "ratchets": packed_ratchets})


def observe(operation):
    try:
        value = operation()
        return {"accepted": True, "result": value.hex() if isinstance(value, bytes) else value}
    except Exception as error:
        return {"accepted": False, "error": type(error).__name__}


local_recall = RNS.Identity.recall(destination.hash)
endpoint_contract = {
    "accepts_links_default": destination.accepts_links(),
    "local_recall_public_key": local_recall.get_public_key().hex(),
    "local_recall_app_data": local_recall.app_data,
    "duplicate_incoming": observe(lambda: RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "parity5", "endpoint")),
    "invalid_direction": observe(lambda: RNS.Destination(identity, 0, RNS.Destination.SINGLE, "parity5", "invalid")),
    "plain_with_identity": observe(lambda: RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.PLAIN, "parity5", "invalid")),
    "outgoing_without_identity": observe(lambda: RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.SINGLE, "parity5", "invalid")),
}

public_key = identity.get_public_key()
malformed = {}
for name, key in [
    ("zero-x25519", bytes(32) + public_key[32:]),
    ("noncanonical-x25519", (2**255 - 19).to_bytes(32, "little") + public_key[32:]),
    ("zero-ed25519", public_key[:32] + bytes(32)),
    ("short-public", public_key[:63]),
    ("long-public", public_key + b"\x00"),
]:
    malformed[name] = observe(lambda key=key: RNS.Identity(create_keys=False).load_public_key(key))

hkdf_cases = []
for length, material in [(1, b"material"), (8160, b"material"), (8161, b"material"), (0, b"material"), (32, b"")]:
    outcome = observe(lambda length=length, material=material: hkdf(length=length, derive_from=material, salt=None, context=b"boundary"))
    if outcome.get("result") and isinstance(outcome["result"], str):
        outcome["sha256"] = hashlib.sha256(bytes.fromhex(outcome.pop("result"))).hexdigest()
    hkdf_cases.append({"length": length, "material": material.hex(), **outcome})

padding_cases = []
key = bytes(range(64))
iv = bytes(range(16))
for name, block in [
    ("valid-two", b"a" * 14 + b"\x02\x02"),
    ("mismatched-two", b"a" * 14 + b"\x01\x02"),
    ("zero-padding", b"a" * 15 + b"\x00"),
    ("oversized-padding", b"a" * 15 + b"\x11"),
]:
    encryptor = Cipher(algorithms.AES(key[32:]), modes.CBC(iv)).encryptor()
    body = iv + encryptor.update(block) + encryptor.finalize()
    token = body + hmac.new(key[:32], body, hashlib.sha256).digest()
    padding_cases.append({"name": name, "token": token.hex(), **observe(lambda token=token: Token(key).decrypt(token))})

provider_cases = []
for name, peer in [
    ("canonical", public_key[:32]),
    ("zero", bytes(32)),
    ("noncanonical-alias-zero", (2**255 - 19).to_bytes(32, "little")),
    ("high-bit-canonical-alias", public_key[:31] + bytes([public_key[31] | 128])),
]:
    internal = observe(lambda peer=peer: InternalPrivate.from_private_bytes(bytes(range(32))).exchange(InternalPublic.from_public_bytes(peer)))
    pyca = observe(lambda peer=peer: PycaPrivate.from_private_bytes(bytes(range(32))).exchange(PycaPublic.from_public_bytes(peer)))
    provider_cases.append({"name": name, "peer": peer.hex(), "internal": internal, "pyca": pyca})

ratchet_keys = [bytes(range(64, 96)), bytes(range(96, 128))]
ratchet_metadata = []
with patch("RNS.Identity.X25519PrivateKey.generate", return_value=X25519PrivateKey.from_private_bytes(bytes(range(128, 160)))), patch(
    "RNS.Cryptography.Token.os.urandom", return_value=bytes(range(16))
):
    for name, selected, plaintext, enforce in [
        ("older-ratchet", ratchet_keys[1], b"selected older key", False),
        ("base-fallback", None, b"base key", False),
        ("enforced-base-rejected", None, b"must reject", True),
        ("empty-ratchet-payload", ratchet_keys[0], b"", True),
    ]:
        public = RNS.Identity._ratchet_public_bytes(selected) if selected is not None else None
        ciphertext = identity.encrypt(plaintext, ratchet=public)
        receiver = SimpleNamespace(latest_ratchet_id=None)
        decrypted = identity.decrypt(ciphertext, ratchets=ratchet_keys, enforce_ratchets=enforce, ratchet_id_receiver=receiver)
        ratchet_metadata.append({
            "name": name, "ciphertext": ciphertext.hex(), "enforce": enforce,
            "plaintext": decrypted.hex() if decrypted is not None else None,
            "ratchet_id": receiver.latest_ratchet_id.hex() if receiver.latest_ratchet_id is not None else None,
        })

source_files = ["RNS/Destination.py", "RNS/Identity.py", "RNS/Transport.py",
                "RNS/Cryptography/Token.py", "RNS/Cryptography/HKDF.py",
                "RNS/Cryptography/PKCS7.py", "RNS/Cryptography/Provider.py",
                "RNS/Cryptography/X25519.py", "RNS/Cryptography/Proxies.py", "RNS/Cryptography/AES.py"]
fixture = {
    "revision": PINNED_RNS_SHA,
    "module": str(Path(RNS.__file__).resolve()),
    "sources": {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in source_files},
    "private_key": private_key.hex(), "public_key": public_key.hex(),
    "destination_hash": destination.hash.hex(),
    "default_calls": calls[0], "announces": announces, "groups": groups,
    "signed_ratchets": ratchets_file.hex(), "malformed_keys": malformed, "hkdf": hkdf_cases,
    "provider": backend(), "padding_key": key.hex(), "padding": padding_cases, "x25519_providers": provider_cases,
    "endpoint_contract": endpoint_contract,
    "ratchet_keys": [value.hex() for value in ratchet_keys], "ratchet_metadata": ratchet_metadata,
}
output = Path(__file__).parent / "Fixtures" / "parity5-endpoints.json"
output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")
print(f"Verified {PINNED_RNS_SHA}: wrote {output}; default calls={calls[0]}")
