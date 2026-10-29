"""Execute the pinned BLE selector/filter; no Bluetooth adapter or radio is opened."""
import argparse
import ast
import asyncio
import hashlib
import importlib
import inspect
import json
import os
from pathlib import Path
import sys
import textwrap
from types import SimpleNamespace
from unittest.mock import patch


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", default=str(Path(__file__).parent / "Fixtures" / "rnode-ble.json"))
    args = parser.parse_args()
    root = Path(args.upstream).resolve(strict=True)
    os.environ["DOTRETICULUM_UPSTREAM"] = str(root)
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    module = importlib.import_module("RNS.Interfaces.RNodeInterface")
    source = Path(module.__file__).resolve(strict=True)
    if not source.is_relative_to(root / "RNS"):
        raise RuntimeError(f"Unexpected reference import: {source}")

    constructor = ast.parse(textwrap.dedent(inspect.getsource(module.RNodeInterface.__init__)))
    selector = next(
        node for node in constructor.body[0].body
        if isinstance(node, ast.If) and any(
            isinstance(child, ast.Name) and child.id == "ble_uri_scheme"
            for child in ast.walk(node)
        )
    )
    selector_code = compile(ast.Module(body=[selector], type_ignores=[]), str(source), "exec")
    selectors = []
    selections = []
    targets = ["ble://", "BLE://RNode Example", "ble://AA:BB:CC:DD:EE:FF",
               "ble://aa:bb:cc:dd:ee:ff", "ble://not:an:addr",
               "ble://zz:zz:zz:zz:zz:zz"]
    devices = [
        ("RNode Example", "AA:BB:CC:DD:EE:FF", True, True),
        ("Other UART", "AA:BB:CC:DD:EE:FF", True, True),
        ("rnode example", "AA:BB:CC:DD:EE:FF", True, True),
        ("RNode Example", "AA:BB:CC:DD:EE:FF", False, True),
        ("RNode Example", "AA:BB:CC:DD:EE:FF", True, False),
        ("RNode Example", "11:22:33:44:55:66", True, True),
    ]
    for port in targets:
        scope = {"port": port, "force_ble": False, "force_tcp": False,
                 "ble_name": None, "ble_addr": None, "tcp_host": None}
        exec(selector_code, scope)
        selectors.append({"port": port, "name": scope["ble_name"], "address": scope["ble_addr"]})
        for name, address, bonded, uart in devices:
            result = observe(module, scope, name, address, bonded, uart, windows=False)
            selections.append({"port": port, "name": name, "address": address,
                               "bonded": bonded, "uart": uart, "matches": result})

    windows_bonds = []
    for bonded in (False, True):
        for props_bonded in (False, True):
            scope = {"ble_name": "RNode Example", "ble_addr": None}
            result = observe(module, scope, "RNode Example", "AA:BB:CC:DD:EE:FF",
                             props_bonded, True, windows=True, windows_bonded=bonded)
            windows_bonds.append({"paired": bonded, "linux_bonded_property": props_bonded, "matches": result})

    output = {"revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
              "source": "RNS/Interfaces/RNodeInterface.py",
              "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "rns_import": "RNS/Interfaces/RNodeInterface.py",
              "method": "Execute original constructor selector AST and BLEConnection.find_target_device/device_bonded; recording scanner and paired-address query only.",
              "selectors": selectors, "selections": selections, "windows_bonds": windows_bonds}
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"Verified import: {source}")
    print(f"Wrote {args.output}")


def observe(module, scope, name, address, bonded, uart, *, windows, windows_bonded=False):
    observed = []
    device = SimpleNamespace(name=name, address=address, details={"props": {"Bonded": bonded}})
    advertisement = SimpleNamespace(service_uuids=[module.BLEConnection.UART_SERVICE_UUID.lower()] if uart else [])

    class Scanner:
        @staticmethod
        async def find_device_by_filter(callback, timeout):
            result = callback(device, advertisement)
            observed.append(result)
            return device if result else None

    connection = module.BLEConnection.__new__(module.BLEConnection)
    connection.target_name = scope["ble_name"]
    connection.target_bt_addr = scope["ble_addr"]
    connection.owner = "recording selector"
    connection.scan_timeout = 2.0
    connection._windows_paired_addrs = None
    connection._get_windows_paired_ble_addresses = lambda: {address.lower()} if windows_bonded else set()
    connection.asyncio = asyncio
    connection.bleak = SimpleNamespace(BleakScanner=Scanner, backends=SimpleNamespace(
        device=SimpleNamespace(BLEDevice=SimpleNamespace), scanner=SimpleNamespace(AdvertisementData=SimpleNamespace)))
    with patch.object(module.RNS.vendor.platformutils, "is_windows", return_value=windows):
        connection.find_target_device()
    if len(observed) != 1:
        raise RuntimeError("The reference scanner filter was not exercised exactly once")
    return observed[0]


if __name__ == "__main__":
    main()
