"""Print public interoperability fixtures using an unmodified Reticulum checkout.

Run: PYTHONPATH=/path/to/Reticulum python generate_vectors.py
Reference: markqvist/Reticulum e40191b3d193b46b7f2d8a44424a594cd758839b
Sources: RNS/Identity.py, RNS/Cryptography/Token.py, RNS/Cryptography/HKDF.py.
Requires Python cryptography (generation used 41.0.7). These deliberately
sequential bytes are TEST KEYS ONLY, not identities or credentials in use.
"""

import json
import subprocess
from pathlib import Path
from unittest.mock import patch

import RNS
from RNS.Cryptography import Token, X25519PrivateKey, hkdf

revision = "e40191b3d193b46b7f2d8a44424a594cd758839b"
checkout = Path(RNS.__file__).resolve().parent.parent
assert subprocess.check_output(
    ["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True
).strip() == revision

private = bytes(range(64))
ephemeral = bytes(range(64, 96))
iv = bytes(range(160, 176))
message = b"Reticulum interoperability \x00\xff"
identity = RNS.Identity(create_keys=False)
assert identity.load_private_key(private)
shared = X25519PrivateKey.from_private_bytes(ephemeral).exchange(identity.pub)
derived = hkdf(length=64, derive_from=shared, salt=identity.hash, context=None)

with patch.object(
    X25519PrivateKey, "generate",
    return_value=X25519PrivateKey.from_private_bytes(ephemeral)
), patch("os.urandom", return_value=iv):
    encrypted = identity.encrypt(message)
    token256 = Token(bytes(range(64))).encrypt(message)
    token128 = Token(bytes(range(32))).encrypt(message)

assert identity.decrypt(encrypted) == message
print(json.dumps({
    "revision": revision,
    "backend": RNS.Cryptography.backend(),
    "private": private.hex(),
    "public": identity.get_public_key().hex(),
    "hash": identity.hash.hex(),
    "message": message.hex(),
    "signature": identity.sign(message).hex(),
    "ephemeral": ephemeral.hex(),
    "shared": shared.hex(),
    "derived": derived.hex(),
    "iv": iv.hex(),
    "token256": token256.hex(),
    "token128": token128.hex(),
    "encrypted": encrypted.hex(),
}, indent=2))
