"""Execute pinned RNS I2P framing/SAM/persistence paths without a router.

Shims: a capturing socket for process_outgoing and a file-existence sentinel
which stops server_tunnel after its unmodified key-path calculations. No
Python tunnel, router, private key generation or network traffic is involved.
"""
import hashlib
import inspect
import json
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

verify_reference(require_lxmf=True)
import RNS
from RNS.Interfaces.I2PInterface import I2PController, I2PInterface, I2PInterfacePeer
from RNS.vendor.i2plib import sam, utils


class CapturingSocket:
    def sendall(self, data):
        self.frame = data


def frame(payload):
    peer = I2PInterfacePeer.__new__(I2PInterfacePeer)
    peer.online = True
    peer.writing = False
    peer.kiss_framing = False
    peer.socket = CapturingSocket()
    peer.txb = 0
    peer.parent_interface = None
    peer.process_outgoing(payload)
    return {"payload": payload.hex(), "frame": peer.socket.frame.hex(), "txb": peer.txb}


class StopAfterPaths(Exception):
    pass


def paths(name, identity_hash, legacy):
    captured = []

    def exists(path):
        captured.append(path)
        if len(captured) == 1:
            return legacy
        raise StopAfterPaths()

    controller = I2PController.__new__(I2PController)
    controller.storagepath = "reference-storage/i2p"
    with patch.object(RNS.Transport, "identity", SimpleNamespace(hash=identity_hash)):
        with patch("RNS.Interfaces.I2PInterface.os.path.isfile", side_effect=exists):
            try:
                controller.server_tunnel(SimpleNamespace(name=name))
            except StopAfterPaths:
                pass
    return {"name": name, "identityHash": identity_hash.hex(), "legacyExists": legacy,
            "legacyFile": Path(captured[0]).name, "selectedFile": Path(captured[1]).name}


root = Path(inspect.getfile(I2PInterface)).resolve().parents[2]
sources = ["RNS/Interfaces/I2PInterface.py", "RNS/vendor/i2plib/sam.py",
           "RNS/vendor/i2plib/aiosam.py", "RNS/vendor/i2plib/tunnel.py",
           "RNS/vendor/i2plib/utils.py", "RNS/Reticulum.py", "RNS/Interfaces/Interface.py"]
# A synthetic PUBLIC destination, not private key material.
public = bytes(384) + bytes([0, 0, 0])
result = {
    "revision": PINNED_RNS_SHA,
    "version": RNS.__version__,
    "rnsModule": RNS.__file__,
    "sourceSha256": {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in sources},
    "method": __doc__.strip(),
    "bitrate": I2PInterface.BITRATE_GUESS,
    "defaultIfacSize": I2PInterface.DEFAULT_IFAC_SIZE,
    "minimumConfiguredBitrate": RNS.Reticulum.MINIMUM_BITRATE,
    "reconnectSeconds": I2PInterfacePeer.RECONNECT_WAIT,
    "probeAfterSeconds": I2PInterfacePeer.I2P_PROBE_AFTER,
    "readTimeoutSeconds": I2PInterfacePeer.I2P_READ_TIMEOUT,
    "samDefaultAddress": list(sam.DEFAULT_ADDRESS),
    "samCommands": [
        sam.hello("3.1", "3.1").decode(),
        sam.dest_generate(sam.Destination.default_sig_type).decode(),
        sam.session_create("STREAM", "reticulum-vector", "TRANSIENT").decode(),
        sam.naming_lookup("example.b32.i2p").decode(),
        sam.stream_connect("reticulum-vector", "public-destination").decode(),
        sam.stream_accept("reticulum-vector").decode(),
    ],
    "publicDestination": sam.Destination(public).base64,
    "publicBase32": sam.Destination(public).base32,
    "frames": [frame(p) for p in [bytes([0, 1, 0x7e, 0x7d, 0xff]), bytes(range(256))]],
    "paths": [paths("I2P vector", bytes(range(16)), old) for old in (False, True)],
}
target = Path(__file__).parent / "Fixtures" / "parity6-i2p.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(target)
