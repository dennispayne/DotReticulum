"""Observe actual pinned Reticulum startup and Identity.prove in isolated processes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference


def observe(transport, probes, implicit):
    verify_reference()
    import RNS

    with tempfile.TemporaryDirectory(prefix="rns-proof-probe-reference-") as directory:
        root = Path(directory)
        settings = ["[reticulum]", "share_instance=no",
                    f"enable_transport={transport}", "static_transport_identity=yes"]
        if probes != "default":
            settings.append(f"respond_to_probes={probes}")
        if implicit != "default":
            settings.append(f"use_implicit_proof={implicit}")
        (root / "config").write_text("\n".join(settings) + "\n[interfaces]\n", encoding="utf-8")
        with patch("time.time", return_value=1700000000.0):
            runtime = RNS.Reticulum(configdir=str(root), loglevel=0)
            management_delay = RNS.Transport.last_mgmt_announce + RNS.Transport.mgmt_announce_interval - 1700000000.0
        identity = RNS.Identity.from_bytes(hashlib.sha512(b"public-test-vector-rns-proof-policy").digest())
        destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "policy", "proof")
        packet = RNS.Packet(destination, b"independent policy observation")
        packet.pack()
        with patch.object(RNS.Packet, "send", autospec=True) as send:
            identity.prove(packet)
        proof = send.call_args.args[0]
        probe = getattr(RNS.Transport, "probe_destination", None)
        return {
            "transport": transport == "yes",
            "probes": probes,
            "implicit": implicit,
            "probePolicy": RNS.Reticulum.probe_destination_enabled(),
            "implicitPolicy": RNS.Reticulum.should_use_implicit_proof(),
            "hasProbe": probe is not None,
            "probeAspect": ".".join(probe.name.split(".")[:-1]) if probe else None,
            "probeAcceptsLinks": probe.accepts_links() if probe else None,
            "probeProofStrategy": probe.proof_strategy if probe else None,
            "proofLength": len(proof.data),
            "proofSignatureValid": identity.validate(proof.data[-64:], packet.packet_hash),
            "proofHashPrefixMatches": proof.data[:-64] == packet.packet_hash,
            "managementDelay": management_delay,
            "managementInterval": RNS.Transport.mgmt_announce_interval,
            "connectedToSharedInstance": runtime.is_connected_to_shared_instance,
        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", nargs=3)
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "Fixtures" / "ProofProbePolicy-vectors.json")
    args = parser.parse_args()
    if args.case:
        print(json.dumps(observe(*args.case), sort_keys=True))
        return
    verify_reference()
    import RNS

    cases = [("no", "default", "default")]
    cases.extend((transport, probes, implicit)
                 for transport in ("no", "yes")
                 for probes in ("no", "yes")
                 for implicit in ("no", "yes"))
    observations = []
    for case in cases:
        result = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--case", *case],
                                check=True, capture_output=True, text=True, timeout=30)
        observations.append(json.loads(result.stdout))
    root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
    result = {
        "revision": PINNED_RNS_SHA,
        "version": RNS.__version__,
        "method": "Actual Reticulum constructors in nine isolated processes with a fixed startup time.time clock; actual Identity.prove with only Packet.send observed, not transmitted; no reference source changes",
        "sources": {name: hashlib.sha256((root / "RNS" / name).read_bytes()).hexdigest()
                    for name in ("Reticulum.py", "Transport.py", "Identity.py")},
        "cases": observations,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2) + "\n")
    print(f"Wrote {len(observations)} actual startup/proof observations to {args.output}")


if __name__ == "__main__":
    main()
