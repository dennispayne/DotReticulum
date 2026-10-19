"""Thin actual-reference CLI adapter for the isolated Git/Pester fixture."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

verify_reference()
from RNS.Utilities.rngit.commitsigs import main

sys.exit(main())
