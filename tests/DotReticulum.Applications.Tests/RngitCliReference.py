"""Execute the actual pinned rngit CLI; PowerShell owns inputs and assertions."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

verify_reference()

from RNS.Utilities.rngit.server import main

main()
