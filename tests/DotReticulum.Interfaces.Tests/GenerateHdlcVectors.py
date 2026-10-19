"""Generates Fixtures/hdlc-vectors.json by EXECUTING the pinned upstream Reticulum TCP HDLC implementation.

Upstream: https://github.com/markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b
  RNS/Interfaces/TCPInterface.py  sha256 d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d
  RNS/Interfaces/SerialInterface.py sha256 676f7b038c2950b92ac71f29653cbbe35ba6513a53ca41742d43e19966e1ef1f (HDLC class/escape identical)
Run:      PYTHONPATH=<upstream checkout> python GenerateHdlcVectors.py > Fixtures/hdlc-vectors.json

Method (nothing is computed by DotReticulum):
  * encode: TCPClientInterface.process_outgoing() is executed on an instance whose socket records sendall(); the
    recorded bytes are the upstream frame (FLAG + HDLC.escape(packet) + FLAG).
  * decode: TCPClientInterface.read_loop() (the standard, non-KISS branch) is executed against a fake socket that
    replays the chunks in each case; every frame it hands to process_incoming() is recorded. HW_MTU is set to 500 so
    upstream's frame-length gate matches DotReticulum's MTU bound (real TCP interfaces use 262144).
The C# tests treat upstream's output as the oracle, except where DotReticulum intentionally hardens behaviour
(it additionally drops frames with invalid escapes and frames that are not parseable Reticulum packets); those cases are
flagged in the fixture via `upstream_passes_invalid_escape`/comments and asserted explicitly in the tests.
"""
import hashlib
import json

import RNS
from RNS.Interfaces.TCPInterface import HDLC, TCPClientInterface

PINNED_SHA = "e40191b3d193b46b7f2d8a44424a594cd758839b"
FLAG = 0x7E
ESC = 0x7D


def det(label, n):
    out = b""
    i = 0
    while len(out) < n:
        out += hashlib.sha256(f"{label}-{i}".encode()).digest()
        i += 1
    return out[:n]


def packet(label, payload_len, payload=None):
    # A valid header-1 data packet: flags=0 (data/single/broadcast), hops=0, 16-byte destination, context=0, payload.
    body = payload if payload is not None else det(label, payload_len)
    return bytes([0x00, 0x00]) + det(label + "-dest", 16) + bytes([0x00]) + body


def make_interface():
    iface = object.__new__(TCPClientInterface)
    iface.online = True
    iface.detached = False
    iface.kiss_framing = False
    iface.initiator = False
    iface.writing = False
    iface.txb = 0
    iface.ifac_size = None
    iface.HW_MTU = 500
    iface.name = "vector"
    iface.target_ip = "127.0.0.1"
    iface.target_port = "0"
    iface.teardown = lambda *a, **k: None
    return iface


class FakeSocket:
    def __init__(self, chunks):
        self.chunks = list(chunks)
        self.sent = []

    def recv(self, n):
        return self.chunks.pop(0) if self.chunks else b""

    def sendall(self, data):
        self.sent.append(bytes(data))


def upstream_encode(raw):
    iface = make_interface()
    iface.socket = FakeSocket([])
    iface.process_outgoing(raw)
    return b"".join(iface.socket.sent)


def upstream_decode(chunks):
    iface = make_interface()
    iface.socket = FakeSocket(chunks)
    frames = []
    iface.process_incoming = lambda data: frames.append(bytes(data))
    iface.read_loop()
    return frames


def split_bytes(data):
    return [bytes([b]) for b in data]


def main():
    assert RNS.__file__.replace("\\", "/").count("upstream-reticulum") == 1, RNS.__file__
    plain = packet("plain", 40)
    special = packet("special", 0, payload=bytes([FLAG, ESC, FLAG, FLAG, ESC, ESC, 0x5E, 0x5D, 0x00, FLAG]) + det("sp", 20))
    max_pkt = packet("max", 500 - 19)
    max_escaped = packet("maxesc", 0, payload=bytes([FLAG, ESC] * ((500 - 19) // 2)) + b"\x01" * ((500 - 19) % 2))
    oversize = packet("over", 501 - 19)
    assert len(max_pkt) == 500 and len(max_escaped) == 500 and len(oversize) == 501
    another = packet("another", 64)

    encode_inputs = {"plain": plain, "special_bytes": special, "max_packet": max_pkt, "max_all_escaped": max_escaped}
    encode = [
        {"name": name, "packet": raw.hex(), "frame": upstream_encode(raw).hex()}
        for name, raw in encode_inputs.items()
    ]

    frame = lambda raw: bytes([FLAG]) + HDLC.escape(raw) + bytes([FLAG])
    cases = {
        "single": [frame(plain)],
        "special_bytes": [frame(special)],
        "back_to_back_one_read": [frame(plain) + frame(special) + frame(another)],
        "shared_flag_between_frames": [bytes([FLAG]) + HDLC.escape(plain) + bytes([FLAG]) + HDLC.escape(another) + bytes([FLAG])],
        "byte_by_byte": split_bytes(frame(special) + frame(plain)),
        "split_mid_escape": [frame(special)[:5], frame(special)[5:]],
        "leading_garbage": [b"\x01\x02\x03" + frame(plain)],
        "garbage_between_frames": [frame(plain) + b"\x11\x22\x33\x44" + frame(another)],
        "long_garbage_between_frames": [frame(plain) + det("garbage", 60) + frame(another)],
        "empty_frames": [bytes([FLAG, FLAG, FLAG]) + frame(plain) + bytes([FLAG, FLAG])],
        "short_frame_dropped": [bytes([FLAG]) + det("short", 10) + bytes([FLAG]) + frame(plain)],
        "max_packet": [frame(max_pkt)],
        "max_all_escaped": [frame(max_escaped)],
        "oversize_dropped_then_recover": [frame(oversize) + frame(plain)],
        "truncated_escape_then_complete": [bytes([FLAG]) + plain[:10] + bytes([ESC]) + frame(another)],
    }
    decode = []
    for name, chunks in cases.items():
        decode.append({
            "name": name,
            "chunks": [c.hex() for c in chunks],
            "upstream_frames": [f.hex() for f in upstream_decode(chunks)],
        })

    # Documented divergence: upstream's unescape leaves an invalid escape sequence in the frame (it does not
    # discard it); DotReticulum discards the frame. Recorded here only so the difference is explicit.
    bad_escape_frame = bytes([FLAG]) + plain[:10] + bytes([ESC, 0x41]) + plain[10:] + bytes([FLAG])
    bad_escape = {
        "name": "invalid_escape",
        "chunks": [bad_escape_frame.hex(), frame(another).hex()],
        "upstream_frames": [f.hex() for f in upstream_decode([bad_escape_frame, frame(another)])],
        "upstream_passes_invalid_escape": True,
    }

    print(json.dumps({
        "upstream": "markqvist/Reticulum",
        "upstream_sha": PINNED_SHA,
        "tcp_interface_sha256": hashlib.sha256(open(__import__("RNS.Interfaces.TCPInterface", fromlist=["x"]).__file__, "rb").read()).hexdigest(),
        "packets": {
            "plain": plain.hex(), "special": special.hex(), "another": another.hex(),
            "max": max_pkt.hex(), "max_escaped": max_escaped.hex(), "oversize": oversize.hex(),
        },
        "encode": encode,
        "decode": decode,
        "divergence": [bad_escape],
    }, indent=2))


main()
