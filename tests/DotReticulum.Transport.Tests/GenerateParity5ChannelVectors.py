"""Execute pinned Channel/Buffer window, ordering and stream algorithms.

The synthetic outlet only supplies state/receipt/I/O stimuli; it is not evidence
for Packet.resend encryption or hash behavior. That separate real-method oracle
is GenerateParity5ChannelRetryCryptoVectors.py.
"""
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

verify_reference()
channel_module = importlib.import_module("RNS.Channel")
buffer_module = importlib.import_module("RNS.Buffer")
Channel = channel_module.Channel
Envelope = channel_module.Envelope
MessageState = channel_module.MessageState


class Message(channel_module.MessageBase):
    MSGTYPE = 0x0101

    def __init__(self, data=b""):
        self.data = data

    def pack(self):
        return self.data

    def unpack(self, raw):
        self.data = raw


class Receipt:
    def __init__(self):
        self.timeout = 0

    def set_timeout(self, value):
        self.timeout = value


class Packet:
    def __init__(self, raw):
        self.raw = raw
        self.receipt = Receipt()
        self.state = MessageState.MSGSTATE_SENT
        self.delivered = None
        self.timeout = None


class Outlet:
    is_usable = True
    mdu = 431

    def __init__(self, rtt):
        self.rtt = rtt
        self.sent = []
        self.resends = 0
        self.closed = False

    def send(self, raw):
        packet = Packet(raw)
        self.sent.append(packet)
        return packet

    def resend(self, packet):
        packet.state = MessageState.MSGSTATE_SENT
        self.resends += 1
        return packet

    def get_packet_state(self, packet):
        return packet.state

    def get_packet_id(self, packet):
        return id(packet)

    def set_packet_delivered_callback(self, packet, callback):
        packet.delivered = callback

    def set_packet_timeout_callback(self, packet, callback, timeout=None):
        packet.timeout = callback
        if timeout is not None:
            packet.receipt.timeout = timeout

    def timed_out(self):
        self.closed = True


def window(channel):
    return [channel.window, channel.window_min, channel.window_max]


def receive_case(start, sequences):
    channel = Channel(Outlet(0.1))
    channel._next_rx_sequence = start
    channel.register_message_type(Message)
    delivered = []
    channel.add_message_handler(lambda message: delivered.append(message.data.hex()))
    steps = []
    for sequence in sequences:
        raw = Envelope(channel._outlet, Message(sequence.to_bytes(2, "big")), sequence=sequence).pack()
        channel._receive(raw)
        steps.append({"sequence": sequence, "delivered": delivered.copy(),
                      "next": channel._next_rx_sequence, "buffered": len(channel._rx_ring)})
    return {"start": start, "steps": steps}


