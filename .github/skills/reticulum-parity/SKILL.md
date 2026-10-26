---
name: reticulum-parity
description: Source-grounded guide for understanding Reticulum Network Stack (RNS) protocols, tracing Python reference behavior, and implementing or verifying DotReticulum parity. Use for RNS architecture, wire formats, routing, links, resources, interfaces, utilities, and roadmap work.
---

# Reticulum protocol and parity engineering

Use this skill when researching or changing Reticulum behavior in DotReticulum.
It is a navigation and engineering guide to the pinned source, not a substitute
for reading that source. Never infer a wire contract from this summary or from
memory when the reference implementation can establish it.

## Authority and project context

- The authoritative reference is the Reticulum-hosted repository at
  `rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`, pinned for
  current live tests to commit `a29bfb51c468945c96a1ee22cae0114e993e2391`
  (RNS 1.5.7). GitHub `markqvist/Reticulum` is a secondary mirror.
- Use the verified working checkout and isolated Python environment configured
  by `DOTRETICULUM_UPSTREAM` and `DOTRETICULUM_PYTHON`. Verify the commit,
  imported module paths and versions using `tests\Interop\reference_environment.py`.
  Do not silently use an installed package, move the pin, or relabel old vectors.
- Read `TODO.md` first and implement only the requested, prerequisite-ready
  scope. Read `.github\development-history.md` for source references, evidence,
  intentional differences and known limitations. TODO is the only active progress
  checklist; `docs/archive/plan.md` and history are evidence, not other trackers.
- DotReticulum targets behavioral and wire parity with an idiomatic native
  .NET implementation: explicit ownership, asynchronous I/O, cancellation,
  bounded resources, testable boundaries and trimming/NativeAOT safety. It is
  experimental and AI-assisted, not official, endorsed, production-certified
  or community-recognized.
- Read `references\upstream-map.md` for a subsystem map and current boundaries.

## Understand an RNS behavior before changing it

1. State the externally observable contract and failure behavior. Identify
   packet fields, state transitions, defaults, configuration, persistence,
   CLI/process effects and relevant platforms as applicable.
2. Trace the pinned implementation from its public entry point through
   serialization, cryptography, transport/interface callbacks and teardown.
   Search callers and tests; distinguish a Python API detail from a behavior
   required for interoperability.
3. Execute the pinned Python implementation to generate independent expected
   vectors. Keep a small `Generate*.py` beside the owning xUnit tests. Record
   the exact upstream revision, source-file SHA-256 where relevant, imported
   module path and generation method. Do not hand-author expected protocol
   bytes or use DotReticulum output as the oracle.
4. Implement the smallest idiomatic .NET slice that preserves observable
   semantics. Keep Python limited to reference execution/adapters; C# owns
   production behavior and protocol assertions, PowerShell owns general
   orchestration.
5. Test vector agreement, malformed/boundary inputs, cancellation, concurrent
   operations, resource bounds, restart/persistence and failure recovery that
   apply to the contract. Add real mixed Python/.NET live tests when the claim
   requires interoperability. A loopback or vector pass is not live-node
   evidence.
6. Update only the relevant roadmap checkbox after its acceptance criteria are
   met. Record exact commands/results, test counts, reference provenance,
   platform/topology evidence and limitations in development history. Refresh
   the README interop table only when the evidence supports a changed claim.

## Architectural mental model

Reticulum composes identities and destinations with signed announcements,
transport path discovery, packet forwarding and higher-level link sessions.
Interfaces carry packets between nodes; the transport router selects paths and
applies interface modes and routing controls. Links provide peer sessions on
which requests/responses, receipts, resources and channels operate. Runtime
configuration, persistence and utilities add contracts beyond packet bytes.

Treat those layers separately while tracing behavior:

- Establish which layer owns the field, timer, retry, cache and cleanup.
- Distinguish raw bearer frames from decoded Reticulum packets and from link or
  application payloads.
- Track ingress, announcement origin and egress separately; a peer/bearer
  failure must not accidentally become a daemon-wide failure.
- Preserve cryptographic authentication and validation ordering. Reject
  malformed or unauthenticated state before it affects routing or persistence.
- Check whether work is bounded, cancellation-aware and owned through
  completion; do not add unbounded queues, background tasks or hidden retries.
- Prefer shared helpers and existing ownership boundaries over duplicate
  protocol logic or a speculative architecture rewrite.

## Interoperability and claims

Use these evidence levels precisely:

- **Reference vector:** expected bytes/state were independently generated by
  executing the pinned upstream; demonstrates a bounded contract only.
- **Managed/loopback test:** DotReticulum components work together locally;
  does not establish Python interoperability.
- **Live Python interoperability:** a pinned, verified Python peer exchanged
  the specified traffic with .NET. State direction, topology, peer settings,
  shims and exercised behavior.
- **NativeAOT execution:** the published binary executed the named behavior.
  Publish success and `--help` do not prove feature execution.
- **Platform/hardware evidence:** name the OS, architecture, interface/device
  and actual hardware used. A simulator or managed build is not device support.

Disclose adapters and reference-side workarounds, and say what their use
prevents claiming about unmodified upstream. Never call module-level coverage,
test count, a synthetically compatible peer, or one-way exchange full parity.
Keep untested directions, impaired-link cases, platforms and hardware explicit.

## Safety and implementation constraints

- Never invent a wire format, crypto primitive, timeout, retry policy or
  fragmentation scheme. Read and execute upstream behavior first.
- Preserve the Reticulum License and notices. Never add private keys,
  credentials or captured private traffic to fixtures or logs.
- Zero secret buffers; use constant-time comparisons and established project
  crypto APIs. Keep identity/public-key validation strict.
- Do not claim remote execution is safe: `rnx` requires explicit authorization.
- Keep unsupported upstream placeholders classified instead of fabricating
  features. Do not begin later roadmap phases unless the user requested them
  or a narrowly necessary prerequisite is established.
- Use the repository's documented commands and serialize builds/tests that
  share outputs. Run focused tests during development and required full gates
  for the completed scope.
