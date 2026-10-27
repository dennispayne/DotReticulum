"""Execute the verified live pin; write only the separate application-value fixture.

Set DOTRETICULUM_UPSTREAM and PYTHONPATH to the materialized RNS checkout.
Run with the isolated refvenv interpreter from the repository root.
"""
import datetime
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference(require_lxmf=True)
import RNS
import LXMF
from RNS.vendor import umsgpack


def main():
    cases = []

    def add(name, value, **options):
        packed = umsgpack.packb(value, **options)
        cases.append({
            "name": name,
            "hex": packed.hex() if len(packed) < 1024 else None,
            "length": len(packed),
            "sha256": hashlib.sha256(packed).hexdigest(),
        })

    for n in [0, 1, 2, 3, 4, 8, 16, 17, 255, 256, 65535, 65536]:
        add(f"ext:{n}", umsgpack.Ext(-42, bytes(i % 256 for i in range(n))))
    for value in [-128, 0, 127]:
        add(f"exttype:{value}", umsgpack.Ext(value, b"\x01"))
    epoch = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
    for seconds, micros in [(0, 0), (2**32 - 1, 0), (2**32, 0),
                            (123456, 123456), (2**34 - 1, 999999),
                            (2**34, 0), (-1, 0), (-1, 123456)]:
        add(f"timestamp:{seconds}:{micros * 1000}",
            epoch + datetime.timedelta(seconds=seconds, microseconds=micros))
    for n in [31, 32, 255, 256, 65535, 65536]:
        add(f"str:{n}", "x" * n)
        add(f"bin:{n}", bytes(n))
    for n in [15, 16, 65535, 65536]:
        add(f"array:{n}", [None] * n)
        add(f"map:{n}", {i: None for i in range(n)})
    add("unicode", "héllo ☃ 😀")
    add("binary-text-keys", {b"\xab": 1, "AB": 2})
    add("scalar-keys", {False: "bool", -3: "int", 2**63: "uint",
                        1.5: "float", b"x": "bin", "x": "str",
                        umsgpack.Ext(7, b"x"): "ext"})
    for name, value in [("negative-zero", -0.0), ("nan", float("nan")),
                        ("positive-infinity", float("inf")),
                        ("negative-infinity", -float("inf"))]:
        add(name, value)
    add("single", 1.5, force_float_precision="single")
    rejected = []
    for name, packed in [
        ("invalid-utf8", b"\xa1\xff"),
        ("duplicate-text", b"\x82\xa1a\x01\xa1a\x02"),
        ("duplicate-binary", b"\x82\xc4\x01x\x01\xc4\x01x\x02"),
        ("duplicate-numeric", b"\x82\xc3\x01\x01\x02"),
        ("timestamp-length", b"\xd4\xff\x00"),
        ("reserved", b"\xc1"),
        ("truncated-bin", b"\xc6\xff\xff\xff\xff"),
        ("truncated-array", b"\xdd\xff\xff\xff\xff"),
        ("truncated-map", b"\xdf\xff\xff\xff\xff"),
    ]:
        try:
            umsgpack.unpackb(packed)
        except Exception as error:
            rejected.append({"name": name, "hex": packed.hex(),
                             "reference_error": type(error).__name__})
        else:
            raise RuntimeError(f"Expected upstream rejection: {name}")
    observations = []
    for name, value in [("null-key", {None: 1}), ("tuple-key", {(1, 2): 3})]:
        packed = umsgpack.packb(value)
        decoded = umsgpack.unpackb(packed)
        observations.append({"name": name, "hex": packed.hex(),
                             "reference_type": type(next(iter(decoded))).__name__})
    extended = []
    for name, value in [
        ("null", {None: "nil"}),
        ("tuple", {(1, 2): "tuple"}),
        ("nested-tuple", {(None, (1, 2), b"x"): "nested"}),
        ("positive-infinity", {float("inf"): "infinity"}),
        ("negative-infinity", {-float("inf"): "infinity"}),
        ("nan", {float("nan"): "nan"}),
        ("distinct-nans", {float("nan"): 1, float("nan"): 2}),
        ("mixed", {"normal": {None: 1}, b"x": {(True, 2.5): [None, 3]}}),
    ]:
        packed = umsgpack.packb(value)
        decoded = umsgpack.unpackb(packed)
        extended.append({
            "name": name, "hex": packed.hex(),
            "repacked_hex": umsgpack.packb(decoded).hex(),
            "entries": len(decoded),
        })
    output = {
        "pinned_sha": PINNED_RNS_SHA,
        "source_sha256": hashlib.sha256(Path(umsgpack.__file__).read_bytes()).hexdigest(),
        "cases": cases, "rejected": rejected, "intentional_restrictions": observations,
        "extended_map_keys": extended,
    }
    target = Path(__file__).parent / "Fixtures" / "msgpack-application-values-a29bfb51.json"
    target.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"RNS: {RNS.__file__}\nLXMF: {LXMF.__file__}\numsgpack: {umsgpack.__file__}")
    print(f"Wrote {len(cases)} vectors, {len(rejected)} rejections, "
          f"{len(observations)} restrictions to {target}")


if __name__ == "__main__":
    main()
