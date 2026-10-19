"""Thin execution adapter for the pinned upstream ReticulumGitClient.

C# owns repositories, request bytes, process lifetimes and all assertions.
No Python client fetches objects on behalf of DotReticulum.
"""
import argparse
import base64
import json
import os
import sys
import time

import RNS
from RNS.Utilities.rngit.server import ReticulumGitClient
from RNS.vendor import umsgpack as mp
from reference_environment import PINNED_RNS_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, required=True)
    parser.add_argument("--target", required=True)
    args = parser.parse_args()
    sys.stdout.reconfigure(line_buffering=True, encoding="utf-8")
    verify_reference()
    root = os.path.abspath(args.config)
    os.makedirs(root, exist_ok=True)
    rnsdir = os.path.join(root, "rns")
    os.makedirs(rnsdir, exist_ok=True)
    with open(os.path.join(rnsdir, "config"), "w", encoding="utf-8") as file:
        file.write(f"""[reticulum]
  enable_transport = no
  share_instance = no
[interfaces]
  [[Native server]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {args.rnsd_port}
""")
    RNS.Reticulum(configdir=rnsdir)
    client = ReticulumGitClient(configdir=root)
    if os.name == "nt":
        original_response_ready = client._response_ready

        def close_before_retain(receipt):
            # server.py moves an open resource file; Windows denies its unlink.
            if hasattr(receipt.response, "read"):
                receipt.response.close()
            original_response_ready(receipt)

        client._response_ready = close_before_retain
    client.connect_remote(args.target)
    deadline = time.time() + 45
    while not client.link_ready and not client.link_failed and time.time() < deadline:
        time.sleep(0.05)
    if not client.link_ready:
        raise RuntimeError("Reference client link establishment failed")
    print("\n@@" + json.dumps({
        "event": "ready", "dest": client.identity.hash.hex(),
        "source": RNS.__file__, "pin": PINNED_RNS_SHA,
        "reference_shim": "Close resource before upstream Windows retain move" if os.name == "nt" else None,
    }), flush=True)
    for line in sys.stdin:
        if line.strip() == "quit":
            break
        command = json.loads(line)
        request = mp.unpackb(base64.b64decode(command["request"], validate=True))
        if command.get("sign_content", False):
            request["signature"] = client.identity.sign(request["content"].encode("utf-8"))
        response, metadata = client.send_request(command["path"], request, timeout=30)
        if hasattr(response, "read"):
            with response:
                response = response.read()
        print("\n@@" + json.dumps({
            "event": "response",
            "data": base64.b64encode(mp.packb(response)).decode("ascii"),
            "metadata": base64.b64encode(mp.packb(metadata)).decode("ascii"),
        }), flush=True)
    if client.link:
        client.link.teardown()


if __name__ == "__main__":
    main()
