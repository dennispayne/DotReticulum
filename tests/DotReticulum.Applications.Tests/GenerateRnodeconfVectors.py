"""Execute pinned upstream methods with a capture-only serial adapter; no hardware."""
import hashlib
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
from RNS.Utilities import rnodeconf as reference


class Capture:
    def __init__(self):
        self.frames = []

    def write(self, data):
        self.frames.append(data.hex())
        return len(data)


serial = Capture()
node = reference.RNode(serial)
reference.sleep = lambda _: None  # Disclosed: eliminate hardware pacing, not serialization.
cases = {}
calls = [
    ("detect", "detect", []), ("normal", "setNormalMode", []),
    ("display", "set_display_intensity", [192]),
    ("timeout", "set_display_blanking", [219]),
    ("rotation", "set_display_rotation", [3]),
    ("display-address", "set_display_address", [60]),
    ("recondition", "recondition_display", []),
    ("neopixel", "set_neopixel_intensity", [255]),
    ("ia-on", "set_disable_interference_avoidance", [False]),
    ("ia-off", "set_disable_interference_avoidance", [True]),
    ("bluetooth-on", "enable_bluetooth", []),
    ("bluetooth-off", "disable_bluetooth", []),
    ("bluetooth-pair", "bluetooth_pair", []),
    ("wifi-ap", "set_wifi_mode", [2]),
    ("wifi-channel", "set_wifi_channel", [14]),
    ("ssid", "set_wifi_ssid", ["Tést"]),
    ("ssid-delete", "set_wifi_ssid", [None]),
    ("psk", "set_wifi_psk", ["test-only-password"]),
    ("psk-delete", "set_wifi_psk", [None]),
    ("ip", "set_wifi_ip", ["192.168.1.219"]),
    ("ip-dhcp", "set_wifi_ip", [None]),
    ("netmask", "set_wifi_nm", ["255.255.255.0"]),
    ("reset", "hard_reset", []), ("wipe", "wipe_eeprom", []),
    ("eeprom-write", "write_eeprom", [192, 219]),
    ("hash-write", "set_firmware_hash", [bytes(range(32))]),
    ("update-indicator", "indicate_firmware_update", []),
]
for name, method, args in calls:
    serial.frames.clear()
    getattr(node, method)(*args)
    cases[name] = list(serial.frames)

node.frequency, node.bandwidth, node.txpower, node.sf, node.cr = 868000000, 125000, 0, 7, 5
serial.frames.clear()
node.initRadio()
node.setTNCMode()
cases["tnc"] = list(serial.frames)
# download methods include unrelated EEPROM parsing; capture the actual method but
# supply the response in write() and suppress parse_eeprom only for this vector.
node.parse_eeprom = lambda: None
original_write = serial.write
def download_write(data):
    result = original_write(data)
    node.eeprom = bytes(range(200))
    return result
serial.write = download_write
for name, method in [("eeprom-read", "download_eeprom"), ("config-read", "download_cfg_sector")]:
    serial.frames.clear()
    getattr(node, method)()
    cases[name] = list(serial.frames)

source = Path(reference.__file__)
result = {
    "revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
    "adapter": "Capture serial writes; sleep suppressed; EEPROM parser suppressed for read-command capture only.",
    "cases": cases,
}
target = Path(__file__).parent / "Fixtures" / "rnodeconf-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(f"Verified RNS import: {reference.__file__}")
print(f"Wrote {len(cases)} capture-only upstream cases to {target}")
