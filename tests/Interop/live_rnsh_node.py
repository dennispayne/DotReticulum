"""Thin POSIX adapter executing pinned rnsh ListenerSession and protocol codecs.

JSON only controls/observes upstream APIs; no shell protocol is reimplemented.
The client driver intentionally bypasses interactive UI (not a CLI/raw-TTY claim).
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
import RNS
from RNS.Utilities.rnsh import protocol, session


def emit(event, **values):
    print("@@" + json.dumps({"event": event, **values}), flush=True)


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    root = Path(args.config)
    root.mkdir(parents=True, exist_ok=True)
    (root / "config").write_text(f"""[reticulum]
  enable_transport = No
  share_instance = No
[logging]
  loglevel = 1
[interfaces]
  [[ToDotNet]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {args.port}
""")
    rns = RNS.Reticulum(configdir=str(root), loglevel=7)
    # Keep machine-readable stdout independent of upstream diagnostics.
    RNS.logdest = RNS.LOG_CALLBACK
    RNS.logcall = lambda message: print(str(message), file=sys.stderr, flush=True)
    identity = RNS.Identity()
    destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "rnsh")
    loop = asyncio.get_running_loop()
    session.ListenerSession.allow_all = True
    session.ListenerSession.allow_remote_command = True
    session.ListenerSession.default_command = ["/bin/sh"]
    destination.set_link_established_callback(
        lambda link: session.ListenerSession(session.RNSOutlet(link), link.get_channel(), loop))
    commands = asyncio.Queue(16)

    def read_commands():
        for line in sys.stdin:
            loop.call_soon_threadsafe(commands.put_nowait, line)
        loop.call_soon_threadsafe(commands.put_nowait, '{"op":"quit"}')
    threading.Thread(target=read_commands, daemon=True).start()

    async def pump():
        while True:
            await session.ListenerSession.pump_all()
            await asyncio.sleep(0.005)
    pumping = asyncio.create_task(pump())
    def emit_sessions(event):
        emit(event, sessions=[{
            "state": item.state.name, "authenticated": item.authenticated,
            "terminated": item.terminated, "process": item.process.pid if item.process else None,
            "return_code": item.return_code, "stdout": len(item.stdout_buf), "stderr": len(item.stderr_buf),
            "link_id": item.outlet.link.link_id.hex(),
            "running": item.process.running if item.process else False,
        } for item in session.ListenerSession.sessions])

    async def observe():
        while True:
            await asyncio.sleep(2)
            emit_sessions("sessions")
    observing = asyncio.create_task(observe())
    destination.announce()
    emit("ready", dest=destination.hexhash, identity=identity.hexhash,
         rns_file=RNS.__file__, version=RNS.__version__)
    try:
        while True:
            command = json.loads(await commands.get())
            if command["op"] == "quit":
                return
            if command["op"] == "announce":
                destination.announce()
                emit("announced")
                continue
            if command["op"] == "session_status":
                emit_sessions("session_status")
                continue
            if command["op"] != "execute":
                raise ValueError("Unknown operation")
            target = bytes.fromhex(command["destination"])
            RNS.Transport.request_path(target)
            deadline = time.monotonic() + 25
            while not RNS.Transport.has_path(target):
                if time.monotonic() > deadline:
                    raise TimeoutError("rnsh path")
                await asyncio.sleep(0.02)
            outgoing = RNS.Destination(RNS.Identity.recall(target), RNS.Destination.OUT,
                                       RNS.Destination.SINGLE, "rnsh")
            link = RNS.Link(outgoing)
            while link.status != RNS.Link.ACTIVE:
                if time.monotonic() > deadline:
                    raise TimeoutError("rnsh link")
                await asyncio.sleep(0.02)
            channel = link.get_channel()
            protocol.register_message_types(channel)
            received = asyncio.Queue(64)
            channel.add_message_handler(lambda message: loop.call_soon_threadsafe(received.put_nowait, message) or True)
            link.identify(identity)
            await asyncio.sleep(0.1)

            async def send(message):
                while not channel.is_ready_to_send():
                    await asyncio.sleep(0.005)
                channel.send(message)
            await send(protocol.VersionInfoMessage())
            version = await asyncio.wait_for(received.get(), 15)
            if not isinstance(version, protocol.VersionInfoMessage) or version.protocol_version != 1:
                raise ValueError("Version response")
            await send(protocol.ExecuteCommandMesssage(command["argv"], True, True, True,
                                                       None, "xterm", 24, 80, 0, 0))
            if command.get("cancel"):
                await asyncio.sleep(0.5)
                link.teardown()
                emit("cancelled")
                continue
            payload = bytes.fromhex(command.get("input", ""))
            for offset in range(0, len(payload), 128):
                await send(protocol.StreamDataMessage(0, payload[offset:offset + 128]))
            await send(protocol.StreamDataMessage(0, b"", True))
            output = bytearray()
            error = bytearray()
            while True:
                message = await asyncio.wait_for(received.get(), 30)
                if isinstance(message, protocol.StreamDataMessage):
                    (output if message.stream_id == 1 else error).extend(message.data)
                elif isinstance(message, protocol.ErrorMessage):
                    emit("error", message=message.msg)
                    break
                elif isinstance(message, protocol.CommandExitedMessage):
                    emit("result", stdout=output.hex(), stderr=error.hex(), code=message.return_code)
                    break
            link.teardown()
    finally:
        pumping.cancel()
        observing.cancel()
        await session.ListenerSession.terminate_all("adapter shutdown")
        RNS.Reticulum.exit_handler()


asyncio.run(main())
