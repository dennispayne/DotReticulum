"""Execute the pinned rnid RSG and rngcs CLI, without reimplementing either.

Only disposable, explicitly public test keys are used. The adapter validates
actual imports, records source digests, and removes only its owned fixture.
These are differential fixtures, not live-network interoperability evidence.
"""
import argparse
import contextlib
import hashlib
import io
import json
from pathlib import Path
import shutil
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities import rnid
from RNS.Utilities.rngit import commitsigs
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    root = Path(__file__).resolve().parents[2] / "TestResults" / ("rngcs-vectors-" + uuid.uuid4().hex)
    root.mkdir(parents=True)
    try:
        private = bytes(range(64))
        identity = RNS.Identity(create_keys=False)
        identity.load_private_key(private)
        other = RNS.Identity(create_keys=False)
        other.load_private_key(bytes(reversed(range(64))))
        signer = identity.hexhash
        commit = f"tree {'a' * 40}\nauthor Fixture <{signer}> 1 +0000\ncommitter Other <ignored@example.invalid> 1 +0000\n\nfixture\n".encode()
        tag = f"object {'a' * 40}\ntype commit\ntag fixture\ntagger Fixture <{signer}> 1 +0000\n\nfixture\n".encode()
        messages = {
            "commit": commit, "tag": tag, "binary": b"\x00\xff\r\nbinary\x80",
            "empty": b"", "wrong-author": commit.replace(signer.encode(), b"wrong@example.invalid"),
            "wrong-tagger": tag.replace(signer.encode(), b"wrong@example.invalid"),
            "crlf": commit.replace(b"\n", b"\r\n"),
            "cr": commit.replace(b"\n", b"\r"),
            "body-author": b"tree " + b"a" * 40 + b"\n\n" + commit,
        }
        signatures = {}
        rsg_vectors = []
        for name, message in messages.items():
            rsg = rnid.create_rsg(identity, message)
            signatures[name] = commitsigs.armor_ssh_signature(commitsigs.create_ssh_signature(
                commitsigs.get_pubkey_wire_format(identity), b"git", b"", b"sha256", rsg))
            rsg_vectors.append({"name": name, "message": message.hex(), "rsg": rsg.hex(),
                                "armor": signatures[name], "embed": False, "meta": None})
        metadata = {"release": "v1.0", "description": "public fixture", "signer": other.hash, "pubkey": other.get_public_key()}
        embedded = rnid.create_rsg(identity, b"release artifact", embed=True, meta=metadata)
        rsg_vectors.append({"name": "release", "message": b"release artifact".hex(), "rsg": embedded.hex(),
                           "embed": True, "meta": {"release": "v1.0", "description": "public fixture"}})

        baseline = rnid.create_rsg(identity, commit)
        variants = {
            "tampered": bytes([baseline[0] ^ 1]) + baseline[1:],
            "legacy": baseline[:64],
            "truncated-rsg": baseline[:65],
            "malformed-envelope": bytes(64) + b"\xdf\xff\xff\xff\xff",
            "structure-only": bytes(64) + mp.packb({"not": "a signature"}),
            "truthy-array": bytes(64) + mp.packb([1]),
            "empty-map": bytes(64) + mp.packb({}),
        }
        for name, change in [("wrong-hash", ("hash", bytes(32))), ("unsupported-hash", ("hashtype", "sha512")),
                             ("claimed-other-signer", ("meta", {"signer": other.hash, "pubkey": identity.get_public_key()})),
                             ("invalid-key", ("meta", {"signer": identity.hash, "pubkey": bytes(64)})),
                             ("missing-signer", ("meta", {"pubkey": identity.get_public_key()}))]:
            data = rnid.extract_signed_rsg_data(baseline)
            data[change[0]] = change[1]
            envelope = mp.packb(data)
            variants[name] = identity.sign(envelope) + envelope
        original_envelope = baseline[64:]
        duplicate = bytes([original_envelope[0] + 1]) + original_envelope[1:] + mp.packb("hashtype") + mp.packb("sha256")
        variants["duplicate-key"] = identity.sign(duplicate) + duplicate
        invalid_utf8 = mp.packb({"hashtype": "sha256", "hash": rnid.get_rsg_hash(commit),
                                "meta": {"signer": identity.hash, "pubkey": identity.get_public_key()},
                                "extra": "unique-marker"}).replace(b"unique-marker", b"\xff" * len("unique-marker"))
        variants["invalid-utf8"] = identity.sign(invalid_utf8) + invalid_utf8
        trailing = original_envelope + b"trailing"
        variants["trailing-envelope"] = identity.sign(trailing) + trailing
        for name, encryption_key in [("hardened-low-order-key", bytes(32)),
                                     ("hardened-noncanonical-key", b"\xff" * 32)]:
            data = rnid.extract_signed_rsg_data(baseline)
            data["meta"]["pubkey"] = encryption_key + identity.get_public_key()[32:]
            envelope = mp.packb(data)
            variants[name] = identity.sign(envelope) + envelope
        for name, rsg in variants.items():
            signatures[name] = commitsigs.armor_ssh_signature(commitsigs.create_ssh_signature(
                commitsigs.get_pubkey_wire_format(identity), b"git", b"", b"sha256", rsg))
        for name, namespace, public, reserved, hash_algorithm in [
            ("wrong-namespace", b"file", commitsigs.get_pubkey_wire_format(identity), b"", b"sha256"),
            ("quoted-namespace", b"a'b", b"", b"", b"sha256"),
            ("control-namespace", b"\n\r\t\x00\xff", b"", b"", b"sha256"),
            ("ignored-outer-fields", b"git", b"not a public key", b"reserved", b"sha512"),
        ]:
            signatures[name] = commitsigs.armor_ssh_signature(commitsigs.create_ssh_signature(
                public, namespace, reserved, hash_algorithm, baseline))
        raw = commitsigs.unarmor_ssh_signature(signatures["commit"])
        signatures["trailing-outer"] = commitsigs.armor_ssh_signature(raw + b"trailing")
        signatures["version"] = commitsigs.armor_ssh_signature(raw[:6] + b"\x00\x00\x00\x02" + raw[10:])
        signatures["truncated-ssh"] = commitsigs.armor_ssh_signature(raw[:12])
        signatures["no-armor"] = "not a signature"
        signatures["non-alphabet-armor"] = signatures["commit"].replace("\n", "\n!?\t")
        signatures["extra-padding-armor"] = signatures["commit"].replace("\n-----END", "====\n-----END")
        signatures["post-padding-armor"] = signatures["commit"].replace("\n-----END", "ignored\n-----END")
        signatures["missing-footer-armor"] = signatures["commit"].split("-----END")[0]
        (root / "key").write_bytes(private)
        (root / "file").write_bytes(commit)
        cases = []

        def run(name, argv, message=b"", signature=None):
            if signature is not None:
                (root / "sig").write_text(signature, encoding="utf-8")
            resolved = [str(root / arg[1:-1]) if arg.startswith("{") and arg.endswith("}") else arg for arg in argv]
            output, error = io.StringIO(), io.StringIO()
            old_argv, old_stdin = sys.argv, sys.stdin
            try:
                sys.argv = ["rngcs"] + resolved
                sys.stdin = io.TextIOWrapper(io.BytesIO(message))
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
                    try:
                        code = commitsigs.main()
                    except SystemExit as ex:
                        code = ex.code
                    except Exception as ex:
                        code = 1
                        error.write("UPSTREAM_EXCEPTION:" + type(ex).__name__ + ":" + str(ex))
                cases.append({"name": name, "args": argv, "message": message.hex(), "signature": signature,
                              "code": code, "output": output.getvalue().replace(str(root), "{root}"),
                              "error": error.getvalue().replace(str(root), "{root}")})
            finally:
                sys.argv, sys.stdin = old_argv, old_stdin

        for name, signature in signatures.items():
            for operation in ["find-principals", "check-novalidate", "verify"]:
                message = messages.get(name, commit)
                run(name + "-" + operation, ["-Y", operation, "-s", "{sig}"], message, signature)
                if name.startswith("hardened-") and operation == "verify":
                    cases[-1]["intentional_key_hardening"] = True
        run("principal-correct", ["-Y", "verify", "-s", "{sig}", "-I", signer], commit, signatures["commit"])
        run("principal-wrong", ["-Y", "verify", "-s", "{sig}", "-I", other.hexhash], commit, signatures["commit"])
        run("ignored-options", ["-Y", "verify", "-s", "{sig}", "-n", "not-git", "-f", "{missing}",
                               "-O", "verify-time=19000101", "-O", "anything"], commit, signatures["commit"])
        run("tampered-message", ["-Y", "verify", "-s", "{sig}"], commit + b"changed", signatures["commit"])
        run("missing-key", ["-Y", "sign", "-f", "{missing}"], commit)
        for operation in ["verify", "find-principals", "check-novalidate"]:
            run("missing-signature-" + operation, ["-Y", operation, "-s", "{missing}"], commit)
        for name in ["commit", "binary", "empty"]:
            run("sign-" + name, ["-Y", "sign", "-f", "{key}"], messages[name])
        run("sign-ignored-namespace", ["-Ysign", "-f", "{key}", "-n", "not-git", "-Oa", "-Ob"], commit)
        run("sign-missing-positional", ["-Y", "sign", "-f", "{key}", "{missing}"], commit)
        run("sign-file", ["-Y", "sign", "-f", "{key}", "{file}"], b"not the file")
        cases[-1]["file_signature"] = (root / "file.sig").read_text(encoding="utf-8")
        for name, argv in [
            ("parse-invalid-operation", ["-Y", "unknown"]),
            ("parse-missing-value", ["-Y", "sign", "-f"]),
            ("parse-missing-operation", []),
            ("parse-unknown-before-required", ["-bad"]),
            ("parse-unknown", ["-Y", "sign", "-bad"]),
            ("parse-extra-positional", ["-Y", "sign", "{file}", "extra"]),
        ]:
            run(name, argv)
        run("negative-namespace", ["-Y", "sign", "-f", "{key}", "-n", "-1"], commit)
        run("equal-option", ["-Y=sign", "-f", "{key}"], commit)

        validations = []
        for name, rsg in {"baseline": baseline, **variants}.items():
            for required_name, required in [("none", None), ("correct", identity.hash), ("other", other.hash)]:
                try:
                    valid, _, signing_identity = rnid.validate_rsg(rsg, commit, required_signer=required)
                    exception = None
                except Exception as ex:
                    valid, signing_identity, exception = False, None, type(ex).__name__
                validations.append({"name": name, "rsg": rsg.hex(), "message": commit.hex(),
                                    "required": None if required is None else required.hex(),
                                    "required_name": required_name, "valid": valid, "exception": exception,
                                    "intentional_key_hardening": name.startswith("hardened-")})
        result = {
            "upstream": PINNED_RNS_SHA, "rns_version": RNS.__version__,
            "actual_rns_module": str(Path(RNS.__file__).resolve()),
            "commitsigs_sha256": hashlib.sha256(Path(commitsigs.__file__).read_bytes()).hexdigest(),
            "rnid_sha256": hashlib.sha256(Path(rnid.__file__).read_bytes()).hexdigest(),
            "public_test_private_key": private.hex(), "signer": signer,
            "rsg_vectors": rsg_vectors, "validations": validations, "cli": cases,
        }
        Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(f"Executed {len(cases)} CLI cases, {len(validations)} validation cases, {len(rsg_vectors)} signing vectors; RNS {RNS.__version__} {PINNED_RNS_SHA}")
        print(f"Actual RNS import: {RNS.__file__}")
    finally:
        shutil.rmtree(root)


if __name__ == "__main__":
    main()
