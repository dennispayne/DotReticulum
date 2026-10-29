"""Execute a reference CLI at an explicitly selected revision for drift checks."""
import argparse
import sys

from reference_environment import verify_reference

parser = argparse.ArgumentParser()
parser.add_argument("--revision", required=True)
parser.add_argument("--tool", choices=["rnid", "rnprobe", "rnstatus", "rnsd", "rnsh", "rnx"], required=True)
parser.add_argument("arguments", nargs=argparse.REMAINDER)
args = parser.parse_args()
verify_reference(expected_revision=args.revision)
from RNS.Utilities import rnid, rnprobe, rnstatus, rnsd

arguments = args.arguments
if arguments[:1] == ["--"]:
    arguments = arguments[1:]
sys.argv = [args.tool, *arguments]
tools = {"rnid": rnid.main, "rnprobe": rnprobe.main, "rnstatus": rnstatus.main, "rnsd": rnsd.main}
if args.tool == "rnsh":
    from RNS.Utilities.rnsh.rnsh import main
    tools["rnsh"] = main
elif args.tool == "rnx":
    from RNS.Utilities.rnx import main
    tools["rnx"] = main
tools[args.tool]()
