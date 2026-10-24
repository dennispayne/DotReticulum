"""Observe the pinned UDP constructor with real ephemeral IPv4 listeners."""
import hashlib
import inspect
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference()
import RNS
from RNS.Interfaces.UDPInterface import UDPInterface


class Owner:
    def inbound(self, data, interface):
        raise AssertionError("The configuration oracle must not receive traffic.")


def observe(name, settings):
    interface = UDPInterface(Owner(), {"name": name, **settings})
    try:
        return {
            "name": name,
            "settings": settings,
            "receives": getattr(interface, "receives", False),
            "forwards": getattr(interface, "forwards", False),
            "bindIp": getattr(interface, "bind_ip", None),
            "bindPort": getattr(interface, "bind_port", None),
            "forwardIp": getattr(interface, "forward_ip", None),
            "forwardPort": getattr(interface, "forward_port", None),
            "online": interface.online,
            "hardwareMtu": interface.HW_MTU,
            "bitrate": interface.bitrate,
        }
    finally:
        if hasattr(interface, "server"):
            interface.server.shutdown()
            interface.detach()


source = Path(inspect.getfile(UDPInterface)).resolve()
cases = [
    ("receive-only", {"listen_ip": "127.0.0.1", "listen_port": 0}),
    ("forward-only", {"forward_ip": "127.0.0.1", "forward_port": 42345}),
    ("global-port-fallback", {
        "port": 42345, "listen_ip": "127.0.0.1", "listen_port": 0,
        "forward_ip": "127.0.0.1",
    }),
    ("explicit-ports-win", {
        "port": 42345, "listen_ip": "127.0.0.1", "listen_port": 0,
        "forward_ip": "127.0.0.1", "forward_port": 42346,
    }),
    ("incomplete-forward", {
        "listen_ip": "127.0.0.1", "listen_port": 0, "forward_ip": "127.0.0.1",
    }),
    ("neither", {}),
]
with tempfile.TemporaryDirectory(prefix="dotreticulum-udp-reference-") as directory:
    Path(directory, "config").write_text(
        "[reticulum]\n  share_instance = no\n[interfaces]\n", encoding="utf-8")
    runtime = RNS.Reticulum(directory, loglevel=RNS.LOG_ERROR)
    try:
        result = {
            "revision": PINNED_RNS_SHA,
            "source": str(source),
            "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "method": "Unmodified UDPInterface constructor; isolated unmodified runtime; real ephemeral loopback listeners; no traffic",
            "cases": [observe(name, settings) for name, settings in cases],
        }
    finally:
        RNS.Reticulum.exit_handler()
target = Path(__file__).parent / "Fixtures" / "parity6-udp-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(target)
