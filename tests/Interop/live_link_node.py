"""Live Python Reticulum node for the DotReticulum P0-6 Links / Resources interop checks.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (set PYTHONPATH to the checkout).
Driven by tests/Interop/PyLinkNode.cs from DOTRETICULUM_LIVE=1 xUnit tests (DotReticulum.Transport.Tests/LiveLinkTests.cs,
DotReticulum.Applications.Tests/LiveLinkTransportTests.cs); not a unit test.

One real RNS instance (no shared instance, no transport). Interfaces: --tcp-server-port opens a TCPServerInterface on 127.0.0.1
(the .NET side connects to it), --rnsd-port opens a TCPClientInterface to a .NET rnsd at 127.0.0.1.
It owns one SINGLE IN destination dotreticulum.interop.link<name> with PROVE_ALL, accepts links (resource strategy ACCEPT_ALL) and
serves the request paths /echo (returns the request data), /reverse (returns the request data reversed) and /file (returns a
RNS.Resource-sized file response, request data = "<size>:<seed>:<random|text>").

Deterministic payloads: "random" = random.Random(seed).randbytes(size), "text" = a repeated text block (compressible).

stdout protocol: lines starting with "@@" followed by JSON (other lines are RNS logging):
  {"event":"ready", "dest", "identity_hash", "rns_file"}
  {"event":"announce", "dest", "app_data", "hops"}          announce accepted by upstream (any aspect)
  {"event":"announced", "dest"}                              reply to "announce"
  {"event":"path", "dest", "has_path", "hops"}               reply to "path <hex>" / "request_path <hex> <timeout s>"
  {"event":"link_up", "role", "link_id", "mtu", "mdu"}       link established (initiator: proof validated; responder: request accepted)
  {"event":"link_failed", "reason"}                          initiator link did not come up within 30 s
  {"event":"link_closed", "role", "reason"}                  teardown reason: 1 timeout, 2 initiator closed, 3 destination closed
  {"event":"data", "payload", "link_id"}                     packet received over the link
  {"event":"sent", "ok", "delivered"}                        reply to "send <hex>" (delivered = receipt proof validated, waited 15 s)
  {"event":"stat", "status", "rtt", "keepalive", "stale_time", "last_inbound_age", "mtu", "mdu", "remote_identity"}   reply to "stat"
  {"event":"identified", "identity_hash"}                    the peer identified itself on the link
  {"event":"resource_started", "size"}                       incoming resource accepted
  {"event":"resource_done", "status", "size", "sha256", "metadata", "compressed", "segments"}   incoming resource concluded
  {"event":"resource_sent", "status", "size", "sha256", "compressed"}   reply to "resource ..." once the transfer concluded
  {"event":"response", "ok", "size", "sha256", "text", "metadata"}      reply to "request ..."
  {"event":"channel_msg", "text"}                            a TextMessage (MSGTYPE 0x0101, utf-8 text) arrived on the channel
  {"event":"channel_sent", "text"} / {"event":"channel_burst_done", "count", "window"}   replies to chsend / chburst
  {"event":"buffer_done", "size", "sha256"}                  stream 1 of the channel reached EOF
  {"event":"buffer_sent", "size", "sha256"}                  reply to bufsend (stream 2 written and closed)
stdin commands: announce [hex] | path <hex> | request_path <hex> <s> | connect <desthash hex> |
  send <hex> | close | stat | identify | resource <size> <seed> <random|text> [metadata-text] |
  request <path> <data text | size:seed:mode> | chsend <text> | chburst <n> <prefix> | bufsend <size> <seed> <random|text> [stream-id=2] | quit (EOF also quits)
"""
import argparse
import hashlib
import json
import os
import random
import sys
import threading
import time

import RNS
from reference_environment import verify_reference
from RNS.vendor import umsgpack

CHANNEL = sys.modules["RNS.Channel"]
BUFFER = sys.modules["RNS.Buffer"]
LOCK = threading.Lock()
STATE = {"link": None, "role": None}


