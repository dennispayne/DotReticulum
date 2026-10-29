"""Observe actual upstream Channel/Buffer message delivery, without executing commands.

The rnsh CLI is an executable Channel consumer. This peer uses its upstream
message classes, not ListenerSession or a substituted subprocess implementation.
Assertions and topology ownership remain in PowerShell.
"""
import argparse
import json
from pathlib import Path
import sys
import threading
import time

from reference_environment import verify_reference


def emit(**values):
    print(json.dumps(values), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--received", required=True)
    parser.add_argument("--send", required=True)
    args = parser.parse_args()
    verify_reference(expected_revision=args.revision)
    import RNS
    from RNS.Utilities.rnsh import protocol

    node = RNS.Reticulum(configdir=args.config, loglevel=0, require_shared_instance=False)
    RNS.logdest = RNS.LOG_CALLBACK
    RNS.logcall = lambda message: print(str(message), file=sys.stderr, flush=True)
    destination = RNS.Destination(RNS.Identity(), RNS.Destination.IN, RNS.Destination.SINGLE, "rnsh")
    incoming_eof = threading.Event()
    closed = threading.Event()
    connected = threading.Event()
    state = {"channel": None, "link": None, "messages": 0, "bytes": 0, "command": None}
    failures = []
    deadline = time.monotonic() + 90

    def send(message):
        channel = state["channel"]
        while not channel.is_ready_to_send():
            if time.monotonic() >= deadline or closed.is_set():
                raise TimeoutError("Upstream Channel became unavailable before send")
            time.sleep(0.005)
        channel.send(message)

    try:
        with Path(args.received).open("wb") as received:
            def handle(message):
                try:
                    if isinstance(message, protocol.VersionInfoMessage):
                        send(protocol.VersionInfoMessage())
                    elif isinstance(message, protocol.ExecuteCommandMesssage):
                        state["command"] = message.cmdline
                    elif isinstance(message, protocol.StreamDataMessage):
                        if message.stream_id != protocol.StreamDataMessage.STREAM_ID_STDIN:
                            raise ValueError("Unexpected incoming stream ID")
                        if incoming_eof.is_set():
                            raise ValueError("Stream data arrived after EOF")
                        state["messages"] += 1
                        state["bytes"] += len(message.data)
                        if state["bytes"] > 4 * 1024 * 1024:
                            raise ValueError("Incoming stream exceeded owned fixture bound")
                        received.write(message.data)
                        if message.eof:
                            received.flush()
                            incoming_eof.set()
                    return True
                except Exception as error:
                    failures.append(error)
                    incoming_eof.set()
                    return True

            def established(link):
                if connected.is_set():
                    link.teardown()
                    return
                state["link"] = link
                state["channel"] = link.get_channel()
                protocol.register_message_types(state["channel"])
                state["channel"].add_message_handler(handle)
                link.set_link_closed_callback(lambda _link: closed.set())
                connected.set()

            destination.set_link_established_callback(established)
            emit(event="ready", destination=destination.hash.hex(), version=RNS.__version__)
            if not incoming_eof.wait(max(0, deadline - time.monotonic())):
                raise TimeoutError("Upstream Channel did not receive stream EOF")
            if failures:
                raise failures[0]
            emit(event="received", messages=state["messages"], bytes=state["bytes"],
                 command=state["command"], eof=True)
            sent_messages = 0
            with Path(args.send).open("rb") as source:
                while data := source.read(protocol.StreamDataMessage.MAX_DATA_LEN):
                    send(protocol.StreamDataMessage(protocol.StreamDataMessage.STREAM_ID_STDOUT, data))
                    sent_messages += 1
            send(protocol.StreamDataMessage(protocol.StreamDataMessage.STREAM_ID_STDOUT, b"", True))
            send(protocol.StreamDataMessage(protocol.StreamDataMessage.STREAM_ID_STDERR, b"", True))
            send(protocol.CommandExitedMessage(0))
            emit(event="sent", messages=sent_messages, eof=True)
            if not closed.wait(max(0, deadline - time.monotonic())):
                raise TimeoutError("Candidate did not close its completed Channel link")
    finally:
        if state["link"] is not None:
            state["link"].teardown()
        node.exit_handler()


if __name__ == "__main__":
    main()
