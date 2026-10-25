"""Thin pinned-RNS node for Parity 7 network-utility process tests."""
import argparse
import json
import sys
from pathlib import Path

from reference_environment import verify_reference

verify_reference()
import RNS


def emit(**values):
    print(json.dumps(values), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--probe-identity", required=True)
    parser.add_argument("--no-proof-identity", required=True)
    args = parser.parse_args()

    config = Path(args.config).resolve(strict=True)
    probe_identity = RNS.Identity.from_file(args.probe_identity)
    no_proof_identity = RNS.Identity.from_file(args.no_proof_identity)
    node = RNS.Reticulum(configdir=str(config), loglevel=0, require_shared_instance=False)

    def received(destination, data, _packet):
        emit(event="probe_received", destination=destination.hex(), payload_bytes=len(data))

    probe = RNS.Destination(probe_identity, RNS.Destination.IN, RNS.Destination.SINGLE, "app", "aspect")
    probe.set_proof_strategy(RNS.Destination.PROVE_ALL)
    probe.set_packet_callback(lambda data, packet: received(probe.hash, data, packet))
    no_proof = RNS.Destination(no_proof_identity, RNS.Destination.IN, RNS.Destination.SINGLE, "app", "aspect")
    no_proof.set_proof_strategy(RNS.Destination.PROVE_NONE)
    no_proof.set_packet_callback(lambda data, packet: received(no_proof.hash, data, packet))

    emit(event="ready", revision=RNS.__version__, module=str(Path(RNS.__file__).resolve()),
         transport_identity=RNS.Transport.identity.hash.hex(),
         remote_management=RNS.Transport.remote_management_destination.hash.hex(),
         probe=probe.hash.hex(), no_proof=no_proof.hash.hex())
    try:
        for line in sys.stdin:
            command = line.strip()
            if command == "quit":
                break
            if command == "announce":
                probe.announce()
                no_proof.announce()
                if RNS.Transport.remote_management_destination is not None:
                    RNS.Transport.remote_management_destination.announce()
                emit(event="announced")
    finally:
        RNS.Reticulum.exit_handler()


if __name__ == "__main__":
    main()
