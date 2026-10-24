"""Observation-only adapter for the pinned, real upstream AutoInterface.

No bearer implementation, socket proxy, timer override or peer-table shim.
The coordinator owns topology; the test owns this process and its config.
Uses live_link_node.emit only for the existing @@ JSON observation framing.
"""
import argparse
import hashlib
import importlib.metadata
import inspect
import json
import os
from pathlib import Path
import re
import sys
import subprocess

sys.dont_write_bytecode = True
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference(require_lxmf=True)
import LXMF
import RNS
from RNS.Interfaces.AutoInterface import AutoInterface
from live_link_node import emit


def snapshot(interface):
    return {
        "online": bool(interface.online),
        "detached": bool(interface.detached),
        "devices": dict(interface.adopted_interfaces),
        "peers": [
            {
                "address": address,
                "device": peer.ifname,
                "online": bool(peer.online),
                "rxb": peer.rxb,
                "txb": peer.txb,
                "ifac_size": getattr(peer, "ifac_size", None),
            }
            for address, peer in list(interface.spawned_interfaces.items())
        ],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--peer", required=True)
    parser.add_argument("--group", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--discovery-port", required=True, type=int)
    parser.add_argument("--data-port", required=True, type=int)
    parser.add_argument("--ifac-name")
    parser.add_argument("--ifac-passphrase")
    args = parser.parse_args()
    for value in [args.device, args.group, args.name, args.ifac_name or "", args.ifac_passphrase or ""]:
        if not re.fullmatch(r"[A-Za-z0-9_-]*", value):
            raise ValueError("Adapter configuration identifiers must be single-line safe test identifiers")

    expected_source = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve() / "RNS" / "Interfaces" / "AutoInterface.py"
    actual_source = Path(inspect.getfile(AutoInterface)).resolve()
    if actual_source != expected_source:
        raise RuntimeError(f"AutoInterface imported from {actual_source}, not {expected_source}")
    source_bytes = actual_source.read_bytes()
    git_blob = subprocess.run(
        ["git", "-C", str(expected_source.parents[2]), "cat-file", "blob",
         f"{PINNED_RNS_SHA}:RNS/Interfaces/AutoInterface.py"],
        check=True, capture_output=True,
    ).stdout
    canonical_source = source_bytes.replace(b"\r\n", b"\n")
    if canonical_source != git_blob.replace(b"\r\n", b"\n"):
        raise RuntimeError("Actual imported AutoInterface differs from the pinned Git blob beyond checkout line endings")
    config_dir = Path(args.config).resolve()
    config_dir.mkdir(parents=True, exist_ok=False)
    ifac = ""
    if args.ifac_name:
        ifac = (f"    network_name = {args.ifac_name}\n"
                f"    passphrase = {args.ifac_passphrase}\n"
                "    ifac_size = 128\n")
    config = (
        "[reticulum]\n"
        "  enable_transport = No\n"
        "  share_instance = No\n"
        "  panic_on_interface_error = No\n"
        "[logging]\n"
        "  loglevel = 4\n"
        "[interfaces]\n"
        "  [[AutoLive]]\n"
        "    type = AutoInterface\n"
        "    enabled = Yes\n"
        f"    devices = {args.device}\n"
        f"    group_id = {args.group}\n"
        f"    discovery_port = {args.discovery_port}\n"
        f"    data_port = {args.data_port}\n"
        + ifac
    )
    (config_dir / "config").write_text(config, encoding="utf-8")
    RNS.Reticulum(configdir=str(config_dir), loglevel=RNS.LOG_WARNING)
    matches = [i for i in RNS.Transport.interfaces if isinstance(i, AutoInterface) and i.name == "AutoLive"]
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly one real AutoInterface, found {len(matches)}")
    interface = matches[0]
    if args.device not in interface.adopted_interfaces:
        raise RuntimeError(f"Reference did not adopt required device {args.device}")
    destination = RNS.Destination(None, RNS.Destination.IN, RNS.Destination.PLAIN,
                                  "dotreticulum", "interop", "auto", "python", args.name)
    native_destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN,
                                         "dotreticulum", "interop", "auto", "native", args.name)

    def received(payload, packet):
        incoming = packet.receiving_interface
        emit({
            "event": "data",
            "payload": payload.hex(),
            "source": getattr(incoming, "addr", None),
            "device": getattr(incoming, "ifname", None),
            "interface_type": type(incoming).__name__,
            "ifac_size": getattr(incoming, "ifac_size", None),
            "destination": packet.destination_hash.hex(),
        })

    destination.set_packet_callback(received)
    emit({
        "event": "ready",
        "revision": PINNED_RNS_SHA,
        "rns_version": RNS.__version__,
        "rns_module": str(Path(RNS.__file__).resolve()),
        "auto_module": str(actual_source),
        "auto_sha256": hashlib.sha256(source_bytes).hexdigest(),
        "auto_canonical_lf_sha256": hashlib.sha256(canonical_source).hexdigest(),
        "auto_git_blob_sha256": hashlib.sha256(git_blob).hexdigest(),
        "lxmf_version": importlib.metadata.version("lxmf"),
        "lxmf_module": str(Path(LXMF.__file__).resolve()),
        "dest": destination.hash.hex(),
        "native_dest": native_destination.hash.hex(),
        "ifac_size": interface.ifac_size if args.ifac_name else None,
        **snapshot(interface),
    })

    try:
        for line in sys.stdin:
            command = None
            try:
                command = json.loads(line)
                request = command["request"]
                operation = command["command"]
                if operation == "stats":
                    emit({"event": "stats", "request": request, **snapshot(interface)})
                elif operation == "send":
                    data = bytes.fromhex(command["payload"])
                    if not 0 < len(data) <= 450:
                        raise ValueError("Live adapter payload must be 1..450 bytes")
                    peer = interface.spawned_interfaces.get(args.peer)
                    if peer is None or not peer.online or not interface.online:
                        raise RuntimeError(f"Real AutoInterface has no online peer {args.peer}")
                    packet = RNS.Packet(native_destination, data, attached_interface=peer, create_receipt=False)
                    packet.send()
                    emit({"event": "sent", "request": request, "sent": bool(packet.sent),
                          "peer": peer.addr, "device": peer.ifname, "size": len(data)})
                elif operation == "detach":
                    interface.detach()
                    emit({"event": "detached", "request": request, **snapshot(interface)})
                elif operation == "quit":
                    emit({"event": "quit", "request": request})
                    break
                else:
                    raise ValueError(f"Unsupported adapter command {operation}")
            except Exception as error:
                emit({"event": "error", "request": command.get("request") if isinstance(command, dict) else None,
                      "message": repr(error)})
    finally:
        if not interface.detached:
            interface.detach()


if __name__ == "__main__":
    main()