def main():
    root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
    output = {
        "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
        "sourceHashes": {name: hashlib.sha256((root / "RNS" / name).read_bytes()).hexdigest()
                         for name in ["Channel.py", "Buffer.py"]},
        "importedModules": {name: str(Path(module.__file__).relative_to(root))
                            for name, module in [("Channel", channel_module), ("Buffer", buffer_module)]},
        "envelopes": [], "windows": [], "receive": [], "retries": [],
    }
    outlet = Outlet(0.1)
    for sequence in [0, 1, 65534, 65535]:
        output["envelopes"].append({"sequence": sequence,
                                   "raw": Envelope(outlet, Message(b"abc"), sequence=sequence).pack().hex()})
    for rtt in [0, 0.18, 0.180001, 0.75, 0.750001, 1.45, 1.450001]:
        outlet = Outlet(rtt)
        channel = Channel(outlet)
        steps = [window(channel)]
        for _ in range(12):
            envelope = channel.send(Message(b"x"))
            envelope.packet.state = MessageState.MSGSTATE_DELIVERED
            envelope.packet.delivered(envelope.packet)
            steps.append(window(channel))
        output["windows"].append({"rtt": rtt, "steps": steps})
    for start, sequences in [
        (0, [2, 1, 2, 0, 0, 3, 52, 51]),
        (65534, [0, 1, 65535, 0, 65534, 65535, 2]),
        (65535, [47, 48, 65534, 0, 65535, 1]),
    ]:
        output["receive"].append(receive_case(start, sequences))
    for rtt in [0, 0.18, 0.75, 1.450001]:
        outlet = Outlet(rtt)
        channel = Channel(outlet)
        envelope = channel.send(Message(b"retry"))
        steps = []
        for _ in range(5):
            steps.append({"tries": envelope.tries, "timeout": envelope.packet.receipt.timeout,
                          "window": window(channel)})
            envelope.packet.state = MessageState.MSGSTATE_FAILED
            envelope.packet.timeout(envelope.packet)
        output["retries"].append({"rtt": rtt, "steps": steps,
                                  "resends": outlet.resends, "closed": outlet.closed})
    outlet = Outlet(0.18)
    channel = Channel(outlet)
    for _ in range(40):
        envelope = channel.send(Message(b"warmup"))
        envelope.packet.state = MessageState.MSGSTATE_DELIVERED
        envelope.packet.delivered(envelope.packet)
    envelope = channel.send(Message(b"shrink"))
    output["lossWindow"] = [window(channel)]
    for _ in range(4):
        envelope.packet.state = MessageState.MSGSTATE_FAILED
        envelope.packet.timeout(envelope.packet)
        output["lossWindow"].append(window(channel))
    envelope.packet.state = MessageState.MSGSTATE_DELIVERED
    envelope.packet.delivered(envelope.packet)
    output["lossWindow"].append(window(channel))
    outlet = Outlet(0.18)
    channel = Channel(outlet)
    first = channel.send(Message(b"first"))

    def timeout_state():
        return [{"sequence": item.sequence, "tries": item.tries, "timeout": item.packet.receipt.timeout}
                for item in channel._tx_ring]

    output["inflightTimeouts"] = [timeout_state()]
    second = channel.send(Message(b"second"))
    output["inflightTimeouts"].append(timeout_state())
    first.packet.state = MessageState.MSGSTATE_FAILED
    first.packet.timeout(first.packet)
    output["inflightTimeouts"].append(timeout_state())
    second.packet.state = MessageState.MSGSTATE_DELIVERED
    second.packet.delivered(second.packet)
    output["inflightTimeouts"].append(timeout_state())
    channel.send(Message(b"third"))
    output["inflightTimeouts"].append(timeout_state())
    malformed_length = bytearray(Envelope(outlet, Message(b"payload"), sequence=0).pack())
    malformed_length[4:6] = b"\0\0"
    output["ignoredDeclaredLength"] = {
        "raw": malformed_length.hex(),
        "data": Envelope(outlet, raw=bytes(malformed_length)).unpack({Message.MSGTYPE: Message}).data.hex(),
    }
    output["streams"] = []
    for stream_id, eof, compressed, data in [
        (0, False, False, b""), (16383, True, False, b"abc"),
        (7, False, True, buffer_module.bz2.compress(b"a" * 16384)),
    ]:
        message = buffer_module.StreamDataMessage(stream_id, data, eof, compressed)
        raw = message.pack()
        decoded = buffer_module.StreamDataMessage()
        decoded.unpack(raw)
        output["streams"].append({"raw": raw.hex(), "id": decoded.stream_id,
                                  "eof": decoded.eof, "compressed": decoded.compressed,
                                  "data": decoded.data.hex()})
    oversized_raw = buffer_module.StreamDataMessage(
        7, buffer_module.bz2.compress(b"a" * 16385), compressed=True).pack()
    try:
        buffer_module.StreamDataMessage().unpack(oversized_raw)
        raise AssertionError("The pinned reference unexpectedly accepted an oversized chunk")
    except OSError as error:
        output["oversizedCompressedChunk"] = {"raw": oversized_raw.hex(), "error": str(error)}
    destination = Path(__file__).parent / "Fixtures" / "parity5-channel-vectors.json"
    destination.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {destination}; imported {channel_module.__file__} and {buffer_module.__file__}")


if __name__ == "__main__":
    main()
