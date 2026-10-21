"""Real pinned Python TCP leaf for discovery interop.

RNS owns node configuration, wire announces, discovery handlers, encryption and
stamp verification. The test-only deterministic candidate selector calls actual
LXMF stamp APIs instead of spawning the CPU-count-sized worker pool. It does not
claim coverage of upstream's multiprocessing stamp search scheduler.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
import RNS
import RNS.Discovery as discovery
from LXMF import LXStamper


def emit(value):
    print("@@" + json.dumps(value), flush=True)


def select_stamp(material, stamp_cost, expand_rounds):
    block = LXStamper.stamp_workblock(material, expand_rounds=expand_rounds)
    n = 0
    while True:
        candidate = n.to_bytes(32, "big")
        if LXStamper.stamp_valid(candidate, stamp_cost, block):
            return candidate, LXStamper.stamp_value(block, candidate)
        n += 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--backbone-port", type=int)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    root = Path(args.config)
    root.mkdir(parents=True, exist_ok=True)
    network = RNS.Identity(create_keys=False)
    network.load_private_key(bytes(range(64)))
    network_path = root / "network_identity"
    network.to_file(str(network_path))
    backbone_config = f"""
  [[Backbone]]
    type = BackboneInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.backbone_port}
    discoverable = yes
    discovery_name = Python Backbone
    discovery_stamp_value = 1
    reachable_on = localhost
    mode = gateway
""" if args.backbone_port is not None else ""
    (root / "config").write_text(f"""[reticulum]
  share_instance = No
  enable_transport = {"Yes" if args.backbone_port is not None else "No"}
  network_identity = "{network_path}"
[logging]
  loglevel = -1
[interfaces]
  [[Native]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {args.port}
{backbone_config}
""", encoding="utf-8")
    runtime = RNS.Reticulum(configdir=str(root), loglevel=-1)
    announcer = RNS.Transport.interface_announcer or discovery.InterfaceAnnouncer(RNS.Transport)
    announcer.stamper = SimpleNamespace(generate_stamp=select_stamp)
    destination = announcer.discovery_destination
    handler = discovery.InterfaceAnnounceHandler(required_value=8, callback=lambda info:
        emit({"event": "received", "name": info["name"], "implementation": info["impl_name"], "value": info["value"]}))
    RNS.Transport.register_announce_handler(handler)
    class Observer:
        aspect_filter = None
        def received_announce(self, destination_hash, announced_identity, app_data):
            emit({"event": "wire", "destination": destination_hash.hex(),
                  "identity": announced_identity.hash.hex(), "length": len(app_data or b"")})
    RNS.Transport.register_announce_handler(Observer())
    RNS.loglevel = 7
    feed = next(i for i in RNS.Transport.interfaces if i.name == "Native")
    data_destination = None
    if args.backbone_port is not None:
        from RNS.Interfaces.BackboneInterface import BackboneInterface
        interface = next(i for i in RNS.Transport.interfaces if isinstance(i, BackboneInterface))
        assert type(interface) is BackboneInterface
        endpoint_identity = RNS.Identity()
        data_destination = RNS.Destination(endpoint_identity, RNS.Destination.IN, RNS.Destination.SINGLE,
                                           "dotreticulum", "discovery", "backbone")

        def link_established(link):
            emit({"event": "link_established"})
            def packet_received(data, packet):
                RNS.Packet(link, data).send()
                emit({"event": "echoed", "payload": data.hex(),
                      "bearer": type(packet.receiving_interface).__name__})
            link.set_packet_callback(packet_received)
        data_destination.set_link_established_callback(link_established)
        source = Path(sys.modules[BackboneInterface.__module__].__file__)
        emit({"event": "ready", "identity": network.hash.hex(), "backbone_type": type(interface).__name__,
              "backbone_module": BackboneInterface.__module__, "backbone_port": interface.bind_port,
              "backbone_source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "data_destination": data_destination.hash.hex()})
    else:
        interface = type("TCPServerInterface", (), {})()
        for key, value in {
            "discovery_stamp_value": 8, "discovery_location": None,
            "discovery_name": "Python discovery", "discovery_latitude": None,
            "discovery_longitude": None, "discovery_height": None,
            "discovery_lxmf_address": None, "reachable_on": "reference.example",
            "bind_port": 4242, "discovery_publish_ifac": False,
            "discovery_encrypt": False,
        }.items():
            setattr(interface, key, value)
        emit({"event": "ready", "identity": network.hash.hex()})
    try:
        for line in sys.stdin:
            command = json.loads(line)
            if command["op"] == "quit":
                break
            if command["op"] == "announce":
                interface.discovery_encrypt = command.get("encrypted", False)
                payload = announcer.get_interface_announce_data(interface)
                assert payload
                destination.announce(app_data=payload, attached_interface=feed)
                emit({"event": "announced", "payload": payload.hex()})
            elif command["op"] == "data_announce":
                if data_destination is None:
                    raise ValueError("Data endpoint requires --backbone-port")
                if not interface.spawned_interfaces:
                    raise RuntimeError("No actual Backbone client has connected")
                peer = interface.spawned_interfaces[0]
                data_destination.announce(attached_interface=peer)
                emit({"event": "data_announced", "peer_type": type(peer).__name__})
            elif command["op"] == "sources":
                # Actual configuration state; no receiver function is replaced.
                RNS.Reticulum._Reticulum__interface_sources = [bytes.fromhex(h) for h in command["values"]]
                emit({"event": "sources"})
    finally:
        runtime.exit_handler()


if __name__ == "__main__":
    main()