def emit(obj):
    with LOCK:
        sys.stdout.write("@@" + json.dumps(obj) + "\n")
        sys.stdout.flush()


def write_config(path, tcp_server_port, rnsd_port):
    interfaces = ""
    if tcp_server_port:
        interfaces += f"""  [[PyTCPServer]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {tcp_server_port}
"""
    if rnsd_port:
        interfaces += f"""  [[ToDotNet]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {rnsd_port}
"""
    config = f"""[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = dotreticulum_interop_p06
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
{interfaces}"""
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, "config"), "w", encoding="utf-8") as f:
        f.write(config)


def make_payload(seed, size, mode):
    if mode == "text":
        block = (seed + " the quick brown fox jumps over the lazy dog. ").encode()
        return (block * (size // len(block) + 1))[:size]
    return random.Random(seed).randbytes(size)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class Handler:
    aspect_filter = None

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit({"event": "announce", "dest": destination_hash.hex(), "app_data": app_data.hex() if app_data else "",
              "hops": RNS.Transport.hops_to(destination_hash)})


def read_resource(resource):
    data = resource.data
    if hasattr(data, "read"):
        data.seek(0)
        data = data.read()
    return data


class TextMessage(CHANNEL.MessageBase):
    MSGTYPE = 0x0101

    def __init__(self, text=""):
        self.text = text

    def pack(self):
        return self.text.encode()

    def unpack(self, raw):
        self.text = bytes(raw).decode()


def configure_channel(link):
    channel = link.get_channel()
    channel.register_message_type(TextMessage)
    def on_message(message):
        if not isinstance(message, TextMessage):
            return False
        emit({"event": "channel_msg", "text": message.text})
        return True
    channel.add_message_handler(on_message)

    raw_reader = BUFFER.RawChannelReader(1, channel)
    acc = {"data": bytearray(), "done": False}

    lock = threading.Lock()

    def on_ready(n):
        with lock:
            chunk = raw_reader.read(max(n, 1))
            if chunk:
                acc["data"].extend(chunk)
            if raw_reader._eof and not raw_reader._buffer and not acc["done"]:
                acc["done"] = True
                emit({"event": "buffer_done", "size": len(acc["data"]), "sha256": sha256(bytes(acc["data"]))})

    raw_reader.add_ready_callback(on_ready)


def configure_link(link, role, identity_box):
    STATE["link"] = link
    STATE["role"] = role
    configure_channel(link)

    def on_packet(message, packet):
        emit({"event": "data", "payload": message.hex(), "link_id": link.link_id.hex()})

    def on_closed(l):
        emit({"event": "link_closed", "role": role, "reason": int(l.teardown_reason) if l.teardown_reason else 0})

    def on_identified(l, identity):
        emit({"event": "identified", "identity_hash": identity.hash.hex()})

    def on_resource_started(resource):
        emit({"event": "resource_started", "size": resource.total_size if hasattr(resource, "total_size") else resource.size})

    def on_resource_concluded(resource):
        try:
            payload = read_resource(resource)
            meta = resource.metadata
            emit({"event": "resource_done", "status": int(resource.status), "size": len(payload), "sha256": sha256(payload),
                  "metadata": json.dumps(meta, default=lambda o: o.hex() if isinstance(o, bytes) else str(o)) if meta is not None else None,
                  "compressed": bool(resource.compressed), "segments": resource.total_segments})
        except Exception as e:
            emit({"event": "resource_done", "status": -1, "size": 0, "sha256": "", "metadata": None, "compressed": False,
                  "segments": 0, "error": str(e)})

    link.set_packet_callback(on_packet)
    link.set_link_closed_callback(on_closed)
    link.set_remote_identified_callback(on_identified)
    link.set_resource_strategy(RNS.Link.ACCEPT_ALL)
    link.set_resource_started_callback(on_resource_started)
    link.set_resource_concluded_callback(on_resource_concluded)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--name", default="")
    args = parser.parse_args()

    verify_reference()
    write_config(args.config, args.tcp_server_port, args.rnsd_port)
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    RNS.Transport.register_announce_handler(Handler())

    identity = RNS.Identity()
    dest = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "dotreticulum", "interop", "link" + args.name)
    dest.set_proof_strategy(RNS.Destination.PROVE_ALL)
    dest.set_link_established_callback(lambda link: (configure_link(link, "responder", identity),
                                                      emit({"event": "link_up", "role": "responder", "link_id": link.link_id.hex(),
                                                            "mtu": link.mtu, "mdu": link.mdu})))

    def echo(path, data, request_id, link_id, remote_identity, requested_at):
        return data

    def reverse(path, data, request_id, link_id, remote_identity, requested_at):
        return bytes(reversed(data)) if isinstance(data, (bytes, bytearray)) else data

    def file_response(path, data, request_id, link_id, remote_identity, requested_at):
        size, seed, mode = data.split(":")
        file_path = os.path.join(args.config, f"file-response-{request_id.hex()}.bin")
        with open(file_path, "wb") as f:
            f.write(make_payload(seed, int(size), mode))
        return [open(file_path, "rb"), {"seed": seed, "size": int(size)}]

    dest.register_request_handler("/echo", response_generator=echo, allow=RNS.Destination.ALLOW_ALL)
    dest.register_request_handler("/reverse", response_generator=reverse, allow=RNS.Destination.ALLOW_ALL)
    dest.register_request_handler("/file", response_generator=file_response, allow=RNS.Destination.ALLOW_ALL,
                                  auto_compress=True)

    emit({"event": "ready", "dest": dest.hash.hex(), "identity_hash": identity.hash.hex(), "rns_file": RNS.__file__})

    for line in sys.stdin:
        parts = line.rstrip("\r\n").split(" ")
        if not parts or not parts[0]:
            continue
        cmd = parts[0]
        link = STATE["link"]
        try:
            if cmd == "interfaces":
                emit({"event": "interfaces", "interfaces": [
                    {"name": interface.name, "online": interface.online}
                    for interface in RNS.Transport.interfaces
                ]})
            elif cmd == "announce":
                dest.announce(app_data=bytes.fromhex(parts[1]) if len(parts) > 1 else b"dotreticulum-p06")
                emit({"event": "announced", "dest": dest.hash.hex()})
            elif cmd in ("path", "request_path"):
                h = bytes.fromhex(parts[1])
                if cmd == "request_path":
                    RNS.Transport.request_path(h)
                    deadline = time.time() + (float(parts[2]) if len(parts) > 2 else 10.0)
                    while time.time() < deadline and not RNS.Transport.has_path(h):
                        time.sleep(0.1)
                emit({"event": "path", "dest": parts[1], "has_path": RNS.Transport.has_path(h), "hops": RNS.Transport.hops_to(h)})
            elif cmd == "connect":
                h = bytes.fromhex(parts[1])
                remote = RNS.Identity.recall(h)
                if remote is None:
                    emit({"event": "link_failed", "reason": "identity not recalled"})
                    continue
                out = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "dotreticulum", "interop", "link" + parts[2] if len(parts) > 2 else "link")
                up = threading.Event()

                def established(l):
                    configure_link(l, "initiator", identity)
                    up.set()
                    emit({"event": "link_up", "role": "initiator", "link_id": l.link_id.hex(), "mtu": l.mtu, "mdu": l.mdu})

                new_link = RNS.Link(out, established_callback=established)
                if not up.wait(30.0):
                    emit({"event": "link_failed", "reason": "timeout"})
            elif cmd == "send":
                packet = RNS.Packet(link, bytes.fromhex(parts[1]))
                receipt = packet.send()
                delivered = threading.Event()
                if receipt:
                    receipt.set_delivery_callback(lambda r: delivered.set())
                ok = receipt is not False and receipt is not None
                emit({"event": "sent", "ok": ok, "delivered": delivered.wait(15.0) if ok else False})
            elif cmd == "close":
                link.teardown()
            elif cmd == "stat":
                emit({"event": "stat", "status": int(link.status), "rtt": link.rtt, "keepalive": link.keepalive,
                      "stale_time": link.stale_time, "last_inbound_age": time.time() - link.last_inbound,
                      "mtu": link.mtu, "mdu": link.mdu,
                      "remote_identity": link.get_remote_identity().hash.hex() if link.get_remote_identity() else None})
            elif cmd == "identify":
                link.identify(identity)
            elif cmd == "resource":
                size, seed, mode = int(parts[1]), parts[2], parts[3]
                metadata = parts[4] if len(parts) > 4 else None
                payload = make_payload(seed, size, mode)
                done = threading.Event()
                box = {}

                def concluded(resource):
                    box["status"] = int(resource.status)
                    box["compressed"] = bool(resource.compressed)
                    done.set()

                RNS.Resource(payload, link, metadata=metadata, callback=concluded, auto_compress=True)
                done.wait(300.0)
                emit({"event": "resource_sent", "status": box.get("status", -1), "size": size, "sha256": sha256(payload),
                      "compressed": box.get("compressed", False)})
            elif cmd == "request":
                path = parts[1]
                text = " ".join(parts[2:])
                data = text.encode() if path in ("/echo", "/reverse") else text
                done = threading.Event()
                box = {}

                def response(receipt):
                    resp = receipt.response
                    if hasattr(resp, "read"):
                        resp.seek(0)
                        resp = resp.read()
                    box["body"] = resp
                    box["receipt"] = receipt
                    done.set()

                def failed(receipt):
                    box["failed"] = True
                    done.set()

                link.request(path, data=data, response_callback=response, failed_callback=failed, timeout=120)
                done.wait(130.0)
                r = box.get("receipt")
                if r is None:
                    emit({"event": "response", "ok": False, "size": 0, "sha256": "", "text": None, "metadata": None})
                else:
                    resp = box["body"]
                    meta = r.metadata if hasattr(r, "metadata") else None
                    body = resp if isinstance(resp, (bytes, bytearray)) else str(resp).encode()
                    emit({"event": "response", "ok": True, "size": len(body), "sha256": sha256(body),
                          "text": body.decode("utf-8", "replace") if len(body) < 256 else None,
                          "metadata": json.dumps(meta, default=str) if meta is not None else None})
            elif cmd == "chsend":
                channel = link.get_channel()
                deadline = time.time() + 30
                while not channel.is_ready_to_send() and time.time() < deadline:
                    time.sleep(0.01)
                channel.send(TextMessage(" ".join(parts[1:])))
                emit({"event": "channel_sent", "text": " ".join(parts[1:])})
            elif cmd == "chburst":
                channel = link.get_channel()
                count, prefix = int(parts[1]), parts[2]
                for i in range(count):
                    deadline = time.time() + 30
                    while not channel.is_ready_to_send() and time.time() < deadline:
                        time.sleep(0.005)
                    channel.send(TextMessage(f"{prefix}{i}"))
                emit({"event": "channel_burst_done", "count": count, "window": channel.window})
            elif cmd == "bufsend":
                size, seed, mode = int(parts[1]), parts[2], parts[3]
                payload = make_payload(seed, size, mode)
                writer = RNS.Buffer.create_writer(int(parts[4]) if len(parts) > 4 else 2, link.get_channel())
                writer.write(payload)
                writer.flush()
                writer.close()
                emit({"event": "buffer_sent", "size": size, "sha256": sha256(payload)})
            elif cmd == "quit":
                break
        except Exception as e:
            sys.stderr.write(f"command failed: {line!r}: {e!r}\n")
            emit({"event": "error", "command": cmd, "message": repr(e)})

    os._exit(0)


if __name__ == "__main__":
    main()
