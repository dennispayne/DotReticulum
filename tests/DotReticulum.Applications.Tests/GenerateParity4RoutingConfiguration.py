"""Execute pinned Reticulum._synthesize_interface configuration decisions, without creating bearers."""
import ast
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from unittest.mock import patch

sys.path.insert(0, str(Path("tests") / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference()
import RNS
from RNS.vendor.configobj import ConfigObj

module = importlib.import_module("RNS.Reticulum")
path = Path(os.environ["DOTRETICULUM_UPSTREAM"]) / "RNS" / "Reticulum.py"
tree = ast.parse(path.read_text(encoding="utf-8"))
method = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "_synthesize_interface")
nodes = [node for node in method.body if 881 <= node.lineno <= 971]
assert nodes[0].lineno == 881 and nodes[-1].end_lineno == 971
code = compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec")
owner = RNS.Reticulum.__new__(RNS.Reticulum)
for field in ("default_gravity", "default_ar_target", "default_ar_grace", "default_ar_penalty"):
    setattr(RNS.Reticulum, "_Reticulum__" + field, None)

cases = []
for transport, settings in [
    (False, ""), (True, ""),
    (False, "announce_rate_target=30"), (True, "announce_rate_target=30"),
    (True, "announce_rate_target=0"), (False, "announce_rate_target=-1"),
    (True, "announce_rate_grace=0\nannounce_rate_penalty=9"),
    (True, "announce_rate_target=20\nannounce_rate_grace=-1\nannounce_rate_penalty=-1"),
    (False, "announce_cap=0"), (False, "announce_cap=-1"), (False, "announce_cap=101"),
    (False, "announce_cap=100"), (False, "announce_cap=.5"),
    (False, "gravity=-100\nannounces_from_internal=no\nannounces_to_internal=yes\nrecursive_prs=yes"),
    (True, "ingress_control=no\negress_control=yes\nic_max_held_announces=4\nic_burst_hold=2.5\nic_burst_freq_new=1.5\nic_burst_freq=7\nic_pr_burst_freq_new=2\nic_pr_burst_freq=4\nec_pr_freq=3\nic_new_time=12\nic_burst_penalty=6\nic_held_release_interval=1"),
]:
    namespace = vars(module).copy()
    namespace.update(self=owner, c=ConfigObj(settings.splitlines()), name="fixture")
    with patch.object(RNS.Reticulum, "transport_enabled", return_value=transport):
        exec(code, namespace)
    fields = ["gravity", "announce_rate_target", "announce_rate_grace", "announce_rate_penalty", "announce_cap",
              "announces_from_internal", "announces_to_internal", "recursive_prs", "ingress_control", "egress_control",
              "ic_max_held_announces", "ic_burst_hold", "ic_burst_freq_new", "ic_burst_freq", "ic_pr_burst_freq_new",
              "ic_pr_burst_freq", "ec_pr_freq", "ic_new_time", "ic_burst_penalty", "ic_held_release_interval"]
    cases.append(dict(transport=transport, settings=settings, expected={field: namespace[field] for field in fields}))
out = dict(pinned=PINNED_RNS_SHA, version=RNS.__version__,
           sourceSha256=hashlib.sha256(path.read_bytes()).hexdigest(),
           method="ConfigObj + unmodified AST-extracted Reticulum._synthesize_interface lines881-971; no bearer I/O",
           cases=cases)
output = Path("tests") / "DotReticulum.Applications.Tests" / "Fixtures" / "Parity4RoutingConfiguration-vectors.json"
output.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {output}: {len(cases)} configuration cases; upstream {PINNED_RNS_SHA}")
