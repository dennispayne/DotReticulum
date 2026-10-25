"""Execute pinned packet, inbound cache, Reticulum query and Link PHY methods.

No daemon or hardware is created: the inbound interface and singleton are test
doubles, while every packet/hash/cache lookup/Link update executes upstream.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference
import RNS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    RNS.loglevel = -1
    reticulum = object.__new__(RNS.Reticulum)
    reticulum.is_connected_to_shared_instance = False
    RNS.Reticulum._Reticulum__instance = reticulum
    RNS.Reticulum._Reticulum__transport_enabled = False
    RNS.Transport.owner = reticulum
    RNS.Transport.local_client_rssi_cache = []
    RNS.Transport.local_client_snr_cache = []
    RNS.Transport.local_client_q_cache = []
    destination = SimpleNamespace(type=RNS.Destination.PLAIN, hash=bytes(range(16)), encrypt=lambda data: data)

    def receive(index, values):
        packet = RNS.Packet(destination, index.to_bytes(2, "big"))
        packet.pack()
        packet = RNS.Packet(None, packet.raw)
        assert packet.unpack()
        packet.receiving_interface = SimpleNamespace(online=True, reports_phy_stats=True,
            r_stat_rssi=values[0], r_stat_snr=values[1], r_stat_q=values[2])
        RNS.Transport._inbound(packet)
        return packet

    observations = []
    packets = []
    for index, values in enumerate([(-91, 4.25, 88.3), (-113, -7.5, 10.0), (None, None, None)]):
        packet = receive(index, values)
        packets.append(packet)
        observations.append({"raw": packet.raw.hex(), "hash": packet.packet_hash.hex(),
            "rssi": packet.get_rssi(), "snr": packet.get_snr(), "quality": packet.get_q()})

    # Use upstream's actual per-field FIFO and first-match lookup semantics.
    duplicate = receive(0, (-70, None, None))
    duplicate_observation = {"direct": duplicate.get_rssi(),
        "cached": reticulum.get_packet_rssi(duplicate.packet_hash)}
    for index in range(3, RNS.Transport.LOCAL_CLIENT_CACHE_MAXSIZE + 3):
        receive(index, (-100, None, None))
    eviction = {"rssi": reticulum.get_packet_rssi(packets[0].packet_hash),
        "snr": reticulum.get_packet_snr(packets[0].packet_hash),
        "quality": reticulum.get_packet_q(packets[0].packet_hash),
        "maximum": RNS.Transport.LOCAL_CLIENT_CACHE_MAXSIZE}

    link = object.__new__(RNS.Link)
    link._Link__track_phy_stats = False
    link.rssi = link.snr = link.q = None
    link._Link__update_phy_stats(packets[1])
    disabled = [link.get_rssi(), link.get_snr(), link.get_q()]
    link.track_phy_stats(True)
    link._Link__update_phy_stats(packets[1])
    enabled = [link.get_rssi(), link.get_snr(), link.get_q()]
    link._Link__update_phy_stats(packets[2])
    absent_preserves = [link.get_rssi(), link.get_snr(), link.get_q()]
    link.track_phy_stats(False)
    hidden = [link.get_rssi(), link.get_snr(), link.get_q()]
    source = Path(RNS.__file__).resolve().parent
    output = {"upstream": PINNED_RNS_SHA, "module": str(source),
        "sources": {name: hashlib.sha256((source / name).read_bytes()).hexdigest()
                    for name in ("Packet.py", "Transport.py", "Reticulum.py", "Link.py")},
        "packets": observations, "duplicate": duplicate_observation, "eviction": eviction,
        "link": {"disabled": disabled, "enabled": enabled, "absent_preserves": absent_preserves, "hidden": hidden}}
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
