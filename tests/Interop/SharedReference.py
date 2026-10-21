"""Thin actual-upstream process adapter for shared bearer and local RPC tests."""
import argparse
import hashlib
import json
import multiprocessing.connection
from pathlib import Path
import sys
import threading
import time

from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.vendor import umsgpack as mp


def emit(**values):
    print(json.dumps(values), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["server", "client", "shared-client", "remote-server", "remote-client"])
    parser.add_argument("--config")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--control-port", type=int, required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--operation", default="interface_stats")
    parser.add_argument("--allowed")
    parser.add_argument("--remote")
    args = parser.parse_args()
    verify_reference()
    key = bytes.fromhex(args.key)
    if args.mode == "client":
        with multiprocessing.connection.Client(("127.0.0.1", args.control_port), authkey=key) as peer:
            peer.send_bytes(mp.packb({"get": args.operation}))
            result = mp.unpackb(peer.recv_bytes())
            interfaces = result.get("interfaces", []) if isinstance(result, dict) else []
            local_clients = sum(1 for interface in interfaces
                                if str(interface.get("name", "")).startswith("LocalShared")
                                or str(interface.get("name", "")).startswith("LocalInterface")
                                or interface.get("type") == "LocalSharedPacketInterface")
            emit(event="response", packed=mp.packb(result).hex(), local_clients=local_clients)
        return
    config = Path(args.config).resolve()
    config.mkdir(parents=True, exist_ok=True)
    if args.mode == "remote-server":
        text = ("[reticulum]\nshare_instance = no\nenable_remote_management = yes\n"
                f"remote_management_allowed = {args.allowed}\n"
                "[interfaces]\n[[Native]]\ntype = TCPServerInterface\nenabled = yes\n"
                f"listen_ip = 127.0.0.1\nlisten_port = {args.port}\n")
    elif args.mode == "remote-client":
        text = ("[reticulum]\nshare_instance = no\n[interfaces]\n"
                "[[Native]]\ntype = TCPClientInterface\nenabled = yes\n"
                f"target_host = 127.0.0.1\ntarget_port = {args.port}\n")
    else:
        text = ("[reticulum]\nshare_instance = yes\nshared_instance_type = tcp\n"
                f"shared_instance_port = {args.port}\ninstance_control_port = {args.control_port}\n"
                f"rpc_key = {args.key}\n[interfaces]\n")
    (config / "config").write_text(text, encoding="utf-8")
    node = RNS.Reticulum(configdir=str(config), require_shared_instance=args.mode == "shared-client", loglevel=0)
    incoming = RNS.Destination(None, RNS.Destination.IN, RNS.Destination.PLAIN, "shared", "test")
    incoming.set_packet_callback(lambda data, packet: emit(event="received", packed=packet.raw.hex()))
    emit(event="ready", upstream=PINNED_RNS_SHA, version=RNS.__version__,
         module=str(Path(RNS.__file__).resolve()),
         reticulum_sha256=hashlib.sha256((Path(RNS.__file__).parent / "Reticulum.py").read_bytes()).hexdigest(),
         host=node.is_shared_instance, client=node.is_connected_to_shared_instance,
         remote=RNS.Transport.remote_management_destination.hash.hex() if args.mode == "remote-server" else None)
    if args.mode == "shared-client":
        destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "shared", "test")
        packet = RNS.Packet(destination, b"\x7e\x7dshared-python")
        packet.pack()
        RNS.Transport.interfaces[0].process_outgoing(packet.raw)
        emit(event="packet", packed=packet.raw.hex())
    for line in sys.stdin:
        command = line.strip()
        if command == "quit":
            RNS.Reticulum.exit_handler()
            return
        if command == "stats":
            peer = node.get_rpc_client()
            peer.send_bytes(mp.packb({"get": "interface_stats"}))
            emit(event="response", packed=peer.recv_bytes().hex())
            peer.close()
        elif command == "remote-status":
            target = bytes.fromhex(args.remote)
            RNS.Transport.request_path(target)
            deadline = time.monotonic() + 15
            while not RNS.Transport.has_path(target):
                if time.monotonic() > deadline:
                    raise TimeoutError("Remote management destination path not found")
                time.sleep(0.025)
            remote_identity = RNS.Identity.recall(target)
            destination = RNS.Destination(remote_identity, RNS.Destination.OUT,
                                          RNS.Destination.SINGLE, "rnstransport", "remote", "management")
            up = threading.Event()
            link = RNS.Link(destination, established_callback=lambda _: up.set())
            if not up.wait(15):
                raise TimeoutError("Remote management link not established")
            link.identify(RNS.Identity.from_bytes(bytes(range(64))))
            done = threading.Event()
            box = {}

            def response(receipt):
                box["value"] = receipt.response
                done.set()

            link.request("/status", [True, True], response_callback=response,
                         failed_callback=lambda _: done.set(), timeout=10)
            if not done.wait(15) or "value" not in box:
                raise TimeoutError("Remote status request failed")
            emit(event="response", packed=mp.packb(box["value"]).hex())
            link.teardown()
        elif command == "send":
            destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "shared", "test")
            packet = RNS.Packet(destination, b"\x7e\x7dshared-python")
            packet.pack()
            for interface in RNS.Transport.local_client_interfaces:
                interface.process_outgoing(packet.raw)
            emit(event="packet", packed=packet.raw.hex())
    RNS.Reticulum.exit_handler()


if __name__ == "__main__":
    main()
