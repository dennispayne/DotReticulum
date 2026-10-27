# Remaining DotReticulum work

This is the single active progress checklist, extracted from the unchecked
items in the [archived plan](docs/archive/plan.md) on 2026-10-26.
[Development history](.github/development-history.md) supplies evidence;
the [compatibility ledger](docs/compatibility-contracts.md) defines residual
contracts and acceptance boundaries. Completed historical phases are not repeated.

- [ ] Parity 6 - Upstream interface coverage
  - [ ] RNodeInterface and RNodeMultiInterface full software contracts (serial/TCP accepted; desktop BLE selector/provider still missing)
  - [ ] Desktop RNode `ble://` Nordic UART selection, bonding, GATT notification/write/reconnect provider and platform/device acceptance
  - [ ] I2PInterface
  - [ ] Android-specific adapters or documented native platform equivalents
  - [ ] Complete UDP/TCP/serial configuration and validate real serial/radio hardware
  - [ ] Android USB/Bluetooth permission/adaptor contracts and on-device execution
  - [ ] Actual I2P stream delivery after persisted bearer restart (fails on i2pd and Java I2P); SAM readiness alone is insufficient
  - [ ] Authorized physical serial/TNC/RNode/Multi/Weave and Android USB/Bluetooth/RF acceptance (COM8 RNode USB/firmware identification verified; one radio only, RF acceptance explicitly retained as unverified; remaining devices/platform access/settings unavailable; see development history)
- [ ] Parity 7 - Upstream utility coverage (software accepted; physical device acceptance outstanding)
  - [ ] Explicitly authorized physical rnodeconf flashing/reset/re-enumeration/EEPROM persistence and RF acceptance (necessary COM8 reflashing conditionally approved; native info/backup verified, no mutation needed/performed; one radio cannot establish RF interoperability)
- [ ] Parity 8 - Compatibility and release acceptance
  - [ ] Broader mesh acceptance: Python-router/crash restart, alternate routes, sustained Channel/resource soak and impaired native-binary/per-bearer matrices
  - [ ] Windows, Linux and macOS managed/native feature flows; explicitly classify other upstream platform requirements
  - [ ] Darwin rnsh terminal backend and actual macOS feature acceptance (CI provides macOS execution; backend remains missing)
  - [ ] Android host/providers, desktop RNode BLE and other missing/partial applicable software identified in the compatibility ledger
  - [ ] Per-interface interoperability and hardware evidence; untested platforms/devices remain unverified
- [ ] CI/CD and automation
  - [x] `ci-build-test.yml` (authorized early on 2026-10-26; green on all three OSes in run 40330280525)
  - [ ] Consolidated compatibility report with every contract row and Linux/macOS/Windows columns
  - [ ] Consolidated Unit/Integration/Native test tables with Linux/macOS/Windows columns
  - [ ] Provision the separately fetched pinned Python reference for CI interoperability
  - [ ] Native .NET `rngit` hosted-CI upstream retrieval and separate upstream-drift comparison
  - [ ] Advertised-core functionality family and directly executed Upstream results alongside the three .NET platforms
  - [ ] `interop-test.yml`
  - [ ] `e2e-mesh-test.yml`
  - [ ] `codeql.yml`
  - [ ] `stale.yml`
