"""Isolated mixed-transport regression peer; no upstream monkeypatches.

Reticulum-hosted RNS snapshot pinned by reference_environment.py.
Both ports: real Python transport with TCP server and client. Client port only:
leaf. All interfaces are loopback, shared instance disabled, gateway discovery
enabled on the transport interfaces. Nothing announces until commanded.
Driven through PyLinkNode by LiveMixedTransportTests (DOTRETICULUM_LIVE=1).
"""
import argparse
import json
import os
import sys
import threading
import time

import RNS
from reference_environment import PINNED_RNS_SHA, verify_reference

LOCK = threading.Lock()
STATE = {"link": None}


def emit(event, **fields):
    with LOCK:
        print("@@" + json.dumps(dict(event=event, **fields)), flush=True)


def configure_link(link, role):
    STATE["link"] = link
    link.set_packet_callback(lambda data, packet: emit(
        "data", payload=data.hex(), link_id=link.link_id.hex(), hops=packet.hops))
    link.set_link_closed_callback(lambda closed: emit(
        "link_closed", link_id=closed.link_id.hex()))
    emit("link_up", role=role, link_id=link.link_id.hex())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, required=True)
    parser.add_argument("--network-name", default="")
    parser.add_argument("--network-key", default="")
    args = parser.parse_args()
    verify_reference()
    transport = bool(args.tcp_server_port)
    os.makedirs(args.config, exist_ok=True)
    ifac = f"""
    network_name = {args.network_name}
    pass_phrase = {args.network_key}
    ifac_size = 128
    bitrate = 64000
""" if args.network_name or args.network_key else ""
    server = f"""
  [[ToLeaf]]
    type = TCPServerInterface
    enabled = yes
    mode = gateway
    listen_ip = 127.0.0.1
    listen_port = {args.tcp_server_port}
{ifac}
""" if transport else ""
    with open(os.path.join(args.config, "config"), "w", encoding="utf-8") as config:
        config.write(f"""[reticulum]
  enable_transport = {"yes" if transport else "no"}
  share_instance = no
[logging]
  loglevel = 4
[interfaces]
  [[ToDotNet]]
    type = TCPClientInterface
    enabled = yes
    mode = gateway
    target_host = 127.0.0.1
    target_port = {args.rnsd_port}
{ifac}
{server}""")
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    identity = RNS.Identity()
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE,
                                  "dotreticulum", "interop", "mixed")
    destination.set_proof_strategy(RNS.Destination.PROVE_ALL)
    destination.set_packet_callback(lambda data, packet: emit(
        "datagram", payload=data.hex(), hops=packet.hops))
    destination.set_link_established_callback(lambda link: configure_link(link, "responder"))

    class Handler:
        aspect_filter = None

        def received_announce(self, destination_hash, announced_identity, app_data):
            emit("announce", dest=destination_hash.hex(), hops=RNS.Transport.hops_to(destination_hash),
                 app_data=(app_data or b"").hex())

    RNS.Transport.register_announce_handler(Handler())
    emit("ready", dest=destination.hash.hex(), transport=RNS.Reticulum.transport_enabled(),
         transport_id=RNS.Transport.identity.hash.hex(), rns_file=RNS.__file__, revision=PINNED_RNS_SHA)
    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        command = parts[0]
        if command == "quit":
            break
        if command == "tunnels":
            with RNS.Transport.tunnels_lock:
                emit("tunnels", entries=[
                    {"id": key.hex(), "paths": [destination.hex() for destination in value[2]],
                     "attached": value[1] is not None, "expires": value[3]}
                    for key, value in RNS.Transport.tunnels.items()])
            continue
        if command == "synthesize":
            interface = next(interface for interface in RNS.Transport.interfaces
                             if interface.name == "ToDotNet")
            RNS.Transport.synthesize_tunnel(interface)
            emit("synthesized")
            continue
        if command in ("path", "request_path"):
            target = bytes.fromhex(parts[1])
            if command == "request_path":
                RNS.Transport.request_path(target)
                deadline = time.monotonic() + float(parts[2])
                while not RNS.Transport.has_path(target) and time.monotonic() < deadline:
                    time.sleep(0.05)
            emit("path", dest=target.hex(), has_path=RNS.Transport.has_path(target),
                 hops=RNS.Transport.hops_to(target))
        elif command == "announce":
            destination.announce(app_data=b"mixed-transport")
            emit("announced", dest=destination.hash.hex())
        elif command in ("connect", "datagram"):
            target = bytes.fromhex(parts[1])
            recalled = RNS.Identity.recall(target)
            assert recalled is not None, "No announced identity"
            outgoing = RNS.Destination(recalled, RNS.Destination.OUT, RNS.Destination.SINGLE,
                                       "dotreticulum", "interop", "mixed")
            assert outgoing.hash == target
            outgoing.set_proof_strategy(RNS.Destination.PROVE_ALL)
            if command == "connect":
                STATE["link"] = RNS.Link(outgoing, established_callback=lambda link: configure_link(link, "initiator"))
            else:
                receipt = RNS.Packet(outgoing, bytes.fromhex(parts[2])).send()
                assert receipt is not None and receipt is not False, "Packet not sent"
                deadline = time.monotonic() + 15
                while receipt.status == RNS.PacketReceipt.SENT and time.monotonic() < deadline:
                    time.sleep(0.02)
                emit("datagram_sent", delivered=receipt.status == RNS.PacketReceipt.DELIVERED)
        elif command == "send":
            receipt = RNS.Packet(STATE["link"], bytes.fromhex(parts[1])).send()
            assert receipt is not None and receipt is not False
            deadline = time.monotonic() + 15
            while receipt.status == RNS.PacketReceipt.SENT and time.monotonic() < deadline:
                time.sleep(0.02)
            emit("sent", delivered=receipt.status == RNS.PacketReceipt.DELIVERED)
        elif command == "close":
            STATE["link"].teardown()
        else:
            raise ValueError(command)
    os._exit(0)


if __name__ == "__main__":
    main()
