"""Observe the pinned outgoing selection and direct-transmit distinction."""
import hashlib
import inspect
import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference()
import RNS


class RecordingBearer:
    def __init__(self, outgoing):
        self.OUT = outgoing
        self.online = True
        self.frames = []

    def process_outgoing(self, raw):
        self.frames.append(bytes(raw))


def observe(branch, outgoing):
    bearer = RecordingBearer(outgoing)
    RNS.Transport.interfaces = [bearer]
    identity = RNS.Identity() if branch == "known-path" else None
    destination = RNS.Destination(
        identity, RNS.Destination.OUT,
        RNS.Destination.SINGLE if identity else RNS.Destination.PLAIN,
        "dotreticulum", "outgoing")
    packet = RNS.Packet(destination, b"selection-oracle", create_receipt=False)
    packet.pack()
    if branch == "attached":
        packet.attached_interface = bearer
    if branch == "known-path":
        RNS.Transport.path_table[destination.hash] = [
            time.time(), bytes(16), 1, time.time() + 3600, [], bearer, bytes(32)]
    try:
        if branch == "direct":
            RNS.Transport.transmit(bearer, packet.raw)
        else:
            RNS.Transport._outbound(packet)
        return {"branch": branch, "outgoing": outgoing, "frames": len(bearer.frames)}
    finally:
        RNS.Transport.path_table.pop(destination.hash, None)
        RNS.Transport.interfaces = []


source = Path(inspect.getfile(RNS.Transport)).resolve()
with tempfile.TemporaryDirectory(prefix="dotreticulum-outgoing-reference-") as directory:
    Path(directory, "config").write_text(
        "[reticulum]\nshare_instance = no\n[interfaces]\n", encoding="utf-8")
    runtime = RNS.Reticulum(directory, loglevel=RNS.LOG_ERROR)
    try:
        result = {
            "revision": PINNED_RNS_SHA,
            "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "method": "Unmodified Transport._outbound/transmit; recording bearer; isolated runtime; no network",
            "cases": [observe(branch, outgoing)
                      for branch in ("broadcast", "attached", "known-path", "direct")
                      for outgoing in (False, True)],
        }
    finally:
        RNS.Reticulum.exit_handler()
target = Path(__file__).parent / "Fixtures" / "parity6-outgoing.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
