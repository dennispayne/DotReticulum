"""Generates Fixtures/msgpack-vectors.json by EXECUTING the pinned upstream bundled umsgpack (RNS/vendor/umsgpack.py).

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
Run:      PYTHONPATH=<upstream checkout> python GenerateMsgPackVectors.py > Fixtures/msgpack-vectors.json
"""
import json

from RNS.vendor import umsgpack

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"

INTS = [0, 1, 127, 128, 255, 256, 65535, 65536, 4294967295, 4294967296, 2**63 - 1, 2**63, 2**64 - 1,
        -1, -32, -33, -128, -129, -32768, -32769, -2147483648, -2147483649, -(2**63)]
STRS = ["", "abc", "x" * 31, "x" * 32, "x" * 255, "x" * 256, "x" * 70000, "h\u00e9llo \u2603"]
BINS = [b"", b"\x01\x02", bytes(range(256))[:255], bytes(256), bytes(70000)]
FLOATS = [0.0, 0.5, 1.2345678901, -3.5e300, 1700000000.123456]


def main():
    cases = []
    for i in INTS:
        cases.append({"name": f"int:{i}", "hex": umsgpack.packb(i).hex()})
    for s in STRS:
        cases.append({"name": f"str:{len(s.encode())}:{s[:8]}", "hex": umsgpack.packb(s).hex()})
    for b in BINS:
        cases.append({"name": f"bin:{len(b)}", "hex": umsgpack.packb(b).hex()})
    for f in FLOATS:
        cases.append({"name": f"float:{f!r}", "hex": umsgpack.packb(f).hex()})
    cases.append({"name": "nil", "hex": umsgpack.packb(None).hex()})
    cases.append({"name": "true", "hex": umsgpack.packb(True).hex()})
    cases.append({"name": "false", "hex": umsgpack.packb(False).hex()})
    cases.append({"name": "array:mixed", "hex": umsgpack.packb([1, "a", b"x", None, True, 2.5, [3, 4]]).hex()})
    cases.append({"name": "array:20", "hex": umsgpack.packb(list(range(20))).hex()})
    cases.append({"name": "array:70000", "hex": umsgpack.packb([0] * 70000).hex()})
    cases.append({"name": "map:mixed", "hex": umsgpack.packb({"t": 1, "h": b"\x00\x01", "q": None, "f": 3}).hex()})
    cases.append({"name": "map:20", "hex": umsgpack.packb({str(i): i for i in range(20)}).hex()})
    print(json.dumps({"pinned_sha": PINNED_SHA, "cases": cases}, indent=1))


main()
