"""Observe actual allowlisted upstream rnx request resources and delegated results."""
import argparse
import json
import threading

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--allowed", required=True)
    args = parser.parse_args()
    verify_reference(expected_revision=args.revision)

    import RNS
    import RNS.vendor.umsgpack as umsgpack
    from RNS.Utilities import rnx

    links = {}
    output_lock = threading.Lock()

    def emit(event, **fields):
        with output_lock:
            print("@@" + json.dumps(dict(event=event, **fields)), flush=True)

    original_established = rnx.command_link_established
    original_execute = rnx.execute_received_command

    def established(link):
        original_established(link)
        links[link.link_id] = link
        def resource_started(resource):
            advertisement = resource.advertisement_packet
            request_id = RNS.ResourceAdvertisement.read_request_id(advertisement)
            emit("request_resource", request_id=request_id.hex() if request_id else None,
                 is_request=RNS.ResourceAdvertisement.is_request(advertisement),
                 link_id=link.link_id.hex(), mdu=link.mdu)

        link.set_resource_started_callback(resource_started)

    def execute(path, data, request_id, link_id, remote_identity, requested_at):
        link = links[link_id]
        packed = umsgpack.packb([requested_at, RNS.Identity.truncated_hash(path.encode("utf-8")), data])
        result = original_execute(path, data, request_id, remote_identity, requested_at)
        emit("execution", request_id=request_id.hex(), packed_request_id=RNS.Identity.truncated_hash(packed).hex(),
             identity=remote_identity.hash.hex() if remote_identity else None, mdu=link.mdu,
             request_bytes=len(packed), response_bytes=len(umsgpack.packb([request_id, result])),
             executed=result[0], exit_code=result[1], stdout_bytes=result[4], stderr_bytes=result[5])
        return result

    rnx.command_link_established = established
    rnx.execute_received_command = execute
    rnx.listen(args.config, quietness=3, allowed=[args.allowed])


if __name__ == "__main__":
    main()
