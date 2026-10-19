"""Opens DotReticulum-produced output with the pinned upstream Reticulum implementation.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Usage:    python VerifyDotNetOutput.py dotnet-output.json   (produced by ExportDotNetVectors.cs)
Exits non-zero on the first mismatch.
"""
import json
import sys

import RNS
from RNS.Cryptography import Token
from RNS.Identity import Identity
from reference_environment import verify_reference

verify_reference()

data = json.load(open(sys.argv[1]))
sys.stderr.write(f"RNS from {RNS.__file__}\n")

for t in data["tokens"]:
    pt = Token(bytes.fromhex(t["key"])).decrypt(bytes.fromhex(t["token"]))
    assert pt == bytes.fromhex(t["plaintext"]), "token plaintext mismatch"

for v in data["identities"]:
    ident = Identity.from_bytes(bytes.fromhex(v["private_key"]))
    assert ident.get_public_key().hex() == v["public_key"], "public key mismatch"
    assert ident.hash.hex() == v["hash"], "hash mismatch"
    msg = bytes.fromhex(v["message"])
    assert ident.decrypt(bytes.fromhex(v["ciphertext"])) == msg, "identity decrypt mismatch"
    assert ident.decrypt(bytes.fromhex(v["ratchet_ciphertext"]), ratchets=[bytes.fromhex(v["ratchet_private"])], enforce_ratchets=True) == msg, "ratchet decrypt mismatch"
    pub = Identity(create_keys=False)
    assert pub.load_public_key(bytes.fromhex(v["public_key"]))
    assert pub.validate(bytes.fromhex(v["signature"]), bytes.fromhex(v["sign_message"])), "signature rejected by upstream"
    assert ident.sign(bytes.fromhex(v["sign_message"])).hex() == v["signature"], "signature differs"

print(f"OK: upstream opened {len(data['tokens'])} tokens and {len(data['identities'])} identities' ciphertexts/signatures")
