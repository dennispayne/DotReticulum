# DotReticulum progress tracker

Last updated: 2026-10-08.

The initial foundation is complete; the full Reticulum stack is not.
Checked items represent implemented work, not full-stack interoperability.
Update this checklist and the README interoperability table as milestones land.
Record test results, reference revisions, and platform evidence before marking
protocol or portability milestones complete.

## Initial foundation

- [x] Initialize the modular .NET 10 / C# 13 solution and test projects.
- [x] Enable nullable diagnostics as errors and production AOT/trimming analysis.
- [x] Add README, contribution guidelines, Reticulum License, Code of Conduct,
  security policy, issue templates, and PR checklist.
- [x] Configure weekly Monday 04:00 UTC NuGet and GitHub Actions updates.
- [x] Implement Ed25519, X25519, HKDF-SHA256, AES-CBC/PKCS7, and authenticated
  Reticulum binary tokens.
- [x] Implement identity generation, private/public loading, signing, verification,
  and ephemeral encrypted payloads.
- [x] Add basic `rnid` identity generation and inspection commands.
- [x] Implement packet header parsing/packing, destination hashes, and packet hashes.
- [x] Add standards vectors, pinned Python reference fixtures, and malformed-input
  tests.
- [x] Add code-path-filtered build/test/coverage and NativeAOT CI for standard
  Ubuntu, Windows, and macOS runners.

## P0 — Basic stack, utilities, and telemetry

### Interfaces and routing

- [x] Define a cancellation-aware asynchronous packet interface contract.
- [x] Implement interface lifecycle management and bounded packet processing.
- [x] Implement UDP datagram and HDLC-framed TCP client/server interfaces.
- [ ] Implement serial interfaces.
- [ ] Implement announce creation, validation, propagation, and rate limiting.
- [ ] Implement path discovery, routing tables, hop handling, and multi-hop transport.
- [ ] Validate routing resilience under packet loss and latency.

### Links and resources

- [ ] Implement reliable link establishment and peer proofs.
- [ ] Implement RTT measurements, keep-alives, timeouts, and link teardown.
- [ ] Implement upstream-compatible resource segmentation and reassembly.
- [ ] Implement windowed resource transfer, compression, checksums, and cancellation.
- [ ] Verify multi-megabyte resource transfers in both directions with Python nodes.

### Applications

- [ ] Implement basic LXMF messaging and verify exchange with Python `lxmd`.
- [ ] Implement `rnsd` daemon / transport node.
- [ ] Implement `rncp` file transfer.
- [ ] Implement `rnx` remote command execution with explicit authorization.
- [ ] Implement telemetry sending/receiving and node statistics.

## P1 — Push-to-talk audio

- [ ] Define the audio codec abstraction and validate NativeAOT codec bindings.
- [ ] Implement low-latency voice frames over links and unlinked channels.
- [ ] Implement the half-duplex `rns-audio` CLI / daemon.
- [ ] Validate voice behavior under constrained bandwidth, loss, and latency.

## P2 — Adaptive video

- [ ] Integrate a low-bandwidth video encoding pipeline.
- [ ] Implement link-capacity estimation and adaptive resolution/frame rate/color.
- [ ] Build and validate a multi-party audio/video receiver proof of concept.

## Additional interfaces and portability

- [ ] Implement AX.25, RNode, I2P, and WebSocket interfaces.
- [ ] Verify Windows and macOS builds, tests, and native executables in hosted CI.
- [ ] Validate Linux ARM and embedded hardware support.
- [ ] Assess and document RISC-V, FreeBSD, Android, and iOS support, including
  runtime/AOT limitations rather than assuming all targets support NativeAOT.

## Automation and interoperability

- [ ] Add nightly 02:00 UTC / manual / `interop-check`-labeled PR Python interop
  tests for discovery, encrypted delivery, links/resources, and LXMF.
- [ ] Add weekly Sunday 03:00 UTC / manual five-node mixed Python/.NET Docker mesh
  tests using serial/pty and TCP bridges with latency/loss injection.
- [ ] Add CodeQL for C# changes in PRs and weekly Monday 05:00 UTC analysis.
- [ ] Add daily 00:00 UTC stale issue/PR maintenance with a 60-day inactivity threshold.
- [ ] Verify live Python interoperability for every implemented P0 capability.
- [ ] Verify interoperability with conforming C++ and Rust implementations.
- [ ] Keep heavy workflows deconflicted and restricted to standard GitHub runners.

## Verification record and open gaps

Foundation and interface milestone verification recorded 2026-10-08:

- [x] Release solution tests: 214 passed (162 Core + 47 Crypto + 5 Interfaces),
  none skipped.
- [x] UDP loopback, TCP bidirectional loopback, manager lifecycle/backpressure,
  and upstream HDLC frame-vector tests passed.
- [x] TCP HDLC frame vector generated from upstream `TCPInterface.py` at
  `e40191b3d193b46b7f2d8a44424a594cd758839b`; source SHA-256
  `0e397dbdd9ce47db533a7181a4b924ef351fb0dee8d8e43c0cc1c64be173668b`.
- [x] Linux `linux-x64` NativeAOT publish passed without warnings and the native
  CLI help smoke test passed during this interface milestone.
- [x] Foundation changes passed secret scanning and independent review.
- [x] Interface changes passed secret scanning; current C#/Python CodeQL reported
  zero alerts.
- [ ] Obtain an independent code review; the automated review executable was
  unavailable in this environment.
- [ ] Obtain live-node interoperability evidence; primitive vectors alone do not
  establish full wire compatibility.
- [ ] Run hosted Windows/macOS interface loopback tests and a live Python node
  test; this local run covered Linux loopback only.

For protocol changes, require independently generated Python reference vectors
with the exact upstream revision and generation method. Round-trip tests alone
are insufficient. Do not add placeholder suites that imply unimplemented
capabilities have been verified.
