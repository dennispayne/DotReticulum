"""Run the actual reference Git helper, observing received bundle sizes only."""
import argparse
import json
import os
import sys

from reference_environment import verify_reference

parser = argparse.ArgumentParser()
parser.add_argument("--revision", required=True)
parser.add_argument("--url", required=True)
parser.add_argument("--observations", required=True)
args = parser.parse_args()
verify_reference(expected_revision=args.revision)

from RNS.Utilities.rngit.client import ReticulumGitClient, main

send_request = ReticulumGitClient.send_request


def observe_request(self, path, data, *positional, **keywords):
    response, metadata = send_request(self, path, data, *positional, **keywords)
    if path == self.PATH_FETCH and hasattr(response, "fileno"):
        with open(args.observations, "a", encoding="utf-8") as output:
            output.write(json.dumps({"bundle_bytes": os.fstat(response.fileno()).st_size}) + "\n")
    return response, metadata


ReticulumGitClient.send_request = observe_request
sys.argv = ["git-remote-rns", "reference", args.url]
main()
