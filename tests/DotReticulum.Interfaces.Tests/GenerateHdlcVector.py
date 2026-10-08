"""Generate the HDLC test vector from upstream's actual framing method.

Run: python tests/DotReticulum.Interfaces.Tests/GenerateHdlcVector.py
The script extracts and executes HDLC.escape from the pinned upstream revision.
"""

import ast
import hashlib
import urllib.request


REVISION = "e40191b3d193b46b7f2d8a44424a594cd758839b"
URL = (
    "https://raw.githubusercontent.com/markqvist/Reticulum/"
    f"{REVISION}/RNS/Interfaces/TCPInterface.py"
)

with urllib.request.urlopen(URL) as response:
    source = response.read()

hdlc = next(
    node for node in ast.parse(source).body
    if isinstance(node, ast.ClassDef) and node.name == "HDLC"
)
hdlc.body = [
    node for node in hdlc.body
    if isinstance(node, ast.Assign)
    or isinstance(node, ast.FunctionDef) and node.name == "escape"
]
namespace = {}
module = ast.fix_missing_locations(ast.Module(body=[hdlc], type_ignores=[]))
exec(compile(module, URL, "exec"), namespace)

packet = bytes([0, 0]) + bytes(16) + bytes([0, 0x7d, 0x7e])
frame = bytes([namespace["HDLC"].FLAG]) + namespace["HDLC"].escape(packet) + bytes(
    [namespace["HDLC"].FLAG]
)
print(f"# {URL} SHA256={hashlib.sha256(source).hexdigest()}")
print(f'packet "{packet.hex()}"')
print(f'hdlc "{frame.hex()}"')
