"""Execute pinned RNS rnsh codecs; never derive expected bytes from .NET."""
import hashlib
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference

verify_reference(require_lxmf=True)
from RNS.Utilities.rnsh import protocol

messages = [
    protocol.NoopMessage(),
    protocol.VersionInfoMessage("0.0.0"),
    protocol.WindowSizeMessage(24, 80, 640, 480),
    protocol.ExecuteCommandMesssage(["echo", "snow ☃"], True, True, True,
                                   None, "xterm", 24, 80, 0, 0),
    protocol.StreamDataMessage(0, b"\x00\xffstdin", False),
    protocol.StreamDataMessage(1, b"", True),
    protocol.StreamDataMessage(2, b"stderr", True),
    protocol.ErrorMessage("Unable to start process", True, {"reason": "spawn"}),
    protocol.CommandExitedMessage(-15),
]
source = Path(protocol.__file__)
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
    "vectors": [{"name": type(m).__name__, "type": m.MSGTYPE,
                 "hex": m.pack().hex()} for m in messages],
}
target = Path(__file__).parent / "Fixtures" / "rnsh-vectors.json"
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"{len(messages)} rnsh vectors; {source}; SHA256 {result['source_sha256']}")
