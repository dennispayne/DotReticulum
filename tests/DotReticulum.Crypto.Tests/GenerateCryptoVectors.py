"""Generates crypto-vectors.json by EXECUTING the pinned upstream Reticulum implementation.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
Run:      PYTHONPATH=<upstream checkout> python GenerateCryptoVectors.py > Fixtures/crypto-vectors.json
Requires: the upstream RNS package (verified via RNS.__file__) and `cryptography` (RNS's PyCA provider).
Nothing here is computed by DotReticulum; every value comes from RNS.Identity / RNS.Cryptography.Token.
"""
import json
import os
import sys

import RNS
from RNS.Cryptography import Token, hkdf
from RNS.Identity import Identity

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"


def h(b):
    return b.hex()


def fixed(label, n):
    # Deterministic, clearly non-secret test material.
    import hashlib
    return hashlib.sha256(label.encode()).digest()[:n] if n <= 32 else (hashlib.sha256(label.encode()).digest() + hashlib.sha256((label + "2").encode()).digest())[:n]


def token_vectors():
    out = []
    for key_len in (32, 64):
        key = fixed(f"token-key-{key_len}", key_len)
        tk = Token(key)
        for n in (0, 1, 15, 16, 17, 31, 32, 100, 465):
            pt = bytes((i * 7 + n) & 0xFF for i in range(n))
            tok = tk.encrypt(pt)
            assert tk.decrypt(tok) == pt
            out.append({"key": h(key), "plaintext": h(pt), "token": h(tok)})
    return out


def hkdf_vectors():
    out = []
    ikm = fixed("hkdf-ikm", 32)
    for salt, info, length in ((b"", b"", 64), (fixed("salt", 16), b"", 64), (fixed("salt", 32), b"ctx", 100), (b"", b"ctx", 1), (b"", b"", 8160), (fixed("salt", 16), b"", 33)):
        out.append({"ikm": h(ikm), "salt": h(salt), "info": h(info), "length": length,
                    "okm": h(hkdf(length=length, derive_from=ikm, salt=salt, context=info))})
    return out


def identity_vectors():
    out = []
    for i in range(3):
        prv = fixed(f"identity-x-{i}", 32) + fixed(f"identity-ed-{i}", 32)
        ident = Identity.from_bytes(prv)
        assert ident is not None
        ratchet_prv = fixed(f"ratchet-{i}", 32)
        ratchet_pub = Identity._ratchet_public_bytes(ratchet_prv)
        msg = f"identity message {i} ".encode() * (i + 1)
        sig_msg = f"sign me {i}".encode()
        entry = {
            "private_key": h(prv),
            "public_key": h(ident.get_public_key()),
            "hash": h(ident.hash),
            "message": h(msg),
            "sign_message": h(sig_msg),
            "signature": h(ident.sign(sig_msg)),
            # Tokens produced by upstream Identity.encrypt (random ephemeral key / IV).
            "ciphertext": h(ident.encrypt(msg)),
            "ratchet_private": h(ratchet_prv),
            "ratchet_public": h(ratchet_pub),
            "ratchet_ciphertext": h(ident.encrypt(msg, ratchet=ratchet_pub)),
        }
        out.append(entry)
    return out


if __name__ == "__main__":
    # Fail loudly if the pinned checkout is not the one being imported.
    sys.stderr.write(f"RNS from {RNS.__file__}; pinned {PINNED_SHA}\n")
    json.dump({"upstream_sha": PINNED_SHA, "tokens": token_vectors(), "hkdf": hkdf_vectors(),
               "identities": identity_vectors()}, sys.stdout, indent=1)
    print()
