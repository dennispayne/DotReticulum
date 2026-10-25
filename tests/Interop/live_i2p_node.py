"""Thin process adapter around the actual pinned RNS I2PInterface.

Creates an isolated ordinary RNS configuration and observes a PLAIN
destination callback. Uses unmodified upstream SAM/tunnel/framing code;
no router, socket proxy, readiness/tunnel-option shim or packet oracle.
The C# test owns topology, assertions, process deadline and cleanup.
"""
import argparse
import hashlib
import importlib.metadata
import inspect
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading

sys.dont_write_bytecode = True
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference(require_lxmf=True)
import LXMF
import RNS
from RNS.Interfaces.I2PInterface import I2PInterface, I2PInterfacePeer

_output_lock = threading.Lock()


def emit(observation):
    with _output_lock:
        print("@@ " + json.dumps(observation, separators=(",", ":")), flush=True)


def peers(interface):
    return [peer for peer in list(RNS.Transport.interfaces)
            if isinstance(peer, I2PInterfacePeer) and peer.parent_interface is interface]


def snapshot(interface):
    return {
        "online": bool(interface.online),
        "b32": interface.b32 + ".b32.i2p" if interface.b32 else None,
        "peers": [{"online": bool(peer.online), "initiator": bool(peer.initiator),
                   "bitrate": peer.bitrate, "hardware_mtu": peer.HW_MTU}
                  for peer in peers(interface)],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--sam", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--peer")
    parser.add_argument("--connectable", action="store_true")
    parser.add_argument("--resume-directory", action="store_true")
    parser.add_argument("--reverse-aspects", action="store_true")
    parser.add_argument("--static-transport-identity", action="store_true")
    args = parser.parse_args()
    host, port = args.sam.rsplit(":", 1)
    if not ipaddress.ip_address(host).is_loopback or ":" in host or not 0 < int(port) < 65536:
        raise ValueError("This adapter only accepts an explicitly owned IPv4 loopback SAM endpoint")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,64}", args.name):
        raise ValueError("Adapter name must be a safe test identifier")
    if args.peer and not re.fullmatch(r"[a-z2-7]{52}\.b32\.i2p", args.peer):
        raise ValueError("Adapter peer must be an I2P base32 destination")
    if bool(args.peer) == args.connectable:
        raise ValueError("Select exactly one role: connectable server or configured peer")
    os.environ["I2P_SAM_ADDRESS"] = args.sam
    if RNS.__version__ != "1.5.7":
        raise RuntimeError("Unexpected RNS version")
    actual = Path(inspect.getfile(I2PInterface)).resolve()
    expected = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve() / "RNS" / "Interfaces" / "I2PInterface.py"
    if actual != expected:
        raise RuntimeError("I2PInterface imported from outside the verified checkout")
    source_bytes = actual.read_bytes()
    git_blob = subprocess.run(
        ["git", "-C", str(expected.parents[2]), "cat-file", "blob",
         f"{PINNED_RNS_SHA}:RNS/Interfaces/I2PInterface.py"],
        check=True, capture_output=True,
    ).stdout
    canonical_source = source_bytes.replace(b"\r\n", b"\n")
    if canonical_source != git_blob.replace(b"\r\n", b"\n"):
        raise RuntimeError("Actual imported I2PInterface differs from the pinned Git blob beyond checkout line endings")
    directory = Path(args.config).resolve()
    if args.resume_directory:
        if not directory.is_dir() or not (directory / "config").is_file():
            raise ValueError("Resume requires an existing owned reference configuration")
    else:
        directory.mkdir(parents=True, exist_ok=False)
    config = (
        "[reticulum]\n"
        "  enable_transport = No\n"
        + ("  static_transport_identity = Yes\n" if args.static_transport_identity else "")
        + "  share_instance = No\n"
        "  panic_on_interface_error = No\n"
        "[logging]\n"
        "  loglevel = 2\n"
        "[interfaces]\n"
        "  [[I2PLive]]\n"
        "    type = I2PInterface\n"
        "    enabled = Yes\n"
        f"    connectable = {'Yes' if args.connectable else 'No'}\n"
        + (f"    peers = {args.peer}\n" if args.peer else "")
    )
    if args.resume_directory:
        if (directory / "config").read_text(encoding="utf-8") != config:
            raise ValueError("Resume configuration must match the original role and peer")
    else:
        (directory / "config").write_text(config, encoding="utf-8")
    RNS.Reticulum(configdir=str(directory), loglevel=RNS.LOG_WARNING)
    matches = [interface for interface in RNS.Transport.interfaces
               if isinstance(interface, I2PInterface) and interface.name == "I2PLive"]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one actual configured I2PInterface, found {len(matches)}")
    interface = matches[0]
    destination = RNS.Destination(None, RNS.Destination.IN, RNS.Destination.PLAIN,
                                  "dotreticulum", "interop", "i2p",
                                  "native" if args.reverse_aspects else "python", args.name)
    native_destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN,
                                         "dotreticulum", "interop", "i2p",
                                         "python" if args.reverse_aspects else "native", args.name)

    def received(payload, packet):
        source = packet.receiving_interface
        emit({"event": "data", "payload": payload.hex(),
              "interface_type": type(source).__name__,
              "initiator": bool(source.initiator),
              "destination": packet.destination_hash.hex()})

    destination.set_packet_callback(received)
    emit({
        "event": "ready", "revision": PINNED_RNS_SHA,
        "rns_version": RNS.__version__, "rns_module": str(Path(RNS.__file__).resolve()),
        "i2p_module": str(actual), "i2p_sha256": hashlib.sha256(source_bytes).hexdigest(),
        "i2p_canonical_lf_sha256": hashlib.sha256(canonical_source).hexdigest(),
        "i2p_git_blob_sha256": hashlib.sha256(git_blob).hexdigest(),
        "lxmf_version": importlib.metadata.version("lxmf"),
        "lxmf_module": str(Path(LXMF.__file__).resolve()),
        "dest": destination.hash.hex(), "native_dest": native_destination.hash.hex(),
        "connectable": args.connectable, "sam": args.sam,
        "ifac_enabled": False, **snapshot(interface),
        "resume_directory": args.resume_directory, "reverse_aspects": args.reverse_aspects,
        "static_transport_identity": RNS.Reticulum.static_transport_identity(),
        "transport_identity_hash": RNS.Transport.identity.hash.hex(),
    })
    try:
        for line in sys.stdin:
            command = None
            try:
                if len(line) > 4096:
                    raise ValueError("Adapter command exceeds bound")
                command = json.loads(line)
                request = command["request"]
                operation = command["command"]
                if operation == "stats":
                    emit({"event": "stats", "request": request, **snapshot(interface)})
                elif operation == "send":
                    payload = bytes.fromhex(command["payload"])
                    if not 0 < len(payload) <= 450:
                        raise ValueError("Adapter payload must be 1..450 bytes")
                    online = [peer for peer in peers(interface) if peer.online]
                    if len(online) != 1:
                        raise RuntimeError(f"Expected exactly one online actual I2P peer, found {len(online)}")
                    packet = RNS.Packet(native_destination, payload, attached_interface=online[0],
                                        create_receipt=False)
                    packet.send()
                    emit({"event": "sent", "request": request, "sent": bool(packet.sent),
                          "initiator": bool(online[0].initiator)})
                elif operation == "quit":
                    emit({"event": "quit", "request": request})
                    break
                else:
                    raise ValueError("Unsupported adapter operation")
            except Exception as error:
                emit({"event": "error", "request": command.get("request") if isinstance(command, dict) else None,
                      "message": str(error)})
    finally:
        # The pinned controller has documented shutdown limitations. The C#
        # owner also imposes a process deadline; no upstream workaround here.
        for peer in peers(interface):
            peer.detach()
        interface.detach()


if __name__ == "__main__":
    main()
