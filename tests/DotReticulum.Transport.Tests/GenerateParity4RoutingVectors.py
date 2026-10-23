"""Execute pinned upstream routing decisions and interface queue scheduling.

Run from the repository root with DOTRETICULUM_UPSTREAM and PYTHONPATH set to
the verified hosted-source checkout, using the isolated reference venv.
The two nested Transport decisions are AST-extracted without rewriting their
bodies because starting a full singleton daemon would make the clock and
interface matrix nondeterministic. Interface.process_announce_queue and
Transport.medium_path_timeout execute directly. No .NET output is an oracle.
"""
import ast
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path("tests") / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA

verify_reference()
import RNS
from RNS.Interfaces.Interface import Interface

transport_module = importlib.import_module("RNS.Transport")
T = RNS.Transport
source_path = Path(os.environ["DOTRETICULUM_UPSTREAM"]) / "RNS" / "Transport.py"
source = source_path.read_text(encoding="utf-8")
tree = ast.parse(source)


def decision(test_text, after):
    nodes = [n for n in ast.walk(tree) if isinstance(n, ast.If)
             and n.lineno > after and ast.unparse(n.test) == test_text]
    node = min(nodes, key=lambda n: n.lineno)
    return compile(ast.Module(body=[node], type_ignores=[]), str(source_path), "exec"), node.lineno, node.end_lineno


path_code, path_start, path_end = decision("not announced_destination_known", 2200)
mode_code, mode_start, mode_end = decision("not local_destination and from_interface == None", 1400)
rate_code, rate_start, rate_end = decision(
    "packet.context != RNS.Packet.PATH_RESPONSE and packet.receiving_interface.announce_rate_target != None", 2200)
now = 1_800_000_000
key = bytes(range(16))
blob = bytes(range(5)) + now.to_bytes(5, "big")
cases = []
for label, current_hops, new_hops, delta, same, old_g, new_g, unresponsive, expired in [
    ("same-equal-gravity", 2, 2, 0, True, 0, 0, False, False),
    ("same-higher-gravity", 2, 2, 0, True, 0, 10, False, False),
    ("same-lower-gravity", 2, 2, 0, True, 10, 0, False, False),
    ("same-shorter-higher-gravity", 3, 1, 0, True, 0, 10, False, False),
    ("same-longer-higher-gravity", 1, 3, 0, True, 0, 10, False, False),
    ("new-random-same-time-higher-gravity", 2, 2, 0, False, 0, 10, False, False),
    ("new-random-same-time-equal-gravity", 2, 2, 0, False, 0, 0, False, False),
    ("newer-shorter", 3, 1, 1, False, 10, 0, True, False),
    ("newer-equal", 2, 2, 1, False, 10, 0, True, False),
    ("newer-longer", 1, 3, 1, False, 10, 0, False, False),
    ("older-shorter", 3, 1, -1, False, 0, 10, False, False),
    ("older-longer", 1, 3, -1, False, 0, 10, False, False),
    ("unresponsive-same-blob-longer", 1, 3, 0, True, 0, 0, True, False),
    ("responsive-same-blob-longer", 1, 3, 0, True, 0, 0, False, False),
    ("unresponsive-older-longer", 1, 3, -1, False, 0, 0, True, False),
    ("expired-old-new-blob", 1, 3, -1, False, 0, 0, False, True),
    ("expired-same-blob", 1, 3, 0, True, 0, 0, True, True),
]:
    entry = [None] * 7
    entry[transport_module.IDX_PT_HOPS] = current_hops
    entry[transport_module.IDX_PT_RANDBLOBS] = [blob]
    entry[transport_module.IDX_PT_RVCD_IF] = SimpleNamespace(gravity=old_g)
    entry[transport_module.IDX_PT_EXPIRES] = now - 1 if expired else now + 604800
    T.path_table = {key: entry}
    T.path_states = {key: T.STATE_UNRESPONSIVE if unresponsive else T.STATE_RESPONSIVE}
    namespace = vars(transport_module).copy()
    namespace.update(announced_destination_known=True, should_add=False, random_blobs=[],
                     random_blob=blob if same else b"\xff" * 5 + (now + delta).to_bytes(5, "big"),
                     announce_emitted=now + delta,
                     packet=SimpleNamespace(destination_hash=key, hops=new_hops,
                                            receiving_interface=SimpleNamespace(gravity=new_g)))
    with patch.object(transport_module.time, "time", return_value=now):
        exec(path_code, namespace)
    cases.append(dict(label=label, currentHops=current_hops, newHops=new_hops,
                      emissionDelta=delta, sameBlob=same, currentGravity=old_g,
                      newGravity=new_g, unresponsive=unresponsive, expired=expired,
                      accepted=namespace["should_add"]))

