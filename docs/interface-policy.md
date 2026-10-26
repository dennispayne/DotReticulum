# Interface selection and ingress feedback

Interface policy determines which bearers are selected for outgoing operations and how a transport router signals data-ingress pressure to Backbone interfaces. It is not a security or authentication boundary.

## Outgoing selection

The generic `outgoing` setting defaults to true. Setting it to false excludes an interface from broadcast and attached-interface selection, including announces, path requests, attached link output, and automatic proofs. It does not prohibit known-path transmission or explicit low-level sends: those operations address a route/interface directly in the pinned implementation. A direct packet send can therefore use an interface whose `outgoing` selection flag is false.

Spawned child interfaces inherit the parent's outgoing-selection policy unless a bearer defines an upstream-specific exception. The manager applies policy; a bearer should not independently reinterpret it. Disabled selection does not count as a sent packet or failed transmission.

## Backbone ingress feedback

Backbone flow control observes the transport router's global DATA queue, not the manager's merged channel or control queues. `PacketInterfaceIngressQueue` keeps its identity while the router rebinds its depth provider to the DATA scheduler. It signals pressure before queue admission, including a rejected full-queue attempt. The immediate threshold is `max(128, max(4, floor(0.90 * capacity)))`; with the default 256-frame queue this is 230 frames. The source's periodic hysteresis uses high and low watermarks of 68% and 10%, sampled every 250 ms.

One manager-scoped controller accounts for all Backbone peers and listeners owned by that manager. Peer teardown unregisters the producer. Fast-flap history is bounded and scoped to the manager lifetime rather than process-global, so unrelated stack instances do not affect one another. Standalone interfaces can receive a manual queue-depth sample but do not claim router DATA-queue parity without the router provider.

## Differences from upstream

The .NET implementation scopes upstream process-global throttling state to a manager's queue identity and bounds its flap history. It preserves the distinction between outgoing-selection policy and direct transmission instead of treating `outgoing = no` as a send prohibition.

## Verification

`Parity6BackboneSharedIngressTests` covers manager-scoped feedback, DATA queue rebinding, threshold boundaries, and producer cleanup. `Parity6BackboneTests` covers the bearer flow-control behavior. `tests\DotReticulum.Interfaces.Tests\GenerateParity6BackbonePipeVectors.py` executes pinned-source observations; these tests do not represent a live multi-router network.
