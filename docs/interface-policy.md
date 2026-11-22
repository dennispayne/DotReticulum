# Interface selection and ingress feedback

Interface policy decides which bearers Reticulum selects for outgoing traffic, and how a transport router tells Backbone interfaces that its data queue is under pressure. It is a traffic-selection mechanism. It is not a security or authentication boundary.

## Configuring outgoing selection

Every interface accepts the common `outgoing` option. It defaults to `yes`. Set it to `no` when you want an interface to receive traffic but never be chosen for broadcast-style output:

```
[interfaces]
  [[Listen Only TCP]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 0.0.0.0
    listen_port = 4242
    outgoing = no
```

With `outgoing = no`, the interface is excluded from broadcast and attached-interface selection. That covers:

- announces
- path requests
- attached link output
- automatic proofs

It does **not** stop known-path transmission or explicit low-level sends. In the pinned implementation those operations address a route or interface directly, so a direct packet send can still use an interface whose `outgoing` flag is `no`.

Spawned child interfaces inherit their parent's outgoing-selection policy unless a bearer defines an upstream-specific exception. The interface manager applies the policy; individual bearers should not reinterpret it. When selection skips an interface, that is not counted as a sent packet or a failed transmission.

## Backbone ingress feedback

Backbone interfaces can slow their peers down when the local router is falling behind. The signal they use is the transport router's global DATA queue. It is not the manager's merged channel or the control queues.

How the feedback works:

- `PacketInterfaceIngressQueue` keeps its identity while the router rebinds its depth provider to the DATA scheduler.
- Pressure is signalled before queue admission, including on a rejected full-queue attempt.
- The immediate threshold is `max(128, max(4, floor(0.90 * capacity)))`. With the default 256-frame queue this is 230 frames.
- The source's periodic hysteresis uses high and low watermarks of 68% and 10%, sampled every 250 ms.

One manager-scoped controller accounts for all Backbone peers and listeners owned by that manager. Peer teardown unregisters the producer. Fast-flap history is bounded and scoped to the manager's lifetime rather than the process, so unrelated stack instances do not affect one another.

A standalone interface, used without a router, can receive a manual queue-depth sample. It does not claim router DATA-queue parity without the router's depth provider.

## Differences from upstream

- Upstream keeps throttling state process-global. DotReticulum scopes it to a manager's queue identity and bounds the flap history.
- DotReticulum keeps the distinction between outgoing-selection policy and direct transmission, rather than treating `outgoing = no` as a send prohibition.

## Verification

- `Parity6BackboneSharedIngressTests` covers manager-scoped feedback, DATA queue rebinding, threshold boundaries and producer cleanup.
- `Parity6BackboneTests` covers bearer flow-control behaviour.
- `tests\DotReticulum.Interfaces.Tests\GenerateParity6BackbonePipeVectors.py` executes pinned-source observations.

These tests do not represent a live multi-router network.
