"""Execute the pinned utility handlers; no DotReticulum implementation is used."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import sys
import contextlib
import io

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    import RNS
    from RNS.Utilities import rnx

    cases = []
    command = shlex.join([sys.executable, "-c",
                          "import sys;sys.stdout.buffer.write(bytes([0,255,65,66,67,68]));"
                          "sys.stderr.buffer.write(bytes([254,0,69,70]));sys.exit(7)"])
    for limit, timeout in ((None, 10), (0, 10), (3, 10), (-1, 10), (-20, 10), (None, None)):
        result = rnx.execute_received_command("command", [command.encode(), timeout, limit, limit, None],
                                             None, None, None)
        cases.append({"limit": limit, "timeout": timeout, "executed": result[0], "returnCode": result[1],
                      "stdout": result[2].hex(), "stderr": result[3].hex(),
                      "stdoutLength": result[4], "stderrLength": result[5],
                      "concluded": result[7] is not None})
    missing = rnx.execute_received_command("command",
                                          [b"dotreticulum-no-such-parity7-command", 10, None, None, None],
                                          None, None, None)
    root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
    sources = {name: hashlib.sha256((root / "RNS" / "Utilities" / (name + ".py")).read_bytes()).hexdigest()
               for name in ("rncp", "rnx", "rnsd")}
    cli = []
    from RNS.Utilities import rncp, rnsd
    for module in (rncp, rnx, rnsd):
        arguments = [["--version"], ["--not-a-parity7-option"], ["--config"]]
        if module is not rnsd:
            arguments.extend([[], ["orphan"]])
        for argv in arguments:
            stdout, stderr = io.StringIO(), io.StringIO()
            sys.argv = [module.__name__.rsplit(".", 1)[-1]] + argv
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                try:
                    module.main()
                    code = 0
                except SystemExit as ex:
                    code = ex.code or 0
            cli.append({"tool": sys.argv[0], "args": argv, "exitCode": code,
                        "stdout": stdout.getvalue(), "stderr": stderr.getvalue()})
    payload = {"revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
               "rnsModule": str(Path(RNS.__file__).resolve()), "sourceSha256": sources,
               "cases": cases, "failedSpawn": missing[:6], "cli": cli}
    capture = {}
    original = rnx.execute
    rnx.execute = lambda **kwargs: capture.update(kwargs)
    try:
        sys.argv = ["rnx", "--stdin=-v", "--stdout=-1", "--stderr=0",
                    "00000000000000000000000000000000", "authorized-vector-no-spawn"]
        rnx.main()
        payload["attachedValues"] = {"stdin": capture["stdin"], "stdout": capture["stdoutl"], "stderr": capture["stderrl"]}
    finally:
        rnx.execute = original
    Path(args.output).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
