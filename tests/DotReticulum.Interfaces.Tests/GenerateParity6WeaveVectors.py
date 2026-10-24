"""Execute pinned Weave methods; no physical serial, UI, transport or threads."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


class RecordingSerial:
    is_open = True

    def __init__(self):
        self.writes = []

    def write(self, data):
        self.writes.append(bytes(data).hex())
        return len(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", default=str(Path(__file__).parent / "Fixtures" / "parity6-weave.json"))
    args = parser.parse_args()
    root = Path(args.upstream).resolve(strict=True)
    os.environ["DOTRETICULUM_UPSTREAM"] = str(root)
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    w = importlib.import_module("RNS.Interfaces.WeaveInterface")
    # Public deterministic test keys, not deployed credentials.
    host = RNS.Identity.from_bytes(bytes(range(64)))
    remote = RNS.Identity.from_bytes(bytes(range(64, 128)))
    connection = w.WDCL.__new__(w.WDCL)
    connection.serial = RecordingSerial()
    connection.switch_identity = host
    connection.switch_pub_bytes = host.sig_pub_bytes
    connection.switch_id = host.sig_pub_bytes[-4:]
    connection.txb = 0
    connection.wdcl_connected = False
    device = w.WeaveDevice(as_interface=True, rns_interface=SimpleNamespace())
    device.connection = connection
    device.discover()
    # Discovery input is signed by the actual upstream identity implementation;
    # incoming_frame validates it and itself emits the connect oracle.
    discovery = connection.switch_id + bytes([w.WDCL.WDCL_T_DISCOVER])
    discovery += remote.sig_pub_bytes + remote.sign(connection.switch_id)
    device.incoming_frame(discovery)
    endpoint = bytes.fromhex("0102030405060708")
    payload = bytes.fromhex("007d7eff10203040")
    device.deliver_packet(endpoint, payload)
    device.connect_display()
    device.disconnect_display()
    device.wdcl_send_command(w.Cmd.WDCL_CMD_ENDPOINTS_LIST, b"")
    device.wdcl_broadcast(w.WDCL.WDCL_T_ENCAP_PROTO, payload)
    source = Path(w.__file__)
    result = {
        "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
        "rns_module": RNS.__file__, "lxmf_module": LXMF.__file__,
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "shims": "Construct WDCL via __new__, recording serial.write, no constructor/port/threads. WeaveDevice methods unmodified. No device trust claim.",
        "host_test_private_key": bytes(range(64)).hex(),
        "host_public_key": host.sig_pub_bytes.hex(),
        "host_switch_id": connection.switch_id.hex(),
        "remote_public_key": remote.sig_pub_bytes.hex(),
        "remote_switch_id": device.switch_id.hex(),
        "discovery_response": discovery.hex(),
        "endpoint": endpoint.hex(), "payload": payload.hex(),
        "writes": connection.serial.writes,
        "wire_transmitted": connection.txb,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"revision": result["revision"], "source_sha256": result["source_sha256"],
                      "rns_module": RNS.__file__, "lxmf_module": LXMF.__file__, "writes": len(result["writes"])}))


if __name__ == "__main__":
    main()
