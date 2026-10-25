"""Thin owned live-peer adapter exercising pinned identity announces and path responses."""
import argparse
import json
from pathlib import Path
import sys
import threading

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
import RNS


def emit(event, **fields):
    print("@@" + json.dumps({"event": event, **fields}), flush=True)


class Announcements:
    aspect_filter = "rns.id"

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit("announce", destination=destination_hash.hex(), identity=announced_identity.hexhash)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    root = Path(args.config)
    root.mkdir(parents=True)
    (root / "config").write_text(f"""[reticulum]
share_instance = no
enable_transport = yes
[interfaces]
  [[Owned server]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.port}
""", encoding="utf-8")
    node = RNS.Reticulum(str(root), loglevel=RNS.LOG_ERROR)
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(bytes(reversed(range(64))))
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "rns", "id")
    RNS.Transport.register_announce_handler(Announcements())
    timer = threading.Timer(90, lambda: __import__("os")._exit(124))
    timer.daemon = True
    timer.start()
    emit("ready", identity=identity.hexhash, destination=destination.hexhash)
    try:
        for line in sys.stdin:
            if line.strip() == "announce":
                destination.announce()
                emit("sent")
            elif line.strip() == "quit":
                return
            else:
                raise ValueError("Unknown adapter command")
    finally:
        timer.cancel()


if __name__ == "__main__":
    main()
