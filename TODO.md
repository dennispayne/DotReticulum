# Remaining DotReticulum work

This is the single active progress checklist, extracted from the unchecked
items in the [archived plan](docs/archive/plan.md) on 2026-10-26.
[Development history](.github/development-history.md) supplies evidence;
the [compatibility ledger](docs/compatibility-contracts.md) defines residual
contracts and acceptance boundaries. [Completed entries](docs/archive/completed-todos.md)
are archived rather than repeated here.

- [ ] Parity 6 - Upstream interface coverage
  - [ ] RNodeInterface and RNodeMultiInterface full software contracts (serial/TCP accepted; Windows BLE software slice implemented, remaining BLE discovery/platform/device acceptance outstanding)
  - [ ] Desktop RNode BLE live-advertisement selection, Linux/macOS providers and physical platform/device acceptance (Windows cached paired-device provider is partial; local host has no Bluetooth adapter)
  - [ ] I2PInterface
  - [ ] Android-specific adapters or documented native platform equivalents (deferred by user)
  - [ ] Complete UDP/TCP/serial configuration and validate real serial/radio hardware
  - [ ] Android USB/Bluetooth permission/adaptor contracts and on-device execution (deferred by user)
  - [ ] I2P public-tunnel delivery after persisted bearer restart (still fails for both .NET and pinned upstream); separate-router overlay, successful transport-enabled persisted startup (fresh succeeds, reused-state probe independently fails for native and pinned upstream candidates) and broader published-native acceptance remain outstanding
  - [ ] Authorized physical serial/TNC/RNode/Multi/Weave and Android USB/Bluetooth/RF acceptance (one Windows RAK receive-only startup/reconfiguration/off/reopen verified; one radio only, RF acceptance explicitly retained as unverified; remaining devices/platform access/settings unavailable; see development history)
- [ ] Parity 7 - Upstream utility coverage (software accepted; physical device acceptance outstanding)
  - [ ] Remaining physical rnodeconf device/platform and RF acceptance (one RAK4631 verified; one radio cannot establish RF interoperability)
- [ ] Parity 8 - Compatibility and release acceptance
  - [ ] Broader mesh acceptance: broader alternate-route topologies, cold initial-window resource-loss recovery, sustained Channel/resource soak and impaired native-binary/per-bearer matrices (pinned upstream cold-window cancellation is independently reproduced, not passing recovery)
  - [ ] Windows, Linux and macOS managed/native feature flows; explicitly classify other upstream platform requirements
  - [ ] Diagnose the earlier intermittent macOS reverse-send timeout (not reproduced in the subsequent five-repeat run); longer Channel/resource soak and broader impaired bearer matrices remain outstanding
  - [ ] Android host/providers, desktop RNode BLE and other missing/partial applicable software identified in the compatibility ledger
  - [ ] Per-interface interoperability and hardware evidence; untested platforms/devices remain unverified
- [ ] CI/CD and automation
  - [ ] Expand shared advertised-claim scenarios for heterogeneous routing, broader application request boundaries and Channel/Buffer
