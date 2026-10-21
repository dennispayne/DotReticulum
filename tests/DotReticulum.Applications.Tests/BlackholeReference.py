"""Thin live adapter: actual pinned publisher, updater and transport over TCP.

stdin commands: announce, fetch <identity>, quit. No reference-side wire shims.
This bridge never implements packet encoding, publication or merge behavior.
"""
import argparse
import json
from pathlib import Path
import sys
import threading

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
import RNS
from RNS.Discovery import BlackholeUpdater
from RNS.vendor import umsgpack as mp


def emit(event, **fields):
    print("@@" + json.dumps({"event": event, **fields}), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--source-identity", required=True)
    args = parser.parse_args()
    verify_reference()
    root = Path(args.config)
    root.mkdir(parents=True, exist_ok=True)
    (root / "config").write_text(
        f"""[reticulum]
share_instance = no
publish_blackhole = yes
[interfaces]
  [[Native]]
    enabled = yes
    type = TCPClientInterface
    target_host = 127.0.0.1
    target_port = {args.port}
""", encoding="utf-8")
    node = RNS.Reticulum(str(root), loglevel=RNS.LOG_ERROR)
    blocked = bytes.fromhex("77" * 16)
    if not node.blackhole_identity(blocked, reason="python publication"):
        raise RuntimeError("Could not add Python publication entry")
    emit("ready", identity=RNS.Transport.identity.hash.hex(),
         destination=RNS.Transport.blackhole_destination.hash.hex())
    for line in sys.stdin:
        command = line.strip().split()
        if not command:
            continue
        if command[0] == "quit":
            return
        if command[0] == "announce":
            RNS.Transport.blackhole_destination.announce()
            emit("announced")
        elif command[0] == "fetch":
            source = bytes.fromhex(command[1])
            if source.hex() != args.source_identity:
                raise RuntimeError("Unexpected source identity")
            destination_hash = RNS.Destination.hash_from_name_and_identity("rnstransport.info.blackhole", source)
            if not RNS.Transport.await_path(destination_hash, timeout=15):
                raise RuntimeError("Native publication path unavailable")
            identity = RNS.Identity.recall(destination_hash)
            if identity.hash != source:
                raise RuntimeError("Native publication identity mismatch")
            destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE,
                                          "rnstransport", "info", "blackhole")
            finished = threading.Event()
            errors = []

            def update(link):
                try:
                    BlackholeUpdater().update_link_established(link)
                except (ValueError, OSError, TypeError) as error:
                    errors.append(str(error))
                finally:
                    finished.set()

            link = RNS.Link(destination, established_callback=update)
            try:
                if not finished.wait(timeout=30):
                    raise RuntimeError("Native publication update timed out")
                if errors:
                    raise RuntimeError(errors[0])
                emit("updated", packed=mp.packb(RNS.Transport.blackholed_identities).hex())
            finally:
                link.teardown()
        else:
            raise RuntimeError("Unknown command")


if __name__ == "__main__":
    main()
