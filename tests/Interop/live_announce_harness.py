"""Live Python Reticulum peer for the DotReticulum P0-4 announce-engine interop check.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Driven by tests/DotReticulum.Transport.Tests/LiveAnnounceTests.cs (DOTRETICULUM_LIVE=1); not a unit test.

One real RNS instance (no shared instance, no transport) with a TCPServerInterface on 127.0.0.1:<tcp-server-port>.
Destinations (all SINGLE, IN):
  dotreticulum.interop.p04ratchet   ratchets enabled AND enforced (only a packet encrypted to the announced ratchet decrypts)
  dotreticulum.interop.p04plain     no ratchets
Both announce every 2 s with app_data and report decrypted packets through their packet callbacks.
An announce handler (no aspect filter) reports every announce that upstream accepts, which is how .NET announces are observed.

stdout protocol: lines starting with "@@" followed by JSON (other lines are RNS logging):
  {"event":"ready", "ratchet_hash", "plain_hash", "ratchet_app_data", "plain_app_data", "rns_file"}
  {"event":"packet", "dest", "payload", "hops"}                 decrypted packet delivered to a local destination
  {"event":"announce", "dest", "app_data", "hops"}              announce accepted by upstream (announce handler)
  {"event":"recall", "dest", "known", "identity_hash", "app_data", "ratchet", "has_path"}   reply to "recall <hex>"
  {"event":"sent", "dest", "recalled", "ok"}                    reply to   "send <full.name> <hex> <desthash>"
  stdin commands: "recall <desthash hex>", "send <app.aspect1.aspect2> <payload hex> <announced desthash hex>", "quit" (EOF also quits).
"""
import argparse
import json
import os
import sys
import threading

import RNS
from reference_environment import verify_reference

RATCHET_APP_DATA = b"dotreticulum-p04-ratchet"
PLAIN_APP_DATA = b"dotreticulum-p04-plain"


def emit(obj):
    sys.stdout.write("@@" + json.dumps(obj) + "\n")
    sys.stdout.flush()


def write_config(path, port):
    config = f"""[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = dotreticulum_interop_p04
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
  [[PyTCPServer]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {port}
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
    parser.add_argument("--tcp-server-port", type=int, required=True)
    args = parser.parse_args()

    verify_reference()
    write_config(args.config, args.tcp_server_port)
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    RNS.Transport.register_announce_handler(Handler())

    ratchet_dest = RNS.Destination(RNS.Identity(), RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "interop", "p04ratchet")
    ratchet_dest.enable_ratchets(os.path.join(args.config, "p04ratchet.ratchets"))
    ratchet_dest.enforce_ratchets()
    plain_dest = RNS.Destination(RNS.Identity(), RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "interop", "p04plain")

    for dest in (ratchet_dest, plain_dest):
        def on_packet(data, packet, dest=dest):
            emit({"event": "packet", "dest": dest.hash.hex(), "payload": data.hex(), "hops": packet.hops})
        dest.set_packet_callback(on_packet)

    emit({
        "event": "ready",
        "ratchet_hash": ratchet_dest.hash.hex(),
        "plain_hash": plain_dest.hash.hex(),
        "ratchet_app_data": RATCHET_APP_DATA.hex(),
        "plain_app_data": PLAIN_APP_DATA.hex(),
        "rns_file": RNS.__file__,
    })

    stop = threading.Event()

    def announcer():
        while not stop.is_set():
            try:
                ratchet_dest.announce(app_data=RATCHET_APP_DATA)
                plain_dest.announce(app_data=PLAIN_APP_DATA)
            except Exception as e:
                sys.stderr.write(f"announce failed: {e}\n")
            stop.wait(2.0)

    threading.Thread(target=announcer, daemon=True).start()

    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "recall":
            h = bytes.fromhex(parts[1])
            identity = RNS.Identity.recall(h)
            ratchet = RNS.Identity.get_ratchet(h)
            app_data = RNS.Identity.recall_app_data(h)
            emit({
                "event": "recall",
                "dest": parts[1],
                "known": identity is not None,
                "identity_hash": identity.hash.hex() if identity else "",
                "app_data": app_data.hex() if app_data else "",
                "ratchet": ratchet.hex() if ratchet else "",
                "has_path": RNS.Transport.has_path(h),
            })
        elif parts[0] == "send":
            app, *aspects = parts[1].split(".")
            payload = bytes.fromhex(parts[2])
            ok = False
            recalled = False
            dest_hash = ""
            try:
                # The identity is recalled from the announced destination hash (parts[3]); the OUT destination is rebuilt from it.
                identity = RNS.Identity.recall(bytes.fromhex(parts[3]))
                recalled = identity is not None
                if identity is not None:
                    out = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, app, *aspects)
                    dest_hash = out.hash.hex()
                    ok = RNS.Packet(out, payload).send() is not False
            except Exception as e:
                sys.stderr.write(f"send failed: {e}\n")
            emit({"event": "sent", "dest": dest_hash, "recalled": recalled, "ok": ok})
        elif parts[0] == "quit":
            break

    stop.set()
    sys.stdout.flush()
    os._exit(0)


main()
