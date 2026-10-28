"""Thin pinned-RNS node for Parity 7 network-utility process tests."""
import argparse
import json
import sys
from pathlib import Path

from reference_environment import verify_reference


def emit(**values):
    print(json.dumps(values), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--probe-identity", required=True)
    parser.add_argument("--no-proof-identity", required=True)
    parser.add_argument("--revision", default=None)
    parser.add_argument("--proof-mode", choices=("missing", "invalid-signature", "wrong-hash", "truncated"),
                        default="missing")
    parser.add_argument("--observe-management", action="store_true")
    args = parser.parse_args()

    if args.revision:
        verify_reference(expected_revision=args.revision)
    else:
        verify_reference()
    import RNS

    config = Path(args.config).resolve(strict=True)
    probe_identity = RNS.Identity.from_file(args.probe_identity)
    no_proof_identity = RNS.Identity.from_file(args.no_proof_identity)
    node = RNS.Reticulum(configdir=str(config), loglevel=0, require_shared_instance=False)
    if args.observe_management:
        RNS.Transport.remote_management_destination.set_link_established_callback(
            lambda link: link.set_remote_identified_callback(
                lambda established_link, identity: emit(event="management_identified", identity=identity.hash.hex())))

    def received(destination, data, _packet):
        emit(event="probe_received", destination=destination.hex(), payload_bytes=len(data))

    probe = RNS.Destination(probe_identity, RNS.Destination.IN, RNS.Destination.SINGLE, "app", "aspect")
    probe.set_proof_strategy(RNS.Destination.PROVE_ALL)
    probe.set_packet_callback(lambda data, packet: received(probe.hash, data, packet))
    no_proof = RNS.Destination(no_proof_identity, RNS.Destination.IN, RNS.Destination.SINGLE, "app", "aspect")
    no_proof.set_proof_strategy(RNS.Destination.PROVE_NONE)
    def unproved_received(data, packet):
        received(no_proof.hash, data, packet)
        if args.proof_mode == "missing":
            return
        signature = no_proof_identity.sign(packet.packet_hash)
        proof_hash = packet.packet_hash
        if args.proof_mode == "invalid-signature":
            signature = bytes([signature[0] ^ 1]) + signature[1:]
        elif args.proof_mode == "wrong-hash":
            proof_hash = bytes([proof_hash[0] ^ 1]) + proof_hash[1:]
            signature = no_proof_identity.sign(proof_hash)
        proof_data = proof_hash + signature
        if args.proof_mode == "truncated":
            proof_data = proof_data[:-1]
        proof = RNS.Packet(packet.generate_proof_destination(), proof_data, RNS.Packet.PROOF,
                           attached_interface=packet.receiving_interface)
        proof.send()
        emit(event="proof_sent", mode=args.proof_mode, payload_bytes=len(proof_data), sent=proof.sent)

    no_proof.set_packet_callback(unproved_received)

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
