# Interface selection and shared ingress feedback

## Pinned source and acceptance

The reference is Reticulum `a29bfb51c468945c96a1ee22cae0114e993e2391`,
RNS 1.5.7. `Reticulum.py:1030-1032` defaults `outgoing` to true and applies
the configured false value after constructing an interface.
`Transport.py:1324-1331,1390-1441,1445-1456` distinguishes direct transmission,
known-path transmission and outgoing-interface selection.
The `Transport.py` SHA-256 is
`333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c`;
the generated selection fixture SHA-256 is
`6a2793168a88a9298bd7049d90a6b2ba6a6e0c584ff0aa957dc67277cf046796`.

`outgoing = no` is **not a transmit prohibition or security boundary**:
upstream's broadcast/attached-interface selection checks `OUT`, but its
known-path and low-level transport-forwarding branches do not. Preserve that
distinction rather than silently turning this setting into a different policy.
Native broadcasts, announces, path requests, attached link output and automatic
proofs must select eligible outgoing interfaces; known paths and explicit
low-level `SendToAsync`/`SendRawToAsync` remain direct transmission operations.
Children inherit the parent's selection policy. Disabled selection must not
increment sent-byte, packet, announce or failure counters.

The independent generator executes unmodified `Transport._outbound` and
`Transport.transmit` with a recording bearer, an isolated actual reference
runtime and no network interfaces. It observes outgoing true/false for
broadcast, attached, known-path and raw/direct branches. Native tests must
exercise the same distinctions, inherited child policy, attach/reload and
statistics. The recording bearer is not live-node or physical-device evidence.

`Transport.py:48-72` couples bounded DATA ingress pressure to Backbone's immediate
throttling. `PacketInterfaceIngressQueue` initially exposes its manager's
merge queue; constructing the router rebinds that same identity to the actual
priority scheduler's DATA queue and configured capacity, not the merge queue or
the sum of control queues. DATA enqueue attempts signal the immediate high
watermark before admission, including rejected full-queue attempts. The immediate
threshold is `max(128, max(4, floor(0.90 * capacity)))`: 230 at the default
256-frame DATA capacity, with the source's 128-frame floor preserved.
Snapshots are read before invoking feedback observers, outside the scheduler's
lock. Queue identity scopes shared state to one manager's lifetime.
Backbone owns its source-specific feedback timers, producer accounting and
flap blocking; Core does not duplicate the bearer state machine.
