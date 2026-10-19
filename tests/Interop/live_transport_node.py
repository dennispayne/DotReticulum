"""Live Python Reticulum leaf node for the DotReticulum P0-5 multi-hop transport check.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Driven by tests/DotReticulum.Applications.Tests/LiveTransportTests.cs (DOTRETICULUM_LIVE=1); not a unit test.

One real RNS instance (no shared instance, no transport) whose only interface is a TCPClientInterface to the
.NET rnsd transport node at 127.0.0.1:<--rnsd-port>. It owns one SINGLE IN destination
dotreticulum.interop.p05<--name> with PROVE_ALL. Nothing announces automatically.

stdout protocol: lines starting with "@@" followed by JSON (other lines are RNS logging):
  {"event":"ready", "dest", "identity_hash", "rns_file"}
  {"event":"packet", "dest", "payload", "hops"}                    decrypted packet delivered to our destination
  {"event":"announce", "dest", "app_data", "hops"}                 announce accepted by upstream (any aspect)
  {"event":"announced", "dest"}                                    reply to "announce"
  {"event":"path", "dest", "has_path", "hops"}                     reply to "path <hex>" (no request) / "request_path <hex> <timeout s>"
  {"event":"sent", "dest", "recalled", "ok", "proved", "packet_hash"}   reply to "send <desthash hex> <app.aspect1.aspect2> <payload hex>"
                                                                   ("proved" is true once the PROVE_ALL proof validated, waited up to 15 s)
stdin commands: "announce [appdata hex]", "path <hex>", "request_path <hex> <timeout s>", "send ...", "quit" (EOF also quits).
"""
import argparse
import json
import os
import sys
import threading
import time

import RNS
from reference_environment import verify_reference


def emit(obj):
    sys.stdout.write("@@" + json.dumps(obj) + "\n")
    sys.stdout.flush()


def write_config(path, port):
    config = f"""[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = dotreticulum_interop_p05
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
  [[ToDotNet]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {port}
"""
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, "config"), "w", encoding="utf-8") as f:
        f.write(config)


class Handler:
    aspect_filter = None

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit({"event": "announce", "dest": destination_hash.hex(), "app_data": app_data.hex() if app_data else "", "hops": RNS.Transport.hops_to(destination_hash)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsd-port", type=int, required=True)
    parser.add_argument("--name", required=True)
    args = parser.parse_args()

    verify_reference()
    write_config(args.config, args.rnsd_port)
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    RNS.Transport.register_announce_handler(Handler())

    identity = RNS.Identity()
    dest = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "interop", "p05" + args.name)
    dest.set_proof_strategy(RNS.Destination.PROVE_ALL)

    def on_packet(data, packet):
        emit({"event": "packet", "dest": dest.hash.hex(), "payload": data.hex(), "hops": packet.hops})
    dest.set_packet_callback(on_packet)

    emit({"event": "ready", "dest": dest.hash.hex(), "identity_hash": identity.hash.hex(), "rns_file": RNS.__file__})

    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        cmd = parts[0]
        if cmd == "announce":
            app_data = bytes.fromhex(parts[1]) if len(parts) > 1 else b"dotreticulum-p05-" + args.name.encode()
            dest.announce(app_data=app_data)
            emit({"event": "announced", "dest": dest.hash.hex()})
        elif cmd == "path":
            h = bytes.fromhex(parts[1])
            emit({"event": "path", "dest": parts[1], "has_path": RNS.Transport.has_path(h), "hops": RNS.Transport.hops_to(h)})
        elif cmd == "request_path":
            h = bytes.fromhex(parts[1])
            timeout = float(parts[2]) if len(parts) > 2 else 10.0
            RNS.Transport.request_path(h)
            deadline = time.time() + timeout
            while time.time() < deadline and not RNS.Transport.has_path(h):
                time.sleep(0.1)
            emit({"event": "path", "dest": parts[1], "has_path": RNS.Transport.has_path(h), "hops": RNS.Transport.hops_to(h)})
        elif cmd == "send":
            h = bytes.fromhex(parts[1])
            app, *aspects = parts[2].split(".")
            payload = bytes.fromhex(parts[3])
            identity_out = RNS.Identity.recall(h)
            if identity_out is None:
                emit({"event": "sent", "dest": parts[1], "recalled": False, "ok": False, "proved": False, "packet_hash": ""})
                continue
            out = RNS.Destination(identity_out, RNS.Destination.OUT, RNS.Destination.SINGLE, app, *aspects)
            assert out.hash == h, "destination hash mismatch"
            packet = RNS.Packet(out, payload)
            receipt = packet.send()
            proved = threading.Event()
            if receipt:
                receipt.set_delivery_callback(lambda r: proved.set())
            emit_ok = receipt is not False and receipt is not None
            ok = proved.wait(15.0) if emit_ok else False
            emit({"event": "sent", "dest": parts[1], "recalled": True, "ok": emit_ok, "proved": ok, "packet_hash": packet.packet_hash.hex() if packet.packet_hash else ""})
        elif cmd == "quit":
            break

    os._exit(0)


if __name__ == "__main__":
    main()
