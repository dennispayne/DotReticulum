"""Execute a reference CLI at an explicitly selected revision for drift checks."""
import argparse
import sys

from reference_environment import verify_reference

parser = argparse.ArgumentParser()
parser.add_argument("--revision", required=True)
parser.add_argument("--tool", choices=["rnid"], required=True)
parser.add_argument("arguments", nargs=argparse.REMAINDER)
args = parser.parse_args()
verify_reference(expected_revision=args.revision)
from RNS.Utilities import rnid

arguments = args.arguments
if arguments[:1] == ["--"]:
    arguments = arguments[1:]
sys.argv = [args.tool, *arguments]
rnid.main()
