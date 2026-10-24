"""Execute pinned upstream RNode methods with a recording serial adapter only."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

PIN = "a29bfb51c468945c96a1ee22cae0114e993e2391"


class RecordingSerial:
    def __init__(self):
        self.writes = []

    def write(self, data):
        self.writes.append(bytes(data).hex())
        return len(data)

    def close(self):
        pass


class ReplaySerial(RecordingSerial):
    def __init__(self, incoming):
        super().__init__()
        self.incoming = incoming
        self.position = 0

    @property
    def is_open(self):
        return self.position < len(self.incoming)

    @property
    def in_waiting(self):
        return len(self.incoming) - self.position

    def read(self, count):
        result = self.incoming[self.position:self.position + count]
        self.position += len(result)
        return result


class RecordingOwner:
    def __init__(self):
        self.packets = []

    def inbound(self, data, interface):
        self.packets.append(data.hex())


def radio(cls):
    instance = cls.__new__(cls)
    instance.serial = RecordingSerial()
    instance.name = "oracle"
    instance.frequency = 868000000
    instance.bandwidth = 125000
    instance.txpower = 7
    instance.sf = 7
    instance.cr = 5
    instance.st_alock = 1.25
    instance.lt_alock = 50.0
    instance.use_ble = instance.use_tcp = False
    instance.online = instance.interface_ready = True
    instance.flow_control = False
    instance.id_callsign = None
    instance.first_tx = None
    instance.txb = 0
    instance.packet_queue = []
    return instance


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", default=str(Path(__file__).parent / "Fixtures" / "parity6-rnode.json"))
    args = parser.parse_args()
    root = Path(args.upstream).resolve(strict=True)
    os.environ["DOTRETICULUM_UPSTREAM"] = str(root)
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    from RNS.vendor.configobj import ConfigObj
    modules = [importlib.import_module("RNS.Interfaces." + name) for name in
               ("RNodeInterface", "RNodeMultiInterface")]
    provenance = []
    support_modules = [importlib.import_module("RNS.Interfaces.Interface"), importlib.import_module("RNS.Reticulum")]
    for module in modules + support_modules:
        source = Path(module.__file__).resolve(strict=True)
        if not source.is_relative_to(root / "RNS"):
            raise RuntimeError(f"Unexpected import: {source}")
        provenance.append({"source": source.relative_to(root).as_posix(),
                           "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
        print(f"Verified import: {source}")
    print(f"RNS {RNS.__version__}: {RNS.__file__}; LXMF: {LXMF.__file__}; revision {PIN}")
    single_type = modules[0].RNodeInterface
    multi_type = modules[1].RNodeMultiInterface
    single = radio(single_type)
    single.detect()
    detect = single.serial.writes.pop()
    single.initRadio()
    configuration = single.serial.writes[:]
    single.serial.writes.clear()
    data = []
    for payload in (b"\x01\xc0\xdb\x02", bytes(range(256)), bytes([0xdb, 0xc0]) * 254):
        single.process_outgoing(payload)
        data.append({"payload": payload.hex(), "wire": single.serial.writes.pop()})
    single.leave()
    leave = single.serial.writes.pop()
    single.display = True
    single.enable_external_framebuffer()
    single.disable_external_framebuffer()
    single.write_framebuffer(0xc0, b"\xc0\xdb\x01\x02\x03\x04\x05\x06")
    single.read_framebuffer()
    single.read_display()
    single.hard_reset()
    display_commands = single.serial.writes[:]
    single.serial.writes.clear()
    multi = radio(multi_type)
    multi.detect()
    multi_detect = multi.serial.writes.pop()
    nested_configuration = []
    # Interface.__init__ consults the singleton for default ingress settings.
    # Supply an unstarted real Reticulum object, without sockets or replaced methods.
    context = RNS.Reticulum.__new__(RNS.Reticulum)
    for setting in ("ic_max_held_announces", "ic_burst_hold", "ic_burst_freq_new", "ic_burst_freq",
                    "ic_pr_burst_freq_new", "ic_pr_burst_freq", "ic_new_time", "ic_burst_penalty",
                    "ic_held_release_interval", "ec_pr_freq", "egress_control"):
        setattr(context, "_Reticulum__" + setting, None)
    RNS.Reticulum._Reticulum__instance = context
    for label, parent_enabled, child_interface_enabled, child_enabled in (
            ("child-interface-enabled", False, True, False),
            ("parent-enabled-override", True, False, False),
            ("nested-enabled-is-not-an-alias", False, False, True)):
        configuration_input = {
            "name": "nested-oracle", "port": "NEVER-OPENED", "enabled": parent_enabled,
            "radio-a": {"vport": "2", "interface_enabled": child_interface_enabled,
                        "enabled": child_enabled, "frequency": "868000000", "bandwidth": "125000",
                        "txpower": "-9", "spreadingfactor": "7", "codingrate": "5", "outgoing": False,
                        "network_name": "ignored-child-network", "passphrase": "public-test-value",
                        "ifac_size": "128", "mode": "roaming", "bitrate": "9600"}}
        try:
            configured = multi_type(None, ConfigObj(configuration_input))
            nested_configuration.append({"case": label, "subinterfaces": configured.subint_config})
        except ValueError as error:
            nested_configuration.append({"case": label, "error": str(error)})
    RNS.Reticulum._Reticulum__instance = None
    children = []
    for index in (0, 2, 10):
        child = SimpleNamespace(index=index, name=f"radio-{index}")
        multi.setFrequency(868000000, child)
        multi.setBandwidth(125000, child)
        multi.setTXPower(-9, child)
        multi.setSpreadingFactor(7, child)
        multi.setCodingRate(5, child)
        multi.setSTALock(1.25, child)
        multi.setLTALock(50.0, child)
        multi.setRadioState(1, child)
        commands = multi.serial.writes[:]
        multi.serial.writes.clear()
        multi.process_outgoing(b"\x01\xc0\xdb\x02", child)
        children.append({"index": index, "configuration": commands, "data": multi.serial.writes.pop()})
    single.r_frequency = 868000100
    single.r_bandwidth = 125000
    single.r_txpower = 7
    single.r_sf = 7
    single.r_cr = 5
    single.r_state = 1
    accepted = single.validateRadioState()
    single.r_frequency = 868000101
    rejected = single.validateRadioState()
    single.r_frequency = 868000000
    single.r_cr = 8
    cr_not_compared = single.validateRadioState()
    single.updateBitrate()
    replay = radio(single_type)
    replay.detached = True
    replay.reconnecting = False
    replay.HW_MTU = 508
    replay.timeout = 100
    replay.owner = RecordingOwner()
    replay.rxb = 0
    replay.r_sf = 7
    replay.r_stat_snr = replay.r_stat_rssi = replay.r_stat_q = None
    replay.r_symbol_time_ms = replay.r_symbol_rate = None
    replay.r_preamble_symbols = replay.r_premable_time_ms = None
    replay.r_csma_slot_time_ms = replay.r_csma_difs_ms = None
    replay.r_framebuffer_readtime = replay.r_disp_readtime = 0
    # These are device-response inputs, not reimplementations of the receiver.
    # Decode and state assertions come exclusively from unmodified readLoop.
    responses = [(0x23, bytes([100])), (0x24, bytes([0xf0])),
                 (0x26, bytes([4, 0, 3, 0xe8, 0, 8, 0, 16, 0, 7, 0, 9])),
                 (0x27, bytes([2, 120])), (0x29, bytes([145])),
                 (0x42, bytes(range(256)) * 2), (0x66, bytes(range(256)) * 4),
                 (0, bytes([1, 0xc0, 0xdb, 2]))]
    incoming = b"".join(bytes([0xc0, command]) + modules[0].KISS.escape(payload) + bytes([0xc0])
                        for command, payload in responses)
    replay.serial = ReplaySerial(incoming)
    replay.readLoop()
    observed = {name: getattr(replay, name) for name in
                ("r_stat_rssi", "r_stat_snr", "r_stat_q", "r_symbol_time_ms", "r_symbol_rate",
                 "r_preamble_symbols", "r_premable_time_ms", "r_csma_slot_time_ms", "r_csma_difs_ms",
                 "r_battery_state", "r_battery_percent", "r_temperature")}
    observed["packets"] = replay.owner.packets
    observed["framebuffer"] = replay.r_framebuffer.hex()
    observed["display"] = replay.r_disp.hex()
    result = {"revision": PIN, "rns_version": RNS.__version__, "sources": provenance,
              "single": {"detect": detect, "configuration": configuration, "leave": leave,
                         "data": data, "frequency_100_accepted": accepted,
                         "frequency_101_accepted": rejected, "cr_mismatch_accepted": cr_not_compared,
                         "bitrate_cr8": single.bitrate,
                         "display_commands": display_commands,
                         "receive": {"wire": incoming.hex(), "observed": observed}},
              "multi": {"detect": multi_detect, "children": children, "nested_configuration": nested_configuration,
                        "constructor_context": "Unstarted Reticulum object, ingress defaults None; no methods replaced or devices opened",
                        "chip_types": {str(t): modules[1].KISS.interface_type_to_str(t)
                                       for t in (0, 1, 2, 16, 17, 32, 33, 255)}},
              "tcp_defaults": {key: getattr(modules[0].TCPConnection, key) for key in
                               ("TARGET_PORT", "CONNECT_TIMEOUT", "INITIAL_CONNECT_TIMEOUT",
                                "RECONNECT_WAIT", "ACTIVITY_TIMEOUT", "ACTIVITY_KEEPALIVE",
                                "TCP_USER_TIMEOUT", "TCP_PROBE_AFTER", "TCP_PROBE_INTERVAL", "TCP_PROBES")}}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
