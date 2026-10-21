"""Execute the pinned Python config parser, interface synthesis, and logger.

The only harness substitution is the name FixtureOnlyInterface, deliberately
absent from the reference interfaces directory: synthesis still runs upstream's
enable/mode selection and records selected_interface_mode without opening I/O.
This is configuration evidence, not live-node/interface interoperability.
"""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

SOURCE_SHA256 = {
    "RNS/Reticulum.py": "c655675a799413d9bb11d44ab90d56bc1f4fe339fee1ee5d108754dc8bf812d7",
    "RNS/__init__.py": "217d8cd060844475acb692f869c9eb1e051ce5b800d3f23479d562e37f048f33",
    "RNS/Utilities/rnsd.py": "e8920d1acde17e09b0aad57651348e86aabc84fbb29a531c340b7365d220e748",
    "RNS/vendor/configobj.py": "fb7036cfe845a256889752ac5839d7e8dce3356eef9c7b440a92a646be4bec8e",
    "RNS/Interfaces/TCPInterface.py": "d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d",
    "RNS/Interfaces/UDPInterface.py": "575d1137716139b238649ae5b35a0f66a6a25a8661bc919f5226942df79f4fa5",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    import RNS
    from RNS.vendor.configobj import ConfigObj

    root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
    for source, expected in SOURCE_SHA256.items():
        if hashlib.sha256((root / source).read_bytes()).hexdigest() != expected:
            raise RuntimeError(f"Source hash changed: {source}; explicitly review before updating fixtures")
    workspace = Path(args.workspace).resolve()
    if workspace.exists():
        raise RuntimeError("Workspace must not exist; only generator-owned artifacts are removed")
    workspace.mkdir(parents=True)
    (workspace / "config").write_text("[reticulum]\nshare_instance=no\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
    runtime = None
    try:
        runtime = RNS.Reticulum(configdir=str(workspace))
        RNS.loglevel = -1
        cases = []
        for settings in [
            "", "enabled=no", "enabled=yes", "interface_enabled=yes",
            "enabled=no\ninterface_enabled=yes",
            "enabled=yes\ninterface_enabled=no",
            "enabled=yes\nmode=ptp",
            "enabled=yes\nmode=pointtopoint",
            "enabled=yes\ninterface_mode=accesspoint\nmode=roaming",
            "enabled=yes\nmode=gateway",
            "enabled=yes\nmode=internal",
            "enabled=yes\nmode=roaming",
            "enabled=yes\nmode=boundary",
        ]:
            text = "[interfaces]\n[[Fixture]]\ntype=FixtureOnlyInterface\n" + settings + "\n"
            parsed = ConfigObj(text.splitlines())
            section = parsed["interfaces"]["Fixture"]
            runtime._synthesize_interface(section, "Fixture")
            mode = section.get("selected_interface_mode")
            cases.append({"settings": settings, "enabled": mode is not None, "mode": mode})

        quoted_text = 'name = "a # b, c" # trailing\nsources = "aa", \'bb,cc\', dd,\n'
        quoted = ConfigObj(quoted_text.splitlines())
        labels = []
        RNS.logtimestamps = False
        RNS.loglevel = 8
        RNS.logdest = RNS.LOG_STDOUT
        for level in range(9):
            capture = io.StringIO()
            with contextlib.redirect_stdout(capture):
                RNS.log("fixture", level)
            labels.append(capture.getvalue().rstrip("\n"))
        fixture = {
            "upstreamRevision": PINNED_RNS_SHA,
            "sourceSha256": {
                name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                for name in ["RNS/Reticulum.py", "RNS/__init__.py", "RNS/Utilities/rnsd.py", "RNS/vendor/configobj.py", "RNS/Interfaces/TCPInterface.py", "RNS/Interfaces/UDPInterface.py"]
            },
            "importedRns": str(Path(RNS.__file__).resolve().relative_to(root.resolve())),
            "rnsVersion": RNS.__version__,
            "method": "ConfigObj + Reticulum._synthesize_interface (absent FixtureOnlyInterface, no interface I/O) + RNS.log",
            "quotedText": quoted_text,
            "quoted": dict(quoted),
            "cases": cases,
            "logLines": labels,
            "logMaximumFileSize": RNS.LOG_MAXSIZE,
        }
        Path(args.output).write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")
    finally:
        if runtime is not None:
            RNS.loglevel = -1
            RNS.Reticulum.exit_handler()
        shutil.rmtree(workspace)


if __name__ == "__main__":
    main()
