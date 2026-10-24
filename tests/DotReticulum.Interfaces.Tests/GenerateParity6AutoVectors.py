"""Execute pinned upstream AutoInterface without opening sockets; not live-node evidence."""
import hashlib
import json
import importlib.metadata
import inspect
import os
from pathlib import Path
import sys
import subprocess

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference

verify_reference(require_lxmf=True)
import RNS
import LXMF
from RNS.Interfaces.AutoInterface import AutoInterface, AutoInterfacePeer
from RNS.vendor.configobj import ConfigObj
from unittest.mock import patch

# Supply the singleton's unconfigured fields, preserving its actual default accessors.
# A full Reticulum constructor would start unrelated interfaces and persistence workers.
defaults = RNS.Reticulum.__new__(RNS.Reticulum)
for field in ["ic_max_held_announces", "ic_burst_hold", "ic_burst_freq_new",
              "ic_burst_freq", "ic_pr_burst_freq_new", "ic_pr_burst_freq",
              "ic_new_time", "ic_burst_penalty", "ic_held_release_interval",
              "ec_pr_freq", "egress_control"]:
    setattr(defaults, "_Reticulum__" + field, None)
RNS.Reticulum.get_instance = staticmethod(lambda: defaults)


def constructor_cases():
    cases = []
    for group in ["reticulum", "", "Parity6-å-網"]:
        for scope in ["link", "admin", "site", "organisation", "global"]:
            for kind in ["temporary", "permanent", "unknown"]:
                with patch.object(AutoInterface, "list_interfaces", return_value=[]):
                    interface = AutoInterface(None, ConfigObj({
                        "name": "vector", "group_id": group,
                        "discovery_scope": scope, "multicast_address_type": kind,
                    }))
                address = "fe80::1234:abcd"
                cases.append({
                    "group": group, "scope": scope, "kind": kind,
                    "multicast": interface.mcast_discovery_address,
                    "source": address,
                    "token": RNS.Identity.full_hash(interface.group_id + address.encode("utf-8")).hex(),
                })
    return cases


class Owner:
    def __init__(self):
        from collections import deque
        self.online = True
        self.HW_MTU = AutoInterface.HW_MTU
        self.FIXED_MTU = True
        self.mif_deque = deque(maxlen=48)
        self.mif_deque_times = deque(maxlen=48)
        self.received = []
        self.owner = self
        self.rxb = 0

    def refresh_peer(self, address):
        pass

    def inbound(self, data, interface):
        self.received.append(data.hex())


def duplicate_cases():
    owner = Owner()
    peer = AutoInterfacePeer(owner, "fe80::2", "vector")
    peer.online = True
    results = []
    for now, frame in [(100, b"one"), (100.749, b"one"), (100.75, b"one"),
                       (100.8, b"two"), (101, b"one"), (101.5, b"one")]:
        before = len(owner.received)
        with patch("RNS.Interfaces.AutoInterface.time.time", return_value=now):
            peer.process_incoming(frame)
        results.append({"time": now, "frame": frame.hex(), "accepted": len(owner.received) > before})
    return results


def discovery_cases():
    # Execute discovery_handler itself; stop its loop using a single datagram socket.
    cases = []
    group = b"reticulum"
    address = "fe80::1234:abcd"
    token = RNS.Identity.full_hash(group + address.encode("utf-8"))
    for name, data in [("exact", token), ("trailing", token + b"extra"),
                       ("short", token[:-1]), ("wrong", bytes(32)), ("empty", b"")]:
        owner = AutoInterface.__new__(AutoInterface)
        owner.group_id = group
        owner.final_init_done = True
        owner.detached = False
        owner.name = "vector"
        added = []
        owner.add_peer = lambda addr, ifname: added.append(addr)

        class OneDatagram:
            def recvfrom(self, size):
                owner.detached = True
                return data, (address, 123, 0, 7)

        owner.discovery_handler(OneDatagram(), "vector", announce=False)
        cases.append({"name": name, "data": data.hex(), "accepted": bool(added)})
    return cases


