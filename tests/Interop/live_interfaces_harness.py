"""Live Python Reticulum peer for the DotReticulum P0-3 interface interop check.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Driven by tests/DotReticulum.Interfaces.Tests/LiveInteropTests.cs (DOTRETICULUM_LIVE=1); not a unit test.

Starts ONE real RNS instance (no shared instance, no transport) with three interfaces:
  * TCPServerInterface on 127.0.0.1:<tcp-server-port>            (a .NET TcpClientPacketInterface connects to it)
  * TCPClientInterface -> 127.0.0.1:<dotnet-tcp-port>            (connects to a .NET TcpServerPacketInterface)
  * UDPInterface listen 127.0.0.1:<udp-listen-port>, forward 127.0.0.1:<udp-forward-port>  (paired with a .NET UdpPacketInterface)
It owns a SINGLE destination (dotreticulum.interop.p03) that it announces on all interfaces every second, and a PLAIN
destination whose packet callback reports every packet it receives, with the name of the interface it arrived on.

stdout protocol: lines starting with "@@" followed by JSON (RNS itself logs to stdout, so other lines are ignored):
  {"event":"ready", "dest_hash", "name_hash", "plain_hash", "app_data", "interfaces":[...], "rns_file"}
  {"event":"packet", "payload", "interface", "hops"}      (PLAIN destination received a packet)
  {"event":"stats", "interfaces":{name:{"rxb","txb"}}}   (response to the "stats" stdin command)
stdin commands: "stats", "quit" (EOF also quits).
"""
import argparse
import json
import os
import sys
import threading
import time

import RNS
from reference_environment import verify_reference

APP_DATA = b"dotreticulum-interop-p03"


def emit(obj):
    sys.stdout.write("@@" + json.dumps(obj) + "\n")
    sys.stdout.flush()


def write_config(path, args):
    config = f"""[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = dotreticulum_interop
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
  [[PyTCPServer]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.tcp_server_port}

  [[PyTCPClient]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {args.dotnet_tcp_port}

  [[PyUDP]]
    type = UDPInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.udp_listen_port}
    forward_ip = 127.0.0.1
    forward_port = {args.udp_forward_port}
"""
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, "config"), "w", encoding="utf-8") as f:
        f.write(config)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True, help="fresh, empty config directory (created if missing)")
    parser.add_argument("--tcp-server-port", type=int, required=True)
    parser.add_argument("--dotnet-tcp-port", type=int, required=True)
    parser.add_argument("--udp-listen-port", type=int, required=True)
    parser.add_argument("--udp-forward-port", type=int, required=True)
    args = parser.parse_args()

    verify_reference()
    write_config(args.config, args)
    reticulum = RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)

    identity = RNS.Identity()
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "interop", "p03")
    plain = RNS.Destination(None, RNS.Destination.IN, RNS.Destination.PLAIN, "dotreticulum", "interop", "plain")

    def on_packet(data, packet):
        iface = packet.receiving_interface
        emit({
            "event": "packet",
            "payload": data.hex(),
            "interface": getattr(iface, "name", str(iface)),
            "hops": packet.hops,
        })

    plain.set_packet_callback(on_packet)

    emit({
        "event": "ready",
        "dest_hash": destination.hash.hex(),
        "name_hash": destination.name_hash.hex(),
        "plain_hash": plain.hash.hex(),
        "app_data": APP_DATA.hex(),
        "interfaces": [i.name for i in RNS.Transport.interfaces],
        "rns_file": RNS.__file__,
    })

    stop = threading.Event()

    def announcer():
        while not stop.is_set():
            try:
                destination.announce(app_data=APP_DATA)
            except Exception as e:
                sys.stderr.write(f"announce failed: {e}\n")
            stop.wait(1.0)

    threading.Thread(target=announcer, daemon=True).start()

    for line in sys.stdin:
        cmd = line.strip()
        if cmd == "stats":
            emit({
                "event": "stats",
                "interfaces": {i.name: {"rxb": i.rxb, "txb": i.txb} for i in RNS.Transport.interfaces},
            })
        elif cmd == "quit":
            break

    stop.set()
    sys.stdout.flush()
    os._exit(0)


main()
