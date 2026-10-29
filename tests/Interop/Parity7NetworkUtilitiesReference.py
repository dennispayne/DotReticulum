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
    parser.add_argument("--interface-observation", type=Path)
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
        management_links = {}

        def established(link):
            management_links[link.link_id] = link
            link.set_remote_identified_callback(
                lambda established_link, identity: emit(
                    event="management_identified", identity=identity.hash.hex()))

        def status_response(path, data, request_id, link_id, remote_identity, requested_at):
            response = RNS.Transport.remote_status_handler(
                path, data, request_id, link_id, remote_identity, requested_at)
            if response is not None:
                from RNS.vendor import umsgpack
                emit(event="management_response", request_id=request_id.hex(), link_id=link_id.hex(),
                     identity=remote_identity.hash.hex(),
                     packed_bytes=len(umsgpack.packb([request_id, response])),
                     mdu=management_links[link_id].mdu,
                     interface_names=[interface["name"] for interface in response[0]["interfaces"]])
            return response

        RNS.Transport.remote_management_destination.set_link_established_callback(
            established)
        RNS.Transport.remote_management_destination.register_request_handler(
            "/status", response_generator=status_response, allow=RNS.Destination.ALLOW_LIST,
            allowed_list=RNS.Transport.remote_management_allowed)

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
            elif command == "interfaces":
                names = [
                    str(interface) for interface in RNS.Transport.interfaces
                    if str(interface).startswith("TCPInterface[Client on Candidate/")]
                if args.interface_observation is not None:
                    pending = args.interface_observation.with_suffix(".pending")
                    pending.write_text(json.dumps(names), encoding="utf-8")
                    pending.replace(args.interface_observation)
                emit(event="interfaces", names=names)
    finally:
        RNS.Reticulum.exit_handler()


if __name__ == "__main__":
    main()
