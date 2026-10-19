"""Generates Fixtures/bz2-vectors.json by EXECUTING Python's bz2 (the module upstream Resource.py uses).

Run: python GenerateBz2Vectors.py > Fixtures/bz2-vectors.json
Records upstream-compressed streams (decompress check) and the plaintext; the .NET test also verifies that
Python's bz2.decompress accepts .NET output via tests/Interop/verify_dotnet_artifacts.py.
"""
import bz2
import hashlib
import json

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"


def data(n, seed):
    out = bytearray()
    c = 0
    while len(out) < n:
        c += 1
        out += hashlib.sha256(f"{seed}-{c}".encode()).digest()
    return bytes(out[:n])


CASES = {
    "empty": b"",
    "text": b"The quick brown fox jumps over the lazy dog. " * 500,
    "zeros": bytes(300000),
    "random-1000": data(1000, "a"),
    "mixed-250k": data(1000, "b") * 120 + bytes(50000),
}


def main():
    cases = [{"name": k, "plain_hex": v.hex(), "bz2_hex": bz2.compress(v).hex()} for k, v in CASES.items()]
    print(json.dumps({"pinned_sha": PINNED_SHA, "cases": cases}))


main()
