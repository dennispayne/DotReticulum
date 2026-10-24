"""Execute the pinned upstream stream contracts without opening any hardware."""
import hashlib
import json
import os
from pathlib import Path
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference

verify_reference(require_lxmf=True)
import RNS
import LXMF
from RNS.Interfaces import TCPInterface as tcp
from RNS.Interfaces import SerialInterface as serial


class Socket:
    def __init__(self):
        self.frames = []

    def sendall(self, data):
        self.frames.append(data.hex())


def outgoing(kiss, data):
    obj = tcp.TCPClientInterface.__new__(tcp.TCPClientInterface)
    obj.online, obj.detached, obj.writing = True, False, False
    obj.kiss_framing, obj.socket, obj.txb = kiss, Socket(), 0
    obj.parent_interface = None
    obj.process_outgoing(data)
    return obj.socket.frames[0]


def retries(limit):
    obj = tcp.TCPClientInterface.__new__(tcp.TCPClientInterface)
    obj.initiator, obj.reconnecting, obj.online, obj.detached = True, False, False, False
    obj.max_reconnect_tries, obj.never_connected, obj.kiss_framing = limit, True, True
    obj.name = "vector"
    obj.target_ip, obj.target_port = "127.0.0.1", 1
    calls = []
    def connect():
        calls.append("connect")
        raise OSError("simulated failure")
    obj.connect = connect
    obj.teardown = lambda: setattr(obj, "detached", True)
    class Thread:
        def __init__(self, **kwargs): pass
        def start(self): pass
    with patch.object(tcp.time, "sleep", lambda _: None), patch.object(tcp.threading, "Thread", Thread), patch.object(RNS, "log", lambda *a: None):
        obj.reconnect()
    return {"maximum": limit, "connect_calls": len(calls)}


def initial_connect():
    obj = tcp.TCPClientInterface.__new__(tcp.TCPClientInterface)
    obj.target_ip, obj.target_port, obj.name = "vector.example", 4242, "vector"
    obj.connect_timeout = 41
    recorded = {}
    class ConnectionSocket:
        def __init__(self, family, kind): recorded["family"] = family
        def settimeout(self, value): recorded.setdefault("timeouts", []).append(value)
        def setsockopt(self, level, option, value): pass
        def connect(self, address): recorded["address"] = address
    answers = [
        (tcp.socket.AF_INET6, tcp.socket.SOCK_STREAM, tcp.socket.IPPROTO_TCP, "", ("::1", 4242, 0, 0)),
        (tcp.socket.AF_INET, tcp.socket.SOCK_STREAM, tcp.socket.IPPROTO_TCP, "", ("127.0.0.1", 4242)),
    ]
    with patch.object(tcp.socket, "getaddrinfo", return_value=answers), patch.object(tcp.socket, "socket", ConnectionSocket), \
            patch.object(tcp.platform, "system", return_value="Windows"), patch.object(RNS, "log", lambda *a: None):
        recorded["connected"] = obj.connect(initial=True)
    return recorded


def client_configuration(configuration):
    calls = []
    try:
        with patch.object(tcp.Interface, "__init__", lambda obj: None), \
                patch.object(tcp.TCPClientInterface, "initial_connect", lambda obj: calls.append("initial_connect")):
            obj = tcp.TCPClientInterface(None, configuration)
        return {"configuration": configuration, "initiator": obj.initiator,
                "initial_connect_calls": len(calls),
                "target_host": getattr(obj, "target_ip", None), "target_port": getattr(obj, "target_port", None)}
    except Exception as error:
        return {"configuration": configuration, "error_type": type(error).__name__, "error_message": str(error)}


def serial_frames(data):
    obj = serial.SerialInterface.__new__(serial.SerialInterface)
    obj.HW_MTU, obj.timeout = 564, 100
    obj.detached, obj.reconnecting = True, False
    received = []
    obj.process_incoming = lambda frame: received.append(frame.hex()) if frame else None
    class Port:
        is_open = True
        def __init__(self): self.data = list(data)
        @property
        def in_waiting(self):
            if not self.data: self.is_open = False
            elif self.data[0] is None:
                self.data.pop(0)
                return 0
            return len(self.data)
        def read(self, n): return bytes([self.data.pop(0)])
        def close(self): self.is_open = False
    obj.serial = Port()
    clock = [0.0]
    def sleep(seconds): clock[0] += seconds
    with patch.object(serial, "sleep", sleep), patch.object(serial.time, "time", lambda: clock[0]):
        obj.readLoop()
    return received


payload = bytes(range(256)) + bytes([0x7e, 0x7d, 0xc0, 0xdb])
frame = bytes([0x7e]) + serial.HDLC.escape(payload) + bytes([0x7e])
reference = Path(os.environ["DOTRETICULUM_UPSTREAM"])
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "rns_version": RNS.__version__,
    "generation_platform": sys.platform,
    "rns_module": str(Path(RNS.__file__).resolve()),
    "lxmf_module": str(Path(LXMF.__file__).resolve()),
    "source_sha256": {name: hashlib.sha256((reference / "RNS" / "Interfaces" / name).read_bytes()).hexdigest()
                      for name in ("TCPInterface.py", "SerialInterface.py")},
    "payload": payload.hex(),
    "hdlc": outgoing(False, payload),
    "kiss": outgoing(True, payload),
    "retry_limits": [retries(n) for n in (0, 1, 2, 3)],
    "initial_connect": initial_connect(),
    "tcp_client_configuration": [client_configuration(configuration) for configuration in (
        {"name": "absent"},
        {"name": "port_only", "port": 4242},
        {"name": "target_port_only", "target_port": 4242},
        {"name": "remote_and_port", "remote": "example.invalid", "port": 4242},
        {"name": "target_host_only", "target_host": "example.invalid"},
        {"name": "explicit", "target_host": "example.invalid", "target_port": 4242, "remote": "ignored.invalid", "port": 9999},
        {"name": "blank", "target_host": "", "target_port": 4242},
        {"name": "null_host", "target_host": None, "target_port": 4242},
    )],
    "tcp_client_configuration_adapter": "Interface base initializer bypassed (no active Reticulum instance); initial_connect suppressed (no network). TCP constructor configuration logic executes unchanged.",
    "serial_separate_flags": serial_frames(frame + frame),
    "serial_shared_flag": serial_frames(frame + frame[1:]),
    "serial_idle_expired": serial_frames(list(frame[:3]) + [None, None, None] + list(frame[3:] + bytes([0x7e]) + frame)),
}
destination = Path(__file__).parent / "Fixtures" / "parity6-streams.json"
destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps({k: v for k, v in result.items() if k not in ("payload", "hdlc", "kiss", "serial_separate_flags", "serial_shared_flag")}, indent=2))