def peer_job_cases():
    cases = []
    for now in [105.2, 105.200001, 122, 122.000001]:
        owner = AutoInterface.__new__(AutoInterface)
        owner.name = "vector"
        owner.detached = False
        owner.peer_job_interval = AutoInterface.PEER_JOB_INTERVAL
        owner.peering_timeout = AutoInterface.PEERING_TIMEOUT
        owner.reverse_peering_interval = AutoInterface.ANNOUNCE_INTERVAL * 3.25
        owner.peers = {"fe80::2": ["vector", 100, 100]}
        owner.spawned_interfaces = {}
        owner.adopted_interfaces = {}
        reverse = []
        owner.reverse_announce = lambda ifname, addr: reverse.append(addr)
        with patch("RNS.Interfaces.AutoInterface.time.sleep", side_effect=lambda _: setattr(owner, "detached", True)):
            with patch("RNS.Interfaces.AutoInterface.time.time", return_value=now):
                owner.peer_jobs()
        cases.append({"elapsed": now - 100, "expired": not owner.peers, "reverse": bool(reverse)})
    return cases


source = Path(os.environ["DOTRETICULUM_UPSTREAM"]) / "RNS" / "Interfaces" / "AutoInterface.py"
if Path(inspect.getfile(AutoInterface)).resolve() != source.resolve():
    raise RuntimeError("AutoInterface imported from an unexpected source file")
source_bytes = source.read_bytes()
git_blob = subprocess.run(
    ["git", "-C", os.environ["DOTRETICULUM_UPSTREAM"], "cat-file", "blob",
     "a29bfb51c468945c96a1ee22cae0114e993e2391:RNS/Interfaces/AutoInterface.py"],
    check=True, capture_output=True,
).stdout
canonical_source = source_bytes.replace(b"\r\n", b"\n")
if canonical_source != git_blob.replace(b"\r\n", b"\n"):
    raise RuntimeError("Imported AutoInterface differs from the pinned Git blob beyond checkout line endings")
RNS.loglevel = -1
fixture = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "rns_version": RNS.__version__,
    "rns_module": str(Path(RNS.__file__).resolve()),
    "auto_module": str(Path(inspect.getfile(AutoInterface)).resolve()),
    "lxmf_version": importlib.metadata.version("lxmf"),
    "lxmf_module": str(Path(LXMF.__file__).resolve()),
    "auto_source_sha256": hashlib.sha256(source_bytes).hexdigest(),
    "auto_source_canonical_lf_sha256": hashlib.sha256(canonical_source).hexdigest(),
    "auto_git_blob_sha256": hashlib.sha256(git_blob).hexdigest(),
    "auto_source_hash_method": "Preserve raw imported-file SHA-256; independently read the exact pinned Git blob with binary git cat-file and require byte equality after CRLF-to-LF normalization.",
    "method": "Execute AutoInterface constructor with no devices, descope_linklocal, discovery_handler, peer_jobs (one iteration), and AutoInterfacePeer.process_incoming with deterministic clocks. Runtime singleton adapter supplies None configuration fields to real default accessors. No sockets or reference-source modifications; not unmodified runtime/live evidence.",
    "constructors": constructor_cases(),
    "discovery": discovery_cases(),
    "duplicates": duplicate_cases(),
    "peer_jobs": peer_job_cases(),
    "descope": [{"source": address, "result": AutoInterface.descope_linklocal(None, address)}
                for address in ["fe80::1234%en0", "fe80:0007::1234%7", "fe80:abcd::1", "fe80::beef", "2001:db8::1%en0"]],
    "constants": {
        "mtu": AutoInterface.HW_MTU, "discovery_port": AutoInterface.DEFAULT_DISCOVERY_PORT,
        "data_port": AutoInterface.DEFAULT_DATA_PORT, "peering_timeout": AutoInterface.PEERING_TIMEOUT,
        "announce_interval": AutoInterface.ANNOUNCE_INTERVAL,
        "peer_job_interval": AutoInterface.PEER_JOB_INTERVAL, "echo_timeout": AutoInterface.MCAST_ECHO_TIMEOUT,
        "duplicate_capacity": AutoInterface.MULTI_IF_DEQUE_LEN, "duplicate_ttl": AutoInterface.MULTI_IF_DEQUE_TTL,
    },
}
output = Path(__file__).parent / "Fixtures" / "parity6-auto.json"
output.parent.mkdir(exist_ok=True)
output.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {output}: {len(fixture['constructors'])} constructor, {len(fixture['discovery'])} discovery, {len(fixture['duplicates'])} duplicate, {len(fixture['peer_jobs'])} peer-job, {len(fixture['descope'])} address cases; raw source {fixture['auto_source_sha256']}; canonical LF {fixture['auto_source_canonical_lf_sha256']}; pinned Git blob {fixture['auto_git_blob_sha256']}; RNS {RNS.__version__}, LXMF {fixture['lxmf_version']}")
