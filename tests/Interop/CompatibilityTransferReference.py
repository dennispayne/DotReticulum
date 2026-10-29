"""Execute pinned rncp, observing actual resources without replacing protocol handlers.

Windows uses the same disclosed copy-instead-of-move workaround as rncp_win_shim.py.
This does not establish unmodified upstream Windows file-retention behavior.
"""
import argparse
import json
import os
import shutil
import sys

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", required=True)
    parser.add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    verify_reference(expected_revision=args.revision)
    import RNS
    from RNS.Utilities import rncp

    def observe(resource, direction):
        print(json.dumps({
            "event": "resource",
            "direction": direction,
            "hash": resource.hash.hex(),
            "compressed": resource.is_compressed(),
            "total_size": resource.total_size,
            "status": resource.status,
            "segment": resource.segment_index,
            "segments": resource.total_segments,
            "hops": resource.link.expected_hops,
        }), file=sys.stderr, flush=True)

    original_started = rncp.receive_resource_started
    original_progress = rncp.sender_progress
    observed = set()

    def started(resource):
        observe(resource, "receive")
        original_started(resource)

    def progress(resource):
        if resource.hash not in observed:
            observed.add(resource.hash)
            observe(resource, "send")
        original_progress(resource)

    rncp.receive_resource_started = started
    rncp.sender_progress = progress
    if os.name == "nt":
        def copy_instead_of_move(source, destination, *unused_args, **unused_kwargs):
            shutil.copyfile(source, destination)
            return destination
        shutil.move = copy_instead_of_move
        print(json.dumps({"event": "windows_retention_shim", "operation": "copy-instead-of-move"}),
              file=sys.stderr, flush=True)

    arguments = args.arguments
    if arguments[:1] == ["--"]:
        arguments = arguments[1:]
    sys.argv = ["rncp", *arguments]
    rncp.main()


if __name__ == "__main__":
    main()
