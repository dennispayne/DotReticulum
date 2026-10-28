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
  - [x] Consolidated compatibility report with every contract row and Linux/macOS/Windows columns (run 40521069184)
  - [x] Consolidated Unit/Integration/Native test tables with Linux/macOS/Windows columns (run 40521069184)
  - [x] Provision the separately fetched pinned Python reference for CI interoperability (hosted run 40521067543, verified source imports and 129 passing native/Python cases)
  - [x] Native .NET `rngit` hosted-CI upstream retrieval and separate upstream-drift comparison (runs 40519261522 / 40520520527; disclosed mirror-parent seed, nonzero network objects, no publisher-authentication claim)
  - [x] Initial advertised-core functionality family and directly executed Upstream results alongside the three .NET platforms (eight bounded rows; unmapped acceptance stays explicit; run 40521069184)
  - [x] Separate integration evidence from shared advertised-claim compatibility scenarios; use upstream-to-upstream as the multi-node baseline and a fixed upstream peer for every .NET candidate (Windows native and Python locally verified; cross-platform CI evidence recorded in development history)
  - [x] Make candidate-owned reference retrieval an advertised Git compatibility scenario; reuse one verified source bundle for the subsequent upstream baseline and Test, replacing the standalone fetch workflow (all 12 jobs passed in run 40621550584)
  - [x] Readable Pester compatibility claim tests, citation-only catalog, read-only discovery and individual Pester outcomes across four candidates (all four Compatibility jobs and renderer passed in runs 40626931637 and 40628676024; later discovery-only metadata cleanup locally verified)
  - [x] Table-only Compatibility summary with commit-pinned test-code hyperlinks, no Family column, and isolated child-process Actions output
  - [ ] Expand shared advertised-claim scenarios for interface authentication, bulk resources, heterogeneous routing, application requests and Channel/Buffer
  - [ ] `interop-test.yml`
  - [ ] `e2e-mesh-test.yml`
  - [ ] `codeql.yml`
  - [ ] `stale.yml`
