"""Generate the pinned signed announce and forwarded packet fixtures.

Run with PYTHONPATH pointing at Reticulum revision
e40191b3d193b46b7f2d8a44424a594cd758839b. Generation uses upstream Identity and
Destination methods plus Transport.mangle_hops from that checkout.
"""

import subprocess
from pathlib import Path

import RNS

REVISION = "e40191b3d193b46b7f2d8a44424a594cd758839b"
checkout = Path(RNS.__file__).resolve().parent.parent
assert subprocess.check_output(
    ["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True
).strip() == REVISION

private = bytes(range(64))
identity = RNS.Identity(create_keys=False)
assert identity.load_private_key(private)
name = "example.echo"
name_hash = RNS.Identity.full_hash(name.encode("utf-8"))[:10]
destination_hash = RNS.Destination.hash(identity, "example", "echo")
random_hash = bytes.fromhex("a1b2c3d4e5") + (1_700_000_000).to_bytes(5, "big")
app_data = b"upstream-vector"
signed_data = (
    destination_hash + identity.get_public_key() + name_hash + random_hash + app_data
)
signature = identity.sign(signed_data)
payload = identity.get_public_key() + name_hash + random_hash + signature + app_data
packet = bytes([RNS.Packet.ANNOUNCE, 0]) + destination_hash + b"\x00" + payload
forwarded_packet = RNS.Transport.mangle_hops(packet, 1)
print(f"destination hash: {destination_hash.hex()}")
print(f"name hash: {name_hash.hex()}")
print(f"random hash: {random_hash.hex()}")
print(f"signature: {signature.hex()}")
print(f"packet: {packet.hex()}")
print(f"forwarded packet: {forwarded_packet.hex()}")
