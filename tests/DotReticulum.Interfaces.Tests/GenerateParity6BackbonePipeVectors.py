"""Execute pinned upstream framing and egress controls, without network/process emulation of our implementation.

Requires DOTRETICULUM_UPSTREAM, PYTHONPATH pointing to that checkout and the isolated reference Python.
Output is written beside this generator; tests assert the provenance and independently produced results.
"""
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

verify_reference()
import RNS
backbone = importlib.import_module("RNS.Interfaces.BackboneInterface")
pipe = importlib.import_module("RNS.Interfaces.PipeInterface")
hdlc = importlib.import_module("RNS.Interfaces.util.HDLC")
transmit = importlib.import_module("RNS.Interfaces.util.TransmitBuffer")
root = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve()
sources = {}
for module in (backbone, pipe, hdlc, transmit, importlib.import_module("RNS.Transport"), importlib.import_module("RNS.Reticulum")):
    path = Path(module.__file__).resolve()
    if not path.is_relative_to(root):
        raise RuntimeError(f"Unexpected import {path}")
    sources[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()


def packet(size):
    return b"\x00" * 19 + bytes((i % 256 for i in range(size - 19)))


class Owner:
    def __init__(self):
        self.frames = []

    def inbound(self, data, interface):
        self.frames.append(bytes(data).hex())


class Child:
    def __init__(self, output):
        self.stdout = io.BytesIO(output)
        self.stdin = io.BytesIO()

    def poll(self):
        return 0

    def kill(self):
        pass


def pipe_decode(wire):
    interface = object.__new__(pipe.PipeInterface)
    interface.owner = Owner()
    interface.name = "reference-vector"
    interface.process = Child(wire)
    interface.HW_MTU = 1064
    interface.rxb = 0
    interface.reconnect_pipe = lambda: None  # Prevent actual process creation after observed EOF.
    interface.readLoop()
    return interface.owner.frames


def backbone_decode(wire, chunk):
    frames = []
    decoder = hdlc.ReceiveBuffer(500, 19, 516, lambda data: frames.append(bytes(data).hex()))
    for start in range(0, len(wire), chunk):
        decoder.feed(wire[start:start + chunk])
    return frames


cases = []
for name, wire in (
    ("escaped", bytes(hdlc.HDLC.frame(packet(280)))),
    ("shared_flags", bytes(hdlc.HDLC.frame(packet(24)))[:-1] + bytes(hdlc.HDLC.frame(packet(25)))) ,
    ("separate_flags", bytes(hdlc.HDLC.frame(packet(24))) + bytes(hdlc.HDLC.frame(packet(25)))),
    ("header_only", bytes(hdlc.HDLC.frame(packet(19)))),
    ("unknown_escape", b"\x7e" + packet(24) + b"\x7d\x01\x7e"),
    ("dangling_escape", b"\x7e" + packet(24) + b"\x7d\x7e"),
    ("over_backbone", bytes(hdlc.HDLC.frame(packet(517)))),
    ("over_pipe", bytes(hdlc.HDLC.frame(packet(1065)))),
):
    cases.append({
        "name": name, "wire": wire.hex(), "backbone": backbone_decode(wire, 7),
        "pipe": pipe_decode(wire),
        "pipe_hardening": name in ("unknown_escape", "dangling_escape", "over_pipe"),
    })

encode = []
for size in (20, 280, 500, 1064):
    raw = packet(size)
    interface = object.__new__(pipe.PipeInterface)
    interface.online = True
    interface.process = Child(b"")
    interface.txb = 0
    interface.process_outgoing(raw)
    encode.append({"raw": raw.hex(), "backbone": bytes(hdlc.HDLC.frame(raw)).hex(),
                   "pipe": interface.process.stdin.getvalue().hex(), "pipe_txb": interface.txb})


class Egress:
    def __init__(self):
        self.transmit_buffer = transmit.TransmitBuffer()
        self._dp_ec_prev_sent = 0
        self._dp_ec_zero_ticks = 0
        self._dp_ec_last_drain = 0
        self.tx_stalled = False


egress = Egress()
egress.transmit_buffer.append(bytes(200000))
states = []
for now, sent in ((1, 0), (2, 0), (3, 0), (4, 1000), (5, 50000), (6, 200000)):
    egress.transmit_buffer._tx_sent = sent
    dead = backbone.BackboneInterface._dp_ec_evaluate(egress, now)
    states.append({"now": now, "sent": sent, "buffered": 200000 - sent, "stalled": egress.tx_stalled, "dead": dead})


class EndTick(BaseException):
    pass


class Descriptor:
    def __init__(self, descriptor):
        self.descriptor = descriptor

    def fileno(self):
        return self.descriptor


class Producer:
    def __init__(self, descriptor, packets, count, gated=False, hold=0):
        self.socket = Descriptor(descriptor)
        self.dp_ingress_packets = packets
        self.dp_ingress_bytes = count
        self.dp_ingress_gated = gated
        self.dp_ingress_tcount = 0
        self.dp_ingress_hold = hold
        self.transmit_buffer = transmit.TransmitBuffer()

    def __str__(self):
        return f"Producer[{self.socket.fileno()}]"


class Poller:
    def __init__(self):
        self.modifications = []

    def modify(self, descriptor, events):
        self.modifications.append((descriptor, events))


class QueueDepth:
    def __init__(self, depth):
        self.depth = depth

    def qsize(self, traffic_class):
        return self.depth


def ingress_case(name, depth, capacity, producers, immediate=False):
    cls = backbone.BackboneInterface
    importlib.import_module("RNS.Interfaces.LocalInterface")
    old_sleep, old_time = backbone.time.sleep, backbone.time.time
    old_queue = RNS.Transport.inbound_queues
    old_peers, old_epoll = cls.spawned_interface_filenos, cls.epoll
    old_active, old_snapshot = cls._ic_job_active, cls._dp_ic_snapshot
    old_watermarks = {key: getattr(cls, key, None) for key in ("DP_IC_HIGH_WM", "DP_IC_MID_WM", "DP_IC_LOW_WM")}
    # Verified with actual Linux `python3 -c "import select; print(select.EPOLLIN, select.EPOLLOUT, select.EPOLLHUP)"`.
    # Windows lacks epoll constants; these affect only our disclosed fake epoll registration.
    missing_epoll = []
    for key, value in (("EPOLLIN", 1), ("EPOLLOUT", 4), ("EPOLLHUP", 16)):
        if not hasattr(backbone.select, key):
            missing_epoll.append(key)
            setattr(backbone.select, key, value)
    calls = 0

    def one_tick(seconds):
        nonlocal calls
        calls += 1
        if calls > 1:
            raise EndTick()

    try:
        backbone.time.sleep = one_tick
        backbone.time.time = lambda: 0.25
        RNS.Transport.inbound_queues = QueueDepth(depth)
        cls.spawned_interface_filenos = {index + 1: Producer(index + 1, **values) for index, values in enumerate(producers)}
        cls.epoll = Poller()
        cls._ic_job_active = False
        cls._dp_ic_snapshot = 0
        cls.DP_IC_HIGH_WM = max(4, int(0.90 * capacity))
        cls.DP_IC_MID_WM = max(2, int(0.68 * capacity))
        cls.DP_IC_LOW_WM = int(0.10 * capacity)
        if immediate:
            cls._throttle_immediate(depth)
        else:
            try:
                cls._BackboneInterface__dp_ic_job()
            except EndTick:
                pass
        if any(peer.dp_ingress_tcount for peer in cls.spawned_interface_filenos.values()) and not cls.epoll.modifications:
            raise RuntimeError("Reference throttle failed before its fake epoll modification; do not serialize partial failure as a vector.")
        return {"name": name, "depth": depth, "capacity": capacity, "now": 0.25,
                "immediate": immediate, "producers": producers,
                "states": [{"gated": peer.dp_ingress_gated, "hold": peer.dp_ingress_hold,
                            "throttles": peer.dp_ingress_tcount, "packets": peer.dp_ingress_packets,
                            "bytes": peer.dp_ingress_bytes} for peer in cls.spawned_interface_filenos.values()]}
    finally:
        backbone.time.sleep, backbone.time.time = old_sleep, old_time
        RNS.Transport.inbound_queues = old_queue
        cls.spawned_interface_filenos, cls.epoll = old_peers, old_epoll
        cls._ic_job_active, cls._dp_ic_snapshot = old_active, old_snapshot
        for key, value in old_watermarks.items():
            if value is None:
                delattr(cls, key)
            else:
                setattr(cls, key, value)
        for key in missing_epoll:
            delattr(backbone.select, key)


pair = [{"packets": 3, "count": 300}, {"packets": 9, "count": 900}]
ingress = [
    ingress_case("global_largest_producer", 175, 256, pair),
    ingress_case("mid_watermark_equality", 174, 256, pair),
    ingress_case("small_queue_minimum_mid", 2, 2, pair),
    ingress_case("immediate_retains_samples", 230, 256, pair, True),
    ingress_case("low_watermark_equality", 25, 256,
                 [{"packets": 0, "count": 0, "gated": True, "hold": 0}]),
    ingress_case("low_releases_one", 24, 256,
                 [{"packets": 0, "count": 0, "gated": True, "hold": 0},
                  {"packets": 0, "count": 0, "gated": True, "hold": 0}]),
    ingress_case("hold_prevents_release", 0, 256,
                 [{"packets": 0, "count": 0, "gated": True, "hold": 100}]),
    ingress_case("global_forty_producers", 175, 256,
                 [{"packets": index + 1, "count": (index + 1) * 100} for index in range(40)]),
]


def global_flaps():
    cls = backbone.BackboneInterface
    old_table, old_time = cls.fast_flapping, backbone.time.time
    old_remove = RNS.Transport.remove_interface
    now = 100
    parents = []
    for grace in (0, 1):
        parent = object.__new__(cls)
        parent.block_fast_flapping = True
        parent.fast_flap_threshold = 20
        parent.fast_flap_grace = grace
        parent.fast_flap_expiry = 43200
        parent.spawned_interfaces = []
        parents.append(parent)
    result = []
    try:
        cls.fast_flapping = {}
        backbone.time.time = lambda: now
        RNS.Transport.remove_interface = lambda interface: None
        for now, duration in ((100, 20), (101, 1), (102, 1), (43302, None), (43302.01, None)):
            if duration is not None:
                child = object.__new__(backbone.BackboneClientInterface)
                child.initiator = False
                child.detached = False
                child.parent_interface = parents[0]
                child.spawned_at = now - duration
                child.target_ip = "127.0.0.1"
                child.target_port = "0"
                child.name = "global-flap-vector"
                parents[0].spawned_interfaces.append(child)
                child.teardown()
            result.append({"now": now, "duration": duration, "grace_zero_count": parents[0].blocked_ip_count,
                           "grace_one_count": parents[1].blocked_ip_count})
        return result
    finally:
        cls.fast_flapping, backbone.time.time = old_table, old_time
        RNS.Transport.remove_interface = old_remove


output = {"revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
          "rns_import": str(Path(RNS.__file__).resolve()), "version": RNS.__version__,
          "sources": sources, "encode": encode, "decode": cases, "egress": states, "ingress": ingress,
          "global_flaps": global_flaps(),
          "shim": "Pipe reconnect_pipe replaced by no-op after fake stdout EOF. Ingress uses fake queue/epoll/descriptors and a controlled clock; on Windows, missing EPOLLIN/EPOLLOUT/EPOLLHUP constants are supplied as Linux-verified 1/4/16 solely for fake registration. Periodic job stops after one real reference iteration via a disclosed BaseException from the second sleep. Flap teardown substitutes only Transport.remove_interface with a no-op. No child process, real socket, actual epoll or live peer used."}
target = Path(__file__).with_name("Fixtures") / "parity6-backbone-pipe.json"
target.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {target}; RNS {RNS.__version__} from {RNS.__file__}")
