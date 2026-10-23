"""Execute upstream Interface burst/frequency/held-release controls at the verified live-reference pin."""
from collections import deque
import hashlib
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

T0 = 1_800_000_000


def interface():
    iface = Interface.__new__(Interface)
    settings = dict(created=T0, ingress_control=True, egress_control=True,
        ic_new_time=Interface.IC_NEW_TIME, ic_burst_freq_new=Interface.IC_BURST_FREQ_NEW,
        ic_burst_freq=Interface.IC_BURST_FREQ, ic_pr_burst_freq_new=Interface.IC_PR_BURST_FREQ_NEW,
        ic_pr_burst_freq=Interface.IC_PR_BURST_FREQ, ic_burst_hold=Interface.IC_BURST_HOLD,
        ic_burst_penalty=Interface.IC_BURST_PENALTY, ic_held_release_interval=Interface.IC_HELD_RELEASE_INTERVAL,
        ic_max_held_announces=Interface.MAX_HELD_ANNOUNCES, ec_pr_freq=Interface.EC_PR_FREQ,
        ic_burst_active=False, ic_burst_activated=0, ic_burst_sustained=0, ic_held_release=0,
        ic_pr_burst_active=False, ic_pr_burst_activated=0, ic_pr_burst_sustained=0, ic_pr_burst_cooldown=0,
        held_announces={}, parent_interface=None, arxc=0, arxb=0, atxc=0, atxb=0, prxc=0, prxb=0, ptxc=0, ptxb=0)
    for key, value in settings.items():
        setattr(iface, key, value)
    iface.ia_freq_deque = deque(maxlen=48)
    iface.oa_freq_deque = deque(maxlen=48)
    iface.ip_freq_deque = deque(maxlen=48)
    iface.op_freq_deque = deque(maxlen=48)
    return iface


announce_cases = []
iface = interface()
for seconds in (0, .1, .2, .3, 1, 2, 16, 18, 19):
    with patch("time.time", return_value=T0 + seconds):
        iface.received_announce(100)
        limited = iface.should_ingress_limit()
        frequency = iface.incoming_announce_frequency()
        announce_cases.append(dict(seconds=seconds, limited=limited, active=iface.ic_burst_active,
                                   received=iface.arxc, samples=len(iface.ia_freq_deque), frequency=frequency))

request_cases = []
iface = interface()
for seconds in (0, .1, .2, .3, 20, 21, 22, 23, 24):
    with patch("time.time", return_value=T0 + seconds):
        iface.received_path_request(100)
        limited = iface.should_ingress_limit_pr()
        frequency = iface.incoming_pr_frequency()
        request_cases.append(dict(seconds=seconds, limited=limited, active=iface.ic_pr_burst_active,
                                  received=iface.prxc, samples=len(iface.ip_freq_deque), frequency=frequency))

egress_cases = []
iface = interface()
for seconds, send in ((0, True), (.1, True), (.5, False), (.6, False), (20, False)):
    with patch("time.time", return_value=T0 + seconds):
        if send:
            iface.sent_path_request(100)
        limited = iface.should_egress_limit_pr()
        frequency = iface.outgoing_pr_frequency()
        egress_cases.append(dict(seconds=seconds, send=send, limited=limited, frequency=frequency,
                                 sent=iface.ptxc, samples=len(iface.op_freq_deque)))

iface = interface()
with patch("time.time", return_value=T0):
    for _ in range(100):
        iface.received_announce(100)
        iface.received_path_request(100)
        iface.sent_announce(100)
        iface.sent_path_request(100)
bounded = [len(iface.ia_freq_deque), len(iface.ip_freq_deque), len(iface.oa_freq_deque), len(iface.op_freq_deque)]

iface = interface()
iface.ic_new_time = 0
iface.ic_burst_freq = 2
iface.ic_burst_hold = 1
iface.ic_burst_penalty = 3
iface.ic_held_release_interval = 2
iface.ic_max_held_announces = 2
packets = {}
accepted = set()
for index, (seconds, hops) in enumerate(((0, 1), (.1, 1), (.2, 6), (.3, 2), (.4, 1))):
    packet = SimpleNamespace(destination_hash=bytes([index]) * 16, hops=hops, raw=bytes([index]), receiving_interface=iface)
    packets[packet.raw] = packet
    with patch("time.time", return_value=T0 + seconds):
        iface.received_announce(100)
        if iface.should_ingress_limit():
            iface.hold_announce(packet)
        else:
            accepted.add(index)


class InlineThread:
    def __init__(self, target, **kwargs):
        self.target = target

    def start(self):
        self.target()


def released_inbound(raw, receiving_interface, **kwargs):
    # Isolated callback observation: run the real receive counter, burst decision
    # and hold methods again, as Transport.inbound does for a held release.
    packet = packets[raw]
    receiving_interface.received_announce(100)
    if receiving_interface.should_ingress_limit():
        receiving_interface.hold_announce(packet)
    else:
        accepted.add(raw[0])


held_cases = []
for seconds in (2, 4, 6.1, 7, 8.2):
    with patch("time.time", return_value=T0 + seconds), \
         patch.object(RNS.Transport, "inbound", side_effect=released_inbound), \
         patch("RNS.Interfaces.Interface.threading.Thread", InlineThread):
        iface.process_held_announces()
    held_cases.append(dict(seconds=seconds, held=len(iface.held_announces), nearAccepted=3 in accepted, farAccepted=2 in accepted))

path = Path(os.environ["DOTRETICULUM_UPSTREAM"]) / "RNS" / "Interfaces" / "Interface.py"
output = Path("tests") / "DotReticulum.Transport.Tests" / "Fixtures" / "Parity4RoutingTraffic-vectors.json"
output.write_text(json.dumps(dict(pinned=PINNED_RNS_SHA, version=RNS.__version__,
    sourceSha256=hashlib.sha256(path.read_bytes()).hexdigest(),
    method="Direct unmodified Interface counters/burst/frequency/hold/process_held_announces; clock patched, release thread made synchronous and Transport.inbound callback replaced with the real counter/burst/hold method sequence (isolated controller evidence, not full inbound interoperability)",
    announceCases=announce_cases, requestCases=request_cases, egressCases=egress_cases, sampleBounds=bounded,
    heldReleaseCases=held_cases), indent=2) + "\n", encoding="utf-8")
print(f"Wrote {output}: {len(announce_cases)} announce, {len(request_cases)} request, {len(egress_cases)} egress steps")