modes = []
for origin in range(1, 8):
    for target in range(1, 8):
        for local in (False, True):
            namespace = vars(transport_module).copy()
            namespace.update(local_destination=object() if local else None,
                             from_interface=SimpleNamespace(mode=origin, announces_to_internal=False),
                             interface=SimpleNamespace(mode=target, announces_from_internal=True,
                                                       bitrate=10000, announce_allowed_at=0, announce_queue=[]),
                             should_transmit=True, outbound_time=now,
                             packet=SimpleNamespace(hops=0 if local else 1, raw=b"x" * 200),
                             ac_loglevel=RNS.LOG_PATHING)
            exec(mode_code, namespace)
            modes.append(dict(origin=origin, target=target, local=local, transmit=namespace["should_transmit"]))

rate_cases = []
T.announce_rate_table = {}
for seconds in (0, 1, 2, 5, 15, 16, 30, 31):
    namespace = vars(transport_module).copy()
    namespace.update(now=now + seconds, rate_blocked=False,
                     packet=SimpleNamespace(context=0, destination_hash=key,
                         receiving_interface=SimpleNamespace(announce_rate_target=10,
                                                            announce_rate_grace=1, announce_rate_penalty=5)))
    exec(rate_code, namespace)
    state = T.announce_rate_table[key]
    rate_cases.append(dict(seconds=seconds, blocked=namespace["rate_blocked"],
                           last=state["last"], violations=state["rate_violations"], blockedUntil=state["blocked_until"]))

selected = []
queue_interface = SimpleNamespace(
    announce_cap=.02, bitrate=1000, announce_queue=[
        dict(destination=b"far", time=now - 30, hops=3, emitted=now, raw=b"far"),
        dict(destination=b"near-new", time=now - 5, hops=1, emitted=now, raw=b"near-new"),
        dict(destination=b"near-old", time=now - 10, hops=1, emitted=now, raw=b"near-old"),
        dict(destination=b"expired", time=now - RNS.Reticulum.QUEUED_ANNOUNCE_LIFE - 1, hops=0, emitted=now, raw=b"expired")],
    sent_announce=lambda size: None, process_announce_queue=lambda: None)
with patch.object(transport_module.time, "time", return_value=now), \
     patch.object(T, "transmit", side_effect=lambda iface, raw: selected.append(raw.decode())), \
     patch("RNS.Interfaces.Interface.threading.Timer"):
    Interface.process_announce_queue(queue_interface)

timeouts = []
for bitrate in (0, 1, 5, 100, 10000):
    T.lowest_interface_bitrate = bitrate
    timeouts.append(dict(bitrate=bitrate, timeout=T.medium_path_timeout()))

identity = RNS.Identity(create_keys=False)
identity.load_private_key(bytes(range(1, 33)) + bytes(range(101, 133)))
with patch.object(T, "register_destination"):
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "parity4", "routing")
with patch.object(transport_module.time, "time", return_value=now), \
     patch.object(RNS.Identity, "get_random_hash", return_value=bytes(range(16))):
    announce = destination.announce(send=False)
announce.pack()

queues = transport_module.InboundQueues((2, 1, 1, 1), hw_mark=128)
for traffic_class, value in ((3, "limited"), (2, "request"), (1, "announce"), (0, "data-a"), (0, "data-b")):
    queues.put(traffic_class, value)
for traffic_class in range(4):
    try:
        queues.put(traffic_class, "overflow")
        raise AssertionError("Reference accepted an overflowing class")
    except transport_module.Full:
        pass
queue_snapshot = queues.snapshot()
drained = [queues.get(block=False) for _ in range(5)]

out = dict(pinned=PINNED_RNS_SHA, version=RNS.__version__, imported=str(Path(RNS.__file__).resolve()),
           transportSha256=hashlib.sha256(source_path.read_bytes()).hexdigest(),
           sourceRanges=dict(path=[path_start, path_end], mode=[mode_start, mode_end], rate=[rate_start, rate_end]),
           method="Unmodified AST-extracted nested decisions; direct interface queue and medium timeout execution",
           pathCases=cases, modeCases=modes, rateCases=rate_cases, mediumTimeouts=timeouts,
           queueSelection=selected, queueAllowedAt=queue_interface.announce_allowed_at,
           inboundHeights=queue_snapshot[1], inboundDropped=queue_snapshot[2], inboundOrder=drained,
           identityPrivate=identity.get_private_key().hex(), announceRaw=announce.raw.hex())
output = Path("tests") / "DotReticulum.Transport.Tests" / "Fixtures" / "Parity4Routing-vectors.json"
output.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {output}: {len(cases)} path, {len(modes)} mode, {len(rate_cases)} rate vectors; upstream {PINNED_RNS_SHA}")
