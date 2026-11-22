# Remaining DotReticulum work

This is the single active progress checklist, extracted from the unchecked
items in the [archived plan](docs/archive/plan.md) on 2026-10-26.
[Development history](.github/development-history.md) supplies evidence;
the [compatibility ledger](docs/compatibility-contracts.md) defines residual
contracts and acceptance boundaries. [Completed entries](docs/archive/completed-todos.md)
are archived rather than repeated here.

Physical-device acceptance and targets outside the existing Windows, Linux x64
and macOS ARM64 Actions matrix are out of scope; they remain unverified, not
passed. Software-only tests do not establish hardware interoperability.

- [ ] Parity 6 - Upstream interface coverage
  - [ ] RNodeInterface and RNodeMultiInterface remaining software contracts (serial/TCP accepted; Windows BLE software slice implemented)
  - [ ] Desktop RNode BLE advertisement selection and Linux/macOS providers, with software-only acceptance (Windows cached paired-device provider is partial)
  - [ ] I2PInterface
  - [ ] Complete UDP/TCP/serial configuration and software acceptance
  - [ ] I2P public-tunnel delivery after persisted bearer restart (still fails for both .NET and pinned upstream); separate-router overlay, successful transport-enabled persisted startup (fresh succeeds, reused-state probe independently fails for native and pinned upstream candidates) and broader published-native acceptance remain outstanding
- [ ] Parity 8 - Compatibility and release acceptance
  - [ ] Broader mesh acceptance: broader alternate-route topologies, cold initial-window resource-loss recovery, sustained Channel/resource soak and impaired native-binary/per-bearer matrices (pinned upstream cold-window cancellation is independently reproduced, not passing recovery)
  - [ ] Windows, Linux x64 and macOS ARM64 managed/native feature flows
  - [ ] Diagnose the earlier intermittent macOS reverse-send timeout (not reproduced in the subsequent five-repeat run); longer Channel/resource soak and broader impaired bearer matrices remain outstanding
  - [ ] Desktop RNode BLE and other missing/partial in-scope software identified in the compatibility ledger
  - [ ] Per-interface software interoperability where testable without physical hardware
- [ ] CI/CD and automation
  - [ ] Expand shared advertised-claim scenarios for heterogeneous routing, broader application request boundaries and Channel/Buffer
