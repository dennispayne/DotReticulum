"""Execute unmodified pinned KISS/AX25 methods with an in-memory serial adapter."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

PIN = "a29bfb51c468945c96a1ee22cae0114e993e2391"


class SerialAdapter:
    def __init__(self, incoming=b""):
        self.incoming = incoming
        self.position = 0
        self.writes = []

    @property
    def is_open(self):
        return self.position < len(self.incoming)

    @property
    def in_waiting(self):
        return len(self.incoming) - self.position

    def read(self, count):
        result = self.incoming[self.position:self.position + count]
        self.position += len(result)
        return result

    def write(self, data):
        self.writes.append(data)
        return len(data)

    def close(self):
        pass


class Owner:
    def __init__(self):
        self.packets = []

    def inbound(self, data, interface):
        self.packets.append(data.hex())


class BeaconSerial(SerialAdapter):
    def __init__(self):
        super().__init__()
        self.open = True

    @property
    def is_open(self):
        return self.open

    def write(self, data):
        result = super().write(data)
        self.open = False
        return result


def make(interface_type, incoming=b"", callsign="N0CALL", ssid=0):
    instance = interface_type.__new__(interface_type)
    instance.serial = SerialAdapter(incoming)
    instance.owner = Owner()
    instance.rxb = instance.txb = 0
    instance.online = instance.interface_ready = True
    instance.flow_control = False
    instance.detached = True
    instance.reconnecting = False
    instance.HW_MTU = 564
    instance.timeout = 100
    instance.beacon_d = b""
    instance.beacon_i = None
    instance.first_tx = None
    instance.src_call = callsign.upper().encode("ascii")
    instance.src_ssid = ssid
    instance.dst_call = b"APZRNS"
    instance.dst_ssid = 0
    instance.name = "vector"
    instance.packet_queue = []
    return instance


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", default=str(Path(__file__).parent / "Fixtures" / "parity6-kiss.json"))
    args = parser.parse_args()
    root = Path(args.upstream).resolve(strict=True)
    os.environ["DOTRETICULUM_UPSTREAM"] = str(root)
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference()
    import RNS
    modules = [importlib.import_module("RNS.Interfaces." + name) for name in
               ("KISSInterface", "AX25KISSInterface")]
    provenance = []
    for module in modules:
        path = Path(module.__file__).resolve(strict=True)
        if not path.is_relative_to(root / "RNS"):
            raise RuntimeError(f"Unexpected import {path}")
        provenance.append({"source": path.relative_to(root).as_posix(),
                           "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        print(f"Verified import: {path}")
    print(f"RNS {RNS.__version__} from {RNS.__file__}; revision {PIN}")
    kiss, ax25 = modules[0].KISSInterface, modules[1].AX25KISSInterface
    outgoing = []
    for kind, cls, call, ssid in [("kiss", kiss, "N0CALL", 0),
                                 ("ax25", ax25, "N0CALL", 15),
                                 ("ax25-short", ax25, "abc", 2)]:
        for payload in [b"\x01\xc0\xdb\xdc\xdd", bytes(range(256)), b"hello"]:
            item = make(cls, callsign=call, ssid=ssid)
            item.process_outgoing(payload)
            outgoing.append({"kind": kind, "callsign": call, "ssid": ssid,
                             "payload": payload.hex(), "wire": b"".join(item.serial.writes).hex()})
    configuration = []
    for values in [(350, 20, 64, 20), (-10, 9999, 999, -1), (1920, 2190, 192, 219)]:
        item = make(kiss)
        for method, value in zip(("setPreamble", "setTxTail", "setPersistence", "setSlotTime"), values):
            getattr(item, method)(value)
        item.setFlowControl(False)
        configuration.append({"values": values, "wire": b"".join(item.serial.writes).hex()})
    incoming = []
    cases = {
        "escaped": bytes.fromhex("c00001dbdcdbddc0"),
        "unknown-escape": bytes.fromhex("c000db1102c0"),
        "truncated-escape": bytes.fromhex("c00001dbc0"),
        "escape-carries": bytes.fromhex("c00001dbc0c000dc02c0"),
        "truncated": bytes.fromhex("c0000102"),
        "unknown-command": bytes.fromhex("c0060102c0c00003c0"),
        "ready-per-byte": bytes.fromhex("c01f010203c0c00007c0"),
        "overflow": b"\xc0\x00" + b"x" * 570 + b"\xc0\xc0\x00ok\xc0",
        "all-ports": b"".join(bytes([0xc0, port << 4, port + 1, 0xc0]) for port in range(16)),
    }
    for kind, cls in [("kiss", kiss), ("ax25", ax25)]:
        for name, wire in cases.items():
            item = make(cls, wire)
            ready = []
            item.process_queue = lambda: ready.append(True)
            item.readLoop()
            incoming.append({"kind": kind, "name": name, "wire": wire.hex(),
                             "packets": item.owner.packets, "ready": len(ready), "rxb": item.rxb})
    for size in [0, 15, 16, 17, 580, 590]:
        wire = b"\xc0\x00" + b"\x11" * size + b"\xc0"
        item = make(ax25, wire)
        item.readLoop()
        incoming.append({"kind": "ax25", "name": f"permissive-header-{size}", "wire": wire.hex(),
                         "packets": item.owner.packets, "ready": 0, "rxb": item.rxb})
    flow = []
    for kind, cls in [("kiss", kiss), ("ax25", ax25)]:
        for ready_count in [0, 1, 3]:
            wire = bytes([0xc0, 0x0f]) + bytes(range(1, ready_count + 1)) + bytes([0xc0])
            item = make(cls, wire)
            item.flow_control = True
            item.process_outgoing(b"\x01")
            item.process_outgoing(b"\x02")
            item.readLoop()
            flow.append({"kind": kind, "ready_count": ready_count, "incoming": wire.hex(),
                         "writes": [frame.hex() for frame in item.serial.writes],
                         "ready_after": item.interface_ready, "queued_after": len(item.packet_queue)})
    beacon = make(kiss)
    beacon.serial = BeaconSerial()
    beacon.beacon_d = b"ID"
    beacon.beacon_i = 1
    beacon.first_tx = 0
    saved_time, saved_sleep = modules[0].time, modules[0].sleep
    try:
        # Injectable clock/sleep adapter only: the upstream readLoop generates the padding and schedule.
        modules[0].time = SimpleNamespace(time=lambda: 100)
        modules[0].sleep = lambda duration: None
        beacon.readLoop()
    finally:
        modules[0].time, modules[0].sleep = saved_time, saved_sleep
    output = {"revision": PIN, "rns_version": RNS.__version__, "sources": provenance,
              "method": "Unmodified process_outgoing, configuration setters and readLoop; recording serial/owner adapters; no device I/O",
              "beacon_method": "readLoop clock/sleep adapter at t=100, first_tx=0, interval=1; no source modifications",
              "beacon": {"callsign": "ID", "wire": b"".join(beacon.serial.writes).hex(), "first_tx": beacon.first_tx},
              "flow": flow, "outgoing": outgoing, "configuration": configuration, "incoming": incoming}
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(outgoing)} transmit, {len(configuration)} configuration, {len(incoming)} receive, {len(flow)} flow and 1 scheduled beacon vectors: {args.output}")


if __name__ == "__main__":
    main()
