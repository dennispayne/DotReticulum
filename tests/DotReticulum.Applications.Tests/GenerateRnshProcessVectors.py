"""Observe pinned POSIX CallbackSubprocess, including EOF and callback ordering."""
import asyncio
import json
import os
from pathlib import Path
import sys
import threading

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "Interop"))
sys.path.insert(0, os.environ["DOTRETICULUM_UPSTREAM"])
from reference_environment import verify_reference
verify_reference(require_lxmf=True)
from RNS.Utilities.rnsh.process import CallbackSubprocess


async def observe(threaded, eof):
    events = []
    loop = asyncio.get_running_loop()
    child = CallbackSubprocess(
        argv=["/usr/bin/python3", "-c", "import os; d=b''\nwhile len(d)<256: d+=os.read(0,256-len(d))\nos.write(1,d); os.write(2,b'err')"],
        env={}, loop=loop,
        stdout_callback=lambda data: events.append(["stdout", data.hex()]),
        stderr_callback=lambda data: events.append(["stderr", data.hex()]),
        terminated_callback=lambda code: events.append(["exit", code]),
        stdin_is_pipe=True, stdout_is_pipe=True, stderr_is_pipe=True)
    if threaded:
        worker = threading.Thread(target=child.start)
        worker.start()
        while worker.is_alive():
            await asyncio.sleep(0.01)
    else:
        child.start()
    child.write(bytes(range(256)))
    if eof:
        child.close_stdin()
    await asyncio.sleep(2)
    result = {"threaded": threaded, "eof": eof, "events": events, "return_code": child.return_code,
              "running": child.running}
    if child.running:
        child.terminate()
        await asyncio.sleep(1.2)
    return result


async def main():
    results = []
    for threaded in (False, True):
        for eof in (False, True):
            results.append(await observe(threaded, eof))
    print(json.dumps(results, indent=2))


asyncio.run(main())
