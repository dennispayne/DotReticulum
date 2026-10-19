"""Real upstream rngit server adapter; C# owns the Git fixture and assertions.

Reference: Reticulum-hosted snapshot pinned by reference_environment.py.
The harness redirects upstream's temporary bundle directories into its owned
test directory; the upstream server/list/fetch/resource implementations are unchanged.
Its Git subprocesses allow bare-repository discovery, which upstream requires.
"""
import argparse
import json
import os
import sys
import tempfile
import threading
import time

import RNS
from RNS.Utilities.rngit.server import ReticulumGitNode
from reference_environment import PINNED_RNS_SHA, verify_reference


def main():
    sys.stdout.reconfigure(line_buffering=True)
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--repositories", required=True)
    args = parser.parse_args()
    root = os.path.abspath(args.config)
    os.makedirs(root, exist_ok=True)
    # Upstream uses cwd-based Git commands inside bare repositories. Scope this
    # override to the harness and children; do not change the user's Git config.
    config_count = int(os.environ.get("GIT_CONFIG_COUNT", "0"))
    os.environ[f"GIT_CONFIG_KEY_{config_count}"] = "safe.bareRepository"
    os.environ[f"GIT_CONFIG_VALUE_{config_count}"] = "all"
    os.environ["GIT_CONFIG_COUNT"] = str(config_count + 1)
    verify_reference()

    tempfile.tempdir = os.path.join(root, "scratch")
    os.makedirs(tempfile.tempdir)
    group = os.path.abspath(args.repositories)

    rnsdir = os.path.join(root, "rns")
    os.makedirs(rnsdir)
    with open(os.path.join(rnsdir, "config"), "w", encoding="utf-8") as file:
        file.write(f"""[reticulum]
  enable_transport = No
  share_instance = No
  panic_on_interface_error = No
[logging]
  loglevel = 4
[interfaces]
  [[Loopback]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.tcp_server_port}
""")
    serverdir = os.path.join(root, "rngit")
    os.makedirs(serverdir)
    with open(os.path.join(serverdir, "config"), "w", encoding="utf-8") as file:
        file.write(f"""[rngit]
  node_name = DotReticulum loopback fixture
  record_stats = No
  mirror_interval = 0
[logging]
  loglevel = 4
[pages]
  serve_nomadnet = No
[repositories]
  fixtures = {group}
  hidden = {group}
[access]
  fixtures = read:all
  hidden = read:none
""")
    RNS.Reticulum(configdir=rnsdir)
    server = ReticulumGitNode(configdir=serverdir)
    RNS.logdest = RNS.LOG_STDOUT
    RNS.loglevel = RNS.LOG_DEBUG
    if not server.ready:
        raise RuntimeError("Upstream rngit server not ready")
    print("@@" + json.dumps({
        "event": "ready", "dest": server.destination.hash.hex(),
        "source": RNS.__file__, "pin": PINNED_RNS_SHA,
    }), flush=True)

    done = threading.Event()

    def announce():
        while not done.wait(1):
            server.announce()

    threading.Thread(target=announce, daemon=True).start()
    # A blocking read on a synchronous Windows stdin pipe prevents Git's
    # inherited-stdin console detection from completing in a request worker.
    os.set_blocking(sys.stdin.fileno(), False)
    commands = b""
    while True:
        try:
            incoming = os.read(sys.stdin.fileno(), 4096)
            if not incoming:
                break
            commands += incoming
            if b"quit\n" in commands or b"quit\r\n" in commands:
                break
        except BlockingIOError:
            pass
        time.sleep(0.05)
    done.set()
    time.sleep(0.1)


if __name__ == "__main__":
    main()
