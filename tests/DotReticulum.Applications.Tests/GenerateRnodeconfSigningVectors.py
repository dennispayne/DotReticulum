"""Independently create public EEPROM verification vectors and execute upstream parser."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
from RNS.Utilities import rnodeconf as reference
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

private = rsa.generate_private_key(public_exponent=65537, key_size=1024)
public = private.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
info = bytes([0x10, 0x12, 1]) + struct.pack(">II", 42, 1700000000)
checksum = hashlib.md5(info).digest()
signature = private.sign(checksum, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=padding.PSS.MAX_LENGTH), hashes.SHA256())
image = info + checksum + signature + bytes([0x73]) + bytes(44)
node = reference.RNode(None)
node.eeprom = image
captured = io.StringIO()
with patch.object(reference, "known_keys", [["CAPTURE-ONLY", public.hex()]]), patch("os.path.isdir", return_value=False):
    with contextlib.redirect_stdout(captured):
        node.parse_eeprom()
if not node.signature_valid or not node.provisioned:
    raise RuntimeError("Upstream did not accept generated EEPROM signature")
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "source_sha256": hashlib.sha256(Path(reference.__file__).read_bytes()).hexdigest(),
    "adapter": "Ephemeral RSA private key discarded; upstream parser given public capture-only signer; filesystem key discovery suppressed.",
    "eeprom": image.hex(), "public_key": public.hex(), "checksum": checksum.hex(),
    "signature_valid": node.signature_valid, "vendor": node.vendor,
}
target = Path(__file__).parent / "Fixtures" / "rnodeconf-signing-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(f"Verified import {reference.__file__}; upstream parser accepted RSA-PSS MAX_LENGTH EEPROM; no private key persisted")
