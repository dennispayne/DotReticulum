"""Execute the pinned placeholder entry points with captured runtime initialization."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
from RNS.Utilities import rnir, rnpkg
from RNS.Resolver import Resolver

cases = []
for module in (rnir, rnpkg):
    tool = module.__name__.split(".")[-1]
    arguments = [[], ["--version"], ["--help"], ["--config", "captured-config", "-v", "-q", "-v"], ["install", "example"]]
    if tool == "rnpkg":
        arguments.append(["--exampleconfig"])
    for args in arguments:
        output, error, initialized = io.StringIO(), io.StringIO(), []
        def initialize(**kwargs):
            initialized.append(kwargs)
        with patch.object(sys, "argv", [tool, *args]), patch.object(module.RNS, "Reticulum", side_effect=initialize):
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
                try:
                    module.main()
                    code = 0
                except SystemExit as ex:
                    code = ex.code or 0
        cases.append({"tool": tool, "args": args, "code": code, "stdout": output.getvalue(),
                      "stderr": error.getvalue(), "initialization": initialized})

sources = [Path(rnir.__file__), Path(rnpkg.__file__), Path(os.environ["DOTRETICULUM_UPSTREAM"]) / "RNS" / "Resolver.py"]
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "adapter": "Reticulum constructor captured, so no real node startup, configuration persistence or network evidence.",
    "sources": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
    "resolver_result": Resolver.resolve_identity("example.invalid"),
    "cases": cases,
}
target = Path(__file__).parent / "Fixtures" / "upstream-placeholder-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(f"Verified imports: {rnir.__file__}; {rnpkg.__file__}")
print(f"Wrote {len(cases)} captured placeholder shell cases; Resolver returned None")
