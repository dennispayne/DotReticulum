"""Execute RNS 1.5.7 rnid for independent, disposable public test-key fixtures."""
import argparse
import base64
import contextlib
import hashlib
import io
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference
import RNS
from RNS.Utilities import rnid
from RNS.vendor import umsgpack as mp


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--encrypt")
    parser.add_argument("--decrypt")
    parser.add_argument("--write")
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(range(64)))  # Public, disposable fixture key.
    if args.encrypt or args.decrypt:
        if bool(args.encrypt) == bool(args.decrypt) or not args.write:
            parser.error("Specify one of --encrypt/--decrypt and --write")
        with open(args.encrypt or args.decrypt, "rb") as source, open(args.write, "xb") as target:
            size = rnid.ENC_CHUNK if args.encrypt else rnid.DEC_CHUNK
            while chunk := source.read(size):
                result = identity.encrypt(chunk) if args.encrypt else identity.decrypt(chunk)
                if result is None:
                    raise RuntimeError("Reference decryption failed")
                target.write(result)
        print(f"Reference {'encrypted' if args.encrypt else 'decrypted'} {args.encrypt or args.decrypt}")
        return
    if not args.output:
        parser.error("--output is required for vector generation")
    message = "Parity 7 signed message — λ\n".encode()
    metadata = rnid.rsg_meta_from_str("title = Example\ncount = 3\n[nested]\nvalue = yes\n")
    result = {
        "revision": PINNED_RNS_SHA,
        "module": str(Path(RNS.__file__).resolve()),
        "rnid_sha256": hashlib.sha256(Path(rnid.__file__).read_bytes()).hexdigest(),
        "public": identity.get_public_key().hex(),
        "hash": identity.hexhash,
        "base32": base64.b32encode(identity.get_public_key()).decode(),
        "base64": base64.urlsafe_b64encode(identity.get_public_key()).decode(),
        "base256_alphabet": RNS.b256rep(bytes(range(256))),
        "message": message.hex(),
        "raw": identity.sign(message).hex(),
        "encrypted": identity.encrypt(message).hex(),
        "destination": RNS.Destination.hash_from_name_and_identity("rns.id", identity).hex(),
        "metadata": metadata,
        "metadata_cases": [],
        "cli_cases": [],
        "rsg": rnid.create_rsg(identity, message).hex(),
        "rsm": rnid.create_rsg(identity, message, embed=True, meta=metadata).hex(),
        "wrapped": {},
    }
    altered = rnid.extract_signed_rsg_data(rnid.create_rsg(identity, message))
    altered["meta"]["pubkey"] = b"not a usable embedded key"
    envelope = mp.packb(altered)
    result["explicit_identity_override_rsg"] = (identity.sign(envelope) + envelope).hex()
    result["explicit_identity_override_valid"] = rnid.validate_rsg(identity.sign(envelope) + envelope, message, identity)[0]
    for encoding in ["hex", "base32", "base64", "base256"]:
        rsg = rnid.create_rsg(identity, message, embed=True, output=encoding)
        result["wrapped"][encoding] = rnid.wrap_rsg(rsg)
    for text, spec in [
        ("root = Example\n[child]\nvalue = %(root)s suffix\n", None),
        ("[DEFAULT]\nname=Example\n[child]\nvalue=%(name)s\n", None),
        ("x=3,4\nb=yes,no\nips=127.0.0.1,10.1.1.1\n", 'x="int_list(1,3)"\nb=bool_list()\nips=ip_addr_list()\n'),
        ("mixed=3, yes, sample\n", 'mixed="mixed_list(integer, boolean, string)"\n'),
        ("", 'count=integer(default=3)\nitems="string_list(default=list(a,b))"\n'),
    ]:
        result["metadata_cases"].append({"text": text, "spec": spec, "result": rnid.rsg_meta_from_str(text, spec)})
    for arguments in [
        ["-m", identity.get_public_key().hex(), "-p"],
        ["-M", bytes(range(64)).hex(), "-p", "-P", "-B"],
        ["-m", base64.urlsafe_b64encode(identity.get_public_key()).decode(), "-x", "-b"],
        ["-M", bytes(range(64)).hex(), "-X", "-B"],
        ["-M", bytes(range(64)).hex(), "-H", "rns.id"],
        ["-i", identity.hexhash, "-N", "-H", "rns.id"],
        ["-b", "-B"],
        ["-s", "fixture", "-e", "fixture"],
        ["--version"],
        ["-vm" + identity.get_public_key().hex(), "-xp"],
        ["-M" + bytes(range(64)).hex(), "-pPB"],
        ["--import-pub=" + identity.get_public_key().hex(), "--print-i"],
        ["--import-pub", identity.get_public_key().hex(), "--export-pu", "--base3"],
        ["-M" + bytes(range(64)).hex(), "-Scluster message", "-b"],
    ]:
        stdout = io.StringIO()
        stderr = io.StringIO()
        previous = sys.argv
        sys.argv = ["rnid", *arguments]
        try:
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                try:
                    rnid.main()
                    code = 0
                except SystemExit as exited:
                    code = exited.code
        finally:
            sys.argv = previous
        result["cli_cases"].append({"arguments": arguments, "stdout": stdout.getvalue(),
                                    "stderr": stderr.getvalue(), "code": code})
    result["parser_errors"] = []
    for arguments in [["--base"], ["--import-"], ["-xp=invalid"], ["-i", "-p"]]:
        stdout, stderr = io.StringIO(), io.StringIO()
        previous = sys.argv
        sys.argv = ["rnid", *arguments]
        try:
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                try:
                    rnid.main()
                    code = 0
                except SystemExit as exited:
                    code = exited.code
        finally:
            sys.argv = previous
        result["parser_errors"].append({"arguments": arguments, "code": code,
                                        "diagnostic": stderr.getvalue().split("rnid: error: ")[-1].strip()})
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Verified {RNS.__version__} at {RNS.__file__}; wrote {args.output}")


if __name__ == "__main__":
    main()
