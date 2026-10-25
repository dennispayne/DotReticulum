"""Execute the actual upstream argparse construction only; no utility side effects."""
import argparse
import ast
import contextlib
import io
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
from RNS.Utilities import rnodeconf as reference

source = Path(reference.__file__)
main = next(node for node in ast.parse(source.read_text(encoding="utf-8")).body if isinstance(node, ast.FunctionDef) and node.name == "main")
parser_block = next(node for node in main.body if isinstance(node, ast.Try) and any(
    isinstance(child, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "parser" for t in child.targets)
    for child in node.body))
statements = []
for node in parser_block.body:
    if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "args" for t in node.targets):
        break
    statements.append(node)
namespace = {"argparse": argparse}
exec(compile(ast.Module(body=statements, type_ignores=[]), str(source), "exec"), namespace)
cases = []
for args in [
    ["COM-NOT-OPENED", "-ib", "--display=300", "--ssid=Tést"],
    ["--tnc", "--freq=868000000", "--bw=125000", "--txp=0", "--sf=7", "--cr=5", "COM-NOT-OPENED"],
    ["--display=-1", "--display=255", "COM-NOT-OPENED"], ["-D192", "-R3", "COM-NOT-OPENED"],
    ["--wifi=AP", "--psk=example-only", "COM-NOT-OPENED"], ["--channel"], ["--unknown"],
    ["--info", "--normal", "--tnc", "COM-NOT-OPENED"], ["--", "-port-name"],
]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            values = vars(namespace["parser"].parse_args(args))
            code = 0
        except SystemExit as ex:
            values, code = None, ex.code
    cases.append({"args": args, "code": code, "values": values})
target = Path(__file__).parent / "Fixtures" / "rnodeconf-cli-vectors.json"
target.write_text(json.dumps({"revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "adapter": "Execute original argparse construction; never enter main's operation branches.", "cases": cases}, indent=2) + "\n", encoding="utf-8")
print(f"Verified import {reference.__file__}; captured {len(cases)} upstream parser cases")
